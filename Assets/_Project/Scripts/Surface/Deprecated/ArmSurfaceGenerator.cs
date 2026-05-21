using UnityEngine;

/// <summary>
/// Generates a tapered cylinder mesh on the forearm surface using
/// Meta Movement SDK body tracking for real elbow and wrist joints.
/// The mesh serves as both the visual forearm overlay and the
/// projection surface for on-skin UI elements.
///
/// Body tracking joint IDs (IOBT skeleton):
///   Body_LeftArmLower  (index 11) = elbow
///   Body_LeftHandWrist (index 19) = wrist
///
/// Dependencies:
///   - OVRBody: provides body skeleton with elbow joint position
///   - CalibrationManager: provides per-user wrist/elbow radii
///
/// Consumers:
///   - TouchInputManager: calls GetClosestSurfacePoint() for hit testing
///   - VisualFeedbackController: reads mesh for shader-based feedback
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ArmSurfaceGenerator : MonoBehaviour
{
    [Header("References")]
    public OVRBody bodyTracking;
    public CalibrationManager calibrationManager;

    [Header("Forearm Dimensions")]
    [Tooltip("How much of the forearm to render (0-1)")]
    [Range(0.5f, 1.0f)]
    public float forearmCoverage = 0.85f;

    [Header("Mesh Resolution")]
    [Range(8, 32)]
    public int circumferenceSegments = 16;

    [Range(2, 12)]
    public int lengthSegments = 6;

    [Header("Offset")]
    [Tooltip("Gap between physical arm and mesh surface (meters)")]
    [Range(0.0f, 0.01f)]
    public float skinOffset = 0.002f;

    [Header("Material")]
    public Material forearmMaterial;

    // IOBT skeleton joint indices for left forearm
    private const int JOINT_LEFT_ARM_LOWER = 11;
    private const int JOINT_LEFT_WRIST = 19;

    // Mesh internals
    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Vector3[] _vertices;
    private Vector2[] _uvs;
    private int[] _triangles;
    private bool _meshBuilt = false;
    private bool _wasVisible = false;

    // Cached per-frame joint data, updated in LateUpdate
    private Vector3 _wristPos;
    private Vector3 _elbowPos;
    private Quaternion _wristRot;

    /// <summary>
    /// Source-of-truth check for whether body tracking is actively
    /// producing valid skeleton data this frame. Mirrors the pattern
    /// used by HandTrackingController.isRightHandTracked.
    ///
    /// Note: OVRBody doesn't expose a confidence tier like OVRHand does.
    /// Body tracking is binary (valid or not). Silent fallback to
    /// low-fidelity mode can be diagnosed via adb, not the API.
    /// </summary>
    public bool IsTracking
    {
        get
        {
            if (bodyTracking == null) return false;

            var skeleton = bodyTracking.GetComponent<OVRSkeleton>();
            if (skeleton == null) return false;

            return bodyTracking.enabled
                && skeleton.IsDataValid
                && skeleton.Bones != null
                && skeleton.Bones.Count > JOINT_LEFT_WRIST;
        }
    }

    void Start()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();

        if (forearmMaterial != null)
            _meshRenderer.material = forearmMaterial;

        _mesh = new Mesh { name = "ForearmCylinder" };
        _meshFilter.mesh = _mesh;

        // Pre-build mesh topology (verts, UVs, triangles).
        // Vertex positions are all zero. They get placed correctly
        // each frame in UpdateCylinderVertices().
        BuildMesh();
        _meshRenderer.enabled = false;

        if (bodyTracking == null)
            Debug.LogError("[ForearmMesh] OVRBody reference not assigned.");
    }

    /// <summary>
    /// Builds mesh topology once at startup. Creates a cylinder grid
    /// with (lengthSegments+1) rings of (circumferenceSegments+1) verts.
    /// UV layout: U wraps around circumference [0,1], V runs along
    /// forearm axis from wrist (0) to elbow (1).
    /// </summary>
    void BuildMesh()
    {
        int rings = lengthSegments + 1;
        int vertsPerRing = circumferenceSegments + 1;
        int vertCount = rings * vertsPerRing;

        _vertices = new Vector3[vertCount];
        _uvs = new Vector2[vertCount];

        for (int ring = 0; ring < rings; ring++)
        {
            float v = (float)ring / lengthSegments;
            for (int seg = 0; seg < vertsPerRing; seg++)
            {
                float u = (float)seg / circumferenceSegments;
                int idx = ring * vertsPerRing + seg;
                _uvs[idx] = new Vector2(u, v);
                _vertices[idx] = Vector3.zero;
            }
        }

        int triCount = lengthSegments * circumferenceSegments * 6;
        _triangles = new int[triCount];
        int ti = 0;

        for (int ring = 0; ring < lengthSegments; ring++)
        {
            for (int seg = 0; seg < circumferenceSegments; seg++)
            {
                int current = ring * vertsPerRing + seg;
                int next = current + vertsPerRing;

                // Two triangles per quad
                _triangles[ti++] = current;
                _triangles[ti++] = next;
                _triangles[ti++] = current + 1;

                _triangles[ti++] = current + 1;
                _triangles[ti++] = next;
                _triangles[ti++] = next + 1;
            }
        }

        _mesh.vertices = _vertices;
        _mesh.uv = _uvs;
        _mesh.triangles = _triangles;
        _meshBuilt = true;
    }

    /// <summary>
    /// Extracts wrist and elbow world positions from the IOBT skeleton.
    /// Called only after IsTracking confirms skeleton validity.
    /// Returns false if positions are near-zero (tracking initialized
    /// but no real spatial data yet).
    /// </summary>
    bool TryGetBodyJoints(out Vector3 wristPos, out Vector3 elbowPos,
                          out Quaternion wristRot)
    {
        var skeleton = bodyTracking.GetComponent<OVRSkeleton>();
        var wristBone = skeleton.Bones[JOINT_LEFT_WRIST];
        var elbowBone = skeleton.Bones[JOINT_LEFT_ARM_LOWER];

        wristPos = wristBone.Transform.position;
        elbowPos = elbowBone.Transform.position;
        wristRot = wristBone.Transform.rotation;

        // Reject near-origin positions: tracking system is initialized
        // but hasn't produced real spatial data yet
        return wristPos.sqrMagnitude >= 0.001f
            && elbowPos.sqrMagnitude >= 0.001f;
    }

    void LateUpdate()
    {
        if (!_meshBuilt || !IsTracking || !TryGetBodyJoints(out _wristPos, out _elbowPos, out _wristRot))
        {
            if (_wasVisible)
            {
                _meshRenderer.enabled = false;
                _wasVisible = false;
            }
            return;
        }

        if (!_wasVisible)
        {
            _meshRenderer.enabled = true;
            _wasVisible = true;
            Debug.Log("[ForearmMesh] Body tracking active.");
        }

        UpdateCylinderVertices();
    }

    /// <summary>
    /// Repositions all mesh vertices to form a tapered cylinder between
    /// wrist and elbow. Radius interpolates from calibrated wrist radius
    /// to elbow radius along the forearm axis.
    ///
    /// Coordinate frame built from:
    ///   - forearmDir: wrist -> elbow axis
    ///   - right/up: perpendicular plane derived from wrist rotation
    /// </summary>
    void UpdateCylinderVertices()
    {
        int rings = lengthSegments + 1;
        int vertsPerRing = circumferenceSegments + 1;

        Vector3 fullForearm = _elbowPos - _wristPos;
        float fullLength = fullForearm.magnitude;
        Vector3 forearmDir = fullForearm.normalized;
        float renderLength = fullLength * forearmCoverage;

        // Build a perpendicular frame using wrist rotation
        Vector3 wristUp = _wristRot * Vector3.up;
        Vector3 right = Vector3.Cross(forearmDir, wristUp).normalized;
        Vector3 up = Vector3.Cross(right, forearmDir).normalized;

        for (int ring = 0; ring < rings; ring++)
        {
            float t = (float)ring / lengthSegments;

            Vector3 ringCenter = _wristPos
                + forearmDir * (t * renderLength);

            // Taper: wrist radius at t=0, elbow radius at t=1
            float radius = Mathf.Lerp(calibrationManager.wristRadius, calibrationManager.elbowRadius, t)
                + skinOffset;

            for (int seg = 0; seg < vertsPerRing; seg++)
            {
                float angle = (float)seg / circumferenceSegments
                    * Mathf.PI * 2f;

                Vector3 offset = (Mathf.Cos(angle) * right
                    + Mathf.Sin(angle) * up) * radius;

                int idx = ring * vertsPerRing + seg;
                _vertices[idx] = transform.InverseTransformPoint(
                    ringCenter + offset);
            }
        }

        _mesh.vertices = _vertices;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    /// <summary>
    /// Projects a world-space point onto the cylinder surface.
    /// Returns the closest point on the surface and the corresponding
    /// UV coordinates for touch/UI mapping.
    ///
    /// UV mapping:
    ///   U [0,1] = circumferential position (wraps around arm)
    ///   V [0,1] = axial position (0 = wrist, 1 = elbow end of render zone)
    ///
    /// Used by TouchInputManager to convert fingertip position into
    /// a surface hit point + UV for interaction routing.
    /// </summary>
    public bool GetClosestSurfacePoint(Vector3 worldPoint,
        out Vector3 closestPoint, out Vector2 uv, out float signedDistance)
    {
        signedDistance = float.MaxValue;
        closestPoint = Vector3.zero;
        uv = Vector2.zero;

        Vector3 fullForearm = _elbowPos - _wristPos;
        float fullLength = fullForearm.magnitude;
        if (fullLength < 0.01f) return false;

        Vector3 forearmDir = fullForearm.normalized;
        float renderLength = fullLength * forearmCoverage;

        Vector3 wristUp = _wristRot * Vector3.up;
        Vector3 right = Vector3.Cross(forearmDir, wristUp).normalized;
        Vector3 up = Vector3.Cross(right, forearmDir).normalized;

        // Project point onto forearm axis to find axial position
        Vector3 toPoint = worldPoint - _wristPos;
        float axisT = Vector3.Dot(toPoint, forearmDir) / renderLength;
        axisT = Mathf.Clamp01(axisT);

        // Find the ring center and radius at this axial position
        Vector3 ringCenter = _wristPos
            + forearmDir * (axisT * renderLength);
        float radius = Mathf.Lerp(calibrationManager.wristRadius, calibrationManager.elbowRadius, axisT)
            + skinOffset;

        // Project onto the ring circumference to find angular position
        Vector3 fromCenter = worldPoint - ringCenter;
        float rightComp = Vector3.Dot(fromCenter, right);
        float upComp = Vector3.Dot(fromCenter, up);
        float angle = Mathf.Atan2(upComp, rightComp);

        closestPoint = ringCenter
            + (Mathf.Cos(angle) * right + Mathf.Sin(angle) * up) * radius;

        // Map angle to [0,1] UV space
        float u = angle / (Mathf.PI * 2f);
        if (u < 0) u += 1f;
        uv = new Vector2(u, axisT);

        float radialDist = Mathf.Sqrt(rightComp * rightComp + upComp * upComp);
        signedDistance = radialDist - radius;  // negative = inside cylinder

        return true;
    }
}