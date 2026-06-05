using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// OrientationDegree experiment.
///
/// Projects a single image onto the arm surface and displays a floating HUD
/// showing the elevation angle of the forearm (0° = horizontal, 90° = vertical).
///
/// Axis source: ForearmDepthSurface.AxisDir — wrist→elbow from the body
/// skeleton's left-arm bones (wrist 19, elbow 11 on the IOBT rig).
///
/// Elevation = Asin(axis.y): immune to Z-rotation (pronation) and head
/// rotation since it only reads the world-up component of the forearm axis.
/// Note: the body skeleton's elbow is estimated (not directly observed by
/// cameras), which can introduce a systematic offset of ~10-20°.
/// </summary>
[DefaultExecutionOrder(110)]
public class OrientationDegreeController : MonoBehaviour
{
    [Header("Surface")]
    public ForearmDepthSurface surface;

    [Header("Shader — drag ForearmImageDisplay.shader here")]
    public Shader imageShader;

    [Header("Image")]
    public Texture2D image;

    [Header("Image Layout on Arm")]
    [Range(0.05f, 1f)]   public float imageScale         = 0.6f;
    [Range(-0.5f, 0.5f)] public float imageCenterOffsetU = 0f;
    [Range(-0.5f, 0.5f)] public float imageCenterOffsetV = 0f;

    [Header("Angle")]
    [Tooltip("Flip the forearm axis direction if the angle reads inverted.")]
    public bool flipAxis = false;
    [Tooltip("Smoothing time constant (s). Higher = more lag but less jitter.")]
    [Min(0f)] public float smoothingTime = 0.25f;
    [Tooltip("Ignore raw angle changes smaller than this (degrees). Suppresses micro-drift.")]
    [Min(0f)] public float deadbandDegrees = 2.5f;
    [Tooltip("While the headset rotates faster than this (deg/sec), freeze the angle. " +
             "Quest body tracking nudges the estimated elbow when the head turns, so we hold " +
             "the last reading until the head settles. Set to 0 to disable the freeze.")]
    [Min(0f)] public float headMotionFreezeDegPerSec = 25f;

    [Header("HUD")]
    [Tooltip("Eye anchor the HUD floats in front of. Auto-resolved if empty.")]
    public Transform cameraAnchor;
    [Min(0.1f)] public float hudDistance    = 0.6f;
    public float             hudRightMeters = 0.22f;
    public float             hudUpMeters    = 0.15f;

    // ── Runtime ───────────────────────────────────────────────────────────────

    Material  _mat;
    Transform _hudRoot;
    Text      _angleText;
    Text      _debugText;
    float     _smoothedAngle;
    float     _targetAngle;
    Quaternion _prevHeadRot;
    bool       _haveHeadRot;

    static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    static readonly int ScaleId   = Shader.PropertyToID("_ImageScale");
    static readonly int OffsetUId = Shader.PropertyToID("_ImageOffsetU");
    static readonly int OffsetVId = Shader.PropertyToID("_ImageOffsetV");

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (cameraAnchor == null && surface != null)
            cameraAnchor = surface.centerEyeAnchor;
        if (cameraAnchor == null && Camera.main != null)
            cameraAnchor = Camera.main.transform;

        SetupArmImage();
        BuildHUD();
    }

    void LateUpdate()
    {
        ApplyLayout();
        UpdateAngle();
        PositionHUD();
    }

    // ── Image ─────────────────────────────────────────────────────────────────

    void SetupArmImage()
    {
        if (surface == null) return;

        Shader sh = imageShader != null
            ? imageShader
            : Shader.Find("Custom/ForearmImageDisplay");
        if (sh == null) { Debug.LogError("[OrientationDegree] Shader not found."); return; }

        _mat = new Material(sh) { name = "OrientationImageMat" };
        if (image != null) _mat.SetTexture(MainTexId, image);

        var mr = surface.GetComponent<MeshRenderer>()
              ?? surface.GetComponentInChildren<MeshRenderer>();
        if (mr != null) mr.material = _mat;

        ApplyLayout();
    }

    void ApplyLayout()
    {
        if (_mat == null) return;
        _mat.SetFloat(ScaleId,   imageScale);
        _mat.SetFloat(OffsetUId, imageCenterOffsetU);
        _mat.SetFloat(OffsetVId, imageCenterOffsetV);
    }

    // ── Angle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns false while the headset is rotating faster than the freeze
    /// threshold, so the caller can hold the last angle. Always true when the
    /// threshold is 0 (freeze disabled) or no camera anchor is available.
    /// </summary>
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

    void UpdateAngle()
    {
        if (_angleText == null) return;
        if (surface == null || surface.AxisDir.sqrMagnitude < 0.0001f)
        {
            _angleText.text = "--";
            return;
        }

        Vector3 axis = (flipAxis ? -surface.AxisDir : surface.AxisDir).normalized;

        // Elevation: angle between the forearm and the world horizontal plane.
        // Pure world space — only the world-up (Y) component matters, so it is
        // mathematically independent of camera orientation.
        float angle = Mathf.Abs(Mathf.Asin(Mathf.Clamp(axis.y, -1f, 1f)) * Mathf.Rad2Deg);

        // Head-motion freeze: Quest body tracking shifts the estimated elbow when
        // the head turns, so hold the last reading while the headset is rotating.
        bool headSteady = IsHeadSteady();

        // Deadband: only move the target when the change exceeds the threshold
        // AND the head is steady enough to trust the reading.
        if (headSteady && Mathf.Abs(angle - _targetAngle) > deadbandDegrees)
            _targetAngle = angle;

        float alpha = smoothingTime > 0.001f
            ? 1f - Mathf.Exp(-Time.deltaTime / smoothingTime)
            : 1f;
        _smoothedAngle = Mathf.Lerp(_smoothedAngle, _targetAngle, alpha);

        _angleText.text = Mathf.RoundToInt(_smoothedAngle) + " deg";

        if (_debugText != null)
            _debugText.text = $"x:{axis.x:F2} y:{axis.y:F2} z:{axis.z:F2}";
    }

    // ── HUD ───────────────────────────────────────────────────────────────────

    void BuildHUD()
    {
        _hudRoot = new GameObject("OrientationHUD").transform;

        var cvGo = new GameObject("Canvas", typeof(Canvas));
        cvGo.transform.SetParent(_hudRoot, false);
        cvGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var rt = cvGo.GetComponent<RectTransform>();
        rt.sizeDelta     = new Vector2(400, 160);
        rt.localScale    = Vector3.one * 0.0005f;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;

        var bgGo = MakeRectChild("BG", rt, new Vector2(400, 160));
        bgGo.anchoredPosition = Vector2.zero;
        bgGo.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.70f);

        var txGo = MakeRectChild("AngleTxt", rt, new Vector2(380, 90));
        txGo.anchoredPosition = new Vector2(0, 30);
        _angleText            = txGo.gameObject.AddComponent<Text>();
        _angleText.font       = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _angleText.fontSize   = 72;
        _angleText.alignment  = TextAnchor.MiddleCenter;
        _angleText.color      = Color.white;
        _angleText.text       = "--";
        _angleText.raycastTarget = false;

        var dbGo = MakeRectChild("DebugTxt", rt, new Vector2(380, 44));
        dbGo.anchoredPosition = new Vector2(0, -50);
        _debugText            = dbGo.gameObject.AddComponent<Text>();
        _debugText.font       = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _debugText.fontSize   = 28;
        _debugText.alignment  = TextAnchor.MiddleCenter;
        _debugText.color      = new Color(1f, 1f, 0.4f, 1f);
        _debugText.text       = "x:- y:- z:-";
        _debugText.raycastTarget = false;
    }

    void PositionHUD()
    {
        if (_hudRoot == null || cameraAnchor == null) return;

        _hudRoot.position = cameraAnchor.position
            + cameraAnchor.forward * hudDistance
            + cameraAnchor.right   * hudRightMeters
            + cameraAnchor.up      * hudUpMeters;

        _hudRoot.rotation = Quaternion.LookRotation(cameraAnchor.forward, cameraAnchor.up);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    static RectTransform MakeRectChild(string name, RectTransform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.sizeDelta  = size;
        rt.localScale = Vector3.one;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot      = new Vector2(0.5f, 0.5f);
        return rt;
    }
}
