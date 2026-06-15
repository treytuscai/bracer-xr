using System.IO;
using UnityEngine;

/// <summary>
/// Persists 1H body-region placements (vertical + horizontal) across app runs.
/// </summary>
public static class OneHHorizVerticalPlacementStore
{
    const string FileName = "OneHHorizVerticalPlacements.json";

    [System.Serializable]
    public struct PlacementData
    {
        public bool IsValid;
        public bool UseMeshUv;
        public float MeshU;
        public float MeshV;
        public int Col;
        public int Row;
        public float Scale;
        public float RotationDegrees;
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
        return data.IsValid;
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
                  $"'{label}' uv=({copy.MeshU:F3},{copy.MeshV:F3}) cell=({copy.Col},{copy.Row}) scale={copy.Scale:F2} rot={copy.RotationDegrees:F0}°");
    }

    public static void ClearAll()
    {
        _cache = new SaveFile();
        _dirty = true;
        Flush();
        Debug.Log("[OneHPlacementStore] Cleared all saved placements.");
    }

    public static void ClearRegion(int regionIndex)
    {
        EnsureLoaded();
        if (_cache.Regions == null || regionIndex < 0 || regionIndex >= _cache.Regions.Length)
            return;

        _cache.Regions[regionIndex] = new RegionRecord();
        _dirty = true;
        Flush();
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

    public static PlacementData FromOrientedPlacement(in OneHHorizVerticalController.OrientedPlacement placement) =>
        new PlacementData
        {
            IsValid = true,
            UseMeshUv = placement.useMeshUv,
            MeshU = placement.meshU,
            MeshV = placement.meshV,
            Col = placement.col,
            Row = placement.row,
            Scale = placement.scale,
            RotationDegrees = placement.rotationDegrees
        };

    public static OneHHorizVerticalController.OrientedPlacement ToOrientedPlacement(in PlacementData data) =>
        new OneHHorizVerticalController.OrientedPlacement
        {
            useMeshUv = data.UseMeshUv,
            meshU = data.MeshU,
            meshV = data.MeshV,
            col = data.Col,
            row = data.Row,
            scale = data.Scale,
            rotationDegrees = data.RotationDegrees
        };
}
