using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the RevisedBoundingBox grid on the forearm depth-surface mesh and provides
/// UV/cell helpers plus a content atlas for baking placed widget images into the shader.
/// </summary>
[DefaultExecutionOrder(100)]
public class RevisedGridController : MonoBehaviour
{
    [Header("Surface")]
    public ForearmDepthSurface surface;

    [Header("Shader — drag ForearmGrid.shader here")]
    public Shader gridShader;

    [Header("Grid Size")]
    [Range(1, 32)] public int columns = 6;
    [Range(1, 32)] public int rows = 6;
    public bool keepSquareCells = true;

    [Header("Content Atlas")]
    [Tooltip("Pixels per grid cell in the baked content atlas. Changing this at runtime rebakes placed images. " +
             "For maximum sharpness after raising this, pick templates from the palette again.")]
    [Min(32)] public int contentTileResolution = 128;
    [Range(0f, 0.4f)] public float cellContentPadding = 0.08f;
    [Tooltip("Pixels below this alpha are treated as fully transparent when baking and drawing.")]
    [Range(0f, 0.25f)] public float contentAlphaCutoff = 0.04f;
    [Tooltip("Feathers content edges between cutoff and cutoff + this value.")]
    [Range(0.02f, 0.3f)] public float contentAlphaSoftness = 0.14f;

    [Header("Placed Image Scale")]
    [Tooltip("Initial scale when a widget is placed on the grid. 1 = one cell; 2 = twice the cell width/height.")]
    [Min(0.25f)] public float defaultPlacedScale = 2f;
    [Tooltip("Maximum scale allowed via the edit sliders and transform texture.")]
    [Min(1f)] public float maxContentScale = 4f;

    [Header("Appearance")]
    public Color defaultColor = new Color(1f, 1f, 1f, 0.15f);
    public Color lineColor     = new Color(1f, 1f, 1f, 0.6f);
    [Range(0f, 0.5f)] public float lineThickness = 0.04f;
    public Color highlightColor = new Color(0.2f, 0.9f, 0.35f, 0.35f);

    [Header("Edit Selection")]
    public Color editTintColor = new Color(0.2f, 0.95f, 0.35f, 0.35f);

    Material _mat;
    int      _effectiveRows;
    int      _builtCols = -1;
    int      _builtRows = -1;
    int      _builtTileResolution = -1;

    struct CellMetadata
    {
        public Color[] SourcePixels;
        public int       SourceWidth;
        public int       SourceHeight;
        public Color     Tint;
        public float     Scale;
        public float     RotationDegrees;
    }

    readonly System.Collections.Generic.Dictionary<int, CellMetadata> _cellMeta =
        new System.Collections.Generic.Dictionary<int, CellMetadata>();

    int _selectedCol = -1;
    int _selectedRow = -1;

    public bool HasSelectedCell => _selectedCol >= 0 && _selectedRow >= 0;

    Texture2D _stateTex;
    Texture2D _contentAtlas;
    Texture2D _transformTex;
    Color32[] _cellStates;
    Color32[] _cellTransforms;

    static readonly int GridColumnsId       = Shader.PropertyToID("_GridColumns");
    static readonly int GridRowsId          = Shader.PropertyToID("_GridRows");
    static readonly int StateTexId          = Shader.PropertyToID("_StateTex");
    static readonly int ContentAtlasId      = Shader.PropertyToID("_ContentAtlas");
    static readonly int TransformTexId      = Shader.PropertyToID("_TransformTex");
    static readonly int MaxContentScaleId   = Shader.PropertyToID("_MaxContentScale");
    static readonly int DefaultColorId   = Shader.PropertyToID("_DefaultColor");
    static readonly int LineColorId      = Shader.PropertyToID("_LineColor");
    static readonly int LineThicknessId  = Shader.PropertyToID("_LineThickness");
    static readonly int HighlightCellId     = Shader.PropertyToID("_HighlightCell");
    static readonly int HighlightColorId    = Shader.PropertyToID("_HighlightColor");
    static readonly int EditSelectionCellId = Shader.PropertyToID("_EditSelectionCell");
    static readonly int EditTintColorId     = Shader.PropertyToID("_EditTintColor");
    static readonly int ContentAlphaCutoffId = Shader.PropertyToID("_ContentAlphaCutoff");
    static readonly int ContentAlphaSoftnessId = Shader.PropertyToID("_ContentAlphaSoftness");

    static Material _spriteBlitMat;

    public int Columns => columns;
    public int Rows    => _effectiveRows;

    void Start()
    {
        if (surface == null)
            surface = FindObjectOfType<ForearmDepthSurface>();

        if (surface == null)
        {
            Debug.LogError("[RevisedGrid] No ForearmDepthSurface found.");
            return;
        }

        Shader sh = gridShader != null ? gridShader : Shader.Find("Custom/ForearmGrid");
        if (sh == null)
        {
            Debug.LogError("[RevisedGrid] Shader 'Custom/ForearmGrid' not found.");
            return;
        }

        _mat = new Material(sh) { name = "RevisedGridMat_Instance" };

        MeshRenderer mr = surface.GetComponent<MeshRenderer>()
                       ?? surface.GetComponentInChildren<MeshRenderer>();
        if (mr != null) mr.material = _mat;

        RebuildGridIfNeeded(force: true);
        PushMaterialParams();
        ClearHighlight();
        ClearEditSelection();
    }

    void LateUpdate()
    {
        if (_mat == null) return;
        RebuildGridIfNeeded();
        PushMaterialParams();
    }

    void RebuildGridIfNeeded(bool force = false)
    {
        _effectiveRows = EffectiveRows();
        contentTileResolution = Mathf.Max(32, contentTileResolution);

        bool gridSizeChanged = force
            || columns != _builtCols
            || _effectiveRows != _builtRows;
        bool tileResChanged = contentTileResolution != _builtTileResolution;

        if (!gridSizeChanged && !tileResChanged)
            return;

        System.Collections.Generic.Dictionary<int, CellMetadata> savedMeta = null;
        if (tileResChanged && !gridSizeChanged && _cellMeta.Count > 0)
            savedMeta = new System.Collections.Generic.Dictionary<int, CellMetadata>(_cellMeta);

        columns = Mathf.Max(1, columns);
        _builtCols = columns;
        _builtRows = _effectiveRows;
        _builtTileResolution = contentTileResolution;

        int total = columns * _effectiveRows;
        _cellStates = new Color32[total];
        _cellTransforms = new Color32[total];

        _stateTex = new Texture2D(columns, _effectiveRows, TextureFormat.RGBA32, false, true)
        {
            name       = "RevisedGridState",
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        _stateTex.SetPixels32(_cellStates);
        _stateTex.Apply(false);

        _transformTex = new Texture2D(columns, _effectiveRows, TextureFormat.RGBA32, false, true)
        {
            name       = "RevisedGridTransform",
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        ClearTransformPixels();
        _transformTex.SetPixels32(_cellTransforms);
        _transformTex.Apply(false);

        int atlasW = columns * contentTileResolution;
        int atlasH = _effectiveRows * contentTileResolution;
        _contentAtlas = new Texture2D(atlasW, atlasH, TextureFormat.RGBA32, false, true)
        {
            name       = "RevisedGridContentAtlas",
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };
        ClearAtlasPixels();
        _contentAtlas.Apply(false);

        if (_mat != null)
        {
            _mat.SetTexture(StateTexId, _stateTex);
            _mat.SetTexture(ContentAtlasId, _contentAtlas);
            _mat.SetTexture(TransformTexId, _transformTex);
            _mat.SetFloat(MaxContentScaleId, maxContentScale);
        }

        if (savedMeta != null)
        {
            _cellMeta.Clear();
            foreach (var kv in savedMeta)
                _cellMeta[kv.Key] = kv.Value;
            RebakeAllOccupiedCells();
        }
        else
        {
            _cellMeta.Clear();
            ClearSelection();
        }
    }

    void RebakeAllOccupiedCells()
    {
        var indices = new System.Collections.Generic.List<int>(_cellMeta.Keys);
        foreach (int idx in indices)
        {
            if (!_cellMeta.TryGetValue(idx, out CellMetadata meta) || meta.SourcePixels == null)
                continue;

            int col = idx % columns;
            int row = idx / columns;
            RebakeCell(col, row);
            PushCellTransform(col, row);
            MarkCellOccupied(col, row);
        }
    }

    int EffectiveRows()
    {
        columns = Mathf.Max(1, columns);
        if (!keepSquareCells) return Mathf.Max(1, rows);

        float aspect = 1f;
        if (surface != null && surface.displayWidth > 1e-4f)
            aspect = surface.displayHeight / surface.displayWidth;
        return Mathf.Max(1, Mathf.RoundToInt(columns * aspect));
    }

    void PushMaterialParams()
    {
        _mat.SetFloat(GridColumnsId, columns);
        _mat.SetFloat(GridRowsId,    _effectiveRows);
        _mat.SetFloat(MaxContentScaleId, maxContentScale);
        _mat.SetColor(DefaultColorId, defaultColor);
        _mat.SetColor(LineColorId,    lineColor);
        _mat.SetFloat(LineThicknessId, lineThickness);
        _mat.SetColor(HighlightColorId, highlightColor);
        _mat.SetColor(EditTintColorId, editTintColor);
        _mat.SetFloat(ContentAlphaCutoffId, contentAlphaCutoff);
        _mat.SetFloat(ContentAlphaSoftnessId, Mathf.Max(contentAlphaSoftness, 0.02f));
    }

    public void UVToCell(Vector2 uv, out int col, out int row)
    {
        col = Mathf.Clamp(Mathf.FloorToInt(uv.x * columns),        0, columns - 1);
        row = Mathf.Clamp(Mathf.FloorToInt(uv.y * _effectiveRows), 0, _effectiveRows - 1);
    }

    public int CellToIndex(int col, int row) => row * columns + col;

    public Vector2 CellCenterUV(int col, int row)
    {
        return new Vector2(
            (col + 0.5f) / columns,
            (row + 0.5f) / _effectiveRows);
    }

    public void SetHighlightCell(int col, int row, bool active)
    {
        if (_mat == null) return;
        _mat.SetVector(HighlightCellId, active
            ? new Vector4(col, row, 1f, 0f)
            : new Vector4(-1f, -1f, 0f, 0f));
    }

    public void SetHighlightFromUV(Vector2 uv, bool active)
    {
        if (!active) { ClearHighlight(); return; }
        UVToCell(uv, out int col, out int row);
        SetHighlightCell(col, row, true);
    }

    public void ClearHighlight() => SetHighlightCell(-1, -1, false);

    public void SetEditSelectionCell(int col, int row, bool active)
    {
        if (_mat == null) return;
        _mat.SetVector(EditSelectionCellId, active
            ? new Vector4(col, row, 1f, 0f)
            : new Vector4(-1f, -1f, 0f, 0f));
    }

    public void ClearEditSelection() => SetEditSelectionCell(-1, -1, false);

    public bool TrySelectCell(int col, int row)
    {
        if (!IsCellOccupied(col, row)) return false;
        _selectedCol = col;
        _selectedRow = row;
        SetEditSelectionCell(col, row, true);
        return true;
    }

    public void ClearSelection()
    {
        _selectedCol = _selectedRow = -1;
        ClearEditSelection();
    }

    public void GetSelectedTransform(out float scale, out float rotationDegrees)
    {
        scale = 1f;
        rotationDegrees = 0f;
        if (!HasSelectedCell) return;
        int idx = CellToIndex(_selectedCol, _selectedRow);
        if (_cellMeta.TryGetValue(idx, out CellMetadata meta))
        {
            scale = meta.Scale;
            rotationDegrees = meta.RotationDegrees;
        }
    }

    public void SetSelectedCellScale(float scale)
    {
        if (!HasSelectedCell) return;
        int idx = CellToIndex(_selectedCol, _selectedRow);
        if (!_cellMeta.TryGetValue(idx, out CellMetadata meta)) return;
        meta.Scale = Mathf.Clamp(scale, 0.25f, maxContentScale);
        _cellMeta[idx] = meta;
        PushCellTransform(_selectedCol, _selectedRow);
    }

    public void SetSelectedCellRotation(float rotationDegrees)
    {
        if (!HasSelectedCell) return;
        int idx = CellToIndex(_selectedCol, _selectedRow);
        if (!_cellMeta.TryGetValue(idx, out CellMetadata meta)) return;
        meta.RotationDegrees = rotationDegrees;
        _cellMeta[idx] = meta;
        PushCellTransform(_selectedCol, _selectedRow);
    }

    /// <summary>
    /// Bakes a UI widget's image into the grid cell so it moves with the mesh UVs.
    /// </summary>
    public bool TryBakeWidgetIntoCell(RectTransform widget, int col, int row)
    {
        if (_contentAtlas == null || _stateTex == null || widget == null) return false;

        var source = widget.GetComponent<RevisedGridCellSource>();
        if (source != null && source.sourcePixels != null && source.SourcePixelWidth > 0 && source.SourcePixelHeight > 0)
        {
            StoreAndBake(col, row, source.sourcePixels, source.SourcePixelWidth, source.SourcePixelHeight,
                source.tint, source.scale, source.rotationDegrees);
            return true;
        }

        if (!TryGetWidgetVisual(widget, out Sprite sprite, out Texture texture, out Color tint))
            return false;

        int tile = contentTileResolution;
        int inner = Mathf.Max(1, Mathf.RoundToInt(tile * (1f - 2f * cellContentPadding)));
        if (sprite != null &&
            sprite.rect.width >= tile - 1f && sprite.rect.height >= tile - 1f)
        {
            inner = tile;
        }

        int srcW = sprite != null ? (int)sprite.rect.width : texture.width;
        int srcH = sprite != null ? (int)sprite.rect.height : texture.height;
        ComputeAspectFitPixelDimensions(srcW, srcH, inner, out int dstW, out int dstH);

        Color[] pixels = sprite != null
            ? ReadSpritePixels(sprite, dstW, dstH)
            : ReadTexturePixels(texture, dstW, dstH);

        StoreAndBake(col, row, pixels, dstW, dstH, tint, defaultPlacedScale, 0f);
        return true;
    }

    void StoreAndBake(int col, int row, Color[] pixels, int width, int height, Color tint, float scale, float rotationDegrees)
    {
        int idx = CellToIndex(col, row);
        _cellMeta[idx] = new CellMetadata
        {
            SourcePixels = pixels,
            SourceWidth = width,
            SourceHeight = height,
            Tint = tint,
            Scale = scale,
            RotationDegrees = rotationDegrees
        };
        RebakeCell(col, row);
        PushCellTransform(col, row);
        MarkCellOccupied(col, row);
    }

    void PushCellTransform(int col, int row)
    {
        if (_transformTex == null) return;

        int idx = CellToIndex(col, row);
        if (!_cellMeta.TryGetValue(idx, out CellMetadata meta))
        {
            _cellTransforms[idx] = new Color32(0, 0, 0, 0);
        }
        else
        {
            float rot = Mathf.Repeat(meta.RotationDegrees, 360f);
            _cellTransforms[idx] = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(meta.Scale / maxContentScale * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(rot / 360f * 255f), 0, 255),
                0,
                255);
        }

        _transformTex.SetPixel(col, row, _cellTransforms[idx]);
        _transformTex.Apply(false);
    }

    void MarkCellOccupied(int col, int row)
    {
        int idx = CellToIndex(col, row);
        _cellStates[idx] = new Color32(0, 0, 0, 255);
        _stateTex.SetPixel(col, row, _cellStates[idx]);
        _stateTex.Apply(false);
    }

    void RebakeCell(int col, int row)
    {
        int idx = CellToIndex(col, row);
        if (!_cellMeta.TryGetValue(idx, out CellMetadata meta) || meta.SourcePixels == null) return;

        ClearTile(col, row);

        int tile = contentTileResolution;
        int destX = col * tile;
        int destY = row * tile;
        int srcW = Mathf.Max(1, meta.SourceWidth);
        int srcH = Mathf.Max(1, meta.SourceHeight);
        var dest = new Color[tile * tile];

        float fit = Mathf.Min((float)tile / srcW, (float)tile / srcH);
        int drawnW = Mathf.Max(1, Mathf.RoundToInt(srcW * fit));
        int drawnH = Mathf.Max(1, Mathf.RoundToInt(srcH * fit));
        int offsetX = (tile - drawnW) / 2;
        int offsetY = (tile - drawnH) / 2;

        for (int y = 0; y < tile; y++)
        {
            for (int x = 0; x < tile; x++)
            {
                int localX = x - offsetX;
                int localY = y - offsetY;
                if (localX < 0 || localY < 0 || localX >= drawnW || localY >= drawnH)
                {
                    dest[y * tile + x] = Color.clear;
                    continue;
                }

                float u = (localX + 0.5f) / drawnW;
                float v = (localY + 0.5f) / drawnH;
                dest[y * tile + x] = HardenContentPixel(
                    SampleSourceBilinear(meta.SourcePixels, srcW, srcH, u, v) * meta.Tint);
            }
        }

        _contentAtlas.SetPixels(destX, destY, tile, tile, dest);
        _contentAtlas.Apply(false);
    }

    Color HardenContentPixel(Color c)
    {
        c.a = SoftContentAlpha(c.a);
        if (c.a <= 0f)
            return Color.clear;
        return c;
    }

    float SoftContentAlpha(float a)
    {
        if (a <= contentAlphaCutoff)
            return 0f;

        float softness = Mathf.Max(contentAlphaSoftness, 0.02f);
        float edge1 = contentAlphaCutoff + softness;
        if (a >= edge1)
            return a;

        float t = Mathf.SmoothStep(contentAlphaCutoff, edge1, a);
        return a * t;
    }

    Color[] HardenPixelArray(Color[] pixels)
    {
        if (pixels == null) return pixels;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = HardenContentPixel(pixels[i]);
        return pixels;
    }

    static Color SampleSourceBilinear(Color[] pixels, int w, int h, float u, float v)
    {
        if (u < 0f || u > 1f || v < 0f || v > 1f) return Color.clear;

        float fx = u * (w - 1);
        float fy = v * (h - 1);
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        int x1 = Mathf.Min(x0 + 1, w - 1);
        int y1 = Mathf.Min(y0 + 1, h - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        Color c00 = pixels[y0 * w + x0];
        Color c10 = pixels[y0 * w + x1];
        Color c01 = pixels[y1 * w + x0];
        Color c11 = pixels[y1 * w + x1];
        Color cx0 = Color.Lerp(c00, c10, tx);
        Color cx1 = Color.Lerp(c01, c11, tx);
        return Color.Lerp(cx0, cx1, ty);
    }

    public void ClearCell(int col, int row)
    {
        if (_stateTex == null || _contentAtlas == null) return;

        ClearTile(col, row);

        int idx = CellToIndex(col, row);
        _cellStates[idx] = new Color32(0, 0, 0, 0);
        _stateTex.SetPixel(col, row, _cellStates[idx]);
        _stateTex.Apply(false);
        _cellTransforms[idx] = new Color32(0, 0, 0, 0);
        if (_transformTex != null)
        {
            _transformTex.SetPixel(col, row, _cellTransforms[idx]);
            _transformTex.Apply(false);
        }
        _cellMeta.Remove(idx);

        if (_selectedCol == col && _selectedRow == row)
            ClearSelection();
    }

    public bool IsCellOccupied(int col, int row)
    {
        int idx = CellToIndex(col, row);
        return _cellStates != null && idx >= 0 && idx < _cellStates.Length && _cellStates[idx].a > 127;
    }

    public void ClearAll()
    {
        if (_cellStates == null || _stateTex == null || _contentAtlas == null) return;

        for (int i = 0; i < _cellStates.Length; i++)
        {
            _cellStates[i] = new Color32(0, 0, 0, 0);
            _cellTransforms[i] = new Color32(0, 0, 0, 0);
        }

        _stateTex.SetPixels32(_cellStates);
        _stateTex.Apply(false);
        if (_transformTex != null)
        {
            _transformTex.SetPixels32(_cellTransforms);
            _transformTex.Apply(false);
        }
        ClearAtlasPixels();
        _contentAtlas.Apply(false);
        _cellMeta.Clear();
        ClearSelection();
    }

    /// <summary>
    /// Builds a temporary UI widget from a baked cell and clears that cell from the atlas.
    /// </summary>
    public bool TryCreateWidgetFromCell(int col, int row, out RectTransform widget)
    {
        widget = null;
        if (_contentAtlas == null || !IsCellOccupied(col, row)) return false;

        int tile = contentTileResolution;
        int srcX = col * tile;
        int srcY = row * tile;

        var pixels = _contentAtlas.GetPixels(srcX, srcY, tile, tile);
        bool hasVisiblePixel = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].a > 0.01f) { hasVisiblePixel = true; break; }
        }
        if (!hasVisiblePixel) return false;

        int idx = CellToIndex(col, row);
        _cellMeta.TryGetValue(idx, out CellMetadata meta);

        var tex = new Texture2D(tile, tile, TextureFormat.RGBA32, false, true)
        {
            name = $"CellContent_{col}_{row}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixels(pixels);
        tex.Apply(false);

        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tile, tile), new Vector2(0.5f, 0.5f), tile);

        int metaW = meta.SourceWidth > 0 ? meta.SourceWidth : tile;
        int metaH = meta.SourceHeight > 0 ? meta.SourceHeight : tile;
        ComputeAspectFitPixelDimensions(metaW, metaH, 100, out int displayW, out int displayH);

        var go = new GameObject($"CellContent_{col}_{row}",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        widget = go.GetComponent<RectTransform>();
        widget.sizeDelta = new Vector2(displayW, displayH);

        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;

        if (meta.SourcePixels != null && meta.SourceWidth > 0 && meta.SourceHeight > 0)
        {
            var src = go.AddComponent<RevisedGridCellSource>();
            src.sourcePixels = (Color[])meta.SourcePixels.Clone();
            src.sourceWidth = meta.SourceWidth;
            src.sourceHeight = meta.SourceHeight;
            src.tint = meta.Tint;
            src.scale = meta.Scale;
            src.rotationDegrees = meta.RotationDegrees;
        }

        ClearCell(col, row);
        return true;
    }

    public bool TryCreateWidgetFromUV(Vector2 uv, out RectTransform widget)
    {
        widget = null;
        UVToCell(uv, out int col, out int row);
        return TryCreateWidgetFromCell(col, row, out widget);
    }

    /// <summary>
    /// Returns world position and outward-facing normal on the live surface mesh
    /// nearest the given display UV.
    /// </summary>
    public bool TrySampleSurfaceAtUV(Vector2 uv, out Vector3 worldPos, out Vector3 worldNormal)
    {
        worldPos = Vector3.zero;
        worldNormal = surface != null ? surface.AxisUp : Vector3.up;

        if (surface == null || !surface.IsValid) return false;

        Mesh mesh = surface.SurfaceMesh;
        if (mesh == null) return false;

        Vector3[] verts = mesh.vertices;
        Vector2[] uvs   = mesh.uv;
        int[] tris      = mesh.triangles;
        if (verts == null || uvs == null || tris == null || tris.Length < 3)
            return false;

        Transform t = surface.transform;

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            Vector2 u0 = uvs[i0], u1 = uvs[i1], u2 = uvs[i2];

            if (!PointInTriangleUV(uv, u0, u1, u2, out Vector3 bary))
                continue;

            Vector3 local = verts[i0] * bary.x + verts[i1] * bary.y + verts[i2] * bary.z;
            worldPos = t.TransformPoint(local);

            Vector3 w0 = t.TransformPoint(verts[i0]);
            Vector3 w1 = t.TransformPoint(verts[i1]);
            Vector3 w2 = t.TransformPoint(verts[i2]);
            worldNormal = Vector3.Cross(w1 - w0, w2 - w0).normalized;

            return true;
        }

        float bestDistSq = float.MaxValue;
        bool  found      = false;

        for (int i = 0; i < uvs.Length; i++)
        {
            float dSq = (uvs[i] - uv).sqrMagnitude;
            if (dSq >= bestDistSq) continue;
            bestDistSq = dSq;
            worldPos = t.TransformPoint(verts[i]);
            found = true;
        }

        return found;
    }

    static bool TryGetWidgetVisual(RectTransform widget, out Sprite sprite, out Texture texture, out Color tint)
    {
        sprite = null;
        texture = null;
        tint = Color.white;

        var image = FindPrimaryWidgetImage(widget);
        if (image != null && image.sprite != null)
        {
            sprite = image.sprite;
            tint = image.color;
            return true;
        }

        var raw = widget.GetComponentInChildren<RawImage>(true);
        if (raw != null && raw.texture != null)
        {
            texture = raw.texture;
            tint = raw.color;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prefers a child icon Image over a solid background Image on the template root.
    /// </summary>
    static Image FindPrimaryWidgetImage(RectTransform widget)
    {
        if (widget == null) return null;

        Image rootImage = null;
        Image bestChild = null;
        float bestChildArea = 0f;

        foreach (var img in widget.GetComponentsInChildren<Image>(true))
        {
            if (img.sprite == null) continue;

            if (img.transform == widget)
            {
                rootImage = img;
                continue;
            }

            var rt = img.rectTransform;
            float area = Mathf.Abs(rt.rect.width * rt.rect.height);
            if (bestChild == null || area > bestChildArea)
            {
                bestChild = img;
                bestChildArea = area;
            }
        }

        return bestChild != null ? bestChild : rootImage;
    }

    static Color[] ReadSpritePixels(Sprite sprite, int width, int height)
    {
        var rect = sprite.textureRect;
        var rt = RenderTexture.GetTemporary((int)rect.width, (int)rect.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

        var prev = RenderTexture.active;
        Graphics.Blit(sprite.texture, rt, SpriteBlitMaterial(sprite));

        RenderTexture.active = rt;
        var extracted = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false, true);
        extracted.ReadPixels(new Rect(0f, 0f, rect.width, rect.height), 0, 0);
        extracted.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var scaled = ScalePixels(extracted.GetPixels(), (int)rect.width, (int)rect.height, width, height);
        Destroy(extracted);
        return scaled;
    }

    static Color[] ReadTexturePixels(Texture texture, int width, int height)
    {
        var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

        var prev = RenderTexture.active;
        Graphics.Blit(texture, rt);

        RenderTexture.active = rt;
        var extracted = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
        extracted.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
        extracted.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var scaled = ScalePixels(extracted.GetPixels(), texture.width, texture.height, width, height);
        Destroy(extracted);
        return scaled;
    }

    static Material SpriteBlitMaterial(Sprite sprite)
    {
        if (_spriteBlitMat == null)
        {
            var sh = Shader.Find("Sprites/Default")
                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Transparent");
            _spriteBlitMat = new Material(sh);
        }

        var tex = sprite.texture;
        var rect = sprite.textureRect;
        _spriteBlitMat.mainTexture = tex;
        _spriteBlitMat.mainTextureScale = new Vector2(rect.width / tex.width, rect.height / tex.height);
        _spriteBlitMat.mainTextureOffset = new Vector2(rect.x / tex.width, rect.y / tex.height);
        _spriteBlitMat.color = Color.white;
        return _spriteBlitMat;
    }

    static void ComputeAspectFitPixelDimensions(int srcW, int srcH, int maxDim, out int dstW, out int dstH)
    {
        srcW = Mathf.Max(1, srcW);
        srcH = Mathf.Max(1, srcH);
        maxDim = Mathf.Max(1, maxDim);

        float aspect = (float)srcW / srcH;
        if (aspect >= 1f)
        {
            dstW = maxDim;
            dstH = Mathf.Max(1, Mathf.RoundToInt(maxDim / aspect));
        }
        else
        {
            dstH = maxDim;
            dstW = Mathf.Max(1, Mathf.RoundToInt(maxDim * aspect));
        }
    }

    static Color[] ScalePixels(Color[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new Color[dstW * dstH];
        for (int y = 0; y < dstH; y++)
        {
            float v = dstH <= 1 ? 0f : y / (float)(dstH - 1);
            int sy = Mathf.Clamp(Mathf.RoundToInt(v * (srcH - 1)), 0, srcH - 1);
            for (int x = 0; x < dstW; x++)
            {
                float u = dstW <= 1 ? 0f : x / (float)(dstW - 1);
                int sx = Mathf.Clamp(Mathf.RoundToInt(u * (srcW - 1)), 0, srcW - 1);
                dst[y * dstW + x] = src[sy * srcW + sx];
            }
        }
        return dst;
    }

    void ClearTransformPixels()
    {
        if (_cellTransforms == null) return;
        for (int i = 0; i < _cellTransforms.Length; i++)
            _cellTransforms[i] = new Color32(0, 0, 0, 0);
    }

    void ClearAtlasPixels()
    {
        var clear = new Color32(0, 0, 0, 0);
        var pixels = new Color32[_contentAtlas.width * _contentAtlas.height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;
        _contentAtlas.SetPixels32(pixels);
    }

    void ClearTile(int col, int row)
    {
        int tile = contentTileResolution;
        int destX = col * tile;
        int destY = row * tile;
        var clear = new Color32(0, 0, 0, 0);

        for (int y = 0; y < tile; y++)
        {
            for (int x = 0; x < tile; x++)
                _contentAtlas.SetPixel(destX + x, destY + y, clear);
        }

        _contentAtlas.Apply(false);
    }

    static bool PointInTriangleUV(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 bary)
    {
        bary = Vector3.zero;
        float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
        if (Mathf.Abs(denom) < 1e-8f) return false;

        float w0 = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / denom;
        float w1 = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / denom;
        float w2 = 1f - w0 - w1;

        if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f) return false;

        bary = new Vector3(w0, w1, w2);
        return true;
    }

    void OnValidate()
    {
        defaultPlacedScale = Mathf.Max(0.25f, defaultPlacedScale);
        maxContentScale = Mathf.Max(1f, maxContentScale);
        if (defaultPlacedScale > maxContentScale)
            defaultPlacedScale = maxContentScale;
    }

    void OnDestroy()
    {
        if (_stateTex != null) Destroy(_stateTex);
        if (_contentAtlas != null) Destroy(_contentAtlas);
        if (_transformTex != null) Destroy(_transformTex);
        if (_mat != null) Destroy(_mat);
    }
}
