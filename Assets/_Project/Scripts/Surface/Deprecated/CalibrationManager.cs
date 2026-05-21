using UnityEngine;

/// <summary>
/// Stores per-user forearm dimensions used by ArmSurfaceGenerator
/// to size the tapered cylinder mesh. Radii can be set manually
/// in the Inspector or programmatically via SetArmDimensions().
///
/// Current implementation: static values set once per session.
/// Does not adapt dynamically to muscle flexion or skin deformation
/// during use.
///
/// Consumers:
///   - ArmSurfaceGenerator: reads wristRadius/elbowRadius each frame
///     to interpolate cylinder ring radii along the forearm axis
/// </summary>
public class CalibrationManager : MonoBehaviour
{
    [Header("Forearm Dimensions")]
    [Tooltip("Radius at the wrist end (meters)")]
    [Range(0.02f, 0.06f)]
    public float wristRadius = 0.03f;

    [Tooltip("Radius at the elbow end (meters)")]
    [Range(0.03f, 0.07f)]
    public float elbowRadius = 0.05f;

    /// <summary>
    /// Converts circumference measurements to radii.
    /// Circumference is easier to measure physically,
    /// so this is the expected input for any future calibration UI.
    /// </summary>
    public void SetArmDimensions(float wristCirc, float elbowCirc)
    {
        wristRadius = wristCirc / (2f * Mathf.PI);
        elbowRadius = elbowCirc / (2f * Mathf.PI);
    }
}
