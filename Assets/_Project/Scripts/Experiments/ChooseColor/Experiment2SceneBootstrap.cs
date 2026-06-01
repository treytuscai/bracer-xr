using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// experiment2_ArmText only: ensures a single OVRCameraRig, no stray Main Camera,
/// and a wired HandTrackingController — mirrors Experiment1ASceneBootstrap pattern.
/// </summary>
[DefaultExecutionOrder(-10000)]
public class Experiment2SceneBootstrap : MonoBehaviour
{
    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment2)
            return;

        RemoveDuplicateCameraRigs();
        RemoveStrayMainCamera();
        EnsureHandTrackingController();
    }

    static void RemoveDuplicateCameraRigs()
    {
        var rigs = FindObjectsOfType<OVRCameraRig>(true);
        for (int i = 1; i < rigs.Length; i++)
            Destroy(rigs[i].gameObject);
    }

    static void RemoveStrayMainCamera()
    {
        foreach (var cam in FindObjectsOfType<Camera>(true))
        {
            if (cam.GetComponentInParent<OVRCameraRig>() != null)
                continue;
            if (cam.gameObject.name == "Main Camera")
                Destroy(cam.gameObject);
        }
    }

    static void EnsureHandTrackingController()
    {
        var existing = FindObjectOfType<HandTrackingController>(true);
        if (existing != null)
        {
            WireMissingHandReferences(existing);
            return;
        }

        if (!TryGetHandReferences(rightHand: true, out OVRHand rightHand, out OVRSkeleton rightSkeleton))
            return;

        TryGetHandReferences(rightHand: false, out OVRHand leftHand, out OVRSkeleton leftSkeleton);

        var managers = GameObject.Find("[Managers]") ?? new GameObject("[Managers]");
        var go = new GameObject("HandTrackingController");
        go.transform.SetParent(managers.transform, false);

        var controller = go.AddComponent<HandTrackingController>();
        controller.leftHand    = leftHand;
        controller.leftSkeleton = leftSkeleton;
        controller.rightHand    = rightHand;
        controller.rightSkeleton = rightSkeleton;
    }

    static void WireMissingHandReferences(HandTrackingController controller)
    {
        if ((controller.leftHand == null || controller.leftSkeleton == null)
            && TryGetHandReferences(false, out OVRHand lh, out OVRSkeleton ls))
        {
            controller.leftHand    = lh;
            controller.leftSkeleton = ls;
        }

        if ((controller.rightHand == null || controller.rightSkeleton == null)
            && TryGetHandReferences(true, out OVRHand rh, out OVRSkeleton rs))
        {
            controller.rightHand    = rh;
            controller.rightSkeleton = rs;
        }
    }

    static bool TryGetHandReferences(bool rightHand, out OVRHand hand, out OVRSkeleton skeleton)
    {
        hand = null; skeleton = null;
        var rig = FindObjectOfType<OVRCameraRig>(true);
        if (rig == null) return false;

        Transform anchor = rightHand
            ? (rig.rightHandAnchor != null ? rig.rightHandAnchor : rig.transform.Find("TrackingSpace/RightHandAnchor"))
            : (rig.leftHandAnchor  != null ? rig.leftHandAnchor  : rig.transform.Find("TrackingSpace/LeftHandAnchor"));

        if (anchor == null) return false;
        hand     = anchor.GetComponentInChildren<OVRHand>(true);
        skeleton = anchor.GetComponentInChildren<OVRSkeleton>(true);
        return hand != null && skeleton != null;
    }
}
