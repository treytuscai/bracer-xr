using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 1D-only: when the RememberNotif palette template is placed on the arm,
/// writes coordinates into <see cref="OneDSessionRememberNotifStore"/> for later scenes in the same session.
/// </summary>
[DefaultExecutionOrder(122)]
public class OneDRememberNotifPlacementRecorder : MonoBehaviour
{
    const string PlacedSuffix = "_Placed";
    const string DefaultSpriteKey = "1dRememberNotif";
    const int IndexTipBone = 10;

    [Header("References")]
    public RevisedGridController grid;
    public ForearmInteraction interaction;
    public MonoBehaviour widgetPlacement;
    public OVRSkeleton rightHandSkeleton;

    [Header("Target Template")]
    [Tooltip("Palette object name to record (without _Placed suffix). Also matches sprite/texture names containing this key.")]
    public string rememberNotifTemplateName = "1dRememberNotif";

    bool _wasCarrying;
    bool _carriedWasRememberNotif;
    string _carriedTemplateName;
    Vector2 _lastArmUv;
    float _carriedScale = 1f;
    float _carriedRotation;
    IForearmWidgetPlacement _placement;

    void Awake()
    {
        OneDSessionRememberNotifStore.GetOrCreate();

        if (grid == null)
            grid = FindObjectOfType<RevisedGridController>();

        if (interaction == null && grid != null)
            interaction = grid.GetComponent<ForearmInteraction>();

        if (rightHandSkeleton == null)
        {
            foreach (var s in FindObjectsOfType<OVRSkeleton>())
            {
                if (s.GetSkeletonType() == OVRSkeleton.SkeletonType.XRHandRight)
                {
                    rightHandSkeleton = s;
                    break;
                }
            }
        }

        _placement = ResolvePlacement(widgetPlacement);
        if (_placement == null)
        {
            var palette = FindObjectOfType<PossibleUIPaletteController>();
            if (palette != null)
                _placement = ResolvePlacement(palette.widgetPlacementOverride);
        }
    }

    void LateUpdate()
    {
        if (_placement == null || grid == null)
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
        // Carried widget is often hidden while the arm ghost preview is shown — keep last-known identity.
        if (!TryGetCarriedWidget(out RectTransform widget))
            return;

        _carriedTemplateName = NormalizeTemplateName(widget.name);
        _carriedWasRememberNotif = IsRememberNotifWidget(widget, _carriedTemplateName);

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

    void TryRecordCompletedPlacement()
    {
        if (!_carriedWasRememberNotif)
            return;

        Vector2 placeUv = _lastArmUv;
        if (interaction != null && interaction.IsActive && IsValidUv(interaction.TouchUV))
            placeUv = interaction.TouchUV;

        if (!IsValidUv(placeUv))
        {
            Debug.LogWarning("[OneDRememberNotif] RememberNotif carry ended without a valid arm UV.");
            return;
        }

        _lastArmUv = placeUv;

        if (!TryResolvePlacedCell(placeUv, out int col, out int row))
        {
            Debug.LogWarning($"[OneDRememberNotif] RememberNotif placed but anchor cell could not be resolved at uv={placeUv}.");
            return;
        }

        var record = new OneDSessionRememberNotifStore.PlacementRecord
        {
            TemplateName = string.IsNullOrEmpty(_carriedTemplateName) ? rememberNotifTemplateName : _carriedTemplateName,
            Col = col,
            Row = row,
            MeshUV = _lastArmUv,
            CellCenterUV = grid.CellCenterUV(col, row),
            Scale = _carriedScale,
            RotationDegrees = _carriedRotation
        };

        if (grid.TrySampleSurfaceAtUV(_lastArmUv, out Vector3 worldPos, out Vector3 worldNormal))
        {
            record.WorldPosition = worldPos;
            record.WorldNormal = worldNormal;
        }

        OneDSessionRememberNotifStore.GetOrCreate().Save(record);
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

    bool IsRememberNotifWidget(RectTransform widget, string carriedName)
    {
        if (widget == null)
            return false;

        if (widget.GetComponentInParent<OneDRememberNotifTemplateMarker>() != null)
            return true;

        if (IsNameMatch(carriedName))
            return true;

        return SpriteMatchesRememberNotif(widget);
    }

    bool IsNameMatch(string carriedName)
    {
        if (string.IsNullOrEmpty(carriedName) || string.IsNullOrEmpty(rememberNotifTemplateName))
            return false;

        return string.Equals(
            NormalizeTemplateName(carriedName),
            NormalizeTemplateName(rememberNotifTemplateName),
            System.StringComparison.OrdinalIgnoreCase);
    }

    static bool SpriteMatchesRememberNotif(RectTransform widget)
    {
        Image image = FindPrimaryImage(widget);
        if (image == null || image.sprite == null)
            return false;

        if (ContainsKey(image.sprite.name, DefaultSpriteKey))
            return true;

        Texture tex = image.sprite.texture;
        return tex != null && ContainsKey(tex.name, DefaultSpriteKey);
    }

    static bool ContainsKey(string value, string key) =>
        !string.IsNullOrEmpty(value) &&
        !string.IsNullOrEmpty(key) &&
        value.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0;

    static Image FindPrimaryImage(RectTransform widget)
    {
        if (widget == null)
            return null;

        Image rootImage = null;
        Image bestChild = null;
        float bestArea = 0f;

        foreach (var img in widget.GetComponentsInChildren<Image>(true))
        {
            if (img.sprite == null)
                continue;

            if (img.transform == widget)
            {
                rootImage = img;
                continue;
            }

            var rt = img.rectTransform;
            float area = Mathf.Abs(rt.rect.width * rt.rect.height);
            if (bestChild == null || area > bestArea)
            {
                bestChild = img;
                bestArea = area;
            }
        }

        return bestChild != null ? bestChild : rootImage;
    }

    Transform IndexTip
    {
        get
        {
            if (rightHandSkeleton == null ||
                !rightHandSkeleton.IsInitialized ||
                !rightHandSkeleton.IsDataValid)
                return null;

            var bones = rightHandSkeleton.Bones;
            if (bones == null || bones.Count <= IndexTipBone)
                return null;

            return bones[IndexTipBone].Transform;
        }
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

    static string NormalizeTemplateName(string widgetName)
    {
        if (string.IsNullOrEmpty(widgetName))
            return string.Empty;

        if (widgetName.EndsWith(PlacedSuffix))
            return widgetName.Substring(0, widgetName.Length - PlacedSuffix.Length);

        return widgetName;
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
