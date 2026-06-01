using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Main Scene only — adds a "Scale" toggle button to the floating palette.
///
/// Workflow:
///   1. Press the Scale button  → scale mode activates (button turns cyan).
///   2. Touch an item on the arm → selects it; a scale handle appears:
///        a white line at 45° from the item centre + a cyan grab dot at the tip.
///   3. Touch and hold the cyan dot → drag outward to enlarge, inward to shrink.
///      Scale is computed relative to where you grabbed the dot, so you always
///      start from the item's current size.
///   4. Release → scale is committed.
///   5. Press the Scale button again to deactivate and restore normal pick/place.
///
/// While scale mode is active, ForearmTouchManager is disabled so normal
/// pick-up / place gestures do not fire.
/// </summary>
[DefaultExecutionOrder(91)]   // must run BEFORE ForearmTouchManager (100) to suppress pickup
public class ArmScaleController : MonoBehaviour
{
    // ── Constants ──────────────────────────────────────────────────────────────

    const float HandleLineLength    = 55f;  // canvas units, item centre → dot
    const float HandleLineThickness =  4f;  // canvas units
    const float HandleDotDiameter   = 24f;  // canvas units
    const float HandlePickRadius    = 28f;  // canvas units: grab radius for the dot
    const float ItemPickPadding     = 18f;  // canvas units: added to item bounds

    /// <summary>
    /// Angle (canvas-space degrees, 0° = right) at which the handle line points.
    /// 45° = upper-right diagonal; visually distinct from the Rotate handle (90° = up).
    /// </summary>
    const float HandleAngleDeg = 45f;

    static readonly float HandleAngleRad = HandleAngleDeg * Mathf.Deg2Rad;

    // ── References ─────────────────────────────────────────────────────────────

    ArmLayoutController          armLayout;
    ForearmTouchManager          forearmTouch;
    PossibleUIPaletteController  palette;
    TouchInputManager            touchInput;
    HandTrackingController       handTracking;

    // ── Palette button ─────────────────────────────────────────────────────────

    RectTransform _scaleBtn;
    bool          _wasPressOnScale;
    bool          _scaleModeActive;

    static readonly Color BtnDefault = new Color(0.22f, 0.45f, 0.82f, 1f);
    static readonly Color BtnActive  = new Color(0.10f, 0.80f, 0.90f, 1f); // cyan

    // ── Scaling state ──────────────────────────────────────────────────────────

    RectTransform _selectedItem;
    bool          _isDraggingDot;
    bool          _wasArmPressHeld;

    /// <summary>Distance from item centre to grab point when drag started.</summary>
    float         _dragAnchorDist;
    /// <summary>Item localScale when drag started.</summary>
    Vector3       _dragBaseScale;

    // ── Handle visuals ─────────────────────────────────────────────────────────

    GameObject    _handleRoot;  // pivot placed at item centre on arm canvas
    RectTransform _handleLine;  // thin white bar
    RectTransform _handleDot;   // cyan grab circle

    // ── Bootstrap ──────────────────────────────────────────────────────────────

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
        if (!scene.IsValid() || scene.name != ExperimentScenes.Main) return;
        if (FindObjectOfType<ArmScaleController>() != null) return;

        var go = new GameObject("ArmScaleController");
        go.AddComponent<ArmScaleController>();
        Debug.Log("[MainScene] ArmScaleController bootstrapped.");
    }

    // ── MonoBehaviour ──────────────────────────────────────────────────────────

    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Main)
        { enabled = false; return; }
    }

    void Start()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Main) return;

        ResolveReferences();
        AddScaleButtonToPalette();
    }

    void LateUpdate()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Main) return;

        if (armLayout == null || touchInput == null) ResolveReferences();

        // Guard against the selected item being destroyed externally.
        if (_selectedItem == null && _isDraggingDot)
        {
            _isDraggingDot = false;
            HideHandle();
        }

        CheckScaleButtonPress();

        if (_scaleModeActive)
        {
            if (forearmTouch != null && forearmTouch.enabled)
                forearmTouch.enabled = false;

            ProcessScaleInput();
        }
        else
        {
            if (forearmTouch != null && !forearmTouch.enabled)
                forearmTouch.enabled = true;
        }
    }

    // ── Reference resolution ───────────────────────────────────────────────────

    void ResolveReferences()
    {
        if (armLayout    == null) armLayout    = FindObjectOfType<ArmLayoutController>();
        if (forearmTouch == null) forearmTouch = FindObjectOfType<ForearmTouchManager>();
        if (palette      == null) palette      = FindObjectOfType<PossibleUIPaletteController>();
        if (touchInput   == null) touchInput   = FindObjectOfType<TouchInputManager>();
        if (handTracking == null) handTracking = FindObjectOfType<HandTrackingController>();
    }

    // ── Palette button setup ───────────────────────────────────────────────────

    void AddScaleButtonToPalette()
    {
        if (palette == null || palette.paletteRect == null) return;

        var go = new GameObject("ScaleBtn",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(palette.paletteRect, false);

        _scaleBtn = (RectTransform)go.transform;
        _scaleBtn.localScale = Vector3.one;

        var le = go.GetComponent<LayoutElement>();
        le.minWidth      = 80f;  le.preferredWidth  = 80f;  le.flexibleWidth  = 0f;
        le.minHeight     = 50f;  le.preferredHeight = 50f;  le.flexibleHeight = 0f;

        go.GetComponent<Image>().color = BtnDefault;
        go.GetComponent<Image>().raycastTarget = false;

        var lblGo = new GameObject("Lbl",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        lblGo.transform.SetParent(go.transform, false);

        var lr = (RectTransform)lblGo.transform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = lr.offsetMax = Vector2.zero;

        var txt = lblGo.GetComponent<Text>();
        txt.text      = "Scale";
        txt.alignment = TextAnchor.MiddleCenter;
        txt.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.fontSize  = 16;
        txt.color     = Color.white;
        txt.raycastTarget = false;
    }

    // ── Palette button interaction ─────────────────────────────────────────────

    void CheckScaleButtonPress()
    {
        if (_scaleBtn == null || palette == null) return;
        if (handTracking == null || !handTracking.isRightHandTracked
                                 || handTracking.rightIndexTip == null) return;

        Vector3 finger   = handTracking.rightIndexTip.position;
        bool    pressing = IsFingerPressingButton(_scaleBtn, finger);

        if (pressing && !_wasPressOnScale)
            ToggleScaleMode();

        _wasPressOnScale = pressing;
    }

    void ToggleScaleMode()
    {
        _scaleModeActive = !_scaleModeActive;
        SetBtnColor(_scaleBtn, _scaleModeActive ? BtnActive : BtnDefault);

        if (_scaleModeActive)
        {
            if (armLayout != null && armLayout.IsCarrying)
                armLayout.AbortCarryWithoutPlace();
        }
        else
        {
            DeselectItem();
            if (forearmTouch != null) forearmTouch.enabled = true;
        }

        Debug.Log($"[MainScene] Scale mode → {_scaleModeActive}");
    }

    // ── Core scale input ───────────────────────────────────────────────────────

    void ProcessScaleInput()
    {
        if (touchInput == null || armLayout == null) return;

        bool pressHeld  = touchInput.touchState == TouchInputManager.TouchState.Press;
        bool pressBegan = pressHeld && !_wasArmPressHeld;
        bool pressEnded = !pressHeld && _wasArmPressHeld;
        _wasArmPressHeld = pressHeld;

        if (pressBegan)
        {
            Vector2 fp = armLayout.TapWorldToCanvasLocal(touchInput.contactPoint);

            if (_selectedItem != null && IsNearHandleDot(fp))
            {
                // Begin drag — record the reference distance and current scale.
                Vector2 itemCentre = GetItemCanvasCentre();
                float   grabDist   = (fp - itemCentre).magnitude;

                // If the user pressed almost exactly on the centre, use the handle
                // length as reference so we don't get a divide-by-zero.
                _dragAnchorDist = Mathf.Max(grabDist, 1f);
                _dragBaseScale  = _selectedItem.localScale;
                _isDraggingDot  = true;
            }
            else
            {
                DeselectItem();
                _selectedItem  = TryPickCanvasItem(fp);
                _isDraggingDot = false;
                RefreshHandle();
            }
        }

        if (pressHeld && _isDraggingDot && _selectedItem != null)
        {
            Vector2 fp         = armLayout.TapWorldToCanvasLocal(touchInput.contactPoint);
            Vector2 itemCentre = GetItemCanvasCentre();
            float   currentDist = (fp - itemCentre).magnitude;

            // Uniform scale: new = base * (current / anchor), clamped to prevent collapse.
            float scaleFactor = Mathf.Max(0.05f, currentDist / _dragAnchorDist);
            _selectedItem.localScale = _dragBaseScale * scaleFactor;
        }

        if (pressEnded)
            _isDraggingDot = false;
    }

    // ── Item selection ─────────────────────────────────────────────────────────

    RectTransform TryPickCanvasItem(Vector2 fingerLocal)
    {
        if (armLayout?.canvasRect == null) return null;

        RectTransform canvas  = armLayout.canvasRect;
        RectTransform best    = null;
        float         bestDist = float.MaxValue;

        for (int i = 0; i < canvas.childCount; i++)
        {
            var child = canvas.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeInHierarchy) continue;
            if (IsHandleObject(child.gameObject)) continue;
            if (child.name.StartsWith("__")) continue;

            child.GetWorldCorners(_cornerBuf);
            float mnX = float.MaxValue, mxX = float.MinValue;
            float mnY = float.MaxValue, mxY = float.MinValue;
            for (int j = 0; j < 4; j++)
            {
                Vector2 cl = canvas.InverseTransformPoint(_cornerBuf[j]);
                if (cl.x < mnX) mnX = cl.x; if (cl.x > mxX) mxX = cl.x;
                if (cl.y < mnY) mnY = cl.y; if (cl.y > mxY) mxY = cl.y;
            }

            bool inBounds =
                fingerLocal.x >= mnX - ItemPickPadding && fingerLocal.x <= mxX + ItemPickPadding &&
                fingerLocal.y >= mnY - ItemPickPadding && fingerLocal.y <= mxY + ItemPickPadding;

            if (!inBounds) continue;

            Vector2 centre = new Vector2((mnX + mxX) * 0.5f, (mnY + mxY) * 0.5f);
            float   dist   = (fingerLocal - centre).magnitude;
            if (dist < bestDist) { bestDist = dist; best = child; }
        }

        return best;
    }

    readonly Vector3[] _cornerBuf = new Vector3[4];

    void DeselectItem()
    {
        _selectedItem  = null;
        _isDraggingDot = false;
        HideHandle();
    }

    // ── Handle visuals ─────────────────────────────────────────────────────────

    void RefreshHandle()
    {
        if (_selectedItem == null) { HideHandle(); return; }

        EnsureHandleExists();
        if (_handleRoot == null) return;

        Vector2 centre  = GetItemCanvasCentre();
        var     dir     = new Vector2(Mathf.Cos(HandleAngleRad), Mathf.Sin(HandleAngleRad));
        var     dotOff  = dir * HandleLineLength;

        var rootRt = _handleRoot.GetComponent<RectTransform>();
        rootRt.anchoredPosition = centre;

        // Line centre = midpoint from root origin (0,0) to dot; rotated to 45°.
        _handleLine.anchoredPosition = dotOff * 0.5f;
        _handleLine.sizeDelta        = new Vector2(HandleLineLength, HandleLineThickness);
        _handleLine.localRotation    = Quaternion.Euler(0f, 0f, HandleAngleDeg);

        _handleDot.anchoredPosition = dotOff;

        _handleRoot.SetActive(true);
    }

    void HideHandle()
    {
        if (_handleRoot != null) _handleRoot.SetActive(false);
    }

    void EnsureHandleExists()
    {
        if (_handleRoot != null) return;
        if (armLayout?.canvasRect == null) return;

        int layer = armLayout.canvasRect.gameObject.layer;

        _handleRoot = new GameObject("__ScaleHandle", typeof(RectTransform));
        _handleRoot.layer = layer;
        _handleRoot.transform.SetParent(armLayout.canvasRect, false);
        var rootRt = _handleRoot.GetComponent<RectTransform>();
        rootRt.anchorMin  = rootRt.anchorMax = new Vector2(0.5f, 0.5f);
        rootRt.pivot      = new Vector2(0.5f, 0.5f);
        rootRt.sizeDelta  = Vector2.zero;
        rootRt.localScale = Vector3.one;

        // Line bar.
        var lineGo = new GameObject("Line",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        lineGo.layer = layer;
        lineGo.transform.SetParent(_handleRoot.transform, false);
        _handleLine            = lineGo.GetComponent<RectTransform>();
        _handleLine.anchorMin  = _handleLine.anchorMax = new Vector2(0.5f, 0.5f);
        _handleLine.pivot      = new Vector2(0.5f, 0.5f);
        _handleLine.localScale = Vector3.one;
        var lineImg            = lineGo.GetComponent<Image>();
        lineImg.color          = new Color(1f, 1f, 1f, 0.90f);
        lineImg.raycastTarget  = false;

        // Grab dot.
        var dotGo = new GameObject("Dot",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        dotGo.layer = layer;
        dotGo.transform.SetParent(_handleRoot.transform, false);
        _handleDot            = dotGo.GetComponent<RectTransform>();
        _handleDot.anchorMin  = _handleDot.anchorMax = new Vector2(0.5f, 0.5f);
        _handleDot.pivot      = new Vector2(0.5f, 0.5f);
        _handleDot.sizeDelta  = Vector2.one * HandleDotDiameter;
        _handleDot.localScale = Vector3.one;
        var dotImg            = dotGo.GetComponent<Image>();
        dotImg.sprite         = GetCircleSprite();
        dotImg.type           = Image.Type.Simple;
        dotImg.color          = new Color(0.10f, 0.90f, 1.00f, 1f); // cyan
        dotImg.raycastTarget  = false;
    }

    bool IsHandleObject(GameObject go)
    {
        if (_handleRoot == null) return false;
        return go == _handleRoot || go.transform.IsChildOf(_handleRoot.transform);
    }

    // ── Utilities ──────────────────────────────────────────────────────────────

    bool IsNearHandleDot(Vector2 fingerLocal)
    {
        if (_handleDot == null || _handleRoot == null || !_handleRoot.activeSelf)
            return false;

        Vector2 dotPos = armLayout.canvasRect.InverseTransformPoint(_handleDot.position);
        return (fingerLocal - dotPos).magnitude < HandlePickRadius;
    }

    Vector2 GetItemCanvasCentre()
    {
        if (_selectedItem == null || armLayout?.canvasRect == null) return Vector2.zero;

        Vector3 worldCentre = _selectedItem.TransformPoint(new Vector3(
            _selectedItem.rect.x + _selectedItem.rect.width  * 0.5f,
            _selectedItem.rect.y + _selectedItem.rect.height * 0.5f,
            0f));

        return armLayout.canvasRect.InverseTransformPoint(worldCentre);
    }

    bool IsFingerPressingButton(RectTransform btn, Vector3 fingerWorld)
    {
        if (btn == null || palette?.paletteRect == null) return false;

        Vector3 towardUser = GetPaletteTowardUser();
        float   depth      = Vector3.Dot(fingerWorld - palette.paletteRect.position, towardUser);
        if (depth < 0f || depth > palette.pressDistanceMeters) return false;

        Vector2 local = palette.paletteRect.InverseTransformPoint(fingerWorld);
        btn.GetWorldCorners(_cornerBuf);
        float mnX = float.MaxValue, mxX = float.MinValue;
        float mnY = float.MaxValue, mxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Vector2 cl = palette.paletteRect.InverseTransformPoint(_cornerBuf[i]);
            if (cl.x < mnX) mnX = cl.x; if (cl.x > mxX) mxX = cl.x;
            if (cl.y < mnY) mnY = cl.y; if (cl.y > mxY) mxY = cl.y;
        }
        return local.x >= mnX && local.x <= mxX && local.y >= mnY && local.y <= mxY;
    }

    Vector3 GetPaletteTowardUser()
    {
        if (palette?.headAnchor != null)
        {
            var dir = palette.headAnchor.position - palette.paletteRect.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f) return dir.normalized;
        }
        return -palette.paletteRect.forward;
    }

    static void SetBtnColor(RectTransform btn, Color c)
    {
        var img = btn?.GetComponent<Image>();
        if (img != null) img.color = c;
    }

    // ── Circle sprite ──────────────────────────────────────────────────────────

    static Sprite _circleSprite;

    static Sprite GetCircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode   = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        float cx = (size - 1) * 0.5f;
        float r  = cx - 0.5f;

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dist  = Mathf.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
                float alpha = Mathf.Clamp01(r - dist + 1f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        tex.Apply();

        _circleSprite = Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            size);

        return _circleSprite;
    }
}
