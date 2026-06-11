using UnityEngine;

/// <summary>
/// Persists the arm placement of the 1dRememberNotif template for the current XR app session.
/// Survives scene loads via DontDestroyOnLoad. 1D-only; other experiments read via <see cref="Instance"/>.
/// </summary>
public sealed class OneDSessionRememberNotifStore : MonoBehaviour
{
    public static OneDSessionRememberNotifStore Instance { get; private set; }

    [System.Serializable]
    public struct PlacementRecord
    {
        public bool IsValid;
        public string TemplateName;
        public int Col;
        public int Row;
        /// <summary>Exact mesh UV used when the widget was committed.</summary>
        public Vector2 MeshUV;
        /// <summary>Center of the anchor grid cell in mesh UV space.</summary>
        public Vector2 CellCenterUV;
        public float Scale;
        public float RotationDegrees;
        public Vector3 WorldPosition;
        public Vector3 WorldNormal;
    }

    PlacementRecord _record;

    public bool HasRecording => _record.IsValid;
    public PlacementRecord Recording => _record;

    public static OneDSessionRememberNotifStore GetOrCreate()
    {
        if (Instance != null)
            return Instance;

        var existing = FindObjectOfType<OneDSessionRememberNotifStore>();
        if (existing != null)
        {
            Instance = existing;
            return existing;
        }

        var go = new GameObject("[OneDSessionRememberNotifStore]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<OneDSessionRememberNotifStore>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Save(PlacementRecord record)
    {
        record.IsValid = true;
        _record = record;
        Debug.Log($"[OneDSessionRememberNotif] Saved '{record.TemplateName}' at cell ({record.Col},{record.Row}), uv={record.MeshUV}");
    }

    public void ClearRecording()
    {
        _record = default;
    }

    /// <summary>
    /// Samples world pose on a live grid surface from the recorded mesh UV.
    /// </summary>
    public bool TryGetWorldPose(RevisedGridController grid, out Vector3 worldPos, out Vector3 worldNormal)
    {
        worldPos = Vector3.zero;
        worldNormal = Vector3.up;

        if (!_record.IsValid || grid == null)
            return false;

        return grid.TrySampleSurfaceAtUV(_record.MeshUV, out worldPos, out worldNormal);
    }
}
