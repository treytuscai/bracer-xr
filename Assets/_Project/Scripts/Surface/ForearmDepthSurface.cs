using UnityEngine;
using System.Collections.Generic;
using Meta.XR;

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

    // Mesh components
    MeshFilter _meshFilter; 
    MeshRenderer _meshRenderer; 
    Mesh _mesh; 
    LineRenderer _line;

    // Per-frame depth sampling grid (all same [_rows, _cols] shape)
    Vector3[,] _hits;   // 3D world position from depth raycast
    Vector3[,] _smoothed; // Scratch buffer for smoothing
    bool[,] _hasDepth;  // Did the raycast return a hit for this cell
    bool[,] _kept;      // Did seed + flood accept this cell into the surface
    int[,] _cellToVert; // Maps grid cell -> vertex index in _verts (-1 if rejected)
    int _rows, _cols;   // Current grid dimensions

    // Reusable buffers for boundary chain extraction
    readonly List<Vector2Int> _boundaryChain = new List<Vector2Int>(512);
    readonly List<Vector3> _chainSmoothed = new List<Vector3>(512);
    bool[,] _boundaryVisited;

    // Mesh construction buffers (pre-allocated, cleared each frame)
    readonly List<Vector3> _verts = new List<Vector3>(2048);
    readonly List<int> _tris = new List<int>(4096);
    readonly List<Vector2> _uvs = new List<Vector2>(2048);
    
    // BFS queue for flood pass (pre-allocated, cleared each use)
    readonly Queue<int> _bfs = new Queue<int>(2048);

    // 8-connected grid neighbor offsets: row and col deltas paired by index
    static readonly int[] _neighborDr = { -1, -1, -1,  0, 0,  1, 1, 1 };
    static readonly int[] _neighborDc = { -1,  0,  1, -1, 1, -1, 0, 1 };

    // Frame state computed in LateUpdate, consumed by UV() and public API
    Vector3 _wristPos, _elbowPos;   // Cached bone world positions
    Vector3 _axis;                  // Wrist->elbow direction (normalized)
    Vector3 _axisRight, _axisUp;    // Orthogonal frame perpendicular to axis
    float _axisLength;              // Wrist-to-elbow distance for UV normalization
    bool _hasFrame;                 // True if this frame produced a valid mesh

    // Linear projection center for UV mapping.
    float _projCenter;          // Center of visible surface projected onto _axisRight (m)

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

        // Sample depth buffer in a padded screen-space bbox around the arm
        if (!Sample(_wristPos, _elbowPos)) return;

        // Stage 1: seed = cylinder filter around the wrist->elbow line.
        // Catches a stable strip of forearm cells even when IOBT elbow is off.
        SeedFromAxisLine(_wristPos, _elbowPos);

        // Stage 2: flood from seeds via depth connectivity.
        // Expands onto forearm cells the seed cylinder missed.
        FloodFromSeeds(_wristPos, _elbowPos);

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

        // Use bone distance for UV normalization. Stable across frames
        // unlike depth coverage extent, adapts to different arm lengths
        _axisLength = (_elbowPos - _wristPos).magnitude;

        // Apply Laplacian smoothing to remove-per pixel depth noise
        SmoothKeptCells();

        SmoothBoundary();

        ComputeProjectedExtent();

        // Stage 3: build triangle mesh from kept cells
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

        // Reallocate only when grid size changes. Per-cell clearing happens
        // in the raycast loop below, so stale data from same-sized frames is safe.
        if (_hits == null || _hits.GetLength(0) != _rows || _hits.GetLength(1) != _cols)
        {
            _hits = new Vector3[_rows, _cols];
            _hasDepth = new bool[_rows, _cols];
            _kept = new bool[_rows, _cols];
        }

        // Cast depth rays through each grid cell and store 3D hit positions.
        // Cells without valid depth are flagged so downstream stages skip them.
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                // Reset state — no cell carries data from previous frames
                _hasDepth[r, c] = false;
                _kept[r, c] = false;
                _hits[r, c] = Vector3.zero;

                // Map grid cell back to screen pixel coordinates.
                // xMin/yMin are already clamped to screen bounds, so every
                // ray goes through a valid depth buffer pixel.
                Vector3 screenPx = new Vector3(xMin + c * pixelStride, yMin + r * pixelStride, 0);
                Ray ray = _cam.ScreenPointToRay(screenPx);

                // Sample the depth buffer at this pixel via EnvironmentRaycastManager
                if (raycastManager.Raycast(ray, out var hit))
                {
                    _hits[r, c] = hit.point;
                    _hasDepth[r, c] = true;
                }
            }
        return true;
    }

    /// <summary>
    /// Seed pass: marks depth cells that fall inside a cylinder around the
    /// wrist->elbow line. These become the starting cells for the flood-fill.
    /// Three filters run in order: too far before the wrist? skip.
    /// Too far past the elbow? skip. Too far from the line? skip.
    /// </summary>
    void SeedFromAxisLine(Vector3 wristPos, Vector3 elbowPos)
    {
        // Wrist->elbow direction for longitudinal bounds
        Vector3 axis = (elbowPos - wristPos).normalized;

        // Max perpendicular distance from the axis line
        // to count as inside the cylinder (avoids sqrt per cell)
        float maxRSq = maxRadialDist * maxRadialDist;

        // Walk every sampled depth cell and test against the cylinder
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                // Skip cells with no depth data
                if (!_hasDepth[r, c]) continue;
                Vector3 p = _hits[r, c];

                // Longitudinal bounds: reject points beyond each end of the arm segment
                float fromWrist = Vector3.Dot(p - wristPos, axis);
                if (fromWrist < minFromWrist) continue;
                float fromElbow = Vector3.Dot(p - elbowPos, axis);
                if (fromElbow > maxFromElbow) continue;

                // Radial bound: perpendicular distance from the wrist->elbow line.
                // Cross product with a unit vector gives the perpendicular component
                // directly. The result is the same regardless of which point on the
                // line we measure from.
                float radialSq = Vector3.Cross(p - elbowPos, axis).sqrMagnitude;

                 // Cell is inside the cylinder — mark as seed for flood pass
                if (radialSq <= maxRSq) _kept[r, c] = true;
            }
    }

    /// <summary>
    /// Flood pass: BFS from every seed cell, expanding onto neighboring depth
    /// cells that pass three checks: 3D-connected (within connectivityThreshold),
    /// within a loose radial bound around the arm axis (floodRadialMultiplier x
    /// maxRadialDist, to prevent leaking onto nearby surfaces like tables), and
    /// within the wrist->elbow longitudinal bounds. This is what catches forearm
    /// surface the seed cylinder missed when the IOBT elbow angle is off.
    /// </summary>
    void FloodFromSeeds(Vector3 wristPos, Vector3 elbowPos)
    {
        // Enqueue all seed cells as BFS starting points.
        // Cells are stored as flat indices (r * _cols + c) to avoid
        // allocating tuples. Decoded back via idx / _cols and idx % _cols.
        _bfs.Clear();
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                if (_kept[r, c]) _bfs.Enqueue(r * _cols + c);

        if (_bfs.Count == 0) return;

        // Wrist->elbow direction for longitudinal bounds
        Vector3 axis = (elbowPos - wristPos).normalized;

        // Max 3D distance between adjacent grid hits to count as
        // the same surface (avoids sqrt per neighbor)
        float connSq = connectivityThreshold * connectivityThreshold;

        // Looser radial cap than the seed pass. Allows the flood to expand
        // past where the wrong-angle axis line reaches, while still rejecting
        // nearby surfaces (e.g. table) that are depth-connected but off the arm
        float floodRadius = maxRadialDist * floodRadialMultiplier;
        float maxFloodRadialSq = floodRadius * floodRadius;

        // Process the queue until no more connected cells can be reached
        while (_bfs.Count > 0)
        {
            // Decode flat index back to grid coordinates
            int idx = _bfs.Dequeue();
            int r = idx / _cols, c = idx % _cols;
            Vector3 currentHit = _hits[r, c];

            // Check all 8 neighbors for possible expansion
            for (int n = 0; n < 8; n++)
            {
                // Neighbor grid coordinates; skip if out of bounds
                int nr = r + _neighborDr[n], nc = c + _neighborDc[n];
                if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols) continue;

                // Skip if already part of the surface or has no depth data
                if (_kept[nr, nc] || !_hasDepth[nr, nc]) continue;

                Vector3 neighborHit = _hits[nr, nc];

                // Depth connectivity: reject if neighbor is too far in 3D
                if ((neighborHit - currentHit).sqrMagnitude > connSq) continue;

                // Radial bound: don't flood onto surfaces far from the arm axis
                float radialSq = Vector3.Cross(neighborHit - elbowPos, axis).sqrMagnitude;
                if (radialSq > maxFloodRadialSq) continue;

                // Same longitudinal bounds as the seed pass.
                // Don't flood before the wrist or past the elbow
                float fromWrist = Vector3.Dot(neighborHit - wristPos, axis);
                if (fromWrist < minFromWrist) continue;
                float fromElbow = Vector3.Dot(neighborHit - elbowPos, axis);
                if (fromElbow > maxFromElbow) continue;

                // Cell passed all checks. Add to surface and continue expanding
                _kept[nr, nc] = true;
                _bfs.Enqueue(nr * _cols + nc);
            }
        }
    }

    /// <summary>
    /// Laplacian smoothing on kept depth cells. Removes per-pixel depth noise
    /// so the mesh looks smooth like skin. Jacobi iteration: each pass snapshots
    /// all positions, then averages each cell toward its neighbors from the
    /// snapshot. Order-independent. Multiple passes spread influence further
    /// with Gaussian-shaped falloff. Only _kept cells participate. Flood-fill
    /// already guarantees surface connectivity.
    /// </summary>
    void SmoothKeptCells()
    {
        // No-op when smoothing is disabled.
        if (smoothPasses <= 0) return;

        // Scratch buffer for Jacobi snapshot (reallocate on grid resize)
        if (_smoothed == null || _smoothed.GetLength(0) != _rows || 
                                 _smoothed.GetLength(1) != _cols)
        {
            _smoothed = new Vector3[_rows, _cols];
        }

        for (int pass = 0; pass < smoothPasses; pass++)
        {
            // Freeze positions so earlier cells don't influence later ones
            System.Array.Copy(_hits, _smoothed, _hits.Length);

            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                {
                    // Skip non-kept cells
                    if (!_kept[r, c]) continue;

                    // Self-weight prevents overshooting
                    Vector3 sum = _smoothed[r, c];
                    float weight = 1f;

                    // Pull toward each valid neighbor's position (8-connected)
                    for (int n = 0; n < 8; n++)
                    {
                        int nr = r + _neighborDr[n], nc = c + _neighborDc[n];

                        // Bounds check: skip neighbors outside the grid
                        if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols) continue;
                        
                        // Only average with kept cells
                        if (!_kept[nr, nc]) continue;

                        sum += _smoothed[nr, nc];
                        weight += 1f;
                    }

                    // Write the averaged position. Division by total weight
                    // produces the centroid of this cell + its valid neighbors.
                    _hits[r, c] = sum / weight;
                }
        }
    }

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
                            sum += _hits[cell.x, cell.y];
                            count++;
                        }

                        _chainSmoothed.Add(sum / count);
                    }

                    // Write smoothed positions back
                    for (int i = 0; i < _boundaryChain.Count; i++)
                    {
                        var cell = _boundaryChain[i];
                        _hits[cell.x, cell.y] = _chainSmoothed[i];
                    }
                }
        }
    }

    /// <summary>
    /// Is this cell kept with at least one non-kept or OOB neighbor?
    /// </summary>
    bool IsBoundaryCell(int r, int c)
    {
        if (!_kept[r, c]) return false;
        for (int n = 0; n < 8; n++)
        {
            int nr = r + _neighborDr[n], nc = c + _neighborDc[n];
            if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols || !_kept[nr, nc])
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
        float min = float.MaxValue, max = float.MinValue;
        float sum = 0f;
        int count = 0;

        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                if (!_kept[r, c]) continue;

                // Project onto _axisRight: signed distance from the arm axis
                // in the direction perpendicular to the arm, within the visible plane.
                float proj = Vector3.Dot(_hits[r, c] - _wristPos, _axisRight);

                sum += proj;
                count++;
                if (proj < min) min = proj;
                if (proj > max) max = proj;
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
        _verts.Clear(); _tris.Clear(); _uvs.Clear();

        // Reuse or reallocate the grid->vertex index lookup
        if (_cellToVert == null || _cellToVert.GetLength(0) != _rows || _cellToVert.GetLength(1) != _cols)
            _cellToVert = new int[_rows, _cols];

        // Build vertex list from kept cells. Each kept cell becomes a vertex.
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                // Rejected cells get -1 so the triangle loop knows to skip them.
                if (!_kept[r, c]) { _cellToVert[r, c] = -1; continue; }

                Vector3 hitPos = _hits[r, c];

                // Store the index this vertex will have once added
                _cellToVert[r, c] = _verts.Count;
                _verts.Add(transform.InverseTransformPoint(hitPos));
                _uvs.Add(UV(hitPos));
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
                    if (!QuadEdgesOK(_hits[r,c], _hits[r,c+1], _hits[r+1,c], _hits[r+1,c+1], maxSq)) 
                        continue;

                    // Split quad into two triangles
                    _tris.Add(topLeft); _tris.Add(botLeft); _tris.Add(topRight);
                    _tris.Add(topRight); _tris.Add(botLeft); _tris.Add(botRight);
                }
            
                // Three corners! Form a single triangle from whichever three exist,
                // but only if no edge stretches across a depth gap.
                else if (topLeft  < 0) {
                    if (!TriEdgesOK(_hits[r,c+1], _hits[r+1,c], _hits[r+1,c+1], maxSq)) continue;
                    _tris.Add(topRight); _tris.Add(botLeft);  _tris.Add(botRight);
                }
                else if (topRight < 0) {
                    if (!TriEdgesOK(_hits[r,c], _hits[r+1,c], _hits[r+1,c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botLeft);  _tris.Add(botRight);
                }
                else if (botLeft  < 0) {
                    if (!TriEdgesOK(_hits[r,c], _hits[r,c+1], _hits[r+1,c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botRight); _tris.Add(topRight);
                }
                else {
                    if (!TriEdgesOK(_hits[r,c], _hits[r+1,c], _hits[r,c+1], maxSq)) continue;
                    _tris.Add(topLeft);  _tris.Add(botLeft);  _tris.Add(topRight);
                }
            }

        // Upload to GPU
        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetUVs(0, _uvs);
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
    /// using a linear planar projection.
    ///
    /// V (0-1) = distance along the arm axis from wrist, normalized by bone length.
    /// U (0-1) = signed distance from the arm axis projected onto _axisRight,
    ///           centered on the visible surface center and normalized by its width.
    ///
    /// This is a flat-decal projection onto the plane defined by (_axis, _axisRight).
    /// For the ~40-60° of arc visible on the inner forearm, the visual difference
    /// from a true cylindrical wrap is negligible (~3% max).
    /// </summary>
    Vector2 UV(Vector3 point)
    {
        Vector3 fromWrist = point - _wristPos;

        // V: fixed physical height, centered at displayOffset from wrist
        float distAlongAxis = Vector3.Dot(fromWrist, _axis);
        float v = ((distAlongAxis - displayOffset) / Mathf.Max(displayHeight, 1e-4f)) + 0.5f;
        v = 1f - v;

        // U: fixed physical width, centered on visible surface
        float projRight = Vector3.Dot(fromWrist, _axisRight);
        float u = ((projRight - _projCenter) / Mathf.Max(displayWidth, 1e-4f)) + 0.5f;

        return new Vector2(u, v);
    }

    public bool    IsValid       => _hasFrame;
    public Vector3 WristPosition => _wristPos;
    public Vector3 ElbowPosition => _elbowPos;
    public Vector3 AxisDir       => _axis;
    public Vector3 AxisRight     => _axisRight;
    public Vector3 AxisUp        => _axisUp;
    public float   AxisLength    => _axisLength;
    public Mesh    SurfaceMesh   => _mesh;
}