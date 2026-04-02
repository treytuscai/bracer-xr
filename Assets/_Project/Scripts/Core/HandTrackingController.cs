using UnityEngine;

/// <summary>
/// Wraps Meta OVRHand/OVRSkeleton APIs into a clean interface for
/// the rest of the system. Exposes per-frame joint positions and
/// high-confidence tracking state for both hands.
///
/// Architecture role:
///   - Right hand: input hand — index fingertip drives touch detection
///   - Inverse configuration (right display, left input) deferred
///     to a later iteration
///
/// Consumers:
///   - TouchInputManager: reads isRightHandTracked + rightIndexTip
///   - ArmSurfaceGenerator: could use leftWrist for future anchoring
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
    /// Generic bone accessor. Returns null if skeleton isn't ready
    /// or the requested bone doesn't exist, allowing callers to
    /// null-check without knowing SDK internals.
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
