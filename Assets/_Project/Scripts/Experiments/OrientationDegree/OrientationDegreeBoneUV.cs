using UnityEngine;
/// <summary>
/// 1i-only placement UV: bone-stable world anchors and bone-fixed lateral projection.
/// Does not use camera-fixed mesh SurfaceUV for stamp center — that reintroduces glide and
/// can push the stamp off the 0–1 mesh patch near the wrist.
/// </summary>
public static class OrientationDegreeBoneUV
{
    /// <summary>
    /// Maps a world-space point to display UV using a bone-fixed lateral axis (typically
    /// Cross(axis, -wrist.up)). Does not subtract mesh ProjCenter — that value tracks the noisy
    /// depth boundary and is tied to the camera frame.
    /// </summary>
    public static Vector2 Compute(
        Vector3 pt,
        Vector3 wristPos,
        Vector3 axis,
        Vector3 boneLateral,
        float pronationScroll,
        float displayOffset,
        float displayWidth,
        float displayHeight)
    {
        Vector3 fromWrist = pt - wristPos;

        float distAlong = Vector3.Dot(fromWrist, axis);
        float v = 1f - (((distAlong - displayOffset) / Mathf.Max(displayHeight, 1e-4f)) + 0.5f);

        float projR = Vector3.Dot(fromWrist, boneLateral);
        float u = (projR / Mathf.Max(displayWidth, 1e-4f)) + 0.25f + pronationScroll;

        return new Vector2(u, v);
    }

    /// <summary>
    /// Rotation (radians, CCW in bone UV) aligning stamp +V toward the wrist from the anchor,
    /// estimated by sampling bone-fixed UV a short distance along the arm.
    /// </summary>
    public static float ComputeTowardWristRotationRadians(
        Vector3 anchorWorld,
        Vector3 wristPos,
        Vector3 axis,
        Vector3 boneLateral,
        float pronationScroll,
        float displayOffset,
        float displayWidth,
        float displayHeight,
        float sampleDistanceMeters = 0.04f)
    {
        if (sampleDistanceMeters <= 1e-5f)
            return 0f;

        Vector3 towardWrist = -axis.normalized;
        Vector2 uv0 = Compute(
            anchorWorld, wristPos, axis, boneLateral, pronationScroll,
            displayOffset, displayWidth, displayHeight);
        Vector2 uv1 = Compute(
            anchorWorld + towardWrist * sampleDistanceMeters,
            wristPos, axis, boneLateral, pronationScroll,
            displayOffset, displayWidth, displayHeight);

        Vector2 delta = uv1 - uv0;
        if (delta.sqrMagnitude < 1e-10f)
            return 0f;

        return Mathf.Atan2(delta.x, delta.y);
    }

    /// <summary>
    /// Bone-stable world anchor on the forearm at a given distance from the wrist.
    /// </summary>
    public static Vector3 ResolveAnchorWorld(
        Vector3 wristPos,
        Vector3 axis,
        Transform wrist,
        Vector3 cameraFacingHint,
        float alongArm,
        float radialArm,
        float lateralArm)
    {
        if (!TryGetBoneLateral(axis, wrist, cameraFacingHint, out Vector3 boneLateral))
            boneLateral = Vector3.Cross(axis, cameraFacingHint).normalized;

        Vector3 boneOut = BoneOutward(axis, boneLateral, cameraFacingHint);
        return wristPos
            + axis * alongArm
            + boneOut * radialArm
            + boneLateral * lateralArm;
    }

    /// <summary>
    /// Forearm bone frame for placement: wrist→elbow axis and lateral from wrist.up (IOBT palm axis).
    /// </summary>
    public static bool TryGetBoneLateral(
        Vector3 axis,
        Transform wrist,
        Vector3 cameraFacingFallback,
        out Vector3 boneLateral)
    {
        boneLateral = Vector3.zero;
        if (wrist == null)
            return false;

        boneLateral = Vector3.Cross(axis, -wrist.up);
        if (boneLateral.sqrMagnitude < 1e-4f)
            boneLateral = Vector3.Cross(axis, cameraFacingFallback);
        if (boneLateral.sqrMagnitude < 1e-4f)
            return false;

        boneLateral.Normalize();
        return true;
    }

    /// <summary>
    /// Unit vector from the arm axis toward the camera-facing side of the forearm (in the bone frame).
    /// </summary>
    public static Vector3 BoneOutward(Vector3 axis, Vector3 boneLateral, Vector3 cameraFacingHint)
    {
        Vector3 boneOut = Vector3.Cross(boneLateral, axis);
        if (boneOut.sqrMagnitude < 1e-4f)
            boneOut = cameraFacingHint;
        else
            boneOut.Normalize();

        if (Vector3.Dot(boneOut, cameraFacingHint) < 0f)
            boneOut = -boneOut;
        return boneOut;
    }
}
