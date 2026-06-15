using System;
using System.Collections.Generic;
using Experiments.Cli;
using Surface.Core;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// OrientationDegree experiment.
///
/// Projects an image onto the arm surface and displays a floating HUD
/// showing the elevation angle of the forearm in world space
/// (0° = horizontal, 90° = vertical).
///
/// Axis source: ForearmDepthSurface.AxisDir — wrist→elbow from the body
/// skeleton's left-arm bones (wrist 19, elbow 11 on the IOBT rig).
///
/// expctl: switch (toggle images), angle (log current angle).
/// Per-image scale, offset, and rotation are set in the Inspector.
/// </summary>
[DefaultExecutionOrder(110)]
public class OrientationDegreeController : MonoBehaviour, IExperimentCommands
{
    [Serializable]
    public class ArmImageLayout
    {
        [Tooltip("Authoring pixel size (e.g. 400×700 vertical, 700×400 horizontal). " +
                 "Use when the PNG file size does not match the designed layout.")]
        public Vector2Int nativePixels = new Vector2Int(400, 700);

        [Tooltip("Stamp height as a fraction of the arm UV patch. Width follows native pixel aspect.")]
        [Range(0.05f, 1f)] public float scale = 0.6f;

        [Tooltip("Pin the image to a bone-stable point on the forearm each frame. " +
                 "Uses the wrist bone frame (not camera-facing axes) so the image does not " +
                 "slide around the arm when you rotate it.")]
        public bool trackArmAnchor = true;
        [Tooltip("Anchor distance from the wrist along the forearm axis (m).")]
        public float anchorAlongArm = 0.08f;
        [Tooltip("Anchor offset from the arm axis toward the camera-facing (dorsal) side (m).")]
        public float anchorRadialArm = 0.05f;
        [Tooltip("Anchor offset around the arm circumference (m). Positive = wrist.up side.")]
        public float anchorLateralArm = 0f;

        [Tooltip("Fine-tune in UV space. Used as fixed center when trackArmAnchor is off; " +
                 "added on top of the arm anchor when on.")]
        [Range(-0.5f, 0.5f)] public float centerOffsetU = 0f;
        [Range(-0.5f, 0.5f)] public float centerOffsetV = 0f;
        [Tooltip("Rotation on the arm (degrees, counter-clockwise in UV space).")]
        [Range(-180f, 180f)] public float rotationDegrees = 0f;
    }

    [Header("Surface")]
    public ForearmDepthSurface surface;

    [Header("Shader — drag OrientationForearmImage.shader here")]
    public Shader imageShader;

    [Header("Images")]
    public Texture2D image;
    public Texture2D imageAlt;
    public ArmImageLayout imageLayout = new ArmImageLayout();
    public ArmImageLayout imageAltLayout = new ArmImageLayout();

    [Header("Image placement stability")]
    [Tooltip("Smooth arm-anchored UV center (s). Reduces residual jitter from depth mesh noise.")]
    [Min(0f)] public float placementSmoothingTime = 0.03f;

    [Header("Angle (world elevation)")]
    [Tooltip("Flip the forearm axis direction if the angle reads inverted.")]
    public bool flipAxis = false;
    [Tooltip("Smoothing time constant (s). Higher = more lag but less jitter.")]
    [Min(0f)] public float smoothingTime = 0.25f;
    [Tooltip("Ignore raw angle changes smaller than this (degrees). Suppresses micro-drift.")]
    [Min(0f)] public float deadbandDegrees = 2.5f;
    [Tooltip("While the headset rotates faster than this (deg/sec), freeze the angle. " +
             "Quest body tracking nudges the estimated elbow when the head turns. Set to 0 to disable.")]
    [Min(0f)] public float headMotionFreezeDegPerSec = 25f;

    [Header("Angle panel")]
    [Tooltip("Show the floating world-space angle readout.")]
    public bool showAnglePanel = true;
    [Tooltip("Eye anchor the panel floats in front of. Auto-resolved if empty.")]
    public Transform cameraAnchor;
    [Min(0.1f)] public float hudDistance = 0.6f;
    public float hudRightMeters = 0.22f;
    public float hudUpMeters = 0.15f;
    [Min(0.12f)] public float panelWorldWidthMeters = 0.28f;
    [Tooltip("Show the raw forearm axis vector under the angle.")]
    public bool showDebugAxis = true;

    const int PanelPixelWidth  = 420;
    const int PanelPixelHeight = 200;
    const int WristBoneIndex   = 19;

    Material _mat;
    Transform _hudRoot;
    Text _titleText;
    Text _angleText;
    Text _debugText;
    float _smoothedAngle;
    float _rawAngle;
    float _targetAngle;
    bool _haveAngleSample;
    bool _usingAltImage;
    Vector3 _lastAxis;
    float _smoothedCenterOffsetU;
    float _smoothedCenterOffsetV;
    bool _havePlacementSample;
    Quaternion _prevHeadRot;
    bool _haveHeadRot;

    static readonly int MainTexId       = Shader.PropertyToID("_MainTex");
    static readonly int ScaleId         = Shader.PropertyToID("_ImageScale");
    static readonly int ImageAspectId   = Shader.PropertyToID("_ImageAspect");
    static readonly int OffsetUId       = Shader.PropertyToID("_ImageOffsetU");
    static readonly int OffsetVId       = Shader.PropertyToID("_ImageOffsetV");
    static readonly int ImageRotationId = Shader.PropertyToID("_ImageRotation");

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (cameraAnchor == null && surface != null)
            cameraAnchor = surface.centerEyeAnchor;
        if (cameraAnchor == null && Camera.main != null)
            cameraAnchor = Camera.main.transform;

        SetupArmImage();
        if (showAnglePanel)
            BuildAnglePanel();
    }

    void LateUpdate()
    {
        ApplyLayout();
        SampleAndSmoothAngle();
        UpdateAnglePanel();
        PositionAnglePanel();
    }

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["switch"] = _ => SwitchImage();
        commands["angle"] = _ => LogAngle();
    }

    string SwitchImage()
    {
        if (image == null && imageAlt == null)
            return "error: assign image and imageAlt in Inspector";

        _usingAltImage = !_usingAltImage;
        _havePlacementSample = false;
        ApplyActiveTexture();
        ApplyLayout();
        string name = ActiveTexture != null ? ActiveTexture.name : "(none)";
        Debug.Log($"[OrientationDegree] switched to {name}");
        return "image: " + name;
    }

    string LogAngle()
    {
        SampleAndSmoothAngle();
        int smoothed = Mathf.RoundToInt(_smoothedAngle);
        int raw = Mathf.RoundToInt(_rawAngle);
        string msg = surface != null && surface.IsValid
            ? $"angle={smoothed} raw={raw}"
            : "angle=-- (no arm tracking)";
        Debug.Log("[OrientationDegree] " + msg);
        return msg;
    }

    Texture2D ActiveTexture => _usingAltImage ? imageAlt : image;
    ArmImageLayout ActiveLayout => _usingAltImage ? imageAltLayout : imageLayout;

    void SetupArmImage()
    {
        if (surface == null) return;

        Shader sh = imageShader != null
            ? imageShader
            : Shader.Find("Custom/OrientationForearmImage")
              ?? Shader.Find("Custom/ForearmImageDisplay");
        if (sh == null)
        {
            Debug.LogError("[OrientationDegree] Shader not found.");
            return;
        }

        _mat = new Material(sh) { name = "OrientationImageMat" };
        ApplyActiveTexture();

        var mr = surface.GetComponent<MeshRenderer>()
              ?? surface.GetComponentInChildren<MeshRenderer>();
        if (mr != null) mr.material = _mat;

        ApplyLayout();
    }

    void ApplyActiveTexture()
    {
        if (_mat == null) return;
        Texture2D tex = ActiveTexture ?? image ?? imageAlt;
        if (tex != null)
            _mat.SetTexture(MainTexId, tex);
    }

    void ApplyLayout()
    {
        if (_mat == null) return;
        ArmImageLayout layout = ActiveLayout;
        Texture2D tex = ActiveTexture ?? image ?? imageAlt;

        ResolveCenterOffsets(layout, out float offsetU, out float offsetV);
        _mat.SetFloat(ScaleId, layout.scale);
        _mat.SetFloat(OffsetUId, offsetU);
        _mat.SetFloat(OffsetVId, offsetV);
        if (_mat.HasProperty(ImageAspectId))
            _mat.SetFloat(ImageAspectId, ResolveContentAspect(tex, layout));
        if (_mat.HasProperty(ImageRotationId))
            _mat.SetFloat(ImageRotationId, layout.rotationDegrees * Mathf.Deg2Rad);
    }

    void ResolveCenterOffsets(ArmImageLayout layout, out float offsetU, out float offsetV)
    {
        float pronationScroll = surface != null && surface.IsValid
            ? surface.PronationAngle / (2f * Mathf.PI)
            : 0f;

        if (layout.trackArmAnchor && surface != null && surface.IsValid)
        {
            Vector3 anchor = ResolveBoneStableAnchor(layout);
            Vector2 uv = SurfaceUV.Compute(
                anchor,
                surface.WristPosition,
                surface.AxisDir,
                surface.AxisRight,
                surface.ProjCenter,
                pronationScroll,
                surface.displayOffset,
                surface.displayWidth,
                surface.displayHeight);
            offsetU = uv.x - 0.5f + layout.centerOffsetU;
            offsetV = uv.y - 0.5f + layout.centerOffsetV;
        }
        else
        {
            // Fixed UV patch; add pronation scroll so wrist roll does not drag the image in U.
            offsetU = layout.centerOffsetU + pronationScroll;
            offsetV = layout.centerOffsetV;
        }

        if (placementSmoothingTime > 0.001f)
        {
            if (!_havePlacementSample)
            {
                _smoothedCenterOffsetU = offsetU;
                _smoothedCenterOffsetV = offsetV;
                _havePlacementSample = true;
            }
            else
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / placementSmoothingTime);
                _smoothedCenterOffsetU = Mathf.Lerp(_smoothedCenterOffsetU, offsetU, alpha);
                _smoothedCenterOffsetV = Mathf.Lerp(_smoothedCenterOffsetV, offsetV, alpha);
            }

            offsetU = _smoothedCenterOffsetU;
            offsetV = _smoothedCenterOffsetV;
        }
    }

    static float ResolveContentAspect(Texture2D tex, ArmImageLayout layout)
    {
        int w, h;
        if (layout.nativePixels.x > 0 && layout.nativePixels.y > 0)
        {
            w = layout.nativePixels.x;
            h = layout.nativePixels.y;
        }
        else if (tex != null)
        {
            w = tex.width;
            h = tex.height;
        }
        else
        {
            return 1f;
        }

        return w / (float)Mathf.Max(1, h);
    }

    Vector3 ResolveBoneStableAnchor(ArmImageLayout layout)
    {
        Vector3 wristPos = surface.WristPosition;
        Vector3 axis = surface.AxisDir;

        if (!TryGetWristTransform(out Transform wrist))
            return wristPos + axis * layout.anchorAlongArm;

        Vector3 boneLateral = Vector3.Cross(axis, -wrist.up);
        if (boneLateral.sqrMagnitude < 1e-4f)
            boneLateral = Vector3.Cross(axis, surface.AxisUp);
        boneLateral.Normalize();

        Vector3 boneOut = Vector3.Cross(boneLateral, axis);
        if (boneOut.sqrMagnitude < 1e-4f)
            boneOut = surface.AxisUp;
        else
            boneOut.Normalize();

        // Prefer the side of the arm facing the camera so radial offset lands on visible skin.
        if (Vector3.Dot(boneOut, surface.AxisUp) < 0f)
            boneOut = -boneOut;

        return wristPos
            + axis * layout.anchorAlongArm
            + boneOut * layout.anchorRadialArm
            + boneLateral * layout.anchorLateralArm;
    }

    bool TryGetWristTransform(out Transform wrist)
    {
        wrist = null;
        if (surface?.bodySkeleton == null)
            return false;

        var bones = surface.bodySkeleton.Bones;
        if (bones == null || bones.Count <= WristBoneIndex)
            return false;

        wrist = bones[WristBoneIndex].Transform;
        return wrist != null;
    }

    bool IsHeadSteady()
    {
        if (headMotionFreezeDegPerSec <= 0f || cameraAnchor == null)
            return true;

        Quaternion rot = cameraAnchor.rotation;
        if (!_haveHeadRot)
        {
            _prevHeadRot = rot;
            _haveHeadRot = true;
            return true;
        }

        float degPerSec = Quaternion.Angle(_prevHeadRot, rot) / Mathf.Max(Time.deltaTime, 1e-4f);
        _prevHeadRot = rot;
        return degPerSec <= headMotionFreezeDegPerSec;
    }

    static float WorldElevationDegrees(Vector3 axis)
    {
        return Mathf.Abs(Mathf.Asin(Mathf.Clamp(axis.y, -1f, 1f)) * Mathf.Rad2Deg);
    }

    void SampleAndSmoothAngle()
    {
        if (surface == null || !surface.IsValid)
        {
            _rawAngle = 0f;
            return;
        }

        Vector3 axis = (flipAxis ? -surface.AxisDir : surface.AxisDir).normalized;
        _lastAxis = axis;
        float angle = WorldElevationDegrees(axis);
        _rawAngle = angle;
        bool headSteady = IsHeadSteady();

        if (!_haveAngleSample)
        {
            _targetAngle = angle;
            _smoothedAngle = angle;
            _haveAngleSample = true;
        }
        else if (headSteady && Mathf.Abs(angle - _targetAngle) > deadbandDegrees)
        {
            _targetAngle = angle;
        }

        float alpha = smoothingTime > 0.001f
            ? 1f - Mathf.Exp(-Time.deltaTime / smoothingTime)
            : 1f;
        _smoothedAngle = Mathf.Lerp(_smoothedAngle, _targetAngle, alpha);
    }

    void UpdateAnglePanel()
    {
        if (_angleText == null) return;

        if (surface == null || !surface.IsValid)
        {
            _angleText.text = "--°";
            if (_debugText != null)
                _debugText.text = "waiting for arm tracking";
            return;
        }

        _angleText.text = Mathf.RoundToInt(_smoothedAngle) + "°";

        if (_debugText != null && showDebugAxis)
        {
            bool headSteady = IsHeadSteady();
            string freeze = headSteady ? "" : "  [head moving]";
            _debugText.text = $"axis ({_lastAxis.x:F2}, {_lastAxis.y:F2}, {_lastAxis.z:F2}){freeze}";
        }
    }

    void BuildAnglePanel()
    {
        _hudRoot = new GameObject("OrientationAnglePanel").transform;
        _hudRoot.SetParent(transform, false);

        var canvasGo = new GameObject("Canvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(_hudRoot, false);

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;

        var panel = canvasGo.GetComponent<RectTransform>();
        panel.sizeDelta = new Vector2(PanelPixelWidth, PanelPixelHeight);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.localPosition = Vector3.zero;
        panel.localRotation = Quaternion.identity;
        float scale = panelWorldWidthMeters / PanelPixelWidth;
        panel.localScale = new Vector3(scale, scale, scale);

        var bg = MakeUiRect("Background", panel, new Vector2(PanelPixelWidth, PanelPixelHeight));
        bg.anchoredPosition = Vector2.zero;
        AddImage(bg, new Color(0.06f, 0.06f, 0.08f, 0.88f));

        var titleRect = MakeUiRect("Title", panel, new Vector2(380f, 36f));
        titleRect.anchoredPosition = new Vector2(0f, 68f);
        _titleText = AddText(titleRect, "Arm elevation (world)", 22, TextAnchor.MiddleCenter,
            new Color(0.85f, 0.85f, 0.9f, 1f));

        var angleRect = MakeUiRect("Angle", panel, new Vector2(380f, 88f));
        angleRect.anchoredPosition = new Vector2(0f, 18f);
        _angleText = AddText(angleRect, "--°", 72, TextAnchor.MiddleCenter, Color.white);

        var hintRect = MakeUiRect("Hint", panel, new Vector2(380f, 28f));
        hintRect.anchoredPosition = new Vector2(0f, -34f);
        AddText(hintRect, "0° = level   90° = straight up", 16, TextAnchor.MiddleCenter,
            new Color(0.65f, 0.65f, 0.7f, 1f));

        if (showDebugAxis)
        {
            var debugRect = MakeUiRect("Debug", panel, new Vector2(380f, 30f));
            debugRect.anchoredPosition = new Vector2(0f, -72f);
            _debugText = AddText(debugRect, "axis —", 16, TextAnchor.MiddleCenter,
                new Color(1f, 0.95f, 0.45f, 1f));
        }
    }

    void PositionAnglePanel()
    {
        if (!showAnglePanel || _hudRoot == null || cameraAnchor == null)
            return;

        _hudRoot.position = cameraAnchor.position
            + cameraAnchor.forward * hudDistance
            + cameraAnchor.right   * hudRightMeters
            + cameraAnchor.up      * hudUpMeters;

        _hudRoot.rotation = Quaternion.LookRotation(cameraAnchor.forward, cameraAnchor.up);

        var canvas = _hudRoot.GetComponentInChildren<Canvas>();
        if (canvas != null)
        {
            var cam = cameraAnchor.GetComponent<Camera>();
            if (cam != null)
                canvas.worldCamera = cam;
        }
    }

    static RectTransform MakeUiRect(string name, RectTransform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    static Image AddImage(RectTransform rt, Color color)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static Text AddText(RectTransform rt, string text, int fontSize, TextAnchor alignment, Color color)
    {
        var t = rt.gameObject.AddComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.alignment = alignment;
        t.color = color;
        t.raycastTarget = false;
        return t;
    }

    void OnDestroy()
    {
        if (_mat != null)
            Destroy(_mat);
    }
}
