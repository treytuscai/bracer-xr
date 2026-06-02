using UnityEngine;

/// <summary>
/// Drives the ForearmColorText material for the color-picker experiment. Exposes simple
/// setters that the color wheel / opacity sliders call to recolor the two layers live.
///
/// Operates on the runtime material INSTANCE owned by ForearmDepthSurface (via SurfaceMat),
/// not the shared .mat asset — so changes are isolated to this surface and don't persist
/// back to the project asset.
///
/// Layers (see ForearmColorText.shader):
///   _TextColor — foreground "Hello World" text (rgb + a opacity).
///   _BgColor   — background canvas behind the text (rgb + a opacity).
///                a = 0 → text sits directly on skin; raise a for a filled background.
/// </summary>
// Runs after ForearmDepthSurface.Start, which creates the material instance read here.
[DefaultExecutionOrder(100)]
public class ColorExperimentController : MonoBehaviour
{
    [Header("Surface")]
    [Tooltip("Forearm surface whose material instance is recolored. Assign in Inspector.")]
    public ForearmDepthSurface surface;

    [Header("Initial colors (a = opacity)")]
    public Color textColor       = Color.white;            // foreground text
    public Color backgroundColor = new Color(1, 1, 1, 0);  // a = 0 → text directly on skin

    // Cached shader property IDs — avoids the per-call string lookup in SetColor.
    static readonly int TextColorId = Shader.PropertyToID("_TextColor");
    static readonly int BgColorId   = Shader.PropertyToID("_BgColor");

    Material _mat;

    // surface is assigned in the Inspector (no scene search). Apply() null-checks it.
    void Start() => Apply();

    // ── Public API for the pickers ──────────────────────────────────────────────

    /// <summary>Set the text RGB from the color wheel (preserves current text opacity).</summary>
    public void SetTextColor(Color rgb)
    {
        textColor = new Color(rgb.r, rgb.g, rgb.b, textColor.a);
        Apply();
    }

    /// <summary>Set the text opacity [0,1] from the slider (preserves current RGB).</summary>
    public void SetTextOpacity(float a)
    {
        textColor.a = Mathf.Clamp01(a);
        Apply();
    }

    /// <summary>Set the background RGB from the color wheel (preserves current opacity).</summary>
    public void SetBackgroundColor(Color rgb)
    {
        backgroundColor = new Color(rgb.r, rgb.g, rgb.b, backgroundColor.a);
        Apply();
    }

    /// <summary>Set the background opacity [0,1]. 0 = text directly on skin, no canvas.</summary>
    public void SetBackgroundOpacity(float a)
    {
        backgroundColor.a = Mathf.Clamp01(a);
        Apply();
    }

    // ── Internal ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes both colors to the material. Lazily resolves the material instance the first
    /// time it exists — ForearmDepthSurface creates it in its own Start, so an early call
    /// (before the surface initializes) is a no-op and the next setter call retries.
    /// </summary>
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
