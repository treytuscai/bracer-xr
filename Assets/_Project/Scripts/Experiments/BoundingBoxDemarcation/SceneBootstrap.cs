using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// experiment1A_BoundBox only: single OVRCameraRig, no stray Main Camera, hand tracking wired.
/// </summary>
[DefaultExecutionOrder(-10000)]
public class Experiment1ASceneBootstrap : MonoBehaviour
{
    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Experiment1A)
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
        foreach (var camera in FindObjectsOfType<Camera>(true))
        {
            if (camera.GetComponentInParent<OVRCameraRig>() != null)
                continue;

            if (camera.gameObject.name == "Main Camera")
                Destroy(camera.gameObject);
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
        controller.leftHand = leftHand;
        controller.leftSkeleton = leftSkeleton;
        controller.rightHand = rightHand;
        controller.rightSkeleton = rightSkeleton;
    }

    static void WireMissingHandReferences(HandTrackingController controller)
    {
        if ((controller.leftHand == null || controller.leftSkeleton == null)
            && TryGetHandReferences(rightHand: false, out OVRHand leftHand, out OVRSkeleton leftSkeleton))
        {
            controller.leftHand = leftHand;
            controller.leftSkeleton = leftSkeleton;
        }

        if ((controller.rightHand == null || controller.rightSkeleton == null)
            && TryGetHandReferences(rightHand: true, out OVRHand rightHand, out OVRSkeleton rightSkeleton))
        {
            controller.rightHand = rightHand;
            controller.rightSkeleton = rightSkeleton;
        }
    }

    static bool TryGetHandReferences(bool rightHand, out OVRHand hand, out OVRSkeleton skeleton)
    {
        hand = null;
        skeleton = null;

        var rig = FindObjectOfType<OVRCameraRig>(true);
        if (rig == null)
            return false;

        Transform anchor = rightHand
            ? rig.rightHandAnchor != null ? rig.rightHandAnchor : rig.transform.Find("TrackingSpace/RightHandAnchor")
            : rig.leftHandAnchor != null ? rig.leftHandAnchor : rig.transform.Find("TrackingSpace/LeftHandAnchor");

        if (anchor == null)
            return false;

        hand = anchor.GetComponentInChildren<OVRHand>(true);
        skeleton = anchor.GetComponentInChildren<OVRSkeleton>(true);
        return hand != null && skeleton != null;
    }
}
