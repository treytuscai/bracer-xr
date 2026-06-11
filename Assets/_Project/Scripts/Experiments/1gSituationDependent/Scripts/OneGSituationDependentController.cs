using System;
using System.Collections;
using System.Collections.Generic;
using Experiments.Cli;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 1G Situation Dependent — on load, auto-places the palette image at the 1D RememberNotif
/// session coordinates when available; otherwise starts with a blank mesh and manual placement.
/// CLI "next" clears the arm and returns to manual pick-and-place from PossibleUIs.
/// </summary>
[DefaultExecutionOrder(125)]
public class OneGSituationDependentController : MonoBehaviour, IExperimentCommands
{
    [Header("References")]
    public PossibleUIPaletteController palette;
    public RevisedGridController grid;
    [Tooltip("RevisedGridPlacementController (or any IForearmWidgetPlacement) on SurfaceManager.")]
    public MonoBehaviour widgetPlacement;

    IForearmWidgetPlacement _placement;
    RectTransform _paletteTemplate;

    void Awake()
    {
        if (palette == null)
            palette = GetComponent<PossibleUIPaletteController>();

        if (grid == null)
            grid = FindObjectOfType<RevisedGridController>();

        _placement = ResolvePlacement(widgetPlacement);
        if (_placement == null && palette != null)
            _placement = ResolvePlacement(palette.widgetPlacementOverride);
    }

    void Start()
    {
        StartCoroutine(InitializeAfterPaletteLayout());
    }

    IEnumerator InitializeAfterPaletteLayout()
    {
        for (int i = 0; i < 3; i++)
            yield return null;

        yield return WaitForGridReady();

        if (!TryResolvePaletteTemplate(out _paletteTemplate))
        {
            Debug.LogError("[OneGSituationDependent] No palette template found under TemplateContainer.");
            yield break;
        }

        EnsurePaletteTemplateVisible();

        var store = OneDSessionRememberNotifStore.GetOrCreate();
        if (store.HasRecording)
        {
            var record = store.Recording;
            if (TryBakeTemplateAtRecording(_paletteTemplate, record))
                Debug.Log($"[OneGSituationDependent] Auto-placed at cell ({record.Col},{record.Row}) from 1D session.");
            else
                Debug.LogWarning("[OneGSituationDependent] Failed to auto-place — use palette manually.");
        }
        else
        {
            Debug.Log("[OneGSituationDependent] No 1D RememberNotif recording — blank mesh, pick from palette.");
        }
    }

    IEnumerator WaitForGridReady()
    {
        const int maxFrames = 30;
        for (int i = 0; i < maxFrames; i++)
        {
            if (grid != null && grid.Columns > 0 && grid.Rows > 0)
                yield break;
            yield return null;
        }
    }

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["next"] = _ => AdvanceToManualPlacement();
    }

    /// <summary>CLI: clear the arm and allow manual placement from PossibleUIs.</summary>
    public string AdvanceToManualPlacement()
    {
        ClearArm();
        EnsurePaletteTemplateVisible();
        return "cleared arm — pick image from palette and place on skin";
    }

    void ClearArm()
    {
        if (_placement == null)
            return;

        if (_placement.IsCarrying)
            _placement.DestroyCarriedItem();

        _placement.ClearAll();
        grid?.ClearCarryPreviewSource();
        grid?.ClearHighlight();
    }

    void EnsurePaletteTemplateVisible()
    {
        if (_paletteTemplate != null)
            _paletteTemplate.gameObject.SetActive(true);
    }

    bool TryBakeTemplateAtRecording(RectTransform template, OneDSessionRememberNotifStore.PlacementRecord record)
    {
        if (template == null || grid == null || !record.IsValid)
            return false;

        if (grid.IsCellOccupied(record.Col, record.Row))
            grid.ClearCell(record.Col, record.Row);

        RectTransform clone = Instantiate(template, template.position, template.rotation);
        clone.name = template.name + "_AutoPlaced";

        if (!grid.TryBakeWidgetIntoCell(clone, record.Col, record.Row))
        {
            Destroy(clone.gameObject);
            return false;
        }

        if (grid.TrySelectCell(record.Col, record.Row))
        {
            grid.SetSelectedCellScale(record.Scale);
            grid.SetSelectedCellRotation(record.RotationDegrees);
        }

        grid.ClearSelection();
        Destroy(clone.gameObject);
        return true;
    }

    bool TryResolvePaletteTemplate(out RectTransform template)
    {
        template = null;
        RectTransform container = FindTemplateContainer();
        if (container == null)
            return false;

        for (int i = 0; i < container.childCount; i++)
        {
            if (container.GetChild(i) is not RectTransform child || !IsTemplateGridEntry(child))
                continue;

            template = ResolveTemplateFromGridChild(child);
            if (template != null)
                return true;
        }

        return false;
    }

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

    static RectTransform ResolveTemplateFromGridChild(RectTransform gridChild)
    {
        if (gridChild == null)
            return null;

        if (gridChild.GetComponent<PaletteTemplateAspectSlot>() != null)
        {
            if (gridChild.childCount == 0)
                return null;

            return gridChild.GetChild(0) as RectTransform;
        }

        return gridChild.GetComponent<Graphic>() != null ? gridChild : null;
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
