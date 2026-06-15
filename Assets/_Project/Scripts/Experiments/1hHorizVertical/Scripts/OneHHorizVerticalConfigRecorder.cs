using UnityEngine;

/// <summary>
/// In 1H config mode, records manual palette placements into <see cref="OneHHorizVerticalPlacementStore"/>.
/// </summary>
[DefaultExecutionOrder(122)]
public class OneHHorizVerticalConfigRecorder : MonoBehaviour
{
    [Header("References")]
    public OneHHorizVerticalController controller;
    public OneHHorizVerticalGridController grid;
    public ForearmInteraction interaction;
    public MonoBehaviour widgetPlacement;
    public RevisedForearmTouchManager touchManager;

    public OVRSkeleton rightHandSkeleton;

    bool _wasCarrying;
    bool _carriedIsHorizontal;
    Vector2 _lastArmUv;
    float _carriedScale = 1f;
    float _carriedRotation;
    IForearmWidgetPlacement _placement;

    void Awake()
    {
        if (controller == null)
            controller = FindObjectOfType<OneHHorizVerticalController>();

        if (grid == null)
            grid = FindObjectOfType<OneHHorizVerticalGridController>();

        if (interaction == null)
        {
            var surface = FindObjectOfType<ForearmDepthSurface>();
            if (surface != null)
                interaction = surface.GetComponent<ForearmInteraction>();
        }

        if (touchManager == null)
            touchManager = FindObjectOfType<RevisedForearmTouchManager>();

        if (rightHandSkeleton == null && touchManager != null)
            rightHandSkeleton = touchManager.rightHandSkeleton;

        _placement = ResolvePlacement(widgetPlacement);
    }

    void LateUpdate()
    {
        if (controller == null || !controller.ConfigMode || _placement == null || grid == null)
            return;

        bool carrying = _placement.IsCarrying;

        if (carrying)
        {
            TrackCarriedWidget();
            TrackArmTargetUv();
        }
        else if (_wasCarrying)
        {
            TryRecordCompletedPlacement();
        }

        _wasCarrying = carrying;
    }

    void TrackCarriedWidget()
    {
        if (!TryGetCarriedWidget(out RectTransform widget))
            return;

        var marker = widget.GetComponentInParent<OneHInterfaceTemplateMarker>();
        _carriedIsHorizontal = marker != null && marker.isHorizontalInterface;

        var source = widget.GetComponent<RevisedGridCellSource>();
        if (source != null)
        {
            _carriedScale = source.scale;
            _carriedRotation = source.rotationDegrees;
        }
        else
        {
            _carriedScale = grid.defaultPlacedScale;
            _carriedRotation = 0f;
        }
    }

    void TrackArmTargetUv()
    {
        if (interaction == null)
            return;

        if (interaction.IsActive)
        {
            _lastArmUv = interaction.TouchUV;
            return;
        }

        Transform tip = IndexTip;
        if (tip != null &&
            interaction.TryGetNearestSurfaceFromPoint(
                tip.position,
                interaction.maxHoverPreviewDistance > 0f ? interaction.maxHoverPreviewDistance : 0.1f,
                out Vector2 hoverUv,
                out _))
        {
            _lastArmUv = hoverUv;
        }
    }

    Transform IndexTip
    {
        get
        {
            const int indexTipBone = 10;
            if (rightHandSkeleton == null ||
                !rightHandSkeleton.IsInitialized ||
                !rightHandSkeleton.IsDataValid)
                return null;

            var bones = rightHandSkeleton.Bones;
            if (bones == null || bones.Count <= indexTipBone)
                return null;

            return bones[indexTipBone].Transform;
        }
    }

    void TryRecordCompletedPlacement()
    {
        if (!IsValidUv(_lastArmUv))
            return;

        if (!TryResolvePlacedCell(_lastArmUv, out int col, out int row))
        {
            Debug.LogWarning("[OneHConfigRecorder] Placement finished but anchor cell could not be resolved.");
            return;
        }

        if (grid.TrySelectCell(col, row))
        {
            grid.GetSelectedTransform(out _carriedScale, out _carriedRotation);
            grid.ClearSelection();
        }

        var oriented = new OneHHorizVerticalController.OrientedPlacement
        {
            useMeshUv = true,
            meshU = _lastArmUv.x,
            meshV = _lastArmUv.y,
            col = col,
            row = row,
            scale = _carriedScale,
            rotationDegrees = _carriedRotation
        };

        controller.OnConfigPlacementRecorded(oriented, _carriedIsHorizontal);
    }

    bool TryResolvePlacedCell(Vector2 uv, out int col, out int row)
    {
        col = -1;
        row = -1;

        if (grid.TryFindOccupiedCellAtUV(uv, out col, out row))
            return true;

        grid.UVToCell(uv, out col, out row);
        return grid.IsCellOccupied(col, row);
    }

    static bool TryGetCarriedWidget(out RectTransform widget)
    {
        widget = null;
        Transform root = WidgetCarryCanvas.Root;
        if (root == null)
            return false;

        for (int i = 0; i < root.childCount; i++)
        {
            if (root.GetChild(i) is RectTransform rt)
            {
                widget = rt;
                return true;
            }
        }

        return false;
    }

    static bool IsValidUv(Vector2 uv) =>
        uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

    static IForearmWidgetPlacement ResolvePlacement(MonoBehaviour behaviour)
    {
        if (behaviour == null)
            return null;

        if (behaviour is IForearmWidgetPlacement direct)
            return direct;

        foreach (var mb in behaviour.GetComponents<MonoBehaviour>())
        {
            if (mb is IForearmWidgetPlacement found)
                return found;
        }

        return null;
    }
}
