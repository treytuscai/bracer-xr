using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds a minimal pick-and-place palette for 1H config mode (vertical + horizontal templates).
/// </summary>
[DefaultExecutionOrder(120)]
public class OneHHorizVerticalConfigPalette : MonoBehaviour
{
    const string VerticalName = "1hVerticalInterface";
    const string HorizontalName = "1hHorizontalInterface";

    [Header("References")]
    public OneHHorizVerticalController controller;
    public Transform headAnchor;
    public MonoBehaviour widgetPlacement;

    [Header("Palette layout")]
    public float heightOffsetMeters = -0.18f;
    public float distanceMeters = 0.55f;
    public float lateralOffsetMeters = 0.22f;

    PossibleUIPaletteController _palette;
    RectTransform _verticalTemplate;
    RectTransform _horizontalTemplate;
    bool _built;

    public PossibleUIPaletteController Palette => _palette;

    public void SetActive(bool active)
    {
        EnsureBuilt();
        if (_palette != null)
            _palette.enabled = active;

        var canvas = GetComponent<Canvas>();
        if (canvas != null)
            canvas.enabled = active;

        gameObject.SetActive(active);
        if (active)
            RefreshTemplateVisibility();
    }

    public void ApplyLayout()
    {
        if (_palette == null)
            return;

        _palette.distanceMeters = distanceMeters;
        _palette.heightOffsetMeters = heightOffsetMeters;
        _palette.lateralOffsetMeters = lateralOffsetMeters;
    }

    public void RefreshTemplateVisibility()
    {
        if (!_built || controller == null)
            return;

        bool horizontal = controller.ShowingHorizontalInterface;
        if (_verticalTemplate != null)
            _verticalTemplate.gameObject.SetActive(!horizontal);
        if (_horizontalTemplate != null)
            _horizontalTemplate.gameObject.SetActive(horizontal);
    }

    void EnsureBuilt()
    {
        if (_built)
            return;

        var canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
        }

        var rt = GetComponent<RectTransform>();
        if (rt == null)
            rt = gameObject.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(240f, 140f);
        rt.localScale = Vector3.one * 0.001f;

        _palette = gameObject.GetComponent<PossibleUIPaletteController>();
        if (_palette == null)
            _palette = gameObject.AddComponent<PossibleUIPaletteController>();
        _palette.headAnchor = headAnchor;
        _palette.paletteRect = rt;
        _palette.widgetPlacementOverride = widgetPlacement;
        _palette.followHead = true;
        ApplyLayout();
        _palette.useGridLayout = true;
        _palette.gridColumns = 2;
        _palette.gridCellSize = new Vector2(100f, 100f);
        _palette.templateDisplayScale = 1.25f;

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        _verticalTemplate = CreateTemplate(VerticalName, controller != null ? controller.verticalInterface : null, false);
        _horizontalTemplate = CreateTemplate(HorizontalName, controller != null ? controller.horizontalInterface : null, true);

        _built = true;
        RefreshTemplateVisibility();
    }

    RectTransform CreateTemplate(string objectName, Texture2D texture, bool isHorizontal)
    {
        RectTransform parent = _palette.paletteRect;
        if (parent != null)
        {
            var container = parent.Find("TemplateContainer") as RectTransform;
            if (container != null)
                parent = container;
        }

        var go = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(PaletteTemplateItem));
        var rt = go.GetComponent<RectTransform>();
        var image = go.GetComponent<Image>();
        rt.SetParent(parent, false);

        if (texture != null)
        {
            int nativeW = texture.width;
            int nativeH = texture.height;
            if (controller != null)
            {
                if (!isHorizontal && controller.verticalNativePixels.x > 0 && controller.verticalNativePixels.y > 0)
                {
                    nativeW = controller.verticalNativePixels.x;
                    nativeH = controller.verticalNativePixels.y;
                }
                else if (isHorizontal && controller.horizontalNativePixels.x > 0 && controller.horizontalNativePixels.y > 0)
                {
                    nativeW = controller.horizontalNativePixels.x;
                    nativeH = controller.horizontalNativePixels.y;
                }
            }

            float aspect = (float)nativeW / nativeH;
            const float maxDim = 100f;
            if (aspect >= 1f)
                rt.sizeDelta = new Vector2(maxDim * aspect, maxDim);
            else
                rt.sizeDelta = new Vector2(maxDim, maxDim / aspect);

            image.sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            image.preserveAspect = true;
            image.color = Color.white;

            var marker = go.AddComponent<OneHInterfaceTemplateMarker>();
            marker.isHorizontalInterface = isHorizontal;

            var src = go.AddComponent<RevisedGridCellSource>();
            src.sourceWidth = nativeW;
            src.sourceHeight = nativeH;
        }
        else
        {
            rt.sizeDelta = new Vector2(100f, 100f);
            image.color = Color.white;
        }

        return rt;
    }
}
