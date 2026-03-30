using UnityEngine;

public class CalibrationManager : MonoBehaviour
{

    [Tooltip("Radius at the wrist end (meters)")]
    [Range(0.02f, 0.06f)]
    public float wristRadius = 0.03f;

    [Tooltip("Radius at the elbow end (meters)")]
    [Range(0.03f, 0.07f)]
    public float elbowRadius = 0.05f;

    /// <summary>
    /// Per-user calibration. Pass circumference in meters.
    /// </summary>
    public void SetArmDimensions(float wristCirc, float elbowCirc)
    {
        wristRadius = wristCirc / (2f * Mathf.PI);
        elbowRadius = elbowCirc / (2f * Mathf.PI);
    }

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
