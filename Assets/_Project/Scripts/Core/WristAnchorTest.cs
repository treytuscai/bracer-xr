using UnityEngine;

/// <summary>
/// Anchors a target transform (the test quad) to the left wrist
/// </summary>
public class WristAnchorTest : MonoBehaviour
{
    [Header("References")]
    public HandTrackingController handTrackingController;
    public Transform targetQuad;

    [Header("Offset (tweak in Inspector)")]
    public Vector3 positionOffset = new Vector3(0f, 0.02f, 0f);
    public Vector3 rotationOffset = new Vector3(-90f, 0f, 0f);

    [Header("Smoothing")]
    [Range(0.01f, 1f)]
    public float smoothSpeed = 0.15f;

    private bool _wasTracked = false;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    // LateUpdate is called once per frame after all Update functions have been processed
    void LateUpdate()
    {
        if (handTrackingController == null || targetQuad == null) return;

        Transform wrist = handTrackingController.leftWrist;
        bool isTracked = handTrackingController.isLeftHandTracked && wrist != null;

        // Show/hide quad based on tracking
        if (isTracked != _wasTracked)
        {
            targetQuad.gameObject.SetActive(isTracked);
            _wasTracked = isTracked;
        }

        if (!isTracked) return;

        // Target position + offset in wrist local space
        Vector3 targetPos = wrist.TransformPoint(positionOffset);
        Quaternion targetRot = wrist.rotation * Quaternion.Euler(rotationOffset);

        // Smooth to reduce jitter
        targetQuad.position = Vector3.Lerp(targetQuad.position, targetPos, smoothSpeed);
        targetQuad.rotation = Quaternion.Slerp(targetQuad.rotation, targetRot, smoothSpeed);
    }

}
