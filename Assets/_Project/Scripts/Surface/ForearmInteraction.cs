using UnityEngine;
using Surface.Buffer;

/// <summary>
/// Detects finger contact with the forearm display surface and exposes the result
/// as a UV coordinate and world point each frame.
///
/// TOUCH DETECTION PIPELINE (per hand vertex, per frame)
///   1. Physical pre-rejection: project the vertex onto the arm coordinate frame and
///      check if it falls within the display region's physical extents. This is a fast,
///      approximate filter — it does not apply ProjCenter or UV corrections, so it is
///      conservative (no false negatives for typical arm positions).
///   2. Nearest surface cell: scan the reconstructed SurfaceBuffer to find the cell
///      whose (along, across) arm-frame position is closest to the hand vertex.
///      O(rows × cols), acceptable at typical grid sizes (~2,000–4,000 cells).
///   3. Above/below test: compute how far the vertex is above or below the nearest
///      surface cell using AxisUp. Accept vertices within [-touchDepth, +touchHoverHeight].
///   4. UV computation: mirror MeshGenerator.VertexJob.CalculateUV exactly so the touch
///      UV addresses the same texture region as the rendered mesh vertices.
///   5. Best-candidate selection: among qualifying vertices, pick the one with the
///      smallest `above` value — closest to or farthest into the surface, which
///      corresponds to the most deliberate contact or deepest press.
///
/// WORLD POINT DERIVATION
/// The returned TouchWorldPoint is pos - AxisUp * above (the hand vertex projected onto
/// the surface plane) rather than the raw surfaceHit from depth reconstruction.
/// Hand tracking is stereo-stable; the depth reconstruction is left-eye-only and carries
/// more noise. Projecting the hand position is more accurate for render-layer alignment.
///
/// Reads ForearmDepthSurface on the same GameObject for surface data. That component
/// owns all reconstruction; this component owns only the interaction logic.
/// </summary>
[RequireComponent(typeof(ForearmDepthSurface))]
public class ForearmInteraction : MonoBehaviour
{
    // ------------------------------------------------------------------
    // INSPECTOR PARAMETERS
    // ------------------------------------------------------------------
    [Header("Touch")]
    // How far above the reconstructed surface a finger can hover and still register.
    // Allows visual feedback (e.g. highlight) before physical contact.
    // Too large -> accidental activations when the hand is near but not touching.
    [Tooltip("How far above the surface a finger can hover and still register as a touch (m)")]
    [Range(0.005f, 0.05f)] public float touchHoverHeight = 0.02f;
    // How far the finger can press through the surface before being rejected.
    // The depth reconstruction represents skin surface; fingertip contact means the
    // vertex lands at or slightly below it. Too large -> false touches through the arm.
    [Tooltip("How far through the surface a finger can press before being ignored (m)")]
    [Range(0.005f, 0.15f)] public float touchDepth = 0.04f;
    // Maximum 2D arm-frame distance (along × across) from a hand vertex to the nearest
    // surface cell for a touch to be considered valid. Roughly 2× touchHoverHeight so the
    // lateral search radius matches the physical scale of intentional contact.
    [Tooltip("Max 2D arm-frame distance to the nearest surface cell for a touch to register. " +
             "Prevents false touches when no surface cell exists beneath the finger (m)")]
    [Range(0.005f, 0.05f)] public float maxCellSearchDist = 0.04f;

    [Header("Debug")]
    [Tooltip("Draw a green circle on the surface material at the active touch UV")]
    public bool showTouchDebug = true;

    // ------------------------------------------------------------------
    // PER-FRAME CACHED RESULT
    // Updated every LateUpdate; external consumers read these without
    // triggering recomputation. TryGetTouchPoint() is also public for
    // callers that need the result outside of LateUpdate order.
    // ------------------------------------------------------------------
    public bool    IsActive        { get; private set; }
    public Vector2 TouchUV         { get; private set; }
    public Vector3 TouchWorldPoint { get; private set; }

    private ForearmDepthSurface _surface;

    /// <summary>
    /// Caches the ForearmDepthSurface reference on the same GameObject.
    /// </summary>
    void Awake()
    {
        _surface = GetComponent<ForearmDepthSurface>();
    }

    /// <summary>
    /// Runs touch detection and updates the cached per-frame result.
    /// Runs in LateUpdate so it reads hand vertices and surface data that were
    /// both finalized after animation (SnapshotMesh and OnDepthReady both run
    /// in LateUpdate and the callback fires on the main thread before this).
    /// </summary>
    void LateUpdate()
    {
        IsActive        = TryGetTouchPoint(out Vector2 uv, out Vector3 wp);
        TouchUV         = uv;
        TouchWorldPoint = wp;

        // Always update the shader property so that toggling showTouchDebug off
        // immediately clears the debug circle rather than leaving a stale one.
        Material mat = _surface.SurfaceMat;
        if (mat != null && mat.HasProperty("_TouchPoint"))
            mat.SetVector("_TouchPoint", IsActive && showTouchDebug
                ? new Vector4(uv.x, uv.y, 1f, 0f)
                : new Vector4(0f, 0f, 0f, 0f));
    }

    /// <summary>
    /// Finds the hand vertex closest to the arm surface within the display region and
    /// returns its UV coordinate on the rendered texture and its world-space position
    /// projected onto the surface. Returns false when no qualifying vertex exists.
    /// Safe to call outside LateUpdate; result is not cached.
    /// </summary>
    public bool TryGetTouchPoint(out Vector2 uv, out Vector3 worldPoint)
    {
        uv         = Vector2.zero;
        worldPoint = Vector3.zero;

        if (!_surface.IsValid || !_surface.HasHandVertices) return false;

        // Physical extents of the display window along the arm axis.
        float displayStart = _surface.displayOffset - _surface.displayHeight * 0.5f;
        float displayEnd   = _surface.displayOffset + _surface.displayHeight * 0.5f;

        float bestAbove = float.MaxValue;
        bool  found     = false;

        for (int i = 0; i < _surface.HandVertexCount; i++)
        {
            Vector3 pos   = _surface.HandVertices[i];
            Vector3 toPos = pos - _surface.WristPosition;

            // Project onto the arm coordinate frame.
            float along  = Vector3.Dot(toPos, _surface.AxisDir);
            float across = Vector3.Dot(toPos, _surface.AxisRight);

            // FAST PRE-REJECTION using physical arm-frame extents.
            // Matches the U centering of ComputeMeshUV (ProjCenter applied) so there is no
            // dead zone at the display edge when the visible arm patch is offset from the
            // wrist axis origin. Pronation and orientation rotation are intentionally omitted
            // — they are UV mapping adjustments, not physical position changes.
            float u = ((across - _surface.ProjCenter) / _surface.displayWidth) + 0.5f;
            float v = (along  - displayStart)          / (displayEnd - displayStart);
            if (u < 0f || u > 1f || v < 0f || v > 1f) continue;

            // Find the nearest reconstructed surface cell in arm-frame 2D space.
            if (!TryGetNearestSurfaceHit(along, across, out Vector3 surfaceHit)) continue;

            // Signed distance above the surface along AxisUp.
            // Positive = hovering, negative = pressed through. Both are valid touches.
            float above = Vector3.Dot(pos - surfaceHit, _surface.AxisUp);
            if (above < -touchDepth || above > touchHoverHeight) continue;

            // Full UV computation matching the rendered mesh — only run when the vertex
            // passed the cheaper physical tests above.
            Vector2 hitUV = ComputeMeshUV(surfaceHit);
            if (hitUV.x < 0f || hitUV.x > 1f || hitUV.y < 0f || hitUV.y > 1f) continue;

            // Among all qualifying vertices, prefer the one that is closest to or
            // furthest through the surface — the most deliberate contact.
            if (above < bestAbove)
            {
                bestAbove = above;
                uv        = hitUV;
                // Project the hand-tracked vertex onto the surface plane by removing
                // its AxisUp component. Hand tracking is stereo-stable and more accurate
                // than the left-eye-only depth reconstruction in surfaceHit.
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
    /// the same texture region as the rendered mesh vertices. Any divergence between
    /// the two formulas would offset touch coordinates from the visual content.
    /// </summary>
    private Vector2 ComputeMeshUV(Vector3 pt)
    {
        Vector3 fromWrist = pt - _surface.WristPosition;

        // V: linear projection along the arm axis, normalized by display height and flipped.
        float distAlong = Vector3.Dot(fromWrist, _surface.AxisDir);
        float v = 1f - (((distAlong - _surface.displayOffset) /
                          Mathf.Max(_surface.displayHeight, 1e-4f)) + 0.5f);

        // U: linear projection along the camera-fixed lateral axis, centered on ProjCenter.
        float projR = Vector3.Dot(fromWrist, _surface.AxisRight);
        float u = ((projR - _surface.ProjCenter) /
                    Mathf.Max(_surface.displayWidth, 1e-4f)) + 0.5f;

        // Pronation scroll and landscape seam offset.
        bool isLandscape = Mathf.Abs(_surface.OrientationAngle) > Mathf.PI * 0.25f;
        u += _surface.PronationAngle / Mathf.PI + (isLandscape ? _surface.LandscapeUOffset : 0f);

        // 2D rotation around (0.5, 0.5) for portrait/landscape orientation.
        float cu = u - 0.5f, cv = v - 0.5f;
        float cosA = Mathf.Cos(_surface.OrientationAngle);
        float sinA = Mathf.Sin(_surface.OrientationAngle);
        return new Vector2(cu * cosA - cv * sinA + 0.5f, cu * sinA + cv * cosA + 0.5f);
    }

    /// <summary>
    /// Scans all surface cells and returns the world-space hit of the cell whose
    /// (along, across) arm-frame projection is nearest to the queried position,
    /// provided that nearest cell is within maxCellSearchDist.
    ///
    /// The distance cap is the critical correctness constraint: without it, this
    /// function always succeeds (returning some cell on the arm), and the above/below
    /// check in the caller only validates the AxisUp component of the displacement.
    /// When the arm rotates, AxisUp changes direction so a hand vertex that is far
    /// from the surface in 3D can still have a near-zero AxisUp projection against a
    /// distant cell and falsely pass as a touch. Capping the 2D search ensures the
    /// found cell is actually beneath the finger, not just the nearest cell on the arm.
    /// </summary>
    private bool TryGetNearestSurfaceHit(float along, float across, out Vector3 hit)
    {
        hit = Vector3.zero;

        int rows = _surface.SurfaceRows;
        int cols = _surface.SurfaceCols;
        if (rows == 0 || cols == 0) return false;

        SurfaceBuffer buf       = _surface.SurfaceBuf;
        int           total     = rows * cols;
        float         bestSq    = maxCellSearchDist * maxCellSearchDist;
        bool          found     = false;

        for (int i = 0; i < total; i++)
        {
            if (!buf.IsSurface[i]) continue;

            Vector3 toHit   = buf.Hits[i] - _surface.WristPosition;
            // 2D distance in arm-frame space (along, across) — ignores depth so
            // the search finds the surface cell directly beneath the finger.
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
