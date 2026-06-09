using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space hue slider for the RevisedBoundingBox experiment.
///
/// A single vertical rainbow track lets the user pick the paint colour for grid
/// cells. The panel is placed once at Start, eye-level and slightly to the
/// user's right (same placement convention as the Color experiment panels).
///
/// While the right index fingertip is on the track, grid-cell toggling is
/// suppressed so adjusting colour does not accidentally paint the arm.
/// </summary>
[DefaultExecutionOrder(105)] // Before RevisedBoundingBoxController (110)
public class RevisedBoundingBoxColorSlider : MonoBehaviour
{
    [Header("References")]
    public ForearmDepthSurface surface;

    [Header("Input")]
    [Tooltip("Right-hand OVRSkeleton for fingertip interaction.")]
    public OVRSkeleton rightHandSkeleton;

    [Tooltip("How close to the panel plane the fingertip must be to register (m).")]
    [Min(0.005f)] public float pressDistanceMeters = 0.02f;

    [Header("Placement")]
    [Min(0.2f)] public float panelDistance   = 0.55f;
    public float             panelRightMeters = 0.22f;
    public float             panelUpMeters    = 0f;

    [Header("Layout")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.12f;

    [Header("Paint colour")]
    [Range(0f, 1f)] public float startHue = 0.33f;
    [Range(0f, 1f)] public float saturation = 1f;
    [Range(0f, 1f)] public float lightness  = 0.5f;
    [Range(0f, 1f)] public float paintAlpha = 0.65f;

    // ── Canvas layout (pixel units) ───────────────────────────────────────────

    const int PanelW  = 120;
    const int PanelH  = 280;
    const int TopPad  = 44;
    const int BotPad  = 52;
    const int TrackX  = 0;
    const int TrackW  = 14;
    const int HandleW = 30;
    const int HandleH = 12;
    const int IndexTipBone = 10;

    // ── Runtime ───────────────────────────────────────────────────────────────

    RectTransform _root;
    RectTransform _handle;
    Image         _swatchImg;
    float         _hue;
    bool          _panelPlaced;

    /// <summary>True while the right index finger is actively on the slider track.</summary>
    public bool IsFingerOnPanel { get; private set; }

    /// <summary>Colour applied to newly selected grid cells (includes paintAlpha).</summary>
    public Color CurrentPaintColor
    {
        get
        {
            Color c = Color.HSVToRGB(_hue, saturation, lightness);
            c.a = paintAlpha;
            return c;
        }
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        _hue = startHue;
        BuildUI();
        RefreshHandle();
        // Hide until head tracking is valid and placement succeeds.
        if (_root != null) _root.gameObject.SetActive(false);
    }

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

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
    }

    void LateUpdate()
    {
        if (!_panelPlaced)
            PositionHUD();

        IsFingerOnPanel = false;
        HandleInteraction();
    }

    // ── Placement ─────────────────────────────────────────────────────────────

    void PositionHUD()
    {
        Transform eye = surface != null ? surface.centerEyeAnchor : null;
        if (eye == null || eye.position.sqrMagnitude < 0.01f) return;

        transform.SetPositionAndRotation(
            eye.position
                + eye.forward * panelDistance
                + eye.right   * panelRightMeters
                + eye.up      * panelUpMeters,
            Quaternion.LookRotation(eye.forward, eye.up));

        if (_root != null) _root.gameObject.SetActive(true);
        _panelPlaced = true;
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    Transform FingerTip
    {
        get
        {
            if (rightHandSkeleton == null ||
                !rightHandSkeleton.IsInitialized ||
                !rightHandSkeleton.IsDataValid) return null;

            var bones = rightHandSkeleton.Bones;
            if (bones == null || bones.Count <= IndexTipBone) return null;
            return bones[IndexTipBone].Transform;
        }
    }

    void HandleInteraction()
    {
        if (_root == null) return;

        Transform tip = FingerTip;
        if (tip == null) return;

        float planeDist = Mathf.Abs(
            Vector3.Dot(tip.position - _root.position, _root.forward));
        if (planeDist > pressDistanceMeters) return;

        Vector2 local = _root.InverseTransformPoint(tip.position);
        if (Mathf.Abs(local.x) > PanelW * 0.5f) return;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;
        if (local.y < trackBottom - 12f || local.y > trackTop + 12f) return;

        IsFingerOnPanel = true;

        float t = Mathf.Clamp01(Mathf.InverseLerp(trackBottom, trackTop, local.y));
        if (!Mathf.Approximately(_hue, t))
        {
            _hue = t;
            RefreshHandle();
        }
    }

    void RefreshHandle()
    {
        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        if (_handle != null)
            _handle.anchoredPosition = new Vector2(TrackX, Mathf.Lerp(trackBottom, trackTop, _hue));

        if (_swatchImg != null)
        {
            Color c = CurrentPaintColor;
            _swatchImg.color = new Color(c.r, c.g, c.b, 1f);
        }
    }

    // ── UI construction ───────────────────────────────────────────────────────

    void BuildUI()
    {
        var canvasGo = new GameObject("ColorSliderCanvas",
            typeof(RectTransform), typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        _root           = canvasGo.GetComponent<RectTransform>();
        _root.sizeDelta = new Vector2(PanelW, PanelH);
        _root.pivot     = new Vector2(0.5f, 0.5f);
        float s         = panelWorldWidthMeters / PanelW;
        _root.localScale    = new Vector3(s, s, s);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;
        float trackH      = trackTop - trackBottom;
        float trackMidY   = (trackBottom + trackTop) * 0.5f;

        var bg = MakeRect("Background", _root, new Vector2(PanelW, PanelH));
        bg.anchoredPosition = Vector2.zero;
        AddSolidImage(bg, new Color(0.08f, 0.08f, 0.10f, 0.88f));

        var title = MakeRect("Title", _root, new Vector2(PanelW - 16f, 28f));
        title.anchoredPosition = new Vector2(0f, (PanelH * 0.5f) - 22f);
        MakeText(title, "Color", 20, TextAnchor.MiddleCenter, Color.white);

        var track = MakeRect("HueTrack", _root, new Vector2(TrackW, trackH));
        track.anchoredPosition = new Vector2(TrackX, trackMidY);
        var trackImg = track.gameObject.AddComponent<RawImage>();
        trackImg.texture    = BuildHueGradient(Mathf.Max(2, Mathf.RoundToInt(trackH)));
        trackImg.raycastTarget = false;

        _handle = MakeRect("HueHandle", _root, new Vector2(HandleW, HandleH));
        _handle.anchoredPosition = new Vector2(TrackX, trackBottom);
        AddSolidImage(_handle, new Color(1f, 1f, 1f, 0.90f));

        var swatch = MakeRect("Swatch", _root, new Vector2(48f, 36f));
        swatch.anchoredPosition = new Vector2(0f, -(PanelH * 0.5f) + 26f);
        _swatchImg = AddSolidImage(swatch, Color.green);
    }

    static Texture2D BuildHueGradient(int height)
    {
        var tex = new Texture2D(1, height, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < height; y++)
            tex.SetPixel(0, y, Color.HSVToRGB((float)y / (height - 1), 1f, 1f));
        tex.Apply();
        return tex;
    }

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
