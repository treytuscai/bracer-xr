using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Semi-transparent polygon fill drawn on the forearm UI canvas (experiment1A only).
/// Supports a dynamic mode where the polygon tracks live dot positions each frame
/// and automatically destroys itself if any dot is deleted.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class Experiment1APolygonFillGraphic : MaskableGraphic
{
    readonly List<Vector2> _points = new List<Vector2>();

    // Dynamic tracking --------------------------------------------------------
    List<Experiment1ADotMarker> _trackedDots;
    RectTransform _coordRoot;

    /// <summary>
    /// Bind this fill to living dot references so it auto-updates as dots move
    /// and destroys itself if any dot is deleted.
    /// </summary>
    public void InitializeDynamic(IReadOnlyList<Experiment1ADotMarker> dots, RectTransform coordRoot)
    {
        _trackedDots = new List<Experiment1ADotMarker>(dots);
        _coordRoot   = coordRoot;
        RefreshFromDots();
    }

    void LateUpdate()
    {
        if (_trackedDots == null || _coordRoot == null) return;

        // If any tracked dot has been destroyed, remove this fill.
        for (int i = 0; i < _trackedDots.Count; i++)
        {
            if (_trackedDots[i] == null)
            {
                Destroy(gameObject);
                return;
            }
        }
        RefreshFromDots();
    }

    void RefreshFromDots()
    {
        if (_trackedDots == null || _coordRoot == null) return;

        var pts = new List<Vector2>(_trackedDots.Count);
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < _trackedDots.Count; i++)
        {
            Vector2 p = _coordRoot.InverseTransformPoint(_trackedDots[i].transform.position);
            pts.Add(p);
            centroid += p;
        }
        centroid /= _trackedDots.Count;

        // Sort points around centroid so the polygon is convex-friendly.
        pts.Sort((a, b) =>
        {
            float angleA = Mathf.Atan2(a.y - centroid.y, a.x - centroid.x);
            float angleB = Mathf.Atan2(b.y - centroid.y, b.x - centroid.x);
            return angleA.CompareTo(angleB);
        });

        SetPolygon(pts);
    }
    // -------------------------------------------------------------------------

    public void SetPolygon(IReadOnlyList<Vector2> canvasLocalPoints)
    {
        _points.Clear();
        if (canvasLocalPoints != null)
            _points.AddRange(canvasLocalPoints);
        SetAllDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_points.Count < 3)
            return;

        var indices = PolygonTriangulator.Triangulate(_points);
        if (indices.Count < 3)
            return;

        var vert = UIVertex.simpleVert;
        vert.color = color;

        int baseIndex = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            Vector2 p = _points[indices[i]];
            vert.position = new Vector3(p.x, p.y, 0f);
            vh.AddVert(vert);
        }

        for (int i = 0; i + 2 < indices.Count; i += 3)
            vh.AddTriangle(i, i + 1, i + 2);
    }
}

static class PolygonTriangulator
{
    public static List<int> Triangulate(IReadOnlyList<Vector2> vertices)
    {
        var result = new List<int>();
        if (vertices == null || vertices.Count < 3)
            return result;

        var remaining = new List<int>(vertices.Count);
        for (int i = 0; i < vertices.Count; i++)
            remaining.Add(i);

        int guard = 0;
        while (remaining.Count > 3 && guard++ < 10000)
        {
            bool earRemoved = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int iPrev = remaining[(i + remaining.Count - 1) % remaining.Count];
                int iCurr = remaining[i];
                int iNext = remaining[(i + 1) % remaining.Count];

                if (!IsEar(vertices, remaining, iPrev, iCurr, iNext))
                    continue;

                result.Add(iPrev);
                result.Add(iCurr);
                result.Add(iNext);
                remaining.RemoveAt(i);
                earRemoved = true;
                break;
            }

            if (!earRemoved)
                break;
        }

        if (remaining.Count == 3)
        {
            result.Add(remaining[0]);
            result.Add(remaining[1]);
            result.Add(remaining[2]);
        }

        return result;
    }

    static bool IsEar(IReadOnlyList<Vector2> vertices, List<int> remaining, int iPrev, int iCurr, int iNext)
    {
        Vector2 a = vertices[iPrev];
        Vector2 b = vertices[iCurr];
        Vector2 c = vertices[iNext];

        if (Cross(b - a, c - b) <= 0f)
            return false;

        for (int i = 0; i < remaining.Count; i++)
        {
            int idx = remaining[i];
            if (idx == iPrev || idx == iCurr || idx == iNext)
                continue;

            if (PointInTriangle(vertices[idx], a, b, c))
                return false;
        }

        return true;
    }

    static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
