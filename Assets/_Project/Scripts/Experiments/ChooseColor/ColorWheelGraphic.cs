using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders a circular HSV color wheel as a UI mesh (experiment2_ArmText only).
/// Hue varies by angle around the circle; saturation varies by radius
/// (centre = white, edge = full hue at current <see cref="Brightness"/>).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class Experiment2ColorWheelGraphic : MaskableGraphic
{
    [Range(16, 128)] public int segments = 64;

    float _brightness = 1f;

    public float Brightness
    {
        get => _brightness;
        set
        {
            _brightness = Mathf.Clamp01(value);
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect rect = rectTransform.rect;
        float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
        Vector2 center = rect.center;

        // Centre vertex — white tinted by brightness.
        UIVertex cv = UIVertex.simpleVert;
        cv.position = new Vector3(center.x, center.y, 0f);
        cv.color    = Color.HSVToRGB(0f, 0f, _brightness);
        vh.AddVert(cv);

        // Outer ring: one vertex per segment + one repeat to close the loop.
        for (int i = 0; i <= segments; i++)
        {
            float t        = (float)i / segments;
            float angleRad = t * 2f * Mathf.PI;
            Vector2 dir    = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad));

            UIVertex v = UIVertex.simpleVert;
            v.position = new Vector3(center.x + dir.x * radius, center.y + dir.y * radius, 0f);
            v.color    = Color.HSVToRGB(t, 1f, _brightness);
            vh.AddVert(v);
        }

        // Fan of triangles from centre to outer ring.
        for (int i = 0; i < segments; i++)
            vh.AddTriangle(0, i + 1, i + 2);
    }

    /// <summary>
    /// Returns the HSV-derived color at <paramref name="localPos"/> (canvas-local coordinates).
    /// Also outputs the raw <paramref name="hue"/> and <paramref name="saturation"/> values.
    /// Returns <c>false</c> if the position is outside the wheel circle.
    /// </summary>
    public bool TryGetColorAt(Vector2 localPos, out Color color, out float hue, out float saturation)
    {
        color      = Color.white;
        hue        = 0f;
        saturation = 0f;

        Rect rect   = rectTransform.rect;
        float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
        Vector2 delta = localPos - rect.center;
        float dist    = delta.magnitude;

        if (dist > radius)
            return false;

        saturation = dist / radius;
        float angle = Mathf.Atan2(delta.y, delta.x);
        if (angle < 0f) angle += 2f * Mathf.PI;
        hue   = angle / (2f * Mathf.PI);
        color = Color.HSVToRGB(hue, saturation, _brightness);
        color.a = 1f;
        return true;
    }
}
