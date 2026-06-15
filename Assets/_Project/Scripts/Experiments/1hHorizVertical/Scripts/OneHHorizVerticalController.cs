using System;
using System.Collections;
using System.Collections.Generic;
using Experiments.Cli;
using Surface.Core;
using UnityEngine;

/// <summary>
/// 1H Horiz/Vertical — projects interface art at mesh UV anchors (no grid).
/// expctl next steps through each region × orientation (Inner V → Inner H → Outer V → …).
/// </summary>
[DefaultExecutionOrder(125)]
public class OneHHorizVerticalController : MonoBehaviour, IExperimentCommands
{
    [Serializable]
    public struct OrientedPlacement
    {
        [Range(0f, 1f)] public float meshU;
        [Range(0f, 1f)] public float meshV;
        [Tooltip("Bigger value = larger image on the arm. PNG aspect ratio is preserved. Try 0.05–0.12.")]
        [Min(0.005f)] public float size;
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

        [Header("Vertical interface")]
        public OrientedPlacement vertical;

        [Header("Horizontal interface")]
        public OrientedPlacement horizontal;
    }

    [Header("References")]
    public ForearmDepthSurface surface;
    public ForearmInteraction interaction;

    [Header("Shader — OneHForearmUvImage.shader")]
    public Shader uvImageShader;

    [Header("Interface art")]
    public Texture2D verticalInterface;
    public Texture2D horizontalInterface;

    [Tooltip("True pixel size of verticalInterface PNG (400×700).")]
    public Vector2Int verticalNativePixels = new Vector2Int(400, 700);

    [Tooltip("True pixel size of horizontalInterface PNG (700×400).")]
    public Vector2Int horizontalNativePixels = new Vector2Int(700, 400);

    [Header("Body regions (expctl next)")]
    public BodyRegionSlot[] regions;

    [Header("Saved placements")]
    [Tooltip("Use JSON saved placements when inspector defaults are not enough.")]
    public bool useSavedPlacements = true;

    [Header("Debug")]
    public bool logOnNext = true;

    int _regionIndex;
    int _stepIndex;
    bool _showingHorizontalInterface;
    bool _initialized;
    Material _mat;
    Vector2 _latchedTouchUv;
    bool _hasLatchedTouch;

    float _savedDisplayOffset;
    float _savedDisplayHeight;
    float _savedDisplayWidth;

    static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    static readonly int TintId = Shader.PropertyToID("_Tint");
    static readonly int CenterId = Shader.PropertyToID("_ImageCenterUV");
    static readonly int SizeId = Shader.PropertyToID("_ImageSize");
    static readonly int AspectId = Shader.PropertyToID("_ImageAspect");
    static readonly int RotationId = Shader.PropertyToID("_ImageRotation");

    public bool ShowingHorizontalInterface => _showingHorizontalInterface;
    public int RegionIndex => _regionIndex;

    void Awake()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (interaction == null && surface != null)
            interaction = surface.GetComponent<ForearmInteraction>();

        CacheDefaultDisplayWindow();
        OneHHorizVerticalPlacementStore.EnsureLoaded();
        OneHHorizVerticalPlacementStore.EnsureRegionCount(regions != null ? regions.Length : 0);
    }

    void Start()
    {
        StartCoroutine(InitializeAfterSurfaceReady());
    }

    void LateUpdate()
    {
        if (interaction != null && interaction.IsActive)
        {
            _latchedTouchUv = interaction.TouchUV;
            _hasLatchedTouch = true;
        }

        if (_initialized && _mat != null)
            ApplyDisplay();
    }

    IEnumerator InitializeAfterSurfaceReady()
    {
        for (int i = 0; i < 3; i++)
            yield return null;

        if (regions == null || regions.Length == 0)
            regions = CreateDefaultRegions();

        if (!EnsureDisplayMaterial())
            yield break;

        LockSurfaceOrientation();
        ApplyStepIndex(0);
        ApplyRegionAndDisplay();
        _initialized = true;

        Debug.Log($"[OneHHorizVertical] {FormatStepLabel()}. expctl next steps V→H per region | loguv | status");
    }

    bool EnsureDisplayMaterial()
    {
        if (surface == null)
        {
            Debug.LogError("[OneHHorizVertical] No ForearmDepthSurface found.");
            return false;
        }

        Shader sh = uvImageShader != null
            ? uvImageShader
            : Shader.Find("Custom/OneHForearmUvImage");

        if (sh == null)
        {
            Debug.LogError("[OneHHorizVertical] Shader 'Custom/OneHForearmUvImage' not found.");
            return false;
        }

        _mat = new Material(sh) { name = "OneHUvImageMat_Instance" };

        var mr = surface.GetComponent<MeshRenderer>() ?? surface.GetComponentInChildren<MeshRenderer>();
        if (mr != null)
            mr.material = _mat;
        else
            Debug.LogWarning("[OneHHorizVertical] No MeshRenderer on ForearmDepthSurface.");

        return true;
    }

    void LockSurfaceOrientation()
    {
        surface.orientationMode = DisplayOrientation.Portrait;
        surface.portraitTexture = null;
        surface.landscapeTexture = null;
    }

    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["next"] = _ => AdvanceRegion();
        commands["loguv"] = _ => LogTouchUv();
        commands["uv"] = _ => LogTouchUv();
        commands["status"] = _ => BuildStatus();
        commands["orient"] = HandleOrientCommand;
        commands["config"] = HandleConfigCommand;
    }

    string HandleOrientCommand(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("0", out string sub))
        {
            if (string.Equals(sub, "v", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sub, "vertical", StringComparison.OrdinalIgnoreCase))
                return SetOrientation(false);
            if (string.Equals(sub, "h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sub, "horizontal", StringComparison.OrdinalIgnoreCase))
                return SetOrientation(true);
        }

        return SetOrientation(!_showingHorizontalInterface);
    }

    string HandleConfigCommand(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("0", out string sub) &&
            string.Equals(sub, "clear", StringComparison.OrdinalIgnoreCase))
        {
            OneHHorizVerticalPlacementStore.ClearAll();
            ApplyRegionAndDisplay();
            return "cleared saved placements";
        }

        return "config palette removed — edit UVs in Inspector or use loguv while touching the arm";
    }

    string SetOrientation(bool horizontal)
    {
        if (!_initialized)
            return "not ready";

        _showingHorizontalInterface = horizontal;
        _stepIndex = RegionOrientationToStep(_regionIndex, horizontal);
        ApplyDisplay();
        return horizontal ? "interface: Horizontal" : "interface: Vertical";
    }

    int TotalSteps => regions != null && regions.Length > 0 ? regions.Length * 2 : 0;

    void ApplyStepIndex(int step)
    {
        if (regions == null || regions.Length == 0)
            return;

        int total = TotalSteps;
        _stepIndex = ((step % total) + total) % total;
        _regionIndex = _stepIndex / 2;
        _showingHorizontalInterface = (_stepIndex % 2) == 1;
    }

    static int RegionOrientationToStep(int regionIndex, bool horizontal) =>
        regionIndex * 2 + (horizontal ? 1 : 0);

    string FormatStepLabel()
    {
        var slot = CurrentRegion();
        string orient = _showingHorizontalInterface ? "Horizontal" : "Vertical";
        return $"step {_stepIndex + 1}/{TotalSteps}: {slot.label} ({orient})";
    }

    public string AdvanceRegion()
    {
        if (!_initialized || regions == null || regions.Length == 0)
            return "not ready";

        ApplyStepIndex(_stepIndex + 1);
        ApplyRegionAndDisplay();

        if (logOnNext)
            Debug.Log($"[OneHHorizVertical] {FormatStepLabel()}");

        return FormatStepLabel();
    }

    public string LogTouchUv()
    {
        if (!_hasLatchedTouch)
            return "touch arm with other hand, then run loguv again";

        var slot = CurrentRegion();
        string msg = $"uv=({_latchedTouchUv.x:F3},{_latchedTouchUv.y:F3}) " +
                       $"region='{slot.label}' interface={(_showingHorizontalInterface ? "H" : "V")} (latched)";
        Debug.Log($"[OneHHorizVertical] {msg}");
        return msg;
    }

    public string BuildStatus()
    {
        if (!_initialized)
            return "not ready";

        var slot = CurrentRegion();
        ResolveActivePlacement(slot, _showingHorizontalInterface, out var placement);
        string touch = _hasLatchedTouch
            ? $"lastTouch=({_latchedTouchUv.x:F3},{_latchedTouchUv.y:F3})"
            : "lastTouch=none";
        string source = useSavedPlacements && HasSavedPlacement(_regionIndex, _showingHorizontalInterface)
            ? "saved"
            : "inspector";

        return $"{FormatStepLabel()} src={source} center=({placement.meshU:F3},{placement.meshV:F3}) " +
               $"size={placement.size:F3} rot={placement.rotationDegrees:F0}° {touch}";
    }

    void ApplyRegionAndDisplay()
    {
        ApplyRegionSurfaceWindow(CurrentRegion());
        ApplyDisplay();
    }

    void ApplyDisplay()
    {
        if (_mat == null || surface == null)
            return;

        Texture2D tex = ResolveActiveTexture();
        if (tex == null)
        {
            Debug.LogWarning("[OneHHorizVertical] No interface texture assigned.");
            return;
        }

        var slot = CurrentRegion();
        ResolveActivePlacement(slot, _showingHorizontalInterface, out OrientedPlacement placement);
        ResolveNativePixelSize(tex, out int nativeW, out int nativeH);
        float aspect = nativeW / (float)Mathf.Max(1, nativeH);

        _mat.SetTexture(MainTexId, tex);
        _mat.SetColor(TintId, Color.white);
        _mat.SetVector(CenterId, new Vector4(placement.meshU, placement.meshV, 0f, 0f));
        _mat.SetFloat(SizeId, Mathf.Max(0.005f, placement.size));
        _mat.SetFloat(AspectId, aspect);
        _mat.SetFloat(RotationId, placement.rotationDegrees / 360f);
    }

    void ResolveActivePlacement(BodyRegionSlot slot, bool horizontal, out OrientedPlacement placement)
    {
        placement = horizontal ? slot.horizontal : slot.vertical;

        if (useSavedPlacements &&
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

    void ApplyRegionSurfaceWindow(BodyRegionSlot slot)
    {
        if (surface == null)
            return;

        if (!slot.overrideDisplayWindow)
        {
            surface.displayOffset = _savedDisplayOffset;
            surface.displayHeight = _savedDisplayHeight;
            surface.displayWidth = _savedDisplayWidth;
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
            vertical = new OrientedPlacement { meshU = 0.25f, meshV = 0.45f, size = 0.08f },
            horizontal = new OrientedPlacement { meshU = 0.25f, meshV = 0.45f, size = 0.08f, rotationDegrees = -90f }
        },
        new BodyRegionSlot
        {
            label = "Outer Forearm",
            vertical = new OrientedPlacement { meshU = 0.75f, meshV = 0.45f, size = 0.08f },
            horizontal = new OrientedPlacement { meshU = 0.75f, meshV = 0.45f, size = 0.08f, rotationDegrees = -90f }
        },
        new BodyRegionSlot
        {
            label = "Back of Hand",
            overrideDisplayWindow = true,
            displayOffset = 0.18f, displayHeight = 0.35f, displayWidth = 0.35f,
            vertical = new OrientedPlacement { meshU = 0.25f, meshV = 0.72f, size = 0.07f },
            horizontal = new OrientedPlacement { meshU = 0.25f, meshV = 0.72f, size = 0.07f, rotationDegrees = -90f }
        },
        new BodyRegionSlot
        {
            label = "Palm",
            overrideDisplayWindow = true,
            displayOffset = 0.18f, displayHeight = 0.35f, displayWidth = 0.35f,
            vertical = new OrientedPlacement { meshU = 0.75f, meshV = 0.72f, size = 0.07f },
            horizontal = new OrientedPlacement { meshU = 0.75f, meshV = 0.72f, size = 0.07f, rotationDegrees = -90f }
        }
    };

    void OnDestroy()
    {
        if (_mat != null)
            Destroy(_mat);
    }
}
