using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space hue slider + Toggle Erase for RevisedBoundingBox.
/// When <see cref="followHead"/> is enabled, the panel stays in front of the user's eyes.
/// </summary>
[DefaultExecutionOrder(105)]
public class RevisedBoundingBoxColorSlider : MonoBehaviour
{
    [Header("Input")]
    public OVRSkeleton rightHandSkeleton;
    [Min(0.005f)] public float pressDistanceMeters = 0.04f;

    [Header("Layout")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.12f;

    [Header("Head follow")]
    [Tooltip("Keep the panel in front of the user's eyes and update each frame as they turn.")]
    public bool followHead = true;
    [Tooltip("Eye anchor. Auto-resolves OVRCameraRig centerEyeAnchor if empty.")]
    public Transform headAnchor;
    [Min(0.1f)] public float distanceMeters = 0.35f;
    [Tooltip("Vertical offset from the eye anchor (meters).")]
    public float heightOffsetMeters = 0f;
    [Tooltip("Lateral offset from the eye anchor (meters). Positive = user's right.")]
    public float lateralOffsetMeters = 0.2f;

    [Header("Paint colour")]
    [Range(0f, 1f)] public float startHue = 0.33f;
    [Range(0f, 1f)] public float saturation = 1f;
    [Range(0f, 1f)] public float lightness  = 0.5f;
    [Range(0f, 1f)] public float paintAlpha = 0.65f;

    const int PanelW  = 130;
    const int PanelH  = 340;
    const int TopPad  = 44;
    const int BotPad  = 96;
    const int TrackX  = 0;
    const int TrackW  = 14;
    const int HandleW = 30;
    const int HandleH = 12;
    const int ToggleBtnW = 118;
    const int ToggleBtnH = 36;
    const int IndexTipBone = 10;

    RectTransform _root;
    RectTransform _handle;
    RectTransform _toggleBtn;
    Image         _swatchImg;
    Image         _toggleBtnBg;
    Text          _toggleBtnLabel;
    float         _hue;
    bool          _toggleHeld;

    public bool IsFingerOnPanel { get; private set; }
    public bool EraseMode { get; private set; }

    public Color CurrentPaintColor
    {
        get
        {
            Color c = Color.HSVToRGB(_hue, saturation, lightness);
            c.a = paintAlpha;
            return c;
        }
    }

    float SwatchY => -(PanelH * 0.5f) + 62f;
    float ToggleBtnY => -(PanelH * 0.5f) + 24f;

    void Awake()
    {
        _hue = startHue;
        BuildUI();
        RefreshHandle();
        RefreshToggleButton();
    }

    void Start()
    {
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
        if (followHead)
            ApplyHeadFollow();

        IsFingerOnPanel = false;
        HandleInteraction();
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

        if (TryHandleToggleButton(tip))
            return;

        if (Mathf.Abs(local.x) > PanelW * 0.5f) return;
        if (local.y < -(PanelH * 0.5f) || local.y > (PanelH * 0.5f)) return;

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

    bool TryHandleToggleButton(Transform tip)
    {
        if (_toggleBtn == null) return false;

        Vector2 btnLocal = _toggleBtn.InverseTransformPoint(tip.position);
        const float pad = 8f;
        bool onBtn = Mathf.Abs(btnLocal.x) <= ToggleBtnW * 0.5f + pad
                    && Mathf.Abs(btnLocal.y) <= ToggleBtnH * 0.5f + pad;

        if (!onBtn)
        {
            _toggleHeld = false;
            return false;
        }

        IsFingerOnPanel = true;
        if (!_toggleHeld)
        {
            _toggleHeld = true;
            EraseMode = !EraseMode;
            RefreshToggleButton();
        }
        return true;
    }

    void RefreshToggleButton()
    {
        if (_toggleBtnBg == null || _toggleBtnLabel == null) return;

        _toggleBtnBg.color = Color.black;
        _toggleBtnLabel.text = EraseMode ? "Toggle Draw" : "Toggle Erase";
        _toggleBtnLabel.color = Color.white;
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
        _root.localScale    = followHead ? new Vector3(-s, s, s) : new Vector3(s, s, s);
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
        trackImg.texture = BuildHueGradient(Mathf.Max(2, Mathf.RoundToInt(trackH)));
        trackImg.raycastTarget = false;

        _handle = MakeRect("HueHandle", _root, new Vector2(HandleW, HandleH));
        _handle.anchoredPosition = new Vector2(TrackX, trackBottom);
        AddSolidImage(_handle, new Color(1f, 1f, 1f, 0.90f));

        var swatch = MakeRect("Swatch", _root, new Vector2(48f, 36f));
        swatch.anchoredPosition = new Vector2(0f, SwatchY);
        _swatchImg = AddSolidImage(swatch, Color.green);

        _toggleBtn = MakeRect("ToggleEraseButton", _root, new Vector2(ToggleBtnW, ToggleBtnH));
        _toggleBtn.anchoredPosition = new Vector2(0f, ToggleBtnY);
        _toggleBtnBg = AddSolidImage(_toggleBtn, Color.black);

        var toggleLabel = MakeRect("ToggleEraseLabel", _toggleBtn, new Vector2(ToggleBtnW, ToggleBtnH));
        toggleLabel.anchoredPosition = Vector2.zero;
        _toggleBtnLabel = MakeText(toggleLabel, "Toggle Erase", 13, TextAnchor.MiddleCenter, Color.white);
        RefreshToggleButton();
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
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }
}
