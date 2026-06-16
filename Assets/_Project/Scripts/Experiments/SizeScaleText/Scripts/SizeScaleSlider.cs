using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space panel with two independent vertical sliders:
///   LEFT  track — Gap   (switches between GapGroups in SizeScaleController)
///   RIGHT track — Size  (switches between images within the current GapGroup)
///
/// When <see cref="followHead"/> is enabled, the panel stays in front of the user's eyes.
/// Otherwise uses <see cref="panelWorldPosition"/> / <see cref="panelWorldEulerAngles"/>.
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
             "Auto-resolved via HandTrackingController if empty.")]
    public OVRSkeleton rightHandSkeleton;

    [Tooltip("How close to the panel plane the fingertip must be to register (m).")]
    [Min(0.005f)] public float pressDistanceMeters = 0.02f;

    [Header("Layout")]
    [Tooltip("Physical width of the panel in metres.")]
    [Min(0.05f)] public float panelWorldWidthMeters = 0.22f;

    [Header("Placement")]
    [Tooltip("Keep the panel in front of the user's eyes and update each frame as they turn.")]
    public bool followHead = true;
    [Tooltip("Eye anchor. Auto-resolves OVRCameraRig centerEyeAnchor if empty.")]
    public Transform headAnchor;
    [Min(0.1f)] public float distanceMeters = 0.65f;
    [Tooltip("Vertical offset from the eye anchor (meters).")]
    public float heightOffsetMeters = 0f;
    [Tooltip("Lateral offset from the eye anchor (meters). Positive = user's right.")]
    public float lateralOffsetMeters = 0.2f;

    [Tooltip("World-space position when followHead is off.")]
    public Vector3 panelWorldPosition = new Vector3(0.2f, 1.2f, 0.35f);

    [Tooltip("World-space rotation of the panel (degrees) when followHead is off.")]
    public Vector3 panelWorldEulerAngles = Vector3.zero;

    // ── Canvas layout constants (canvas-pixel units) ──────────────────────────

    const int PanelW  = 220;
    const int PanelH  = 280;
    const int TopPad  = 44;   // reserved for column titles
    const int BotPad  = 22;

    // Gap track (left column)
    const int GapTrackX  = -70;
    const int GapTrackW  = 10;
    const int GapNotchW  = 22;
    const int GapNotchH  =  4;
    const int GapHandleW = 26;
    const int GapHandleH = 14;

    // Size track (right column)
    const int SizeTrackX  = +40;
    const int SizeTrackW  = 10;
    const int SizeNotchW  = 22;
    const int SizeNotchH  =  4;
    const int SizeHandleW = 26;
    const int SizeHandleH = 14;

    // Column divider x (midpoint between the two tracks)
    const float ColDivX = (GapTrackX + SizeTrackX) / 2f;   // ≈ −15

    // Label width for each column
    const int LabelW = 46;

    // XRHand IndexTip bone index
    const int IndexTipBone = 10;

    // ── Runtime ───────────────────────────────────────────────────────────────

    RectTransform _root;

    // Gap track visuals
    RectTransform _gapHandle;
    Image[]       _gapNotchImgs;
    Text[]        _gapLabelTxts;

    // Size track visuals
    RectTransform _sizeHandle;
    Image[]       _sizeNotchImgs;
    Text[]        _sizeLabelTxts;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        ApplyPlacement();
        ResolveReferences();
        BuildUI();
        RefreshVisuals();
    }

    void ApplyPlacement()
    {
        if (followHead)
            return;

        transform.SetPositionAndRotation(
            panelWorldPosition,
            Quaternion.Euler(panelWorldEulerAngles));
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!isActiveAndEnabled || followHead) return;
        ApplyPlacement();
    }
#endif

    void LateUpdate()
    {
        if (followHead)
            ApplyHeadFollow();

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
        if (controller == null || _root == null) return;

        Transform tip = FingerTip;
        if (tip == null) return;

        // Distance gate — same plane-distance check as ForearmColorWheel.
        float planeDist = Mathf.Abs(
            Vector3.Dot(tip.position - _root.position, _root.forward));
        if (planeDist > pressDistanceMeters) return;

        Vector2 local = _root.InverseTransformPoint(tip.position);

        // Horizontal panel bounds.
        if (Mathf.Abs(local.x) > PanelW * 0.5f) return;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        // Vertical bounds (with ±12 px padding beyond track ends).
        if (local.y < trackBottom - 12f || local.y > trackTop + 12f) return;

        float t = Mathf.InverseLerp(trackBottom, trackTop, local.y);

        // Determine which column the finger is in.
        bool isGapColumn = local.x < ColDivX;

        if (isGapColumn)
        {
            int gapIndex = Mathf.RoundToInt(t * (controller.GapCount - 1));
            if (gapIndex != controller.CurrentGapIndex)
            {
                controller.SetGapIndex(gapIndex);
                RefreshVisuals();
            }
        }
        else
        {
            int sizeIndex = Mathf.RoundToInt(t * (controller.ImageCount - 1));
            if (sizeIndex != controller.CurrentIndex)
            {
                controller.SetImageIndex(sizeIndex);
                RefreshVisuals();
            }
        }
    }

    // ── Visual refresh ────────────────────────────────────────────────────────

    void RefreshVisuals()
    {
        if (controller == null) return;

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        // Gap track
        int gapCount = controller.GapCount;
        int curGap   = controller.CurrentGapIndex;

        if (_gapHandle != null)
        {
            float tGap = gapCount > 1 ? (float)curGap / (gapCount - 1) : 0f;
            _gapHandle.anchoredPosition =
                new Vector2(GapTrackX, Mathf.Lerp(trackBottom, trackTop, tGap));
        }

        RefreshNotches(_gapNotchImgs, _gapLabelTxts, curGap);

        // Size track
        int sizeCount = controller.ImageCount;
        int curSize   = controller.CurrentIndex;

        if (_sizeHandle != null)
        {
            float tSize = sizeCount > 1 ? (float)curSize / (sizeCount - 1) : 0f;
            _sizeHandle.anchoredPosition =
                new Vector2(SizeTrackX, Mathf.Lerp(trackBottom, trackTop, tSize));
        }

        RefreshNotches(_sizeNotchImgs, _sizeLabelTxts, curSize);
    }

    static void RefreshNotches(Image[] notches, Text[] labels, int activeIndex)
    {
        if (notches == null) return;
        for (int i = 0; i < notches.Length; i++)
        {
            bool active = (i == activeIndex);
            if (notches[i] != null)
                notches[i].color = active
                    ? new Color(0.30f, 0.80f, 1.00f, 1f)
                    : new Color(0.65f, 0.65f, 0.70f, 1f);
            if (labels != null && i < labels.Length && labels[i] != null)
                labels[i].color = active
                    ? Color.white
                    : new Color(0.65f, 0.65f, 0.70f, 1f);
        }
    }

    // ── UI construction ───────────────────────────────────────────────────────

    void BuildUI()
    {
        int gapCount  = controller != null ? Mathf.Max(controller.GapCount,  1) : 2;
        int sizeCount = controller != null ? Mathf.Max(controller.ImageCount, 1) : 2;

        // Canvas -----------------------------------------------------------
        var canvasGo = new GameObject("SizeSliderCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        _root            = canvasGo.GetComponent<RectTransform>();
        _root.sizeDelta  = new Vector2(PanelW, PanelH);
        _root.pivot      = new Vector2(0.5f, 0.5f);
        float s          = panelWorldWidthMeters / PanelW;
        _root.localScale = followHead ? new Vector3(-s, s, s) : new Vector3(s, s, s);
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;

        // Dark backing card ------------------------------------------------
        var bg = MakeRect("Background", _root, new Vector2(PanelW, PanelH));
        bg.anchoredPosition = Vector2.zero;
        AddImage(bg, new Color(0.10f, 0.10f, 0.12f, 1f));

        // Column divider line ----------------------------------------------
        var div = MakeRect("Divider", _root, new Vector2(1, PanelH - 16));
        div.anchoredPosition = new Vector2(ColDivX, 0f);
        AddImage(div, new Color(0.25f, 0.25f, 0.28f, 1f));

        float trackBottom = -(PanelH * 0.5f) + BotPad;
        float trackTop    =  (PanelH * 0.5f) - TopPad;

        // Build both columns -----------------------------------------------
        BuildColumn("Gap",  GapTrackX,  GapTrackW,  GapNotchW,  GapNotchH,
                    GapHandleW,  GapHandleH,  gapCount,  trackBottom, trackTop,
                    new Color(1.00f, 0.65f, 0.20f, 1f),   // amber accent for gap
                    out _gapHandle, out _gapNotchImgs, out _gapLabelTxts);

        BuildColumn("Size", SizeTrackX, SizeTrackW, SizeNotchW, SizeNotchH,
                    SizeHandleW, SizeHandleH, sizeCount, trackBottom, trackTop,
                    new Color(0.30f, 0.80f, 1.00f, 1f),   // cyan accent for size
                    out _sizeHandle, out _sizeNotchImgs, out _sizeLabelTxts);
    }

    void BuildColumn(
        string label,
        int trackX, int trackW, int notchW, int notchH,
        int handleW, int handleH,
        int count,
        float trackBottom, float trackTop,
        Color accent,
        out RectTransform handle,
        out Image[] notchImgs,
        out Text[]  labelTxts)
    {
        float trackH = trackTop - trackBottom;

        // Column title
        var titleR = MakeRect($"Title_{label}", _root, new Vector2(80, 24));
        titleR.anchorMin        = new Vector2(0f, 1f);
        titleR.anchorMax        = new Vector2(1f, 1f);
        titleR.pivot            = new Vector2(0.5f, 1f);
        titleR.anchoredPosition = new Vector2(trackX, -8f);
        MakeText(titleR, label, 16, TextAnchor.MiddleCenter, accent);

        // Track bar
        var track = MakeRect($"Track_{label}", _root, new Vector2(trackW, Mathf.Max(trackH, 1f)));
        track.anchoredPosition = new Vector2(trackX, (trackBottom + trackTop) * 0.5f);
        AddImage(track, new Color(0.18f, 0.18f, 0.20f, 1f));

        // Notches + labels
        notchImgs = new Image[count];
        labelTxts = new Text[count];

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            float y = Mathf.Lerp(trackBottom, trackTop, t);

            var notch = MakeRect($"Notch_{label}_{i}", _root, new Vector2(notchW, notchH));
            notch.anchoredPosition = new Vector2(trackX, y);
            notchImgs[i] = AddImage(notch, new Color(0.65f, 0.65f, 0.70f, 1f));

            var lbl = MakeRect($"Label_{label}_{i}", _root, new Vector2(LabelW, 18));
            lbl.anchoredPosition = new Vector2(trackX + notchW / 2f + 6f + LabelW / 2f, y);
            labelTxts[i] = MakeText(lbl, $"{label} {i + 1}", 11,
                TextAnchor.MiddleLeft, new Color(0.65f, 0.65f, 0.70f, 1f));
        }

        // Handle (selection indicator)
        handle = MakeRect($"Handle_{label}", _root, new Vector2(handleW, handleH));
        handle.anchoredPosition = new Vector2(trackX, trackBottom);
        AddImage(handle, accent);
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
