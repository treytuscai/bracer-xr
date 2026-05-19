using UnityEngine;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Collections;
using Surface.Helpers;

/// <summary>
/// Reconstructs the forearm surface as a triangle mesh each frame using the
/// Quest 3 depth buffer, anchored to wrist and elbow bones from OVRSkeleton.
///
/// Pipeline (runs every LateUpdate):
///   1. Sample:  Project wrist/elbow into screen space, build a distance-aware
///               padded bbox, cast a grid of depth rays via EnvironmentRaycastManager.
///   2. Seed:    Mark cells inside a cylinder around the wrist->elbow axis line.
///               This catches a stable strip of forearm even when IOBT elbow is off.
///   3. Flood:   BFS from seed cells onto depth-connected neighbors, bounded by
///               connectivity threshold, a loose radial cap, and longitudinal limits.
///               Expands across the real forearm surface past where the axis missed.
///   4. Mesh:    Convert kept cells to vertices (local space), tile adjacent 2x2
///               blocks into quads/triangles, reject edges spanning depth gaps.
///   5. UV:      Map each vertex to (U, V) in a wrist-anchored cylindrical frame.
///               U = angle around the arm axis, V = distance from wrist normalized
///               by bone length. Follows forearm rotation, ignores hand flexion.
///
/// Key constraints:
///   - Depth rays must be camera-aligned (from centerEyeAnchor through screen-space
///     pixels). The depth buffer is a 2D camera projection. Rays from other origins
///     (e.g. fingertip toward arm, radial from bone axis) don't map to depth pixels
///     and return nothing. This is architectural, not tunable.
///   - IOBT elbow position is used for direction only. It reports short on bend.
///     Flood-fill handles expansion past the reported elbow.
///
/// Public API: IsValid, WristPosition, ElbowPosition, AxisDir, AxisRight,
///             AxisUp, AxisLength, SurfaceMesh — consumed by TouchInputManager
///             and VisualFeedbackController.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ForearmDepthSurface : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Body skeleton providing wrist and elbow bone transforms")]
    public OVRSkeleton bodySkeleton;

    [Tooltip("Camera transform used for screen-space projection and depth ray origin")]
    public Transform centerEyeAnchor;

    [Tooltip("Material for rendering the forearm surface. Falls back to transparent cyan if unset")]
    public Material surfaceMaterial;

    [Header("Bones")]
    [Tooltip("Index of the wrist bone in OVRSkeleton.Bones (OpenXR hand skeleton)")]
    public int wristBoneIndex = 19;
    
    [Tooltip("Index of the elbow bone in OVRSkeleton.Bones (IOBT body skeleton). " +
             "Used for direction only. Position can be unreliable on bend")]
    public int elbowBoneIndex = 11;

    [Header("Sampling")]
    [Tooltip("Screen-space step between depth samples (px). Lower = denser mesh, more raycasts")]
    [Range(2, 20)] public int pixelStride = 8;

    [Header("Seed (wrist->elbow cylinder filter)")]
    [Tooltip("Max perpendicular distance from the arm axis to count as inside the seed cylinder (m)")]
    [Range(0.02f, 0.12f)] public float maxRadialDist = 0.07f;

    [Tooltip("How far before the wrist (negative, along axis) seed cells are allowed (m)")]
    [Range(-0.05f, 0f)] public float minFromWrist = -0.02f;

    [Tooltip("How far past the elbow (along axis) seed cells are allowed (m)")]
    [Range(0f, 0.10f)] public float maxFromElbow = 0.05f;

    [Header("Flood (depth connectivity expansion)")]
    [Tooltip("Max 3D step between adjacent grid hits to count as connected (m). " +
             "This is what lets the flood expand off the wrong-angle seed line " +
             "onto the actual forearm surface.")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.025f;
    
    [Tooltip("Multiplier on maxRadialDist for the flood's radial bound. " +
             "Looser than the seed to allow expansion past where the axis misses, " +
             "tight enough to reject nearby surfaces like tables")]
    [Range(1f, 3f)] public float floodRadialMultiplier = 1.5f;

    [Header("Smoothing")]
    [Tooltip("Spatial smoothing passes on depth hits. 0 = raw depth")]
    public int smoothPasses = 3;

    [Header("Edge Smoothing")]
    [Tooltip("1D smoothing passes along boundary contour chains")]
    [Range(0, 5)] public int edgeSmoothPasses = 2;
    [Tooltip("Half-width of the moving average window along boundary chains")]
    [Range(1, 6)] public int edgeWindowRadius = 3;

    [Header("Mesh")]
    [Tooltip("Drop quads/tris whose longest edge exceeds this (m). Must be ≥ connectivityThreshold " +
             "to allow for surface curvature between cells that connected through different flood paths")]
    [Range(0.005f, 0.06f)] public float maxQuadEdge = 0.030f;

    [Header("Debug")]
    [Tooltip("Show a green line from wrist to elbow on device")]
    public bool drawAxis = true;

    [Tooltip("Log per-stage pipeline counts every N frames. Set to 0 to disable.")]
    [Range(0, 120)] public int verboseLogEveryNFrames = 30;

    [Tooltip("Skip seed/flood. Treat every valid depth pixel as surface and mesh it. " +
             "Use to visualize the raw unprojection independent of arm-finding logic. " +
             "When on, bump maxQuadEdge to ~0.5 so the mesh doesn't get pruned by edge length.")]
    public bool bypassSeedFlood = false;

    [Header("Display")]
    [Tooltip("Physical width of the display region on the arm (m)")]
    public float displayWidth = 0.05f;

    [Tooltip("Physical height of the display region along the arm (m)")]
    public float displayHeight = 0.05f;

    [Tooltip("How far from the wrist (along axis) to center the display (m)")]
    public float displayOffset = 0.12f;

    // Cached bone transforms, resolved once from OVRSkeleton
    Transform _wrist, _elbow; 

    // Camera reference, cached in Start
    Camera _cam;

    // True if this frame produced a valid mesh
    bool _hasFrame;

    // Mesh components
    MeshFilter _meshFilter; 
    MeshRenderer _meshRenderer; 
    Mesh _mesh; 
    LineRenderer _line;

    private DepthReadback _readbackManager;
    private bool _isProcessingMesh = false;
    private int _logFrameCounter = 0;
    // Stashed for diagnostic log: results of the most recent screen-space
    // projection of the wrist/elbow bones in CalculateScreenBounds. Lets us
    // see whether bones project to where the arm actually is on screen.
    private Vector3 _wristScreen, _elbowScreen;

    int[,] _cellToVert; // Maps grid cell -> vertex index in _verts (-1 if rejected)
    int _rows, _cols;   // Current grid dimensions

    // Surface Buffer for the Job System
    private SurfaceBuffer _buffer = new SurfaceBuffer();

    // Reusable buffers for boundary chain extraction
    readonly List<int> _boundaryChain = new List<int>(512);
    readonly List<Vector3> _chainSmoothed = new List<Vector3>(512);

    // Mesh construction buffers (pre-allocated, cleared each frame)
    readonly List<Vector3> _verts = new List<Vector3>(2048);
    readonly List<int> _tris = new List<int>(4096);
    readonly List<Vector2> _uvs = new List<Vector2>(2048);
    readonly List<Vector2> _edgeDists = new List<Vector2>(2048);


    // Frame state computed in LateUpdate, consumed by UV() and public API
    Vector3 _wristPos, _elbowPos;   // Cached bone world positions
    Vector3 _axis;                  // Wrist->elbow direction (normalized)
    Vector3 _axisRight, _axisUp;    // Orthogonal frame perpendicular to axis
    float _projCenter;              // Linear projection center for UV mapping.

    // Pronation tracking: measures how much the forearm has rotated relative
    // to the camera view. Used to offset U so rotating the wrist scrolls
    // through different regions of a wider virtual canvas.
    // _axisRight (camera-derived) handles projection quality.
    // _pronationAngle (bone-derived vs camera-derived) handles content selection.
    static readonly Vector3 _wristUpLocal = Vector3.up;
    float _pronationAngle;          // Signed angle (rad) of forearm rotation
    float _smoothedPronation;       // Temporally smoothed to reduce bone jitter

    // Orientation tracking: detects whether the arm is held vertically (portrait)
    // or horizontally (landscape) in the camera view, and rotates UVs to keep
    // the UI content upright.
    float _orientationAngle;        // Current snapped rotation (0 or ±π/2)

    private int _cropX, _cropY, _cropW, _cropH;

    /// <summary>
    /// Initializes the dynamic mesh, assigns or creates a surface material,
    /// sets up the debug axis line, and caches the camera reference.
    /// </summary>
    void Start()
    {
        // Set up the mesh that gets rebuilt every frame
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _mesh = new Mesh
        {
            name = "ForearmDepth",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        _mesh.MarkDynamic(); // Tell Unity this mesh changes every frame
        _meshFilter.mesh = _mesh;

        // Use assigned material if provided, otherwise a transparent cyan fallback
        // for debugging mesh coverage without needing a shader in the Inspector
        _meshRenderer.material = surfaceMaterial != null ? surfaceMaterial : MakeFallback();

        // Debug line for visualizing the wrist->elbow axis on device
        var debugLine = new GameObject("DebugAxis");
        debugLine.transform.SetParent(transform, false);
        _line = debugLine.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.widthMultiplier = 0.004f;
        _line.positionCount = 2;
        _line.material = new Material(Shader.Find("Sprites/Default"));
        _line.startColor = _line.endColor = Color.green;
        _line.enabled = false;
    }

    /// <summary>
    /// Creates a semi-transparent cyan material for visualizing the mesh
    /// when no surface material is assigned in the Inspector.
    /// </summary>
    Material MakeFallback()
    {
        var fallback = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            color = new Color(0f, 1f, 1f, 0.5f)
        };
        fallback.SetFloat("_Surface", 1);
        fallback.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        fallback.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        fallback.SetInt("_ZWrite", 0);
        fallback.renderQueue = 3000;
        return fallback;
    }

    /// <summary>
    /// Per-frame pipeline: resolves bones, samples depth in a screen-space bbox,
    /// seeds a cylinder filter around the wrist->elbow line, flood-fills via depth
    /// connectivity, builds an orthogonal wrist frame for UVs, and outputs the mesh.
    /// Runs in LateUpdate so bone transforms are finalized before reading.
    /// </summary>
    void LateUpdate()
    {
        // Reset frame validity. Downstream consumers check IsValid
        _hasFrame = false;
        if (_line) _line.enabled = false;

        // Wait for everything to populate.
        if (_readbackManager == null) _readbackManager = new DepthReadback();
        if (_cam == null)  _cam = centerEyeAnchor.GetComponent<Camera>();
        if (bodySkeleton == null || _cam == null) return;
        if (bodySkeleton.Bones == null || bodySkeleton.Bones.Count <= Mathf.Max(wristBoneIndex, elbowBoneIndex)) return;

        // Resolve bone transforms. Can be null if skeleton is still initializing
        _wrist = bodySkeleton.Bones[wristBoneIndex].Transform;
        _elbow = bodySkeleton.Bones[elbowBoneIndex].Transform;
        if (_wrist == null || _elbow == null) return;

        // Cache positions and compute arm direction
        _wristPos = _wrist.position;
        _elbowPos = _elbow.position;
        _axis = (_elbowPos - _wristPos).normalized;

        // 1. Calculate Bounds
        if (!CalculateScreenBounds(out _cropX, out _cropY, out _cropW, out _cropH)) return;

        // 2. Request GPU Data (Only if we aren't already waiting for the previous frame to finish)
        if (!_isProcessingMesh)
        {
            _isProcessingMesh = true;

            // DepthReadback reads Meta's _EnvironmentDepthReprojectionMatrices
            // directly and uses the left-eye matrix's inverse internally. No
            // camera math needs to travel down to the readback layer — that
            // was the source of the swim, since the rendering camera's pose
            // doesn't match the depth frame's pose.
            _readbackManager.RequestDepth(
                _cam.pixelWidth, _cam.pixelHeight,
                _cropX, _cropY, _cropW, _cropH,
                OnDepthDataReceived
            );
        }

        // Build orthogonal frame for UV projection.
        // Derives _axisRight from the camera-to-arm direction so it always
        // points "across" the camera-facing surface strip, regardless of
        // wrist pronation/supination.
        //
        // Camera-derived frame = always perpendicular to view = always
        // good U spread across the visible mesh.
        Vector3 armMid = (_wristPos + _elbowPos) * 0.5f;
        Vector3 toCamera = (_cam.transform.position - armMid).normalized;
        _axisRight = Vector3.Cross(_axis, toCamera).normalized;
        _axisUp = Vector3.Cross(_axisRight, _axis).normalized;

        // Measure pronation: signed angle between bone-derived and camera-derived
        // reference frames, projected onto the plane perpendicular to the arm axis.
        // This tells us how much the physical forearm has rotated relative to what
        // the camera is currently seeing. Palm-up ≈ 0, palm-down ≈ ±π.
        Vector3 wristUp = (_wrist.rotation * _wristUpLocal).normalized;
        Vector3 boneRight = Vector3.Cross(_axis, wristUp).normalized;
        float cos = Vector3.Dot(boneRight, _axisRight);
        float sin = Vector3.Dot(Vector3.Cross(boneRight, _axisRight), _axis);
        _pronationAngle = Mathf.Atan2(sin, cos);
        _smoothedPronation = Mathf.Lerp(_smoothedPronation, _pronationAngle, 0.15f);

        // Measure orientation: snap to nearest 90°
        // Detect arm orientation on screen
        float screenX = Vector3.Dot(_axis, _cam.transform.right);
        float screenY = Vector3.Dot(_axis, _cam.transform.up);
        float absX = Mathf.Abs(screenX);
        float absY = Mathf.Abs(screenY);
        _orientationAngle = absX > absY ? -Mathf.PI * 0.5f: 0f; // Landscape: rotate 90°, Portrait: no rotation,
    }

    bool CalculateScreenBounds(out int xMin, out int yMin, out int width, out int height)
    {
        xMin = yMin = width = height = 0;

        // 1. Grab Meta's hardware depth matrix (NOT the rendering camera!)
        Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        if (depthMatrices == null || depthMatrices.Length == 0) return false;
        
        Matrix4x4 depthVP = depthMatrices[0]; // Left Eye Depth Sensor

        // 2. Project using the physical Depth Sensor's specific matrix
        Vector2 wristPx = WorldToDepthTexturePixels(depthVP, _wristPos, out float wristZ);
        Vector2 elbowPx = WorldToDepthTexturePixels(depthVP, _elbowPos, out float elbowZ);

        if (wristZ <= 0f || elbowZ <= 0f) return false;

        float armMidDist = Vector3.Distance(_cam.transform.position, (_wristPos + _elbowPos) * 0.5f);
        float focalPx = _cam.pixelHeight / (2f * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        float armRadiusPx = maxRadialDist / armMidDist * focalPx;
        float dynamicPadding = armRadiusPx * 1.5f;

        float fXMin = Mathf.Max(0, Mathf.Min(wristPx.x, elbowPx.x) - dynamicPadding);
        float fXMax = Mathf.Min(_cam.pixelWidth, Mathf.Max(wristPx.x, elbowPx.x) + dynamicPadding);
        float fYMin = Mathf.Max(0, Mathf.Min(wristPx.y, elbowPx.y) - dynamicPadding);
        float fYMax = Mathf.Min(_cam.pixelHeight, Mathf.Max(wristPx.y, elbowPx.y) + dynamicPadding);

        if (fXMax - fXMin < pixelStride || fYMax - fYMin < pixelStride) return false;

        xMin = (int)fXMin;
        yMin = (int)fYMin;
        width = (int)(fXMax - fXMin);
        height = (int)(fYMax - fYMin);

        return true;
    }

    // --- NEW HELPER ---
    private Vector2 WorldToDepthTexturePixels(Matrix4x4 depthVP, Vector3 worldPos, out float z)
    {
        // 1. Meta's matrix transforms directly to Clip Space
        Vector4 clip = depthVP * new Vector4(worldPos.x, worldPos.y, worldPos.z, 1.0f);
        z = clip.w;

        // 2. Perspective divide -> NDC [-1, 1]
        Vector2 ndc = new Vector2(clip.x / clip.w, clip.y / clip.w);

        // 3. Convert to UV [0, 1]
        float u = (ndc.x + 1f) * 0.5f;
        float v = (ndc.y + 1f) * 0.5f;

        // 5. Scale to our RenderTexture dimensions
        return new Vector2(u * _cam.pixelWidth, v * _cam.pixelHeight);
    }

    // THIS RUNS WHEN THE GPU IS DONE COPYING
    void OnDepthDataReceived(NativeArray<Vector4> rawCroppedDepth, int safeX, int safeY, int safeW, int safeH)
    {
        // 1. DEADLOCK FIX: If the GPU errored out, unlock and abort
        if (!rawCroppedDepth.IsCreated || rawCroppedDepth.Length == 0)
        {
            if (verboseLogEveryNFrames > 0)
                Debug.LogWarning("[Forearm] Readback returned empty buffer.");
            _isProcessingMesh = false;
            return;
        }

        // Diagnostic: count valid world-position pixels in the raw readback BEFORE
        // the job runs. Tells us whether the shader produced data at all.
        // Also scan rawDepth (carried in w) so we can see the distribution.
        bool shouldLog = verboseLogEveryNFrames > 0
                      && (_logFrameCounter++ % verboseLogEveryNFrames == 0);
        int validPixels = 0;
        Vector3 sampleWorldPos = Vector3.zero;
        float rawDepthMin = 1f, rawDepthMax = 0f, rawDepthSum = 0f;
        float sampleRawDepth = -1f;
        if (shouldLog)
        {
            for (int i = 0; i < rawCroppedDepth.Length; i++)
            {
                float w = rawCroppedDepth[i].w;
                if (w >= 0f)
                {
                    validPixels++;
                    if (w < rawDepthMin) rawDepthMin = w;
                    if (w > rawDepthMax) rawDepthMax = w;
                    rawDepthSum += w;
                }
            }

            // Sample the center pixel so we can see whether positions are plausible
            int centerIdx = (safeH / 2) * safeW + (safeW / 2);
            if (centerIdx < rawCroppedDepth.Length)
            {
                Vector4 s = rawCroppedDepth[centerIdx];
                sampleWorldPos = new Vector3(s.x, s.y, s.z);
                sampleRawDepth = s.w;
            }
        }

        // Shader has already unprojected to world space using the depth-frame's
        // own reprojection matrix, so the job just samples this buffer.
        JobHandle sampleHandle = DepthSampler.ScheduleUnprojection(
            rawCroppedDepth, safeW, safeH, _buffer, pixelStride,
            out _rows, out _cols);

        if (_rows > 0 && _cols > 0)
        {
            if (bypassSeedFlood)
            {
                // Wait for unprojection to finish, then mark every valid depth
                // cell as surface so BuildMesh emits geometry from the entire
                // depth buffer. Skips seed/flood entirely — useful for seeing
                // what the raw unprojection actually produces.
                sampleHandle.Complete();
                int total = _rows * _cols;
                for (int i = 0; i < total; i++)
                    _buffer.IsSurface[i] = _buffer.HasDepth[i];
            }
            else
            {
                RunExtractionJobs(sampleHandle);
            }

            // Wait for Burst jobs to finish before we check the buffer
            RunSmoothing();
            RunBoundarySmoothing();

            ComputeProjectedExtent();

            // --- ZERO THE TRANSFORM ---
            // Our vertices are already in perfect World Space. 
            // We must force this GameObject to world origin so Unity doesn't move them twice!
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            // --------------------------

            BuildMesh();

            _hasFrame = true;
            if (_line && drawAxis)
            {
                _line.enabled = true;
                _line.SetPosition(0, _wristPos);
                _line.SetPosition(1, _elbowPos);
            }

            if (shouldLog)
            {
                // Count cells that survived each stage. HasDepth = job copied a
                // valid world position; IsSurface = passed seed+flood filter.
                // Also track the world-space spread of all valid hits — if the
                // unprojection is sane, this should span 1-2 meters; if it's
                // collapsing to one point, the math is wrong.
                int hits = 0, surface = 0;
                Vector3 boundsMin = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
                Vector3 boundsMax = new Vector3( float.MinValue,  float.MinValue,  float.MinValue);
                Vector3 sum = Vector3.zero;
                int total = _rows * _cols;
                for (int i = 0; i < total; i++)
                {
                    if (_buffer.HasDepth[i])
                    {
                        hits++;
                        Vector3 p = _buffer.Hits[i];
                        boundsMin = Vector3.Min(boundsMin, p);
                        boundsMax = Vector3.Max(boundsMax, p);
                        sum += p;
                    }
                    if (_buffer.IsSurface[i]) surface++;
                }

                Vector3 extent = hits > 0 ? boundsMax - boundsMin : Vector3.zero;
                Vector3 mean = hits > 0 ? sum / hits : Vector3.zero;
                float rawDepthMean = validPixels > 0 ? rawDepthSum / validPixels : 0f;

                float wristToSample = Vector3.Distance(_wristPos, sampleWorldPos);
                Vector3 camPos = _cam.transform.position;
                Debug.Log(
                    $"[Forearm] cropAt=({safeX},{safeY}) crop={safeW}x{safeH} " +
                    $"screen={_cam.pixelWidth}x{_cam.pixelHeight} fov={_cam.fieldOfView:F1} " +
                    $"wristScreen=({_wristScreen.x:F0},{_wristScreen.y:F0},{_wristScreen.z:F2}) " +
                    $"elbowScreen=({_elbowScreen.x:F0},{_elbowScreen.y:F0},{_elbowScreen.z:F2}) " +
                    $"gridValid={validPixels}/{rawCroppedDepth.Length} " +
                    $"grid={_cols}x{_rows} hits={hits} surface={surface} " +
                    $"verts={_verts.Count} tris={_tris.Count / 3} " +
                    $"cam={camPos} wrist={_wristPos} centerPx={sampleWorldPos} dist={wristToSample:F2}m " +
                    $"hitsMean=({mean.x:F2},{mean.y:F2},{mean.z:F2}) " +
                    $"hitsExtent=({extent.x:F2},{extent.y:F2},{extent.z:F2}) " +
                    $"rawDepth[min={rawDepthMin:F4} max={rawDepthMax:F4} mean={rawDepthMean:F4} centerPx={sampleRawDepth:F4}]");
            }
        }
        else if (shouldLog)
        {
            Debug.LogWarning($"[Forearm] grid resolved to {_cols}x{_rows} — pipeline aborted before jobs.");
        }

        // Unlock so LateUpdate can request the next frame!
        _isProcessingMesh = false;
    }

    /// <summary>
    /// Executes the Burst-compiled Jobs to extract the forearm surface.
    /// Waits for the unprojection job to finish before starting the Seed and Flood passes.
    /// </summary>
    void RunExtractionJobs(JobHandle dependency)
    {
        // Clear the queue from the previous frame
        _buffer.BFSQueue.Clear();

        // Stage 1: seed = cylinder filter around the wrist->elbow line.
        // Catches a stable strip of forearm cells even when IOBT elbow is off.
        var seedJob = new SeedFromAxisJob {
            Hits = _buffer.Hits,
            HasDepth = _buffer.HasDepth,
            Kept = _buffer.IsSurface,
            BFSQueueWriter = _buffer.BFSQueue.AsParallelWriter(),
            WristPos = _wristPos, ElbowPos = _elbowPos, Axis = _axis,
            MaxRSq = maxRadialDist * maxRadialDist, 
            MinFromWrist = minFromWrist, MaxFromElbow = maxFromElbow
        };
        JobHandle seedHandle = seedJob.Schedule(_rows * _cols, 64, dependency);

        // Stage 2: flood from seeds via depth connectivity.
        // Expands onto forearm cells the seed cylinder missed.
        float floodRadius = maxRadialDist * floodRadialMultiplier;
        var floodJob = new FloodFromSeedsJob {
            BFSQueue = _buffer.BFSQueue,
            Hits = _buffer.Hits,
            HasDepth = _buffer.HasDepth,
            Kept = _buffer.IsSurface,
            Cols = _cols, Rows = _rows,
            WristPos = _wristPos, ElbowPos = _elbowPos, Axis = _axis,
            ConnSq = connectivityThreshold * connectivityThreshold, 
            MaxFloodRadialSq = floodRadius * floodRadius,
            MinFromWrist = minFromWrist, MaxFromElbow = maxFromElbow
        };
        
        // Schedule flood, wait for seed to finish first, then block until flood is done
        JobHandle floodHandle = floodJob.Schedule(seedHandle);
        floodHandle.Complete();
    }

    /// <summary>
    /// Applies a Laplacian smoothing pass across the reconstructed surface.
    /// This runs in parallel on the GPU-optimized background threads using Burst.
    /// </summary>
    void RunSmoothing()
    {
        if (smoothPasses <= 0) return;

        // Execute Jobs
        for (int i = 0; i < smoothPasses; i++)
        {
            var job = new SmoothSurfaceJob {
                Hits = _buffer.Hits,
                IsSurface = _buffer.IsSurface,
                Smoothed = _buffer.Smoothed,
                GridHeight = _rows, GridWidth = _cols
            };

            job.Schedule(_rows * _cols, 64).Complete();

            // Swap directly inside the buffer
            var temp = _buffer.Hits;
            _buffer.Hits = _buffer.Smoothed;
            _buffer.Smoothed = temp;
        }
    }

    /// <summary>
    /// Extracts ordered boundary contours from the kept-cell grid and applies a 1D moving average 
    /// along each chain to smooth jagged mesh edges. Runs on the main thread using flat-indexed 
    /// native buffers to maintain zero garbage collection.
    /// </summary>
    void RunBoundarySmoothing()
    {
        BoundarySmoother.ProcessBoundary(
            edgeSmoothPasses, 
            edgeWindowRadius, 
            _rows, 
            _cols, 
            _buffer.Hits, 
            _buffer.IsSurface, 
            _buffer.BoundaryVisited, 
            _boundaryChain, 
            _chainSmoothed
        );
    }

    void OnDestroy()
    {
        _readbackManager?.Dispose();
        _buffer.Dispose();
    }

    /// <summary>
    /// Computes the center of the visible forearm surface by projecting
    /// all kept cells onto the _axisRight direction (perpendicular to the arm axis,
    /// in the plane of the visible surface).
    ///
    /// For the ~40-60° of arc visible on the inner forearm, linear vs cylindrical
    /// projection differs by at most ~3% — visually identical.
    /// </summary>
    void ComputeProjectedExtent()
    {
        float sum = 0f;
        int count = 0;

        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                if (!_buffer.IsSurface[r * _cols + c]) continue;

                // Project onto _axisRight: signed distance from the arm axis
                // in the direction perpendicular to the arm, within the visible plane.
                float proj = Vector3.Dot(_buffer.Hits[r * _cols + c] - _wristPos, _axisRight);

                sum += proj;
                count++;
            }

        _projCenter = sum / count;
    }

    /// <summary>
    /// Builds a triangle mesh from kept depth cells. Each kept cell becomes a vertex
    /// (converted to local space for Unity's mesh system). Adjacent 2x2 cell blocks
    /// form quads (split into two triangles) or single triangles if one corner is
    /// missing. Edges that span depth gaps are rejected via QuadEdgesOK/TriEdgesOK.
    /// </summary>
    void BuildMesh()
    {
        // Reset mesh data from previous frame
        _verts.Clear(); _tris.Clear(); _uvs.Clear(); _edgeDists.Clear();

        // Reuse or reallocate the grid->vertex index lookup
        if (_cellToVert == null || _cellToVert.GetLength(0) != _rows || _cellToVert.GetLength(1) != _cols)
            _cellToVert = new int[_rows, _cols];

        // Per-row projected bounds along _axisRight (meters).
        // Each row gets its own edges so the fade strips follow the
        // actual mesh boundary at every V-slice.
        float[] rowProjMin = new float[_rows];
        float[] rowProjMax = new float[_rows];
        for (int r = 0; r < _rows; r++)
        {
            rowProjMin[r] = float.MaxValue;
            rowProjMax[r] = float.MinValue;
            for (int c = 0; c < _cols; c++)
            {
                if (!_buffer.IsSurface[r * _cols + c]) continue;
                float proj = Vector3.Dot(_buffer.Hits[r * _cols + c] - _wristPos, _axisRight);
                if (proj < rowProjMin[r]) rowProjMin[r] = proj;
                if (proj > rowProjMax[r]) rowProjMax[r] = proj;
            }
        }

        // Smooth per-row bounds along V so the fade edge is a smooth curve
        // instead of a stair-step following the grid boundary.
        for (int pass = 0; pass < 3; pass++)
        {
            float[] sMin = new float[_rows];
            float[] sMax = new float[_rows];
            for (int r = 0; r < _rows; r++)
            {
                if (rowProjMin[r] == float.MaxValue) { sMin[r] = rowProjMin[r]; sMax[r] = rowProjMax[r]; continue; }

                float sumMin = rowProjMin[r], sumMax = rowProjMax[r];
                int n = 1;
                if (r > 0 && rowProjMin[r - 1] < float.MaxValue)
                    { sumMin += rowProjMin[r - 1]; sumMax += rowProjMax[r - 1]; n++; }
                if (r < _rows - 1 && rowProjMin[r + 1] < float.MaxValue)
                    { sumMin += rowProjMin[r + 1]; sumMax += rowProjMax[r + 1]; n++; }

                sMin[r] = sumMin / n;
                sMax[r] = sumMax / n;
            }
            System.Array.Copy(sMin, rowProjMin, _rows);
            System.Array.Copy(sMax, rowProjMax, _rows);
        }

        // Build vertex list from kept cells. Each kept cell becomes a vertex.
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                // Rejected cells get -1 so the triangle loop knows to skip them.
                if (!_buffer.IsSurface[r * _cols + c]) { _cellToVert[r, c] = -1; continue; }

                Vector3 hitPos = _buffer.Hits[r * _cols + c];

                // Physical distance from this row's nearest mesh edge (meters).
                // Stored in UV1.x so the shader can smoothstep per-fragment.
                float proj = Vector3.Dot(hitPos - _wristPos, _axisRight);
                float dist = Mathf.Max(0f, Mathf.Min(proj - rowProjMin[r], rowProjMax[r] - proj));

                _cellToVert[r, c] = _verts.Count;
                _verts.Add(transform.InverseTransformPoint(hitPos));
                _uvs.Add(UV(hitPos));
                _edgeDists.Add(new Vector2(dist, 0f));
            }

        // Quads with edges longer than this get dropped to avoid stretching
        // across depth gaps
        float maxSq = maxQuadEdge * maxQuadEdge;

        // Walk every 2x2 cell block and try to form quads (two triangles).
        // All winding is CCW so normals face the camera consistently.
        for (int r = 0; r < _rows - 1; r++)
            for (int c = 0; c < _cols - 1; c++)
            {
                // Each block has four corners: top-left, top-right, bottom-left, bottom-right.
                int topLeft = _cellToVert[r, c];
                int topRight = _cellToVert[r, c+1];
                int botLeft = _cellToVert[r+1, c];
                int botRight = _cellToVert[r+1, c+1];

                // Count how many corners have vertices
                int vertCount = (topLeft >= 0 ? 1 : 0) + (topRight >= 0 ? 1 : 0) 
                              + (botLeft >= 0 ? 1 : 0) + (botRight >= 0 ? 1: 0);

                // Need at least 3 corners to form a triangle
                if (vertCount < 3) continue;

                // Full quad! 
                if (vertCount == 4)
                {
                    // Check that no edge is too long before emitting
                    if (!QuadEdgesOK(_buffer.Hits[r*_cols+c], _buffer.Hits[r*_cols+c+1], _buffer.Hits[(r+1)*_cols+c], _buffer.Hits[(r+1)*_cols+c+1], maxSq)) 
                        continue;

                    // Split quad into two triangles
                    _tris.Add(topLeft); _tris.Add(botLeft); _tris.Add(topRight);
                    _tris.Add(topRight); _tris.Add(botLeft); _tris.Add(botRight);
                }
            
                // Three corners! Form a single triangle from whichever three exist,
                // but only if no edge stretches across a depth gap.
                else if (topLeft  < 0) {
                    if (!TriEdgesOK(_buffer.Hits[r*_cols+c+1], _buffer.Hits[(r+1)*_cols+c], _buffer.Hits[(r+1)*_cols+c+1], maxSq)) continue;
                    _tris.Add(topRight); _tris.Add(botLeft);  _tris.Add(botRight);
                }
                else if (topRight < 0) {
                    if (!TriEdgesOK(_buffer.Hits[r*_cols+c], _buffer.Hits[(r+1)*_cols+c], _buffer.Hits[(r+1)*_cols+c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botLeft);  _tris.Add(botRight);
                }
                else if (botLeft  < 0) {
                    if (!TriEdgesOK(_buffer.Hits[r*_cols+c], _buffer.Hits[r*_cols+c+1], _buffer.Hits[(r+1)*_cols+c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botRight); _tris.Add(topRight);
                }
                else {
                    if (!TriEdgesOK(_buffer.Hits[r*_cols+c], _buffer.Hits[(r+1)*_cols+c], _buffer.Hits[r*_cols+c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botLeft);  _tris.Add(topRight);
                }
            }

        // Upload to GPU
        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetUVs(0, _uvs);
        _mesh.SetUVs(1, _edgeDists);
        _mesh.SetTriangles(_tris, 0);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    // Check all four edges of a quad against max length
    static bool QuadEdgesOK(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float maxSq) =>
        (a - b).sqrMagnitude < maxSq && (a - c).sqrMagnitude < maxSq
        && (c - d).sqrMagnitude < maxSq && (b - d).sqrMagnitude < maxSq;

    // Check all three edges of a triangle against max length
    static bool TriEdgesOK(Vector3 a, Vector3 b, Vector3 c, float maxSq) =>
        (a - b).sqrMagnitude < maxSq && (b - c).sqrMagnitude < maxSq
        && (a - c).sqrMagnitude < maxSq;

    /// <summary>
    /// Converts a 3D world-space point on the forearm into a 2D UV coordinate
    /// using a linear planar projection with rotation-based content scrolling.
    ///
    /// V (0-1) = distance along the arm axis.
    /// U (0-1) = position across the arm, centered on visible surface,
    ///           offset by pronation angle to scroll through a wider virtual canvas.
    ///
    /// The camera-derived _axisRight keeps lines straight (projection quality).
    /// The bone-derived _pronationAngle shifts U so rotating the wrist reveals
    /// different content regions (content selection). These are independent:
    /// straight lines at every rotation angle.
    /// </summary>
    Vector2 UV(Vector3 point)
    {
        Vector3 fromWrist = point - _wristPos;
        bool isLandscape = Mathf.Abs(_orientationAngle) > Mathf.PI * 0.25f;

        // V: along arm
        float distAlongAxis = Vector3.Dot(fromWrist, _axis);
        float v = ((distAlongAxis - displayOffset) / 
                    Mathf.Max(isLandscape ? displayWidth : displayHeight, 1e-4f)) + 0.5f;
        v = 1f - v;

        // U: across arm
        float projRight = Vector3.Dot(fromWrist, _axisRight);
        float u = ((projRight - _projCenter) / 
                    Mathf.Max(isLandscape ? displayHeight : displayWidth, 1e-4f)) + 0.5f;

        // Pronation offset: binary, matching isLandscape
        float pronationOffset = isLandscape ? Mathf.PI * 0.75f : 0f;
        u += (_smoothedPronation + pronationOffset) / Mathf.PI;

        // Rotate UVs around center to counteract arm screen orientation.
        // Keeps UI content upright whether the arm is vertical or horizontal.
        // Snaps to 90° increments, smoothly animated via _smoothedOrientation.
        float a = _orientationAngle;
        float cu = u - 0.5f, cv = v - 0.5f;
        float cosA = Mathf.Cos(a), sinA = Mathf.Sin(a);
        u = cu * cosA - cv * sinA + 0.5f;
        v = cu * sinA + cv * cosA + 0.5f;

        return new Vector2(u, v);
    }

    public bool    IsValid          => _hasFrame;
    public Vector3 WristPosition    => _wristPos;
    public Vector3 ElbowPosition    => _elbowPos;
    public Vector3 AxisDir          => _axis;
    public Vector3 AxisRight        => _axisRight;
    public Vector3 AxisUp           => _axisUp;
    public Mesh    SurfaceMesh      => _mesh;
    public float   PronationAngle   => _smoothedPronation;
    public float   OrientationAngle => _orientationAngle;
}