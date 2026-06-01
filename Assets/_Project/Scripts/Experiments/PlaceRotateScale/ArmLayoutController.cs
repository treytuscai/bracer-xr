using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Maps arm mesh UV onto this canvas and drives pick / placement.
/// <see cref="InteractionMode.VerticalListReorder"/> legacy list reshuffle with placeholder.
/// <see cref="InteractionMode.FreePlace"/> pick presses a widget → it follows the index fingertip in world space;
/// committing maps it back onto the decal using linear UV projection.
/// </summary>
public class ArmLayoutController : MonoBehaviour
{
    public enum InteractionMode
    {
        VerticalListReorder,
        FreePlace
    }

    [Tooltip("Rectangle whose size defines UV normalization (usually this object's RectTransform).")]
    public RectTransform canvasRect;

    [Tooltip("ArmSurfaceGenerator used to compute the linear UV at commit time. Must match the surface the canvas is mapped onto.")]
    public ArmSurfaceGenerator armSurface;

    [Tooltip("The camera that captures the UICanvas into the RenderTexture used by the cylinder material. Used to invert the canvas→cylinder rendering pipeline so a tap on the cylinder maps to the exact canvas position whose content is rendered there.")]
    public Camera canvasCamera;

    [Tooltip("Physical lateral width of the arm canvas in meters — must match ForearmDepthSurface.textWidthMeters. Used to compute linear U at commit time.")]
    [Min(0.01f)] public float lateralWidthMeters = 0.15f;

    public InteractionMode interactionMode = InteractionMode.FreePlace;

    [Tooltip("If the finger moves opposite to the list on-device, toggle this.")]
    public bool invertVerticalUv = true;

    [Header("UV ↔ canvas (placement on arm)")]
    [Tooltip("If true, placements use one fixed stripe U instead of lateral tracking")]
    public bool pinHorizontalUvToStripeCenter;

    [Range(0f, 1f)] public float pinnedHorizontalUv = 0.5f;

    [Tooltip("Lateral (U) clamp. Set to (0, 1) for the full canvas width. Clamping too tightly pins placement to canvas X edges.")]
    public Vector2 lateralUvClampMinMax = new Vector2(0f, 1f);

    [Tooltip("Axial (V) clamp. Set to (0, 1) for the full canvas height. Slight inset (0.02–0.98) only if you see widgets clip at the wrist/elbow ends.")]
    public Vector2 axialUvClampMinMax = new Vector2(0f, 1f);

    [Header("Finger carry")]
    [Tooltip("Seconds smoothing index-tip motion while the widget floats on the finger. 0 = raw tracking.")]
    [Min(0f)] public float fingerCarrySmoothTime = 0.048f;

    [Tooltip("Blend widget rotation toward the fingertip orientation (helps if you want tilt). 0 = keep rotation frozen at pickup.")]
    [Range(0f, 1f)] public float fingerCarryRotationBlend;

    [Tooltip("Offset (meters) from index fingertip to widget origin, in fingertip-local axes. +Z is along the finger forward, +Y is up from the nail. Adjust if the widget sits inside the hand or behind it.")]
    public Vector3 carryAttachOffsetTipLocal = new Vector3(0f, 0.012f, 0.005f);

    [Tooltip("If true, ignore the saved grab-point offset and snap the widget origin to the fingertip when picked up. Recommended.")]
    public bool stickWidgetOriginToFingertip = true;

    [Tooltip("Target world width (meters) of the widget while carried on the fingertip. ~2-3x a fingertip (≈25 mm), so 40 mm is a good default.")]
    [Min(0.005f)] public float carriedWorldWidthMeters = 0.04f;

    [Header("Picking")]
    [Tooltip("When multiple widgets overlap, prefer the smaller one (the precise target). Useful when a small icon sits over a large background.")]
    public bool preferSmallerOverlappingTarget = true;

    [Tooltip("Vertical-list arms: match fingertip to rows by V (axial) only, ignoring U. Lets the user touch anywhere across the forearm width and still hit the right row.")]
    public bool pickByVerticalAxisOnly = true;

    [Tooltip("Extra padding (in canvas-local units) added to each row's bounds when picking. Bigger = more forgiving target.")]
    [Min(0f)] public float pickPaddingLocal = 20f;

    [Header("Drag stability (list mode & placement smoothing)")]
    [Tooltip("Placement UV low-pass before mapping to decal (Hz). 0 = instant snap.")]
    [Min(0f)] public float fingerSmoothHz = 14f;

    [Min(1)] public int placeholderSettleFrames = 3;

    [Tooltip("(Free mode) Leave ignoreLayout enabled after sticking so VerticalLayoutGroup does not reclaim.")]
    public bool keepIgnoreLayoutAfterFreePlace = true;

    [Header("Canvas logical extent (overrides RectTransform.rect for UV ↔ canvas mapping)")]
    [Tooltip("The canvas-local rectangle that maps to UV (0,0)→(1,1). MUST cover the area where children are visible. " +
             "If RectTransform.rect.height is 0 (zero-sizeDelta canvases driven by anchors), the script falls back to this. " +
             "Default (-50,-200,100,200) covers a 100×200 area extending downward from the top-left anchor.")]
    public Rect canvasLogicalRect = new Rect(-50f, -200f, 100f, 200f);

    [Tooltip("If true, use canvasLogicalRect even when RectTransform.rect is non-degenerate. Useful if the canvas rect doesn't match the RT capture area.")]
    public bool forceUseLogicalRect = true;

    [Header("Grid snap (FreePlace placement)")]
    [Tooltip("If true, placements snap to the nearest cell in a regular grid over the canvas. Hides residual mapping/tracking error and gives consistent, predictable placement.")]
    public bool useGridSnap = true;

    [Tooltip("Number of horizontal grid cells across the canvas (columns).")]
    [Range(1, 12)] public int gridColumns = 3;

    [Tooltip("Number of vertical grid cells along the canvas (rows).")]
    [Range(1, 12)] public int gridRows = 4;

    [Tooltip("Padding (canvas-local units) inset from each canvas edge before subdividing into cells. Keeps items from snapping to extreme edges.")]
    [Range(0f, 40f)] public float gridPadding = 5f;

    [Tooltip("If true, when the snapped cell is already occupied by another widget the placement falls back to the nearest unoccupied cell. Otherwise multiple widgets can stack in one cell.")]
    public bool preventCellOverlap = true;

    [Tooltip("Match radius (canvas-local units) used to consider a sibling widget as 'occupying' a cell.")]
    [Min(1f)] public float occupancyMatchRadius = 25f;

    [Tooltip("On placement, resize the widget's RectTransform.sizeDelta to fit the snapped grid cell (minus cellInnerPadding on each side). Disable to keep the widget's original size.")]
    public bool resizeToGridCell = true;

    [Tooltip("Inner padding (canvas-local units) inside each cell when resizing widgets, so they don't touch the grid border.")]
    [Min(0f)] public float cellInnerPadding = 4f;

    [Header("Grid overlay (drawn into the canvas, shown on cylinder via RT)")]
    [Tooltip("If true, the controller creates Image children inside canvasRect that draw the grid cell borders. The grid is drawn on the canvas so it gets captured into the RenderTexture and shown on the cylinder — guaranteeing the grid you SEE and the cells the snap targets are the exact same thing.")]
    public bool drawGridOnCanvas = true;

    [Tooltip("Grid line thickness in canvas-local units.")]
    [Range(0.5f, 8f)] public float gridLineThickness = 1.5f;

    [Tooltip("Color of the grid cell borders.")]
    public Color gridLineColor = new Color(1f, 1f, 1f, 0.55f);

    [Tooltip("Color used to highlight the currently-touched cell.")]
    public Color gridHighlightColor = new Color(0.3f, 0.9f, 1f, 0.35f);

    [Header("Debug")]
    [Tooltip("Log placement and pick coordinates each gesture. Verify the UV path produces expected values.")]
    public bool debugLogInteractions = true;

    [Tooltip("Draw a Gizmos overlay of the grid cells in the scene view (only visible in Editor / when the controller is selected).")]
    public bool drawGridGizmos = true;

    RectTransform draggedItem;
    GameObject placeholder;
    Vector2 smoothedFingerLocal;
    Vector2 smoothedSanitizedUv;
    int placeholderCandidateIndex = -1;
    int placeholderCandidateStreak;

    // Free fingertip carry
    Transform _carrySavedParent;
    Transform _carryCommitParent;
    int _carrySavedSiblingIndex;
    Vector3 _holdOffsetLocalInTipSpace;
    Quaternion _carryPickupWorldRotation;
    Vector3 _carrySavedLocalScale = Vector3.one;
    bool _destroyCarriedOnAbort;

    /// <summary>True while a widget is detached and following the fingertip.</summary>
    public bool IsCarrying => draggedItem != null;
    /// <summary>The RectTransform currently being carried, or null. Experiment scenes use this to skip per-frame resize enforcement on the carried item.</summary>
    public RectTransform CarriedItem => draggedItem;

    Vector3 _tipFilteredPos;
    Vector3 _tipPosSmoothVel;
    Quaternion _tipFilteredRot;

    // Grid overlay state — generated at runtime as canvas children so the grid is
    // captured into the RenderTexture and rendered onto the cylinder via the same
    // pipeline as the widgets themselves.
    RectTransform _gridOverlayRoot;
    RectTransform _gridHighlight;
    int _gridLastColumns = -1, _gridLastRows = -1;
    float _gridLastPadding = float.NaN, _gridLastThickness = float.NaN;
    Rect _gridLastRect;

    RectTransform LayoutRect => canvasRect != null ? canvasRect : transform as RectTransform;

    void OnValidate()
    {
        if (canvasRect == null)
            canvasRect = GetComponent<RectTransform>();
    }

    Vector2 SanitizedMeshUv(Vector2 raw)
    {
        float u = raw.x;
        float v = raw.y;

        if (pinHorizontalUvToStripeCenter)
            u = pinnedHorizontalUv;
        else
        {
            float uMin = Mathf.Min(lateralUvClampMinMax.x, lateralUvClampMinMax.y);
            float uMax = Mathf.Max(lateralUvClampMinMax.x, lateralUvClampMinMax.y);
            u = Mathf.Clamp(u, uMin, uMax);
        }

        float vMin = Mathf.Min(axialUvClampMinMax.x, axialUvClampMinMax.y);
        float vMax = Mathf.Max(axialUvClampMinMax.x, axialUvClampMinMax.y);
        v = Mathf.Clamp(v, vMin, vMax);

        if (invertVerticalUv)
            v = 1f - v;

        return new Vector2(u, v);
    }

    /// <summary>
    /// The canvas-local rectangle that exactly matches the canvas camera's capture
    /// region — i.e. the area whose content actually ends up on the cylinder via
    /// the RenderTexture pipeline. This guarantees that grid lines drawn inside
    /// this rect appear on the visible part of the cylinder, and that grid-snap
    /// targets match what the user sees.
    ///
    /// Falls back to canvasLogicalRect (then RectTransform.rect) if no camera is wired.
    /// </summary>
    Rect EffectiveCanvasRect()
    {
        RectTransform rt = LayoutRect;
        if (rt == null) return canvasLogicalRect;

        // Camera-driven extent (preferred): use the orthographic camera's actual capture
        // region in canvas-local coordinates.
        if (canvasCamera != null && canvasCamera.orthographic)
        {
            float halfH = canvasCamera.orthographicSize;
            float halfW = halfH * Mathf.Max(canvasCamera.aspect, 1e-3f);
            Vector3 camCenterCanvas = rt.InverseTransformPoint(canvasCamera.transform.position);
            // Note: this assumes the canvas axes are aligned with the camera's right/up.
            // For the default axis-aligned setup (no rotation), this is exact.
            return new Rect(
                camCenterCanvas.x - halfW,
                camCenterCanvas.y - halfH,
                2f * halfW,
                2f * halfH);
        }

        Rect r = rt.rect;
        bool rectDegenerate = Mathf.Abs(r.width) < 1e-3f || Mathf.Abs(r.height) < 1e-3f;
        return (forceUseLogicalRect || rectDegenerate) ? canvasLogicalRect : r;
    }

    Vector3 FingerWorldFromSanitizedUv(Vector2 sanitizedUv)
    {
        RectTransform rt = LayoutRect;
        if (rt == null) return Vector3.zero;

        Rect r = EffectiveCanvasRect();
        float x = Mathf.Lerp(r.xMin, r.xMax, sanitizedUv.x);
        float y = Mathf.Lerp(r.yMin, r.yMax, sanitizedUv.y);
        return rt.TransformPoint(new Vector3(x, y, 0f));
    }

    Vector2 LayoutLocalFromSanitizedUv(Vector2 sanitizedUv)
    {
        if (LayoutRect == null) return Vector2.zero;
        Rect r = EffectiveCanvasRect();
        return new Vector2(
            Mathf.Lerp(r.xMin, r.xMax, sanitizedUv.x),
            Mathf.Lerp(r.yMin, r.yMax, sanitizedUv.y));
    }

    Vector2 FingerLocalFromUv(Vector2 rawMeshUv) =>
        LayoutLocalFromSanitizedUv(SanitizedMeshUv(rawMeshUv));

    /// <summary>
    /// THE robust mapping: given a tap point on the cylinder, return the canvas-local
    /// (x, y) position whose rendered content actually appears at that tap point.
    ///
    /// How: the cylinder shader samples the canvas RenderTexture at the mesh-UV of
    /// each fragment. The RT is captured by <see cref="canvasCamera"/> from the canvas.
    /// So mesh-UV (u,v) on the cylinder == camera viewport (u,v) == a specific point
    /// on the canvas plane (found by intersecting the camera's viewport ray with the
    /// canvas plane). This sidesteps every guess about orth size / aspect / scale —
    /// we just ask Unity's camera projection directly.
    ///
    /// Falls back to the legacy arc-remap path if no camera/surface is wired up.
    /// </summary>
    public Vector2 TapWorldToCanvasLocal(Vector3 tapWorld)
    {
        if (canvasCamera == null || armSurface == null || canvasRect == null)
        {
            // BREAKING
            // Vector2 raw = armSurface != null
            //     ? armSurface.ComputeLinearUv(tapWorld, lateralWidthMeters)
            //     : Vector2.zero;
            Vector2 raw = Vector2.zero;
            return LayoutLocalFromSanitizedUv(SanitizedMeshUv(raw));
        }

        // Get the cylinder mesh-UV at the tap (angular U around cylinder, axial V).
        if (!armSurface.GetClosestSurfacePoint(tapWorld, out _, out Vector2 meshUv, out _))
            return Vector2.zero;

        // Optional axial flip (matches the existing convention).
        if (invertVerticalUv) meshUv.y = 1f - meshUv.y;

        // Cast a ray through the camera's viewport at (meshUv) and intersect with
        // the canvas plane. The hit point's canvas-local (x, y) is exactly where the
        // canvas content that the cylinder shader samples lives.
        Ray ray = canvasCamera.ViewportPointToRay(new Vector3(meshUv.x, meshUv.y, 0f));
        Plane canvasPlane = new Plane(canvasRect.forward, canvasRect.position);

        if (!canvasPlane.Raycast(ray, out float hitDist))
            return Vector2.zero;

        Vector3 worldHit = ray.GetPoint(hitDist);
        Vector3 canvasLocal3 = canvasRect.InverseTransformPoint(worldHit);

        return new Vector2(canvasLocal3.x, canvasLocal3.y);
    }

    /// <summary>
    /// Returns the (xMin, yMin, cellWidth, cellHeight) of the inset grid region
    /// used by <see cref="SnapToGrid"/>. Centralized so gizmos and the snap math
    /// always agree.
    /// </summary>
    void GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH)
    {
        Rect r = EffectiveCanvasRect();
        xMin = r.xMin + gridPadding;
        yMin = r.yMin + gridPadding;
        float xMax = r.xMax - gridPadding;
        float yMax = r.yMax - gridPadding;
        cellW = (xMax - xMin) / Mathf.Max(gridColumns, 1);
        cellH = (yMax - yMin) / Mathf.Max(gridRows, 1);
    }

    /// <summary>Center of cell (col, row) in canvas-local coords.</summary>
    Vector2 CellCenter(int col, int row)
    {
        GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH);
        return new Vector2(
            xMin + cellW * (col + 0.5f),
            yMin + cellH * (row + 0.5f));
    }

    /// <summary>Cell indices nearest to the given canvas-local point.</summary>
    void NearestCellIndex(Vector2 canvasPos, out int col, out int row)
    {
        GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH);
        col = Mathf.Clamp(Mathf.FloorToInt((canvasPos.x - xMin) / Mathf.Max(cellW, 1e-3f)), 0, gridColumns - 1);
        row = Mathf.Clamp(Mathf.FloorToInt((canvasPos.y - yMin) / Mathf.Max(cellH, 1e-3f)), 0, gridRows - 1);
    }

    /// <summary>
    /// Returns the canvas-local center of the grid cell nearest to <paramref name="canvasPos"/>.
    /// If <see cref="preventCellOverlap"/> is true and that cell is occupied by a sibling
    /// widget (excluding <paramref name="ignore"/>), picks the nearest unoccupied cell instead.
    /// </summary>
    Vector2 SnapToGrid(Vector2 canvasPos, RectTransform ignore)
    {
        if (!useGridSnap) return canvasPos;

        NearestCellIndex(canvasPos, out int col, out int row);
        Vector2 target = CellCenter(col, row);

        if (!preventCellOverlap || LayoutRect == null) return target;

        // If desired cell is free, take it.
        if (!IsCellOccupied(target, ignore)) return target;

        // Otherwise spiral outward over cells looking for a free one.
        int maxRing = Mathf.Max(gridColumns, gridRows);
        for (int ring = 1; ring <= maxRing; ring++)
        {
            for (int dRow = -ring; dRow <= ring; dRow++)
            {
                for (int dCol = -ring; dCol <= ring; dCol++)
                {
                    // Only consider the ring boundary, not interior cells we've already checked.
                    if (Mathf.Abs(dCol) != ring && Mathf.Abs(dRow) != ring) continue;

                    int c = col + dCol;
                    int r = row + dRow;
                    if (c < 0 || c >= gridColumns || r < 0 || r >= gridRows) continue;

                    Vector2 candidate = CellCenter(c, r);
                    if (!IsCellOccupied(candidate, ignore)) return candidate;
                }
            }
        }

        // All cells full — fall back to the originally requested cell (stacks).
        return target;
    }

    bool IsCellOccupied(Vector2 cellCenter, RectTransform ignore)
    {
        if (LayoutRect == null) return false;
        float r2 = occupancyMatchRadius * occupancyMatchRadius;
        int n = LayoutRect.childCount;
        for (int i = 0; i < n; i++)
        {
            var child = LayoutRect.GetChild(i) as RectTransform;
            if (child == null || child == ignore || !child.gameObject.activeSelf) continue;
            // Skip the overlay/highlight elements — they belong to us, not to user content.
            if (child == _gridOverlayRoot || child == _gridHighlight) continue;
            Vector2 pos = child.localPosition;
            if ((pos - cellCenter).sqrMagnitude < r2) return true;
        }
        return false;
    }

    /// <summary>
    /// Generate / regenerate the in-canvas grid line images (and the highlight rect).
    /// Called on Start, OnValidate (editor), and when grid parameters change. Cheap
    /// when nothing has changed (early-outs based on cached params).
    /// </summary>
    void EnsureGridOverlay()
    {
        if (!useGridSnap || !drawGridOnCanvas || LayoutRect == null) return;

        Rect r = EffectiveCanvasRect();
        bool dirty =
            _gridOverlayRoot == null ||
            _gridLastColumns != gridColumns ||
            _gridLastRows != gridRows ||
            !Mathf.Approximately(_gridLastPadding, gridPadding) ||
            !Mathf.Approximately(_gridLastThickness, gridLineThickness) ||
            _gridLastRect != r;

        if (!dirty)
        {
            // Just refresh colors in case the inspector changed them.
            if (_gridOverlayRoot != null)
                foreach (var img in _gridOverlayRoot.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    if (img.transform != _gridHighlight) img.color = gridLineColor;
            if (_gridHighlight != null)
            {
                var img = _gridHighlight.GetComponent<UnityEngine.UI.Image>();
                if (img != null) img.color = gridHighlightColor;
            }
            return;
        }

        // Tear down old overlay.
        if (_gridOverlayRoot != null)
        {
            if (Application.isPlaying) Destroy(_gridOverlayRoot.gameObject);
            else DestroyImmediate(_gridOverlayRoot.gameObject);
            _gridOverlayRoot = null;
            _gridHighlight = null;
        }

        // Create overlay root — a non-interactive RectTransform that fills the canvas.
        var rootGo = new GameObject("__GridOverlay",
            typeof(RectTransform), typeof(CanvasGroup));
        _gridOverlayRoot = rootGo.GetComponent<RectTransform>();
        _gridOverlayRoot.SetParent(LayoutRect, false);
        _gridOverlayRoot.anchorMin = Vector2.zero;
        _gridOverlayRoot.anchorMax = Vector2.one;
        _gridOverlayRoot.offsetMin = Vector2.zero;
        _gridOverlayRoot.offsetMax = Vector2.zero;
        _gridOverlayRoot.localPosition = Vector3.zero;
        var cg = rootGo.GetComponent<CanvasGroup>();
        cg.blocksRaycasts = false;
        cg.interactable = false;
        _gridOverlayRoot.SetAsFirstSibling();

        GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH);
        float xMax = xMin + cellW * gridColumns;
        float yMax = yMin + cellH * gridRows;
        float t = Mathf.Max(gridLineThickness, 0.5f);

        // Vertical lines.
        for (int c = 0; c <= gridColumns; c++)
        {
            float x = xMin + c * cellW;
            CreateLine(_gridOverlayRoot,
                new Vector2(x, (yMin + yMax) * 0.5f),
                new Vector2(t, yMax - yMin),
                gridLineColor,
                $"V{c}");
        }

        // Horizontal lines.
        for (int rrow = 0; rrow <= gridRows; rrow++)
        {
            float y = yMin + rrow * cellH;
            CreateLine(_gridOverlayRoot,
                new Vector2((xMin + xMax) * 0.5f, y),
                new Vector2(xMax - xMin, t),
                gridLineColor,
                $"H{rrow}");
        }

        // Cell highlight (one filled rect that moves around).
        var hi = new GameObject("__GridHighlight",
            typeof(RectTransform), typeof(UnityEngine.UI.Image));
        _gridHighlight = hi.GetComponent<RectTransform>();
        _gridHighlight.SetParent(_gridOverlayRoot, false);
        _gridHighlight.anchorMin = _gridHighlight.anchorMax = new Vector2(0.5f, 0.5f);
        _gridHighlight.pivot = new Vector2(0.5f, 0.5f);
        _gridHighlight.sizeDelta = new Vector2(cellW, cellH);
        var himg = hi.GetComponent<UnityEngine.UI.Image>();
        himg.color = gridHighlightColor;
        himg.raycastTarget = false;
        _gridHighlight.gameObject.SetActive(false);

        _gridLastColumns = gridColumns;
        _gridLastRows = gridRows;
        _gridLastPadding = gridPadding;
        _gridLastThickness = gridLineThickness;
        _gridLastRect = r;
    }

    static void CreateLine(RectTransform parent, Vector2 center, Vector2 size, Color color, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = center;
        rt.sizeDelta = size;
        var img = go.GetComponent<UnityEngine.UI.Image>();
        img.color = color;
        img.raycastTarget = false;
    }

    /// <summary>Move the highlight to a specific cell. Pass (-1,-1) to hide.</summary>
    public void SetHighlightCell(int col, int row)
    {
        if (_gridHighlight == null) return;
        if (col < 0 || row < 0 || col >= gridColumns || row >= gridRows)
        {
            _gridHighlight.gameObject.SetActive(false);
            return;
        }
        GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH);
        _gridHighlight.anchoredPosition = new Vector2(
            xMin + cellW * (col + 0.5f),
            yMin + cellH * (row + 0.5f));
        _gridHighlight.sizeDelta = new Vector2(cellW, cellH);
        _gridHighlight.gameObject.SetActive(true);
    }

    /// <summary>Updates the highlight from a canvas-local point (snaps to nearest cell).</summary>
    public void HighlightFromCanvasPos(Vector2 canvasLocalPos)
    {
        NearestCellIndex(canvasLocalPos, out int col, out int row);
        SetHighlightCell(col, row);
    }

    void Start()
    {
        EnsureGridOverlay();
    }

    void Update()
    {
        // Cheap idempotent re-check so inspector tweaks during play mode are reflected live.
        EnsureGridOverlay();

        // Hide the cell highlight when nothing is being carried (so the grid is just lines).
        if (draggedItem == null && _gridHighlight != null && _gridHighlight.gameObject.activeSelf)
            _gridHighlight.gameObject.SetActive(false);
    }

    void SmoothSanitizedUvToward(Vector2 targetSanitizedUv, float dt)
    {
        if (fingerSmoothHz <= 0f)
            smoothedSanitizedUv = targetSanitizedUv;
        else
        {
            float t = 1f - Mathf.Exp(-fingerSmoothHz * Mathf.Max(dt, 1e-5f));
            smoothedSanitizedUv = Vector2.Lerp(smoothedSanitizedUv, targetSanitizedUv, t);
        }
    }

    void SmoothFingerToward(Vector2 finger, float dt)
    {
        if (fingerSmoothHz <= 0f)
            smoothedFingerLocal = finger;
        else
        {
            float t = 1f - Mathf.Exp(-fingerSmoothHz * Mathf.Max(dt, 1e-5f));
            smoothedFingerLocal = Vector2.Lerp(smoothedFingerLocal, finger, t);
        }
    }

    static void GetAxisAlignedBoundsInParent(RectTransform parent, RectTransform child,
        out float minY, out float maxY, out float centerY)
    {
        minY = float.MaxValue;
        maxY = float.MinValue;
        Vector3[] corners = new Vector3[4];
        child.GetWorldCorners(corners);
        for (int i = 0; i < 4; i++)
        {
            Vector3 lp = parent.InverseTransformPoint(corners[i]);
            minY = Mathf.Min(minY, lp.y);
            maxY = Mathf.Max(maxY, lp.y);
        }
        centerY = (minY + maxY) * 0.5f;
    }

    static void GetAxisAlignedBoundsInParent2D(RectTransform parent, RectTransform child,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        minY = float.MaxValue;
        maxY = float.MinValue;
        Vector3[] corners = new Vector3[4];
        child.GetWorldCorners(corners);
        for (int i = 0; i < 4; i++)
        {
            Vector3 lp = parent.InverseTransformPoint(corners[i]);
            minX = Mathf.Min(minX, lp.x);
            maxX = Mathf.Max(maxX, lp.x);
            minY = Mathf.Min(minY, lp.y);
            maxY = Mathf.Max(maxY, lp.y);
        }
    }

    /// <summary>
    /// FreePlace picker: choose the child whose bounding-box center is closest to the finger.
    /// If the finger is inside any child's padded bounds, those candidates win over non-overlapping ones.
    /// Order-independent and works correctly when widgets are laid out manually with anchored positions.
    /// </summary>
    bool TryPickChildByProximity(Vector2 fingerLocal, out RectTransform hit)
    {
        hit = null;
        RectTransform parentRt = LayoutRect;
        if (parentRt == null) return false;

        RectTransform bestOverlap = null;
        float bestOverlapDistSq = float.MaxValue;
        RectTransform bestProximity = null;
        float bestProximityDistSq = float.MaxValue;

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            if (!(transform.GetChild(i) is RectTransform childRt)) continue;
            if (childRt.gameObject == placeholder) continue;
            if (!childRt.gameObject.activeInHierarchy) continue;
            // Never pick the grid overlay / highlight — they cover the whole canvas.
            if (childRt == _gridOverlayRoot || childRt == _gridHighlight) continue;
            if (childRt.name == "__GridOverlay" || childRt.name == "__GridHighlight") continue;

            GetAxisAlignedBoundsInParent2D(parentRt, childRt,
                out float mnX, out float mxX, out float mnY, out float mxY);

            float padded_mnX = mnX - pickPaddingLocal;
            float padded_mxX = mxX + pickPaddingLocal;
            float padded_mnY = mnY - pickPaddingLocal;
            float padded_mxY = mxY + pickPaddingLocal;

            Vector2 center = new Vector2((mnX + mxX) * 0.5f, (mnY + mxY) * 0.5f);
            float distSq = (fingerLocal - center).sqrMagnitude;

            bool overlaps =
                fingerLocal.x >= padded_mnX && fingerLocal.x <= padded_mxX &&
                fingerLocal.y >= padded_mnY && fingerLocal.y <= padded_mxY;

            if (overlaps)
            {
                if (distSq < bestOverlapDistSq)
                {
                    bestOverlapDistSq = distSq;
                    bestOverlap = childRt;
                }
            }
            else if (distSq < bestProximityDistSq)
            {
                bestProximityDistSq = distSq;
                bestProximity = childRt;
            }
        }

        hit = bestOverlap != null ? bestOverlap : bestProximity;
        return hit != null;
    }

    bool TryPickChildAtFinger(Vector2 fingerLocal, bool use2DOverlap, out RectTransform hit)
    {
        hit = null;
        var parentRt = LayoutRect;
        if (parentRt == null) return false;

        // For vertical lists, ignore lateral position when picking — only Y matters.
        bool yOnly = pickByVerticalAxisOnly || !use2DOverlap;

        RectTransform bestOverlap = null;
        float bestOverlapArea = float.MaxValue;
        RectTransform bestFallback = null;
        float bestFallbackScore = float.MaxValue;

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            if (!(transform.GetChild(i) is RectTransform childRt)) continue;
            if (childRt.gameObject == placeholder) continue;
            if (!childRt.gameObject.activeInHierarchy) continue;

            GetAxisAlignedBoundsInParent2D(parentRt, childRt,
                out float mnX, out float mxX, out float mnY, out float mxY);

            // Pad so small targets are easier to grab.
            mnX -= pickPaddingLocal; mxX += pickPaddingLocal;
            mnY -= pickPaddingLocal; mxY += pickPaddingLocal;

            float width = mxX - mnX;
            float height = mxY - mnY;
            float area = Mathf.Max(0f, width) * Mathf.Max(0f, height);

            bool overlaps = yOnly
                ? (fingerLocal.y >= mnY && fingerLocal.y <= mxY)
                : (fingerLocal.x >= mnX && fingerLocal.x <= mxX &&
                   fingerLocal.y >= mnY && fingerLocal.y <= mxY);

            if (overlaps)
            {
                if (!preferSmallerOverlappingTarget)
                {
                    hit = childRt;
                    return true;
                }

                if (area < bestOverlapArea)
                {
                    bestOverlapArea = area;
                    bestOverlap = childRt;
                }
            }
            else
            {
                float cy = (mnY + mxY) * 0.5f;
                float score = Mathf.Abs(fingerLocal.y - cy);
                if (!yOnly)
                {
                    float cx = (mnX + mxX) * 0.5f;
                    score += Mathf.Abs(fingerLocal.x - cx);
                }
                if (score < bestFallbackScore)
                {
                    bestFallbackScore = score;
                    bestFallback = childRt;
                }
            }
        }

        hit = bestOverlap != null ? bestOverlap : bestFallback;
        return hit != null;
    }

    // --- Vertical list reorder ---

    public RectTransform StartDrag(Vector2 uv)
    {
        if (interactionMode != InteractionMode.VerticalListReorder || LayoutRect == null || transform.childCount == 0)
            return null;

        Vector2 finger = FingerLocalFromUv(uv);
        if (!TryPickChildAtFinger(finger, false, out draggedItem))
            return null;

        placeholder = new GameObject("Placeholder");
        placeholder.AddComponent<RectTransform>().SetParent(transform, false);
        LayoutElement le = placeholder.AddComponent<LayoutElement>();

        le.preferredHeight = draggedItem.rect.height;
        le.preferredWidth = draggedItem.rect.width;

        placeholder.transform.SetSiblingIndex(draggedItem.GetSiblingIndex());

        LayoutElement draggedLe = draggedItem.GetComponent<LayoutElement>();
        if (draggedLe == null) draggedLe = draggedItem.gameObject.AddComponent<LayoutElement>();
        draggedLe.ignoreLayout = true;

        draggedItem.SetAsLastSibling();

        smoothedFingerLocal = finger;
        placeholderCandidateIndex = -1;
        placeholderCandidateStreak = 0;

        return draggedItem;
    }

    public void UpdateDrag(Vector2 uv)
    {
        if (interactionMode != InteractionMode.VerticalListReorder ||
            LayoutRect == null || draggedItem == null || placeholder == null) return;

        Vector2 finger = FingerLocalFromUv(uv);
        SmoothFingerToward(finger, Time.deltaTime);

        float followY = smoothedFingerLocal.y;
        draggedItem.anchoredPosition = new Vector2(draggedItem.anchoredPosition.x, followY);

        int newIndex = ComputeBestSlotIndexForFingerY(followY);
        int phIdx = placeholder.transform.GetSiblingIndex();

        if (newIndex == placeholderCandidateIndex)
            placeholderCandidateStreak++;
        else
        {
            placeholderCandidateIndex = newIndex;
            placeholderCandidateStreak = 1;
        }

        if (placeholderCandidateStreak >= placeholderSettleFrames && newIndex != phIdx)
        {
            placeholder.transform.SetSiblingIndex(newIndex);
            placeholderCandidateStreak = 0;
        }
    }

    int ComputeBestSlotIndexForFingerY(float targetY)
    {
        var parentRt = LayoutRect;
        int bestIdx = 0;
        float bestDist = float.MaxValue;

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child == draggedItem || child == placeholder.transform) continue;

            if (!(child is RectTransform childRt)) continue;

            GetAxisAlignedBoundsInParent(parentRt, childRt, out float minY, out float maxY, out float centerY);

            float distFromBand = targetY < minY ? minY - targetY : targetY > maxY ? targetY - maxY : 0f;
            float score = distFromBand * distFromBand * 100f + Mathf.Abs(targetY - centerY);

            if (score < bestDist)
            {
                bestDist = score;
                bestIdx = i;
            }
        }

        return Mathf.Clamp(bestIdx, 0, Mathf.Max(0, transform.childCount - 1));
    }

    public void EndDrag()
    {
        if (interactionMode != InteractionMode.VerticalListReorder ||
            draggedItem == null || placeholder == null) return;

        LayoutElement le = draggedItem.GetComponent<LayoutElement>();
        if (le != null) le.ignoreLayout = false;

        draggedItem.SetSiblingIndex(placeholder.transform.GetSiblingIndex());

        Destroy(placeholder);
        placeholder = null;
        draggedItem = null;
        placeholderCandidateIndex = -1;
        placeholderCandidateStreak = 0;
    }

    // --- Free place — fingertip then stick on decal ---

    /// <summary>
    /// Project a world point onto the ItemList canvas plane and return it in the canvas's local 2-D coordinates.
    /// </summary>
    Vector2 WorldToCanvasLocal(Vector3 worldPoint)
    {
        Vector3 local = LayoutRect.InverseTransformPoint(worldPoint);
        return new Vector2(local.x, local.y);   // ignore z — project onto plane
    }

    public bool TryBeginCarry(Vector3 contactWorldPoint, Transform indexTipWorld)
    {
        if (interactionMode != InteractionMode.FreePlace || LayoutRect == null || transform.childCount == 0)
            return false;

        if (indexTipWorld == null)
            return false;

        // Direct camera-projection mapping — same one used for placement, so pick and
        // place are guaranteed to agree on where the canvas-local finger position is.
        Vector2 finger = TapWorldToCanvasLocal(contactWorldPoint);

        if (debugLogInteractions)
        {
            Debug.Log($"[PickAttempt] contact={contactWorldPoint:F3} " +
                      $"canvasLocal={finger:F2} extent={EffectiveCanvasRect()}");

            // Dump every candidate child's bounds and distance so we can verify the picker chose correctly.
            RectTransform parentRt = LayoutRect;
            for (int ci = 0; ci < transform.childCount; ci++)
            {
                if (!(transform.GetChild(ci) is RectTransform crt)) continue;
                if (!crt.gameObject.activeInHierarchy) continue;
                GetAxisAlignedBoundsInParent2D(parentRt, crt,
                    out float mnX, out float mxX, out float mnY, out float mxY);
                Vector2 c = new Vector2((mnX + mxX) * 0.5f, (mnY + mxY) * 0.5f);
                float d = (finger - c).magnitude;
                Debug.Log($"[PickCandidate] sibling={ci} name='{crt.name}' " +
                          $"bounds=({mnX:F1},{mnY:F1})→({mxX:F1},{mxY:F1}) " +
                          $"center=({c.x:F1},{c.y:F1}) dist={d:F1}");
            }
        }

        // FreePlace uses proximity-based picking, not Y-only — the user can press anywhere
        // on the arm and we pick the visually-nearest widget.
        if (!TryPickChildByProximity(finger, out RectTransform picked))
            return false;

        if (debugLogInteractions)
            Debug.Log($"[PickResult] picked sibling={picked.GetSiblingIndex()} name='{picked.name}'");

        return BeginCarryWidget(picked, indexTipWorld, commitParent: null, destroyOnAbort: false, unparentDuringCarry: false);
    }

    /// <summary>
    /// Begin carrying a widget that did not originate on the arm canvas (e.g. a clone from
    /// <see cref="PossibleUIPaletteController"/>). On commit it is parented to this canvas.
    /// </summary>
    public bool BeginCarryExternal(RectTransform widget, Transform indexTipWorld, bool destroyOnAbort = true)
    {
        if (interactionMode != InteractionMode.FreePlace || widget == null || indexTipWorld == null)
            return false;

        if (LayoutRect == null)
            return false;

        return BeginCarryWidget(
            widget,
            indexTipWorld,
            commitParent: LayoutRect,
            destroyOnAbort: destroyOnAbort,
            unparentDuringCarry: true);
    }

    bool BeginCarryWidget(
        RectTransform widget,
        Transform indexTipWorld,
        Transform commitParent,
        bool destroyOnAbort,
        bool unparentDuringCarry)
    {
        if (widget == null || indexTipWorld == null)
            return false;

        draggedItem = widget;

        LayoutElement draggedLe = draggedItem.GetComponent<LayoutElement>();
        if (draggedLe == null) draggedLe = draggedItem.gameObject.AddComponent<LayoutElement>();
        draggedLe.ignoreLayout = true;

        _carrySavedParent = draggedItem.parent;
        _carryCommitParent = commitParent;
        _destroyCarriedOnAbort = destroyOnAbort;
        _carrySavedSiblingIndex = draggedItem.GetSiblingIndex();

        if (unparentDuringCarry)
        {
            // Palette clones must live under a Canvas to render while on the fingertip.
            draggedItem.SetParent(WidgetCarryCanvas.Root, worldPositionStays: true);

            float currentWorldWidth = Mathf.Abs(draggedItem.rect.width * draggedItem.lossyScale.x);
            if (currentWorldWidth > 1e-4f && carriedWorldWidthMeters > 1e-4f)
            {
                float scaleFactor = Mathf.Min(1f, carriedWorldWidthMeters / currentWorldWidth);
                draggedItem.localScale *= scaleFactor;
            }

            _carrySavedLocalScale = draggedItem.localScale;
        }
        else
        {
            _carrySavedLocalScale = draggedItem.localScale;
            draggedItem.SetAsLastSibling();

            float currentWorldWidth = Mathf.Abs(draggedItem.rect.width * draggedItem.lossyScale.x);
            if (currentWorldWidth > 1e-4f && carriedWorldWidthMeters > 1e-4f)
            {
                float scaleFactor = Mathf.Min(1f, carriedWorldWidthMeters / currentWorldWidth);
                draggedItem.localScale = _carrySavedLocalScale * scaleFactor;
            }
        }

        Quaternion tipRot = indexTipWorld.rotation;
        if (stickWidgetOriginToFingertip)
            _holdOffsetLocalInTipSpace = carryAttachOffsetTipLocal;
        else
        {
            Vector3 deltaWorld = draggedItem.position - indexTipWorld.position;
            _holdOffsetLocalInTipSpace = Quaternion.Inverse(tipRot) * deltaWorld;
        }

        _carryPickupWorldRotation = draggedItem.rotation;
        _tipFilteredPos = indexTipWorld.position;
        _tipFilteredRot = indexTipWorld.rotation;
        _tipPosSmoothVel = Vector3.zero;

        TickCarryFollowFinger(indexTipWorld);
        return true;
    }

    public void TickCarryFollowFinger(Transform indexTipWorld)
    {
        if (interactionMode != InteractionMode.FreePlace || draggedItem == null || indexTipWorld == null)
            return;

        // Live cell highlight while carrying — the user sees which cell they're about to drop into.
        if (useGridSnap && drawGridOnCanvas && armSurface != null && armSurface.IsTracking)
        {
            if (armSurface.GetClosestSurfacePoint(indexTipWorld.position, out Vector3 surfPt, out _, out _))
                HighlightFromCanvasPos(TapWorldToCanvasLocal(surfPt));
        }

        float dt = Time.deltaTime;

        if (fingerCarrySmoothTime <= Mathf.Epsilon)
        {
            _tipFilteredPos = indexTipWorld.position;
            _tipFilteredRot = indexTipWorld.rotation;
        }
        else
        {
            _tipFilteredPos = Vector3.SmoothDamp(
                _tipFilteredPos,
                indexTipWorld.position,
                ref _tipPosSmoothVel,
                fingerCarrySmoothTime,
                Mathf.Infinity,
                dt);

            float rotT = 1f - Mathf.Exp(-dt / Mathf.Max(fingerCarrySmoothTime * 1.25f, 1e-4f));
            _tipFilteredRot = Quaternion.Slerp(_tipFilteredRot, indexTipWorld.rotation, rotT);
        }

        Vector3 worldPos = _tipFilteredPos + _tipFilteredRot * _holdOffsetLocalInTipSpace;
        Quaternion blendedRot = Quaternion.Slerp(
            _carryPickupWorldRotation,
            _tipFilteredRot,
            fingerCarryRotationBlend);

        draggedItem.SetPositionAndRotation(worldPos, blendedRot);
    }

    /// <summary>Place the carried widget back onto the arm at the finger's current contact position.</summary>
    public void CommitPlace(Vector3 contactWorldPoint)
    {
        if (interactionMode != InteractionMode.FreePlace || draggedItem == null) return;

        Transform targetParent = _carryCommitParent != null ? _carryCommitParent : _carrySavedParent;
        if (targetParent != null && draggedItem.parent != targetParent)
            draggedItem.SetParent(targetParent, worldPositionStays: false);

        // Restore scale and rotation FIRST so the rect-width-based pivot offset uses
        // the widget's normal (pre-carry) size, not the shrunken carry size.
        // Palette clones use carry-canvas scale; ItemList children restore their pre-carry scale.
        draggedItem.localScale    = _carryCommitParent != null ? Vector3.one : _carrySavedLocalScale;
        draggedItem.localRotation = Quaternion.identity;

        if (LayoutRect != null && armSurface != null)
        {
            // Direct camera-projection mapping (canvas content at the tap point on cylinder).
            Vector2 rawCanvasCenter = TapWorldToCanvasLocal(contactWorldPoint);

            // Snap to grid cell.
            Vector2 desiredCanvasCenter = SnapToGrid(rawCanvasCenter, draggedItem);

            // Resize widget to fit the snapped cell (minus a small inner padding).
            if (useGridSnap && resizeToGridCell)
            {
                GetGridBounds(out _, out _, out float cellW, out float cellH);
                float innerW = Mathf.Max(cellW - 2f * cellInnerPadding, 1f);
                float innerH = Mathf.Max(cellH - 2f * cellInnerPadding, 1f);
                draggedItem.sizeDelta = new Vector2(innerW, innerH);
            }

            // Pivot compensation: localPosition places the widget's pivot, not its center.
            Vector2 pivotOffsetLocal = new Vector2(
                (draggedItem.pivot.x - 0.5f) * draggedItem.rect.width,
                (draggedItem.pivot.y - 0.5f) * draggedItem.rect.height);

            Vector3 newLocalPos = new Vector3(
                desiredCanvasCenter.x + pivotOffsetLocal.x,
                desiredCanvasCenter.y + pivotOffsetLocal.y,
                0f);

            draggedItem.localPosition = newLocalPos;

            // Update the visible cell highlight to point at the snapped cell.
            NearestCellIndex(desiredCanvasCenter, out int hCol, out int hRow);
            SetHighlightCell(hCol, hRow);

            if (debugLogInteractions)
                Debug.Log($"[CommitPlace] contact={contactWorldPoint:F3} " +
                          $"raw={rawCanvasCenter:F2} snapped={desiredCanvasCenter:F2} " +
                          $"pivotOffset={pivotOffsetLocal:F2} localPos={newLocalPos:F2} " +
                          $"extent={EffectiveCanvasRect()}");
        }

        // Sibling order: keep on top so the placed widget renders above its siblings.
        // (We intentionally do NOT restore the old sibling index here — once placed
        //  freely the user expects it to stay where they put it visually.)
        draggedItem.SetAsLastSibling();

        if (!keepIgnoreLayoutAfterFreePlace)
        {
            LayoutElement le = draggedItem.GetComponent<LayoutElement>();
            if (le != null) le.ignoreLayout = false;
        }

        draggedItem = null;
        ResetCarryFingerMetadata();
        smoothedSanitizedUv = Vector2.zero;
    }

    void ResetCarryFingerMetadata()
    {
        _carrySavedParent = null;
        _carryCommitParent = null;
        _destroyCarriedOnAbort = false;
        _carrySavedSiblingIndex = 0;
        _tipPosSmoothVel = Vector3.zero;
    }

    public void AbortCarryWithoutPlace()
    {
        if (draggedItem == null) return;

        RectTransform carried = draggedItem;
        bool destroyClone = _destroyCarriedOnAbort;

        LayoutElement le = carried.GetComponent<LayoutElement>();
        if (le != null) le.ignoreLayout = false;

        if (destroyClone)
        {
            Destroy(carried.gameObject);
        }
        else if (_carrySavedParent != null)
        {
            carried.localScale = _carrySavedLocalScale;
            carried.localRotation = Quaternion.identity;
            carried.SetParent(_carrySavedParent, false);

            int maxCi = Mathf.Max(0, _carrySavedParent.childCount - 1);
            carried.SetSiblingIndex(Mathf.Clamp(_carrySavedSiblingIndex, 0, maxCi));
        }
        else
        {
            carried.localScale = _carrySavedLocalScale;
        }

        draggedItem = null;
        smoothedFingerLocal = Vector2.zero;
        smoothedSanitizedUv = Vector2.zero;
        ResetCarryFingerMetadata();
    }

    /// <summary>Destroy the widget currently carried on the fingertip (palette delete zone).</summary>
    public void DestroyCarriedItem()
    {
        if (draggedItem == null) return;

        Destroy(draggedItem.gameObject);
        draggedItem = null;
        smoothedFingerLocal = Vector2.zero;
        smoothedSanitizedUv = Vector2.zero;
        ResetCarryFingerMetadata();
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGridGizmos || !useGridSnap) return;
        if (LayoutRect == null) return;

        GetGridBounds(out float xMin, out float yMin, out float cellW, out float cellH);

        Color cellColor = new Color(0.2f, 0.8f, 1f, 0.45f);
        Color borderColor = new Color(0.05f, 0.4f, 0.7f, 0.9f);

        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = LayoutRect.localToWorldMatrix;

        for (int row = 0; row < gridRows; row++)
        {
            for (int col = 0; col < gridColumns; col++)
            {
                Vector2 c = new Vector2(
                    xMin + cellW * (col + 0.5f),
                    yMin + cellH * (row + 0.5f));
                Gizmos.color = cellColor;
                Gizmos.DrawWireCube(new Vector3(c.x, c.y, 0f), new Vector3(cellW * 0.95f, cellH * 0.95f, 0f));
            }
        }

        Gizmos.color = borderColor;
        Rect r = EffectiveCanvasRect();
        Vector3 bl = new Vector3(r.xMin, r.yMin, 0);
        Vector3 br = new Vector3(r.xMax, r.yMin, 0);
        Vector3 tl = new Vector3(r.xMin, r.yMax, 0);
        Vector3 tr = new Vector3(r.xMax, r.yMax, 0);
        Gizmos.DrawLine(bl, br); Gizmos.DrawLine(br, tr);
        Gizmos.DrawLine(tr, tl); Gizmos.DrawLine(tl, bl);

        Gizmos.matrix = prev;
    }
}
