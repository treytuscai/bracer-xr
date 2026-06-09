using UnityEngine;

/// <summary>
/// RevisedBoundingBox experiment.
///
/// Overlays an adjustable grid onto the forearm depth-surface mesh and lets the
/// user paint individual cells with the right index fingertip using the colour
/// chosen on the floating hue slider:
///   • Touch an empty cell  → fills it with the current slider colour.
///   • Touch a painted cell → clears it back to semi-transparent default.
///
/// Each cell stores its own RGBA colour, so multiple colours can coexist on the arm.
/// Grid rendering uses Custom/ForearmGrid (shader + per-cell state texture) — no
/// canvas on the arm itself.
/// </summary>
[DefaultExecutionOrder(110)]
public class RevisedBoundingBoxController : MonoBehaviour
{
    [Header("Surface")]
    public ForearmDepthSurface surface;

    [Header("Shader — drag ForearmGrid.shader here")]
    public Shader gridShader;

    [Header("Color picker")]
    [Tooltip("Floating hue slider. Auto-found via FindObjectOfType if empty.")]
    public RevisedBoundingBoxColorSlider colorSlider;

    [Header("Grid Size (cell resolution)")]
    [Range(1, 32)] public int columns = 6;
    [Range(1, 32)] public int rows = 6;
    public bool keepSquareCells = true;

    [Header("Colors")]
    public Color defaultColor = new Color(1f, 1f, 1f, 0.15f);
    public Color lineColor     = new Color(1f, 1f, 1f, 0.6f);
    [Range(0f, 0.5f)] public float lineThickness = 0.04f;

    [Header("Touch")]
    public ForearmInteraction interaction;
    [Min(0f)] public float touchReleaseGraceSeconds = 0.15f;

    // ── Runtime state ───────────────────────────────────────────────────────────

    Material  _mat;
    Texture2D _stateTex;
    Texture2D _emptyContentAtlas;
    Texture2D _emptyTransformTex;
    Color32[] _cellColors;
    int       _builtCols = -1;
    int       _builtRows = -1;
    int       _effectiveRows;

    int   _lastToggledCell = -1;
    float _inactiveTimer;

    static readonly int GridColumnsId   = Shader.PropertyToID("_GridColumns");
    static readonly int GridRowsId      = Shader.PropertyToID("_GridRows");
    static readonly int StateTexId      = Shader.PropertyToID("_StateTex");
    static readonly int ContentAtlasId     = Shader.PropertyToID("_ContentAtlas");
    static readonly int TransformTexId     = Shader.PropertyToID("_TransformTex");
    static readonly int MaxContentScaleId  = Shader.PropertyToID("_MaxContentScale");
    static readonly int DefaultColorId  = Shader.PropertyToID("_DefaultColor");
    static readonly int LineColorId     = Shader.PropertyToID("_LineColor");
    static readonly int LineThicknessId  = Shader.PropertyToID("_LineThickness");
    static readonly int EditSelectionCellId = Shader.PropertyToID("_EditSelectionCell");
    static readonly int EditTintColorId     = Shader.PropertyToID("_EditTintColor");
    static readonly int ContentAlphaCutoffId = Shader.PropertyToID("_ContentAlphaCutoff");
    static readonly int ContentAlphaSoftnessId = Shader.PropertyToID("_ContentAlphaSoftness");

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (surface == null)
        {
            Debug.LogError("[RevisedBoundingBox] No ForearmDepthSurface found.");
            return;
        }

        if (interaction == null)
            interaction = surface.GetComponent<ForearmInteraction>();

        if (colorSlider == null)
            colorSlider = FindObjectOfType<RevisedBoundingBoxColorSlider>();

        Shader sh = gridShader != null ? gridShader : Shader.Find("Custom/ForearmGrid");
        if (sh == null)
        {
            Debug.LogError("[RevisedBoundingBox] Shader 'Custom/ForearmGrid' not found.");
            return;
        }

        _mat = new Material(sh) { name = "ForearmGridMat_Instance" };

        _emptyContentAtlas = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
        {
            name = "ForearmGridEmptyContent",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _emptyContentAtlas.SetPixel(0, 0, Color.clear);
        _emptyContentAtlas.Apply(false);
        _mat.SetTexture(ContentAtlasId, _emptyContentAtlas);

        _emptyTransformTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
        {
            name = "ForearmGridEmptyTransform",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _emptyTransformTex.SetPixel(0, 0, Color.clear);
        _emptyTransformTex.Apply(false);
        _mat.SetTexture(TransformTexId, _emptyTransformTex);
        _mat.SetFloat(MaxContentScaleId, 4f);
        _mat.SetVector(EditSelectionCellId, new Vector4(-1f, -1f, 0f, 0f));
        _mat.SetColor(EditTintColorId, new Color(0.2f, 0.95f, 0.35f, 0.35f));
        _mat.SetFloat(ContentAlphaCutoffId, 0.04f);
        _mat.SetFloat(ContentAlphaSoftnessId, 0.14f);

        MeshRenderer mr = surface.GetComponent<MeshRenderer>()
                       ?? surface.GetComponentInChildren<MeshRenderer>();
        if (mr != null) mr.material = _mat;

        RebuildGrid();
        PushMaterialParams();
    }

    void LateUpdate()
    {
        if (_mat == null) return;

        if (columns != _builtCols || EffectiveRows() != _builtRows)
            RebuildGrid();

        PushMaterialParams();
        HandleTouch();
    }

    // ── Grid construction ───────────────────────────────────────────────────────

    int EffectiveRows()
    {
        columns = Mathf.Max(1, columns);
        if (!keepSquareCells)
            return Mathf.Max(1, rows);

        float aspect = 1f;
        if (surface != null && surface.displayWidth > 1e-4f)
            aspect = surface.displayHeight / surface.displayWidth;
        return Mathf.Max(1, Mathf.RoundToInt(columns * aspect));
    }

    void RebuildGrid()
    {
        _effectiveRows = EffectiveRows();
        columns        = Mathf.Max(1, columns);

        int total = columns * _effectiveRows;
        _cellColors = new Color32[total];

        _stateTex = new Texture2D(columns, _effectiveRows, TextureFormat.RGBA32, false, true)
        {
            name       = "ForearmGridState",
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };

        _stateTex.SetPixels32(_cellColors);
        _stateTex.Apply(false);

        if (_mat != null) _mat.SetTexture(StateTexId, _stateTex);

        _builtCols       = columns;
        _builtRows       = _effectiveRows;
        _lastToggledCell = -1;
    }

    void PushMaterialParams()
    {
        _mat.SetFloat(GridColumnsId, columns);
        _mat.SetFloat(GridRowsId,    _effectiveRows);
        _mat.SetColor(DefaultColorId, defaultColor);
        _mat.SetColor(LineColorId,    lineColor);
        _mat.SetFloat(LineThicknessId, lineThickness);
    }

    // ── Touch handling ────────────────────────────────────────────────────────

    void HandleTouch()
    {
        // Don't paint the arm while the user is adjusting the colour slider.
        if (colorSlider != null && colorSlider.IsFingerOnPanel)
        {
            _inactiveTimer += Time.deltaTime;
            if (_inactiveTimer >= touchReleaseGraceSeconds)
                _lastToggledCell = -1;
            return;
        }

        Vector2 uv = interaction != null ? interaction.TouchUV : Vector2.zero;
        bool touching = interaction != null && interaction.IsActive
                        && uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

        if (!touching)
        {
            _inactiveTimer += Time.deltaTime;
            if (_inactiveTimer >= touchReleaseGraceSeconds)
                _lastToggledCell = -1;
            return;
        }

        _inactiveTimer = 0f;

        int col = Mathf.Clamp(Mathf.FloorToInt(uv.x * columns),        0, columns - 1);
        int row = Mathf.Clamp(Mathf.FloorToInt(uv.y * _effectiveRows), 0, _effectiveRows - 1);
        int idx = row * columns + col;

        if (idx == _lastToggledCell) return;

        _lastToggledCell = idx;
        ToggleCell(idx);
    }

    void ToggleCell(int idx)
    {
        if (_cellColors == null || idx < 0 || idx >= _cellColors.Length) return;

        bool painted = _cellColors[idx].a > 127;

        if (painted)
        {
            _cellColors[idx] = new Color32(0, 0, 0, 0);
        }
        else
        {
            Color paint = colorSlider != null
                ? colorSlider.CurrentPaintColor
                : new Color(0.1f, 0.85f, 0.2f, 0.65f);
            _cellColors[idx] = paint;
        }

        int col = idx % columns;
        int row = idx / columns;
        _stateTex.SetPixel(col, row, _cellColors[idx]);
        _stateTex.Apply(false);
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    public void SetColumns(int newColumns)
    {
        columns = Mathf.Max(1, newColumns);
        RebuildGrid();
    }

    public void SetGridSize(int newColumns, int newRows)
    {
        keepSquareCells = false;
        columns = Mathf.Max(1, newColumns);
        rows    = Mathf.Max(1, newRows);
        RebuildGrid();
    }

    public void ClearAll()
    {
        if (_cellColors == null || _stateTex == null) return;
        for (int i = 0; i < _cellColors.Length; i++)
            _cellColors[i] = new Color32(0, 0, 0, 0);
        _stateTex.SetPixels32(_cellColors);
        _stateTex.Apply(false);
        _lastToggledCell = -1;
    }

    public int ActiveCellCount
    {
        get
        {
            if (_cellColors == null) return 0;
            int n = 0;
            for (int i = 0; i < _cellColors.Length; i++)
                if (_cellColors[i].a > 127) n++;
            return n;
        }
    }

    public int Columns => columns;
    public int Rows    => _effectiveRows;

    void OnDestroy()
    {
        if (_stateTex != null) Destroy(_stateTex);
        if (_emptyContentAtlas != null) Destroy(_emptyContentAtlas);
        if (_emptyTransformTex != null) Destroy(_emptyTransformTex);
        if (_mat != null)      Destroy(_mat);
    }
}
