using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders a horizontal gradient bar from black (left) to a full-brightness hue colour (right).
/// Used below <see cref="Experiment2ColorWheelGraphic"/> so the user can adjust brightness
/// independently of hue/saturation (experiment2_ArmText only).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class Experiment2BrightnessBarGraphic : MaskableGraphic
{
    Color _hueColor = Color.red;

    /// <summary>The pure (full-brightness) hue colour that the right edge displays.</summary>
    public Color HueColor
    {
        get => _hueColor;
        set
        {
            _hueColor = value;
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect r = rectTransform.rect;
        Color right = _hueColor;
        right.a = 1f;

        // Two triangles forming a quad with a left→right gradient (black → hue colour).
        AddVert(vh, new Vector2(r.xMin, r.yMin), Color.black);
        AddVert(vh, new Vector2(r.xMin, r.yMax), Color.black);
        AddVert(vh, new Vector2(r.xMax, r.yMax), right);
        AddVert(vh, new Vector2(r.xMax, r.yMin), right);

        vh.AddTriangle(0, 1, 2);
        vh.AddTriangle(0, 2, 3);
    }

    static void AddVert(VertexHelper vh, Vector2 pos, Color c)
    {
        UIVertex v = UIVertex.simpleVert;
        v.position = pos;
        v.color    = c;
        vh.AddVert(v);
    }

    /// <summary>
    /// Returns 0–1 brightness value based on horizontal position inside the bar.
    /// Returns <c>false</c> if <paramref name="localPos"/> is outside the bar rect.
    /// </summary>
    public bool TryGetBrightnessAt(Vector2 localPos, out float brightness)
    {
        brightness = 1f;
        Rect r = rectTransform.rect;
        if (!r.Contains(localPos))
            return false;

        brightness = Mathf.Clamp01((localPos.x - r.xMin) / r.width);
        return true;
    }
}
