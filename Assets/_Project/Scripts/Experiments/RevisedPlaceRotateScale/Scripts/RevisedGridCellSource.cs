using UnityEngine;

/// <summary>
/// Carries source pixel data for a grid cell while a widget is on the fingertip,
/// so re-placement preserves scale/rotation metadata.
/// </summary>
public class RevisedGridCellSource : MonoBehaviour
{
    public Color[] sourcePixels;
    public int     sourceSize;
    public Color   tint = Color.white;
    public float   scale = 1f;
    public float   rotationDegrees;
}
