using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// experiment2_ArmText: renders configurable text on the forearm canvas and lets
/// the user adjust its colour via a floating HSV colour picker.
///
/// Text is rendered through the arm's existing RenderTexture → cylinder pipeline
/// so it rotates/moves with the arm automatically.  The text element is set to the
/// same layer as the arm canvas, which is the layer the dedicated RT camera renders.
///
/// All behaviour is guarded to this scene only.
/// </summary>
[DefaultExecutionOrder(95)]
public class Experiment2Controller : MonoBehaviour
{
    // ── Inspector fields ──────────────────────────────────────────────────────

    [Header("References — auto-resolved when empty")]
    public ArmLayoutController    armLayout;
    public ForearmTouchManager    forearmTouch;
    public HandTrackingController handTracking;

    [Header("Arm text")]
    [Tooltip("Optional: drag your scene text/graphic here. " +
             "If empty the controller finds 'colorDependentText' automatically.")]
    public Graphic armTextReference;

    [Header("Colour picker panel")]
    [Tooltip("Metres forward from the head when the panel is anchored.")]
    [Min(0.1f)] public float pickerForwardMeters = 0.5f;

    [Tooltip("Metres to the right from the head when the panel is anchored.")]
    [Min(0.0f)] public float pickerRightMeters = 0.35f;

    [Tooltip("Optional font for picker labels. Leave empty for built-in default.")]
    public Font pickerLabelFont;

    // ── Runtime state ─────────────────────────────────────────────────────────

    Graphic                     _armText;
    Experiment2ColorPickerPanel _pickerPanel;

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void RegisterSceneHook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryEnsureForScene(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => TryEnsureForScene(scene);

    static void TryEnsureForScene(Scene scene)
    {
        if (!scene.IsValid() || scene.name != ExperimentScenes.Experiment2)
            return;

        if (FindObjectOfType<Experiment2Controller>() != null)
            return;

        var root = new GameObject("Experiment2");
        //root.AddComponent<Experiment2SceneBootstrap>();
        root.AddComponent<Experiment2Controller>();
        Debug.Log("[Experiment2] Runtime bootstrap created controller.");
    }

    // ── MonoBehaviour ─────────────────────────────────────────────────────────

    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment2)
        {
            enabled = false;
            return;
        }

        ResolveReferences();
    }

    void Start()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment2)
            return;

        DisableForearmPickInteraction();
        SetupArmText();
        SetupColorPickerPanel();
    }

    // ── Reference resolution ──────────────────────────────────────────────────

    void ResolveReferences()
    {
        if (armLayout    == null) armLayout    = FindObjectOfType<ArmLayoutController>();
        if (forearmTouch == null) forearmTouch = FindObjectOfType<ForearmTouchManager>();
        if (handTracking == null) handTracking = FindObjectOfType<HandTrackingController>();
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    void DisableForearmPickInteraction()
    {
        if (forearmTouch != null)
            forearmTouch.enabled = false;

        // Disable the dot/line palette — experiment2 uses its own colour picker.
        var palette = FindObjectOfType<PossibleUIPaletteController>();
        if (palette != null)
            palette.enabled = false;
    }

    void SetupArmText()
    {
        // ── Path A: Inspector-assigned reference ──────────────────────────────
        if (armTextReference != null)
        {
            _armText = armTextReference;
            Debug.Log($"[Experiment2] Using Inspector-assigned graphic '{_armText.name}' " +
                      $"(type={_armText.GetType().Name}).");
            return;
        }

        // ── Path B: find 'colorDependentText' by name ─────────────────────────
        // Search ALL Graphic components (covers legacy Text, TextMeshProUGUI,
        // Image, etc.) including on inactive objects.
        foreach (Graphic g in Resources.FindObjectsOfTypeAll<Graphic>())
        {
            if (!g.gameObject.scene.IsValid()) continue; // skip prefab assets
            if (g.gameObject.name != "colorDependentText") continue;

            _armText = g;
            Debug.Log($"[Experiment2] Linked to 'colorDependentText' " +
                      $"type={g.GetType().Name} " +
                      $"active={g.gameObject.activeInHierarchy}. " +
                      "Colour picker will drive its colour.");
            return;
        }

        Debug.LogWarning("[Experiment2] Could not find a Graphic named " +
                         "'colorDependentText'. Check the exact name in the Hierarchy.");
    }

    void SetupColorPickerPanel()
    {
        var panelGo = new GameObject("Exp2_ColorPickerHost");
        _pickerPanel = panelGo.AddComponent<Experiment2ColorPickerPanel>();
        _pickerPanel.handTracking = handTracking;
        _pickerPanel.labelFont    = pickerLabelFont != null ? pickerLabelFont : GetBuiltinFont();
        _pickerPanel.BuildPanel();
        _pickerPanel.OnColorChanged += OnColorPickerChanged;

        StartCoroutine(AnchorPanelAfterDelay());
    }

    IEnumerator AnchorPanelAfterDelay()
    {
        for (int i = 0; i < 5; i++)
            yield return null;

        Transform head = GetHeadTransform();
        if (head != null)
            _pickerPanel.AnchorToHead(head, pickerForwardMeters, pickerRightMeters);
        else
            Debug.LogWarning("[Experiment2] Head transform not found for panel anchor.");
    }

    // ── Colour picker callback ────────────────────────────────────────────────

    void OnColorPickerChanged(Color c)
    {
        if (_armText != null)
            _armText.color = c;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Re-anchors the colour picker panel relative to the current head pose.</summary>
    public void ReanchorPanel()
    {
        Transform head = GetHeadTransform();
        if (head != null && _pickerPanel != null)
            _pickerPanel.AnchorToHead(head, pickerForwardMeters, pickerRightMeters);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    Transform GetHeadTransform()
    {
        var rig = FindObjectOfType<OVRCameraRig>(true);
        if (rig != null && rig.centerEyeAnchor != null)
            return rig.centerEyeAnchor;
        if (Camera.main != null)
            return Camera.main.transform;
        return null;
    }

    static Font GetBuiltinFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }
}
