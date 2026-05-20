using UnityEngine;
using Unity.Jobs;
using Surface.Buffer;
using Surface.Core;
using Surface.Math;
using Surface.Processing;
using Surface.Hand;

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
    [Tooltip("Right Hand skeleton. Masks interacting hand from forearm depth")]
    public OVRSkeleton handSkeleton;
    [Tooltip("Camera transform used for screen-space projection and depth ray origin")]
    public Transform centerEyeAnchor;
    [Tooltip("Material for rendering the forearm surface. Falls back to transparent cyan if unset")]
    public Material surfaceMaterial;

    [Header("Hand Masking")]
    [Range(0.01f, 0.04f)]
    public float handMaskRadius = 0.02f;

    [Header("Sampling")]
    [Tooltip("Screen-space step between depth samples (px). Lower = denser mesh, more raycasts")]
    [Range(2, 20)] public int pixelStride = 8;

    [Header("Seed (wrist->elbow cylinder filter)")]
    [Tooltip("(Also used in Flood) Max perpendicular distance from the arm axis to count as inside the seed cylinder (m)")]
    [Range(0.02f, 0.2f)] public float maxRadialDist = 0.15f;
    [Tooltip("How far before the wrist (negative, along axis) seed cells are allowed (m)")]
    [Range(-0.05f, 0f)] public float minFromWrist = -0.02f;
    [Tooltip("How far past the elbow (along axis) seed cells are allowed (m)")]
    [Range(0f, 0.10f)] public float maxFromElbow = 0.05f;

    [Header("Flood (depth connectivity expansion)")]
    [Tooltip("Max 3D step between adjacent grid hits to count as connected (m).")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.025f;

    [Header("Smoothing")]
    [Tooltip("Spatial smoothing passes on depth hits. 0 = raw depth")]
    [Range(0, 5)]
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

    [Header("Display")]
    [Tooltip("Physical width of the display region on the arm (m)")]
    public float displayWidth = 0.05f;
    [Tooltip("Physical height of the display region along the arm (m)")]
    public float displayHeight = 0.05f;
    [Tooltip("How far from the wrist (along axis) to center the display (m)")]
    public float displayOffset = 0.12f;

    [Header("Debug")]
    [Tooltip("Skip seed/flood. Treat every valid depth pixel as surface and mesh it. " +
             "Use to visualize the raw unprojection independent of arm-finding logic. " +
             "When on, bump maxQuadEdge to ~0.5 so the mesh doesn't get pruned by edge length.")]
    public bool bypassSeedFlood = false;

    // Camera Reference
    Camera _cam;

    // True if this frame produced a valid mesh
    bool _hasFrame;

    // Classes
    private HandMask _handMask;
    private SurfaceExtractor _surfaceExtractor;
    private DepthReadback _depthReadback;
    private MeshGenerator _meshGenerator;
    private SurfaceSmoother _surfaceSmoother;

    // Mesh components
    MeshFilter _meshFilter; 
    MeshRenderer _meshRenderer; 
    Mesh _mesh;

    // Depth readback
    private int _cropX, _cropY, _cropW, _cropH;
    private bool _isProcessingMesh = false;

    // Current grid dimensions
    int _rows, _cols;

    // Buffers
    private SurfaceBuffer _surfaceBuffer = new SurfaceBuffer();
    private MeshBuffer _meshBuffer = new MeshBuffer();

    // Frame state computed in LateUpdate, consumed by UV() and public API
    static readonly int wristBoneIndex = 19; // Index of the wrist bone in OVRSkeleton.Bones
    static readonly int elbowBoneIndex = 11; // Index of the elbow bone in OVRSkeleton.Bones
    Transform _wrist, _elbow;
    Vector3 _wristPos, _elbowPos;   // Cached bone world positions
    Vector3 _axis;                  // Wrist->elbow direction (normalized)
    Vector3 _axisRight, _axisUp;    // Orthogonal frame perpendicular to axis
    float _projCenter;              // Linear projection center for UV mapping.
    static readonly Vector3 _wristUpLocal = Vector3.up; // Local up
    float _pronationAngle;          // Signed angle (rad) of forearm rotation
    float _smoothedPronation;       // Temporally smoothed to reduce bone jitter
    float _orientationAngle;        // Current snapped rotation (0 or ±π/2)

    void Awake()
    {
        _handMask = new HandMask(handSkeleton, handMaskRadius);
        _depthReadback = new DepthReadback();
        _surfaceExtractor = new SurfaceExtractor(maxRadialDist, minFromWrist, maxFromElbow, connectivityThreshold);
        _surfaceSmoother  = new SurfaceSmoother(smoothPasses, edgeSmoothPasses, edgeWindowRadius);
        _meshGenerator = new MeshGenerator(maxQuadEdge, displayOffset, displayWidth, displayHeight);
    }

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

        // Use assigned material if provided, otherwise a transparent cyan fallback for debug
        _meshRenderer.material = surfaceMaterial != null ? surfaceMaterial : MakeFallback();
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
        if (!SetupBasis()) {
            _hasFrame = false;
            return;
        }

        // DEPTH MATRIX VALIDATION (Quest 3 Specific)
        // We must fetch these every frame as Meta updates them for the current head pose
        Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        if (depthMatrices == null || depthMatrices.Length == 0) return;

        // CALCULATE SCREEN-SPACE CROP
        // Project the 3D arm into the depth buffer's 2D space to find the active region
        Vector3 camPos = _cam.transform.position;
        if (!BoundingBox.CalculateArmBounds(
            ref depthMatrices[0], ref _wristPos, ref _elbowPos, ref camPos, 
            _cam.fieldOfView, _cam.pixelWidth, _cam.pixelHeight, 
            maxRadialDist, pixelStride, 
            out _cropX, out _cropY, out _cropW, out _cropH)) 
        {
            return; // Arm is likely behind the camera or off-screen
        }

        // ASYNC GPU READBACK REQUEST
        // Only request a new frame if the previous Burst/Readback job has finished
        if (!_isProcessingMesh)
        {
            _depthReadback.Schedule(
                _cam.pixelWidth, _cam.pixelHeight,
                _cropX, _cropY, _cropW, _cropH,
                _surfaceBuffer, pixelStride,
                OnDepthReady
            );
        }
    }

    void OnDestroy()
    {
        _handMask?.Dispose();
        _depthReadback?.Dispose();
        _meshBuffer?.Dispose();
        _surfaceBuffer.Dispose();
        _meshGenerator?.Dispose();
    }

    private bool SetupBasis() 
    {
        // 1. BASIC COMPONENT VALIDATION
        if (bodySkeleton == null || centerEyeAnchor == null) return false;
        
        if (_cam == null) _cam = centerEyeAnchor.GetComponent<Camera>();
        if (_cam == null) return false;

        // 2. BONE RESOLUTION
        // Check if the skeleton has initialized and indices are within range
        var bones = bodySkeleton.Bones;
        if (bones == null || bones.Count <= Mathf.Max(wristBoneIndex, elbowBoneIndex)) return false;

        _wrist = bones[wristBoneIndex].Transform;
        _elbow = bones[elbowBoneIndex].Transform;

        // Ensure transforms are valid (can be null if tracking is lost)
        if (_wrist == null || _elbow == null) return false;

        // 3. TRANSFORM & AXIS RESOLUTION
        _wristPos = _wrist.position;
        _elbowPos = _elbow.position;
        
        // Safety check: avoid NaN if wrist and elbow overlap
        Vector3 delta = _elbowPos - _wristPos;
        if (delta.sqrMagnitude < 0.001f) return false; 
        _axis = delta.normalized;

        // 4. ORTHOGONAL FRAME CONSTRUCTION
        // Derive a camera-facing coordinate system for the arm
        Vector3 armMid = (_wristPos + _elbowPos) * 0.5f;
        Vector3 toCamera = (_cam.transform.position - armMid).normalized;
        
        // axisRight points across the arm from the camera's perspective
        _axisRight = Vector3.Cross(_axis, toCamera).normalized;
        _axisUp = Vector3.Cross(_axisRight, _axis).normalized;

        // 5. PRONATION MATH (Physical Rotation)
        // Project the wrist's local "up" into our camera-derived frame to track twist
        Vector3 wristUpWorld = (_wrist.rotation * _wristUpLocal).normalized;
        Vector3 boneRight = Vector3.Cross(_axis, wristUpWorld).normalized;
        
        float cos = Vector3.Dot(boneRight, _axisRight);
        float sin = Vector3.Dot(Vector3.Cross(boneRight, _axisRight), _axis);
        _pronationAngle = Mathf.Atan2(sin, cos);
        
        // Smooth the pronation to ignore micro-jitters in IK/Hand-Tracking
        _smoothedPronation = Mathf.Lerp(_smoothedPronation, _pronationAngle, 0.15f);

        // 6. SCREEN ORIENTATION SNAPPING
        // Determine if the arm is predominantly horizontal or vertical on screen
        float screenX = Vector3.Dot(_axis, _cam.transform.right);
        float screenY = Vector3.Dot(_axis, _cam.transform.up);
        
        // Snaps to 90-degree increments to keep the UI content aligned to the arm's long axis
        float targetOrientation = Mathf.Abs(screenX) > Mathf.Abs(screenY) ? -Mathf.PI * 0.5f : 0f;
        
        // We use a low Lerp value here so the UI "spins" smoothly when you rotate your arm
        _orientationAngle = Mathf.LerpAngle(_orientationAngle, targetOrientation, 0.1f);

        return true;
    }

    void OnDepthReady(JobHandle sampleHandle, int rows, int cols)
    {
        if (rows == 0 || cols == 0)
        {
            _isProcessingMesh = false;
            return;
        }

        _rows = rows;
        _cols = cols;

        // HAND MASKING: Flag depth cells inside hand
        JobHandle maskHandle = _handMask.Schedule(_surfaceBuffer, sampleHandle);

        // EXTRACTION: Seed and Flood to isolate the forearm
        JobHandle extractionHandle;
        if (bypassSeedFlood)
        {
            // Skips seed/flood entirely, for seeing the raw unprojection.
            maskHandle.Complete();
            int total = _rows * _cols;
            for (int i = 0; i < total; i++)
                _surfaceBuffer.IsSurface[i] = _surfaceBuffer.HasDepth[i];
            extractionHandle = default;
        }
        else
        {
            // Schedules the BFS/Flood jobs
            extractionHandle = _surfaceExtractor.Schedule(
                _surfaceBuffer, _rows, _cols,
                _wristPos, _elbowPos, _axis,
                maskHandle);
        }

        // SMOOTHING: Laplacian and Boundary passes
        _surfaceSmoother.Schedule(_surfaceBuffer, _rows, _cols, extractionHandle);

        // MESH GENERATION: Execute the multi-pass Burst pipeline.
        _meshGenerator.Generate(
            _meshBuffer,
            _surfaceBuffer, 
            _rows, _cols,
            _wristPos, _axis, _axisRight,
            _smoothedPronation, _orientationAngle,
            transform.worldToLocalMatrix,
            out float frameCenter
        );

        // GPU UPLOAD: Push NativeArrays to the Mesh object
        UpdateUnityMesh();

        // Unlock so LateUpdate can request the next frame
        _hasFrame = true;
        _isProcessingMesh = false;
    }

    private void UpdateUnityMesh()
    {
        if (_meshBuffer.VertexCount == 0) return;

        _mesh.Clear();

        // Upload Vertices and UVs directly from NativeArrays
        // We use the count returned by the generator to only upload valid data
        _mesh.SetVertices(_meshBuffer.Vertices, 0, _meshBuffer.VertexCount);
        _mesh.SetUVs(0, _meshBuffer.UVs, 0, _meshBuffer.VertexCount);
        _mesh.SetUVs(1, _meshBuffer.EdgeDists, 0, _meshBuffer.VertexCount);

        // Upload Triangles
        // SetIndices is the most efficient way to handle NativeArray<int>
        _mesh.SetIndices(
            _meshBuffer.Triangles, 
            0, 
            _meshBuffer.TriangleCount, 
            MeshTopology.Triangles, 
            0
        );

        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
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