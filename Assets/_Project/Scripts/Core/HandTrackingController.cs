using UnityEngine;

/// <summary>
/// Wraps Meta hand tracking APIs. Provides wrist anchor and fingertip positions
/// for both hands each frame.
/// </summary>
public class HandTrackingController : MonoBehaviour
{
    [Header("References (assign in Inspector)")]
    public OVRHand leftHand;
    public OVRHand rightHand;
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    // Public accessors for other components
    public bool isLeftHandTracked => leftHand != null
        && leftHand.IsTracked
        && leftHand.HandConfidence >= OVRHand.TrackingConfidence.High;

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

    // Convenience: Left wrist transform (display arm anchor)
    public Transform leftWrist => getBoneTransform(leftSkeleton, OVRSkeleton.BoneId.Hand_WristRoot);

    // Convenience: Right index fingertip (input finger)
    public Transform rightIndexTip => getBoneTransform(rightSkeleton, OVRSkeleton.BoneId.Hand_IndexTip);

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (isLeftHandTracked && leftWrist != null)
        {
            Debug.Log($"Left wrist position: {leftWrist.position}");
        }
    }
}
