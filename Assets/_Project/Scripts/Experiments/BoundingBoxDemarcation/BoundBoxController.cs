using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// experiment1A_BoundBox — three direct-touch interaction modes.
///
///   Draw   — touch the arm to place a dot; an elastic preview extends from it;
///             the next touch places another dot and makes the line permanent;
///             when a loop closes the region auto-fills and the chain resets.
///   Resize — touch an existing dot and drag it to a new position;
///             all connected lines update live; fills rebuild on release.
///   Remove — touch a dot or line to delete it; fills rebuild immediately.
///
/// A floating palette with three buttons selects the active mode.
/// ForearmTouchManager is disabled — this controller owns all arm input.
/// </summary>
[DefaultExecutionOrder(95)]
public class Experiment1ABoundBoxController : MonoBehaviour
{
    // ── Tool mode ──────────────────────────────────────────────────────────────

    enum ToolMode { Draw, Resize, Remove }

    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("References (auto-found when empty)")]
    public ArmLayoutController    armLayout;
    public ForearmTouchManager    forearmTouch;
    public PossibleUIPaletteController palette;
    public TouchInputManager      touchInput;
    public HandTrackingController handTracking;

    [Header("Dot visuals")]
    public Vector2 placedDotCanvasSize = new Vector2(15f, 15f);
    public Color   dotColor            = Color.white;

    [Header("Line visuals")]
    public Color lineColor    = Color.white;
    [Min(1f)] public float lineThickness = 3f;

    [Header("Fill")]
    public Color fillColor = new Color(0.2f, 0.9f, 0.35f, 0.5f);

    [Header("Interaction thresholds (canvas units)")]
    [Tooltip("Radius around a dot that counts as 'touching' it (pick / snap).")]
    [Min(0f)] public float dotPickPaddingLocal = 20f;
    [Tooltip("Distance from a line segment that counts as 'touching' it for removal.")]
    [Min(0f)] public float linePickRadiusLocal = 18f;

    [Header("Palette")]
    [Tooltip("Vertical offset from eye height for the palette panel (meters).")]
    public float paletteHeightOffsetMeters = -0.12f;

    // ── Tool state ─────────────────────────────────────────────────────────────

    ToolMode _toolMode = ToolMode.Draw;

    // ── Palette ────────────────────────────────────────────────────────────────

    RectTransform _paletteRect;
    RectTransform _drawBtn, _resizeBtn, _removeBtn;
    bool _wasPressOnDraw, _wasPressOnResize, _wasPressOnRemove;

    static readonly Color BtnDefault = new Color(0.22f, 0.45f, 0.82f, 1f);
    static readonly Color BtnActive  = new Color(0.35f, 0.65f, 0.98f, 1f);

    // ── Draw mode ──────────────────────────────────────────────────────────────

    Experiment1ADotMarker    _drawChainStart;   // first dot of current open chain
    Experiment1ADotMarker    _drawChainLast;    // most-recently placed dot

    // Elastic preview: an invisible ghost dot tracks the finger; a semi-transparent
    // line segment connects _drawChainLast → ghost so the user can see where the
    // next line will go.
    GameObject               _ghostDotGo;
    Experiment1ADotMarker    _ghostMarker;
    Experiment1ALineSegment  _elasticSegment;

    // ── Resize mode ────────────────────────────────────────────────────────────

    Experiment1ADotMarker _resizeDragDot;

    // ── Shared input edge tracking ─────────────────────────────────────────────

    bool _wasArmPressHeld;

    // ── Graph data ─────────────────────────────────────────────────────────────

    readonly List<Experiment1ADotMarker>          _dots      = new List<Experiment1ADotMarker>();
    readonly List<Experiment1ALineSegment>        _lines     = new List<Experiment1ALineSegment>();
    readonly Dictionary<int, List<int>>           _adjacency = new Dictionary<int, List<int>>();
    readonly HashSet<string>                      _filledCycles = new HashSet<string>();

    int       _nextDotId;
    Transform _fillsRoot;
    Transform _linesRoot;

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
        if (!scene.IsValid() || scene.name != ExperimentScenes.Experiment1A) return;
        if (FindObjectOfType<Experiment1ABoundBoxController>() != null) return;

        var root = new GameObject("Experiment1A");
        root.AddComponent<Experiment1ASceneBootstrap>();
        root.AddComponent<Experiment1ABoundBoxController>();
        Debug.Log("[Experiment1A] Runtime bootstrap created controller.");
    }

    // ── MonoBehaviour ──────────────────────────────────────────────────────────

    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment1A)
        { enabled = false; return; }

        ResolveReferences();
    }

    void Start()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment1A) return;

        ConfigureArmLayout();
        ConfigurePaletteForExperiment();
        ClearPreplacedArmWidgets();
        EnsureArmLayers();

        if (forearmTouch != null) forearmTouch.enabled = false;
        if (palette      != null) palette.RequestWorldReanchor();

        Debug.Log("[Experiment1A] Draw / Resize / Remove mode ready.");
    }

    void LateUpdate()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment1A) return;

        // Always keep ForearmTouchManager disabled — we own all arm input.
        if (forearmTouch != null && forearmTouch.enabled)
            forearmTouch.enabled = false;

        CleanupDeletedItems();
        TrySelectTool();

        switch (_toolMode)
        {
            case ToolMode.Draw:   ProcessDrawMode();   break;
            case ToolMode.Resize: ProcessResizeMode(); break;
            case ToolMode.Remove: ProcessRemoveMode(); break;
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

    // ── Scene setup ────────────────────────────────────────────────────────────

    void ConfigureArmLayout()
    {
        if (armLayout == null) return;
        armLayout.interactionMode    = ArmLayoutController.InteractionMode.FreePlace;
        armLayout.useGridSnap        = false;
        armLayout.drawGridOnCanvas   = false;
        armLayout.resizeToGridCell   = false;
        armLayout.preventCellOverlap = false;
    }

    void ConfigurePaletteForExperiment()
    {
        if (palette == null || palette.paletteRect == null) return;

        _paletteRect = palette.paletteRect;

        palette.followHead            = false;
        palette.distanceMeters        = 0.5f;
        palette.heightOffsetMeters    = -0.10f;  // ≈4 in below eye level
        palette.minHeadHeightToAnchor = 1.0f;
        palette.maxHeadHeightToAnchor = 2.2f;
        palette.maxAnchorWaitFrames   = 300;
        palette.fallbackEyeHeightMeters = 1.45f;
        palette.lateralOffsetMeters   = 0.25f;   // 0.35 - 0.10 (≈4 in left)
        palette.panelFaceYawDegrees   = -12f;
        palette.blockWorldAnchor      = false;
        palette.hoverDistanceMeters   = 0.05f;
        palette.pressDistanceMeters   = 0.038f;

        // Hide the built-in delete zone — Remove mode handles all deletion.
        Transform dz = _paletteRect.Find("DeleteZone");
        if (dz != null) dz.gameObject.SetActive(false);

        // The three tool-mode buttons are added directly to _paletteRect so
        // PossibleUIPaletteController's TemplateContainer doesn't try to clone them.
        _drawBtn   = CreateToolButton("Draw");
        _resizeBtn = CreateToolButton("Resize");
        _removeBtn = CreateToolButton("Remove");

        ApplyToolHighlight(_toolMode);
    }

    RectTransform CreateToolButton(string label)
    {
        var go = new GameObject(label + "Btn",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(_paletteRect, false);

        var rect = (RectTransform)go.transform;
        rect.localScale = Vector3.one;

        var le = go.GetComponent<LayoutElement>();
        le.minWidth      = 80f;  le.preferredWidth  = 80f;  le.flexibleWidth  = 0f;
        le.minHeight     = 50f;  le.preferredHeight = 50f;  le.flexibleHeight = 0f;

        go.GetComponent<Image>().color = BtnDefault;
        go.GetComponent<Image>().raycastTarget = false;

        var labelGo = new GameObject("Lbl",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelGo.transform.SetParent(go.transform, false);

        var lr = (RectTransform)labelGo.transform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = lr.offsetMax = Vector2.zero;

        var t = labelGo.GetComponent<Text>();
        t.text = label;
        t.alignment = TextAnchor.MiddleCenter;
        t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize  = 18;
        t.color     = Color.white;
        t.raycastTarget = false;

        return rect;
    }

    void ApplyToolHighlight(ToolMode mode)
    {
        SetBtnColor(_drawBtn,   mode == ToolMode.Draw);
        SetBtnColor(_resizeBtn, mode == ToolMode.Resize);
        SetBtnColor(_removeBtn, mode == ToolMode.Remove);
    }

    static void SetBtnColor(RectTransform btn, bool active)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = active ? BtnActive : BtnDefault;
    }

    void ClearPreplacedArmWidgets()
    {
        if (armLayout == null) return;
        var root = armLayout.transform;
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            var child = root.GetChild(i);
            if (child.name.StartsWith("__")) continue;
            Destroy(child.gameObject);
        }
    }

    void EnsureArmLayers()
    {
        if (armLayout == null) return;
        _fillsRoot = EnsureChildLayer(armLayout.transform, "Experiment1A_Fills");
        _linesRoot  = EnsureChildLayer(armLayout.transform, "Experiment1A_Lines");
        _fillsRoot.SetAsFirstSibling();
        _linesRoot.SetSiblingIndex(1);
    }

    static Transform EnsureChildLayer(Transform parent, string layerName)
    {
        var existing = parent.Find(layerName);
        if (existing != null) return existing;

        var go   = new GameObject(layerName, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        return rect;
    }

    // ── Tool selection (palette buttons) ──────────────────────────────────────

    void TrySelectTool()
    {
        if (_paletteRect == null || palette == null) return;
        if (handTracking == null || !handTracking.isRightHandTracked
                                 || handTracking.rightIndexTip == null) return;

        palette.UpdateTouchFromFinger(handTracking.rightIndexTip.position);
        Vector3 finger = handTracking.rightIndexTip.position;

        bool onDraw   = IsFingerPressingButton(_drawBtn,   finger);
        bool onResize = IsFingerPressingButton(_resizeBtn, finger);
        bool onRemove = IsFingerPressingButton(_removeBtn, finger);

        if (onDraw   && !_wasPressOnDraw)   SetToolMode(ToolMode.Draw);
        if (onResize && !_wasPressOnResize) SetToolMode(ToolMode.Resize);
        if (onRemove && !_wasPressOnRemove) SetToolMode(ToolMode.Remove);

        _wasPressOnDraw   = onDraw;
        _wasPressOnResize = onResize;
        _wasPressOnRemove = onRemove;
    }

    void SetToolMode(ToolMode mode)
    {
        _toolMode = mode;
        ApplyToolHighlight(mode);
        if (mode != ToolMode.Resize) _resizeDragDot = null;
        Debug.Log($"[Experiment1A] Tool → {mode}");
    }

    bool IsFingerPressingButton(RectTransform btn, Vector3 fingerWorld)
    {
        if (btn == null || _paletteRect == null) return false;
        if (PalettePlaneDistance(fingerWorld) > palette.pressDistanceMeters) return false;

        Vector2 local = _paletteRect.InverseTransformPoint(fingerWorld);

        Vector3[] corners = new Vector3[4];
        btn.GetWorldCorners(corners);
        float mnX = float.MaxValue, mxX = float.MinValue;
        float mnY = float.MaxValue, mxY = float.MinValue;
        foreach (var c in corners)
        {
            Vector2 cl = _paletteRect.InverseTransformPoint(c);
            if (cl.x < mnX) mnX = cl.x; if (cl.x > mxX) mxX = cl.x;
            if (cl.y < mnY) mnY = cl.y; if (cl.y > mxY) mxY = cl.y;
        }
        return local.x >= mnX && local.x <= mxX && local.y >= mnY && local.y <= mxY;
    }

    float PalettePlaneDistance(Vector3 fingerWorld)
    {
        Vector3 towardUser;
        if (palette != null && palette.headAnchor != null)
        {
            towardUser = palette.headAnchor.position - _paletteRect.position;
            towardUser.y = 0f;
            if (towardUser.sqrMagnitude < 1e-6f) towardUser = -_paletteRect.forward;
            else towardUser.Normalize();
        }
        else
        {
            towardUser = -_paletteRect.forward;
        }
        float signed = Vector3.Dot(fingerWorld - _paletteRect.position, towardUser);
        return signed < 0f ? float.MaxValue : signed;
    }

    // ── Draw mode ──────────────────────────────────────────────────────────────

    void ProcessDrawMode()
    {
        if (touchInput == null) return;

        bool pressHeld  = touchInput.touchState == TouchInputManager.TouchState.Press;
        bool pressBegan = pressHeld && !_wasArmPressHeld;
        _wasArmPressHeld = pressHeld;

        UpdateElasticLine();

        if (!pressBegan) return;

        // Find an existing dot to snap to, or place a brand-new one.
        bool snapped = TryFindSnapDot(touchInput.contactPoint, out Experiment1ADotMarker target);
        if (!snapped)
            target = PlaceDotAtContact(touchInput.contactPoint);

        // Connect to the chain if we have a previous dot.
        if (_drawChainLast != null && target != _drawChainLast)
        {
            int prevFills = _filledCycles.Count;
            TryAddLine(_drawChainLast, target);

            if (_filledCycles.Count > prevFills)
            {
                // A region just closed — auto-reset for the next drawing chain.
                _drawChainStart = null;
                _drawChainLast  = null;
                HideElastic();
                return;
            }
        }

        if (_drawChainStart == null) _drawChainStart = target;
        _drawChainLast = target;
        RefreshElasticFrom(target);
    }

    void UpdateElasticLine()
    {
        bool active = _drawChainLast != null
                   && touchInput != null
                   && touchInput.touchState != TouchInputManager.TouchState.None;

        if (_elasticSegment != null) _elasticSegment.gameObject.SetActive(active);
        if (_ghostDotGo     != null) _ghostDotGo.SetActive(active);

        if (!active || touchInput == null) return;

        Vector2 fp = armLayout.TapWorldToCanvasLocal(touchInput.contactPoint);
        if (_ghostMarker?.RectTransform != null)
            _ghostMarker.RectTransform.anchoredPosition = fp;
    }

    void RefreshElasticFrom(Experiment1ADotMarker fromDot)
    {
        if (fromDot == null) return;

        int layer = ArmCanvasLayer();

        if (_ghostDotGo == null)
        {
            _ghostDotGo = new GameObject("ElasticGhost", typeof(RectTransform));
            _ghostDotGo.layer = layer;
            _ghostDotGo.transform.SetParent(armLayout.canvasRect, false);
            _ghostMarker = _ghostDotGo.AddComponent<Experiment1ADotMarker>();
        }

        if (_elasticSegment == null)
        {
            var go = new GameObject("ElasticLine",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(Experiment1ALineSegment));
            go.layer = layer;
            go.transform.SetParent(
                _linesRoot != null ? _linesRoot : (Transform)armLayout.canvasRect, false);
            _elasticSegment = go.GetComponent<Experiment1ALineSegment>();
        }

        _elasticSegment.Initialize(
            fromDot, _ghostMarker,
            new Color(lineColor.r, lineColor.g, lineColor.b, 0.5f),
            lineThickness,
            armLayout.canvasRect);

        _elasticSegment.gameObject.SetActive(false); // shown by UpdateElasticLine
    }

    void HideElastic()
    {
        if (_elasticSegment != null) _elasticSegment.gameObject.SetActive(false);
        if (_ghostDotGo     != null) _ghostDotGo.SetActive(false);
    }

    Experiment1ADotMarker PlaceDotAtContact(Vector3 contactWorld)
    {
        Vector2 local = armLayout.TapWorldToCanvasLocal(contactWorld);
        int layer = ArmCanvasLayer();

        var go = new GameObject("Dot",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = layer;
        go.transform.SetParent(armLayout.canvasRect, false);

        var rect = (RectTransform)go.transform;
        rect.anchoredPosition = local;
        rect.sizeDelta  = placedDotCanvasSize;
        rect.localScale = Vector3.one;

        var img = go.GetComponent<Image>();
        img.sprite = GetCircleSprite();
        img.type   = Image.Type.Simple;
        img.color  = dotColor;
        img.raycastTarget = false;

        var marker = go.AddComponent<Experiment1ADotMarker>();
        marker.DotId = _nextDotId++;
        _dots.Add(marker);
        return marker;
    }

    bool TryFindSnapDot(Vector3 contactWorld, out Experiment1ADotMarker snapped)
    {
        snapped = null;
        if (_dots.Count == 0) return false;

        RectTransform canvas = armLayout.canvasRect ?? armLayout.transform as RectTransform;
        Vector2 fp = armLayout.TapWorldToCanvasLocal(contactWorld);

        float bestDist = float.MaxValue;
        foreach (var dot in _dots)
        {
            if (dot == null || dot == _ghostMarker || dot == _drawChainLast) continue;
            if (dot.RectTransform == null) continue;

            Vector2 dp = canvas.InverseTransformPoint(dot.RectTransform.position);
            float r = Mathf.Max(dot.RectTransform.sizeDelta.x,
                                dot.RectTransform.sizeDelta.y) * 0.5f + dotPickPaddingLocal;
            float d = (fp - dp).magnitude;
            if (d < r && d < bestDist) { bestDist = d; snapped = dot; }
        }
        return snapped != null;
    }

    // ── Resize mode ────────────────────────────────────────────────────────────

    void ProcessResizeMode()
    {
        if (touchInput == null) return;

        bool pressHeld  = touchInput.touchState == TouchInputManager.TouchState.Press;
        bool pressBegan = pressHeld && !_wasArmPressHeld;
        bool pressEnded = !pressHeld && _wasArmPressHeld;
        _wasArmPressHeld = pressHeld;

        if (pressBegan)
            TryPickDotAtContact(touchInput.contactPoint, out _resizeDragDot);

        if (pressHeld && _resizeDragDot?.RectTransform != null)
            _resizeDragDot.RectTransform.anchoredPosition =
                armLayout.TapWorldToCanvasLocal(touchInput.contactPoint);
        // Experiment1ALineSegment.LateUpdate (order 98) refreshes geometry each frame.

        if (pressEnded)
        {
            if (_resizeDragDot != null) InvalidateAndRebuildFills();
            _resizeDragDot = null;
        }
    }

    // ── Remove mode ────────────────────────────────────────────────────────────

    void ProcessRemoveMode()
    {
        if (touchInput == null) return;

        bool pressHeld  = touchInput.touchState == TouchInputManager.TouchState.Press;
        bool pressBegan = pressHeld && !_wasArmPressHeld;
        _wasArmPressHeld = pressHeld;

        if (!pressBegan) return;

        if (TryPickDotAtContact(touchInput.contactPoint, out Experiment1ADotMarker dot))
            RemoveDot(dot);
        else
            TryRemoveLineAtContact(touchInput.contactPoint);
    }

    void RemoveDot(Experiment1ADotMarker dot)
    {
        if (dot == null) return;

        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i] == null) continue;
            if (_lines[i].DotA == dot || _lines[i].DotB == dot)
            {
                Destroy(_lines[i].gameObject);
                _lines.RemoveAt(i);
            }
        }

        _dots.Remove(dot);
        Destroy(dot.gameObject);

        if (dot == _drawChainStart) _drawChainStart = null;
        if (dot == _drawChainLast)  { _drawChainLast = null; HideElastic(); }

        RebuildAdjacency();
        InvalidateAndRebuildFills();
    }

    void TryRemoveLineAtContact(Vector3 contactWorld)
    {
        RectTransform canvas = armLayout.canvasRect ?? armLayout.transform as RectTransform;
        Vector2 fp = armLayout.TapWorldToCanvasLocal(contactWorld);

        Experiment1ALineSegment nearest = null;
        float nearestDist = linePickRadiusLocal;

        foreach (var seg in _lines)
        {
            if (seg?.DotA?.RectTransform == null || seg.DotB?.RectTransform == null) continue;
            Vector2 a = canvas.InverseTransformPoint(seg.DotA.RectTransform.position);
            Vector2 b = canvas.InverseTransformPoint(seg.DotB.RectTransform.position);
            float d = DistancePointToSegment(fp, a, b);
            if (d < nearestDist) { nearestDist = d; nearest = seg; }
        }

        if (nearest == null) return;

        _lines.Remove(nearest);
        Destroy(nearest.gameObject);
        RebuildAdjacency();
        InvalidateAndRebuildFills();
    }

    // ── Shared pick ────────────────────────────────────────────────────────────

    bool TryPickDotAtContact(Vector3 contactWorld, out Experiment1ADotMarker picked)
    {
        picked = null;
        if (armLayout == null || _dots.Count == 0) return false;

        RectTransform canvas = armLayout.canvasRect ?? armLayout.transform as RectTransform;
        if (canvas == null) return false;

        Vector2 fp   = armLayout.TapWorldToCanvasLocal(contactWorld);
        float bestDist = float.MaxValue;

        foreach (var dot in _dots)
        {
            if (dot == null || dot == _ghostMarker || dot.RectTransform == null) continue;
            Vector2 dp = canvas.InverseTransformPoint(dot.RectTransform.position);
            float pad = dotPickPaddingLocal;
            float hw  = dot.RectTransform.sizeDelta.x * 0.5f + pad;
            float hh  = dot.RectTransform.sizeDelta.y * 0.5f + pad;
            if (Mathf.Abs(fp.x - dp.x) > hw || Mathf.Abs(fp.y - dp.y) > hh) continue;
            float d = (fp - dp).magnitude;
            if (d < bestDist) { bestDist = d; picked = dot; }
        }
        return picked != null;
    }

    // ── Line / fill management ─────────────────────────────────────────────────

    void TryAddLine(Experiment1ADotMarker a, Experiment1ADotMarker b)
    {
        if (a == null || b == null || a == b) return;
        if (FindLine(a.DotId, b.DotId) != null) return;

        List<int> path = FindPath(a.DotId, b.DotId);
        if (path != null && path.Count >= 2)
            TryFillCycle(path);

        AddEdge(a.DotId, b.DotId);
        CreateLineVisual(a, b);
    }

    Experiment1ALineSegment FindLine(int idA, int idB)
    {
        foreach (var line in _lines)
        {
            if (line == null) continue;
            int a = line.DotA.DotId, b = line.DotB.DotId;
            if ((a == idA && b == idB) || (a == idB && b == idA)) return line;
        }
        return null;
    }

    void CreateLineVisual(Experiment1ADotMarker a, Experiment1ADotMarker b)
    {
        Transform parent = _linesRoot != null ? _linesRoot
            : (Transform)(armLayout.canvasRect ?? armLayout.transform as RectTransform);
        int layer = ArmCanvasLayer();

        var go = new GameObject($"Line_{a.DotId}_{b.DotId}",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
            typeof(Experiment1ALineSegment));
        go.layer = layer;
        go.transform.SetParent(parent, false);

        var seg = go.GetComponent<Experiment1ALineSegment>();
        seg.Initialize(a, b, lineColor, lineThickness, armLayout.canvasRect);
        _lines.Add(seg);
    }

    // ── Graph helpers ──────────────────────────────────────────────────────────

    void AddEdge(int a, int b) { AddDir(a, b); AddDir(b, a); }

    void AddDir(int from, int to)
    {
        if (!_adjacency.TryGetValue(from, out var nb))
        { nb = new List<int>(); _adjacency[from] = nb; }
        if (!nb.Contains(to)) nb.Add(to);
    }

    void RebuildAdjacency()
    {
        _adjacency.Clear();
        foreach (var seg in _lines)
        {
            if (seg?.DotA == null || seg.DotB == null) continue;
            AddEdge(seg.DotA.DotId, seg.DotB.DotId);
        }
    }

    List<int> FindPath(int startId, int endId)
    {
        if (startId == endId) return null;

        var queue  = new Queue<int>();
        var parent = new Dictionary<int, int>();
        queue.Enqueue(startId);
        parent[startId] = startId;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!_adjacency.TryGetValue(cur, out var nb)) continue;
            foreach (int next in nb)
            {
                if (parent.ContainsKey(next)) continue;
                parent[next] = cur;
                if (next == endId) return ReconstructPath(parent, startId, endId);
                queue.Enqueue(next);
            }
        }
        return null;
    }

    static List<int> ReconstructPath(Dictionary<int, int> parent, int start, int end)
    {
        var path = new List<int>();
        int node = end;
        while (node != start) { path.Add(node); node = parent[node]; }
        path.Add(start);
        path.Reverse();
        return path;
    }

    Experiment1ADotMarker FindDot(int dotId)
    {
        foreach (var d in _dots)
            if (d != null && d.DotId == dotId) return d;
        return null;
    }

    // ── Fill helpers ───────────────────────────────────────────────────────────

    void TryFillCycle(IReadOnlyList<int> cycleNodeIds)
    {
        if (cycleNodeIds == null || cycleNodeIds.Count < 3) return;

        string key = BuildCycleKey(cycleNodeIds);
        if (_filledCycles.Contains(key)) return;

        var cycleDots = new List<Experiment1ADotMarker>(cycleNodeIds.Count);
        foreach (int id in cycleNodeIds)
        {
            var d = FindDot(id);
            if (d?.RectTransform == null) return;
            cycleDots.Add(d);
        }

        CreateFillGraphic(cycleDots, key);
        _filledCycles.Add(key);
    }

    void CreateFillGraphic(List<Experiment1ADotMarker> cycleDots, string cycleKey)
    {
        var go = new GameObject($"Fill_{cycleKey}",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Experiment1APolygonFillGraphic));
        go.transform.SetParent(_fillsRoot != null ? _fillsRoot : armLayout.transform, false);

        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;

        var graphic = go.GetComponent<Experiment1APolygonFillGraphic>();
        graphic.color = fillColor;
        graphic.raycastTarget = false;

        RectTransform coordRoot = armLayout.canvasRect ?? armLayout.transform as RectTransform;
        graphic.InitializeDynamic(cycleDots, coordRoot);
    }

    void InvalidateAndRebuildFills()
    {
        if (_fillsRoot != null)
            for (int i = _fillsRoot.childCount - 1; i >= 0; i--)
                Destroy(_fillsRoot.GetChild(i).gameObject);
        _filledCycles.Clear();

        // For each edge (A,B) check whether a path from A→B exists without using
        // the direct A-B edge. If so, that path + the A-B edge forms a closed cycle.
        // Using FindPath(A,B) would always return the 1-hop direct edge and miss the
        // full polygon, so we explicitly exclude the direct edge during the search.
        foreach (var seg in _lines)
        {
            if (seg?.DotA == null || seg.DotB == null) continue;
            int idA = seg.DotA.DotId, idB = seg.DotB.DotId;
            var path = FindPathExcludingEdge(idA, idB, idA, idB);
            if (path != null && path.Count >= 2) TryFillCycle(path);
        }
    }

    /// <summary>
    /// BFS from <paramref name="startId"/> to <paramref name="endId"/> while skipping
    /// the single directed edge (<paramref name="skipFrom"/> → <paramref name="skipTo"/>)
    /// and its reverse. This lets InvalidateAndRebuildFills find the surrounding polygon
    /// without short-circuiting through the edge being tested.
    /// </summary>
    List<int> FindPathExcludingEdge(int startId, int endId, int skipFrom, int skipTo)
    {
        if (startId == endId) return null;

        var queue  = new Queue<int>();
        var parent = new Dictionary<int, int>();
        queue.Enqueue(startId);
        parent[startId] = startId;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!_adjacency.TryGetValue(cur, out var nb)) continue;
            foreach (int next in nb)
            {
                // Skip the excluded edge in both directions.
                if ((cur == skipFrom && next == skipTo) ||
                    (cur == skipTo   && next == skipFrom)) continue;

                if (parent.ContainsKey(next)) continue;
                parent[next] = cur;
                if (next == endId) return ReconstructPath(parent, startId, endId);
                queue.Enqueue(next);
            }
        }
        return null;
    }

    // ── Cleanup ────────────────────────────────────────────────────────────────

    void CleanupDeletedItems()
    {
        bool any = false;
        for (int i = _dots.Count  - 1; i >= 0; i--)
            if (_dots[i]  == null) { _dots.RemoveAt(i);  any = true; }
        for (int i = _lines.Count - 1; i >= 0; i--)
            if (_lines[i] == null) { _lines.RemoveAt(i); any = true; }
        if (any) { RebuildAdjacency(); InvalidateAndRebuildFills(); }
    }

    // ── Utilities ──────────────────────────────────────────────────────────────

    static Sprite _circleSprite;

    /// <summary>
    /// Returns a cached anti-aliased circle sprite generated from a runtime Texture2D.
    /// Avoids relying on the built-in Knob.psd which can be square-ish or missing on device.
    /// </summary>
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
        float r  = cx - 0.5f; // slight inset so edge pixels anti-alias softly

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist  = Mathf.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
                float alpha = Mathf.Clamp01(r - dist + 1f); // 1-pixel soft edge
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        _circleSprite = Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            size);

        return _circleSprite;
    }

    int ArmCanvasLayer()
    {
        if (armLayout?.canvasRect != null)
            return armLayout.canvasRect.gameObject.layer;
        return 0;
    }

    static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f);
        return (p - (a + Mathf.Clamp01(t) * ab)).magnitude;
    }

    static string BuildCycleKey(IReadOnlyList<int> ids)
    {
        var sorted = new List<int>(ids);
        sorted.Sort();
        return string.Join("-", sorted);
    }
}
