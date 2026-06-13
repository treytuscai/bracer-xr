// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using UnityEngine;
using Surface.Buffer;
using Surface.Core;

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
///   4. Compute the touch UV via the shared SurfaceUV mapping so it addresses the same texture
///      region as the rendered mesh vertices.
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
    [Range(0.005f, 0.05f)] public float touchHoverHeight = 0.008f;
    [Tooltip("How far through the surface a finger can press before being ignored (m)")]
    [Range(0.005f, 0.15f)] public float touchDepth = 0.04f;
    [Tooltip("Max 2D arm-frame distance to the nearest surface cell for a touch to register. " +
             "Must exceed the hand-silhouette hole radius (~1cm) since cells under the hand are excluded (m)")]
    [Range(0.005f, 0.15f)] public float maxCellSearchDist = 0.04f;
    [Tooltip("Max 3D distance (m) from a world point to the surface for hover/placement preview.")]
    [Min(0.01f)] public float maxHoverPreviewDistance = 0.1f;

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

    private static readonly int TouchPointID = Shader.PropertyToID("_TouchPoint");

    /// <summary>
    /// Caches the ForearmDepthSurface reference on the same GameObject.
    /// </summary>
    void Awake()
    {
        _surface = GetComponent<ForearmDepthSurface>();
    }

    /// <summary>
    /// Runs touch detection and updates the cached per-frame result.
    /// ForearmDepthSurface has a lower execution order, so its LateUpdate
    /// (UpdateFingertips + the readback harvest) finishes before this one.
    /// </summary>
    void LateUpdate()
    {
        IsActive        = TryGetTouchPoint(out Vector2 uv, out Vector3 wp);
        TouchUV         = uv;
        TouchWorldPoint = wp;

        // Always update the shader property so that toggling showTouchDebug off
        // immediately clears the debug circle rather than leaving a stale one.
        Material mat = _surface.SurfaceMat;
        if (mat != null && mat.HasProperty(TouchPointID))
            mat.SetVector(TouchPointID, IsActive && showTouchDebug
                ? new Vector4(uv.x, uv.y, 1f, 0f)
                : new Vector4(0f, 0f, 0f, 0f));
    }

    /// <summary>
    /// Finds the fingertip closest to the arm surface within the display region and
    /// returns its UV coordinate on the rendered texture and its world-space position
    /// projected onto the surface. Returns false when no qualifying fingertip exists.
    /// Safe to call outside LateUpdate; serves the last cached result while the surface
    /// buffers are mid-update on worker threads.
    /// </summary>
    public bool TryGetTouchPoint(out Vector2 uv, out Vector3 worldPoint)
    {
        // Worker jobs are writing Hits/IsSurface between the dispatch callback and the harvest;
        // reading them mid-write would race. Serve the last stable result for that ~1-frame window.
        if (!_surface.SurfaceStable)
        {
            uv         = TouchUV;
            worldPoint = TouchWorldPoint;
            return IsActive;
        }

        uv         = Vector2.zero;
        worldPoint = Vector3.zero;

        if (!_surface.IsValid || !_surface.HasFingertips) return false;

        // Physical extents of the display window along the arm axis.
        float displayStart = _surface.displayOffset - _surface.displayHeight * 0.5f;
        float displayEnd   = _surface.displayOffset + _surface.displayHeight * 0.5f;

        float bestAbove = float.MaxValue;
        bool  found     = false;

        for (int i = 0; i < _surface.FingertipCount; i++)
        {
            Vector3 pos   = _surface.Fingertips[i];
            Vector3 toPos = pos - _surface.WristPosition;

            // Project onto the arm coordinate frame.
            float along  = Vector3.Dot(toPos, _surface.AxisDir);
            float across = Vector3.Dot(toPos, _surface.AxisRight);

            // FAST PRE-REJECTION in physical arm-frame extents (pronation/orientation omitted — those
            // are UV adjustments, not physical). The U lower bound is -0.5, not 0, to cover the full
            // pronation scroll: at palm-up the palmar panel's left edge sits at u≈-0.25 pre-scroll, so
            // a 0 bound would drop valid touches there.
            float u = ((across - _surface.ProjCenter) / Mathf.Max(_surface.displayWidth, 1e-4f)) + 0.25f;
            float v = (along  - displayStart)          / Mathf.Max(displayEnd - displayStart, 1e-4f);
            if (u < -0.5f || u > 1f || v < 0f || v > 1f) continue;

            // Find the nearest reconstructed surface cell in arm-frame 2D space.
            if (!TryGetNearestSurfaceHit(along, across, maxCellSearchDist, out Vector3 surfaceHit)) continue;

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

            // Among all qualifying fingertips, prefer the one that is closest to or
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

    // Last TryGetNearestSurfaceFromPoint result, served while the surface buffers are
    // mid-update on worker threads (same gating as TryGetTouchPoint).
    private bool    _lastPreviewFound;
    private Vector2 _lastPreviewUV;
    private Vector3 _lastPreviewPoint;

    /// <summary>
    /// Finds the nearest display-surface UV to an arbitrary world point (e.g. index tip while carrying).
    /// Used for placement preview when the finger is near but not necessarily touching the mesh.
    /// </summary>
    public bool TryGetNearestSurfaceFromPoint(
        Vector3 worldPoint,
        float maxDistanceMeters,
        out Vector2 uv,
        out Vector3 surfacePoint)
    {
        // Worker jobs own Hits/IsSurface between the dispatch callback and the harvest; serve the
        // last stable result for that ~1-frame window.
        if (!_surface.SurfaceStable)
        {
            uv           = _lastPreviewUV;
            surfacePoint = _lastPreviewPoint;
            return _lastPreviewFound;
        }

        uv = Vector2.zero;
        surfacePoint = Vector3.zero;
        _lastPreviewFound = false;

        if (!_surface.IsValid)
            return false;

        maxDistanceMeters = Mathf.Max(0.01f, maxDistanceMeters);
        float searchDist = Mathf.Max(maxCellSearchDist, maxDistanceMeters);

        Vector3 toPos = worldPoint - _surface.WristPosition;
        float along  = Vector3.Dot(toPos, _surface.AxisDir);
        float across = Vector3.Dot(toPos, _surface.AxisRight);

        if (!TryGetNearestSurfaceHit(along, across, searchDist, out Vector3 surfaceHit))
            return false;

        float above = Vector3.Dot(worldPoint - surfaceHit, _surface.AxisUp);
        Vector3 projectedPos = worldPoint - _surface.AxisUp * above;
        float distance = Vector3.Distance(worldPoint, projectedPos);
        if (distance > maxDistanceMeters)
            return false;

        uv = ComputeMeshUV(projectedPos);
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)
            return false;

        surfacePoint = projectedPos;
        _lastPreviewFound = true;
        _lastPreviewUV    = uv;
        _lastPreviewPoint = surfacePoint;
        return true;
    }

    // -----------------------------------------------------------------------
    // HELPERS
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes the touch UV with the same SurfaceUV mapping the mesh vertices use, so touch
    /// coordinates address the rendered texture exactly.
    /// </summary>
    private Vector2 ComputeMeshUV(Vector3 pt)
    {
        return SurfaceUV.Compute(
            pt, _surface.WristPosition, _surface.AxisDir, _surface.AxisRight,
            _surface.ProjCenter, _surface.PronationAngle / (2f * Mathf.PI),
            _surface.displayOffset, _surface.displayWidth, _surface.displayHeight);
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
    private bool TryGetNearestSurfaceHit(float along, float across, float maxSearchDist, out Vector3 hit)
    {
        hit = Vector3.zero;

        int rows = _surface.SurfaceRows;
        int cols = _surface.SurfaceCols;
        if (rows == 0 || cols == 0) return false;

        SurfaceBuffer buf       = _surface.SurfaceBuf;
        int           total     = rows * cols;
        float         bestSq    = maxSearchDist * maxSearchDist;
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
