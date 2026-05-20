using UnityEngine;
using Unity.Jobs;
using Surface.Buffer;
using Surface.Core;

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

    [Header("Seed + Flood")]
    [Tooltip("Max perpendicular distance from the arm axis to count as inside the seed cylinder (m)")]
    [Range(0.02f, 0.2f)] public float maxRadialDist = 0.15f;
    [Tooltip("How far before the wrist (negative, along axis) seed cells are allowed (m)")]
    [Range(-0.05f, 0f)] public float minFromWrist = -0.02f;
    [Tooltip("How far past the elbow (along axis) seed cells are allowed (m)")]
    [Range(0f, 0.10f)] public float maxFromElbow = 0.05f;
    [Tooltip("Max 3D flood step between adjacent grid hits to count as connected (m).")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.025f;

    [Header("Smoothing")]
    [Tooltip("Spatial smoothing passes on depth hits. 0 = raw depth")]
    [Range(0, 5)]
    public int smoothPasses = 3;
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

    // Shared data flowing between pipeline stages
    private SurfaceBuffer _surfaceBuffer = new SurfaceBuffer();
    private MeshBuffer _meshBuffer = new MeshBuffer();

    // Pipeline stages (constructed in Awake, called in sequence each frame)
    private ArmFrame _armFrame;
    private HandMask _handMask;
    private SurfaceExtractor _surfaceExtractor;
    private DepthReadback _depthReadback;
    private MeshGenerator _meshGenerator;
    private SurfaceSmoother _surfaceSmoother;

    // Unity rendering
    MeshFilter _meshFilter; 
    MeshRenderer _meshRenderer; 
    Mesh _mesh;

    // Per-frame state
    int _rows, _cols;
    private bool _isProcessingMesh = false;
    bool _hasFrame;

    void Awake()
    {
        _armFrame = new ArmFrame(bodySkeleton, centerEyeAnchor);
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
        if (!_armFrame.TryUpdate())
        {
            _hasFrame = false;
            return;
        }

        // ASYNC GPU READBACK REQUEST
        // Only request a new frame if the previous Burst/Readback job has finished
        if (!_isProcessingMesh)
        {
            _depthReadback.Schedule(
                _armFrame, maxRadialDist, pixelStride,
                _surfaceBuffer, OnDepthReady
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
                _armFrame.WristPos, _armFrame.ElbowPos, _armFrame.Axis,
                maskHandle);
        }

        // SMOOTHING: Laplacian and Boundary passes
        _surfaceSmoother.Schedule(_surfaceBuffer, _rows, _cols, extractionHandle);

        // MESH GENERATION: Execute the multi-pass Burst pipeline.
        _meshGenerator.Generate(
            _meshBuffer,
            _surfaceBuffer, 
            _rows, _cols,
            _armFrame.WristPos, _armFrame.Axis, _armFrame.AxisRight,
            _armFrame.Pronation, _armFrame.Orientation,
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
    public Vector3 WristPosition    => _armFrame.WristPos;
    public Vector3 ElbowPosition    => _armFrame.ElbowPos;
    public Vector3 AxisDir          => _armFrame.Axis;
    public Vector3 AxisRight        => _armFrame.AxisRight;
    public Vector3 AxisUp           => _armFrame.AxisUp;
    public float   PronationAngle   => _armFrame.Pronation;
    public float   OrientationAngle => _armFrame.Orientation;
    public Mesh    SurfaceMesh      => _mesh;
}