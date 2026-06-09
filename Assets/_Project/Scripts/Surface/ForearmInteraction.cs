using UnityEngine;
using Surface.Buffer;

/// <summary>
/// Detects finger contact with the forearm display surface and exposes the result as a UV
/// coordinate and world point each frame. Reads ForearmDepthSurface on the same GameObject for
/// surface data; owns only the interaction logic.
///
/// Per fingertip bone, per frame:
///   1. Pre-reject bones outside the display region's physical extents (fast, conservative).
///   2. Find the nearest surface cell in arm-frame (along, across) space — O(rows × cols).
///   3. Above/below test along AxisUp (the IndexTip joint sits ~5-10mm inside the finger, so
///      `above` reads slightly positive even on flat contact — see touchHoverHeight).
///   4. Compute the touch UV, mirroring MeshGenerator.CalculateUV so it addresses the same texture.
///   5. Among qualifying bones, pick the smallest `above` (most deliberate contact).
///
/// TouchWorldPoint is the bone projected onto the surface (pos - AxisUp*above), not the surface
/// cell: hand tracking is stereo-stable while the left-eye depth is noisier, so the projected bone
/// aligns better with the render layer.
/// </summary>
[RequireComponent(typeof(ForearmDepthSurface))]
public class ForearmInteraction : MonoBehaviour
{
    // ------------------------------------------------------------------
    // INSPECTOR PARAMETERS
    // ------------------------------------------------------------------
    [Header("Touch")]
    [Tooltip("How far above the surface a finger can hover and still register as a touch (m)")]
    [Range(0.005f, 0.05f)] public float touchHoverHeight = 0.005f;
    [Tooltip("How far through the surface a finger can press before being ignored (m)")]
    [Range(0.005f, 0.15f)] public float touchDepth = 0.04f;
    [Tooltip("Max 2D arm-frame distance to the nearest surface cell for a touch to register. " +
             "Must exceed the GPU mask radius (~1-2cm) since masked cells are excluded (m)")]
    [Range(0.005f, 0.15f)] public float maxCellSearchDist = 0.04f;

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

            // FAST PRE-REJECTION in physical arm-frame extents (pronation/orientation omitted — those
            // are UV adjustments, not physical). The U lower bound is -0.5, not 0, to cover the full
            // pronation scroll: at palm-up the palmar panel's left edge sits at u≈-0.25 pre-scroll, so
            // a 0 bound would drop valid touches there.
            float u = ((across - _surface.ProjCenter) / _surface.displayWidth) + 0.25f;
            float v = (along  - displayStart)          / (displayEnd - displayStart);
            if (u < -0.5f || u > 1f || v < 0f || v > 1f) continue;

            // Find the nearest reconstructed surface cell in arm-frame 2D space.
            if (!TryGetNearestSurfaceHit(along, across, out Vector3 surfaceHit)) continue;

            // Signed distance above the surface along AxisUp.
            // Positive = hovering, negative = pressed through. Both are valid touches.
            float above = Vector3.Dot(pos - surfaceHit, _surface.AxisUp);
            if (above < -touchDepth || above > touchHoverHeight) continue;

            // Project the finger onto the surface plane (remove the AxisUp component). AxisUp is
            // orthogonal to Axis/AxisRight, so along/across (and thus U/V) are unaffected. Using the
            // finger rather than surfaceHit gives sub-cell UV precision instead of snapping to a cell.
            Vector3 projectedPos = pos - _surface.AxisUp * above;
            Vector2 hitUV        = ComputeMeshUV(projectedPos);
            if (hitUV.x < 0f || hitUV.x > 1f || hitUV.y < 0f || hitUV.y > 1f) continue;

            // Among all qualifying vertices, prefer the one that is closest to or
            // furthest through the surface — the most deliberate contact.
            if (above < bestAbove)
            {
                bestAbove  = above;
                uv         = hitUV;
                worldPoint = projectedPos;
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
                    Mathf.Max(_surface.displayWidth, 1e-4f)) + 0.25f;

        u += _surface.PronationAngle / (2f * Mathf.PI);

        return new Vector2(u, v);
    }

    /// <summary>
    /// Returns the world-space hit of the surface cell whose (along, across) arm-frame projection is
    /// nearest the queried position, if within maxCellSearchDist.
    ///
    /// The distance cap is the critical correctness constraint: without it this always returns some
    /// cell, and the caller's above/below test only checks the AxisUp component — so a finger far
    /// from the surface could project near-zero against a distant cell and falsely register. Capping
    /// the 2D search ensures the found cell is actually beneath the finger.
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