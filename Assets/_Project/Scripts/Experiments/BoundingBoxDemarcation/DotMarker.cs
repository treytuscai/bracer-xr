using UnityEngine;

/// <summary>
/// Marks a placed forearm dot in experiment1A_BoundBox. Scene-specific only.
/// </summary>
public class Experiment1ADotMarker : MonoBehaviour
{
    public int DotId { get; internal set; }

    public RectTransform RectTransform => transform as RectTransform;
}
