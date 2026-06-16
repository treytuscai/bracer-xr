using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

[System.Serializable] public class ColorChangedEvent : UnityEvent<Color> { }

/// <summary>
/// World-space HSL color picker built from four vertical sliders:
///   1. Hue        — 0 → 360°  (rainbow gradient)
///   2. Saturation — grey → full hue color
///   3. Lightness  — black → white
///   4. Opacity    — transparent → opaque
///
/// A panel header label reads "Text Color Settings" or "Canvas Color Settings"
/// depending on the Channel setting, so each instance is self-identifying.
/// </summary>
[DefaultExecutionOrder(60)]
public class ForearmColorWheel : MonoBehaviour
{
    public enum Channel { Text, Background }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Output")]
    public ColorExperimentController target;
    public Channel channel = Channel.Text;
    public ColorChangedEvent onColorChanged;

    [Header("Input")]
    public OVRSkeleton rightHandSkeleton;
    [Min(0.001f)] public float pressDistanceMeters = 0.02f;

    [Header("Layout")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.18f;

    [Header("Head follow")]
    [Tooltip("Keep the panel in front of the user's eyes and update each frame as they turn.")]
    public bool followHead = true;
    [Tooltip("Eye anchor. Auto-resolves OVRCameraRig centerEyeAnchor if empty.")]
    public Transform headAnchor;
    [Min(0.1f)] public float distanceMeters = 0.35f;
    [Tooltip("Vertical offset from the eye anchor (meters).")]
    public float heightOffsetMeters = 0f;
    [Tooltip("Lateral offset from the eye anchor (meters). Positive = user's right.")]
    public float lateralOffsetMeters;

    [Header("Initial HSL + Opacity")]
    [Range(0f, 1f)] public float startHue        = 0f;
    [Range(0f, 1f)] public float startSaturation = 1f;
    [Range(0f, 1f)] public float startLightness  = 0.5f;
    [Range(0f, 1f)] public float startOpacity    = 1f;

    // ── Canvas layout constants ───────────────────────────────────────────────

    const int PanelW  = 340;   // wider to fit 4 tracks
    const int PanelH  = 330;   // taller to fit panel header + track titles
    const int TopPad  = 64;    // reserved for panel header + column titles
    const int BotPad  = 52;    // reserved for swatch

    const int TrackW  = 14;
    const int HandleW = 30;
    const int HandleH = 12;

    // Four track centres (panel origin = centre).
    const int HueX  = -114;
    const int SatX  =  -38;
    const int LitX  =  +38;
    const int OpxX  = +114;

    // Column dividers (midpoints between adjacent tracks).
    const float ColDiv1 = (HueX + SatX) / 2f;   // -76
    const float ColDiv2 = (SatX + LitX) / 2f;   //   0
    const float ColDiv3 = (LitX + OpxX) / 2f;   // +76

    const int IndexTipBone = 10;

    // ── Runtime state ─────────────────────────────────────────────────────────

    float _hue, _sat, _lightness, _opacity;

    RectTransform _root;
    RectTransform _hueHandle, _satHandle, _litHandle, _opxHandle;
    RawImage      _satTrackImg;
    Image         _swatchImg;

    Texture2D _satGradTex;
    float     _lastHueForSatGrad = -1f;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        if (Mathf.Approximately(lateralOffsetMeters, 0f))
            lateralOffsetMeters = channel == Channel.Text ? -0.2f : 0.2f;

        _hue       = startHue;
        _sat       = startSaturation;
        _lightness = startLightness;
        _opacity   = startOpacity;

        BuildUI();
        RefreshIndicators();
    }

    void LateUpdate()
    {
        if (followHead)
            ApplyHeadFollow();

        Transform tip = FingerTip;
        if (tip == null || _root == null) return;

        float planeDist = Mathf.Abs(
            Vector3.Dot(tip.position - _root.position, _root.forward));
        if (planeDist > pressDistanceMeters) return;

        Vector2 local = _root.InverseTransformPoint(tip.position);
        if (Mathf.Abs(local.x) > PanelW * 0.5f) return;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;
        if (local.y < trackBottom - 12f || local.y > trackTop + 12f) return;

        float t = Mathf.Clamp01(Mathf.InverseLerp(trackBottom, trackTop, local.y));

        bool changed = false;

        if (local.x < ColDiv1)
        {
            if (!Mathf.Approximately(_hue, t))       { _hue       = t; changed = true; }
        }
        else if (local.x < ColDiv2)
        {
            if (!Mathf.Approximately(_sat, t))       { _sat       = t; changed = true; }
        }
        else if (local.x < ColDiv3)
        {
            if (!Mathf.Approximately(_lightness, t)) { _lightness = t; changed = true; }
        }
        else
        {
            if (!Mathf.Approximately(_opacity, t))   { _opacity   = t; changed = true; }
        }

        if (changed) Emit();
    }

    void ApplyHeadFollow()
    {
        Transform anchor = ResolveHeadAnchor();
        if (anchor == null || _root == null)
            return;

        Vector3 viewForward = anchor.forward;
        if (viewForward.sqrMagnitude < 1e-6f)
            viewForward = Vector3.forward;
        viewForward.Normalize();

        Vector3 viewRight = anchor.right;
        if (viewRight.sqrMagnitude < 1e-6f)
            viewRight = Vector3.right;
        viewRight.Normalize();

        Vector3 anchorPos = anchor.position;
        Vector3 targetPos = anchorPos
            + viewForward * distanceMeters
            + viewRight * lateralOffsetMeters
            + anchor.up * heightOffsetMeters;

        transform.SetPositionAndRotation(targetPos, ComputeFacingRotation(anchorPos, targetPos, anchor.up));
    }

    Transform ResolveHeadAnchor()
    {
        if (headAnchor != null)
            return headAnchor;

        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            headAnchor = rig.centerEyeAnchor;

        return headAnchor;
    }

    static Quaternion ComputeFacingRotation(Vector3 anchorPos, Vector3 targetPos, Vector3 up)
    {
        Vector3 faceDir = anchorPos - targetPos;
        if (faceDir.sqrMagnitude < 1e-6f)
            faceDir = Vector3.forward;

        Vector3 upAxis = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
        return Quaternion.LookRotation(faceDir.normalized, upAxis);
    }

    // ── Color output ─────────────────────────────────────────────────────────

    public Color CurrentColor
    {
        get
        {
            Color c = HSLToRGB(_hue, _sat, _lightness);
            c.a = _opacity;
            return c;
        }
    }

    static Color HSLToRGB(float h, float s, float l)
    {
        float v  = l + s * Mathf.Min(l, 1f - l);
        float sv = v < 0.0001f ? 0f : 2f * (1f - l / v);
        return Color.HSVToRGB(h, sv, v);
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

    // ── Visual refresh ────────────────────────────────────────────────────────

    void RefreshIndicators()
    {
        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        MoveHandle(_hueHandle, HueX, _hue,       trackBottom, trackTop);
        MoveHandle(_satHandle, SatX, _sat,       trackBottom, trackTop);
        MoveHandle(_litHandle, LitX, _lightness, trackBottom, trackTop);
        MoveHandle(_opxHandle, OpxX, _opacity,   trackBottom, trackTop);

        if (!Mathf.Approximately(_lastHueForSatGrad, _hue))
        {
            RebuildSatGradient();
            _lastHueForSatGrad = _hue;
        }

        if (_swatchImg != null)
        {
            Color c = CurrentColor;
            _swatchImg.color = new Color(c.r, c.g, c.b, 1f);
        }
    }

    static void MoveHandle(RectTransform h, float trackX, float t, float bottom, float top)
    {
        if (h == null) return;
        h.anchoredPosition = new Vector2(trackX, Mathf.Lerp(bottom, top, t));
    }

    void RebuildSatGradient()
    {
        if (_satTrackImg == null) return;
        const int H = 64;
        if (_satGradTex == null)
            _satGradTex = new Texture2D(1, H, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        Color full = Color.HSVToRGB(_hue, 1f, 1f);
        for (int y = 0; y < H; y++)
        {
            float t = (float)y / (H - 1);
            _satGradTex.SetPixel(0, y, Color.Lerp(new Color(0.30f, 0.30f, 0.32f), full, t));
        }
        _satGradTex.Apply();
        _satTrackImg.texture = _satGradTex;
    }

    // ── Finger tip ───────────────────────────────────────────────────────────

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

    // ── UI construction ───────────────────────────────────────────────────────

    void BuildUI()
    {
        var canvasGo = new GameObject("ColorSlidersCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        _root           = canvasGo.GetComponent<RectTransform>();
        _root.sizeDelta = new Vector2(PanelW, PanelH);
        _root.pivot     = new Vector2(0.5f, 0.5f);
        float s         = panelWorldWidthMeters / PanelW;
        _root.localScale    = followHead ? new Vector3(-s, s, s) : new Vector3(s, s, s);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;
        float trackH      = trackTop - trackBottom;
        float trackMidY   = (trackBottom + trackTop) * 0.5f;

        // Backing card.
        var bg = MakeRect("Background", _root, new Vector2(PanelW, PanelH));
        bg.anchoredPosition = Vector2.zero;
        AddSolidImage(bg, new Color(0.10f, 0.10f, 0.12f, 0.5f));

        // Panel header label ("Text Color Settings" / "Canvas Color Settings").
        string headerText = channel == Channel.Text ? "Text Color Settings" : "Canvas Color Settings";
        var headerR = MakeRect("PanelHeader", _root, new Vector2(PanelW - 16, 22));
        headerR.anchoredPosition = new Vector2(0f, (PanelH * 0.5f) - 18f);
        MakeText(headerR, headerText, 15, TextAnchor.MiddleCenter, new Color(0.90f, 0.90f, 0.90f, 1f));

        // Thin separator line under the header.
        var sep = MakeRect("HeaderSep", _root, new Vector2(PanelW - 20, 1f));
        sep.anchoredPosition = new Vector2(0f, (PanelH * 0.5f) - 36f);
        AddSolidImage(sep, new Color(0.30f, 0.30f, 0.35f, 1f));

        // Column divider lines between tracks.
        BuildDivider(ColDiv1, trackH, trackMidY);
        BuildDivider(ColDiv2, trackH, trackMidY);
        BuildDivider(ColDiv3, trackH, trackMidY);

        // Hue track — rainbow gradient.
        BuildSliderColumn("Hue",      HueX, trackH, trackMidY, trackBottom,
            BuildHueGradient(128),      new Color(1.00f, 0.55f, 0.55f, 1f),
            out _, out _hueHandle);

        // Saturation track — dynamic grey → hue color.
        RawImage satRaw;
        BuildSliderColumn("Sat",      SatX, trackH, trackMidY, trackBottom,
            BuildSatGradient(128, _hue), new Color(0.55f, 1.00f, 0.55f, 1f),
            out satRaw, out _satHandle);
        _satTrackImg = satRaw;

        // Lightness track — black → white.
        BuildSliderColumn("Light",    LitX, trackH, trackMidY, trackBottom,
            BuildMonoGradient(128, new Color(0f, 0f, 0f), new Color(1f, 1f, 1f)),
            new Color(0.85f, 0.85f, 1.00f, 1f),
            out _, out _litHandle);

        // Opacity track — dark grey (transparent) → white (opaque).
        BuildSliderColumn("Opacity",  OpxX, trackH, trackMidY, trackBottom,
            BuildMonoGradient(128, new Color(0.15f, 0.15f, 0.17f), Color.white),
            new Color(0.90f, 0.75f, 1.00f, 1f),
            out _, out _opxHandle);

        // Preview swatch at the bottom.
        var swatchR = MakeRect("Swatch", _root, new Vector2(90, 28));
        swatchR.anchoredPosition = new Vector2(0f, -(PanelH * 0.5f) + 22f);
        _swatchImg = AddSolidImage(swatchR, Color.white);
    }

    void BuildSliderColumn(
        string label,
        float trackX, float trackH, float trackMidY, float trackBottom,
        Texture2D gradient, Color titleColor,
        out RawImage trackRawImg,
        out RectTransform handle)
    {
        // Column title (sits inside the TopPad area, just above the track).
        var titleR = MakeRect("Title_" + label, _root, new Vector2(72, 20));
        titleR.anchoredPosition = new Vector2(trackX, (PanelH * 0.5f) - TopPad + 12f);
        MakeText(titleR, label, 13, TextAnchor.MiddleCenter, titleColor);

        // Track bar.
        var trackR = MakeRect(label + "_Track", _root,
            new Vector2(TrackW, Mathf.Max(trackH, 1f)));
        trackR.anchoredPosition = new Vector2(trackX, trackMidY);
        var raw = trackR.gameObject.AddComponent<RawImage>();
        raw.texture       = gradient;
        raw.raycastTarget = false;
        trackRawImg = raw;

        // Selection handle.
        handle = MakeRect(label + "_Handle", _root, new Vector2(HandleW, HandleH));
        handle.anchoredPosition = new Vector2(trackX, trackBottom);
        AddSolidImage(handle, new Color(1f, 1f, 1f, 0.90f));
    }

    void BuildDivider(float x, float h, float midY)
    {
        var d = MakeRect("Divider", _root, new Vector2(1f, h));
        d.anchoredPosition = new Vector2(x, midY);
        AddSolidImage(d, new Color(0.25f, 0.25f, 0.28f, 1f));
    }

    // ── Gradient texture builders ─────────────────────────────────────────────

    static Texture2D BuildHueGradient(int height)
    {
        var tex = new Texture2D(1, height, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < height; y++)
            tex.SetPixel(0, y, Color.HSVToRGB((float)y / (height - 1), 1f, 1f));
        tex.Apply();
        return tex;
    }

    static Texture2D BuildSatGradient(int height, float hue)
    {
        var tex = new Texture2D(1, height, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Color full = Color.HSVToRGB(hue, 1f, 1f);
        for (int y = 0; y < height; y++)
            tex.SetPixel(0, y,
                Color.Lerp(new Color(0.30f, 0.30f, 0.32f), full, (float)y / (height - 1)));
        tex.Apply();
        return tex;
    }

    static Texture2D BuildMonoGradient(int height, Color bottom, Color top)
    {
        var tex = new Texture2D(1, height, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < height; y++)
            tex.SetPixel(0, y, Color.Lerp(bottom, top, (float)y / (height - 1)));
        tex.Apply();
        return tex;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static RectTransform MakeRect(string name, RectTransform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.sizeDelta  = size;
        rt.localScale = Vector3.one;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        return rt;
    }

    static Image AddSolidImage(RectTransform rt, Color color)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.color         = color;
        img.raycastTarget = false;
        return img;
    }

    static Text MakeText(RectTransform rt, string text, int fontSize,
                         TextAnchor alignment, Color color)
    {
        var t = rt.gameObject.AddComponent<Text>();
        t.text          = text;
        t.font          = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize      = fontSize;
        t.alignment     = alignment;
        t.color         = color;
        t.raycastTarget = false;
        return t;
    }
}
