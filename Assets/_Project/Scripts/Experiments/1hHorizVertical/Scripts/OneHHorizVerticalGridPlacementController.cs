using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Experiments.Cli;

/// <summary>
/// 1H-only widget placement — uses <see cref="OneHHorizVerticalGridController"/> for aspect-preserving bakes.
/// </summary>
[DefaultExecutionOrder(105)]
public class OneHHorizVerticalGridPlacementController : MonoBehaviour, IForearmWidgetPlacement, IExperimentCommands {
    /// <summary>
    /// CLI hook: "clear" erases all placed widgets from the grid (e.g. to reset a participant's
    /// arm between trials). Declared here so the command lives with the logic it drives.
    /// </summary>
    public void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
    {
        commands["clear"] = _ =>
        {
            ClearAll();
            return "cleared placed widgets";
        };
        PlacementCliCommands.Register(commands);
    }
    [Header("References")]
    public OneHHorizVerticalGridController grid;
    public ForearmDepthSurface surface;
    public ForearmInteraction interaction;

    [Header("Carry")]
    [Min(0.005f)] public float carriedWorldWidthMeters = 0.04f;
    [Tooltip("When enabled, the carried preview scales with OneHHorizVerticalGridController.defaultPlacedScale.")]
    public bool scaleCarryPreviewWithPlacedScale = true;
    [Min(0.25f)] public float carryPreviewScaleMultiplier = 1f;
    [Min(0f)] public float fingerCarrySmoothTime = 0.048f;
    public Vector3 carryAttachOffsetTipLocal = Vector3.zero;
    public bool stickWidgetOriginToFingertip = true;

    [Header("Placement Preview")]
    [Tooltip("Shows a ghost of the carried image on the arm when the fingertip is near the surface.")]
    public bool showPlacementPreview = true;
    [Tooltip("Hide the image on the fingertip while the arm ghost preview is visible.")]
    public bool hideFingerCarryWhenPreviewShown = true;
    [Tooltip("Uses ForearmInteraction.maxHoverPreviewDistance when > 0, otherwise this value (meters).")]
    [Min(0f)] public float placementPreviewMaxDistanceMeters = 0.1f;
    [Range(0.05f, 1f)] public float placementPreviewAlpha = 0.45f;

    RectTransform _draggedItem;
    Transform     _carrySavedParent;
    Vector3       _carrySavedLocalScale;
    bool          _destroyCarriedOnAbort;

    Vector3 _holdOffsetLocalInTipSpace;
    Quaternion _carryPickupWorldRotation;
    Vector3 _tipFilteredPos;
    Quaternion _tipFilteredRot;
    Vector3 _tipPosSmoothVel;
    Transform _activeIndexTip;

    public bool IsCarrying => _draggedItem != null;

    void Awake()
    {
        if (FindObjectOfType<RevisedGridEditController>() == null)
            gameObject.AddComponent<RevisedGridEditController>();
    }

    void Start()
    {
        if (grid == null) grid = FindObjectOfType<OneHHorizVerticalGridController>();
        if (surface == null) surface = FindObjectOfType<ForearmDepthSurface>();
        if (interaction == null && surface != null)
            interaction = surface.GetComponent<ForearmInteraction>();
    }

    void LateUpdate()
    {
        if (IsCarrying)
        {
            UpdatePlacementPreview();

            if (interaction != null && interaction.IsActive)
                grid?.SetHighlightFromUV(interaction.TouchUV, true);
            else if (grid != null && _activeIndexTip != null &&
                     interaction != null &&
                     interaction.TryGetNearestSurfaceFromPoint(
                         _activeIndexTip.position,
                         ResolvePreviewMaxDistance(),
                         out Vector2 hoverUv,
                         out _))
            {
                grid.SetHighlightFromUV(hoverUv, true);
            }
        }
        else
        {
            grid?.ClearCarryPreviewSource();
            grid?.ClearHighlight();
        }
    }

    float ResolvePreviewMaxDistance()
    {
        if (interaction != null && interaction.maxHoverPreviewDistance > 0f)
            return interaction.maxHoverPreviewDistance;
        return placementPreviewMaxDistanceMeters;
    }

    void UpdatePlacementPreview()
    {
        bool ghostVisible = false;

        if (showPlacementPreview && grid != null && interaction != null && _activeIndexTip != null &&
            interaction.TryGetNearestSurfaceFromPoint(
                _activeIndexTip.position,
                ResolvePreviewMaxDistance(),
                out Vector2 uv,
                out _))
        {
            grid.UVToCell(uv, out int col, out int row);
            grid.ShowPlacementPreviewAtCell(col, row, placementPreviewAlpha);
            ghostVisible = true;
        }
        else
        {
            grid?.ClearPlacementPreview();
        }

        if (hideFingerCarryWhenPreviewShown && showPlacementPreview)
            SetCarriedWidgetVisible(!ghostVisible);
        else
            SetCarriedWidgetVisible(true);
    }

    void SetCarriedWidgetVisible(bool visible)
    {
        if (_draggedItem == null || _draggedItem.gameObject.activeSelf == visible)
            return;

        _draggedItem.gameObject.SetActive(visible);
    }

    public bool BeginCarryExternal(RectTransform widget, Transform indexTipWorld, bool destroyOnAbort = true)
    {
        if (widget == null || indexTipWorld == null) return false;

        _draggedItem = widget;
        _destroyCarriedOnAbort = destroyOnAbort;
        _carrySavedParent = widget.parent;
        _carrySavedLocalScale = widget.localScale;

        widget.SetParent(WidgetCarryCanvas.Root, worldPositionStays: false);
        widget.localRotation = Quaternion.identity;
        widget.localScale = Vector3.one;

        float canvasScale = Mathf.Max(Mathf.Abs(WidgetCarryCanvas.Root.lossyScale.x), 1e-6f);
        float baseWorldWidth = Mathf.Max(widget.rect.width * canvasScale, 1e-6f);
        float carryScale = ResolveCarryPreviewScale();
        float uniform = carriedWorldWidthMeters * carryScale / baseWorldWidth;
        widget.localScale = new Vector3(uniform, uniform, uniform);

        _carrySavedLocalScale = widget.localScale;

        Quaternion tipRot = indexTipWorld.rotation;
        _holdOffsetLocalInTipSpace = stickWidgetOriginToFingertip
            ? carryAttachOffsetTipLocal
            : Quaternion.Inverse(tipRot) * (widget.position - indexTipWorld.position);

        _carryPickupWorldRotation = widget.rotation;
        _tipFilteredPos = indexTipWorld.position;
        _tipFilteredRot = indexTipWorld.rotation;
        _tipPosSmoothVel = Vector3.zero;

        TickCarryFollowFinger(indexTipWorld);
        SetCarriedWidgetVisible(true);
        grid?.TryCacheCarryPreviewSource(_draggedItem, out _, out _);
        grid?.ClearHighlight();
        return true;
    }

    public void TickCarryFollowFinger(Transform indexTipWorld)
    {
        if (_draggedItem == null || indexTipWorld == null) return;

        _activeIndexTip = indexTipWorld;

        float dt = Time.deltaTime;
        if (fingerCarrySmoothTime <= Mathf.Epsilon)
        {
            _tipFilteredPos = indexTipWorld.position;
            _tipFilteredRot = indexTipWorld.rotation;
        }
        else
        {
            _tipFilteredPos = Vector3.SmoothDamp(
                _tipFilteredPos, indexTipWorld.position, ref _tipPosSmoothVel,
                fingerCarrySmoothTime, Mathf.Infinity, dt);

            float rotT = 1f - Mathf.Exp(-dt / Mathf.Max(fingerCarrySmoothTime * 1.25f, 1e-4f));
            _tipFilteredRot = Quaternion.Slerp(_tipFilteredRot, indexTipWorld.rotation, rotT);
        }

        Vector3 worldPos = _tipFilteredPos + _tipFilteredRot * _holdOffsetLocalInTipSpace;
        _draggedItem.SetPositionAndRotation(worldPos, _carryPickupWorldRotation);
    }

    float ResolveCarryPreviewScale()
    {
        float scale = carryPreviewScaleMultiplier;
        if (scaleCarryPreviewWithPlacedScale && grid != null)
            scale *= grid.defaultPlacedScale;
        return Mathf.Max(0.25f, scale);
    }

    public void CommitPlace(Vector3 contactWorldPoint)
    {
        if (_draggedItem == null || grid == null) return;

        Vector2 uv;
        if (interaction != null && interaction.IsActive)
            uv = interaction.TouchUV;
        else if (!TryGetUVNearWorldPoint(contactWorldPoint, out uv))
            return;

        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return;

        grid.UVToCell(uv, out int col, out int row);

        if (grid.IsCellOccupied(col, row))
            grid.ClearCell(col, row);

        // Baking reads UI images; the carry preview may have hidden the widget on the fingertip.
        _draggedItem.gameObject.SetActive(true);

        if (!grid.TryBakeWidgetIntoCell(_draggedItem, col, row))
        {
            Debug.LogWarning($"[RevisedGridPlacement] Could not bake '{_draggedItem.name}' into cell ({col},{row}).");
            return;
        }

        Destroy(_draggedItem.gameObject);
        _draggedItem = null;
        _activeIndexTip = null;
        grid?.ClearCarryPreviewSource();
        grid.ClearHighlight();
    }

    public void DestroyCarriedItem()
    {
        if (_draggedItem == null) return;

        Destroy(_draggedItem.gameObject);
        _draggedItem = null;
        _activeIndexTip = null;
        grid?.ClearCarryPreviewSource();
        grid?.ClearHighlight();
    }

    public void ClearAll()
    {
        if (IsCarrying)
            DestroyCarriedItem();

        grid?.ClearAll();
        FindObjectOfType<RevisedGridEditController>()?.ClearEditState();
    }

    public bool TryBeginCarryFromSurface(Vector2 surfaceUV, Transform indexTipWorld)
    {
        if (IsCarrying || grid == null || indexTipWorld == null) return false;
        if (surfaceUV.x < 0f || surfaceUV.x > 1f || surfaceUV.y < 0f || surfaceUV.y > 1f) return false;

        if (!grid.TryFindOccupiedCellAtUV(surfaceUV, out int col, out int row))
            return false;
        if (!grid.TryCreateWidgetFromCell(col, row, out RectTransform widget)) return false;

        return BeginCarryExternal(widget, indexTipWorld, destroyOnAbort: true);
    }

    bool TryGetUVNearWorldPoint(Vector3 world, out Vector2 uv)
    {
        uv = Vector2.zero;
        if (surface == null || !surface.IsValid) return false;

        Mesh mesh = surface.SurfaceMesh;
        if (mesh == null) return false;

        Vector3[] verts = mesh.vertices;
        Vector2[] uvs   = mesh.uv;
        Transform t     = surface.transform;

        float bestDistSq = float.MaxValue;
        int   bestIdx    = -1;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = t.TransformPoint(verts[i]);
            float dSq = (w - world).sqrMagnitude;
            if (dSq >= bestDistSq) continue;
            bestDistSq = dSq;
            bestIdx = i;
        }

        if (bestIdx < 0) return false;
        uv = uvs[bestIdx];
        return true;
    }
}
