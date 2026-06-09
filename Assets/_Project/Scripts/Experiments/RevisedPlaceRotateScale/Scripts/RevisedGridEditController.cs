using UnityEngine;

/// <summary>
/// Edit-mode orchestration: palette Edit toggle, cell selection, and transform sliders.
/// </summary>
[DefaultExecutionOrder(112)]
public class RevisedGridEditController : MonoBehaviour
{
    [Header("References")]
    public RevisedGridController grid;
    public RevisedGridTransformPanel transformPanel;
    public PossibleUIPaletteController palette;

    public bool IsEditModeActive { get; private set; }

    float _configuredMaxScale = -1f;
    float _configuredDefaultScale = -1f;

    void Awake()
    {
        if (grid == null) grid = FindObjectOfType<RevisedGridController>();
        if (palette == null) palette = FindObjectOfType<PossibleUIPaletteController>();

        if (transformPanel == null)
        {
            transformPanel = FindObjectOfType<RevisedGridTransformPanel>();
            if (transformPanel == null)
                transformPanel = gameObject.AddComponent<RevisedGridTransformPanel>();
        }

        transformPanel.ScaleChanged += OnScaleChanged;
        transformPanel.RotationChanged += OnRotationChanged;

        if (palette != null)
            palette.BindGridEditController(this);

        ConfigureTransformPanelFromGrid();
    }

    void Start()
    {
        if (palette == null)
            palette = FindObjectOfType<PossibleUIPaletteController>();
        if (palette != null && palette.gridEditController != this)
            palette.BindGridEditController(this);

        ConfigureTransformPanelFromGrid();
        UpdatePanelVisibility();
    }

    void LateUpdate()
    {
        if (grid == null) return;
        if (Mathf.Approximately(grid.maxContentScale, _configuredMaxScale) &&
            Mathf.Approximately(grid.defaultPlacedScale, _configuredDefaultScale))
            return;

        ConfigureTransformPanelFromGrid();
    }

    void ConfigureTransformPanelFromGrid()
    {
        if (transformPanel == null || grid == null) return;
        transformPanel.ConfigureScaleRange(0.25f, grid.maxContentScale, grid.defaultPlacedScale);
        _configuredMaxScale = grid.maxContentScale;
        _configuredDefaultScale = grid.defaultPlacedScale;
    }

    public bool IsFingerOnTransformPanel =>
        transformPanel != null && transformPanel.IsFingerOnPanel;

    public void ToggleEditMode()
    {
        IsEditModeActive = !IsEditModeActive;
        if (!IsEditModeActive)
            grid?.ClearSelection();
        UpdatePanelVisibility();
    }

    public bool TrySelectCellFromUV(Vector2 uv)
    {
        if (!IsEditModeActive || grid == null) return false;
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return false;

        grid.UVToCell(uv, out int col, out int row);
        if (!grid.IsCellOccupied(col, row)) return false;
        if (!grid.TrySelectCell(col, row)) return false;

        SyncPanelToSelection();
        return true;
    }

    void OnScaleChanged(float scale)
    {
        if (grid == null || !grid.HasSelectedCell) return;
        grid.SetSelectedCellScale(scale);
    }

    void OnRotationChanged(float degrees)
    {
        if (grid == null || !grid.HasSelectedCell) return;
        grid.SetSelectedCellRotation(degrees);
    }

    void SyncPanelToSelection()
    {
        if (transformPanel == null || grid == null || !grid.HasSelectedCell) return;
        grid.GetSelectedTransform(out float scale, out float rotation);
        transformPanel.SyncValues(scale, rotation);
    }

    void UpdatePanelVisibility()
    {
        if (transformPanel == null) return;
        transformPanel.SetVisible(IsEditModeActive);
        if (IsEditModeActive && grid != null && grid.HasSelectedCell)
            SyncPanelToSelection();
    }

    public void ClearEditState()
    {
        IsEditModeActive = false;
        grid?.ClearSelection();
        UpdatePanelVisibility();
    }
}
