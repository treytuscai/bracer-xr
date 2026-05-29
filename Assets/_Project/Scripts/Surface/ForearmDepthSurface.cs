using UnityEngine;
using Unity.Jobs;
using Surface.Buffer;
using Surface.Core;

/// <summary>
/// Root MonoBehaviour that orchestrates the full forearm surface reconstruction pipeline
/// each frame. Sits at the top of the call chain: it holds references to each stage,
/// drives execution order, and exposes the final mesh and surface state to downstream
/// consumers like ForearmInteraction.
///
/// Pipeline (runs every LateUpdate, asynchronously via AsyncGPUReadback + Burst):
///   1. ArmFrame:      Resolve wrist/elbow bones, compute the arm coordinate frame
///                     (axis, right, up) and smooth pronation/orientation.
///   2. DepthReadback: Project wrist+elbow into screen space to crop the depth texture,
///                     blit through MetaDepthCopy.shader to recover world-space positions,
///                     GPU-readback that crop, and unproject each sampled pixel into a
///                     flat world-space hit grid (rows × cols).
///   3. HandMask:      Flag grid cells that fall within handMaskRadius of any downsampled
///                     hand mesh vertex so they are excluded from the arm surface.
///   4. Extraction:    Mark cells inside a tight seed cylinder (seedRadialDist) along the
///                     wrist->elbow axis as definite forearm, then BFS-flood from those seeds
///                     across depth-connected neighbors up to a wider flood cylinder
///                     (maxRadialDist) to grow the full forearm patch (IsSurface flags).
///   5. Smoothing:     Laplacian passes pull each surface vertex toward its neighbors,
///                     then boundary contour smoothing applies a 1D moving average along
///                     the mesh edge to reduce the staircase from the pixel grid.
///   6. Freeze:        If any hand-masked cells are detected, lock the last clean surface
///                     in wrist-local space and re-inject it each frame so the UI stays
///                     stable during touch interaction. Unfreeze when the hand leaves.
///   7. Mesh:          Convert the final IsSurface hits to vertices, tile quads/tris with
///                     an edge-length rejection pass, compute cylindrical UVs corrected for
///                     arm twist and orientation, and upload to the GPU mesh.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ForearmDepthSurface : MonoBehaviour
{
    // ------------------------------------------------------------------
    // INSPECTOR REFERENCES
    // All four must be assigned before play. bodySkeleton drives the arm
    // coordinate frame; handMesh drives masking and occlusion fade;
    // centerEyeAnchor provides the camera used for projection math.
    // ------------------------------------------------------------------
    [Header("References")]
    [Tooltip("Body skeleton providing wrist and elbow bone transforms")]
    public OVRSkeleton bodySkeleton;
    [Tooltip("Hand SkinnedMeshRenderer for depth masking and occlusion fade")]
    public SkinnedMeshRenderer handMesh;
    [Tooltip("Camera transform used for screen-space projection and depth ray origin")]
    public Transform centerEyeAnchor;
    [Tooltip("Material for rendering the forearm surface. Falls back to transparent cyan if unset")]
    public Material surfaceMaterial;

    // ------------------------------------------------------------------
    // HAND MASKING
    // A larger radius excludes more of the hand from the arm surface but
    // can also clip near-wrist cells. The same radius is passed to the
    // shader as a sphere test for the soft occlusion fade.
    // ------------------------------------------------------------------
    [Header("Hand Masking")]
    [Tooltip("Padding around hand mesh vertices to prevent incorporating into arm surface")]
    [Range(0.005f, 0.05f)] public float handMaskRadius = 0.03f;

    // ------------------------------------------------------------------
    // SAMPLING
    // pixelStride trades mesh density for performance. Each stride step
    // is one pixel sample through the depth texture; halving the stride
    // quadruples sample count and Burst job time.
    // ------------------------------------------------------------------
    [Header("Sampling")]
    [Tooltip("Screen-space step between depth samples (px). Lower = denser mesh, more raycasts")]
    [Range(2, 20)] public int pixelStride = 6;

    // ------------------------------------------------------------------
    // SEED + FLOOD
    // seedRadialDist defines the tight inner cylinder: only cells very close
    // to the wrist->elbow axis are trusted as definite forearm and used as
    // BFS starting points. floodRadialDist is the looser outer wall that lets
    // the flood grow to capture the full arm curvature while still preventing
    // runaway expansion into background geometry. The depth crop uses
    // floodRadialDist as its padding so the readback region covers the full
    // area the flood can reach. connectivityThreshold gates each BFS step.
    // ------------------------------------------------------------------
    [Header("Seed + Flood")]
    [Tooltip("Max radial distance from the arm axis for seed cells — tight inner cylinder of confident forearm hits (m)")]
    [Range(0.02f, 0.12f)] public float seedRadialDist = 0.05f;
    [Tooltip("Max radial distance from the arm axis for flood cells — outer wall that caps BFS growth (m)")]
    [Range(0.02f, 0.2f)] public float maxRadialDist = 0.15f;
    [Tooltip("How far before the wrist (negative, along axis) seed cells are allowed (m)")]
    [Range(-0.15f, 0f)] public float minFromWrist = -0.12f;
    [Tooltip("How far past the elbow (along axis) seed cells are allowed (m)")]
    [Range(0f, 0.10f)] public float maxFromElbow = 0.02f;
    [Tooltip("Max 3D flood step between adjacent grid hits to count as connected (m).")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.010f;

    // ------------------------------------------------------------------
    // SMOOTHING
    // smoothPasses runs Laplacian averaging (each cell -> centroid of its
    // surface neighbors). edgeSmoothPasses runs a 1D moving average along
    // boundary chains to reduce the staircase from the pixel grid.
    // edgeWindowRadius controls the half-width of that moving-average window.
    // ------------------------------------------------------------------
    [Header("Smoothing")]
    [Tooltip("Spatial smoothing passes on depth hits. 0 = raw depth")]
    [Range(0, 5)] public int smoothPasses = 3;
    [Tooltip("Smoothing passes over the boundary contour chains. 0 = no edge smoothing")]
    [Range(0, 5)] public int edgeSmoothPasses = 4;
    [Tooltip("Half-width of the moving average window along boundary chains")]
    [Range(1, 6)] public int edgeWindowRadius = 3;

    // ------------------------------------------------------------------
    // MESH
    // maxQuadEdge rejects quads or tris whose longest edge exceeds this
    // threshold, preventing stretched faces across depth gaps (e.g. arm
    // to table). Must be >= connectivityThreshold because flood-connected
    // cells can be farther apart in 3D when the surface is curved.
    // ------------------------------------------------------------------
    [Header("Mesh")]
    [Tooltip("Drop quads/tris whose longest edge exceeds this (m). Must be ≥ connectivityThreshold " +
             "to allow for surface curvature between cells that connected through different flood paths")]
    [Range(0.005f, 0.06f)] public float maxQuadEdge = 0.014f;

    // ------------------------------------------------------------------
    // DISPLAY
    // These define the UV projection window on the arm surface.
    // displayWidth/Height are the physical dimensions of the display region.
    // displayOffset shifts the window center along the arm axis from the wrist.
    // landscapeUOffset corrects for the inner-forearm seam when the arm is
    // held horizontally in the camera frame.
    // ------------------------------------------------------------------
    [Header("Display")]
    [Tooltip("Physical width of the display region on the arm (m)")]
    public float displayWidth = 0.5f;
    [Tooltip("Physical height of the display region along the arm (m)")]
    public float displayHeight = 0.5f;
    [Tooltip("How far from the wrist (along axis) to center the display (m)")]
    public float displayOffset = 0.0f;
    [Tooltip("Extra U offset (in turns) applied when the arm is in landscape orientation, " +
             "to shift the texture start away from the inner forearm")]
    [Range(-1f, 1f)] public float landscapeUOffset = 0.5f;

    // ------------------------------------------------------------------
    // PIPELINE DATA BUSES
    // Shared NativeArrays that flow between all pipeline stages each frame.
    // SurfaceBuffer carries per-cell flags (HasDepth, IsSurface, IsHandMasked)
    // and the world-space hit grid (Hits). MeshBuffer carries the final
    // vertex, UV, and index arrays for GPU upload.
    // ------------------------------------------------------------------
    private SurfaceBuffer _surfaceBuffer = new SurfaceBuffer();
    private MeshBuffer _meshBuffer = new MeshBuffer();

    // ------------------------------------------------------------------
    // PIPELINE STAGES
    // Constructed once in Awake with their config parameters and called
    // in sequence inside OnDepthReady.
    // ------------------------------------------------------------------
    private ArmFrame _armFrame;                 // Bone resolution and arm coordinate frame
    private HandMask _handMask;                 // Flags cells inside the hand mesh volume
    private SurfaceExtractor _surfaceExtractor; // Seed cylinder + BFS flood
    private DepthReadback _depthReadback;       // GPU crop, readback, and world-space unproject
    private MeshGenerator _meshGenerator;       // Vertex/UV/triangle generation
    private ForearmModel _forearmModel;         // Wrist-local freeze/thaw for occlusion hold
    private SurfaceSmoother _surfaceSmoother;   // Laplacian + boundary contour smoothing

    // ------------------------------------------------------------------
    // UNITY RENDERING
    // Standard Unity mesh pipeline: MeshFilter holds the Mesh asset,
    // MeshRenderer draws it each frame with _mat.
    // ------------------------------------------------------------------
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh;
    private Material _mat;

    // ------------------------------------------------------------------
    // PER-FRAME STATE
    // ------------------------------------------------------------------
    // Grid dimensions of the current depth crop (in cells, not pixels).
    int _rows, _cols;
    // Backpressure guard: prevents a new GPU readback from being scheduled
    // while the previous frame's Burst chain is still executing, which would
    // alias SurfaceBuffer reads and writes across frames.
    private bool _isProcessingMesh = false;
    // True when any grid cell is hand-masked; drives the surface freeze.
    private bool _isOccluded = false;
    // Set to true in OnDestroy so the async GPU readback callback can detect
    // that the component has been torn down and skip accessing freed NativeArrays.
    private bool _isDestroyed = false;
    // Set to true once at least one valid surface frame has been produced.
    bool _hasFrame;
    // Mean projected position of all surface hits along AxisRight (world units).
    // Used by MeshGenerator to center the UV window on the visible arm patch
    // rather than on the wrist origin.
    float _projCenter;

    /// <summary>
    /// Constructs all pipeline stages and binds Inspector parameters to each.
    /// Runs before Start so stages are non-null when the first LateUpdate fires.
    /// </summary>
    void Awake()
    {
        _armFrame = new ArmFrame(bodySkeleton, centerEyeAnchor);
        _handMask = new HandMask(handMesh, handMaskRadius);
        _depthReadback = new DepthReadback();
        _surfaceExtractor = new SurfaceExtractor(seedRadialDist, maxRadialDist, minFromWrist, maxFromElbow, connectivityThreshold);
        _surfaceSmoother  = new SurfaceSmoother(smoothPasses, edgeSmoothPasses, edgeWindowRadius);
        _meshGenerator = new MeshGenerator(maxQuadEdge, displayOffset, displayWidth, displayHeight, landscapeUOffset);
        _forearmModel = new ForearmModel();
    }

    /// <summary>
    /// Initializes the dynamic mesh asset and assigns or creates the surface material.
    /// </summary>
    void Start()
    {
        // Set up the mesh that gets rebuilt every frame
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _mesh = new Mesh
        {
            name = "ForearmDepth",
            // UInt32 index format allows meshes to bypass the legacy 16-bit limit. 
            // At small pixelStride values the depth grid easily exceeds the UInt16 limit.
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        // Tell Unity this mesh changes every frame so the GPU allocates its
        // buffer in a write-friendly memory region (avoids stalls on upload).
        _mesh.MarkDynamic();
        _meshFilter.mesh = _mesh;

        // Use assigned material if provided, otherwise a transparent cyan fallback for debug
        _meshRenderer.material = surfaceMaterial != null ? surfaceMaterial : MakeFallback();
        _mat = _meshRenderer.material;
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
        // Configure URP's blend-state properties manually. These are normally
        // set by the material import wizard; bypassing it requires explicit writes.
        fallback.SetFloat("_Surface", 1);
        fallback.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        fallback.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        fallback.SetInt("_ZWrite", 0);
        fallback.renderQueue = 3000;
        return fallback;
    }

    /// <summary>
    /// Requests a new depth readback each frame (unless one is in flight).
    /// Runs in LateUpdate so bone transforms are finalized after animation
    /// and physics before they are consumed by the arm coordinate frame.
    /// </summary>
    void LateUpdate()
    {
        // Recompute the arm coordinate frame from the latest bone positions.
        // Returns false if bones are invalid or tracking is unavailable.
        if (!_armFrame.TryUpdate())
        {
            _hasFrame = false;
            return;
        }

        // Only request a new GPU readback if the previous frame's Burst chain has
        // finished. The flag is set to Schedule()'s return value: Schedule has several
        // silent early-return paths (no depth matrices, arm off-screen) that never call
        // OnDepthReady, so arming unconditionally would permanently deadlock the pipeline.
        // When a readback IS enqueued the flag also blocks SnapshotMesh from writing to
        // _nativeVerts while HandMaskJob holds a ReadOnly reference to the same array.
        if (!_isProcessingMesh)
        {
            _isProcessingMesh = _depthReadback.Schedule(
                _armFrame, maxRadialDist, pixelStride,
                _surfaceBuffer, OnDepthReady
            );
            // Snapshot the hand mesh vertices at the same moment we issue the
            // depth request. This keeps the mask aligned with the depth frame
            // even though GPU readback introduces a frame latency.
            _handMask.SnapshotMesh();
        }
    }

    /// <summary>
    /// Releases all unmanaged NativeArray memory held by pipeline stages.
    /// Each stage that owns persistent NativeArrays implements IDisposable.
    /// </summary>
    void OnDestroy()
    {
        // Signal the async readback callback to bail out if it fires after this point.
        _isDestroyed = true;
        _handMask?.Dispose();
        _depthReadback?.Dispose();
        _meshBuffer?.Dispose();
        _surfaceBuffer.Dispose();
        _meshGenerator?.Dispose();
        _forearmModel?.Dispose();
    }

    /// <summary>
    /// Callback invoked on the main thread once the GPU readback completes.
    /// Runs the full CPU-side pipeline: mask -> extract -> smooth -> freeze -> mesh.
    ///
    /// The job chain uses Unity's JobHandle dependency system: each stage receives
    /// the previous stage's handle and the Burst scheduler guarantees the earlier
    /// stage's NativeArray writes are visible before the next stage reads them.
    /// SurfaceSmoother.Schedule() calls JobHandle.Complete() internally, making all
    /// buffer data readable on the main thread for the occlusion scan that follows.
    /// </summary>
    /// <param name="sampleHandle">JobHandle for the completed depth unproject job.</param>
    /// <param name="rows">Number of grid rows in the current depth crop.</param>
    /// <param name="cols">Number of grid columns in the current depth crop.</param>
    void OnDepthReady(JobHandle sampleHandle, int rows, int cols)
    {
        // Guard against accessing disposed NativeArrays if the callback fires
        // after OnDestroy (AsyncGPUReadback holds the delegate past component teardown).
        if (_isDestroyed) return;

        if (rows == 0 || cols == 0)
        {
            _isProcessingMesh = false;
            return;
        }

        _rows = rows;
        _cols = cols;

        // MASK: Flag cells inside the hand volume.
        // Depends on sampleHandle so Hits is fully populated before the mask
        // job reads world-space positions.
        JobHandle maskHandle = _handMask.Schedule(_surfaceBuffer, sampleHandle);

        // EXTRACT: Seed cylinder + BFS flood to isolate the forearm patch.
        // Depends on maskHandle so IsHandMasked is written before the seed job
        // skips masked cells.
        JobHandle extractionHandle = _surfaceExtractor.Schedule(
            _surfaceBuffer, _rows, _cols,
            _armFrame.WristPos, _armFrame.ElbowPos, _armFrame.Axis,
            maskHandle);

        // SMOOTH: Laplacian passes + boundary contour smoothing.
        // Calls Complete() internally; all arrays are readable on the main
        // thread after this returns.
        _surfaceSmoother.Schedule(_surfaceBuffer, _rows, _cols, extractionHandle);

        // FREEZE: Scan the grid for any hand-masked cell.
        // Any masked cell means the hand is touching the surface, which would
        // corrupt the live reconstruction. Break on the first hit.
        bool wasOccluded = _isOccluded;
        _isOccluded = false;
        int total = _rows * _cols;
        for (int i = 0; i < total; i++)
        {
            if (_surfaceBuffer.IsHandMasked[i]) { _isOccluded = true; break; }
        }

        // Trigger Freeze/Unfreeze only on the leading and trailing edge of
        // occlusion. Calling them every frame while _isOccluded is true would
        // overwrite the frozen snapshot on each frame instead of holding it.
        if (_isOccluded && !wasOccluded)
            _forearmModel.Freeze();
        else if (!_isOccluded && wasOccluded)
            _forearmModel.Unfreeze();

        if (_forearmModel.IsFrozen)
            // Re-inject the wrist-local frozen surface back into SurfaceBuffer so
            // the mesh generator sees stable geometry during touch interaction.
            // Wrist-local storage lets the frozen surface track minor arm movement.
            _forearmModel.Infill(
                _surfaceBuffer, _rows, _cols,
                _armFrame.WristPos, _armFrame.WristRotation);
        else
            // Capture the current smoothed surface into wrist-local space so a
            // clean snapshot is ready if occlusion begins next frame.
            _forearmModel.Capture(
                _surfaceBuffer, _rows, _cols,
                _armFrame.WristPos, _armFrame.WristRotation);

        // MESH: Convert final IsSurface hits to vertices, UVs, and triangles.
        // _projCenter is the mean lateral (AxisRight) projection of the surface,
        // used to center the UV window on the visible arm patch.
        _meshGenerator.Generate(
            _meshBuffer,
            _surfaceBuffer,
            _rows, _cols,
            _armFrame.WristPos, _armFrame.Axis, _armFrame.AxisRight,
            _armFrame.Pronation, _armFrame.Orientation,
            transform.worldToLocalMatrix,
            out _projCenter
        );

        // GPU UPLOAD: Push NativeArrays to the Mesh object, then recompute
        // normals and bounds for lighting and frustum culling.
        UpdateUnityMesh();

        // Unlock so LateUpdate can request the next frame
        _hasFrame = true;
        _isProcessingMesh = false;
    }

    /// <summary>
    /// Uploads the latest geometry from MeshBuffer to the Unity Mesh asset.
    /// Uses VertexCount/TriangleCount from the generator to upload only the
    /// live portion of the pre-allocated NativeArrays, avoiding managed copies.
    /// </summary>
    private void UpdateUnityMesh()
    {
        if (_meshBuffer.VertexCount == 0) return;

        _mesh.Clear();

        // SetVertices/SetUVs with an explicit count upload a slice of the NativeArray
        // directly to the GPU without first copying into a managed List<>.
        _mesh.SetVertices(_meshBuffer.Vertices, 0, _meshBuffer.VertexCount);
        _mesh.SetUVs(0, _meshBuffer.UVs, 0, _meshBuffer.VertexCount);

        // SetIndices with NativeArray<int> avoids the managed array allocation
        // that SetTriangles(List<int>) would require.
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

    // ------------------------------------------------------------------
    // PUBLIC API — ARM FRAME
    // Expose the arm coordinate frame so downstream consumers (e.g.
    // ForearmInteraction) do not need a direct reference to ArmFrame.
    // ------------------------------------------------------------------
    public bool    IsValid          => _hasFrame;
    public Vector3 WristPosition    => _armFrame.WristPos;
    public Vector3 ElbowPosition    => _armFrame.ElbowPos;
    public Vector3 AxisDir          => _armFrame.Axis;
    public Vector3 AxisRight        => _armFrame.AxisRight;
    public Vector3 AxisUp           => _armFrame.AxisUp;
    public float   PronationAngle   => _armFrame.Pronation;
    public float   OrientationAngle => _armFrame.Orientation;
    public Mesh    SurfaceMesh      => _mesh;

    // ------------------------------------------------------------------
    // PUBLIC API — SURFACE BUFFER
    // Expose buffer internals for interaction logic and other consumers
    // that need to query surface state (hit testing, finger depth comparisons).
    // ------------------------------------------------------------------
    public SurfaceBuffer SurfaceBuf  => _surfaceBuffer;
    public int           SurfaceRows => _rows;
    public int           SurfaceCols => _cols;
    // Mean lateral projection of the surface along AxisRight; used externally
    // to keep UV-space coordinates centered on the visible arm patch.
    public float         ProjCenter  => _projCenter;
    public Material      SurfaceMat  => _mat;

    public float     LandscapeUOffset => landscapeUOffset;

    // ------------------------------------------------------------------
    // PUBLIC API — HAND VERTICES
    // Downsampled world-space hand positions baked from the SkinnedMeshRenderer
    // each frame. Used by ForearmProjection.shader for the soft occlusion fade
    // and by interaction code for proximity tests against the surface.
    // ------------------------------------------------------------------
    public Vector4[] HandVertices    => _handMask.Vertices;
    public int       HandVertexCount => _handMask.VertexCount;
    public bool      HasHandVertices => _handMask.HasVertices;
}
