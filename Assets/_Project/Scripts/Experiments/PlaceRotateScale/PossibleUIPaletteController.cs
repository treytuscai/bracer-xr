using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating world-space palette of UI templates in front of the user.
/// Picking a template clones it, attaches it to the fingertip via
/// <see cref="ArmLayoutController.BeginCarryExternal"/>, and placement on the
/// forearm adds it to ItemList. A delete icon on the far right removes carried items.
/// </summary>
[DefaultExecutionOrder(90)]
public class PossibleUIPaletteController : MonoBehaviour
{
    public enum PaletteTouchState
    {
        None,
        Hover,
        Press
    }

    [Header("References")]
    [Tooltip("Optional head anchor — only used when Follow Head is enabled.")]
    public Transform headAnchor;

    [Tooltip("Root rect whose children are the selectable UI templates.")]
    public RectTransform paletteRect;

    [Tooltip("ItemList controller — receives the carried clone and owns arm placement.")]
    public ArmLayoutController itemListController;

    [Tooltip("Optional alternate placement handler (e.g. RevisedGridPlacementController). " +
             "When set, palette carry/placement uses this instead of itemListController.")]
    public MonoBehaviour widgetPlacementOverride;

    IForearmWidgetPlacement Placement =>
        ResolvePlacement(widgetPlacementOverride) ?? itemListController;

    static IForearmWidgetPlacement ResolvePlacement(MonoBehaviour behaviour)
    {
        if (behaviour == null) return null;
        if (behaviour is IForearmWidgetPlacement direct) return direct;

        // Allow dragging the parent GameObject (e.g. SurfaceManager) instead of the
        // specific RevisedGridPlacementController component.
        foreach (var mb in behaviour.GetComponents<MonoBehaviour>())
        {
            if (mb is IForearmWidgetPlacement found)
                return found;
        }

        return null;
    }

    [Header("Delete")]
    [Tooltip("Optional trash-can sprite. If unset, a built-in X icon is shown at runtime.")]
    public Sprite deleteIconSprite;

    public Vector2 deleteIconSize = new Vector2(56f, 56f);

    [Tooltip("Highlight color when hovering the delete icon while carrying an item.")]
    public Color deleteHoverHighlightColor = new Color(1f, 0.35f, 0.35f, 1f);

    [Tooltip("Label shown above the delete icon.")]
    public string deleteLabelText = "Trash";

    public int deleteTitleFontSize = 16;

    [Tooltip("Space between the Trash label and delete icon.")]
    [Min(0f)] public float deleteColumnSpacing = 4f;

    [Tooltip("Width of the vertical separator bar beside the delete zone.")]
    [Min(1f)] public float deleteSeparatorWidth = 2f;

    public Color deleteSeparatorColor = new Color(1f, 1f, 1f, 0.85f);

    [Tooltip("Space between the separator bar and delete column.")]
    [Min(0f)] public float deleteSeparatorPadding = 8f;

    [Header("Clear")]
    [Tooltip("Optional clear-all icon. If unset, a built-in eraser-style label is shown.")]
    public Sprite clearIconSprite;

    public Vector2 clearIconSize = new Vector2(56f, 56f);

    [Tooltip("Highlight color when hovering the clear icon.")]
    public Color clearHoverHighlightColor = new Color(0.35f, 0.75f, 1f, 1f);

    [Tooltip("Label shown above the clear icon.")]
    public string clearLabelText = "Clear";

    public int clearTitleFontSize = 16;

    [Tooltip("Space between the Clear label and clear icon.")]
    [Min(0f)] public float clearColumnSpacing = 4f;

    [Header("Grid Edit")]
    [Tooltip("Optional — enables the Edit Image column and transform sliders.")]
    public RevisedGridEditController gridEditController;

    public string editLabelText = "Edit Image\n(Resize and rotate)";
    public Vector2 editIconSize = new Vector2(56f, 56f);
    public int editTitleFontSize = 13;
    [Min(0f)] public float editColumnSpacing = 4f;
    public Color editHoverHighlightColor = new Color(0.35f, 0.95f, 0.45f, 1f);

    [Header("Placement")]
    [Tooltip("If true, the palette follows the head each frame. If false, it is anchored once in world space.")]
    public bool followHead = false;

    [Tooltip("Distance in meters in front of the head anchor.")]
    [Min(0.2f)] public float distanceMeters = 0.55f;

    [Tooltip("Vertical offset from the head anchor (meters). Use 0 for eye height.")]
    public float heightOffsetMeters = 0f;

    [Tooltip("Head world Y must reach this before the palette is anchored (avoids floor-level XR startup poses).")]
    [Min(0.5f)] public float minHeadHeightToAnchor = 1.0f;

    [Tooltip("Reject head poses above this Y when anchoring (avoids bad tracking spikes).")]
    [Min(1f)] public float maxHeadHeightToAnchor = 2.2f;

    [Tooltip("Frames to wait for head tracking before using fallback eye height.")]
    [Min(1)] public int maxAnchorWaitFrames = 300;

    [Tooltip("Fallback world Y (meters) if head tracking is not ready in time.")]
    [Min(0.5f)] public float fallbackEyeHeightMeters = 1.45f;

    [Tooltip("Lateral offset from the head anchor (meters). Positive moves the panel to the user's right.")]
    public float lateralOffsetMeters = 0.18f;

    [Tooltip("Yaw added after the panel faces the user. Negative angles the panel slightly to the user's left.")]
    public float panelFaceYawDegrees = -12f;

    [Tooltip("If true, the palette only follows head yaw when placement updates.")]
    public bool yawOnlyBillboard = true;

    [Tooltip("World width of the palette panel in meters.")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.28f;

    [Header("Grid")]
    [Tooltip("If true, arranges template children in a grid at runtime.")]
    public bool useGridLayout = true;

    [Range(1, 8)] public int gridColumns = 3;

    public Vector2 gridCellSize = new Vector2(70f, 70f);
    public Vector2 gridSpacing = new Vector2(12f, 12f);

    [Tooltip("Inner padding around the palette grid (canvas-local units).")]
    [Min(0)] public int gridPaddingLeft = 12;
    [Min(0)] public int gridPaddingRight = 12;
    [Min(0)] public int gridPaddingTop = 12;
    [Min(0)] public int gridPaddingBottom = 12;

    [Tooltip("Space between the template grid and the delete icon.")]
    [Min(0f)] public float deleteSeparatorSpacing = 16f;

    [Header("Touch")]
    [Tooltip("Finger within this distance (meters) of the palette plane counts as hovering.")]
    [Min(0.001f)] public float hoverDistanceMeters = 0.035f;

    [Tooltip("Finger within this distance (meters) of the palette plane counts as pressing.")]
    [Min(0.001f)] public float pressDistanceMeters = 0.018f;

    [Tooltip("Extra padding (canvas-local units) when picking a template.")]
    [Min(0f)] public float pickPaddingLocal = 8f;

    [Header("Hover highlight")]
    [Tooltip("Show a green outline on the template under the fingertip before pickup.")]
    public bool showHoverOutline = true;

    public Color hoverOutlineColor = new Color(0.25f, 0.95f, 0.35f, 1f);
    public Vector2 hoverOutlineDistance = new Vector2(5f, -5f);

    [Header("Debug")]
    public bool debugLogPalette = true;

    [Tooltip("If true, world anchoring is skipped until cleared (experiment scenes may set this during setup).")]
    public bool blockWorldAnchor;

    public PaletteTouchState TouchState { get; private set; }
    public bool IsFingerOverPalette => TouchState != PaletteTouchState.None;

    RectTransform _templateContainer;
    RectTransform _clearZone;
    RectTransform _clearSeparator;
    RectTransform _clearTitle;
    RectTransform _clearTarget;
    RectTransform _editZone;
    RectTransform _editSeparator;
    RectTransform _editTitle;
    RectTransform _editTarget;
    RectTransform _deleteZone;
    RectTransform _deleteSeparator;
    RectTransform _deleteTitle;
    RectTransform _deleteTarget;
    HorizontalLayoutGroup _rowLayout;
    GridLayoutGroup _gridLayout;
    ContentSizeFitter _templateSizeFitter;
    ContentSizeFitter _paletteSizeFitter;
    RectTransform _hoveredTemplate;
    Outline _clearOutline;
    Outline _editOutline;
    Outline _deleteOutline;
    Vector3 _lastFingerWorld;
    bool _worldAnchorApplied;
    bool _panelLayoutLocked;
    bool _editPressActive;
    int _anchorWaitFrames;
    int _stableHeadFrames;
    float _lastMeasuredHeadY;

    void Awake()
    {
        if (paletteRect == null)
            paletteRect = transform as RectTransform;

        ResolveHeadAnchor();
        EnsurePaletteLayout();
        ApplyPanelScale();
    }

    void LateUpdate()
    {
        ResolveHeadAnchor();

        if (!followHead && !_worldAnchorApplied)
            AnchorPaletteInWorldOnce();

        if (followHead)
            ApplyHeadPlacement();
        else if (_worldAnchorApplied && !_panelLayoutLocked)
            FinalizeAnchoredPaletteLayout();
        else if (!_worldAnchorApplied)
            RefreshPaletteLayout();

        if (gridEditController != null && _editZone == null)
            EnsureEditZones();

        UpdateClearHighlight(_lastFingerWorld);
        UpdateEditHighlight(_lastFingerWorld);
        UpdateDeleteHighlight(_lastFingerWorld);
    }

    void RefreshPaletteLayout()
    {
        EnsureRowChildOrder();
        LayoutEditZoneManual();
        LayoutClearZoneManual();
        LayoutDeleteZoneManual();
        ApplyPanelScale();
    }

    void FinalizeAnchoredPaletteLayout()
    {
        EnsureRowChildOrder();
        LayoutEditZoneManual();
        LayoutClearZoneManual();
        LayoutDeleteZoneManual();
        ApplyPanelScale();
        _panelLayoutLocked = true;
    }

    /// <summary>Creates the combined Edit Image column using the same layout as Clear/Trash.</summary>
    public void EnsureEditZones()
    {
        if (paletteRect == null) return;

        EnsureEditZone();
        DisableLegacyRotateZone();
        EnsureRowChildOrder();
        LayoutEditZoneManual();
    }

    /// <summary>Wires edit-mode UI and rebuilds palette layout to include the Edit column.</summary>
    public void BindGridEditController(RevisedGridEditController edit)
    {
        if (edit == null) return;
        gridEditController = edit;
        EnsureEditZones();
        NotifyLayoutChanged();
    }

    void ResolveHeadAnchor()
    {
        if (headAnchor != null)
            return;

        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            headAnchor = rig.centerEyeAnchor;
    }

    void AnchorPaletteInWorldOnce()
    {
        if (blockWorldAnchor || _worldAnchorApplied || paletteRect == null || headAnchor == null)
            return;

        float measuredHeadY = headAnchor.position.y;
        bool headHeightValid = measuredHeadY >= minHeadHeightToAnchor
            && measuredHeadY <= maxHeadHeightToAnchor;

        if (!headHeightValid)
        {
            _anchorWaitFrames++;
            _stableHeadFrames = 0;
            if (_anchorWaitFrames < maxAnchorWaitFrames)
                return;

            if (debugLogPalette)
                Debug.LogWarning(
                    $"[Palette] Head tracking not ready after {maxAnchorWaitFrames} frames; anchoring with fallback eye height {fallbackEyeHeightMeters:F2}m.");
        }
        else
        {
            if (Mathf.Abs(measuredHeadY - _lastMeasuredHeadY) > 0.04f)
                _stableHeadFrames = 0;
            else
                _stableHeadFrames++;

            _lastMeasuredHeadY = measuredHeadY;

            // Wait for a few stable tracking frames so we do not lock a startup spike pose.
            if (_stableHeadFrames < 2)
                return;
        }

        _worldAnchorApplied = true;
        _panelLayoutLocked = false;

        if (paletteRect.parent != null)
            paletteRect.SetParent(null, true);

        paletteRect.pivot = new Vector2(0.5f, 0.5f);
        paletteRect.anchorMin = paletteRect.anchorMax = new Vector2(0.5f, 0.5f);

        ApplyHeadPlacement(useFallbackHeight: !headHeightValid);
        FinalizeAnchoredPaletteLayout();

        if (debugLogPalette)
            Debug.Log(
                $"[Palette] Anchored in world space at {paletteRect.position} (head Y={measuredHeadY:F2}, stableFrames={_stableHeadFrames}).");
    }

    void ApplyHeadPlacement(bool useFallbackHeight = false)
    {
        if (headAnchor == null || paletteRect == null)
            return;

        Vector3 flatForward = headAnchor.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 1e-6f)
            flatForward = Vector3.forward;

        flatForward.Normalize();
        Vector3 flatRight = headAnchor.right;
        flatRight.y = 0f;
        if (flatRight.sqrMagnitude < 1e-6f)
            flatRight = Vector3.Cross(Vector3.up, flatForward).normalized;
        else
            flatRight.Normalize();

        Vector3 anchorPos = headAnchor.position;
        Vector3 targetPos =
            anchorPos
            + flatForward * distanceMeters
            + flatRight * lateralOffsetMeters;

        targetPos.y = ResolveAnchorEyeHeight(anchorPos.y, useFallbackHeight) + heightOffsetMeters;
        paletteRect.SetPositionAndRotation(targetPos, ComputeFacingRotation(anchorPos, targetPos, flatForward));
    }

    Quaternion ComputeFacingRotation(Vector3 anchorPos, Vector3 targetPos, Vector3 flatForward)
    {
        Vector3 faceDir = anchorPos - targetPos;
        faceDir.y = 0f;
        if (faceDir.sqrMagnitude < 1e-6f)
            faceDir = -flatForward;

        Quaternion faceUser = yawOnlyBillboard
            ? Quaternion.LookRotation(faceDir.normalized, Vector3.up)
            : Quaternion.LookRotation((anchorPos - targetPos).normalized, Vector3.up);

        if (Mathf.Abs(panelFaceYawDegrees) > 0.01f)
            faceUser = Quaternion.AngleAxis(panelFaceYawDegrees, Vector3.up) * faceUser;

        return faceUser;
    }

    float ResolveAnchorEyeHeight(float measuredHeadY, bool useFallbackHeight)
    {
        if (useFallbackHeight)
            return fallbackEyeHeightMeters;

        if (measuredHeadY < minHeadHeightToAnchor || measuredHeadY > maxHeadHeightToAnchor)
            return fallbackEyeHeightMeters;

        return measuredHeadY;
    }

    void EnsurePaletteLayout()
    {
        if (paletteRect == null)
            return;

        _rowLayout = paletteRect.GetComponent<HorizontalLayoutGroup>();
        if (_rowLayout == null)
            _rowLayout = paletteRect.gameObject.AddComponent<HorizontalLayoutGroup>();
        _rowLayout.spacing = deleteSeparatorSpacing;
        _rowLayout.childAlignment = TextAnchor.MiddleLeft;
        _rowLayout.childControlWidth = false;
        _rowLayout.childControlHeight = false;
        _rowLayout.childForceExpandWidth = false;
        _rowLayout.childForceExpandHeight = false;
        _rowLayout.reverseArrangement = false;
        _rowLayout.padding = new RectOffset(0, 0, 0, 0);

        _paletteSizeFitter = paletteRect.GetComponent<ContentSizeFitter>();
        if (_paletteSizeFitter == null)
            _paletteSizeFitter = paletteRect.gameObject.AddComponent<ContentSizeFitter>();
        _paletteSizeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        _paletteSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _templateContainer = paletteRect.Find("TemplateContainer") as RectTransform;
        if (_templateContainer == null)
        {
            var containerGo = new GameObject("TemplateContainer",
                typeof(RectTransform), typeof(GridLayoutGroup), typeof(ContentSizeFitter));
            _templateContainer = containerGo.GetComponent<RectTransform>();
            _templateContainer.SetParent(paletteRect, false);
            _templateContainer.localScale = Vector3.one;
        }

        MigrateTemplatesIntoContainer();
        EnsureGridLayout();
        EnsureClearZone();
        EnsureDeleteZone();
        EnsureRowChildOrder();
    }

    void MigrateTemplatesIntoContainer()
    {
        for (int i = paletteRect.childCount - 1; i >= 0; i--)
        {
            Transform child = paletteRect.GetChild(i);
            if (child == _templateContainer || child == _clearZone || child == _deleteZone || child == _deleteTarget ||
                child == _editZone)
                continue;
            if (child.GetComponent<PaletteDeleteTarget>() != null ||
                child.GetComponent<PaletteClearTarget>() != null ||
                child.GetComponent<PaletteEditTarget>() != null ||
                child.GetComponent<PaletteResizeTarget>() != null ||
                child.GetComponent<PaletteRotateTarget>() != null)
                continue;

            child.SetParent(_templateContainer, false);

            if (child.GetComponent<Graphic>() != null && child.GetComponent<PaletteTemplateItem>() == null)
                child.gameObject.AddComponent<PaletteTemplateItem>();
        }
    }

    void EnsureGridLayout()
    {
        if (!useGridLayout || _templateContainer == null)
            return;

        _gridLayout = _templateContainer.GetComponent<GridLayoutGroup>();
        if (_gridLayout == null)
            _gridLayout = _templateContainer.gameObject.AddComponent<GridLayoutGroup>();

        _gridLayout.cellSize = gridCellSize;
        _gridLayout.spacing = gridSpacing;
        _gridLayout.padding = new RectOffset(
            gridPaddingLeft, gridPaddingRight, gridPaddingTop, gridPaddingBottom);
        _gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        _gridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
        _gridLayout.childAlignment = TextAnchor.UpperCenter;
        _gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        _gridLayout.constraintCount = Mathf.Max(1, gridColumns);

        _templateSizeFitter = _templateContainer.GetComponent<ContentSizeFitter>();
        if (_templateSizeFitter == null)
            _templateSizeFitter = _templateContainer.gameObject.AddComponent<ContentSizeFitter>();
        _templateSizeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        _templateSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    void EnsureClearZone()
    {
        _clearZone = paletteRect.Find("ClearZone") as RectTransform;
        if (_clearZone == null)
        {
            var zoneGo = new GameObject("ClearZone", typeof(RectTransform), typeof(LayoutElement));
            _clearZone = zoneGo.GetComponent<RectTransform>();
            _clearZone.SetParent(paletteRect, false);
        }

        RemoveLegacyDeleteLayoutComponents(_clearZone);

        PaletteClearTarget zoneMarker = _clearZone.GetComponent<PaletteClearTarget>();
        if (zoneMarker != null)
            Destroy(zoneMarker);

        EnsureClearSeparator();
        EnsureClearTitle();
        EnsureClearTarget();
        LayoutClearZoneManual();
    }

    void EnsureClearSeparator()
    {
        _clearSeparator = _clearZone.Find("ClearSeparator") as RectTransform;
        if (_clearSeparator == null)
        {
            var separatorGo = new GameObject("ClearSeparator",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _clearSeparator = separatorGo.GetComponent<RectTransform>();
            _clearSeparator.SetParent(_clearZone, false);
        }

        RemoveLegacyDeleteLayoutComponents(_clearSeparator);

        var separatorImage = _clearSeparator.GetComponent<Image>();
        separatorImage.sprite = null;
        separatorImage.color = deleteSeparatorColor;
        separatorImage.raycastTarget = false;
    }

    void EnsureClearTitle()
    {
        _clearTitle = _clearZone.Find("ClearTitle") as RectTransform;
        if (_clearTitle == null)
        {
            var titleGo = new GameObject("ClearTitle",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            _clearTitle = titleGo.GetComponent<RectTransform>();
            _clearTitle.SetParent(_clearZone, false);
        }

        var titleText = _clearTitle.GetComponent<Text>();
        titleText.text = clearLabelText;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = clearTitleFontSize;
        titleText.raycastTarget = false;
        titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        titleText.verticalOverflow = VerticalWrapMode.Overflow;
        _clearTitle.localScale = Vector3.one;
    }

    void EnsureClearTarget()
    {
        if (_clearTarget == null)
            _clearTarget = paletteRect.Find("ClearIcon") as RectTransform;
        if (_clearTarget == null && _clearZone != null)
            _clearTarget = _clearZone.Find("ClearIcon") as RectTransform;

        if (_clearTarget == null)
        {
            var clearGo = new GameObject("ClearIcon",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _clearTarget = clearGo.GetComponent<RectTransform>();
            clearGo.AddComponent<PaletteClearTarget>();
        }

        if (_clearZone != null && _clearTarget.parent != _clearZone)
            _clearTarget.SetParent(_clearZone, false);

        if (_clearTarget.GetComponent<PaletteClearTarget>() == null)
            _clearTarget.gameObject.AddComponent<PaletteClearTarget>();

        var image = _clearTarget.GetComponent<Image>();
        if (image == null)
            image = _clearTarget.gameObject.AddComponent<Image>();
        ApplyClearIconVisual(image);
        image.preserveAspect = true;
        image.raycastTarget = false;

        LayoutElement legacyLayout = _clearTarget.GetComponent<LayoutElement>();
        if (legacyLayout != null)
            Destroy(legacyLayout);
    }

    void ApplyClearIconVisual(Image image)
    {
        var label = _clearTarget.Find("ClearIconLabel") as RectTransform;

        if (clearIconSprite != null)
        {
            if (label != null)
                label.gameObject.SetActive(false);

            image.sprite = clearIconSprite;
            image.color = Color.white;
            return;
        }

        image.sprite = null;
        image.color = new Color(0.25f, 0.55f, 0.95f, 0.85f);

        if (label == null)
        {
            var labelGo = new GameObject("ClearIconLabel",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            label = labelGo.GetComponent<RectTransform>();
            label.SetParent(_clearTarget, false);
            label.anchorMin = Vector2.zero;
            label.anchorMax = Vector2.one;
            label.offsetMin = Vector2.zero;
            label.offsetMax = Vector2.zero;
        }
        else
        {
            label.gameObject.SetActive(true);
        }

        var text = label.GetComponent<Text>();
        text.text = "\u232B";
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = Mathf.RoundToInt(Mathf.Min(clearIconSize.x, clearIconSize.y) * 0.55f);
        text.raycastTarget = false;
    }

    void LayoutClearZoneManual()
    {
        LayoutActionZoneManual(
            _clearZone, _clearSeparator, _clearTitle, _clearTarget,
            clearIconSize, clearTitleFontSize, clearColumnSpacing);
    }

    void EnsureEditZone()
    {
        _editZone = paletteRect.Find("EditZone") as RectTransform;
        if (_editZone == null)
            _editZone = paletteRect.Find("ResizeZone") as RectTransform;
        if (_editZone == null)
        {
            var zoneGo = new GameObject("EditZone", typeof(RectTransform), typeof(LayoutElement));
            _editZone = zoneGo.GetComponent<RectTransform>();
            _editZone.SetParent(paletteRect, false);
        }

        RemoveLegacyDeleteLayoutComponents(_editZone);
        EnsureEditSeparator(_editZone, ref _editSeparator, "EditSeparator");
        EnsureEditTitle(_editZone, ref _editTitle, "EditTitle", editLabelText, editTitleFontSize);
        EnsureEditTarget(_editZone, ref _editTarget, "EditIcon", typeof(PaletteEditTarget),
            new Color(0.25f, 0.85f, 0.45f, 0.85f), "\u270E");
    }

    void DisableLegacyRotateZone()
    {
        var legacyRotate = paletteRect.Find("RotateZone");
        if (legacyRotate != null)
            legacyRotate.gameObject.SetActive(false);
        var legacyResize = paletteRect.Find("ResizeZone");
        if (legacyResize != null && legacyResize != _editZone)
            legacyResize.gameObject.SetActive(false);
    }

    void EnsureEditSeparator(RectTransform zone, ref RectTransform separator, string name)
    {
        separator = zone.Find(name) as RectTransform;
        if (separator == null)
        {
            var separatorGo = new GameObject(name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            separator = separatorGo.GetComponent<RectTransform>();
            separator.SetParent(zone, false);
        }

        RemoveLegacyDeleteLayoutComponents(separator);
        var separatorImage = separator.GetComponent<Image>();
        separatorImage.sprite = null;
        separatorImage.color = deleteSeparatorColor;
        separatorImage.raycastTarget = false;
    }

    void EnsureEditTitle(RectTransform zone, ref RectTransform title, string name, string label, int fontSize)
    {
        title = zone.Find(name) as RectTransform;
        if (title == null)
        {
            var titleGo = new GameObject(name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            title = titleGo.GetComponent<RectTransform>();
            title.SetParent(zone, false);
        }

        var titleText = title.GetComponent<Text>();
        titleText.text = label;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = fontSize;
        titleText.raycastTarget = false;
        titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        titleText.verticalOverflow = VerticalWrapMode.Overflow;
        title.localScale = Vector3.one;
    }

    void EnsureEditTarget(RectTransform zone, ref RectTransform target, string name,
        System.Type markerType, Color fallbackColor, string fallbackGlyph)
    {
        target = zone.Find(name) as RectTransform;
        if (target == null)
        {
            var iconGo = new GameObject(name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            target = iconGo.GetComponent<RectTransform>();
            iconGo.AddComponent(markerType);
        }

        if (target.parent != zone)
            target.SetParent(zone, false);

        if (target.GetComponent(markerType) == null)
            target.gameObject.AddComponent(markerType);

        var image = target.GetComponent<Image>();
        if (image == null)
            image = target.gameObject.AddComponent<Image>();
        image.sprite = null;
        image.color = fallbackColor;
        image.preserveAspect = true;
        image.raycastTarget = false;

        var label = target.Find("IconLabel") as RectTransform;
        if (label == null)
        {
            var labelGo = new GameObject("IconLabel",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            label = labelGo.GetComponent<RectTransform>();
            label.SetParent(target, false);
            label.anchorMin = Vector2.zero;
            label.anchorMax = Vector2.one;
            label.offsetMin = Vector2.zero;
            label.offsetMax = Vector2.zero;
        }

        var text = label.GetComponent<Text>();
        text.text = fallbackGlyph;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = Mathf.RoundToInt(Mathf.Min(editIconSize.x, editIconSize.y) * 0.55f);
        text.raycastTarget = false;

        LayoutElement legacyLayout = target.GetComponent<LayoutElement>();
        if (legacyLayout != null)
            Destroy(legacyLayout);
    }

    void LayoutEditZoneManual()
    {
        if (_editZone == null) return;
        LayoutActionZoneManual(
            _editZone, _editSeparator, _editTitle, _editTarget,
            editIconSize, editTitleFontSize, editColumnSpacing);

        if (_editTitle == null) return;
        int lines = Mathf.Max(1, editLabelText.Split('\n').Length);
        float titleHeight = editTitleFontSize * lines + 4f * lines;
        var titleSize = _editTitle.sizeDelta;
        _editTitle.sizeDelta = new Vector2(Mathf.Max(titleSize.x, 96f), titleHeight);
    }

    void EnsureDeleteZone()
    {
        _deleteZone = paletteRect.Find("DeleteZone") as RectTransform;
        if (_deleteZone == null)
        {
            var zoneGo = new GameObject("DeleteZone", typeof(RectTransform), typeof(LayoutElement));
            _deleteZone = zoneGo.GetComponent<RectTransform>();
            _deleteZone.SetParent(paletteRect, false);
        }

        RemoveLegacyDeleteLayoutComponents(_deleteZone);

        PaletteDeleteTarget zoneMarker = _deleteZone.GetComponent<PaletteDeleteTarget>();
        if (zoneMarker != null)
            Destroy(zoneMarker);

        Transform legacyColumn = _deleteZone.Find("DeleteColumn");
        if (legacyColumn != null)
        {
            for (int i = legacyColumn.childCount - 1; i >= 0; i--)
                legacyColumn.GetChild(i).SetParent(_deleteZone, false);

            Destroy(legacyColumn.gameObject);
        }

        EnsureDeleteSeparator();
        EnsureDeleteTitle();
        EnsureDeleteTarget();
        LayoutDeleteZoneManual();
    }

    static void RemoveLegacyDeleteLayoutComponents(RectTransform root)
    {
        HorizontalLayoutGroup horizontal = root.GetComponent<HorizontalLayoutGroup>();
        if (horizontal != null)
            Destroy(horizontal);

        VerticalLayoutGroup vertical = root.GetComponent<VerticalLayoutGroup>();
        if (vertical != null)
            Destroy(vertical);
    }

    void EnsureDeleteSeparator()
    {
        _deleteSeparator = _deleteZone.Find("DeleteSeparator") as RectTransform;
        if (_deleteSeparator == null)
        {
            var separatorGo = new GameObject("DeleteSeparator",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _deleteSeparator = separatorGo.GetComponent<RectTransform>();
            _deleteSeparator.SetParent(_deleteZone, false);
        }

        RemoveLegacyDeleteLayoutComponents(_deleteSeparator);

        var separatorImage = _deleteSeparator.GetComponent<Image>();
        separatorImage.sprite = null;
        separatorImage.color = deleteSeparatorColor;
        separatorImage.raycastTarget = false;
    }

    void EnsureDeleteTitle()
    {
        _deleteTitle = _deleteZone.Find("DeleteTitle") as RectTransform;
        if (_deleteTitle == null)
        {
            var titleGo = new GameObject("DeleteTitle",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            _deleteTitle = titleGo.GetComponent<RectTransform>();
            _deleteTitle.SetParent(_deleteZone, false);
        }

        var titleText = _deleteTitle.GetComponent<Text>();
        titleText.text = deleteLabelText;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = deleteTitleFontSize;
        titleText.raycastTarget = false;
        titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        titleText.verticalOverflow = VerticalWrapMode.Overflow;
        _deleteTitle.localScale = Vector3.one;
    }

    void LayoutDeleteZoneManual()
    {
        LayoutActionZoneManual(
            _deleteZone, _deleteSeparator, _deleteTitle, _deleteTarget,
            deleteIconSize, deleteTitleFontSize, deleteColumnSpacing);
    }

    void LayoutActionZoneManual(
        RectTransform zone,
        RectTransform separator,
        RectTransform title,
        RectTransform target,
        Vector2 iconSize,
        int titleFontSize,
        float columnSpacing)
    {
        if (zone == null || separator == null || title == null || target == null)
            return;

        float columnWidth = Mathf.Max(iconSize.x, 48f);
        float titleHeight = titleFontSize + 4f;
        float columnHeight = titleHeight + columnSpacing + iconSize.y;
        float zoneWidth = deleteSeparatorWidth + deleteSeparatorPadding + columnWidth;
        float columnCenterX = deleteSeparatorWidth + deleteSeparatorPadding + columnWidth * 0.5f;

        var zoneLayout = zone.GetComponent<LayoutElement>();
        if (zoneLayout == null)
            zoneLayout = zone.gameObject.AddComponent<LayoutElement>();
        zoneLayout.minWidth = zoneWidth;
        zoneLayout.preferredWidth = zoneWidth;
        zoneLayout.minHeight = columnHeight;
        zoneLayout.preferredHeight = columnHeight;
        zoneLayout.flexibleWidth = 0f;
        zoneLayout.flexibleHeight = 0f;

        separator.SetAsFirstSibling();
        title.SetSiblingIndex(1);
        target.SetAsLastSibling();

        separator.anchorMin = new Vector2(0f, 0f);
        separator.anchorMax = new Vector2(0f, 1f);
        separator.pivot = new Vector2(0f, 0.5f);
        separator.sizeDelta = new Vector2(deleteSeparatorWidth, 0f);
        separator.anchoredPosition = Vector2.zero;

        title.anchorMin = new Vector2(0f, 1f);
        title.anchorMax = new Vector2(0f, 1f);
        title.pivot = new Vector2(0.5f, 1f);
        title.sizeDelta = new Vector2(columnWidth, titleHeight);
        title.anchoredPosition = new Vector2(columnCenterX, 0f);

        target.anchorMin = new Vector2(0f, 1f);
        target.anchorMax = new Vector2(0f, 1f);
        target.pivot = new Vector2(0.5f, 1f);
        target.sizeDelta = iconSize;
        target.anchoredPosition = new Vector2(columnCenterX, -(titleHeight + columnSpacing));
    }

    void EnsureDeleteTarget()
    {
        if (_deleteTarget == null)
            _deleteTarget = paletteRect.Find("DeleteIcon") as RectTransform;
        if (_deleteTarget == null && _deleteZone != null)
            _deleteTarget = _deleteZone.Find("DeleteIcon") as RectTransform;

        if (_deleteTarget == null)
        {
            var deleteGo = new GameObject("DeleteIcon",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _deleteTarget = deleteGo.GetComponent<RectTransform>();
            deleteGo.AddComponent<PaletteDeleteTarget>();
        }

        if (_deleteZone != null && _deleteTarget.parent != _deleteZone)
            _deleteTarget.SetParent(_deleteZone, false);

        if (_deleteTarget.GetComponent<PaletteDeleteTarget>() == null)
            _deleteTarget.gameObject.AddComponent<PaletteDeleteTarget>();

        var image = _deleteTarget.GetComponent<Image>();
        if (image == null)
            image = _deleteTarget.gameObject.AddComponent<Image>();
        ApplyDeleteIconVisual(image);
        image.preserveAspect = true;
        image.raycastTarget = false;

        LayoutElement legacyLayout = _deleteTarget.GetComponent<LayoutElement>();
        if (legacyLayout != null)
            Destroy(legacyLayout);
    }

    void ApplyDeleteIconVisual(Image image)
    {
        var label = _deleteTarget.Find("DeleteIconLabel") as RectTransform;

        if (deleteIconSprite != null)
        {
            if (label != null)
                label.gameObject.SetActive(false);

            image.sprite = deleteIconSprite;
            image.color = Color.white;
            return;
        }

        image.sprite = null;
        image.color = new Color(0.85f, 0.2f, 0.2f, 0.85f);

        if (label == null)
        {
            var labelGo = new GameObject("DeleteIconLabel",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            label = labelGo.GetComponent<RectTransform>();
            label.SetParent(_deleteTarget, false);
            label.anchorMin = Vector2.zero;
            label.anchorMax = Vector2.one;
            label.offsetMin = Vector2.zero;
            label.offsetMax = Vector2.zero;
        }
        else
        {
            label.gameObject.SetActive(true);
        }

        var text = label.GetComponent<Text>();
        text.text = "\u2715";
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = Mathf.RoundToInt(Mathf.Min(deleteIconSize.x, deleteIconSize.y) * 0.65f);
        text.raycastTarget = false;
    }

    void EnsureRowChildOrder()
    {
        if (_templateContainer == null)
            return;

        _templateContainer.SetAsFirstSibling();
        int order = 1;
        if (_editZone != null)
            _editZone.SetSiblingIndex(order++);
        if (_clearZone != null)
            _clearZone.SetSiblingIndex(order++);
        if (_deleteZone != null)
            _deleteZone.SetAsLastSibling();
    }

    void ApplyPanelScale()
    {
        if (paletteRect == null || panelWorldWidthMeters <= 0f)
            return;

        if (_panelLayoutLocked)
            return;

        LayoutRebuilder.ForceRebuildLayoutImmediate(paletteRect);

        float rectWidth = paletteRect.rect.width;
        if (rectWidth < 1f)
            return;

        float scale = panelWorldWidthMeters / rectWidth;
        Vector3 worldPos = paletteRect.position;
        Quaternion worldRot = paletteRect.rotation;

        // Negative X un-mirrors world-space UI so layout, text, and lateral offsets match the user's view.
        paletteRect.localScale = new Vector3(-scale, scale, scale);

        // Keep the panel fixed in world space when scale changes (center pivot).
        if (_worldAnchorApplied)
        {
            paletteRect.position = worldPos;
            paletteRect.rotation = worldRot;
        }
    }

    /// <summary>
    /// Clears the one-shot world anchor so placement runs again (e.g. after experiment-specific palette setup).
    /// MainScene does not call this; experiment scenes may.
    /// </summary>
    public void RequestWorldReanchor()
    {
        _worldAnchorApplied = false;
        _panelLayoutLocked = false;
        _anchorWaitFrames = 0;
        _stableHeadFrames = 0;
    }

    public void NotifyLayoutChanged()
    {
        if (_worldAnchorApplied)
        {
            _panelLayoutLocked = false;
            FinalizeAnchoredPaletteLayout();
        }
        else
        {
            RefreshPaletteLayout();
        }
    }

    public bool IsWorldAnchored => _worldAnchorApplied;

    /// <summary>
    /// Anchor immediately at the current head pose (skips stable-frame wait).
    /// Experiment scenes may call this; MainScene uses the default delayed anchor.
    /// </summary>
    public void ForceWorldAnchorNow()
    {
        if (paletteRect == null)
            return;

        ResolveHeadAnchor();
        if (headAnchor == null)
            return;

        _worldAnchorApplied = true;
        _panelLayoutLocked = false;

        if (paletteRect.parent != null)
            paletteRect.SetParent(null, true);

        paletteRect.pivot = new Vector2(0.5f, 0.5f);
        paletteRect.anchorMin = paletteRect.anchorMax = new Vector2(0.5f, 0.5f);

        float headY = headAnchor.position.y;
        bool useFallback = headY < minHeadHeightToAnchor || headY > maxHeadHeightToAnchor;
        ApplyHeadPlacement(useFallbackHeight: useFallback);
        FinalizeAnchoredPaletteLayout();

        if (debugLogPalette)
            Debug.Log($"[Palette] Force-anchored at {paletteRect.position} (head Y={headY:F2}, fallback={useFallback}).");
    }

    /// <summary>Updates hover/press state from the index fingertip world position.</summary>
    public void UpdateTouchFromFinger(Vector3 fingerWorld)
    {
        _lastFingerWorld = fingerWorld;

        if (paletteRect == null)
        {
            TouchState = PaletteTouchState.None;
            UpdateHoverHighlight(fingerWorld);
            return;
        }

        if (!followHead && !_worldAnchorApplied)
        {
            TouchState = PaletteTouchState.None;
            UpdateHoverHighlight(fingerWorld);
            _editPressActive = false;
            return;
        }

        float signedDistance = SignedDistanceToPalettePlane(fingerWorld);
        if (signedDistance > hoverDistanceMeters)
            TouchState = PaletteTouchState.None;
        else if (signedDistance <= pressDistanceMeters)
            TouchState = PaletteTouchState.Press;
        else
            TouchState = PaletteTouchState.Hover;

        UpdateHoverHighlight(fingerWorld);
        ProcessEditButtonPresses(fingerWorld);
    }

    void ProcessEditButtonPresses(Vector3 fingerWorld)
    {
        if (gridEditController == null || _editTarget == null)
            return;

        bool pressing = SignedDistanceToPalettePlane(fingerWorld) <= pressDistanceMeters;
        bool onEdit = IsFingerOverEdit(fingerWorld);

        if (pressing && onEdit && !_editPressActive)
            gridEditController.ToggleEditMode();

        _editPressActive = pressing && onEdit;
    }

    /// <summary>True when the fingertip is over the clear icon bounds.</summary>
    public bool IsFingerOverClear(Vector3 fingerWorld)
    {
        return IsFingerOverActionTarget(fingerWorld, _clearTarget, _clearTitle);
    }

    /// <summary>True when the fingertip is over the delete icon bounds.</summary>
    public bool IsFingerOverDelete(Vector3 fingerWorld)
    {
        return IsFingerOverActionTarget(fingerWorld, _deleteTarget, _deleteTitle);
    }

    public bool IsFingerOverEdit(Vector3 fingerWorld) =>
        IsFingerOverActionTarget(fingerWorld, _editTarget, _editTitle);

    /// <summary>Legacy alias kept for touch-manager checks.</summary>
    public bool IsFingerOverResize(Vector3 fingerWorld) => IsFingerOverEdit(fingerWorld);

    /// <summary>Legacy alias kept for touch-manager checks.</summary>
    public bool IsFingerOverRotate(Vector3 fingerWorld) => IsFingerOverEdit(fingerWorld);

    /// <summary>True when the finger is close enough to the panel to count as pressing a zone.</summary>
    public bool IsFingerPressingPalette(Vector3 fingerWorld) =>
        paletteRect != null && SignedDistanceToPalettePlane(fingerWorld) <= pressDistanceMeters;

    public bool IsFingerPressingZone(Vector3 fingerWorld, RectTransform icon, RectTransform title) =>
        IsFingerPressingPalette(fingerWorld) && IsFingerOverZoneBounds(fingerWorld, icon, title);

    bool IsFingerOverZoneBounds(Vector3 fingerWorld, RectTransform target, RectTransform title)
    {
        if (target == null || paletteRect == null) return false;

        Vector2 fingerLocal = FingerLocalOnPalette(fingerWorld);
        float pad = pickPaddingLocal;

        GetBoundsInPaletteLocal(target, out float iconMnX, out float iconMxX, out float iconMnY, out float iconMxY);
        if (fingerLocal.x >= iconMnX - pad && fingerLocal.x <= iconMxX + pad &&
            fingerLocal.y >= iconMnY - pad && fingerLocal.y <= iconMxY + pad)
            return true;

        if (title == null) return false;

        GetBoundsInPaletteLocal(title, out float titleMnX, out float titleMxX, out float titleMnY, out float titleMxY);
        return fingerLocal.x >= titleMnX - pad && fingerLocal.x <= titleMxX + pad &&
               fingerLocal.y >= titleMnY - pad && fingerLocal.y <= titleMxY + pad;
    }

    bool IsFingerOverActionTarget(Vector3 fingerWorld, RectTransform target, RectTransform title)
    {
        if (target == null || paletteRect == null)
            return false;

        if (SignedDistanceToPalettePlane(fingerWorld) > hoverDistanceMeters)
            return false;

        return IsFingerOverZoneBounds(fingerWorld, target, title);
    }

    void UpdateEditHighlight(Vector3 fingerWorld)
    {
        if (_editTarget == null) return;

        bool modeActive = gridEditController != null && gridEditController.IsEditModeActive;
        bool highlight = modeActive || IsFingerOverEdit(fingerWorld);
        UpdateActionOutline(_editTarget, ref _editOutline, highlight, editHoverHighlightColor);
    }

    static void UpdateActionOutline(RectTransform target, ref Outline outline, bool enabled, Color color)
    {
        if (target == null) return;
        if (enabled)
        {
            if (outline == null)
            {
                outline = target.gameObject.AddComponent<Outline>();
                outline.useGraphicAlpha = true;
                outline.effectDistance = new Vector2(4f, -4f);
            }
            outline.effectColor = color;
            outline.enabled = true;
        }
        else if (outline != null)
        {
            outline.enabled = false;
        }
    }

    void UpdateClearHighlight(Vector3 fingerWorld)
    {
        if (_clearTarget == null)
            return;

        bool highlight = IsFingerOverClear(fingerWorld);

        if (_clearOutline == null)
            _clearOutline = _clearTarget.GetComponent<Outline>();

        if (highlight)
        {
            if (_clearOutline == null)
            {
                _clearOutline = _clearTarget.gameObject.AddComponent<Outline>();
                _clearOutline.useGraphicAlpha = true;
            }

            _clearOutline.effectColor = clearHoverHighlightColor;
            _clearOutline.effectDistance = hoverOutlineDistance;
            _clearOutline.enabled = true;
        }
        else if (_clearOutline != null)
        {
            _clearOutline.enabled = false;
        }
    }

    void UpdateHoverHighlight(Vector3 fingerWorld)
    {
        if (!showHoverOutline)
        {
            SetTemplateHoverOutline(_hoveredTemplate, false);
            _hoveredTemplate = null;
            return;
        }

        if (Placement != null && Placement.IsCarrying)
        {
            SetTemplateHoverOutline(_hoveredTemplate, false);
            _hoveredTemplate = null;
            return;
        }

        RectTransform next = null;
        if (TouchState != PaletteTouchState.None)
        {
            Vector2 fingerLocal = FingerLocalOnPalette(fingerWorld);
            TryPickTemplate(fingerLocal, out next);
        }

        if (_hoveredTemplate == next)
            return;

        SetTemplateHoverOutline(_hoveredTemplate, false);
        _hoveredTemplate = next;
        SetTemplateHoverOutline(_hoveredTemplate, true);
    }

    void UpdateDeleteHighlight(Vector3 fingerWorld)
    {
        if (_deleteTarget == null)
            return;

        bool carrying = Placement != null && Placement.IsCarrying;
        bool highlight = carrying && IsFingerOverDelete(fingerWorld);

        if (_deleteOutline == null)
            _deleteOutline = _deleteTarget.GetComponent<Outline>();

        if (highlight)
        {
            if (_deleteOutline == null)
            {
                _deleteOutline = _deleteTarget.gameObject.AddComponent<Outline>();
                _deleteOutline.useGraphicAlpha = true;
            }

            _deleteOutline.effectColor = deleteHoverHighlightColor;
            _deleteOutline.effectDistance = hoverOutlineDistance;
            _deleteOutline.enabled = true;
        }
        else if (_deleteOutline != null)
        {
            _deleteOutline.enabled = false;
        }
    }

    void SetTemplateHoverOutline(RectTransform template, bool enabled)
    {
        if (template == null)
            return;

        Outline outline = template.GetComponent<Outline>();
        if (outline == null)
        {
            if (!enabled)
                return;

            outline = template.gameObject.AddComponent<Outline>();
            outline.effectColor = hoverOutlineColor;
            outline.effectDistance = hoverOutlineDistance;
            outline.useGraphicAlpha = true;
        }

        outline.effectColor = hoverOutlineColor;
        outline.effectDistance = hoverOutlineDistance;
        outline.enabled = enabled;
    }

    float SignedDistanceToPalettePlane(Vector3 worldPoint)
    {
        Vector3 towardUser = GetTowardUserNormal();
        float signed = Vector3.Dot(worldPoint - paletteRect.position, towardUser);

        // Ignore hits behind the panel (away from the user).
        if (signed < 0f)
            return float.MaxValue;

        return signed;
    }

    Vector3 GetTowardUserNormal()
    {
        if (headAnchor != null)
        {
            Vector3 towardUser = headAnchor.position - paletteRect.position;
            towardUser.y = 0f;
            if (towardUser.sqrMagnitude > 1e-6f)
                return towardUser.normalized;
        }

        return -paletteRect.forward;
    }

    Vector2 FingerLocalOnPalette(Vector3 fingerWorld)
    {
        Vector3 local = paletteRect.InverseTransformPoint(fingerWorld);
        return new Vector2(local.x, local.y);
    }

    static bool IsSelectableTemplate(RectTransform childRt)
    {
        return childRt != null
            && childRt.gameObject.activeInHierarchy
            && childRt.GetComponent<PaletteDeleteTarget>() == null
            && childRt.GetComponent<PaletteClearTarget>() == null
            && childRt.GetComponent<PaletteEditTarget>() == null
            && childRt.GetComponent<PaletteResizeTarget>() == null
            && childRt.GetComponent<PaletteRotateTarget>() == null
            && childRt.GetComponent<Graphic>() != null;
    }

    bool TryPickTemplate(Vector2 fingerLocal, out RectTransform hit)
    {
        hit = null;
        if (_templateContainer == null)
            return false;

        RectTransform bestOverlap = null;
        float bestOverlapDistSq = float.MaxValue;

        for (int i = _templateContainer.childCount - 1; i >= 0; i--)
        {
            if (!(_templateContainer.GetChild(i) is RectTransform childRt))
                continue;
            if (!IsSelectableTemplate(childRt))
                continue;

            GetBoundsInPaletteLocal(childRt, out float mnX, out float mxX, out float mnY, out float mxY);

            float paddedMnX = mnX - pickPaddingLocal;
            float paddedMxX = mxX + pickPaddingLocal;
            float paddedMnY = mnY - pickPaddingLocal;
            float paddedMxY = mxY + pickPaddingLocal;

            bool overlaps =
                fingerLocal.x >= paddedMnX && fingerLocal.x <= paddedMxX &&
                fingerLocal.y >= paddedMnY && fingerLocal.y <= paddedMxY;

            if (!overlaps)
                continue;

            Vector2 center = new Vector2((mnX + mxX) * 0.5f, (mnY + mxY) * 0.5f);
            float distSq = (fingerLocal - center).sqrMagnitude;
            if (distSq < bestOverlapDistSq)
            {
                bestOverlapDistSq = distSq;
                bestOverlap = childRt;
            }
        }

        hit = bestOverlap;
        return hit != null;
    }

    void GetBoundsInPaletteLocal(RectTransform child, out float mnX, out float mxX, out float mnY, out float mxY)
    {
        Vector3[] corners = new Vector3[4];
        child.GetWorldCorners(corners);

        mnX = mnY = float.MaxValue;
        mxX = mxY = float.MinValue;

        for (int i = 0; i < 4; i++)
        {
            Vector3 local = paletteRect.InverseTransformPoint(corners[i]);
            if (local.x < mnX) mnX = local.x;
            if (local.x > mxX) mxX = local.x;
            if (local.y < mnY) mnY = local.y;
            if (local.y > mxY) mxY = local.y;
        }
    }

    /// <summary>
    /// Clone the touched template and begin carrying it toward the arm via ItemList.
    /// </summary>
    public bool TryBeginCarryFromPalette(Transform indexTipWorld)
    {
        if (indexTipWorld == null || Placement == null || paletteRect == null)
            return false;

        if (Placement.IsCarrying)
            return false;

        if (IsFingerOverDelete(indexTipWorld.position) ||
            IsFingerOverClear(indexTipWorld.position) ||
            IsFingerOverEdit(indexTipWorld.position))
            return false;

        Vector2 fingerLocal = FingerLocalOnPalette(indexTipWorld.position);
        if (!TryPickTemplate(fingerLocal, out RectTransform template))
            return false;

        SetTemplateHoverOutline(_hoveredTemplate, false);
        _hoveredTemplate = null;

        RectTransform clone = Instantiate(template, template.position, template.rotation);
        clone.name = template.name + "_Placed";

        if (debugLogPalette)
            Debug.Log($"[Palette] Picked template '{template.name}' at canvasLocal={fingerLocal:F1}");

        return Placement.BeginCarryExternal(clone, indexTipWorld, destroyOnAbort: true);
    }
}

/// <summary>
/// Marks a palette UI element as the delete drop zone.
/// </summary>
public class PaletteDeleteTarget : MonoBehaviour { }

/// <summary>
/// Marks a palette UI element as the clear-all control.
/// </summary>
public class PaletteClearTarget : MonoBehaviour { }

public class PaletteEditTarget : MonoBehaviour { }

public class PaletteResizeTarget : MonoBehaviour { }

public class PaletteRotateTarget : MonoBehaviour { }

/// <summary>
/// Marks a palette child as a selectable template that can be cloned onto the arm.
/// </summary>
public class PaletteTemplateItem : MonoBehaviour { }
