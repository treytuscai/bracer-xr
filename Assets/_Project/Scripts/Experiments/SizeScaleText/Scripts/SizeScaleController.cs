using System;
using UnityEngine;

/// <summary>
/// One "gap" variant. Holds the ordered set of size images for that gap level.
/// Drag size_1, size_2, … into the Sizes array in the Inspector.
/// </summary>
[Serializable]
public class GapGroup
{
    [Tooltip("Images for this gap level, ordered size_1 → size_N.")]
    public Texture2D[] sizes;
}

/// <summary>
/// Drives the arm-surface display for the SizeScaleText experiment.
///
/// Image hierarchy:
///   gaps[gapIndex].sizes[sizeIndex]  →  texture shown on arm
///
/// The gap slider switches between GapGroups (different gap settings).
/// The size slider switches between images within the current GapGroup.
///
/// At runtime a Material using Custom/ForearmImageDisplay is created and
/// assigned directly to the ForearmDepthSurface MeshRenderer, leaving all
/// shared material assets untouched (so other scenes are unaffected).
/// </summary>
[DefaultExecutionOrder(110)]  // After ForearmDepthSurface (100)
public class SizeScaleController : MonoBehaviour
{
    [Header("Surface")]
    [Tooltip("ForearmDepthSurface whose renderer receives the image material. " +
             "Auto-found via FindObjectOfType if left empty.")]
    public ForearmDepthSurface surface;

    [Header("Shader — drag ForearmImageDisplay.shader here")]
    [Tooltip("Assign the ForearmImageDisplay shader asset so it is always included " +
             "in the build. Shader.Find() is used as a fallback but can fail on " +
             "device if the shader is not referenced elsewhere.")]
    public Shader imageShader;

    [Header("Images — gaps[0] = smallest gap, gaps[N] = largest gap")]
    [Tooltip("Each GapGroup contains size_1…size_N textures for that gap level. " +
             "The gap slider switches between groups; the size slider switches within one.")]
    public GapGroup[] gaps;

    [Header("Image Layout on Arm")]
    [Tooltip("Fraction of the arm UV patch the image covers. " +
             "1 = full patch, 0.5 = half-width and half-height centred on the arm.")]
    [Range(0.05f, 1f)]
    public float imageScale = 0.6f;

    [Tooltip("Shift the image centre horizontally within the arm UV patch (−0.5 … 0.5).")]
    [Range(-0.5f, 0.5f)]
    public float imageCenterOffsetU = 0f;

    [Tooltip("Shift the image centre vertically within the arm UV patch (−0.5 … 0.5).")]
    [Range(-0.5f, 0.5f)]
    public float imageCenterOffsetV = 0f;

    // ── Runtime state ─────────────────────────────────────────────────────────

    Material _mat;
    int      _currentGapIndex;
    int      _currentSizeIndex;

    static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    static readonly int ScaleId   = Shader.PropertyToID("_ImageScale");
    static readonly int OffsetUId = Shader.PropertyToID("_ImageOffsetU");
    static readonly int OffsetVId = Shader.PropertyToID("_ImageOffsetV");

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (surface == null)
        {
            Debug.LogError("[SizeScaleController] No ForearmDepthSurface found. " +
                           "Assign it in the Inspector or ensure one exists in the scene.");
            return;
        }

        Shader sh = imageShader != null
            ? imageShader
            : Shader.Find("Custom/ForearmImageDisplay");

        if (sh == null)
        {
            Debug.LogError("[SizeScaleController] Shader 'Custom/ForearmImageDisplay' not found. " +
                           "Drag ForearmImageDisplay.shader into the 'Image Shader' field.");
            return;
        }

        _mat = new Material(sh) { name = "SizeScaleImageMat_Instance" };

        MeshRenderer mr = surface.GetComponent<MeshRenderer>();
        if (mr == null) mr = surface.GetComponentInChildren<MeshRenderer>();

        if (mr != null)
        {
            mr.material = _mat;
            Debug.Log("[SizeScaleController] Image material applied to " + mr.gameObject.name);
        }
        else
        {
            Debug.LogWarning("[SizeScaleController] No MeshRenderer on ForearmDepthSurface " +
                             "— image will not appear on the arm.");
        }

        SetGapIndex(0);
        ApplyLayout();
    }

    void Update()
    {
        ApplyLayout();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    void ApplyLayout()
    {
        if (_mat == null) return;
        _mat.SetFloat(ScaleId,   imageScale);
        _mat.SetFloat(OffsetUId, imageCenterOffsetU);
        _mat.SetFloat(OffsetVId, imageCenterOffsetV);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    Texture2D[] CurrentSizes =>
        (gaps != null && gaps.Length > 0 && _currentGapIndex < gaps.Length)
            ? gaps[_currentGapIndex]?.sizes
            : null;

    void ApplyTexture()
    {
        Texture2D[] sizes = CurrentSizes;
        if (_mat == null || sizes == null || sizes.Length == 0) return;

        _currentSizeIndex = Mathf.Clamp(_currentSizeIndex, 0, sizes.Length - 1);
        Texture2D tex = sizes[_currentSizeIndex];
        if (tex != null)
            _mat.SetTexture(MainTexId, tex);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Switch to gap level <paramref name="index"/>. Clamps size index to valid range.</summary>
    public void SetGapIndex(int index)
    {
        if (gaps == null || gaps.Length == 0) return;
        _currentGapIndex = Mathf.Clamp(index, 0, gaps.Length - 1);

        Texture2D[] sizes = CurrentSizes;
        if (sizes != null && sizes.Length > 0)
            _currentSizeIndex = Mathf.Clamp(_currentSizeIndex, 0, sizes.Length - 1);

        ApplyTexture();
    }

    /// <summary>Switch gap by normalised position [0 = first gap, 1 = last gap].</summary>
    public void SetGapFraction(float t)
    {
        if (gaps == null || gaps.Length == 0) return;
        SetGapIndex(Mathf.RoundToInt(Mathf.Clamp01(t) * (gaps.Length - 1)));
    }

    /// <summary>Switch to size index within the current gap group.</summary>
    public void SetImageIndex(int index)
    {
        Texture2D[] sizes = CurrentSizes;
        if (_mat == null || sizes == null || sizes.Length == 0) return;
        _currentSizeIndex = Mathf.Clamp(index, 0, sizes.Length - 1);
        ApplyTexture();
    }

    /// <summary>Switch size by normalised position [0 = first, 1 = last].</summary>
    public void SetImageFraction(float t)
    {
        Texture2D[] sizes = CurrentSizes;
        if (sizes == null || sizes.Length == 0) return;
        SetImageIndex(Mathf.RoundToInt(Mathf.Clamp01(t) * (sizes.Length - 1)));
    }

    // ── Properties ───────────────────────────────────────────────────────────

    public int GapCount        => gaps != null ? gaps.Length : 0;
    public int ImageCount      => CurrentSizes?.Length ?? 0;
    public int CurrentGapIndex => _currentGapIndex;
    public int CurrentIndex    => _currentSizeIndex;
}
