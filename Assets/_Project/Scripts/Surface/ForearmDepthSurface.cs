// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

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
///                     (axis, right, up), smooth pronation, and the portrait/landscape orientation.
///   2. DepthReadback: Stabilize the depth with a 3-frame reprojected median (DepthTemporalMedian),
///                     blit the stabilized depth through MetaDepthCopy.shader, GPU-readback the forearm
///                     crop, and unproject each sampled pixel into a flat world-space hit grid (rows × cols).
///   3. HandMask:      Render the hand mesh as a GPU silhouette (CommandBuffer.DrawMesh) into a
///                     depth-frame mask before stabilization. The median's extract pass carves the finger
///                     to invalid (a hole the rest of the pipeline skips) and reconstructs the bleed it
///                     lifts around itself — excluding the hand from extraction and touch without raising
///                     the mesh at the contact point.
///   4. Extraction:    Mark cells inside a tight seed cylinder (seedRadialDist) along the
///                     wrist->elbow axis as definite forearm, then BFS-flood from those seeds
///                     across depth-connected neighbors up to a wider flood cylinder
///                     (maxFloodDist) to grow the full forearm patch (IsSurface flags).
///   5. Smoothing:     Boundary flicker is removed upstream by the temporal median (step 2); a parallel
///                     boundary smoother de-steps the mesh edge by averaging each boundary cell with its
///                     boundary neighbors.
///   6. Mesh:          Convert the final IsSurface hits to vertices, tile quads/tris with
///                     an edge-length rejection pass, compute UVs corrected for arm twist,
///                     and upload to the GPU mesh.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ForearmDepthSurface : MonoBehaviour
{
    // ------------------------------------------------------------------
    // INSPECTOR PARAMETERS
    // ------------------------------------------------------------------
    [Header("References")]
    [Tooltip("Body skeleton providing wrist and elbow bone transforms")]
    public OVRSkeleton bodySkeleton;
    [Tooltip("Hand SkinnedMeshRenderer for GPU depth masking and silhouette rendering")]
    public SkinnedMeshRenderer handMesh;
    [Tooltip("Hand skeleton for fingertip bone positions used in touch detection")]
    public OVRSkeleton handSkeleton;
    [Tooltip("Camera transform used for screen-space projection and depth ray origin")]
    public Transform centerEyeAnchor;
    [Tooltip("Material for rendering the forearm surface. Falls back to transparent cyan if unset")]
    public Material surfaceMaterial;

    // ------------------------------------------------------------------
    // HAND MASKING — the hand is rendered as a GPU silhouette before each blit; masked depth
    // pixels arrive HasDepth=false, excluding the hand from reconstruction and touch.
    // ------------------------------------------------------------------
    [Header("Hand Masking")]
    [Tooltip("Depth texels the hand silhouette is grown by (3x3 max). Covers the stereo bleed/lift " +
             "around the hand plus readback latency. Carved from depth history; the lifted ring inside " +
             "this margin is reconstructed from clean surface at consume, not deleted.")]
    [Range(0, 10)] public int handMarginTexels = 8;

    [Tooltip("Inner cushion (depth texels) around the hand kept as a hole, covering the real hand that " +
             "peeks past the rendered mask. Without it, peek-through gets flattened to arm — a sliver " +
             "trailing onto the hand. Raise until the sliver clears. Must stay below handMarginTexels.")]
    [Range(0, 4)] public int occlusionMarginTexels = 1;

    [Tooltip("Depth window (m) behind the nearest borrowed sample that still counts as the same " +
             "surface when reconstructing the lifted ring. Wide enough to span the forearm, tight " +
             "enough to reject a farther surface (e.g. a table behind the arm).")]
    [Range(0.005f, 0.1f)] public float borrowDepthBand = 0.03f;

    // ------------------------------------------------------------------
    // SEED + FLOOD - The depth crop pads by maxFloodDist so the readback
    // region covers the full area the flood can reach.
    // ------------------------------------------------------------------
    [Header("Seed + Flood")]
    [Tooltip("Include the palm (wrist -> middle-finger MCP) in the reconstruction. Off = forearm only.")]
    public bool enablePalm = true;
    [Tooltip("Max radial distance from the arm axis for seed cells — tight inner cylinder of confident forearm hits (m)")]
    [Range(0.02f, 0.1f)] public float seedRadialDist = 0.05f;
    [Tooltip("Max radial distance from the arm axis for flood cells — outer wall that caps BFS growth (m)")]
    [Range(0.02f, 0.2f)] public float maxFloodDist = 0.1f;
    [Tooltip("How far past the elbow the forearm cylinder extends (m). The wrist-side cap is flat " +
             "(at the wrist); the palm cylinder takes over from there.")]
    [Range(0f, 0.15f)] public float maxFromElbow = 0.02f;
    [Tooltip("Max 3D flood step between adjacent grid hits to count as connected (m).")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.01f;

    // ------------------------------------------------------------------
    // BOUNDARY SMOOTHING — de-steps the mesh edge.
    // ------------------------------------------------------------------
    [Header("Boundary Smoothing")]
    [Tooltip("Boundary smoothing passes. 0 = no edge smoothing. Prefer more passes over a large radius.")]
    [Range(0, 5)] public int edgeSmoothPasses = 3;
    [Tooltip("Neighborhood half-width (cells) averaged per boundary pass. >2 starts rounding the arm's real shape.")]
    [Range(1, 6)] public int edgeWindowRadius = 2;

    // ------------------------------------------------------------------
    // MESH
    // ------------------------------------------------------------------
    [Header("Mesh")]
    [Tooltip("Triangle discontinuity cut: drop a face whose two cells differ in true depth by more " +
             "than this fraction. Grazing-tolerant (fills steep surface, no holes) but cuts " +
             "self-occluded folds (no webbing). Lower = stricter. ~0.15 is a good start.")]
    [Range(0.03f, 0.5f)] public float depthStepRatio = 0.15f;

    // ------------------------------------------------------------------
    // DISPLAY — the physical UV projection window on the arm surface.
    // ------------------------------------------------------------------
    [Header("Display")]
    [Tooltip("Physical height of the display region along the arm (m). Primary value — set this first.")]
    public float displayHeight = 0.4f;
    [Tooltip("Physical width of one display panel (m). Set equal to displayHeight for square (undistorted) pixels.")]
    public float displayWidth = 0.4f;
    [Tooltip("Center of the display window along the arm axis from the wrist (m).")]
    public float displayOffset = 0.08f;
    [Tooltip("Auto = swap portrait/landscape image with the arm's pitch. Portrait/Landscape = lock to " +
             "one image, no swap.")]
    public DisplayOrientation orientationMode = DisplayOrientation.Portrait;
    [Tooltip("Image shown when the arm is upright (portrait). Drives the material's UI Texture.")]
    public Texture portraitTexture;
    [Tooltip("Image shown when the arm is horizontal (landscape). Author it sideways: orientation swaps the texture.")]
    public Texture landscapeTexture;

    // ------------------------------------------------------------------
    // PIPELINE DATA BUSES — shared NativeArrays flowing between stages each frame (SurfaceBuffer =
    // per-cell flags + world hit grid; MeshBuffer = vertex/UV/index arrays for upload).
    // ------------------------------------------------------------------
    private SurfaceBuffer _surfaceBuffer = new SurfaceBuffer();
    private MeshBuffer _meshBuffer = new MeshBuffer();

    // Frame-pipelining: OnDepthReady schedules the extract->boundary->mesh chain but does NOT
    // block on it. _pendingMesh is its final handle; LateUpdate harvests (instant Complete +
    // upload) once it's done, so the main thread never stalls waiting for the CPU pipeline.
    private JobHandle _pendingMesh;
    private bool _harvestPending;

    // ------------------------------------------------------------------
    // PIPELINE STAGES
    // Constructed once in Awake with their config parameters and called
    // in sequence inside OnDepthReady.
    // ------------------------------------------------------------------
    private ArmFrame _armFrame;                 // Bone resolution and arm coordinate frame
    private HandMask _handMask;                 // Baked mesh for GPU silhouette + touch vertices
    private SurfaceExtractor _surfaceExtractor; // Seed cylinder + BFS flood
    private DepthReadback _depthReadback;       // GPU crop, readback, and world-space unproject
    private MeshGenerator _meshGenerator;       // Vertex/UV/triangle generation
    private BoundarySmoother _boundarySmoother; // parallel edge de-stepping (interior is GPU-smoothed)

    // ------------------------------------------------------------------
    // UNITY RENDERING
    // Standard Unity mesh pipeline: MeshFilter holds the Mesh asset,
    // MeshRenderer draws it each frame with _mat.
    // ------------------------------------------------------------------
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh;
    private Material _mat;
    // UI texture currently bound to the material; the orientation swap only writes on a change.
    private Texture _activeTexture;
    // Cached in Start: true if the material exposes _MainTex (false for the debug cyan fallback).
    private bool _hasMainTex;

    // ------------------------------------------------------------------
    // PER-FRAME STATE
    // ------------------------------------------------------------------
    // Grid dimensions of the current depth crop (in cells, not pixels).
    int _rows, _cols;
    // Backpressure guard: prevents a new GPU readback from being scheduled
    // while the previous frame's Burst chain is still executing, which would
    // alias SurfaceBuffer reads and writes across frames.
    private bool _isProcessingMesh = false;
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
        _armFrame = new ArmFrame(bodySkeleton, centerEyeAnchor, orientationMode, enablePalm);
        _handMask = new HandMask(handMesh, handSkeleton);
        _depthReadback = new DepthReadback(_handMask, handMarginTexels, occlusionMarginTexels, borrowDepthBand);
        _surfaceExtractor = new SurfaceExtractor(seedRadialDist, maxFloodDist, maxFromElbow, connectivityThreshold);
        _boundarySmoother = new BoundarySmoother(edgeSmoothPasses, edgeWindowRadius);
        _meshGenerator = new MeshGenerator(depthStepRatio, displayOffset, displayWidth, displayHeight);
    }

    /// <summary>
    /// Initializes the dynamic mesh asset and assigns or creates the surface material.
    /// </summary>
    void Start()
    {
        // Set up the mesh that gets rebuilt every frame
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _mesh = new Mesh
        {
            name = "ForearmDepth",
            // UInt32 index format allows meshes to bypass the legacy 16-bit limit.
            // The depth-resolution grid (up to ~320×320 cells) can exceed the UInt16 vertex limit.
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        // Tell Unity this mesh changes every frame so the GPU allocates its
        // buffer in a write-friendly memory region (avoids stalls on upload).
        _mesh.MarkDynamic();
        _meshFilter.mesh = _mesh;

        // Use assigned material if provided, otherwise a transparent cyan fallback for debug
        _meshRenderer.material = surfaceMaterial != null ? surfaceMaterial : MakeFallback();
        _mat = _meshRenderer.material;

        _hasMainTex = _mat.HasProperty("_MainTex");

        // Show the portrait image until the arm frame reports an orientation. If unset, leave the
        // material's own UI Texture untouched.
        if (_hasMainTex && portraitTexture != null)
        {
            _mat.SetTexture("_MainTex", portraitTexture);
            _activeTexture = portraitTexture;
        }
    }

    /// <summary>
    /// Creates a semi-transparent cyan material for debug visualization of the mesh
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
    /// Requests a new depth readback each frame (unless one is in flight or no new depth
    /// frame has arrived — reconstruction is capped at the depth rate, ~25 Hz).
    /// Runs in LateUpdate so bone transforms are finalized after animation
    /// and physics before they are consumed by the arm coordinate frame.
    /// </summary>
    void LateUpdate()
    {
        // HARVEST: once the deferred extract->boundary->mesh chain has finished on worker threads,
        // complete it (instant — no stall) and upload the mesh. Done first so it still completes if
        // tracking drops this frame. Frees _isProcessingMesh so a new cycle can dispatch below.
        if (_harvestPending && _pendingMesh.IsCompleted)
        {
            _pendingMesh.Complete();
            _meshGenerator.Finish(_meshBuffer, out _projCenter);
            UpdateUnityMesh();
            _hasFrame         = true;
            _harvestPending   = false;
            _isProcessingMesh = false;
        }

        // Recompute the arm coordinate frame from the latest bone positions.
        // Returns false if bones are invalid or tracking is unavailable.
        if (!_armFrame.TryUpdate())
        {
            _hasFrame = false;
            return;
        }

        // Swap the displayed image to match the arm's orientation. ArmFrame decides portrait vs
        // landscape; the consumer supplies both textures. Skipped on the debug fallback (no _MainTex).
        if (_hasMainTex)
        {
            Texture target = _armFrame.IsLandscape ? landscapeTexture : portraitTexture;
            if (target != _activeTexture)
            {
                _activeTexture = target;
                _mat.SetTexture("_MainTex", target);
            }
        }

        // Fingertip touch candidates are cheap bone reads, updated every frame so touch tracks
        // the hand at render rate. The expensive hand-mesh bake lives inside TryDispatch, which
        // runs it only when a reconstruction actually dispatches (~depth rate).
        _handMask.UpdateFingertips();

        // Only request a new GPU readback if the previous frame's pipeline has finished.
        // The flag is set to TryDispatch()'s return value: TryDispatch has several silent
        // early-return paths (no depth matrices, no new depth frame, arm off-screen) that
        // never call OnDepthReady, so arming unconditionally would permanently deadlock the
        // pipeline.
        if (!_isProcessingMesh)
        {
            _isProcessingMesh = _depthReadback.TryDispatch(
                _armFrame, maxFloodDist,
                _surfaceBuffer, OnDepthReady
            );
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
        // Finish any in-flight deferred chain before disposing the buffers it reads/writes.
        if (_harvestPending) _pendingMesh.Complete();
        _handMask?.Dispose();
        _depthReadback?.Dispose();
        _meshBuffer?.Dispose();
        _surfaceBuffer.Dispose();
        _meshGenerator?.Dispose();
    }

    /// <summary>
    /// Callback invoked on the main thread once the GPU readback completes. Completes the cheap
    /// unproject job (it reads the readback NativeArray, which is only valid during this callback),
    /// then SCHEDULES the extract -> boundary -> mesh chain WITHOUT blocking on it. The chain is
    /// harvested a frame later in LateUpdate (frame-pipelined), so the main thread never stalls on
    /// the CPU pipeline. Arm-frame values are copied into the jobs at schedule time, so deferring
    /// the harvest by a frame is safe.
    /// </summary>
    /// <param name="sampleHandle">JobHandle for the scheduled depth unproject job (completed here).</param>
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

        // Complete the depth-unproject job NOW: it reads the AsyncGPUReadback NativeArray, which is
        // only valid for the duration of this callback. It's cheap (a 1:1 grid copy); afterwards
        // buffer.Hits is populated and the readback buffer can be safely released.
        sampleHandle.Complete();

        // Schedule extract -> boundary -> mesh as ONE deferred chain (no Complete here). Hand pixels
        // already have HasDepth=false (rejected in MetaDepthCopy.shader), so seed+flood skips them.
        JobHandle h = _surfaceExtractor.Schedule(
            _surfaceBuffer, _rows, _cols,
            _armFrame.WristPos, _armFrame.ElbowPos, _armFrame.Axis,
            _armFrame.PalmCapPos, _armFrame.HasPalm,
            default);
        h = _boundarySmoother.Schedule(_surfaceBuffer, _rows, _cols, h);
        h = _meshGenerator.Schedule(
            _meshBuffer, _surfaceBuffer, _rows, _cols,
            _armFrame.WristPos, _armFrame.Axis, _armFrame.AxisRight,
            _armFrame.Pronation,
            transform.worldToLocalMatrix,
            h);

        // Flush the chain onto worker threads now so it progresses before the harvest check.
        JobHandle.ScheduleBatchedJobs();

        // Hand off to LateUpdate: it harvests (instant Complete + upload) once the chain finishes.
        // _isProcessingMesh stays true until then, guarding the buffers from a new cycle.
        _pendingMesh    = h;
        _harvestPending = true;
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

        // Normals are precomputed off the main thread by NormalsJob — upload directly instead of
        // the single-threaded Mesh.RecalculateNormals().
        _mesh.SetNormals(_meshBuffer.Normals, 0, _meshBuffer.VertexCount);

        // SetIndices with NativeArray<int> avoids the managed array allocation
        // that SetTriangles(List<int>) would require.
        _mesh.SetIndices(
            _meshBuffer.Triangles,
            0,
            _meshBuffer.TriangleCount,
            MeshTopology.Triangles,
            0
        );

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
    public bool    IsLandscape      => _armFrame.IsLandscape;
    public Mesh    SurfaceMesh      => _mesh;

    // ------------------------------------------------------------------
    // PUBLIC API — SURFACE BUFFER
    // Expose buffer internals for interaction logic and other consumers
    // that need to query surface state (hit testing, finger depth comparisons).
    // ------------------------------------------------------------------
    public SurfaceBuffer SurfaceBuf  => _surfaceBuffer;
    public int           SurfaceRows => _rows;
    public int           SurfaceCols => _cols;
    public float         ProjCenter  => _projCenter;
    public Material      SurfaceMat  => _mat;
    // False while the deferred extract->boundary->mesh chain is writing SurfaceBuf on worker
    // threads (dispatch callback -> harvest, ~1 frame per cycle). Main-thread consumers must not
    // read Hits/IsSurface in that window: the collections safety checks that would catch the race
    // are disabled in device builds, so it corrupts silently.
    public bool          SurfaceStable => !_harvestPending;

    // ------------------------------------------------------------------
    // PUBLIC API — HAND VERTICES
    // Downsampled world-space hand positions baked each frame. Consumed by
    // ForearmInteraction to iterate finger candidates for touch detection.
    // ------------------------------------------------------------------
    public Vector4[] HandVertices    => _handMask.Vertices;
    public int       HandVertexCount => _handMask.VertexCount;
    public bool      HasHandVertices => _handMask.HasVertices;
}
