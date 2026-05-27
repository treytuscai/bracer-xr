using UnityEngine;

/// <summary>
/// Drives visual feedback on the forearm mesh based on touch state.
/// Sets shader uniforms each frame so the forearm material renders
/// a proximity glow (hover) or contact highlight (press) at the
/// touch point.
///
/// Purely reactive — contains no input logic. Reads state from
/// TouchInputManager and pushes it to the GPU via material uniforms.
///
/// Requires the forearm material to use the Custom/ForearmSurface
/// shader, which exposes:
///   _TouchUV      (Vector4) — UV of the current touch point
///   _TouchState   (Float)   — 0 = none, 1 = hover, 2 = press
///   _TouchRadius  (Float)   — radius of the feedback circle (UV space)
///
/// Dependencies:
///   - TouchInputManager: provides touch state, UV, and contact point
///   - ArmSurfaceGenerator: provides the forearm material reference
/// </summary>
public class VisualFeedbackController : MonoBehaviour
{
    [Header("References")]
    public TouchInputManager touchInputManager;
    public ArmSurfaceGenerator armSurfaceGenerator;

    [Header("Feedback Settings")]
    [Tooltip("Radius of the hover/press highlight in UV space")]
    [Range(0.01f, 0.15f)]
    public float feedbackRadius = 0.05f;

    // Shader property IDs — cached to avoid per-frame string hashing
    private int _touchUVId;
    private int _touchStateId;
    private int _touchRadiusId;

    void Start()
    {
        _touchUVId = Shader.PropertyToID("_TouchUV");
        _touchStateId = Shader.PropertyToID("_TouchState");
        _touchRadiusId = Shader.PropertyToID("_TouchRadius");
    }

    void LateUpdate()
    {
        // Grab directly each frame to avoid script execution order issues
        Material mat = armSurfaceGenerator.GetComponent<MeshRenderer>().material;
        if (mat == null) return;

        float state = 0f;

        switch (touchInputManager.touchState)
        {
            case TouchInputManager.TouchState.Hover:
                state = 1f;
                break;
            case TouchInputManager.TouchState.Press:
                state = 2f;
                break;
        }

        mat.SetVector(_touchUVId, touchInputManager.currentUV);
        mat.SetFloat(_touchStateId, state);
        mat.SetFloat(_touchRadiusId, feedbackRadius);
    }
}