using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates and manages a self-contained floating world-space colour-picker panel
/// (experiment2_ArmText only).
///
/// Layout (200 × 280 canvas units, scale ≈ 0.0006 → ~12 cm × 17 cm world):
///   • "Color Picker" title at top
///   • HSV colour wheel (160 × 160 units)
///   • Brightness strip (160 × 28 units)
///   • Colour preview swatch at bottom
///
/// Interaction: right index fingertip proximity is checked each frame.
/// A press (depth &lt; <see cref="pressDepthMeters"/>) on the wheel sets hue+sat;
/// a press on the strip sets brightness. The combined colour is broadcast via
/// <see cref="OnColorChanged"/>.
/// </summary>
public class Experiment2ColorPickerPanel : MonoBehaviour
{
    // ── Public settings ───────────────────────────────────────────────────────
    [Tooltip("Finger depth (meters) from panel plane that counts as a press.")]
    [Min(0.005f)] public float pressDepthMeters = 0.025f;

    [Tooltip("Finger depth (meters) from panel plane that counts as hover.")]
    [Min(0.005f)] public float hoverDepthMeters = 0.05f;

    [Tooltip("Canvas units per world metre for the panel. 0.0006 → 200 px ≈ 12 cm.")]
    public float panelWorldScale = 0.0006f;

    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Fired every frame the colour changes (hover or press).</summary>
    public event Action<Color> OnColorChanged;

    // ── References (set by Experiment2Controller) ─────────────────────────────
    [HideInInspector] public HandTrackingController handTracking;
    [HideInInspector] public Font labelFont;

    // ── State ─────────────────────────────────────────────────────────────────
    public Color SelectedColor { get; private set; } = Color.white;

    GameObject       _panelRoot;
    Canvas           _panelCanvas;
    RectTransform    _canvasRect;

    Experiment2ColorWheelGraphic   _wheelGraphic;
    Experiment2BrightnessBarGraphic _brightnessGraphic;
    Image                           _previewImage;
    Image                           _wheelHoverDot;
    Image                           _brightnessHoverDot;

    float _currentHue        = 0f;
    float _currentSaturation = 1f;
    float _currentBrightness = 1f;

    bool  _wasPress;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Call once after awarding references to build the panel hierarchy.
    /// </summary>
    public void BuildPanel()
    {
        _panelRoot = new GameObject("Exp2_ColorPickerPanel");
        _panelRoot.transform.SetParent(transform, false);

        // World-space canvas
        _panelCanvas = _panelRoot.AddComponent<Canvas>();
        _panelCanvas.renderMode = RenderMode.WorldSpace;

        _canvasRect = _panelRoot.GetComponent<RectTransform>();
        _canvasRect.sizeDelta = new Vector2(200f, 310f);
        // Negative X un-mirrors world-space UI so text and the colour wheel
        // read correctly from the user's perspective (same convention as
        // PossibleUIPaletteController.ApplyPanelScale). InverseTransformPoint
        // in UpdateFingerOnPanel already accounts for this, so no other
        // coordinate-mapping changes are needed.
        _canvasRect.localScale = new Vector3(-panelWorldScale, panelWorldScale, panelWorldScale);

        BuildPanelChildren();

        ApplyColor();
    }

    void BuildPanelChildren()
    {
        // Background
        var bg = AddRect(_canvasRect, "Background");
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = bg.offsetMax = Vector2.zero;
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0.08f, 0.08f, 0.12f, 0.95f);
        bgImg.raycastTarget = false;

        // Title
        var titleRect = AddCentred(_canvasRect, "Title", new Vector2(0f, 120f), new Vector2(180f, 24f));
        var titleTxt  = titleRect.gameObject.AddComponent<Text>();
        titleTxt.text      = "Color Picker";
        titleTxt.alignment = TextAnchor.MiddleCenter;
        titleTxt.color     = Color.white;
        titleTxt.fontSize  = 18;
        titleTxt.font      = labelFont;
        titleTxt.raycastTarget = false;

        // Color wheel
        var wheelRect = AddCentred(_canvasRect, "ColorWheel", new Vector2(0f, 25f), new Vector2(160f, 160f));
        _wheelGraphic = wheelRect.gameObject.AddComponent<Experiment2ColorWheelGraphic>();
        _wheelGraphic.raycastTarget = false;

        // Small dot that tracks finger position on the wheel
        _wheelHoverDot = AddCircleDot(_canvasRect, "WheelCursor", 7f);
        _wheelHoverDot.gameObject.SetActive(false);

        // Brightness strip
        var stripRect = AddCentred(_canvasRect, "BrightnessStrip", new Vector2(0f, -88f), new Vector2(160f, 28f));
        _brightnessGraphic = stripRect.gameObject.AddComponent<Experiment2BrightnessBarGraphic>();
        _brightnessGraphic.raycastTarget = false;

        // Brightness cursor dot
        _brightnessHoverDot = AddCircleDot(_canvasRect, "BrightnessCursor", 6f);
        _brightnessHoverDot.gameObject.SetActive(false);

        // Brightness label — sits ABOVE the brightness strip
        var bLabelRect = AddCentred(_canvasRect, "BrightnessLabel", new Vector2(0f, -65f), new Vector2(160f, 14f));
        var bLabel = bLabelRect.gameObject.AddComponent<Text>();
        bLabel.text      = "Brightness";
        bLabel.alignment = TextAnchor.MiddleCenter;
        bLabel.color     = new Color(0.75f, 0.75f, 0.75f, 1f);
        bLabel.fontSize  = 12;
        bLabel.font      = labelFont;
        bLabel.raycastTarget = false;

        // "Final Color" label — sits below the brightness strip, above the swatch
        var fcLabelRect = AddCentred(_canvasRect, "FinalColorLabel", new Vector2(0f, -112f), new Vector2(160f, 14f));
        var fcLabel = fcLabelRect.gameObject.AddComponent<Text>();
        fcLabel.text      = "Final Color";
        fcLabel.alignment = TextAnchor.MiddleCenter;
        fcLabel.color     = new Color(0.75f, 0.75f, 0.75f, 1f);
        fcLabel.fontSize  = 12;
        fcLabel.font      = labelFont;
        fcLabel.raycastTarget = false;

        // Preview swatch
        var swatchRect = AddCentred(_canvasRect, "Preview", new Vector2(0f, -136f), new Vector2(100f, 22f));
        _previewImage = swatchRect.gameObject.AddComponent<Image>();
        _previewImage.color         = SelectedColor;
        _previewImage.raycastTarget = false;

        // Thin border around swatch
        var borderRect = AddCentred(_canvasRect, "PreviewBorder", new Vector2(0f, -136f), new Vector2(104f, 26f));
        var borderImg  = borderRect.gameObject.AddComponent<Image>();
        borderImg.color         = new Color(1f, 1f, 1f, 0.5f);
        borderImg.raycastTarget = false;
        borderRect.SetSiblingIndex(swatchRect.GetSiblingIndex()); // border behind swatch
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (_panelRoot == null || handTracking == null) return;
        if (!handTracking.isRightHandTracked || handTracking.rightIndexTip == null) return;

        Vector3 fingerWorld = handTracking.rightIndexTip.position;
        UpdateFingerOnPanel(fingerWorld);
    }

    void UpdateFingerOnPanel(Vector3 fingerWorld)
    {
        // Transform finger into panel-canvas local space.
        // local.x / local.y are in canvas units; local.z is also in canvas units.
        // Convert z back to world metres before comparing against the metre thresholds.
        Vector3 local = _canvasRect.InverseTransformPoint(fingerWorld);
        Vector2 canvasPos = new Vector2(local.x, local.y);
        float depthMeters = Mathf.Abs(local.z) * panelWorldScale;

        bool near  = depthMeters < hoverDepthMeters;
        bool press = depthMeters < pressDepthMeters;
        bool pressBegan = press && !_wasPress;
        _wasPress = press;

        // Hide cursors when far away.
        if (!near)
        {
            _wheelHoverDot.gameObject.SetActive(false);
            _brightnessHoverDot.gameObject.SetActive(false);
            return;
        }

        // --- Colour wheel ---
        RectTransform wheelRect = _wheelGraphic.rectTransform;
        Vector2 wheelLocal = canvasPos - (Vector2)wheelRect.anchoredPosition;

        if (_wheelGraphic.TryGetColorAt(wheelLocal, out Color wheelColor, out float h, out float s))
        {
            // Show hover cursor on wheel.
            _wheelHoverDot.gameObject.SetActive(true);
            _wheelHoverDot.rectTransform.anchoredPosition = canvasPos;
            _wheelHoverDot.color = new Color(1f - wheelColor.r, 1f - wheelColor.g, 1f - wheelColor.b, 1f); // contrast

            if (pressBegan)
            {
                _currentHue        = h;
                _currentSaturation = s;
                _wheelGraphic.Brightness = _currentBrightness;
                _brightnessGraphic.HueColor = Color.HSVToRGB(h, s, 1f);
                ApplyColor();
            }

            _brightnessHoverDot.gameObject.SetActive(false);
            return;
        }

        _wheelHoverDot.gameObject.SetActive(false);

        // --- Brightness strip ---
        RectTransform stripRect = _brightnessGraphic.rectTransform;
        Vector2 stripLocal = canvasPos - (Vector2)stripRect.anchoredPosition;

        if (_brightnessGraphic.TryGetBrightnessAt(stripLocal, out float brightness))
        {
            _brightnessHoverDot.gameObject.SetActive(true);
            _brightnessHoverDot.rectTransform.anchoredPosition = canvasPos;

            if (pressBegan)
            {
                _currentBrightness   = brightness;
                _wheelGraphic.Brightness = brightness;
                _brightnessGraphic.HueColor = Color.HSVToRGB(_currentHue, _currentSaturation, 1f);
                ApplyColor();
            }

            return;
        }

        _brightnessHoverDot.gameObject.SetActive(false);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Moves the panel so it floats at eye level to the right of the head.</summary>
    public void AnchorToHead(Transform head, float forwardMeters = 0.5f, float rightMeters = 0.35f)
    {
        if (_panelRoot == null || head == null) return;

        Vector3 fwd   = new Vector3(head.forward.x, 0f, head.forward.z).normalized;
        Vector3 right = new Vector3(head.right.x,   0f, head.right.z).normalized;

        Vector3 pos = head.position
                    + fwd   * forwardMeters
                    + right * rightMeters;
        pos.y = head.position.y; // eye level

        _panelRoot.transform.position = pos;

        // Panel faces the user.
        Vector3 toHead = head.position - pos;
        toHead.y = 0f;
        if (toHead.sqrMagnitude > 0.001f)
            _panelRoot.transform.rotation = Quaternion.LookRotation(toHead.normalized, Vector3.up);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void ApplyColor()
    {
        Color c = Color.HSVToRGB(_currentHue, _currentSaturation, _currentBrightness);
        c.a = 1f;
        SelectedColor = c;
        if (_previewImage != null) _previewImage.color = SelectedColor;
        OnColorChanged?.Invoke(SelectedColor);
    }

    static RectTransform AddRect(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static RectTransform AddCentred(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        var rt = AddRect(parent, name);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
        return rt;
    }

    Image AddCircleDot(Transform parent, string name, float radius)
    {
        var rt  = AddCentred(parent, name, Vector2.zero, Vector2.one * radius * 2f);
        var img = rt.gameObject.AddComponent<Image>();
        img.color         = Color.white;
        img.raycastTarget = false;
        return img;
    }
}
