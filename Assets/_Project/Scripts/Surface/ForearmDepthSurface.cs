using UnityEngine;
using Unity.Jobs;
using Surface.Buffer;
using Surface.Core;

/// <summary>
/// Reconstructs the forearm surface as a triangle mesh each frame using the
/// Quest 3 depth buffer, anchored to wrist and elbow bones from OVRSkeleton.
///
/// Pipeline (runs every LateUpdate):
///   1. Sample:  GPU depth readback in a screen-space crop around the forearm.
///   2. Mask:    Flag depth cells inside the interacting hand's AABB.
///   3. Seed:    Mark cells inside a cylinder around the wrist→elbow axis line.
///   4. Flood:   BFS from seed cells onto depth-connected neighbors.
///   5. Smooth:  Laplacian passes + boundary contour smoothing.
///   6. Freeze:  On any hand occlusion, hold the last clean surface stable.
///   7. Mesh:    Convert kept cells to vertices, tile quads/tris, compute UVs.
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
    [Tooltip("Padding around hand mask to prevent incorporating into mesh surface")]
    [Range(0.01f, 0.06f)] public float handMaskRadius = 0.05f;
    [Header("Finger Occlusion")]
    [Tooltip("Radius of the per-joint sphere that punches a hole in the UI where fingers touch")]
    [Range(0.005f, 0.03f)] public float fingerOccluderRadius = 0.0065f;

    [Header("Sampling")]
    [Tooltip("Screen-space step between depth samples (px). Lower = denser mesh, more raycasts")]
    [Range(2, 20)] public int pixelStride = 6;

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
    [Range(0, 5)] public int smoothPasses = 3;
    [Range(0, 5)] public int edgeSmoothPasses = 4;
    [Tooltip("Half-width of the moving average window along boundary chains")]
    [Range(1, 6)] public int edgeWindowRadius = 3;

    [Header("Mesh")]
    [Tooltip("Drop quads/tris whose longest edge exceeds this (m). Must be ≥ connectivityThreshold " +
             "to allow for surface curvature between cells that connected through different flood paths")]
    [Range(0.005f, 0.06f)] public float maxQuadEdge = 0.03f;

    [Header("Display")]
    [Tooltip("Physical width of the display region on the arm (m)")]
    public float displayWidth = 0.24f;
    [Tooltip("Physical height of the display region along the arm (m)")]
    public float displayHeight = 0.12f;
    [Tooltip("How far from the wrist (along axis) to center the display (m)")]
    public float displayOffset = 0.13f;

    // Shared data flowing between pipeline stages
    private SurfaceBuffer _surfaceBuffer = new SurfaceBuffer();
    private MeshBuffer _meshBuffer = new MeshBuffer();

    // Pipeline stages (constructed in Awake, called in sequence each frame)
    private ArmFrame _armFrame;
    private HandMask _handMask;
    private SurfaceExtractor _surfaceExtractor;
    private DepthReadback _depthReadback;
    private MeshGenerator _meshGenerator;
    private ForearmModel _forearmModel;
    private SurfaceSmoother _surfaceSmoother;

    // Unity rendering
    MeshFilter _meshFilter;
    MeshRenderer _meshRenderer;
    Material _material;
    Mesh _mesh;

    // Per-frame state
    int _rows, _cols;
    private bool _isProcessingMesh = false;
    private bool _isOccluded = false;
    bool _hasFrame;

    void Awake()
    {
        _armFrame = new ArmFrame(bodySkeleton, centerEyeAnchor);
        _handMask = new HandMask(handSkeleton, handMaskRadius);
        _depthReadback = new DepthReadback();
        _surfaceExtractor = new SurfaceExtractor(maxRadialDist, minFromWrist, maxFromElbow, connectivityThreshold);
        _surfaceSmoother  = new SurfaceSmoother(smoothPasses, edgeSmoothPasses, edgeWindowRadius);
        _meshGenerator = new MeshGenerator(maxQuadEdge, displayOffset, displayWidth, displayHeight);
        _forearmModel = new ForearmModel();
    }

    /// <summary>
    /// Initializes the dynamic mesh and assigns or creates a surface material.
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
        _material = _meshRenderer.material = surfaceMaterial != null ? surfaceMaterial : MakeFallback();
        _material.EnableKeyword("SOFT_OCCLUSION");
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
    /// Requests a new depth readback each frame (unless one is in flight).
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
            _handMask.SnapshotJoints();
        }

        UpdateFingerSpheres();
    }

    private void UpdateFingerSpheres()
    {
        if (_material == null) return;

        if (!_handMask.HasCapsules)
        {
            _material.SetInt("_FingerCapsuleCount", 0);
            return;
        }

        _material.SetVectorArray("_FingerCapsuleA", _handMask.CapsuleA);
        _material.SetVectorArray("_FingerCapsuleB", _handMask.CapsuleB);
        _material.SetInt("_FingerCapsuleCount", _handMask.CapsuleCount);
        _material.SetFloat("_FingerRadius", fingerOccluderRadius);
    }

    void OnDestroy()
    {
        _handMask?.Dispose();
        _depthReadback?.Dispose();
        _meshBuffer?.Dispose();
        _surfaceBuffer.Dispose();
        _meshGenerator?.Dispose();
        _forearmModel?.Dispose();
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

        // MASK: flag depth cells inside the hand AABB
        JobHandle maskHandle = _handMask.Schedule(_surfaceBuffer, sampleHandle);

        // EXTRACT: seed cylinder + BFS flood to isolate the forearm
        JobHandle extractionHandle = _surfaceExtractor.Schedule(
            _surfaceBuffer, _rows, _cols,
            _armFrame.WristPos, _armFrame.ElbowPos, _armFrame.Axis,
            maskHandle);

        // SMOOTH: Laplacian passes + boundary contour smoothing.
        // Calls Complete() internally, all arrays are readable after this returns.
        _surfaceSmoother.Schedule(_surfaceBuffer, _rows, _cols, extractionHandle);
        
        // FREEZE: on any hand occlusion hold the last clean surface.
        // Capture runs every live frame so the frozen snapshot is always fresh.
        int maskedCount = 0;
        int total = _rows * _cols;
        for (int i = 0; i < total; i++)
            if (_surfaceBuffer.IsHandMasked[i]) maskedCount++;

        bool wasOccluded = _isOccluded;
        _isOccluded = maskedCount > 0;

        if (_isOccluded && !wasOccluded)
            _forearmModel.Freeze();
        else if (!_isOccluded && wasOccluded)
            _forearmModel.Unfreeze();

        if (_forearmModel.IsFrozen)
            _forearmModel.Infill(
                _surfaceBuffer, _rows, _cols,
                _armFrame.WristPos, _armFrame.WristRotation);
        else
            _forearmModel.Capture(
                _surfaceBuffer, _rows, _cols,
                _armFrame.WristPos, _armFrame.WristRotation);

        // MESH: build geometry from the final IsSurface hits
        _meshGenerator.Generate(
            _meshBuffer,
            _surfaceBuffer, 
            _rows, _cols,
            _armFrame.WristPos, _armFrame.Axis, _armFrame.AxisRight,
            _armFrame.Pronation, _armFrame.Orientation,
            transform.worldToLocalMatrix,
            out _
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
