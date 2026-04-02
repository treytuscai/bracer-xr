using UnityEngine;

/// <summary>
/// Wraps Meta hand tracking APIs. Provides wrist anchor and fingertip positions
/// for both hands each frame.
/// </summary>
public class HandTrackingController : MonoBehaviour
{
    [Header("References (assign in Inspector)")]
    public OVRHand rightHand;
    public OVRSkeleton rightSkeleton;

    // Require non-null, actively tracked, AND high confidence.
    // High confidence gate prevents acting on degraded tracking
    public bool isRightHandTracked => rightHand != null
        && rightHand.IsTracked
        && rightHand.HandConfidence >= OVRHand.TrackingConfidence.High;

    /// <summary>
    /// Returns the world space transform of the specified bone.
    /// Returns null if the bone is not tracked or bone unavailable.
    /// </summary>
    public Transform getBoneTransform(OVRSkeleton skeleton, OVRSkeleton.BoneId boneID)
    {
        if (skeleton == null || skeleton.Bones == null) return null;
        if ((int)boneID >= skeleton.Bones.Count) return null;
        return skeleton.Bones[(int)boneID].Transform;
    }

    // Convenience: Right index fingertip (input finger)
    public Transform rightIndexTip => getBoneTransform(rightSkeleton, OVRSkeleton.BoneId.Hand_IndexTip);
}
