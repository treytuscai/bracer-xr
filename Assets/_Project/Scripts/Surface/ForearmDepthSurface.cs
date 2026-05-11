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
    [Range(20, 200)] public int samplePadding = 60;
    [Range(0.20f, 0.50f)] public float maxAxisLength = 0.40f;

    [Header("Seed (wrist→elbow cylinder filter)")]
    [Range(0.02f, 0.12f)] public float maxRadialDist = 0.07f;
    [Range(-0.05f, 0f)] public float minAxisT = -0.02f;

    [Header("Flood (depth connectivity expansion)")]
    [Tooltip("Max 3D step between adjacent grid hits to count as connected (m). " +
             "This is what lets the flood expand off the wrong-angle seed line " +
             "onto the actual forearm surface.")]
    [Range(0.005f, 0.05f)] public float connectivityThreshold = 0.025f;

    [Header("Mesh")]
    [Tooltip("Drop quads whose longest edge exceeds this (m). Should be ≥ connectivityThreshold " +
             "or quads across the arm width (where curvature creates 3D step) get dropped.")]
    [Range(0.005f, 0.06f)] public float maxQuadEdge = 0.030f;
    [Range(0f, 0.01f)] public float skinOffset = 0.002f;

    [Header("Debug")]
    public bool drawAxis = true;

    Transform _wrist, _elbow; Camera _cam;
    MeshFilter _mf; MeshRenderer _mr; Mesh _mesh; LineRenderer _line;

    Vector3[,] _hits;
    bool[,] _hasDepth; // raw: did we get a depth hit here?
    bool[,] _kept;      // pipeline output: does this cell go into the mesh?
    int _rows, _cols;

    readonly List<Vector3> _verts = new List<Vector3>(2048);
    readonly List<int> _tris = new List<int>(4096);
    readonly List<Vector2> _uvs = new List<Vector2>(2048);
    readonly Queue<int> _bfs = new Queue<int>(2048);

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
        SeedFromAxisLine(_wristPos, _axis, weLen);

        // Stage 2: flood from seeds via depth connectivity.
        // This is what catches forearm cells the wrong-angle line misses.
        FloodFromSeeds(_wristPos);

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

    bool Sample(Vector3 a, Vector3 b)
    {
        Vector3 sa = _cam.WorldToScreenPoint(a);
        if (sa.z <= 0f) return false;
        Vector3 sb = _cam.WorldToScreenPoint(b);
        Vector2 sa2 = new Vector2(sa.x, sa.y);
        Vector2 sb2 = (sb.z > 0f) ? new Vector2(sb.x, sb.y) : sa2;

        float xMin = Mathf.Max(0, Mathf.Min(sa2.x, sb2.x) - samplePadding);
        float xMax = Mathf.Min(_cam.pixelWidth, Mathf.Max(sa2.x, sb2.x) + samplePadding);
        float yMin = Mathf.Max(0, Mathf.Min(sa2.y, sb2.y) - samplePadding);
        float yMax = Mathf.Min(_cam.pixelHeight, Mathf.Max(sa2.y, sb2.y) + samplePadding);
        if (xMax - xMin < pixelStride || yMax - yMin < pixelStride) return false;

        _cols = Mathf.Max(2, Mathf.CeilToInt((xMax - xMin) / pixelStride));
        _rows = Mathf.Max(2, Mathf.CeilToInt((yMax - yMin) / pixelStride));

        if (_hits == null || _hits.GetLength(0) != _rows || _hits.GetLength(1) != _cols)
        {
            _hits = new Vector3[_rows, _cols];
            _hasDepth = new bool[_rows, _cols];
            _kept = new bool[_rows, _cols];
        }

        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                _hasDepth[r, c] = false;
                _kept[r, c] = false;
                Ray ray = _cam.ScreenPointToRay(
                    new Vector3(xMin + c * pixelStride, yMin + r * pixelStride, 0));
                if (raycastManager.Raycast(ray, out var hit))
                {
                    _hits[r, c] = hit.point;
                    _hasDepth[r, c] = true;
                }
            }
        return true;
    }

    /// <summary>
    /// Seed: mark cells that fall inside the cylinder around the wrist→axis line.
    /// Same logic as v1; output is a stable multi-cell seed for flood.
    /// </summary>
    void SeedFromAxisLine(Vector3 wristPos, Vector3 axis, float farLen)
    {
        float maxRSq = maxRadialDist * maxRadialDist;
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                if (!_hasDepth[r, c]) continue;
                Vector3 p = _hits[r, c];
                float along = Vector3.Dot(p - wristPos, axis);
                if (along < minAxisT || along > farLen) continue;
                Vector3 axisPt = wristPos + axis * along;
                if ((p - axisPt).sqrMagnitude <= maxRSq) _kept[r, c] = true;
            }
    }

    /// <summary>
    /// Flood: BFS from every seed cell. Add neighbors that have valid depth,
    /// aren't already kept, and whose 3D hit is within connectivityThreshold
    /// of their parent's hit (= depth-connected skin) AND within maxAxisLength
    /// of the wrist (cap). Walks across the actual forearm surface, even where
    /// the seed line missed it because the IOBT angle was off.
    /// </summary>
    void FloodFromSeeds(Vector3 wristPos)
    {
        _bfs.Clear();
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                if (_kept[r, c]) _bfs.Enqueue(r * _cols + c);

        if (_bfs.Count == 0) return;

        float connSq = connectivityThreshold * connectivityThreshold;
        float maxSq = maxAxisLength * maxAxisLength;

        // 8 connected
        int[] dr = { -1, -1, -1, 0, 0, 1, 1, 1 };
        int[] dc = { -1,  0,  1,-1, 1,-1, 0, 1 };

        while (_bfs.Count > 0)
        {
            int idx = _bfs.Dequeue();
            int r = idx / _cols, c = idx % _cols;
            Vector3 here = _hits[r, c];

            for (int n = 0; n < 4; n++)
            {
                int nr = r + dr[n], nc = c + dc[n];
                if (nr < 0 || nc < 0 || nr >= _rows || nc >= _cols) continue;
                if (_kept[nr, nc] || !_hasDepth[nr, nc]) continue;

                Vector3 there = _hits[nr, nc];
                if ((there - here).sqrMagnitude > connSq) continue;
                if ((there - wristPos).sqrMagnitude > maxSq) continue;

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

    void BuildMesh()
    {
        _verts.Clear(); _tris.Clear(); _uvs.Clear();
        Vector3 camPos = _cam.transform.position;

        int[,] idx = new int[_rows, _cols];
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                if (!_kept[r, c]) { idx[r, c] = -1; continue; }
                Vector3 p = _hits[r, c];
                if (skinOffset > 0f) p += (camPos - p).normalized * skinOffset;
                idx[r, c] = _verts.Count;
                _verts.Add(transform.InverseTransformPoint(p));
                _uvs.Add(UV(_hits[r, c]));
            }

        float maxSq = maxQuadEdge * maxQuadEdge;
        for (int r = 0; r < _rows - 1; r++)
            for (int c = 0; c < _cols - 1; c++)
            {
                int tl = idx[r, c], tr = idx[r, c+1], bl = idx[r+1, c], br = idx[r+1, c+1];
                int n = (tl>=0?1:0) + (tr>=0?1:0) + (bl>=0?1:0) + (br>=0?1:0);
                if (n < 3) continue;
                if (n == 4)
                {
                    if (!EdgesOK(_hits[r,c], _hits[r,c+1], _hits[r+1,c], _hits[r+1,c+1], maxSq)) continue;
                    _tris.Add(tl); _tris.Add(bl); _tris.Add(tr);
                    _tris.Add(tr); _tris.Add(bl); _tris.Add(br);
                }
                else if (tl < 0) { _tris.Add(tr); _tris.Add(bl); _tris.Add(br); }
                else if (tr < 0) { _tris.Add(tl); _tris.Add(bl); _tris.Add(br); }
                else if (bl < 0) { _tris.Add(tl); _tris.Add(br); _tris.Add(tr); }
                else             { _tris.Add(tl); _tris.Add(bl); _tris.Add(tr); }
            }

        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetUVs(0, _uvs);
        _mesh.SetTriangles(_tris, 0);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    static bool EdgesOK(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float maxSq) =>
        (a-b).sqrMagnitude < maxSq && (a-c).sqrMagnitude < maxSq
        && (c-d).sqrMagnitude < maxSq && (b-d).sqrMagnitude < maxSq;

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