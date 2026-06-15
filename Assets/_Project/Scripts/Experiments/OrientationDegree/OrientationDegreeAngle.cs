using UnityEngine;

/// <summary>
/// 1i-only arm elevation from the IOBT body forearm axis (wrist 19 → elbow 11).
/// </summary>
public static class OrientationDegreeAngle
{
    public static bool TryDirectionFromBonePositions(
        Vector3 wristWorld,
        Vector3 elbowWorld,
        bool flipAxis,
        out Vector3 direction)
    {
        direction = elbowWorld - wristWorld;
        if (flipAxis)
            direction = -direction;

        if (direction.sqrMagnitude < 1e-6f)
            return false;

        direction.Normalize();
        return true;
    }

    public static bool TryResolveForearmDirection(
        ForearmDepthSurface surface,
        bool flipAxis,
        out Vector3 direction)
    {
        direction = Vector3.zero;
        if (surface == null || !surface.IsValid)
            return false;

        return TryDirectionFromBonePositions(
            surface.WristPosition, surface.ElbowPosition, flipAxis, out direction);
    }

    /// <summary>
    /// Unsigned elevation from the world horizontal plane (0° = level, 90° = straight up).
    /// </summary>
    public static float ElevationFromHorizontalDegrees(Vector3 direction)
    {
        direction.Normalize();
        float horizontal = new Vector2(direction.x, direction.z).magnitude;
        return Mathf.Abs(Mathf.Atan2(direction.y, horizontal) * Mathf.Rad2Deg);
    }
}
