using System;
using System.Collections.Generic;
using Experiments.Cli;
using Surface.Core;
using UnityEngine;

/// <summary>
/// 1K Flow vs Grid — displays interface art via ForearmDepthSurface portrait texture.
/// expctl next steps through the assigned interface images in order.
/// </summary>
[DefaultExecutionOrder(125)]
public class OneKFlowVsGridController : MonoBehaviour, IExperimentCommands
{
    [Header("References")]
    public ForearmDepthSurface surface;

    [Header("Interface images (expctl next order)")]
    [Tooltip("Assign PNGs from 1kFlowVsGrid/Interfaces in trial order.")]
    public Texture2D[] interfaceImages;

    [Header("Sequence")]
    [Tooltip("Randomize image order on start. Off = use Inspector array order.")]
    public bool shuffleOnStart;
    [Tooltip("Optional seed for reproducible shuffle. 0 = random each run.")]
    public int shuffleSeed;

    readonly List<Texture2D> _queue = new List<Texture2D>();
    int _index;
    bool _initialized;
    bool _sequenceComplete;

    public int CurrentIndex => _index;
    public int ImageCount => _queue.Count;
    public bool SequenceComplete => _sequenceComplete;

    void Awake()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();
    }

    void Start()
    {
        if (surface == null)
        {
            Debug.LogError("[OneKFlowVsGrid] No ForearmDepthSurface found.");
            return;
        }

        surface.orientationMode = DisplayOrientation.Portrait;

        if (!RebuildQueue())
        {
            Debug.LogError("[OneKFlowVsGrid] Assign interfaceImages in the Inspector.");
            return;
        }

        if (shuffleOnStart)
            ShuffleQueue();

        _index = 0;
        _sequenceComplete = false;
        ApplyCurrentImage();
        _initialized = true;

        Debug.Log($"[OneKFlowVsGrid] {FormatProgress()}: '{CurrentImageName()}'. expctl next advances.");
    }

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["next"] = _ => AdvanceToNextImage();
        commands["status"] = _ => BuildStatus();
    }

    public string AdvanceToNextImage()
    {
        if (!_initialized)
            return "not ready";

        if (_sequenceComplete || _queue.Count == 0)
            return "all interfaces complete";

        _index++;

        if (_index >= _queue.Count)
        {
            _sequenceComplete = true;
            return "all interfaces complete";
        }

        ApplyCurrentImage();
        string msg = $"{FormatProgress()}: {CurrentImageName()}";
        Debug.Log("[OneKFlowVsGrid] " + msg);
        return msg;
    }

    string BuildStatus()
    {
        if (!_initialized)
            return "not ready";
        if (_sequenceComplete)
            return "all interfaces complete";
        return $"{FormatProgress()} {CurrentImageName()}";
    }

    void ApplyCurrentImage()
    {
        if (surface == null || _queue.Count == 0)
            return;

        Texture2D tex = _queue[_index];
        surface.portraitTexture = tex;
        surface.landscapeTexture = tex;
    }

    bool RebuildQueue()
    {
        _queue.Clear();
        if (interfaceImages == null)
            return false;

        for (int i = 0; i < interfaceImages.Length; i++)
        {
            if (interfaceImages[i] != null)
                _queue.Add(interfaceImages[i]);
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
    }

    string FormatProgress() => $"image {_index + 1}/{_queue.Count}";

    string CurrentImageName() =>
        _index >= 0 && _index < _queue.Count && _queue[_index] != null
            ? _queue[_index].name
            : "(none)";
}
