using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 1E Content Dependent — all PossibleUI templates stay visible in a randomized grid order.
/// When the participant picks a new template from the palette, previously placed arm content is cleared.
/// Attach only to the 1e scene; does not modify RevisedPlaceRotateScale or 1D scripts.
/// </summary>
[DefaultExecutionOrder(118)]
public class OneEContentDependentPaletteController : MonoBehaviour
{
    [Header("References")]
    public PossibleUIPaletteController palette;
    public RevisedGridController grid;
    public RevisedGridEditController gridEditController;
    [Tooltip("Used to detect carry transitions. Auto-resolved from the palette override if empty.")]
    public MonoBehaviour widgetPlacement;

    [Header("Palette Order")]
    [Tooltip("Optional seed for reproducible shuffle order. 0 = random each run.")]
    public int shuffleSeed;

    readonly List<RectTransform> _templates = new List<RectTransform>();
    bool _wasCarrying;
    IForearmWidgetPlacement _placement;

    void Awake()
    {
        if (palette == null)
            palette = GetComponent<PossibleUIPaletteController>();

        if (grid == null)
            grid = FindObjectOfType<RevisedGridController>();

        if (gridEditController == null)
            gridEditController = FindObjectOfType<RevisedGridEditController>();

        _placement = ResolvePlacement(widgetPlacement);
        if (_placement == null && palette != null)
            _placement = ResolvePlacement(palette.widgetPlacementOverride);
    }

    void Start()
    {
        StartCoroutine(InitializeAfterPaletteLayout());
    }

    void LateUpdate()
    {
        if (_placement == null)
            return;

        bool carrying = _placement.IsCarrying;
        if (carrying && !_wasCarrying &&
            TryGetCarriedWidget(out RectTransform carried) &&
            IsPaletteCarry(carried))
        {
            ClearMeshOnly();
            // ClearAll() also clears the carry preview; rebuild it for the new widget.
            grid?.TryCacheCarryPreviewSource(carried, out _, out _);
        }

        _wasCarrying = carrying;
    }

    IEnumerator InitializeAfterPaletteLayout()
    {
        for (int i = 0; i < 3; i++)
            yield return null;

        if (!RebuildTemplateList())
        {
            Debug.LogError("[OneEContentDependent] No palette templates found under TemplateContainer.");
            yield break;
        }

        ShuffleTemplateOrder();
        EnsureAllTemplatesVisible();

        Debug.Log($"[OneEContentDependent] Showing {_templates.Count} templates in randomized order.");
    }

    void ClearMeshOnly()
    {
        grid?.ClearAll();
        gridEditController?.ClearEditState();
    }

    bool RebuildTemplateList()
    {
        _templates.Clear();

        RectTransform container = FindTemplateContainer();
        if (container == null)
            return false;

        for (int i = 0; i < container.childCount; i++)
        {
            if (container.GetChild(i) is RectTransform child && IsTemplateGridEntry(child))
                _templates.Add(child);
        }

        return _templates.Count > 0;
    }

    void ShuffleTemplateOrder()
    {
        var rng = shuffleSeed != 0 ? new System.Random(shuffleSeed) : null;

        for (int i = _templates.Count - 1; i > 0; i--)
        {
            int j = rng != null ? rng.Next(i + 1) : Random.Range(0, i + 1);
            (_templates[i], _templates[j]) = (_templates[j], _templates[i]);
        }

        for (int i = 0; i < _templates.Count; i++)
            _templates[i].SetSiblingIndex(i);
    }

    void EnsureAllTemplatesVisible()
    {
        for (int i = 0; i < _templates.Count; i++)
            _templates[i].gameObject.SetActive(true);
    }

    static bool TryGetCarriedWidget(out RectTransform widget)
    {
        widget = null;
        Transform root = WidgetCarryCanvas.Root;
        if (root == null)
            return false;

        for (int i = 0; i < root.childCount; i++)
        {
            if (root.GetChild(i) is RectTransform rt && rt.gameObject.activeInHierarchy)
            {
                widget = rt;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Palette clones are named "{TemplateName}_Placed". Surface pickups use "CellContent_*".
    /// </summary>
    static bool IsPaletteCarry(RectTransform widget) =>
        widget != null &&
        widget.name.EndsWith("_Placed") &&
        !widget.name.StartsWith("CellContent_");

    RectTransform FindTemplateContainer()
    {
        if (palette == null || palette.paletteRect == null)
            return null;

        return palette.paletteRect.Find("TemplateContainer") as RectTransform;
    }

    static bool IsTemplateGridEntry(RectTransform rt)
    {
        if (rt == null)
            return false;

        if (rt.GetComponent<PaletteDeleteTarget>() != null ||
            rt.GetComponent<PaletteClearTarget>() != null ||
            rt.GetComponent<PaletteEditTarget>() != null ||
            rt.GetComponent<PaletteResizeTarget>() != null ||
            rt.GetComponent<PaletteRotateTarget>() != null)
            return false;

        if (rt.GetComponent<PaletteTemplateAspectSlot>() != null)
            return true;

        return rt.GetComponent<Graphic>() != null;
    }

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
