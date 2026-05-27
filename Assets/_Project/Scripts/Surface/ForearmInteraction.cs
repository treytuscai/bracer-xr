using UnityEngine;
using Surface.Buffer;

/// <summary>
/// Detects finger touches on the forearm display surface and exposes the result as a
/// UV coordinate and world point each frame. Requires a ForearmDepthSurface on the
/// same GameObject, which handles all surface reconstruction; this component only
/// owns the interaction logic.
///
/// Per-frame result is cached so callers read IsActive / TouchUV / TouchWorldPoint
/// without triggering recomputation. TryGetTouchPoint() is also public for callers
/// that need the result outside of LateUpdate order.
/// </summary>
[RequireComponent(typeof(ForearmDepthSurface))]
public class ForearmInteraction : MonoBehaviour
{
    [Header("Touch")]
    [Tooltip("Symmetric window around the surface: fingers within this distance above OR " +
             "pressed through count as touching (m)")]
    [Range(0.005f, 0.05f)] public float touchDetectHeight = 0.035f;

    [Header("Debug")]
    [Tooltip("Draw a green circle on the surface material at the active touch UV")]
    public bool showTouchDebug = true;

    // Cached per-frame — read by external consumers
    public bool    IsActive        { get; private set; }
    public Vector2 TouchUV         { get; private set; }
    public Vector3 TouchWorldPoint { get; private set; }

    private ForearmDepthSurface _surface;

    void Awake()
    {
        _surface = GetComponent<ForearmDepthSurface>();
    }

    void LateUpdate()
    {
        IsActive = TryGetTouchPoint(out Vector2 uv, out Vector3 wp);
        TouchUV         = uv;
        TouchWorldPoint = wp;

        if (showTouchDebug)
        {
            Material mat = _surface.SurfaceMat;
            if (mat != null && mat.HasProperty("_TouchPoint"))
                mat.SetVector("_TouchPoint", IsActive
                    ? new Vector4(uv.x, uv.y, 1f, 0f)
                    : new Vector4(0f, 0f, 0f, 0f));
        }
    }

    /// <summary>
    /// Finds the hand vertex closest to the arm surface within the display region and
    /// returns its UV coordinate on the rendered texture and its world-space position
    /// projected onto the surface. Returns false when no qualifying vertex exists.
    /// </summary>
    public bool TryGetTouchPoint(out Vector2 uv, out Vector3 worldPoint)
    {
        uv         = Vector2.zero;
        worldPoint = Vector3.zero;

        if (!_surface.IsValid || !_surface.HasHandVertices) return false;

        float displayStart = _surface.displayOffset - _surface.displayHeight * 0.5f;
        float displayEnd   = _surface.displayOffset + _surface.displayHeight * 0.5f;
        float touchThresh  = touchDetectHeight;
        float bestAbove    = float.MaxValue;
        bool  found        = false;

        for (int i = 0; i < _surface.HandVertexCount; i++)
        {
            Vector3 pos   = _surface.HandVertices[i];
            Vector3 toPos = pos - _surface.WristPosition;

            float along  = Vector3.Dot(toPos, _surface.AxisDir);
            float across = Vector3.Dot(toPos, _surface.AxisRight);

            // Cheap pre-rejection: skip vertices clearly outside the display region
            float u = (across + _surface.displayWidth  * 0.5f) / _surface.displayWidth;
            float v = (along  - displayStart)                   / (displayEnd - displayStart);
            if (u < 0f || u > 1f || v < 0f || v > 1f) continue;

            if (!TryGetNearestSurfaceHit(along, across, out Vector3 surfaceHit)) continue;

            // Positive = above surface, negative = pressed through. Both are valid touches.
            float above = Vector3.Dot(pos - surfaceHit, _surface.AxisUp);
            if (above < -touchThresh || above > touchThresh) continue;

            // UV in the same cylindrical space as the rendered mesh (pronation, orientation,
            // ProjCenter all applied) so the coordinate correctly addresses the texture.
            Vector2 hitUV = ComputeMeshUV(surfaceHit);
            if (hitUV.x < 0f || hitUV.x > 1f || hitUV.y < 0f || hitUV.y > 1f) continue;

            if (above < bestAbove)
            {
                bestAbove  = above;
                uv         = hitUV;
                // Derive world point from hand tracking (stereo-stable) rather than from
                // the left-eye-only depth reconstruction used by surfaceHit.
                worldPoint = pos - _surface.AxisUp * above;
                found      = true;
            }
        }

        return found;
    }

    // -----------------------------------------------------------------------
    // HELPERS
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mirrors MeshGenerator.VertexJob.CalculateUV exactly so touch UVs address
    /// the same texture region as the rendered mesh vertices.
    /// </summary>
    private Vector2 ComputeMeshUV(Vector3 pt)
    {
        Vector3 fromWrist = pt - _surface.WristPosition;
        bool isLandscape  = Mathf.Abs(_surface.OrientationAngle) > Mathf.PI * 0.25f;

        float distAlong = Vector3.Dot(fromWrist, _surface.AxisDir);
        float v = 1f - (((distAlong - _surface.displayOffset) /
                          Mathf.Max(isLandscape ? _surface.displayWidth : _surface.displayHeight, 1e-4f)) + 0.5f);

        float projR = Vector3.Dot(fromWrist, _surface.AxisRight);
        float u = ((projR - _surface.ProjCenter) /
                    Mathf.Max(isLandscape ? _surface.displayHeight : _surface.displayWidth, 1e-4f)) + 0.5f;

        u += (_surface.PronationAngle + (isLandscape ? Mathf.PI * 0.75f : 0f)) / Mathf.PI;

        float cu = u - 0.5f, cv = v - 0.5f;
        float cosA = Mathf.Cos(_surface.OrientationAngle);
        float sinA = Mathf.Sin(_surface.OrientationAngle);
        return new Vector2(cu * cosA - cv * sinA + 0.5f, cu * sinA + cv * cosA + 0.5f);
    }

    /// <summary>
    /// Finds the reconstructed surface cell nearest to the given (along, across) position
    /// in the arm coordinate frame. O(rows × cols) — acceptable given typical grid sizes
    /// and that only display-region vertices reach this call.
    /// </summary>
    private bool TryGetNearestSurfaceHit(float along, float across, out Vector3 hit)
    {
        hit = Vector3.zero;

        int rows = _surface.SurfaceRows;
        int cols = _surface.SurfaceCols;
        if (rows == 0 || cols == 0) return false;

        SurfaceBuffer buf    = _surface.SurfaceBuf;
        int           total  = rows * cols;
        float         bestSq = float.MaxValue;
        bool          found  = false;

        for (int i = 0; i < total; i++)
        {
            if (!buf.IsSurface[i]) continue;

            Vector3 toHit   = buf.Hits[i] - _surface.WristPosition;
            float   dAlong  = Vector3.Dot(toHit, _surface.AxisDir)   - along;
            float   dAcross = Vector3.Dot(toHit, _surface.AxisRight) - across;
            float   dSq     = dAlong * dAlong + dAcross * dAcross;

            if (dSq < bestSq)
            {
                bestSq = dSq;
                hit    = buf.Hits[i];
                found  = true;
            }
        }

        return found;
    }
}
