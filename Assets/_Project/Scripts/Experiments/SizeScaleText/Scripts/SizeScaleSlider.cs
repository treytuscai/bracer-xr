using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space vertical image-selection slider for the SizeScaleText experiment.
///
/// PLACEMENT
///   Works exactly like ForearmColorWheel in the Color experiment: place the
///   GameObject in the scene at whatever world position you want the panel to
///   appear (e.g. (0.2, 1.2, 0.35) to match the Color wheel). The panel builds
///   itself as children of this Transform and never moves after Start().
///   There is no head-following or auto-anchoring.
///
/// VISUAL DESIGN
///   A compact panel (120 × 280 canvas-px) built entirely from coloured Image
///   quads and Text components — no art assets required.
///     • Vertical track with one discrete notch per image.
///     • Highlighted handle that snaps to the selected notch.
///     • "Size N" label beside each notch.
///
/// INTERACTION (same model as ForearmColorWheel)
///   The right index fingertip drives selection. When the tip is within
///   pressDistanceMeters of the panel plane AND within the panel bounds, the
///   notch nearest the finger's Y position is selected and SizeScaleController
///   is notified immediately.
/// </summary>
[DefaultExecutionOrder(120)]   // After SizeScaleController (110)
public class SizeScaleSlider : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Output")]
    [Tooltip("SizeScaleController to drive. Auto-found via FindObjectOfType if empty.")]
    public SizeScaleController controller;

    [Header("Input")]
    [Tooltip("Right-hand OVRSkeleton for fingertip interaction. " +
             "Auto-resolved via HandTrackingController or skeleton type if empty.")]
    public OVRSkeleton rightHandSkeleton;

    [Tooltip("How close to the panel plane the fingertip must be to register (m). " +
             "Matches pressDistanceMeters convention in ForearmColorWheel.")]
    [Min(0.005f)] public float pressDistanceMeters = 0.02f;

    [Header("Layout")]
    [Tooltip("Physical width of the panel in metres (same as panelWorldWidthMeters on ForearmColorWheel).")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.18f;

    // ── Canvas layout constants (canvas-pixel units) ──────────────────────────

    const int PanelW  = 120;
    const int PanelH  = 280;
    const int TopPad  = 44;    // reserved for title
    const int BotPad  = 22;    // bottom margin
    const int TrackX  = -28;   // track centre X (left-of-centre leaves room for labels)
    const int TrackW  = 10;
    const int NotchW  = 24;
    const int NotchH  =  4;
    const int HandleW = 28;
    const int HandleH = 14;

    // XRHand IndexTip bone index (verified in HandMask.cs)
    const int IndexTipBone = 10;

    // ── Runtime ───────────────────────────────────────────────────────────────

    RectTransform _root;
    RectTransform _handle;
    Image[]       _notchImgs;
    Text[]        _labelTxts;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        ResolveReferences();
        BuildUI();
        // Reflect initial state without pushing any change to the controller.
        RefreshVisuals();
    }

    void LateUpdate()
    {
        HandleInteraction();
    }

    // ── Reference resolution ─────────────────────────────────────────────────

    void ResolveReferences()
    {
        if (controller == null)
            controller = FindObjectOfType<SizeScaleController>();

        if (rightHandSkeleton == null)
        {
            var htc = FindObjectOfType<HandTrackingController>();
            if (htc != null) rightHandSkeleton = htc.rightSkeleton;
        }

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

    // ── Finger tip ───────────────────────────────────────────────────────────

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

    // ── Interaction ──────────────────────────────────────────────────────────

    void HandleInteraction()
    {
        if (controller == null || controller.ImageCount <= 1 || _root == null) return;

        Transform tip = FingerTip;
        if (tip == null) return;

        // Distance gate — same plane-distance check as ForearmColorWheel.
        float planeDist = Mathf.Abs(
            Vector3.Dot(tip.position - _root.position, _root.forward));
        if (planeDist > pressDistanceMeters) return;

        // Map finger to canvas-local coordinates.
        Vector2 local = _root.InverseTransformPoint(tip.position);

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        // Horizontal bounds (full panel width).
        if (Mathf.Abs(local.x) > PanelW * 0.5f) return;
        // Vertical bounds (with ±12 px padding beyond track ends).
        if (local.y < trackBottom - 12f || local.y > trackTop + 12f) return;

        float t     = Mathf.InverseLerp(trackBottom, trackTop, local.y);
        int   index = Mathf.RoundToInt(t * (controller.ImageCount - 1));

        if (index != controller.CurrentIndex)
        {
            controller.SetImageIndex(index);
            RefreshVisuals();
        }
    }

    // ── Visual refresh ────────────────────────────────────────────────────────

    void RefreshVisuals()
    {
        if (_handle == null || controller == null) return;

        int   count       = controller.ImageCount;
        int   cur         = controller.CurrentIndex;
        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        float t = count > 1 ? (float)cur / (count - 1) : 0f;
        _handle.anchoredPosition = new Vector2(TrackX, Mathf.Lerp(trackBottom, trackTop, t));

        if (_notchImgs != null)
        {
            for (int i = 0; i < _notchImgs.Length; i++)
            {
                bool active = (i == cur);
                if (_notchImgs[i] != null)
                    _notchImgs[i].color = active
                        ? new Color(0.30f, 0.80f, 1.00f, 1f)
                        : new Color(0.65f, 0.65f, 0.70f, 1f);
                if (_labelTxts != null && i < _labelTxts.Length && _labelTxts[i] != null)
                    _labelTxts[i].color = active
                        ? Color.white
                        : new Color(0.65f, 0.65f, 0.70f, 1f);
            }
        }
    }

    // ── UI construction ───────────────────────────────────────────────────────

    void BuildUI()
    {
        int count = controller != null ? Mathf.Max(controller.ImageCount, 1) : 2;

        // Canvas -----------------------------------------------------------
        var canvasGo = new GameObject("SizeSliderCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        _root            = canvasGo.GetComponent<RectTransform>();
        _root.sizeDelta  = new Vector2(PanelW, PanelH);
        _root.pivot      = new Vector2(0.5f, 0.5f);
        float s          = panelWorldWidthMeters / PanelW;
        _root.localScale = new Vector3(s, s, s);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;

        // Dark backing card ------------------------------------------------
        var bg = MakeRect("Background", _root, new Vector2(PanelW, PanelH));
        bg.anchoredPosition = Vector2.zero;
        AddImage(bg, new Color(0.10f, 0.10f, 0.12f, 1f));

        // Title ------------------------------------------------------------
        var titleR = MakeRect("Title", _root, new Vector2(PanelW - 8, 26));
        titleR.anchorMin        = new Vector2(0f, 1f);
        titleR.anchorMax        = new Vector2(1f, 1f);
        titleR.pivot            = new Vector2(0.5f, 1f);
        titleR.anchoredPosition = new Vector2(0f, -6f);
        MakeText(titleR, "Size", 18, TextAnchor.MiddleCenter, Color.white);

        // Track ------------------------------------------------------------
        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;
        float trackH      = trackTop - trackBottom;

        var track = MakeRect("Track", _root, new Vector2(TrackW, Mathf.Max(trackH, 1f)));
        track.anchoredPosition = new Vector2(TrackX, (trackBottom + trackTop) * 0.5f);
        AddImage(track, new Color(0.15f, 0.15f, 0.15f, 1f));

        // Notches + labels -------------------------------------------------
        _notchImgs = new Image[count];
        _labelTxts = new Text[count];

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            float y = Mathf.Lerp(trackBottom, trackTop, t);

            var notch = MakeRect($"Notch_{i}", _root, new Vector2(NotchW, NotchH));
            notch.anchoredPosition = new Vector2(TrackX, y);
            _notchImgs[i] = AddImage(notch, new Color(0.65f, 0.65f, 0.70f, 1f));

            var lbl = MakeRect($"Label_{i}", _root, new Vector2(62, 20));
            lbl.anchoredPosition = new Vector2(TrackX + NotchW / 2f + 8f + 31f, y);
            _labelTxts[i] = MakeText(lbl, $"Size {i + 1}", 14,
                TextAnchor.MiddleLeft, new Color(0.65f, 0.65f, 0.70f, 1f));
        }

        // Handle (selection indicator) -------------------------------------
        _handle = MakeRect("Handle", _root, new Vector2(HandleW, HandleH));
        _handle.anchoredPosition = new Vector2(TrackX, trackBottom);
        AddImage(_handle, new Color(0.30f, 0.80f, 1.00f, 1f));
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

    static Image AddImage(RectTransform rt, Color color)
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
