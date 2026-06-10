using System;
using System.Collections;
using System.Collections.Generic;
using Experiments.Cli;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 1D Elicitation of Placement — shows one randomized PossibleUI template at a time.
/// Does not modify RevisedPlaceRotateScale; attach only to the 1d scene palette.
/// </summary>
[DefaultExecutionOrder(125)]
public class OneDSequentialPaletteController : MonoBehaviour, IExperimentCommands
{
    [Header("References")]
    public PossibleUIPaletteController palette;
    [Tooltip("RevisedGridPlacementController (or any IForearmWidgetPlacement) on SurfaceManager.")]
    public MonoBehaviour widgetPlacement;

    [Header("Sequence")]
    [Tooltip("Optional seed for reproducible shuffle order. 0 = random each run.")]
    public int shuffleSeed;

    readonly List<RectTransform> _queue = new List<RectTransform>();
    int _currentIndex;
    bool _initialized;
    bool _sequenceComplete;
    IForearmWidgetPlacement _placement;

    public int CurrentIndex => _currentIndex;
    public int TemplateCount => _queue.Count;
    public bool SequenceComplete => _sequenceComplete;

    void Awake()
    {
        if (palette == null)
            palette = GetComponent<PossibleUIPaletteController>();

        _placement = ResolvePlacement(widgetPlacement);
        if (_placement == null)
            _placement = ResolvePlacement(palette != null ? palette.widgetPlacementOverride : null);
    }

    void Start()
    {
        StartCoroutine(InitializeAfterPaletteLayout());
    }

    void LateUpdate()
    {
        if (!_initialized || _sequenceComplete)
            return;

        ApplyCurrentTemplateVisibility();
    }

    IEnumerator InitializeAfterPaletteLayout()
    {
        // PossibleUIPaletteController builds TemplateContainer in Awake / first LateUpdate.
        for (int i = 0; i < 3; i++)
            yield return null;

        if (!RebuildTemplateQueue())
        {
            Debug.LogError("[OneDSequentialPalette] No palette templates found under TemplateContainer.");
            yield break;
        }

        ShuffleQueue();
        _currentIndex = 0;
        _sequenceComplete = false;
        _initialized = true;
        ApplyCurrentTemplateVisibility();

        Debug.Log($"[OneDSequentialPalette] Trial 1/{_queue.Count}: '{CurrentTemplateName()}'.");
    }

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["next"] = _ => AdvanceToNextTemplate();
    }

    /// <summary>CLI handler: clear the arm and show the next randomized template.</summary>
    public string AdvanceToNextTemplate()
    {
        if (!_initialized)
            return "palette not ready";

        ClearArmPlacement();

        if (_sequenceComplete || _queue.Count == 0)
            return "all templates complete";

        _currentIndex++;

        if (_currentIndex >= _queue.Count)
        {
            _sequenceComplete = true;
            HideAllTemplates();
            return "all templates complete";
        }

        ApplyCurrentTemplateVisibility();
        return $"showing template {_currentIndex + 1}/{_queue.Count}: {CurrentTemplateName()}";
    }

    void ClearArmPlacement()
    {
        if (_placement == null)
            return;

        if (_placement.IsCarrying)
            _placement.DestroyCarriedItem();

        _placement.ClearAll();
    }

    bool RebuildTemplateQueue()
    {
        _queue.Clear();

        RectTransform container = FindTemplateContainer();
        if (container == null)
            return false;

        for (int i = 0; i < container.childCount; i++)
        {
            if (container.GetChild(i) is RectTransform child && IsTemplateGridEntry(child))
                _queue.Add(child);
        }

        return _queue.Count > 0;
    }

    void ShuffleQueue()
    {
        var rng = shuffleSeed != 0 ? new System.Random(shuffleSeed) : null;

        for (int i = _queue.Count - 1; i > 0; i--)
        {
            int j = rng != null ? rng.Next(i + 1) : UnityEngine.Random.Range(0, i + 1);
            (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
        }

        for (int i = 0; i < _queue.Count; i++)
            _queue[i].SetSiblingIndex(i);
    }

    void ApplyCurrentTemplateVisibility()
    {
        if (_sequenceComplete)
        {
            HideAllTemplates();
            return;
        }

        for (int i = 0; i < _queue.Count; i++)
            _queue[i].gameObject.SetActive(i == _currentIndex);
    }

    void HideAllTemplates()
    {
        for (int i = 0; i < _queue.Count; i++)
            _queue[i].gameObject.SetActive(false);
    }

    string CurrentTemplateName()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count)
            return string.Empty;

        RectTransform entry = _queue[_currentIndex];
        RectTransform template = ResolveTemplateFromGridChild(entry);
        return template != null ? template.name : entry.name;
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
