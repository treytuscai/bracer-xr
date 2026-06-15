using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 1i-only angle calibration: capture raw readings at known reference poses (e.g. 0°, 45°, 90°)
/// and map raw tracker output to iPhone level readings.
/// </summary>
public static class OrientationDegreeCalibration
{
    public enum Mode
    {
        SingleOffset,
        LinearScaleOffset,
        PiecewiseLinear,
    }

    [Serializable]
    public struct Point
    {
        public float referenceDegrees;
        public float rawDegrees;
        public bool captured;

        public Point(float referenceDegrees)
        {
            this.referenceDegrees = referenceDegrees;
            rawDegrees = 0f;
            captured = false;
        }
    }

    public static readonly float[] StandardReferences = { 0f, 45f, 90f };

    public static float Apply(float rawDegrees, Mode mode, float scale, float offset, IList<Point> points)
    {
        switch (mode)
        {
            case Mode.LinearScaleOffset:
                return scale * rawDegrees + offset;
            case Mode.PiecewiseLinear:
                return PiecewiseMap(rawDegrees, points);
            default:
                return rawDegrees + offset;
        }
    }

    public static bool TryFitLinear(IList<Point> points, out float scale, out float offset)
    {
        scale = 1f;
        offset = 0f;

        int n = 0;
        float sumX = 0f, sumY = 0f, sumXX = 0f, sumXY = 0f;
        for (int i = 0; i < points.Count; i++)
        {
            if (!points[i].captured)
                continue;

            n++;
            float x = points[i].rawDegrees;
            float y = points[i].referenceDegrees;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        if (n < 2)
            return false;

        float denom = n * sumXX - sumX * sumX;
        if (Mathf.Abs(denom) < 1e-6f)
        {
            offset = sumY / n - sumX / n;
            scale = 1f;
            return true;
        }

        scale = (n * sumXY - sumX * sumY) / denom;
        offset = (sumY - scale * sumX) / n;
        return true;
    }

    static float PiecewiseMap(float rawDegrees, IList<Point> points)
    {
        var sorted = new List<Point>();
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].captured)
                sorted.Add(points[i]);
        }

        if (sorted.Count == 0)
            return rawDegrees;

        sorted.Sort((a, b) => a.rawDegrees.CompareTo(b.rawDegrees));

        if (sorted.Count == 1)
            return sorted[0].referenceDegrees + (rawDegrees - sorted[0].rawDegrees);

        if (rawDegrees <= sorted[0].rawDegrees)
            return Extrapolate(sorted[0], sorted[1], rawDegrees);

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            Point a = sorted[i];
            Point b = sorted[i + 1];
            if (rawDegrees <= b.rawDegrees)
                return Interpolate(a, b, rawDegrees);
        }

        return Extrapolate(sorted[sorted.Count - 2], sorted[sorted.Count - 1], rawDegrees);
    }

    static float Interpolate(Point a, Point b, float rawDegrees)
    {
        float span = b.rawDegrees - a.rawDegrees;
        if (Mathf.Abs(span) < 1e-5f)
            return a.referenceDegrees;

        float t = (rawDegrees - a.rawDegrees) / span;
        return Mathf.Lerp(a.referenceDegrees, b.referenceDegrees, t);
    }

    static float Extrapolate(Point a, Point b, float rawDegrees) => Interpolate(a, b, rawDegrees);

    public static int CapturedCount(IList<Point> points)
    {
        int n = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].captured)
                n++;
        }

        return n;
    }

    public static string FormatStatus(Mode mode, float scale, float offset, IList<Point> points)
    {
        var parts = new List<string> { $"mode={mode}" };
        for (int i = 0; i < points.Count; i++)
        {
            Point p = points[i];
            parts.Add(p.captured
                ? $"{p.referenceDegrees:F0}°→raw {p.rawDegrees:F1}"
                : $"{p.referenceDegrees:F0}°=(pending)");
        }

        if (mode == Mode.LinearScaleOffset)
            parts.Add($"scale={scale:F3} offset={offset:F1}");

        return string.Join("  ", parts);
    }
}
