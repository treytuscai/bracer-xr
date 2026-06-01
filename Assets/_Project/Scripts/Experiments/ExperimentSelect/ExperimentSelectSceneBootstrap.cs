using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runs early in experimentSelectScene to keep a single OVRCameraRig, remove the default
/// Main Camera, and ensure HandTrackingController exists under [Managers].
/// </summary>
[DefaultExecutionOrder(-10000)]
public class ExperimentSelectSceneBootstrap : MonoBehaviour
{
    void Awake()
    {
        if (SceneManager.GetActiveScene().name != ExperimentScenes.Select)
            return;

        RemoveDuplicateCameraRigs();
        RemoveStrayMainCamera();
        EnsureHandTrackingController();
    }

    static void RemoveDuplicateCameraRigs()
    {
        var rigs = FindObjectsOfType<OVRCameraRig>(true);
        for (int i = 1; i < rigs.Length; i++)
        {
            Debug.LogWarning(
                $"ExperimentSelectSceneBootstrap: destroying duplicate OVRCameraRig on '{rigs[i].name}'. " +
                "Close other loaded scenes (e.g. MainScene) if this keeps happening in the editor.",
                rigs[i]);
            Destroy(rigs[i].gameObject);
        }
    }

    static void RemoveStrayMainCamera()
    {
        foreach (var camera in FindObjectsOfType<Camera>(true))
        {
            if (camera.GetComponentInParent<OVRCameraRig>() != null)
                continue;

            if (camera.gameObject.name != "Main Camera")
                continue;

            Debug.Log("ExperimentSelectSceneBootstrap: removing default Main Camera (OVRCameraRig provides the XR camera).");
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

        if (!TryGetHandReferences(rigHandAnchor: true, out OVRHand rightHand, out OVRSkeleton rightSkeleton))
        {
            Debug.LogWarning(
                "ExperimentSelectSceneBootstrap: HandTrackingController not found and right OVRHand could not be resolved.");
            return;
        }

        TryGetHandReferences(rigHandAnchor: false, out OVRHand leftHand, out OVRSkeleton leftSkeleton);

        Transform managersRoot = FindManagersRoot();
        var controllerGo = new GameObject("HandTrackingController");
        controllerGo.transform.SetParent(managersRoot, false);

        var controller = controllerGo.AddComponent<HandTrackingController>();
        controller.leftHand = leftHand;
        controller.leftSkeleton = leftSkeleton;
        controller.rightHand = rightHand;
        controller.rightSkeleton = rightSkeleton;

        Debug.Log("ExperimentSelectSceneBootstrap: created HandTrackingController under [Managers].");
    }

    static void WireMissingHandReferences(HandTrackingController controller)
    {
        if ((controller.leftHand == null || controller.leftSkeleton == null)
            && TryGetHandReferences(rigHandAnchor: false, out OVRHand leftHand, out OVRSkeleton leftSkeleton))
        {
            controller.leftHand = leftHand;
            controller.leftSkeleton = leftSkeleton;
        }

        if ((controller.rightHand == null || controller.rightSkeleton == null)
            && TryGetHandReferences(rigHandAnchor: true, out OVRHand rightHand, out OVRSkeleton rightSkeleton))
        {
            controller.rightHand = rightHand;
            controller.rightSkeleton = rightSkeleton;
        }
    }

    static Transform FindManagersRoot()
    {
        var existing = GameObject.Find("[Managers]");
        if (existing != null)
            return existing.transform;

        var managersGo = new GameObject("[Managers]");
        return managersGo.transform;
    }

    static bool TryGetHandReferences(bool rigHandAnchor, out OVRHand hand, out OVRSkeleton skeleton)
    {
        hand = null;
        skeleton = null;

        var rig = FindObjectOfType<OVRCameraRig>(true);
        if (rig == null)
            return false;

        Transform handAnchor = rigHandAnchor
            ? rig.rightHandAnchor != null
                ? rig.rightHandAnchor
                : rig.transform.Find("TrackingSpace/RightHandAnchor")
            : rig.leftHandAnchor != null
                ? rig.leftHandAnchor
                : rig.transform.Find("TrackingSpace/LeftHandAnchor");

        if (handAnchor == null)
            return false;

        hand = handAnchor.GetComponentInChildren<OVRHand>(true);
        skeleton = handAnchor.GetComponentInChildren<OVRSkeleton>(true);
        return hand != null && skeleton != null;
    }
}
