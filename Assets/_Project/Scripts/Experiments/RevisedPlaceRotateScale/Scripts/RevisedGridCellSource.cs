using UnityEngine;

/// <summary>
/// Carries source pixel data for a grid cell while a widget is on the fingertip,
/// so re-placement preserves scale/rotation metadata.
/// </summary>
public class RevisedGridCellSource : MonoBehaviour
{
    public Color[] sourcePixels;
    [Tooltip("Legacy square size — used only when sourceWidth/sourceHeight are unset.")]
    public int sourceSize;
    public int sourceWidth;
    public int sourceHeight;
    public Color tint = Color.white;
    public float scale = 1f;
    public float rotationDegrees;

    public int SourcePixelWidth => sourceWidth > 0 ? sourceWidth : sourceSize;
    public int SourcePixelHeight => sourceHeight > 0 ? sourceHeight : sourceSize;
}
