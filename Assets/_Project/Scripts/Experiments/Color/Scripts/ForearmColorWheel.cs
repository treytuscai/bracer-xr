using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

/// <summary>
/// Self-contained world-space color picker poked with the index fingertip. Builds its own
/// UI at runtime — an HSV wheel (hue = angle, saturation = radius), a vertical opacity
/// slider, and a preview swatch — so no canvas/RenderTexture or art assets are needed.
///
/// Output goes to a ColorExperimentController (Text or Background channel) and/or a
/// UnityEvent, every frame the fingertip is pressing a control.
///
/// Place the widget GameObject where you want the picker; it builds its UI as children and
/// scales to panelWorldWidthMeters. For Step 2 of the study, drop two of these — one set to
/// Channel.Text, one to Channel.Background.
/// </summary>
[System.Serializable] public class ColorChangedEvent : UnityEvent<Color> { }

[DefaultExecutionOrder(60)]
public class ForearmColorWheel : MonoBehaviour
{
    public enum Channel { Text, Background }

    [Header("Output")]
    [Tooltip("Controller to drive. Assign in Inspector.")]
    public ColorExperimentController target;
    [Tooltip("Which material layer this wheel controls.")]
    public Channel channel = Channel.Text;
    [Tooltip("Optional extra hookup; fires with the full RGBA each change.")]
    public ColorChangedEvent onColorChanged;

    [Header("Input")]
    [Tooltip("Right hand skeleton — the index fingertip (bone 10) is the picker cursor.")]
    public OVRSkeleton rightHandSkeleton;
    [Tooltip("How close to the panel plane the fingertip must be to register (m).")]
    [Min(0.001f)] public float pressDistanceMeters = 0.02f;

    // XRHand IndexTip bone index, verified against runtime bone names in HandMask.
    const int IndexTipBone = 10;

    [Header("Layout")]
    [Min(64)] public int wheelPixels = 256;
    [Min(0.05f)] public float panelWorldWidthMeters = 0.18f;

    [Header("Initial")]
    [Range(0f, 1f)] public float startValue   = 1f; // brightness (V); wheel covers H and S
    [Range(0f, 1f)] public float startOpacity = 1f;

    // Current selection.
    float _hue, _sat, _value, _opacity;

    // UI refs.
    RectTransform _root, _wheelRect, _sliderRect, _sliderHandle, _swatch;
    Image _swatchImg;

    void Awake()
    {
        _value   = startValue;
        _opacity = startOpacity;
        BuildUI();
        // Reflect the start state in the swatch/handle, but DON'T push to the material yet —
        // the controller's defaults (white text, transparent background) define the Step 1
        // "text on skin" start. The material only changes once the user touches a control.
        RefreshIndicators();
    }

    /// <summary>
    /// World-space index-fingertip transform from the hand skeleton (bone 10), or null when
    /// the skeleton data isn't valid yet. Bones stay allocated but stale when IsDataValid is
    /// false, so that flag is checked explicitly.
    /// </summary>
    Transform FingerTip
    {
        get
        {
            OVRSkeleton skel = rightHandSkeleton;
            if (skel == null || !skel.IsInitialized || !skel.IsDataValid) return null;
            var bones = skel.Bones;
            if (bones == null || bones.Count <= IndexTipBone) return null;
            return bones[IndexTipBone].Transform;
        }
    }

    void LateUpdate()
    {
        Transform tip = FingerTip;
        if (tip == null || _root == null) return;

        // Gate on distance to the panel plane (same idea as the palette buttons).
        Vector3 fw = tip.position;
        float planeDist = Mathf.Abs(Vector3.Dot(fw - _root.position, _root.forward));
        if (planeDist > pressDistanceMeters) return;

        // Wheel hit test (polar in the wheel's local space, centered at its pivot).
        Vector2 wLocal = _wheelRect.InverseTransformPoint(fw);
        float wheelR = _wheelRect.rect.width * 0.5f;
        if (wLocal.magnitude <= wheelR)
        {
            Vector2 p = wLocal / wheelR;          // unit disc
            float hue = Mathf.Atan2(p.y, p.x) / (2f * Mathf.PI);
            if (hue < 0f) hue += 1f;
            _hue = hue;
            _sat = Mathf.Clamp01(p.magnitude);
            Emit();
            return;
        }

        // Opacity slider hit test (1D along local Y).
        Vector2 sLocal = _sliderRect.InverseTransformPoint(fw);
        Rect sr = _sliderRect.rect;
        if (sLocal.x >= sr.xMin && sLocal.x <= sr.xMax &&
            sLocal.y >= sr.yMin && sLocal.y <= sr.yMax)
        {
            _opacity = Mathf.InverseLerp(sr.yMin, sr.yMax, sLocal.y);
            Emit();
        }
    }

    /// <summary>Current selection as RGBA: RGB from the wheel + value, A from the slider.</summary>
    public Color CurrentColor
    {
        get { Color c = Color.HSVToRGB(_hue, _sat, _value); c.a = _opacity; return c; }
    }

    void Emit()
    {
        Color c = CurrentColor;

        if (target != null)
        {
            if (channel == Channel.Text)
            {
                target.SetTextColor(c);
                target.SetTextOpacity(c.a);
            }
            else
            {
                target.SetBackgroundColor(c);
                target.SetBackgroundOpacity(c.a);
            }
        }

        onColorChanged?.Invoke(c);
        RefreshIndicators();
    }

    /// <summary>Updates the preview swatch (opaque hue) and the slider handle position.</summary>
    void RefreshIndicators()
    {
        Color c = CurrentColor;
        if (_swatchImg != null) _swatchImg.color = new Color(c.r, c.g, c.b, 1f);
        if (_sliderHandle != null && _sliderRect != null)
        {
            Rect sr = _sliderRect.rect;
            _sliderHandle.anchoredPosition = new Vector2(0f, Mathf.Lerp(sr.yMin, sr.yMax, _opacity));
        }
    }

    // ── UI construction ──────────────────────────────────────────────────────────

    void BuildUI()
    {
        float slider = wheelPixels * 0.16f;   // slider bar width
        float gap    = wheelPixels * 0.10f;
        float swatch = wheelPixels * 0.22f;
        float totalW = wheelPixels + gap + slider;
        float totalH = wheelPixels + gap + swatch;

        var canvasGo = new GameObject("ColorWheelCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        _root = canvasGo.GetComponent<RectTransform>();
        _root.sizeDelta = new Vector2(totalW, totalH);
        _root.pivot = new Vector2(0.5f, 0.5f);
        // Scale the whole panel to the requested physical width.
        float s = panelWorldWidthMeters / Mathf.Max(totalW, 1f);
        _root.localScale = new Vector3(s, s, s);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;

        // Opaque backing card — added first so it renders BEHIND the wheel/slider/swatch.
        // Without it the transparent UI blends into passthrough and washes out; the dark
        // card gives the colors something to pop against.
        var bgRect = MakeChild("Background", _root, new Vector2(wheelPixels, wheelPixels));
        bgRect.anchoredPosition = new Vector2(-(totalW - wheelPixels) * 0.5f,
                                                   (totalH - wheelPixels) * 0.5f);
        var bgImg = bgRect.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0.10f, 0.10f, 0.12f, 1f);
        bgImg.raycastTarget = false;

        // Wheel (top-left), pivot center for clean polar math.
        _wheelRect = MakeChild("Wheel", _root, new Vector2(wheelPixels, wheelPixels));
        _wheelRect.anchoredPosition = new Vector2(-(totalW - wheelPixels) * 0.5f,
                                                   (totalH - wheelPixels) * 0.5f);
        var wheelImg = _wheelRect.gameObject.AddComponent<RawImage>();
        wheelImg.texture = BuildWheelTexture(wheelPixels);
        wheelImg.raycastTarget = false;

        // Opacity slider (top-right): dark bar + handle.
        _sliderRect = MakeChild("OpacitySlider", _root, new Vector2(slider, wheelPixels));
        _sliderRect.anchoredPosition = new Vector2((totalW - slider) * 0.5f,
                                                   (totalH - wheelPixels) * 0.5f);
        var barImg = _sliderRect.gameObject.AddComponent<Image>();
        barImg.color = new Color(0.15f, 0.15f, 0.15f, 1f);
        barImg.raycastTarget = false;

        _sliderHandle = MakeChild("Handle", _sliderRect, new Vector2(slider, slider * 0.35f));
        _sliderHandle.anchorMin = _sliderHandle.anchorMax = new Vector2(0.5f, 0.5f);
        var handleImg = _sliderHandle.gameObject.AddComponent<Image>();
        handleImg.color = Color.white;
        handleImg.raycastTarget = false;

        // Preview swatch (bottom).
        _swatch = MakeChild("Swatch", _root, new Vector2(swatch, swatch));
        _swatch.anchoredPosition = new Vector2(0f, -(totalH - swatch) * 0.5f);
        _swatchImg = _swatch.gameObject.AddComponent<Image>();
        _swatchImg.raycastTarget = false;
    }

    static RectTransform MakeChild(string name, RectTransform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        return rt;
    }

    /// <summary>
    /// Generates an HSV color wheel: hue from the angle, saturation from the radius,
    /// value fixed at 1. Pixels outside the unit disc are transparent, with a 1-px soft edge.
    /// </summary>
    static Texture2D BuildWheelTexture(int size)
    {
        // linear: false = sRGB texture. The colors below are sRGB (Color.HSVToRGB), so the
        // texture MUST be sRGB or the pipeline washes them out (dull/milky) in a linear project.
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false, linear: true)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c;
                float dy = (y - c) / c;
                float r  = Mathf.Sqrt(dx * dx + dy * dy);
                if (r > 1f) { tex.SetPixel(x, y, Color.clear); continue; }

                float hue = Mathf.Atan2(dy, dx) / (2f * Mathf.PI);
                if (hue < 0f) hue += 1f;
                Color col = Color.HSVToRGB(hue, Mathf.Clamp01(r), 1f);
                col.a = 1f - Mathf.SmoothStep(0.97f, 1f, r); // soft outer edge
                tex.SetPixel(x, y, col);
            }
        }
        tex.Apply();
        return tex;
    }
}
