using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space experiment picker centered in view in experimentSelectScene.
/// Follows head rotation; poke a button with either index fingertip to load that scene.
/// </summary>
[DefaultExecutionOrder(50)]
public class ExperimentSelectMenuController : MonoBehaviour
{
    [System.Serializable]
    public struct SceneChoice
    {
        public string label;
        public string sceneName;
    }

    [Header("References")]
    public Transform headAnchor;
    public HandTrackingController handController;

    [Header("Menu")]
    public string titleText = "Select Experiment";

    public SceneChoice[] choices =
    {
        new SceneChoice { label = "Main Scene",    sceneName = ExperimentScenes.Main },
        new SceneChoice { label = "Experiment 1A", sceneName = ExperimentScenes.Experiment1A },
        new SceneChoice { label = "Experiment 2",  sceneName = ExperimentScenes.Experiment2 },
    };

    [Header("Placement")]
    [Tooltip("When enabled, the menu stays centered in view and follows head rotation.")]
    public bool followHead = true;
    [Min(0.2f)] public float distanceMeters = 0.55f;
    public float heightOffsetMeters = 0f;
    [Min(0.5f)] public float minHeadHeightToAnchor = 1.0f;
    [Min(1)] public int maxAnchorWaitFrames = 300;
    [Min(0.5f)] public float fallbackEyeHeightMeters = 1.45f;
    [Min(0.05f)] public float panelWorldWidthMeters = 0.34f;

    [Header("Touch")]
    [Min(0.001f)] public float hoverDistanceMeters = 0.035f;
    [Min(0.001f)] public float pressDistanceMeters = 0.018f;
    [Min(0f)] public float pickPaddingLocal = 8f;

    [Header("Layout")]
    public Vector2 buttonSize = new Vector2(280f, 72f);
    public float buttonSpacing = 16f;
    public float titleHeight = 56f;
    public int titleFontSize = 28;
    public int buttonFontSize = 24;
    public Color panelColor = new Color(0.08f, 0.08f, 0.1f, 0.88f);
    public Color buttonColor = new Color(0.18f, 0.42f, 0.82f, 1f);
    public Color buttonHoverColor = new Color(0.28f, 0.58f, 0.98f, 1f);
    public Color buttonPressColor = new Color(0.12f, 0.32f, 0.68f, 1f);

    RectTransform _menuRect;
    readonly List<MenuButton> _buttons = new List<MenuButton>();
    bool _headFollowActive;
    int _anchorWaitFrames;
    int _pressedButtonIndex = -1;

    struct MenuButton
    {
        public RectTransform rect;
        public Image image;
        public string sceneName;
    }

    void Awake()
    {
        BuildMenu();
        ApplyPanelScale();
    }

    void LateUpdate()
    {
        TryEnableHeadFollow();
        if (_headFollowActive && followHead)
            ApplyMenuPlacement();
        ApplyPanelScale();
        UpdateButtonInteraction();
    }

    void BuildMenu()
    {
        var canvasGo = new GameObject("ExperimentSelectMenu",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        _menuRect = canvasGo.GetComponent<RectTransform>();
        _menuRect.sizeDelta = new Vector2(320f, 320f);

        var backgroundGo = new GameObject("Background",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup),
            typeof(ContentSizeFitter));
        var background = backgroundGo.GetComponent<RectTransform>();
        background.SetParent(_menuRect, false);
        background.anchorMin = Vector2.zero;
        background.anchorMax = Vector2.one;
        background.offsetMin = Vector2.zero;
        background.offsetMax = Vector2.zero;

        var bgImage = backgroundGo.GetComponent<Image>();
        bgImage.color = panelColor;

        var layout = backgroundGo.GetComponent<VerticalLayoutGroup>();
        layout.spacing = buttonSpacing;
        layout.padding = new RectOffset(20, 20, 20, 20);
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = backgroundGo.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        CreateTitle(background, titleText);

        _buttons.Clear();
        if (choices != null)
        {
            for (int i = 0; i < choices.Length; i++)
                CreateChoiceButton(background, choices[i], i);
        }
    }

    void CreateTitle(RectTransform parent, string text)
    {
        var titleGo = new GameObject("Title",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
        titleGo.transform.SetParent(parent, false);

        var title = titleGo.GetComponent<Text>();
        title.text = text;
        title.alignment = TextAnchor.MiddleCenter;
        title.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        title.fontSize = titleFontSize;
        title.color = Color.white;
        title.raycastTarget = false;

        var layout = titleGo.GetComponent<LayoutElement>();
        layout.minHeight = titleHeight;
        layout.preferredHeight = titleHeight;
    }

    void CreateChoiceButton(RectTransform parent, SceneChoice choice, int index)
    {
        var buttonGo = new GameObject($"Choice_{index}",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        buttonGo.transform.SetParent(parent, false);

        var rect = buttonGo.GetComponent<RectTransform>();
        var image = buttonGo.GetComponent<Image>();
        image.color = buttonColor;

        var layout = buttonGo.GetComponent<LayoutElement>();
        layout.minHeight = buttonSize.y;
        layout.preferredHeight = buttonSize.y;

        var labelGo = new GameObject("Label",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelGo.transform.SetParent(buttonGo.transform, false);

        var labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var label = labelGo.GetComponent<Text>();
        label.text = string.IsNullOrWhiteSpace(choice.label) ? choice.sceneName : choice.label;
        label.alignment = TextAnchor.MiddleCenter;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = buttonFontSize;
        label.color = Color.white;
        label.raycastTarget = false;

        _buttons.Add(new MenuButton
        {
            rect = rect,
            image = image,
            sceneName = choice.sceneName
        });
    }

    void TryEnableHeadFollow()
    {
        if (_headFollowActive || _menuRect == null || headAnchor == null)
            return;

        Transform placementAnchor = GetHeadPlacementAnchor();
        if (placementAnchor == null)
            return;

        bool headHeightValid = placementAnchor.position.y >= minHeadHeightToAnchor;
        if (!headHeightValid)
        {
            _anchorWaitFrames++;
            if (_anchorWaitFrames < maxAnchorWaitFrames)
                return;
        }

        _headFollowActive = true;
        ApplyMenuPlacement(useFallbackHeight: !headHeightValid);
    }

    Transform GetHeadPlacementAnchor()
    {
        if (headAnchor == null)
            return null;

        Camera headCamera = headAnchor.GetComponent<Camera>();
        if (headCamera == null)
            headCamera = headAnchor.GetComponentInChildren<Camera>();

        return headCamera != null ? headCamera.transform : headAnchor;
    }

    void ApplyMenuPlacement(bool useFallbackHeight = false)
    {
        if (headAnchor == null || _menuRect == null)
            return;

        Transform placementAnchor = GetHeadPlacementAnchor();
        if (placementAnchor == null)
            return;

        Vector3 viewForward = placementAnchor.forward;
        if (viewForward.sqrMagnitude < 1e-6f)
            viewForward = Vector3.forward;
        viewForward.Normalize();

        Vector3 anchorPos = placementAnchor.position;
        Vector3 targetPos = anchorPos + viewForward * distanceMeters;
        if (useFallbackHeight)
            targetPos.y = fallbackEyeHeightMeters + heightOffsetMeters;
        else
            targetPos += placementAnchor.up * heightOffsetMeters;

        _menuRect.position = targetPos;

        Vector3 faceDir = anchorPos - targetPos;
        if (faceDir.sqrMagnitude < 1e-6f)
            faceDir = -viewForward;

        _menuRect.rotation = Quaternion.LookRotation(faceDir.normalized, placementAnchor.up);
    }

    void ApplyPanelScale()
    {
        if (_menuRect == null || panelWorldWidthMeters <= 0f)
            return;

        LayoutRebuilder.ForceRebuildLayoutImmediate(_menuRect);

        float rectWidth = _menuRect.rect.width;
        if (rectWidth < 1f)
            return;

        float scale = panelWorldWidthMeters / rectWidth;
        _menuRect.localScale = new Vector3(-scale, scale, scale);
    }

    void UpdateButtonInteraction()
    {
        if (_menuRect == null || _buttons.Count == 0)
            return;

        int hoveredIndex = -1;
        int pressedIndex = -1;

        if (handController != null && handController.rightIndexTip != null) {
            TryUpdateButtonInteractionForFinger(handController.rightIndexTip, ref hoveredIndex, ref pressedIndex);
        }

        for (int i = 0; i < _buttons.Count; i++)
        {
            Color color = buttonColor;
            if (i == pressedIndex)
                color = buttonPressColor;
            else if (i == hoveredIndex)
                color = buttonHoverColor;

            _buttons[i].image.color = color;
        }

        if (pressedIndex >= 0 && _pressedButtonIndex < 0)
            _pressedButtonIndex = pressedIndex;

        if (_pressedButtonIndex >= 0 && pressedIndex < 0)
        {
            string sceneName = _buttons[_pressedButtonIndex].sceneName;
            _pressedButtonIndex = -1;
            ExperimentSceneLoader.LoadScene(sceneName);
        }
    }

    bool TryUpdateButtonInteractionForFinger(Transform finger, ref int hoveredIndex, ref int pressedIndex)
    {
        if (finger == null)
            return false;

        bool anyHit = false;
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (!TryGetButtonTouchState(_buttons[i].rect, finger.position, out bool hover, out bool press))
                continue;

            anyHit = true;
            if (hover)
                hoveredIndex = i;
            if (press)
                pressedIndex = i;
        }

        return anyHit;
    }

    bool TryGetButtonTouchState(RectTransform button, Vector3 fingerWorld, out bool hover, out bool press)
    {
        hover = false;
        press = false;

        float signedDistance = SignedDistanceToMenuPlane(fingerWorld);
        if (signedDistance > hoverDistanceMeters)
            return false;

        Vector2 fingerLocal = FingerLocalOnMenu(fingerWorld);
        GetBoundsInMenuLocal(button, out float mnX, out float mxX, out float mnY, out float mxY);

        float pad = pickPaddingLocal;
        hover =
            fingerLocal.x >= mnX - pad && fingerLocal.x <= mxX + pad &&
            fingerLocal.y >= mnY - pad && fingerLocal.y <= mxY + pad;

        press = hover && signedDistance <= pressDistanceMeters;
        return hover;
    }

    float SignedDistanceToMenuPlane(Vector3 worldPoint)
    {
        Vector3 normal = _menuRect.forward;
        return Vector3.Dot(worldPoint - _menuRect.position, normal);
    }

    Vector2 FingerLocalOnMenu(Vector3 fingerWorld)
    {
        Vector3 local = _menuRect.InverseTransformPoint(fingerWorld);
        return new Vector2(local.x, local.y);
    }

    void GetBoundsInMenuLocal(RectTransform child, out float mnX, out float mxX, out float mnY, out float mxY)
    {
        Vector3[] corners = new Vector3[4];
        child.GetWorldCorners(corners);

        mnX = mnY = float.MaxValue;
        mxX = mxY = float.MinValue;

        for (int i = 0; i < 4; i++)
        {
            Vector3 local = _menuRect.InverseTransformPoint(corners[i]);
            if (local.x < mnX) mnX = local.x;
            if (local.x > mxX) mxX = local.x;
            if (local.y < mnY) mnY = local.y;
            if (local.y > mxY) mxY = local.y;
        }
    }
}
