using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Experiments.Cli;

/// <summary>
/// Places palette widgets into grid cells on the forearm depth-surface mesh by baking
/// their image into the ForearmGrid shader content atlas (UV-locked, no floating canvas).
/// Implements <see cref="IForearmWidgetPlacement"/> for <see cref="PossibleUIPaletteController"/>.
/// /// Also exposes a "clear" verb to the ExperimentCommandServer CLI via <see cref="IExperimentCommands"/>.
/// </summary>
[DefaultExecutionOrder(105)]
public class RevisedGridPlacementController : MonoBehaviour, IForearmWidgetPlacement, IExperimentCommands {
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
    }
    [Header("References")]
    public RevisedGridController grid;
    public ForearmDepthSurface surface;
    public ForearmInteraction interaction;

    [Header("Carry")]
    [Min(0.005f)] public float carriedWorldWidthMeters = 0.04f;
    [Tooltip("When enabled, the carried preview scales with RevisedGridController.defaultPlacedScale.")]
    public bool scaleCarryPreviewWithPlacedScale = true;
    [Min(0.25f)] public float carryPreviewScaleMultiplier = 1f;
    [Min(0f)] public float fingerCarrySmoothTime = 0.048f;
    public Vector3 carryAttachOffsetTipLocal = Vector3.zero;
    public bool stickWidgetOriginToFingertip = true;

    RectTransform _draggedItem;
    Transform     _carrySavedParent;
    Vector3       _carrySavedLocalScale;
    bool          _destroyCarriedOnAbort;

    Vector3 _holdOffsetLocalInTipSpace;
    Quaternion _carryPickupWorldRotation;
    Vector3 _tipFilteredPos;
    Quaternion _tipFilteredRot;
    Vector3 _tipPosSmoothVel;

    public bool IsCarrying => _draggedItem != null;

    void Awake()
    {
        if (FindObjectOfType<RevisedGridEditController>() == null)
            gameObject.AddComponent<RevisedGridEditController>();
    }

    void Start()
    {
        if (grid == null) grid = FindObjectOfType<RevisedGridController>();
        if (surface == null) surface = FindObjectOfType<ForearmDepthSurface>();
        if (interaction == null && surface != null)
            interaction = surface.GetComponent<ForearmInteraction>();
    }

    void LateUpdate()
    {
        if (IsCarrying && interaction != null && interaction.IsActive)
            grid?.SetHighlightFromUV(interaction.TouchUV, true);
        else if (!IsCarrying)
            grid?.ClearHighlight();
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
        grid?.ClearHighlight();
        return true;
    }

    public void TickCarryFollowFinger(Transform indexTipWorld)
    {
        if (_draggedItem == null || indexTipWorld == null) return;

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

        if (!grid.TryBakeWidgetIntoCell(_draggedItem, col, row))
            Debug.LogWarning("[RevisedGridPlacement] Could not bake widget image into grid cell.");

        Destroy(_draggedItem.gameObject);
        _draggedItem = null;
        grid.ClearHighlight();
    }

    public void DestroyCarriedItem()
    {
        if (_draggedItem == null) return;

        Destroy(_draggedItem.gameObject);
        _draggedItem = null;
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

        grid.UVToCell(surfaceUV, out int col, out int row);
        if (!grid.IsCellOccupied(col, row)) return false;
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
