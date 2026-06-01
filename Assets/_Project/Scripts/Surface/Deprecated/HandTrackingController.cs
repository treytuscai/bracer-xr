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
        && rightHand.IsTracked;

    /// <summary>
    /// Generic bone accessor. Returns null if skeleton isn't ready
    /// or the requested bone doesn't exist, allowing callers to
    /// null-check without knowing SDK internals.
    /// </summary>
    public Transform getBoneTransform(OVRSkeleton skeleton, string boneName)
    {
        if (skeleton == null || skeleton.Bones == null) return null;
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Transform != null && bone.Transform.name == boneName)
                return bone.Transform;
        }
        return null;
    }

    // Index 10 = IndexTip per OVR XRHand layout (verified in HandMask.cs)
    public Transform rightIndexTip => (rightSkeleton != null && rightSkeleton.Bones != null && rightSkeleton.Bones.Count > 10)
        ? rightSkeleton.Bones[10].Transform
        : null;
}
