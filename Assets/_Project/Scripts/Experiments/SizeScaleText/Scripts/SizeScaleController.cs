using UnityEngine;

/// <summary>
/// Drives the arm-surface display for the SizeScaleText experiment.
/// At runtime it creates a Material using Custom/ForearmImageDisplay and
/// assigns it directly to the ForearmDepthSurface's MeshRenderer, replacing
/// whatever shared material asset was set in the Inspector.  This keeps the
/// shared .mat asset untouched and leaves ForearmDepthSurface.SurfaceMat
/// pointing to the original instance (used by ForearmInteraction for the
/// touch-debug overlay — harmless because that shader property check will
/// simply be skipped on the old material).
///
/// Consumers:
///   SizeScaleSlider — calls SetImageIndex / SetImageFraction each frame
///                     the user's finger is on the slider.
/// </summary>
[DefaultExecutionOrder(110)]  // After ForearmDepthSurface (100) which creates its own mat instance
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

    [Header("Images — ordered small → large (size_1 at index 0)")]
    [Tooltip("Assign ScaledInterfaceImgs/size_1, size_2, … in order. " +
             "The slider maps its range to these indices.")]
    public Texture2D[] images;

    // Runtime material instance (Custom/ForearmImageDisplay).
    Material _mat;
    int      _currentIndex;

    static readonly int MainTexId = Shader.PropertyToID("_MainTex");

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

        // Prefer the directly-assigned shader asset; fall back to name lookup.
        Shader sh = imageShader != null
            ? imageShader
            : Shader.Find("Custom/ForearmImageDisplay");

        if (sh == null)
        {
            Debug.LogError("[SizeScaleController] Shader 'Custom/ForearmImageDisplay' not found. " +
                           "Drag ForearmImageDisplay.shader into the 'Image Shader' field in the Inspector.");
            return;
        }

        // Build a fresh material instance (not sourced from any .mat asset).
        _mat = new Material(sh) { name = "SizeScaleImageMat_Instance" };

        // Replace the mesh renderer material so the display patch shows the
        // selected image from frame 1.
        MeshRenderer mr = surface.GetComponent<MeshRenderer>();
        if (mr == null) mr = surface.GetComponentInChildren<MeshRenderer>();

        if (mr != null)
        {
            mr.material = _mat;
            Debug.Log("[SizeScaleController] Image material applied to " + mr.gameObject.name);
        }
        else
        {
            Debug.LogWarning("[SizeScaleController] No MeshRenderer found on ForearmDepthSurface " +
                             "— image will not appear on the arm.");
        }

        // Show the first image immediately.
        SetImageIndex(0);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Select the image at <paramref name="index"/> [0, images.Length-1].
    /// The slider calls this each frame the finger is on the track.
    /// </summary>
    public void SetImageIndex(int index)
    {
        if (_mat == null || images == null || images.Length == 0) return;

        _currentIndex = Mathf.Clamp(index, 0, images.Length - 1);

        Texture2D tex = images[_currentIndex];
        if (tex != null)
            _mat.SetTexture(MainTexId, tex);
    }

    /// <summary>
    /// Select an image by normalised slider position [0 = first, 1 = last].
    /// Rounds to the nearest discrete index.
    /// </summary>
    public void SetImageFraction(float t)
    {
        if (images == null || images.Length == 0) return;
        SetImageIndex(Mathf.RoundToInt(Mathf.Clamp01(t) * (images.Length - 1)));
    }

    public int ImageCount    => images != null ? images.Length : 0;
    public int CurrentIndex  => _currentIndex;
}
