using UnityEngine;
using System.Collections.Generic;
using Meta.XR;

/// <summary>
/// Camera-aligned forearm surface reconstruction via depth raycasting.
///
/// This script casts rays FROM the camera THROUGH a screen-space pixel grid.
/// Each ray maps naturally to the depth texture's native resolution,
/// so every pixel with valid depth produces a hit w/o gaps.
///
/// Hits are filtered by proximity to the body-tracking forearm axis
/// to isolate arm surface points from background geometry.
///
/// Setup:
///   1. Assign body tracking OVRSkeleton in Inspector
///   2. Assign EnvironmentRaycastManager in Inspector
///   3. Assign CenterEyeAnchor transform in Inspector
///   4. Ensure passthrough + scene support are enabled
/// </summary>
public class DepthRaycastTest : MonoBehaviour
{
    [Header("References")]
    public OVRSkeleton bodySkeleton;
    public EnvironmentRaycastManager raycastManager;
    public Transform centerEyeAnchor;

    [Header("Sampling Grid")]
    [Tooltip("Pixels between sample points in screen space")]
    [Range(2, 20)] public int pixelStride = 10;

    [Tooltip("Padding around projected forearm bounds (pixels)")]
    [Range(0, 60)] public int screenPadding = 30;

    [Header("Filtering")]
    [Tooltip("Max distance from forearm axis to accept a hit (meters)")]
    [Range(0.02f, 0.10f)] public float maxRadialDist = 0.08f;

    // Body tracking joint indices
    private const int JOINT_LEFT_ARM_LOWER = 11;
    private const int JOINT_LEFT_WRIST = 19;

    private Transform _elbow, _wrist;
    private bool _bonesResolved;

    // Mesh
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh;

    // Per-frame buffers
    private Vector3[,] _hitGrid;
    private bool[,] _validGrid;
    private int _gridRows, _gridCols;

    // Reusable lists for mesh building
    private List<Vector3> _verts = new List<Vector3>(2048);
    private List<int> _tris = new List<int>(4096);
    private List<Vector2> _uvs = new List<Vector2>(2048);

    void Start()
    {
        _mesh = new Mesh { name = "ForearmDepthMesh" };
        _mesh.MarkDynamic();

        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshFilter.mesh = _mesh;

        _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0f, 1f, 1f, 0.5f);
        mat.SetFloat("_Surface", 1); // transparent
        mat.SetFloat("_Blend", 0);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        _meshRenderer.material = mat;
    }

    void LateUpdate()
    {
        if (raycastManager == null || bodySkeleton == null
            || centerEyeAnchor == null) return;

        if (!ResolveBones()) return;

        Camera cam = centerEyeAnchor.GetComponent<Camera>();
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Vector3 elbowPos = _elbow.position;
        Vector3 wristPos = _wrist.position;
        Vector3 forearmAxis = (wristPos - elbowPos).normalized;
        float forearmLen = Vector3.Distance(elbowPos, wristPos);

        // Project forearm endpoints + midpoint to screen space
        // to find the bounding rectangle
        Rect screenBounds = GetForearmScreenBounds(
            cam, elbowPos, wristPos, forearmAxis, screenPadding);

        if (screenBounds.width < 10 || screenBounds.height < 10) return;

        // Determine grid dimensions from bounds and stride
        _gridCols = Mathf.Max(2, Mathf.CeilToInt(screenBounds.width / pixelStride));
        _gridRows = Mathf.Max(2, Mathf.CeilToInt(screenBounds.height / pixelStride));

        // Ensure buffers are large enough
        if (_hitGrid == null || _hitGrid.GetLength(0) != _gridRows
            || _hitGrid.GetLength(1) != _gridCols)
        {
            _hitGrid = new Vector3[_gridRows, _gridCols];
            _validGrid = new bool[_gridRows, _gridCols];
        }

        // Cast rays through screen-space grid
        int hits = 0, tested = 0;

        for (int row = 0; row < _gridRows; row++)
        {
            for (int col = 0; col < _gridCols; col++)
            {
                _validGrid[row, col] = false;

                // Screen-space position for this grid cell
                float sx = screenBounds.xMin + col * pixelStride;
                float sy = screenBounds.yMin + row * pixelStride;

                Ray ray = cam.ScreenPointToRay(new Vector3(sx, sy, 0));
                tested++;

                if (!raycastManager.Raycast(ray, out var hit))
                    continue;

                Vector3 hitPoint = hit.point;

                // Filter: is this hit close to the forearm axis?
                float radialDist = DistanceToLine(
                    hitPoint, elbowPos, wristPos, forearmAxis,
                    forearmLen, out float t);

                // t in [0,1] = between elbow and wrist
                if (t < -0.05f || t > 1.05f) continue;
                if (radialDist > maxRadialDist) continue;

                _hitGrid[row, col] = hitPoint;
                _validGrid[row, col] = true;
                hits++;
            }
        }

        BuildMesh(elbowPos, wristPos, forearmAxis, forearmLen);
    }

    /// <summary>
    /// Projects the forearm to screen space and returns a padded bounding rect.
    /// Samples several points along the axis + offset perpendiculars to get
    /// a conservative bound that covers the arm's visible width.
    /// </summary>
    Rect GetForearmScreenBounds(Camera cam, Vector3 elbow, Vector3 wrist,
        Vector3 axis, int padding)
    {
        // Build a perpendicular frame for width estimation
        Vector3 camFwd = cam.transform.forward;
        Vector3 sideDir = Vector3.Cross(axis, camFwd).normalized;
        if (sideDir.sqrMagnitude < 0.01f)
            sideDir = Vector3.Cross(axis, cam.transform.up).normalized;

        float estimatedRadius = maxRadialDist;

        // Sample 5 points along the axis + offsets
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < 5; i++)
        {
            float t = i / 4f;
            Vector3 axisPoint = Vector3.Lerp(elbow, wrist, t);

            // Center + left + right
            Vector3[] samples = {
                axisPoint,
                axisPoint + sideDir * estimatedRadius,
                axisPoint - sideDir * estimatedRadius
            };

            foreach (var pt in samples)
            {
                Vector3 sp = cam.WorldToScreenPoint(pt);
                if (sp.z <= 0) continue; // behind camera
                if (sp.x < minX) minX = sp.x;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.y > maxY) maxY = sp.y;
            }
        }

        // Apply padding and clamp to screen
        minX = Mathf.Max(0, minX - padding);
        minY = Mathf.Max(0, minY - padding);
        maxX = Mathf.Min(cam.pixelWidth, maxX + padding);
        maxY = Mathf.Min(cam.pixelHeight, maxY + padding);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Builds a quad mesh from the hit grid. Adjacent valid hits
    /// form quads; gaps are skipped.
    /// </summary>
    void BuildMesh(Vector3 elbow, Vector3 wrist, Vector3 axis, float armLen)
    {
        _verts.Clear();
        _tris.Clear();
        _uvs.Clear();

        // Map each valid grid cell to a vertex index (-1 = no vertex)
        int[,] vertIdx = new int[_gridRows, _gridCols];

        for (int r = 0; r < _gridRows; r++)
        {
            for (int c = 0; c < _gridCols; c++)
            {
                if (_validGrid[r, c])
                {
                    vertIdx[r, c] = _verts.Count;
                    _verts.Add(_hitGrid[r, c]);

                    // UV: project onto forearm axis for V, use grid col for U
                    float t = ProjectOntoAxis(
                        _hitGrid[r, c], elbow, axis, armLen);
                    float u = (float)c / (_gridCols - 1);
                    _uvs.Add(new Vector2(u, Mathf.Clamp01(t)));
                }
                else
                {
                    vertIdx[r, c] = -1;
                }
            }
        }

        // Build quads from adjacent valid cells
        for (int r = 0; r < _gridRows - 1; r++)
        {
            for (int c = 0; c < _gridCols - 1; c++)
            {
                int tl = vertIdx[r, c];
                int tr = vertIdx[r, c + 1];
                int bl = vertIdx[r + 1, c];
                int br = vertIdx[r + 1, c + 1];

                // Need all 4 corners for a quad
                if (tl < 0 || tr < 0 || bl < 0 || br < 0) continue;

                // Skip degenerate quads where hits are too far apart
                // (edge of arm where some hits are background)
                float maxEdge = MaxEdgeLength(
                    _verts[tl], _verts[tr], _verts[bl], _verts[br]);
                if (maxEdge > pixelStride * 0.003f) continue;

                // Two triangles per quad
                _tris.Add(tl); _tris.Add(bl); _tris.Add(tr);
                _tris.Add(tr); _tris.Add(bl); _tris.Add(br);
            }
        }

        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetUVs(0, _uvs);
        _mesh.SetTriangles(_tris, 0);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    // --- Utility methods ---

    bool ResolveBones()
    {
        if (_bonesResolved) return _elbow != null && _wrist != null;

        if (bodySkeleton.Bones == null
            || bodySkeleton.Bones.Count <= JOINT_LEFT_WRIST) return false;

        _elbow = bodySkeleton.Bones[JOINT_LEFT_ARM_LOWER].Transform;
        _wrist = bodySkeleton.Bones[JOINT_LEFT_WRIST].Transform;
        _bonesResolved = true;
        return _elbow != null && _wrist != null;
    }

    /// <summary>
    /// Shortest distance from point to the elbow-wrist line segment.
    /// Returns the parametric t along the segment (0=elbow, 1=wrist).
    /// </summary>
    static float DistanceToLine(Vector3 point, Vector3 lineStart,
        Vector3 lineEnd, Vector3 lineDir, float lineLen, out float t)
    {
        Vector3 toPoint = point - lineStart;
        t = Vector3.Dot(toPoint, lineDir) / lineLen;
        Vector3 closestOnLine = lineStart + lineDir * (t * lineLen);
        return Vector3.Distance(point, closestOnLine);
    }

    static float ProjectOntoAxis(Vector3 point, Vector3 start,
        Vector3 dir, float len)
    {
        return Vector3.Dot(point - start, dir) / len;
    }

    static float MaxEdgeLength(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float e1 = (b - a).sqrMagnitude;
        float e2 = (c - a).sqrMagnitude;
        float e3 = (d - b).sqrMagnitude;
        float e4 = (d - c).sqrMagnitude;
        return Mathf.Sqrt(Mathf.Max(e1, Mathf.Max(e2, Mathf.Max(e3, e4))));
    }
}