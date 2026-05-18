using UnityEngine;
using System.Collections.Generic;
using Meta.XR;
using Unity.Jobs;
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

    [Tooltip("MRUK depth sampler. Casts rays against the depth buffer")]
    public EnvironmentRaycastManager raycastManager;

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

    int[,] _cellToVert; // Maps grid cell -> vertex index in _verts (-1 if rejected)
    int _rows, _cols;   // Current grid dimensions

    // Surface Buffer for the Job System
    private SurfaceBuffer _buffer = new SurfaceBuffer();

    // Reusable buffers for boundary chain extraction
    readonly List<Vector2Int> _boundaryChain = new List<Vector2Int>(512);
    readonly List<Vector3> _chainSmoothed = new List<Vector3>(512);
    bool[,] _boundaryVisited;

    // Mesh construction buffers (pre-allocated, cleared each frame)
    readonly List<Vector3> _verts = new List<Vector3>(2048);
    readonly List<int> _tris = new List<int>(4096);
    readonly List<Vector2> _uvs = new List<Vector2>(2048);
    readonly List<Vector2> _edgeDists = new List<Vector2>(2048);

    // 8-connected grid neighbor offsets: row and col deltas paired by index
    static readonly int[] _neighborDr = { -1, -1, -1,  0, 0,  1, 1, 1 };
    static readonly int[] _neighborDc = { -1,  0,  1, -1, 1, -1, 0, 1 };

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

    /// <summary>
    /// Initializes the dynamic mesh, assigns or creates a surface material,
    /// sets up the debug axis line, and caches the camera reference.
    /// </summary>
    void Start()
    {
        // Set up the mesh that gets rebuilt every frame
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _mesh = new Mesh { name = "ForearmDepth" };
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

        // Cache camera reference. Doesn't change at runtime
        _cam = centerEyeAnchor.GetComponent<Camera>();
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

        // Bail if required references are missing
        if (raycastManager == null || bodySkeleton == null || _cam == null) return;

        // Wait for skeleton to populate. Bones may not exist on first frames
        if (bodySkeleton.Bones == null || bodySkeleton.Bones.Count <= Mathf.Max(wristBoneIndex, elbowBoneIndex)) return;

        // Resolve bone transforms. Can be null if skeleton is still initializing
        _wrist = bodySkeleton.Bones[wristBoneIndex].Transform;
        _elbow = bodySkeleton.Bones[elbowBoneIndex].Transform;
        if (_wrist == null || _elbow == null) return;

        // Cache positions and compute arm direction
        _wristPos = _wrist.position;
        _elbowPos = _elbow.position;
        _axis = (_elbowPos - _wristPos).normalized;

        // 1. Raycast Depth Buffer
        if (!Sample(_wristPos, _elbowPos)) return;

        // 2. Surface Extraction (Seed & Flood Jobs)
        RunExtractionJobs();

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

        // 3. Post-Processing & Mesh Gen
        RunSmoothing();
        SmoothBoundary();
        ComputeProjectedExtent();
        BuildMesh();

        // Mark frame as valid for external consumers
        _hasFrame = true;

        // Debug: draw wrist->elbow line on device
        if (_line && drawAxis)
        {
            _line.enabled = true;
            _line.SetPosition(0, _wristPos);
            _line.SetPosition(1, _elbowPos);
        }
    }

    /// <summary>
    /// Projects wrist and elbow into screen space, builds a padded bounding box,
    /// then casts a grid of depth rays through that region. Populates _hits,
    /// _hasDepth, and _kept arrays for downstream seed and flood passes.
    /// Returns false if either bone is behind the camera or the bbox is too small.
    /// </summary>
    bool Sample(Vector3 wristPos, Vector3 elbowPos)
    {
        // Project bone positions into screen space for sampling bounds
        Vector3 wristScreen = _cam.WorldToScreenPoint(wristPos);
        if (wristScreen.z <= 0f) return false;
        Vector3 elbowScreen = _cam.WorldToScreenPoint(elbowPos);
        if (elbowScreen.z <= 0f) return false;

        Vector2 wristPx = new Vector2(wristScreen.x, wristScreen.y);
        Vector2 elbowPx = new Vector2(elbowScreen.x, elbowScreen.y);

        // Convert the arm's physical radius to screen pixels based on distance.
        // focalPx = vertical focal length in pixels (how many pixels per radian).
        // armRadiusPx = how many pixels the arm extends beyond the bone line.
        // 1.5x multiplier gives margin for curvature and depth noise at edges.
        float armMidDist = Vector3.Distance(_cam.transform.position, (_wristPos + _elbowPos) * 0.5f);
        float focalPx = _cam.pixelHeight / (2f * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        float armRadiusPx = maxRadialDist / armMidDist * focalPx;
        float dynamicPadding = armRadiusPx * 1.5f;

        // Build a padded bounding box around both bone projections, clamped to screen.
        // This is the region we'll sample depth rays from. Padding catches the arm
        // width that extends beyond the bone line.
        float xMin = Mathf.Max(0, Mathf.Min(wristPx.x, elbowPx.x) - dynamicPadding);
        float xMax = Mathf.Min(_cam.pixelWidth, Mathf.Max(wristPx.x, elbowPx.x) + dynamicPadding);
        float yMin = Mathf.Max(0, Mathf.Min(wristPx.y, elbowPx.y) - dynamicPadding);
        float yMax = Mathf.Min(_cam.pixelHeight, Mathf.Max(wristPx.y, elbowPx.y) + dynamicPadding);
        if (xMax - xMin < pixelStride || yMax - yMin < pixelStride) return false;

        // Grid dimensions from the screen-space bbox. Min 2 so we can form quads.
        _cols = Mathf.Max(2, Mathf.CeilToInt((xMax - xMin) / pixelStride));
        _rows = Mathf.Max(2, Mathf.CeilToInt((yMax - yMin) / pixelStride));

        // Resize native buffer
        _buffer.ResizeIfNeeded(_rows, _cols);

        // Cast depth rays through each grid cell and store 3D hit positions.
        // Cells without valid depth are flagged so downstream stages skip them.
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                // Reset state — no cell carries data from previous frames
                int idx = r * _cols + c;
                _buffer.HasDepth[idx] = false;
                _buffer.IsSurface[idx] = false;
                _buffer.Hits[idx] = Vector3.zero;

                // Map grid cell back to screen pixel coordinates.
                // xMin/yMin are already clamped to screen bounds, so every
                // ray goes through a valid depth buffer pixel.
                Vector3 screenPx = new Vector3(xMin + c * pixelStride, yMin + r * pixelStride, 0);
                Ray ray = _cam.ScreenPointToRay(screenPx);

                // Sample the depth buffer at this pixel via EnvironmentRaycastManager
                if (raycastManager.Raycast(ray, out var hit))
                {
                    _buffer.Hits[idx] = hit.point;
                    _buffer.HasDepth[idx] = true;
                }
            }
        return true;
    }

    /// <summary>
    /// Executes the Burst-compiled Jobs to extract the forearm surface from the raw depth data.
    /// Stage 1 (Seed): A parallel job that marks depth cells inside a cylinder around the wrist->elbow axis.
    /// Stage 2 (Flood): A background job that expands from the seeds using a BFS to find all 3D-connected forearm cells.
    /// Blocks the main thread until the flood fill is complete.
    /// </summary>
    void RunExtractionJobs()
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
        JobHandle seedHandle = seedJob.Schedule(_rows * _cols, 64);

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

    void OnDestroy() => _buffer.Dispose();

    /// <summary>
    /// Extracts ordered boundary contours from the kept-cell grid and
    /// applies a 1D moving average along each chain. Smooths edge shape
    /// without pulling vertices toward interior. Boundary = kept cell
    /// with at least one non-kept or out-of-bounds neighbor.
    /// </summary>
    void SmoothBoundary()
    {
        if (edgeSmoothPasses <= 0) return;

        // Tag boundary cells
        if (_boundaryVisited == null || _boundaryVisited.GetLength(0) != _rows ||
                                        _boundaryVisited.GetLength(1) != _cols)
            _boundaryVisited = new bool[_rows, _cols];

        for (int pass = 0; pass < edgeSmoothPasses; pass++)
        {
            // Reset visited flags for chain extraction
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                    _boundaryVisited[r, c] = false;

            // Find and smooth each connected boundary contour
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                {
                    if (!IsBoundaryCell(r, c) || _boundaryVisited[r, c]) continue;

                    // Trace an ordered chain starting from this cell
                    TraceChain(r, c);

                    if (_boundaryChain.Count < 3) continue;

                    // 1D moving average along the chain
                    _chainSmoothed.Clear();
                    for (int i = 0; i < _boundaryChain.Count; i++)
                    {
                        Vector3 sum = Vector3.zero;
                        int count = 0;

                        // Window centered on i, clamped to chain bounds
                        int lo = Mathf.Max(0, i - edgeWindowRadius);
                        int hi = Mathf.Min(_boundaryChain.Count - 1, i + edgeWindowRadius);

                        for (int j = lo; j <= hi; j++)
                        {
                            var cell = _boundaryChain[j];
                            sum += _buffer.Hits[cell.x * _cols + cell.y];
                            count++;
                        }

                        _chainSmoothed.Add(sum / count);
                    }

                    // Write smoothed positions back
                    for (int i = 0; i < _boundaryChain.Count; i++)
                    {
                        var cell = _boundaryChain[i];
                        _buffer.Hits[cell.x * _cols + cell.y] = _chainSmoothed[i];
                    }
                }
        }
    }

    /// <summary>
    /// Is this cell kept with at least one non-kept or OOB neighbor?
    /// </summary>
    bool IsBoundaryCell(int r, int c)
    {
        if (!_buffer.IsSurface[r * _cols + c]) return false;
        for (int n = 0; n < 8; n++)
        {
            int nr = r + _neighborDr[n], nc = c + _neighborDc[n];
            if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols || !_buffer.IsSurface[nr * _cols + nc])
                return true;
        }
        return false;
    }

    /// <summary>
    /// Greedy contour walk from a starting boundary cell. At each step,
    /// picks the nearest unvisited boundary neighbor. Produces an ordered
    /// chain in _boundaryChain.
    /// </summary>
    void TraceChain(int startR, int startC)
    {
        _boundaryChain.Clear();
        int r = startR, c = startC;

        while (true)
        {
            _boundaryVisited[r, c] = true;
            _boundaryChain.Add(new Vector2Int(r, c));

            // Find next unvisited boundary neighbor (prefer 4-connected for
            // cleaner chain order, fall back to 8-connected)
            int bestR = -1, bestC = -1;
            bool found = false;

            for (int n = 0; n < 8; n++)
            {
                int nr = r + _neighborDr[n], nc = c + _neighborDc[n];
                if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols) continue;
                if (_boundaryVisited[nr, nc] || !IsBoundaryCell(nr, nc)) continue;

                bestR = nr;
                bestC = nc;
                found = true;

                // 4-connected neighbors are indices 1,3,4,6 in the offset arrays.
                // Prefer these for straighter chain ordering.
                if (n == 1 || n == 3 || n == 4 || n == 6) break;
            }

            if (!found) break;
            r = bestR;
            c = bestC;
        }
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