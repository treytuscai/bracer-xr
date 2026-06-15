using System;

using System.Collections;

using System.Collections.Generic;

using Experiments.Cli;

using UnityEngine;

using UnityEngine.UI;



/// <summary>

/// 1H Horiz/Vertical — auto-bakes interface art per body region and Auto orientation.

/// Config mode enables tap-to-place calibration; saved placements persist for later runs.

/// </summary>

[DefaultExecutionOrder(125)]

public class OneHHorizVerticalController : MonoBehaviour, IExperimentCommands

{

    [Serializable]

    public struct OrientedPlacement

    {

        [Tooltip("When enabled, col/row are derived from mesh UV each bake.")]

        public bool useMeshUv;

        [Range(0f, 1f)] public float meshU;

        [Range(0f, 1f)] public float meshV;

        public int col;

        public int row;

        [Min(0.25f)] public float scale;

        public float rotationDegrees;

    }



    [Serializable]

    public struct BodyRegionSlot

    {

        public string label;



        [Header("Surface window (optional)")]

        public bool overrideDisplayWindow;

        public float displayOffset;

        public float displayHeight;

        public float displayWidth;



        [Header("Vertical (IsLandscape = false)")]

        public OrientedPlacement vertical;



        [Header("Horizontal (IsLandscape = true)")]

        public OrientedPlacement horizontal;

    }



    [Header("References")]

    public OneHHorizVerticalGridController grid;

    public ForearmDepthSurface surface;

    public ForearmInteraction interaction;

    public MonoBehaviour widgetPlacement;

    public RevisedForearmTouchManager touchManager;

    public Transform headAnchor;



    [Header("Interface art")]

    public Texture2D verticalInterface;

    public Texture2D horizontalInterface;

    [Tooltip("True pixel size of verticalInterface PNG (file is 400×700). Used for aspect — not rotation.")]
    public Vector2Int verticalNativePixels = new Vector2Int(400, 700);

    [Tooltip("True pixel size of horizontalInterface PNG (file is 700×400). Used for aspect — not rotation.")]
    public Vector2Int horizontalNativePixels = new Vector2Int(700, 400);



    [Header("Body regions (expctl next)")]

    public BodyRegionSlot[] regions;



    [Header("Config mode")]

    [Tooltip("When on: pick from palette and place to calibrate. Saved placements used when off.")]

    public bool configMode;

    [Tooltip("Use JSON saved placements when config mode is off. Inspector defaults are fallback.")]
    public bool useSavedPlacements = true;
    [Tooltip("World-space vertical offset for the config palette (negative = lower).")]
    public float configPaletteHeightOffset = -0.18f;
    public float configPaletteDistance = 0.55f;
    public float configPaletteLateralOffset = 0.22f;



    [Header("Debug")]

    public bool logOrientationChanges = true;



    int _regionIndex;

    bool _showingHorizontalInterface;

    bool _hasOrientationSample;

    bool _initialized;

    bool _rebakeQueued;

    IForearmWidgetPlacement _placement;

    RectTransform _bakeRoot;

    OneHHorizVerticalConfigPalette _configPalette;

    OneHHorizVerticalConfigRecorder _configRecorder;

    float _savedDisplayOffset;

    float _savedDisplayHeight;

    float _savedDisplayWidth;

    bool _savedDisplayWindow;

    Vector2 _latchedTouchUv;

    int _latchedCol = -1;

    int _latchedRow = -1;

    bool _hasLatchedTouch;



    public bool ConfigMode => configMode;

    public bool ShowingHorizontalInterface => _showingHorizontalInterface;

    public int RegionIndex => _regionIndex;



    void Awake()

    {

        if (grid == null)

            grid = FindObjectOfType<OneHHorizVerticalGridController>();



        if (surface == null && grid != null)

            surface = grid.GetComponent<ForearmDepthSurface>();



        if (surface == null)

            surface = FindObjectOfType<ForearmDepthSurface>();



        if (interaction == null && surface != null)

            interaction = surface.GetComponent<ForearmInteraction>();



        if (touchManager == null)

            touchManager = FindObjectOfType<RevisedForearmTouchManager>();



        if (headAnchor == null && surface != null)

            headAnchor = surface.centerEyeAnchor;



        _placement = ResolvePlacement(widgetPlacement);

        if (_placement == null && touchManager != null)

            _placement = ResolvePlacement(touchManager.widgetPlacement);



        var rootGo = new GameObject("[OneHBakeRoot]");

        rootGo.hideFlags = HideFlags.HideAndDontSave;

        _bakeRoot = rootGo.AddComponent<RectTransform>();



        EnsureConfigSystems();

        CacheDefaultDisplayWindow();

        OneHHorizVerticalPlacementStore.EnsureLoaded();

        OneHHorizVerticalPlacementStore.EnsureRegionCount(regions != null ? regions.Length : 0);

    }



    void Start()

    {

        StartCoroutine(InitializeAfterGridReady());

    }



    void LateUpdate()
    {
        TrackTouchSample();

        if (!_initialized || surface == null)
            return;

        bool landscape = ResolveShowingHorizontalInterface();

        if (configMode)
        {
            if (landscape != _showingHorizontalInterface)
            {
                _showingHorizontalInterface = landscape;
                _configPalette?.RefreshTemplateVisibility();
            }
            return;
        }

        if (!_hasOrientationSample)
        {
            _showingHorizontalInterface = landscape;
            _hasOrientationSample = true;
            return;
        }

        if (landscape != _showingHorizontalInterface)
        {
            if (logOrientationChanges)
                Debug.Log($"[OneHHorizVertical] Interface → {(landscape ? "Horizontal" : "Vertical")} (Auto)");
            _showingHorizontalInterface = landscape;
            QueueRebake();
        }
    }



    void EnsureConfigSystems()

    {

        if (_configPalette == null)

        {

            var paletteGo = new GameObject("[OneHConfigPalette]");

            paletteGo.SetActive(false);

            _configPalette = paletteGo.AddComponent<OneHHorizVerticalConfigPalette>();

            _configPalette.controller = this;

            _configPalette.headAnchor = headAnchor;

            _configPalette.widgetPlacement = widgetPlacement != null
                ? widgetPlacement
                : touchManager != null ? touchManager.widgetPlacement : null;
            ApplyConfigPaletteLayout();
        }



        if (_configRecorder == null)

        {

            var recorderGo = new GameObject("[OneHConfigRecorder]");

            recorderGo.transform.SetParent(transform);

            _configRecorder = recorderGo.AddComponent<OneHHorizVerticalConfigRecorder>();

            _configRecorder.controller = this;

            _configRecorder.grid = grid;

            _configRecorder.interaction = interaction;

            _configRecorder.widgetPlacement = widgetPlacement;

            _configRecorder.touchManager = touchManager;

        }

    }



    void ApplyConfigPaletteLayout()
    {
        if (_configPalette == null)
            return;

        _configPalette.heightOffsetMeters = configPaletteHeightOffset;
        _configPalette.distanceMeters = configPaletteDistance;
        _configPalette.lateralOffsetMeters = configPaletteLateralOffset;
        _configPalette.ApplyLayout();
    }

    void TrackTouchSample()

    {

        if (interaction == null || grid == null || !interaction.IsActive)

            return;



        _latchedTouchUv = interaction.TouchUV;

        grid.UVToCell(_latchedTouchUv, out _latchedCol, out _latchedRow);

        _hasLatchedTouch = true;

    }



    IEnumerator InitializeAfterGridReady()

    {

        for (int i = 0; i < 3; i++)

            yield return null;



        const int maxFrames = 60;

        for (int i = 0; i < maxFrames; i++)

        {

            if (grid != null && grid.Columns > 0 && grid.Rows > 0)

                break;

            yield return null;

        }



        if (regions == null || regions.Length == 0)

            regions = CreateDefaultRegions();



        _regionIndex = 0;

        _hasOrientationSample = false;

        ApplyConfigModeState();

        _initialized = true;



        if (configMode)

        {

            Debug.Log("[OneHHorizVertical] CONFIG MODE — expctl next to change region, place vertical/horizontal " +

                      "interface from palette. expctl config off when done.");

        }

        else

        {

            ApplyRegionAndRebake();

            Debug.Log($"[OneHHorizVertical] Region 1/{regions.Length}: '{CurrentRegion().label}'. " +

                      "expctl config on to calibrate | next | status");

        }

    }



    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)

    {

        commands["next"] = _ => AdvanceRegion();

        commands["loguv"] = _ => LogTouchUv();

        commands["uv"] = _ => LogTouchUv();

        commands["status"] = _ => BuildStatus();

        commands["config"] = HandleConfigCommand;

    }



    string HandleConfigCommand(IReadOnlyDictionary<string, string> args)

    {

        if (args.TryGetValue("0", out string sub))

        {

            if (string.Equals(sub, "on", StringComparison.OrdinalIgnoreCase))

                return SetConfigMode(true);

            if (string.Equals(sub, "off", StringComparison.OrdinalIgnoreCase))

                return SetConfigMode(false);

            if (string.Equals(sub, "clear", StringComparison.OrdinalIgnoreCase))

            {

                OneHHorizVerticalPlacementStore.ClearAll();

                ClearArm();

                return "cleared saved placements";

            }

        }



        return SetConfigMode(!configMode);

    }



    public string SetConfigMode(bool enabled)

    {

        configMode = enabled;

        if (!_initialized)

            return enabled ? "config on (initializing...)" : "config off (initializing...)";



        ApplyConfigModeState();

        return enabled

            ? "config on — place interface from palette for each region/orientation"

            : "config off — using saved placements";

    }



    void ApplyConfigModeState()

    {

        EnsureConfigSystems();



        if (configMode)
        {
            ClearArm();
            ApplyConfigPaletteLayout();
            _configPalette.SetActive(true);

            _configPalette.RefreshTemplateVisibility();



            if (touchManager != null && _configPalette.Palette != null)

                touchManager.possibleUIPalette = _configPalette.Palette;

        }

        else

        {

            _configPalette.SetActive(false);



            if (touchManager != null)

                touchManager.possibleUIPalette = null;



            _hasOrientationSample = false;

            ApplyRegionAndRebake();

        }

    }



    public void OnConfigPlacementRecorded(OrientedPlacement placement, bool horizontal)

    {

        var slot = CurrentRegion();

        OneHHorizVerticalPlacementStore.Save(

            _regionIndex,

            slot.label,

            horizontal,

            OneHHorizVerticalPlacementStore.FromOrientedPlacement(placement));



        if (horizontal)

            Debug.Log($"[OneHHorizVertical] Config saved HORIZONTAL placement for '{slot.label}'.");

        else

            Debug.Log($"[OneHHorizVertical] Config saved VERTICAL placement for '{slot.label}'.");

    }



    public string AdvanceRegion()

    {

        if (!_initialized || regions == null || regions.Length == 0)

            return "not ready";



        _regionIndex = (_regionIndex + 1) % regions.Length;



        if (configMode)

        {

            ClearArm();

            _configPalette?.RefreshTemplateVisibility();

            ApplyRegionSurfaceWindow(CurrentRegion());

            return $"config region {_regionIndex + 1}/{regions.Length}: {CurrentRegion().label}";

        }



        ApplyRegionAndRebake();

        return $"region {_regionIndex + 1}/{regions.Length}: {CurrentRegion().label}";

    }



    public string LogTouchUv()

    {

        if (grid == null)

            return "missing grid";



        if (!_hasLatchedTouch)

            return "touch arm with other hand, then run loguv again";



        var slot = CurrentRegion();

        bool horizontal = ResolveShowingHorizontalInterface();

        string msg = $"uv=({_latchedTouchUv.x:F3},{_latchedTouchUv.y:F3}) cell=({_latchedCol},{_latchedRow}) " +

                     $"region='{slot.label}' orient={(horizontal ? "H" : "V")} (latched)";

        Debug.Log($"[OneHHorizVertical] {msg}");

        return msg;

    }



    public string BuildStatus()

    {

        if (!_initialized)

            return "not ready";



        var slot = CurrentRegion();

        bool horizontal = ResolveShowingHorizontalInterface();

        ResolveActiveOrientedPlacement(slot, horizontal, out var placement);

        string touch = _hasLatchedTouch

            ? $"lastTouch=({_latchedTouchUv.x:F3},{_latchedTouchUv.y:F3}) cell=({_latchedCol},{_latchedRow})"

            : "lastTouch=none";

        string source = configMode ? "config" : (useSavedPlacements && HasSavedPlacement(_regionIndex, horizontal) ? "saved" : "inspector");



        return $"config={configMode} region={slot.label} interface={(horizontal ? "Horizontal" : "Vertical")} " +

               $"src={source} uv=({placement.meshU:F3},{placement.meshV:F3}) rot={placement.rotationDegrees:F0}° {touch}";

    }



    void ApplyRegionAndRebake()

    {

        ApplyRegionSurfaceWindow(CurrentRegion());

        QueueRebake();

    }



    void QueueRebake()

    {

        if (_rebakeQueued || configMode)

            return;



        _rebakeQueued = true;

        StartCoroutine(RebakeAfterSurfaceSettles());

    }



    IEnumerator RebakeAfterSurfaceSettles()

    {

        for (int i = 0; i < 2; i++)

            yield return null;



        _rebakeQueued = false;

        RebakeCurrentInterface();

    }



    void RebakeCurrentInterface()

    {

        if (grid == null || configMode)

            return;



        _showingHorizontalInterface = ResolveShowingHorizontalInterface();

        _hasOrientationSample = true;



        ClearArm();



        Texture2D tex = ResolveActiveTexture();

        if (tex == null)

        {

            Debug.LogWarning("[OneHHorizVertical] No interface texture assigned.");

            return;

        }



        var slot = CurrentRegion();

        ResolveActiveOrientedPlacement(slot, _showingHorizontalInterface, out OrientedPlacement placement);

        ResolvePlacementCell(placement, out int col, out int row);

        ResolveNativePixelSize(tex, out int nativeW, out int nativeH);

        if (!grid.TryBakeTextureIntoCell(tex, col, row, placement.scale, placement.rotationDegrees,
                nativeW, nativeH))
        {
            Debug.LogWarning($"[OneHHorizVertical] Bake failed at ({col},{row}) for '{slot.label}'.");
            return;
        }
    }



    bool ResolveShowingHorizontalInterface() =>

        surface != null && surface.IsLandscape;



    void ResolveActiveOrientedPlacement(BodyRegionSlot slot, bool horizontal, out OrientedPlacement placement)

    {

        placement = horizontal ? slot.horizontal : slot.vertical;



        if (!configMode && useSavedPlacements &&

            OneHHorizVerticalPlacementStore.TryGet(_regionIndex, horizontal, out var saved))

        {

            placement = OneHHorizVerticalPlacementStore.ToOrientedPlacement(saved);

        }

    }



    static bool HasSavedPlacement(int regionIndex, bool horizontal) =>

        OneHHorizVerticalPlacementStore.TryGet(regionIndex, horizontal, out _);



    void ResolveNativePixelSize(Texture2D tex, out int width, out int height)
    {
        if (tex == verticalInterface && verticalNativePixels.x > 0 && verticalNativePixels.y > 0)
        {
            width = verticalNativePixels.x;
            height = verticalNativePixels.y;
            return;
        }

        if (tex == horizontalInterface && horizontalNativePixels.x > 0 && horizontalNativePixels.y > 0)
        {
            width = horizontalNativePixels.x;
            height = horizontalNativePixels.y;
            return;
        }

        width = tex != null ? tex.width : 1;
        height = tex != null ? tex.height : 1;
    }

    Texture2D ResolveActiveTexture()

    {

        if (_showingHorizontalInterface && horizontalInterface != null)

            return horizontalInterface;

        if (verticalInterface != null)

            return verticalInterface;

        return horizontalInterface;

    }



    RectTransform CreateBakeWidget(Texture2D texture)

    {

        var go = new GameObject("OneH_InterfaceBake", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));

        go.transform.SetParent(_bakeRoot, false);



        var rt = go.GetComponent<RectTransform>();

        rt.sizeDelta = new Vector2(texture.width, texture.height);



        var raw = go.GetComponent<RawImage>();

        raw.texture = texture;

        raw.color = Color.white;



        return rt;

    }



    void ClearArm()

    {

        if (_placement != null)

        {

            if (_placement.IsCarrying)

                _placement.DestroyCarriedItem();

            _placement.ClearAll();

        }

        else

        {

            grid?.ClearAll();

        }



        grid?.ClearCarryPreviewSource();

        grid?.ClearHighlight();

    }



    void ApplyRegionSurfaceWindow(BodyRegionSlot slot)

    {

        if (surface == null)

            return;



        if (!slot.overrideDisplayWindow)

        {

            if (_savedDisplayWindow)

            {

                surface.displayOffset = _savedDisplayOffset;

                surface.displayHeight = _savedDisplayHeight;

                surface.displayWidth = _savedDisplayWidth;

            }

            return;

        }



        surface.displayOffset = slot.displayOffset;

        surface.displayHeight = slot.displayHeight;

        surface.displayWidth = slot.displayWidth;

    }



    void CacheDefaultDisplayWindow()

    {

        if (surface == null)

            return;



        _savedDisplayOffset = surface.displayOffset;

        _savedDisplayHeight = surface.displayHeight;

        _savedDisplayWidth = surface.displayWidth;

        _savedDisplayWindow = true;

    }



    void ResolvePlacementCell(OrientedPlacement placement, out int col, out int row)
    {
        if (placement.useMeshUv && grid != null)
        {
            grid.UVToCell(new Vector2(placement.meshU, placement.meshV), out col, out row);
            return;
        }

        col = placement.col;
        row = placement.row;
    }



    BodyRegionSlot CurrentRegion()

    {

        if (regions == null || regions.Length == 0)

            return default;



        _regionIndex = Mathf.Clamp(_regionIndex, 0, regions.Length - 1);

        return regions[_regionIndex];

    }



    static BodyRegionSlot[] CreateDefaultRegions() => new[]

    {

        new BodyRegionSlot

        {

            label = "Inner Forearm",

            vertical = new OrientedPlacement { useMeshUv = true, meshU = 0.25f, meshV = 0.45f, col = 10, row = 9, scale = 4f },

            horizontal = new OrientedPlacement { useMeshUv = true, meshU = 0.25f, meshV = 0.45f, col = 10, row = 9, scale = 4f, rotationDegrees = -90f }

        },

        new BodyRegionSlot

        {

            label = "Outer Forearm",

            vertical = new OrientedPlacement { useMeshUv = true, meshU = 0.75f, meshV = 0.45f, col = 10, row = 9, scale = 4f },

            horizontal = new OrientedPlacement { useMeshUv = true, meshU = 0.75f, meshV = 0.45f, col = 10, row = 9, scale = 4f, rotationDegrees = -90f }

        },

        new BodyRegionSlot

        {

            label = "Back of Hand",

            overrideDisplayWindow = true,

            displayOffset = 0.18f, displayHeight = 0.35f, displayWidth = 0.35f,

            vertical = new OrientedPlacement { useMeshUv = true, meshU = 0.25f, meshV = 0.72f, col = 10, row = 14, scale = 3.5f },

            horizontal = new OrientedPlacement { useMeshUv = true, meshU = 0.25f, meshV = 0.72f, col = 10, row = 14, scale = 3.5f, rotationDegrees = -90f }

        },

        new BodyRegionSlot

        {

            label = "Palm",

            overrideDisplayWindow = true,

            displayOffset = 0.18f, displayHeight = 0.35f, displayWidth = 0.35f,

            vertical = new OrientedPlacement { useMeshUv = true, meshU = 0.75f, meshV = 0.72f, col = 10, row = 14, scale = 3.5f },

            horizontal = new OrientedPlacement { useMeshUv = true, meshU = 0.75f, meshV = 0.72f, col = 10, row = 14, scale = 3.5f, rotationDegrees = -90f }

        }

    };



    static IForearmWidgetPlacement ResolvePlacement(MonoBehaviour behaviour)

    {

        if (behaviour == null)

            return null;



        if (behaviour is IForearmWidgetPlacement direct)

            return direct;



        foreach (var mb in behaviour.GetComponents<MonoBehaviour>())

        {

            if (mb is IForearmWidgetPlacement found)

                return found;

        }



        return null;

    }

}


