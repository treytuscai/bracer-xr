using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visual line between two forearm dots on the ItemList canvas (experiment1A only).
/// </summary>
// Runs after Experiment1ABoundBoxController (95) so geometry refreshes on the same frame dots move.
[DefaultExecutionOrder(98)]
[RequireComponent(typeof(Image))]
public class Experiment1ALineSegment : MonoBehaviour
{
    public Experiment1ADotMarker DotA { get; private set; }
    public Experiment1ADotMarker DotB { get; private set; }

    RectTransform _rect;
    Image _image;

    RectTransform _coordRoot;

    [Min(1f)] public float thickness = 3f;

    public void Initialize(Experiment1ADotMarker a, Experiment1ADotMarker b, Color color, float lineThickness,
        RectTransform coordinateRoot)
    {
        DotA = a;
        DotB = b;
        thickness = lineThickness;
        _coordRoot = coordinateRoot;

        _rect = transform as RectTransform;
        _image = GetComponent<Image>();
        _image.color = color;
        _image.raycastTarget = false;

        RefreshGeometry();
    }

    /// <summary>Changes the rendered thickness without altering the baseline <see cref="thickness"/> field.</summary>
    public void SetDisplayThickness(float t)
    {
        if (_rect != null)
            _rect.sizeDelta = new Vector2(_rect.sizeDelta.x, Mathf.Max(1f, t));
    }

    void LateUpdate()
    {
        if (DotA == null || DotB == null)
            return;

        RefreshGeometry();
    }

    void RefreshGeometry()
    {
        if (_rect == null || DotA == null || DotB == null)
            return;

        RectTransform canvasRect = _coordRoot != null ? _coordRoot : _rect.parent as RectTransform;
        if (canvasRect == null)
            return;

        Vector2 a = canvasRect.InverseTransformPoint(DotA.transform.position);
        Vector2 b = canvasRect.InverseTransformPoint(DotB.transform.position);
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length < 0.5f)
            length = 0.5f;

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        _rect.localRotation = Quaternion.Euler(0f, 0f, angle);
        _rect.sizeDelta = new Vector2(length, thickness);
        _rect.anchoredPosition = a + delta * 0.5f;
    }
}
