using UnityEngine;

/// <summary>
/// Detects touch interactions from the right hand's index fingertip
/// against the forearm surface mesh. Runs a simple state machine
/// (None -> Hover -> Press) based on fingertip-to-surface distance.
///
/// On-skin interface rationale: only two active states (Hover, Press)
/// because the margin between "contact" and "press" is negligible
/// at sub-centimeter scale on a body surface.
///
/// Dependencies:
///   - ArmSurfaceGenerator: provides surface projection + tracking state
///   - HandTrackingController: provides right index fingertip position
///
/// Consumers:
///   - VisualFeedbackController: polls touchState, contactPoint, currentUV
///     each frame to drive shader feedback (hover glow, press ripple)
/// </summary>
public class TouchInputManager : MonoBehaviour
{
    [Header("References")]
    public ArmSurfaceGenerator armSurfaceGenerator;
    public HandTrackingController handTrackingController;

    [Header("Thresholds")]
    [Tooltip("Distance at which hover state activates (meters)")]
    [Range(0.000f, 0.01f)]
    public float hoverDistance = 0.005f;
    [Tooltip("Distance at which press state activates (meters)")]
    [Range(0.000f, 0.01f)]
    public float pressDistance = 0.001f;

    // State machine
    public enum TouchState
    {
        None,  // Finger not near surface
        Hover, // Finger within hover threshold
        Press  // Finger within press threshold (on-skin contact)
    }
    public TouchState touchState;

    // Touch point data (valid when state != None)
    /// <summary>UV on the arm mesh: U = circumference, V = wrist-to-elbow axis</summary>
    public Vector2 currentUV;
    /// <summary>World-space point on the mesh surface closest to fingertip</summary>
    public Vector3 contactPoint;

    /// <summary>
    /// Runs after ArmSurfaceGenerator.LateUpdate has updated the mesh.
    ///
    /// Guard order:
    ///   1. Right hand tracked at high confidence
    ///   2. Body tracking producing valid skeleton data
    ///   3. Surface projection succeeds (forearm has nonzero length)
    ///
    /// If any guard fails, state resets to None, downstream consumers
    /// (VisualFeedbackController) will clear any active feedback.
    /// </summary>
    void LateUpdate()
    {
        // Both tracking systems must be active
        if (!handTrackingController.isRightHandTracked || !armSurfaceGenerator.IsTracking)
        {
            touchState = TouchState.None;
            return;
        }

        // Project fingertip onto the arm cylinder surface
        if (!armSurfaceGenerator.GetClosestSurfacePoint(
                handTrackingController.rightIndexTip.position,
                out Vector3 closestPoint, out Vector2 uv, out float signedDistance))
        {
            touchState = TouchState.None;
            return;
        }

        // signedDistance >= 0 means finger is outside → use threshold checks
        if (signedDistance <= pressDistance)
        {
            touchState = TouchState.Press;
            currentUV = uv;
            contactPoint = closestPoint;
        }
        else if (signedDistance <= hoverDistance)
        {
            touchState = TouchState.Hover;
            currentUV = uv;
            contactPoint = closestPoint;
        }
        else
        {
            touchState = TouchState.None;
        }
    }
}
