using System.IO;
using UnityEngine;

/// <summary>
/// Persists 1H body-region UV placements (vertical + horizontal) across app runs.
/// </summary>
public static class OneHHorizVerticalPlacementStore
{
    const string FileName = "OneHHorizVerticalPlacements.json";

    [System.Serializable]
    public struct PlacementData
    {
        public bool IsValid;
        public float MeshU;
        public float MeshV;
        public float Size;
        public float RotationDegrees;

        // Legacy — loaded from older saves.
        public float UvHalfHeight;
        public float Scale;
    }

    [System.Serializable]
    public class RegionRecord
    {
        public string Label;
        public PlacementData Vertical;
        public PlacementData Horizontal;
    }

    [System.Serializable]
    class SaveFile
    {
        public RegionRecord[] Regions = System.Array.Empty<RegionRecord>();
    }

    static SaveFile _cache;
    static bool _dirty;

    static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    public static void EnsureLoaded()
    {
        if (_cache != null)
            return;

        _cache = new SaveFile();
        if (!File.Exists(FilePath))
            return;

        try
        {
            string json = File.ReadAllText(FilePath);
            var loaded = JsonUtility.FromJson<SaveFile>(json);
            if (loaded?.Regions != null)
                _cache = loaded;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[OneHPlacementStore] Could not load '{FilePath}': {e.Message}");
        }
    }

    public static void EnsureRegionCount(int count)
    {
        EnsureLoaded();
        if (_cache.Regions != null && _cache.Regions.Length == count)
            return;

        var next = new RegionRecord[count];
        for (int i = 0; i < count; i++)
        {
            if (_cache.Regions != null && i < _cache.Regions.Length && _cache.Regions[i] != null)
                next[i] = _cache.Regions[i];
            else
                next[i] = new RegionRecord();
        }

        _cache.Regions = next;
    }

    public static bool TryGet(int regionIndex, bool horizontal, out PlacementData data)
    {
        data = default;
        EnsureLoaded();
        if (_cache.Regions == null || regionIndex < 0 || regionIndex >= _cache.Regions.Length)
            return false;

        RegionRecord region = _cache.Regions[regionIndex];
        if (region == null)
            return false;

        data = horizontal ? region.Horizontal : region.Vertical;
        if (!data.IsValid)
            return false;

        MigrateLegacy(ref data);
        return true;
    }

    public static void Save(int regionIndex, string label, bool horizontal, in PlacementData data)
    {
        EnsureLoaded();
        EnsureRegionCount(Mathf.Max(regionIndex + 1, _cache.Regions?.Length ?? 0));

        RegionRecord region = _cache.Regions[regionIndex] ??= new RegionRecord();
        region.Label = label;
        var copy = data;
        copy.IsValid = true;

        if (horizontal)
            region.Horizontal = copy;
        else
            region.Vertical = copy;

        _dirty = true;
        Flush();
        Debug.Log($"[OneHPlacementStore] Saved {(horizontal ? "horizontal" : "vertical")} " +
                  $"'{label}' uv=({copy.MeshU:F3},{copy.MeshV:F3}) size={copy.Size:F3} rot={copy.RotationDegrees:F0}°");
    }

    public static void ClearAll()
    {
        _cache = new SaveFile();
        _dirty = true;
        Flush();
        Debug.Log("[OneHPlacementStore] Cleared all saved placements.");
    }

    static void Flush()
    {
        if (!_dirty || _cache == null)
            return;

        try
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(_cache, prettyPrint: true));
            _dirty = false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[OneHPlacementStore] Could not write '{FilePath}': {e.Message}");
        }
    }

    static void MigrateLegacy(ref PlacementData data)
    {
        if (data.Size > 0f)
            return;

        if (data.UvHalfHeight > 0f)
            data.Size = data.UvHalfHeight;
        else if (data.Scale > 0f)
            data.Size = Mathf.Clamp(data.Scale * 0.04f, 0.005f, 0.25f);
        else
            data.Size = 0.08f;
    }

    public static PlacementData FromOrientedPlacement(in OneHHorizVerticalController.OrientedPlacement placement) =>
        new PlacementData
        {
            IsValid = true,
            MeshU = placement.meshU,
            MeshV = placement.meshV,
            Size = placement.size,
            RotationDegrees = placement.rotationDegrees
        };

    public static OneHHorizVerticalController.OrientedPlacement ToOrientedPlacement(in PlacementData data)
    {
        var copy = data;
        MigrateLegacy(ref copy);
        return new OneHHorizVerticalController.OrientedPlacement
        {
            meshU = copy.MeshU,
            meshV = copy.MeshV,
            size = copy.Size,
            rotationDegrees = copy.RotationDegrees
        };
    }
}
