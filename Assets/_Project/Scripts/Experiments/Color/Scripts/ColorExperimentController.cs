using System;
using System.Collections.Generic;
using Experiments.Cli;
using UnityEngine;

/// <summary>
/// Drives the ForearmColorText material for the color-picker experiment. Exposes simple
/// setters that the color wheel / opacity sliders call to recolor the two layers live.
///
/// expctl: hex — log text and canvas colors as #RRGGBBAA.
/// </summary>
[DefaultExecutionOrder(100)]
public class ColorExperimentController : MonoBehaviour, IExperimentCommands
{
    [Header("Surface")]
    [Tooltip("Forearm surface whose material instance is recolored. Assign in Inspector.")]
    public ForearmDepthSurface surface;

    [Header("Initial colors (a = opacity)")]
    public Color textColor       = Color.white;
    public Color backgroundColor = new Color(1, 1, 1, 0);

    static readonly int TextColorId = Shader.PropertyToID("_TextColor");
    static readonly int BgColorId   = Shader.PropertyToID("_BgColor");

    Material _mat;

    void Start() => Apply();

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["hex"] = _ => LogHexColors();
    }

    public string LogHexColors()
    {
        Apply();
        string textHex = ColorToHex(textColor);
        string canvasHex = ColorToHex(backgroundColor);
        string msg = $"text={textHex} canvas={canvasHex}";
        Debug.Log("[ColorExperiment] " + msg);
        return msg;
    }

    public static string ColorToHex(Color c) =>
        "#" + ColorUtility.ToHtmlStringRGBA(c);

    public void SetTextColor(Color rgb)
    {
        textColor = new Color(rgb.r, rgb.g, rgb.b, textColor.a);
        Apply();
    }

    public void SetTextOpacity(float a)
    {
        textColor.a = Mathf.Clamp01(a);
        Apply();
    }

    public void SetBackgroundColor(Color rgb)
    {
        backgroundColor = new Color(rgb.r, rgb.g, rgb.b, backgroundColor.a);
        Apply();
    }

    public void SetBackgroundOpacity(float a)
    {
        backgroundColor.a = Mathf.Clamp01(a);
        Apply();
    }

    void Apply()
    {
        if (_mat == null)
        {
            if (surface == null) return;
            _mat = surface.SurfaceMat;
            if (_mat == null) return;
        }

        if (_mat.HasProperty(TextColorId)) _mat.SetColor(TextColorId, textColor);
        if (_mat.HasProperty(BgColorId))   _mat.SetColor(BgColorId, backgroundColor);
    }
}
