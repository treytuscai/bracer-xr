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
    [Tooltip("Pixels per grid cell in the baked content atlas.")]
    [Min(32)] public int contentTileResolution = 128;
    [Range(0f, 0.4f)] public float cellContentPadding = 0.08f;

    [Header("Appearance")]
    public Color defaultColor = new Color(1f, 1f, 1f, 0.15f);
    public Color lineColor     = new Color(1f, 1f, 1f, 0.6f);
    [Range(0f, 0.5f)] public float lineThickness = 0.04f;
    public Color highlightColor = new Color(0.2f, 0.9f, 0.35f, 0.35f);

    Material _mat;
    int      _effectiveRows;
    int      _builtCols = -1;
    int      _builtRows = -1;

    Texture2D _stateTex;
    Texture2D _contentAtlas;
    Color32[] _cellStates;

    static readonly int GridColumnsId    = Shader.PropertyToID("_GridColumns");
    static readonly int GridRowsId       = Shader.PropertyToID("_GridRows");
    static readonly int StateTexId       = Shader.PropertyToID("_StateTex");
    static readonly int ContentAtlasId   = Shader.PropertyToID("_ContentAtlas");
    static readonly int DefaultColorId   = Shader.PropertyToID("_DefaultColor");
    static readonly int LineColorId      = Shader.PropertyToID("_LineColor");
    static readonly int LineThicknessId  = Shader.PropertyToID("_LineThickness");
    static readonly int HighlightCellId  = Shader.PropertyToID("_HighlightCell");
    static readonly int HighlightColorId = Shader.PropertyToID("_HighlightColor");

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
        if (!force && columns == _builtCols && _effectiveRows == _builtRows) return;

        columns = Mathf.Max(1, columns);
        _builtCols = columns;
        _builtRows = _effectiveRows;

        int total = columns * _effectiveRows;
        _cellStates = new Color32[total];

        _stateTex = new Texture2D(columns, _effectiveRows, TextureFormat.RGBA32, false, true)
        {
            name       = "RevisedGridState",
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        _stateTex.SetPixels32(_cellStates);
        _stateTex.Apply(false);

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
        _mat.SetColor(DefaultColorId, defaultColor);
        _mat.SetColor(LineColorId,    lineColor);
        _mat.SetFloat(LineThicknessId, lineThickness);
        _mat.SetColor(HighlightColorId, highlightColor);
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

    /// <summary>
    /// Bakes a UI widget's image into the grid cell so it moves with the mesh UVs.
    /// </summary>
    public bool TryBakeWidgetIntoCell(RectTransform widget, int col, int row)
    {
        if (_contentAtlas == null || _stateTex == null || widget == null) return false;

        if (!TryGetWidgetVisual(widget, out Sprite sprite, out Texture texture, out Color tint))
            return false;

        if (sprite != null)
            BakeSpriteIntoCell(col, row, sprite, tint);
        else
            BakeTextureIntoCell(col, row, texture, tint);

        int idx = CellToIndex(col, row);
        _cellStates[idx] = new Color32(255, 255, 255, 255);
        _stateTex.SetPixel(col, row, _cellStates[idx]);
        _stateTex.Apply(false);
        return true;
    }

    public void ClearCell(int col, int row)
    {
        if (_stateTex == null || _contentAtlas == null) return;

        ClearTile(col, row);

        int idx = CellToIndex(col, row);
        _cellStates[idx] = new Color32(0, 0, 0, 0);
        _stateTex.SetPixel(col, row, _cellStates[idx]);
        _stateTex.Apply(false);
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
            _cellStates[i] = new Color32(0, 0, 0, 0);

        _stateTex.SetPixels32(_cellStates);
        _stateTex.Apply(false);
        ClearAtlasPixels();
        _contentAtlas.Apply(false);
        ClearHighlight();
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

        var tex = new Texture2D(tile, tile, TextureFormat.RGBA32, false, true)
        {
            name = $"CellContent_{col}_{row}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixels(pixels);
        tex.Apply(false);

        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tile, tile), new Vector2(0.5f, 0.5f), tile);

        var go = new GameObject($"CellContent_{col}_{row}",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        widget = go.GetComponent<RectTransform>();
        widget.sizeDelta = new Vector2(100f, 100f);

        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;

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

    void BakeSpriteIntoCell(int col, int row, Sprite sprite, Color tint)
    {
        ClearTile(col, row);

        int tile = contentTileResolution;

        // Cell-extracted sprites are already tile-sized; skip padding inset to avoid
        // shrinking the image on each pick-up → place cycle.
        bool alreadyCellSized =
            sprite.rect.width >= tile - 1f && sprite.rect.height >= tile - 1f;

        int inner = alreadyCellSized
            ? tile
            : Mathf.Max(1, Mathf.RoundToInt(tile * (1f - 2f * cellContentPadding)));
        int offset = alreadyCellSized ? 0 : (tile - inner) / 2;

        var srcPixels = ReadSpritePixels(sprite, inner, inner);
        int destX = col * tile;
        int destY = row * tile;

        for (int y = 0; y < inner; y++)
        {
            for (int x = 0; x < inner; x++)
            {
                Color c = srcPixels[y * inner + x] * tint;
                _contentAtlas.SetPixel(destX + offset + x, destY + offset + y, c);
            }
        }

        _contentAtlas.Apply(false);
    }

    void BakeTextureIntoCell(int col, int row, Texture texture, Color tint)
    {
        ClearTile(col, row);

        int tile = contentTileResolution;
        int inner = Mathf.Max(1, Mathf.RoundToInt(tile * (1f - 2f * cellContentPadding)));
        int offset = (tile - inner) / 2;

        var srcPixels = ReadTexturePixels(texture, inner, inner);
        int destX = col * tile;
        int destY = row * tile;

        for (int y = 0; y < inner; y++)
        {
            for (int x = 0; x < inner; x++)
            {
                Color c = srcPixels[y * inner + x] * tint;
                _contentAtlas.SetPixel(destX + offset + x, destY + offset + y, c);
            }
        }

        _contentAtlas.Apply(false);
    }

    static bool TryGetWidgetVisual(RectTransform widget, out Sprite sprite, out Texture texture, out Color tint)
    {
        sprite = null;
        texture = null;
        tint = Color.white;

        var image = widget.GetComponentInChildren<Image>(true);
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

    void OnDestroy()
    {
        if (_stateTex != null) Destroy(_stateTex);
        if (_contentAtlas != null) Destroy(_contentAtlas);
        if (_mat != null) Destroy(_mat);
    }
}
