using System;
using System.Collections.Generic;
using Experiments.Cli;
using UnityEngine;
using UnityEngine.Serialization;
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
/// expctl: switch, angle, calibrate point 0|45|90, calibrate apply, calibrate status, calibrate clear.
/// Per-image scale, offset, and rotation are set in the Inspector.
/// Single bone anchor + bone-fixed UV for stable placement on the forearm.
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

        [Tooltip("Pin the image to a bone-stable point on the forearm (OrientationDegreeBoneUV).")]
        public bool trackArmAnchor = true;
        [Tooltip("Anchor distance from the wrist along the forearm axis (m).")]
        [FormerlySerializedAs("baseAlongArm")]
        public float anchorAlongArm = 0.09f;
        [Tooltip("Anchor offset from the arm axis toward the camera-facing (dorsal) side (m).")]
        public float anchorRadialArm = 0.05f;
        [Tooltip("Anchor offset around the arm circumference (m). Positive = wrist.up side.")]
        public float anchorLateralArm = 0f;

        [Tooltip("Fine-tune in UV space. Used as fixed center when trackArmAnchor is off; " +
                 "added on top of the arm anchor when on.")]
        [Range(-0.5f, 0.5f)] public float centerOffsetU = 0f;
        [Range(-0.5f, 0.5f)] public float centerOffsetV = 0f;
        [Tooltip("Extra rotation on the arm (degrees, CCW in UV space). Added on top of wrist aim.")]
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
    [Tooltip("Smooth arm-anchored UV center (s). Reduces jitter from depth mesh noise.")]
    [Min(0f)] public float placementSmoothingTime = 0.03f;
    [Tooltip("When arm anchor is on, align stamp +V toward the wrist (bone UV estimate).")]
    public bool alignImageTowardWrist = false;
    [Tooltip("Blend arm elevation angle into stamp rotation (0 = base→wrist aim only).")]
    [Range(0f, 1f)] public float elevationRotationBlend = 0f;
    [Tooltip("Optional extra rotation filter (s). 0 = use anchor smoothing only.")]
    [Min(0f)] public float rotationSmoothingTime = 0f;

    [Header("Angle (world elevation — body wrist→elbow)")]
    [Tooltip("Single-offset mode only. Multi-point apply sets scale + offset automatically.")]
    public float angleOffsetDegrees;
    [Tooltip("Linear fit scale (corrected = scale × raw + offset). 1 = no stretch.")]
    public float angleScale = 1f;
    public OrientationDegreeCalibration.Mode calibrationMode =
        OrientationDegreeCalibration.Mode.SingleOffset;
    [Tooltip("Captured iPhone reference poses. Fill via expctl calibrate point 0|45|90.")]
    public OrientationDegreeCalibration.Point[] calibrationPoints =
    {
        new OrientationDegreeCalibration.Point(0f),
        new OrientationDegreeCalibration.Point(45f),
        new OrientationDegreeCalibration.Point(90f),
    };
    [Tooltip("Smooth body wrist position (s) before axis is computed.")]
    [Min(0f)] public float wristSmoothingTime = 0.04f;
    [Tooltip("Smooth body elbow position (s). Slower = less head-motion coupling (elbow estimate drifts with headset).")]
    [Min(0f)] public float elbowSmoothingTime = 0.45f;
    [Tooltip("Flip the measured direction if the angle reads inverted.")]
    public bool flipAxis = false;
    [Tooltip("Smoothing time constant (s). Higher = more lag but less jitter.")]
    [Min(0f)] public float smoothingTime = 0.25f;
    [Tooltip("Ignore raw angle changes smaller than this (degrees). Suppresses micro-drift.")]
    [Min(0f)] public float deadbandDegrees = 2.5f;
    [Tooltip("While the headset rotates faster than this (deg/sec), hold the last angle and bone estimates.")]
    [Min(0f)] public float headMotionFreezeDegPerSec = 10f;

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
    Vector3 _smoothedWristWorld;
    Vector3 _smoothedElbowWorld;
    bool _haveBonePositionSample;
    bool _angleFrozenByHead;
    float _smoothedCenterOffsetU;
    float _smoothedCenterOffsetV;
    float _smoothedImageRotationRad;
    bool _havePlacementSample;
    bool _haveRotationSample;
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
        commands["calibrate"] = HandleCalibrateCommand;
        commands["offset"] = HandleOffsetCommand;
    }

    string HandleCalibrateCommand(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("0", out string sub))
            return CalibrateUsage();

        string key = sub.Trim().ToLowerInvariant();
        switch (key)
        {
            case "clear":
                ClearCalibration();
                return "calibration cleared (single-offset mode)";
            case "status":
                return OrientationDegreeCalibration.FormatStatus(
                    calibrationMode, angleScale, angleOffsetDegrees, calibrationPoints);
            case "apply":
                return ApplyCalibration(args.TryGetValue("1", out string modeArg) ? modeArg : "linear");
            case "point":
                if (!args.TryGetValue("1", out string refText) ||
                    !float.TryParse(refText, out float referenceDegrees))
                    return "usage: calibrate point <0|45|90>";
                return CaptureCalibrationPoint(referenceDegrees);
            default:
                if (!float.TryParse(sub, out float legacyRef))
                    return CalibrateUsage();
                return ApplySingleOffsetCalibration(legacyRef);
        }
    }

    static string CalibrateUsage()
    {
        return "usage: calibrate point 0|45|90  |  calibrate apply [linear|piecewise]  |  " +
               "calibrate status  |  calibrate clear  |  calibrate <ref> (single offset)";
    }

    void ClearCalibration()
    {
        calibrationMode = OrientationDegreeCalibration.Mode.SingleOffset;
        angleScale = 1f;
        angleOffsetDegrees = 0f;
        for (int i = 0; i < calibrationPoints.Length; i++)
            calibrationPoints[i] = new OrientationDegreeCalibration.Point(calibrationPoints[i].referenceDegrees);
    }

    string CaptureCalibrationPoint(float referenceDegrees)
    {
        SampleAndSmoothAngle();
        int slot = FindCalibrationSlot(referenceDegrees);
        if (slot < 0)
            return "no calibration slot (expected 0, 45, or 90)";

        calibrationPoints[slot] = new OrientationDegreeCalibration.Point(referenceDegrees)
        {
            rawDegrees = _rawAngle,
            captured = true,
        };

        string msg = $"captured {referenceDegrees:F0}° raw={_rawAngle:F1} " +
                     $"({OrientationDegreeCalibration.CapturedCount(calibrationPoints)}/3)";
        Debug.Log("[OrientationDegree] " + msg);
        return msg;
    }

    int FindCalibrationSlot(float referenceDegrees)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < calibrationPoints.Length; i++)
        {
            float dist = Mathf.Abs(calibrationPoints[i].referenceDegrees - referenceDegrees);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return bestDist <= 5f ? best : -1;
    }

    string ApplyCalibration(string modeArg)
    {
        int captured = OrientationDegreeCalibration.CapturedCount(calibrationPoints);
        if (captured < 2)
            return $"need at least 2 points (have {captured}). Use: calibrate point 0|45|90";

        string mode = modeArg.Trim().ToLowerInvariant();
        if (mode is "piecewise" or "piece")
        {
            calibrationMode = OrientationDegreeCalibration.Mode.PiecewiseLinear;
            RefreshCorrectedAngleImmediate();
            string msg = "piecewise calibration applied — " +
                         OrientationDegreeCalibration.FormatStatus(
                             calibrationMode, angleScale, angleOffsetDegrees, calibrationPoints);
            Debug.Log("[OrientationDegree] " + msg);
            return msg;
        }

        if (!OrientationDegreeCalibration.TryFitLinear(
                calibrationPoints, out float scale, out float offset))
            return "linear fit failed";

        angleScale = scale;
        angleOffsetDegrees = offset;
        calibrationMode = OrientationDegreeCalibration.Mode.LinearScaleOffset;
        RefreshCorrectedAngleImmediate();
        string linearMsg = $"linear fit scale={scale:F3} offset={offset:F1}° (R² over {captured} pts)";
        Debug.Log("[OrientationDegree] " + linearMsg);
        return linearMsg;
    }

    string ApplySingleOffsetCalibration(float referenceDegrees)
    {
        SampleAndSmoothAngle();
        calibrationMode = OrientationDegreeCalibration.Mode.SingleOffset;
        angleScale = 1f;
        angleOffsetDegrees = referenceDegrees - _rawAngle;
        RefreshCorrectedAngleImmediate();
        string msg = $"single offset={angleOffsetDegrees:F1}° (ref={referenceDegrees:F0} raw={_rawAngle:F1})";
        Debug.Log("[OrientationDegree] " + msg);
        return msg;
    }

    void RefreshCorrectedAngleImmediate()
    {
        float corrected = CorrectRawAngle(_rawAngle);
        _targetAngle = corrected;
        _smoothedAngle = corrected;
    }

    float CorrectRawAngle(float rawDegrees) =>
        OrientationDegreeCalibration.Apply(
            rawDegrees, calibrationMode, angleScale, angleOffsetDegrees, calibrationPoints);

    string CalibrationSummary()
    {
        switch (calibrationMode)
        {
            case OrientationDegreeCalibration.Mode.LinearScaleOffset:
                return $"×{angleScale:F2} {angleOffsetDegrees:+0;-0}°";
            case OrientationDegreeCalibration.Mode.PiecewiseLinear:
                return $"piecewise {OrientationDegreeCalibration.CapturedCount(calibrationPoints)}/3";
            default:
                return $"off {angleOffsetDegrees:+0;-0}°";
        }
    }

    string HandleOffsetCommand(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("0", out string offText) || !float.TryParse(offText, out float degrees))
            return CalibrationSummary();

        calibrationMode = OrientationDegreeCalibration.Mode.SingleOffset;
        angleScale = 1f;
        angleOffsetDegrees = degrees;
        return CalibrationSummary();
    }

    string SwitchImage()
    {
        if (image == null && imageAlt == null)
            return "error: assign image and imageAlt in Inspector";

        _usingAltImage = !_usingAltImage;
        _havePlacementSample = false;
        _haveRotationSample = false;
        ApplyActiveTexture();
        ApplyLayout();
        string name = ActiveTexture != null ? ActiveTexture.name : "(none)";
        Debug.Log($"[OrientationDegree] switched to {name}");
        return "image: " + name;
    }

    string LogAngle()
    {
        SampleAndSmoothAngle();
        int corrected = Mathf.RoundToInt(CorrectRawAngle(_rawAngle));
        int smoothed = Mathf.RoundToInt(_smoothedAngle);
        string msg = surface != null && surface.IsValid
            ? $"angle={smoothed} corrected={corrected} raw={Mathf.RoundToInt(_rawAngle)} {CalibrationSummary()}"
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
        float rotationRad = ResolveImageRotationRadians(layout);
        _mat.SetFloat(ScaleId, layout.scale);
        _mat.SetFloat(OffsetUId, offsetU);
        _mat.SetFloat(OffsetVId, offsetV);
        if (_mat.HasProperty(ImageAspectId))
            _mat.SetFloat(ImageAspectId, ResolveContentAspect(tex, layout));
        if (_mat.HasProperty(ImageRotationId))
            _mat.SetFloat(ImageRotationId, rotationRad);
    }

    float ResolveImageRotationRadians(ArmImageLayout layout)
    {
        float rotationRad = layout.rotationDegrees * Mathf.Deg2Rad;

        if (layout.trackArmAnchor && alignImageTowardWrist && surface != null && surface.IsValid)
        {
            Vector3 anchor = ResolveBoneStableAnchor(layout, out Vector3 boneLateral);
            float pronationScroll = surface.PronationAngle / (2f * Mathf.PI);
            rotationRad += OrientationDegreeBoneUV.ComputeTowardWristRotationRadians(
                anchor,
                surface.WristPosition,
                surface.AxisDir,
                boneLateral,
                pronationScroll,
                surface.displayOffset,
                surface.displayWidth,
                surface.displayHeight);

            if (elevationRotationBlend > 0f)
            {
                SampleAndSmoothAngle();
                rotationRad += _smoothedAngle * Mathf.Deg2Rad * elevationRotationBlend;
            }
        }

        if (rotationSmoothingTime > 0.001f)
        {
            if (!_haveRotationSample)
            {
                _smoothedImageRotationRad = rotationRad;
                _haveRotationSample = true;
            }
            else
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / rotationSmoothingTime);
                _smoothedImageRotationRad = Mathf.LerpAngle(
                    _smoothedImageRotationRad * Mathf.Rad2Deg,
                    rotationRad * Mathf.Rad2Deg,
                    alpha) * Mathf.Deg2Rad;
            }

            rotationRad = _smoothedImageRotationRad;
        }

        return rotationRad;
    }

    void ResolveCenterOffsets(ArmImageLayout layout, out float offsetU, out float offsetV)
    {
        float pronationScroll = surface != null && surface.IsValid
            ? surface.PronationAngle / (2f * Mathf.PI)
            : 0f;

        if (layout.trackArmAnchor && surface != null && surface.IsValid)
        {
            Vector3 anchor = ResolveBoneStableAnchor(layout, out Vector3 boneLateral);
            Vector2 uv = OrientationDegreeBoneUV.Compute(
                anchor,
                surface.WristPosition,
                surface.AxisDir,
                boneLateral,
                pronationScroll,
                surface.displayOffset,
                surface.displayWidth,
                surface.displayHeight);
            offsetU = uv.x - 0.5f + layout.centerOffsetU;
            offsetV = uv.y - 0.5f + layout.centerOffsetV;
        }
        else
        {
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

    Vector3 ResolveBoneStableAnchor(ArmImageLayout layout, out Vector3 boneLateral)
    {
        TryGetWristTransform(out Transform wrist);
        if (!OrientationDegreeBoneUV.TryGetBoneLateral(surface.AxisDir, wrist, surface.AxisUp, out boneLateral))
            boneLateral = surface.AxisRight;

        return OrientationDegreeBoneUV.ResolveAnchorWorld(
            surface.WristPosition,
            surface.AxisDir,
            wrist,
            surface.AxisUp,
            layout.anchorAlongArm,
            layout.anchorRadialArm,
            layout.anchorLateralArm);
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

    void SampleAndSmoothAngle()
    {
        if (surface == null || !surface.IsValid)
        {
            _rawAngle = 0f;
            _angleFrozenByHead = false;
            return;
        }

        bool headSteady = IsHeadSteady();
        _angleFrozenByHead = !headSteady;

        if (!headSteady)
            return;

        Vector3 rawWrist = surface.WristPosition;
        Vector3 rawElbow = surface.ElbowPosition;

        if (!_haveBonePositionSample)
        {
            _smoothedWristWorld = rawWrist;
            _smoothedElbowWorld = rawElbow;
            _haveBonePositionSample = true;
        }
        else
        {
            if (wristSmoothingTime > 0.001f)
            {
                float alphaW = 1f - Mathf.Exp(-Time.deltaTime / wristSmoothingTime);
                _smoothedWristWorld = Vector3.Lerp(_smoothedWristWorld, rawWrist, alphaW);
            }
            else
            {
                _smoothedWristWorld = rawWrist;
            }

            if (elbowSmoothingTime > 0.001f)
            {
                float alphaE = 1f - Mathf.Exp(-Time.deltaTime / elbowSmoothingTime);
                _smoothedElbowWorld = Vector3.Lerp(_smoothedElbowWorld, rawElbow, alphaE);
            }
            else
            {
                _smoothedElbowWorld = rawElbow;
            }
        }

        if (!OrientationDegreeAngle.TryDirectionFromBonePositions(
                _smoothedWristWorld, _smoothedElbowWorld, flipAxis, out Vector3 direction))
        {
            return;
        }

        _lastAxis = direction;
        float angle = OrientationDegreeAngle.ElevationFromHorizontalDegrees(direction);
        _rawAngle = angle;
        float corrected = CorrectRawAngle(angle);

        if (!_haveAngleSample)
        {
            _targetAngle = corrected;
            _smoothedAngle = corrected;
            _haveAngleSample = true;
        }
        else if (Mathf.Abs(corrected - _targetAngle) > deadbandDegrees)
        {
            _targetAngle = corrected;
        }

        float alphaSmooth = smoothingTime > 0.001f
            ? 1f - Mathf.Exp(-Time.deltaTime / smoothingTime)
            : 1f;
        _smoothedAngle = Mathf.Lerp(_smoothedAngle, _targetAngle, alphaSmooth);
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
            string freeze = _angleFrozenByHead ? "  [head frozen]" : "";
            _debugText.text =
                $"raw {_rawAngle:F0}°  {CalibrationSummary()}{freeze}";
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
        AddText(hintRect, "Cal: point 0|45|90 then apply", 16, TextAnchor.MiddleCenter,
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
