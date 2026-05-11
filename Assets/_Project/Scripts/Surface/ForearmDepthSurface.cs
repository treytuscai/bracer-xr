using UnityEngine;
using System.Collections.Generic;
using Meta.XR;

/// <summary>
/// Wrist-anchored forearm surface = v1's wrist→elbow line filter as SEED,
/// then BFS flood-fill via depth connectivity to expand outward.
///
/// Why this works when the IOBT angle is off:
///   - The wrist→elbow line still clips part of the real forearm (a strip
///     where the wrong angle happens to intersect skin). Cylinder filter
///     keeps those cells as a stable, multi-cell seed.
///   - Flood-fill expands from those cells onto neighboring depth pixels
///     that are 3D-connected (within connectivityThreshold). Skin is
///     continuous, so the flood walks the whole forearm.
///   - Hands are removed from the depth texture, so the flood naturally
///     stops at the wrist (no leak onto hand). Background depth jumps stop
///     it at arm edges. maxAxisLength caps anything still connected.
///
/// Pipeline:
///   1. axis = (E - W).normalized; far end = W + axis * weLen * extensionFactor
///   2. Sample depth grid in bbox(wristProj, farProj) + padding.
///   3. Seed pass: mark cells inside the wrist-axis cylinder.
///   4. Flood pass: BFS from seeds; add neighbors with 3D step < threshold.
///   5. Build mesh + wrist-frame UVs from kept cells.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ForearmDepthSurface : MonoBehaviour
{
    [Header("References")]
    public OVRSkeleton bodySkeleton;
    public EnvironmentRaycastManager raycastManager;
    public Transform centerEyeAnchor;
    public Material surfaceMaterial;

    [Header("Bones")]
    public int wristBoneIndex = 19;
    public int elbowBoneIndex = 11;

    [Header("Wrist Frame (UV stability)")]
    public Vector3 wristUpLocal = Vector3.up;

    [Header("Sampling")]
    [Range(2, 20)] public int pixelStride = 8;

    [Header("Seed (wrist->elbow cylinder filter)")]
    [Range(0.02f, 0.12f)] public float maxRadialDist = 0.07f;
    [Range(-0.05f, 0f)] public float minFromWrist = -0.02f;
    [Range(0f, 0.10f)] public float maxFromElbow = 0.05f;

    [Header("Flood (depth connectivity expansion)")]
    [Tooltip("Max 3D step between adjacent grid hits to count as connected (m). " +
             "This is what lets the flood expand off the wrong-angle seed line " +
             "onto the actual forearm surface.")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.025f;
    [Range(1f, 3f)] public float floodRadialMultiplier = 1.5f;

    [Header("Mesh")]
    [Tooltip("Drop quads whose longest edge exceeds this (m). Should be ≥ connectivityThreshold " +
             "or quads across the arm width (where curvature creates 3D step) get dropped.")]
    [Range(0.005f, 0.06f)] public float maxQuadEdge = 0.030f;

    [Header("Debug")]
    public bool drawAxis = true;

    Transform _wrist, _elbow; Camera _cam;
    MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; LineRenderer _line;

    Vector3[,] _hits;
    bool[,] _hasDepth; // raw: did we get a depth hit here?
    bool[,] _kept;      // pipeline output: does this cell go into the mesh?
    int _rows, _cols;
    int[,] _cellToVert;

    readonly List<Vector3> _verts = new List<Vector3>(2048);
    readonly List<int> _tris = new List<int>(4096);
    readonly List<Vector2> _uvs = new List<Vector2>(2048);
    readonly Queue<int> _bfs = new Queue<int>(2048);

    // 8-connected grid neighbor offsets: row and col deltas paired by index
    static readonly int[] _neighborDr = { -1, -1, -1,  0, 0,  1, 1, 1 };
    static readonly int[] _neighborDc = { -1,  0,  1, -1, 1, -1, 0, 1 };

    Vector3 _wristPos, _elbowPos, _axis, _axisRight, _axisUp;
    float _axisLength;
    bool _hasFrame;

    void Start()
    {
        _mf = GetComponent<MeshFilter>(); _mr = GetComponent<MeshRenderer>();
        _mesh = new Mesh { name = "ForearmDepth" }; _mesh.MarkDynamic();
        _mf.mesh = _mesh;
        if (surfaceMaterial != null) _mr.material = surfaceMaterial;
        else _mr.material = MakeFallback();

        var go = new GameObject("DebugAxis"); go.transform.SetParent(transform, false);
        _line = go.AddComponent<LineRenderer>();
        _line.useWorldSpace = true; _line.widthMultiplier = 0.004f; _line.positionCount = 2;
        _line.material = new Material(Shader.Find("Sprites/Default"));
        _line.startColor = _line.endColor = Color.green;
        _line.enabled = false;
    }

    Material MakeFallback()
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.color = new Color(0f, 1f, 1f, 0.5f);
        m.SetFloat("_Surface", 1);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0); m.renderQueue = 3000;
        return m;
    }

    void LateUpdate()
    {
        _hasFrame = false;
        if (_line) _line.enabled = false;

        if (raycastManager == null || bodySkeleton == null || centerEyeAnchor == null) return;
        if (!ResolveBones()) return;
        _cam = centerEyeAnchor.GetComponent<Camera>(); if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        _wristPos = _wrist.position;
        _elbowPos = _elbow.position;
        Quaternion wristRot = _wrist.rotation;

        Vector3 we = _elbowPos - _wristPos;
        float weLen = we.magnitude;
        if (weLen < 0.05f) return;
        _axis = we / weLen;

        if (!Sample(_wristPos, _elbowPos)) return;

        // Stage 1: seed = v1's cylinder filter around the wrist→elbow line.
        SeedFromAxisLine(_wristPos, _elbowPos);

        // Stage 2: flood from seeds via depth connectivity.
        // This is what catches forearm cells the wrong-angle line misses.
        FloodFromSeeds(_wristPos, _elbowPos);

        Vector3 wristUp = (wristRot * wristUpLocal).normalized;
        _axisRight = Vector3.Cross(_axis, wristUp).normalized;
        _axisUp = Vector3.Cross(_axisRight, _axis).normalized;

        _axisLength = ComputeExtent();
        if (_axisLength < 0.05f) return;

        BuildMesh();

        _hasFrame = true;
        if (_line && drawAxis)
        {
            _line.enabled = true;
            _line.SetPosition(0, _wristPos);
            _line.SetPosition(1, _wristPos + _axis * _axisLength);
        }
    }

    bool ResolveBones()
    {
        if (_wrist != null && _elbow != null) return true;
        if (bodySkeleton.Bones == null) return false;
        int needed = Mathf.Max(wristBoneIndex, elbowBoneIndex);
        if (bodySkeleton.Bones.Count <= needed) return false;
        _wrist = bodySkeleton.Bones[wristBoneIndex].Transform;
        _elbow = bodySkeleton.Bones[elbowBoneIndex].Transform;
        return _wrist != null && _elbow != null;
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

    float ComputeExtent()
    {
        float m = 0f;
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                if (!_kept[r, c]) continue;
                float t = Vector3.Dot(_hits[r, c] - _wristPos, _axis);
                if (t > m) m = t;
            }
        return m;
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

    Vector2 UV(Vector3 p)
    {
        Vector3 fw = p - _wristPos;
        float along = Vector3.Dot(fw, _axis);
        float v = Mathf.Clamp01(along / Mathf.Max(_axisLength, 1e-3f));
        Vector3 axisPt = _wristPos + _axis * along;
        Vector3 rad = p - axisPt;
        float rm = rad.magnitude;
        if (rm < 1e-5f) return new Vector2(0.5f, v);
        rad /= rm;
        float a = Mathf.Atan2(Vector3.Dot(rad, _axisRight), Vector3.Dot(rad, _axisUp));
        return new Vector2((a + Mathf.PI) / (2f * Mathf.PI), v);
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