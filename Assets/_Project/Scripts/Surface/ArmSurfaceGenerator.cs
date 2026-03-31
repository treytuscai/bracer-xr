using UnityEngine;

/// <summary>
/// Generates a tapered cylinder mesh on the forearm surface using
/// Meta Movement SDK body tracking for real elbow and wrist joints.
///
/// Body tracking joint IDs:
///   Body_LeftArmLower  (index 11) = elbow
///   Body_LeftHandWrist (index 19) = wrist
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

    // Body tracking joint indices
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

    // Current frame tracking data
    private Vector3 _wristPos;
    private Vector3 _elbowPos;
    private Quaternion _wristRot;

    void Start()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();

        if (forearmMaterial != null)
            _meshRenderer.material = forearmMaterial;

        _mesh = new Mesh();
        _mesh.name = "ForearmCylinder";
        _meshFilter.mesh = _mesh;

        BuildMesh();
        _meshRenderer.enabled = false;

        if (bodyTracking == null)
            Debug.LogError("[ForearmMesh] OVRBody reference not assigned.");
    }

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
    /// Reads elbow and wrist positions from OVRBody skeleton.
    /// </summary>
    bool TryGetBodyJoints(out Vector3 wristPos, out Vector3 elbowPos,
                          out Quaternion wristRot)
    {
        wristPos = Vector3.zero;
        elbowPos = Vector3.zero;
        wristRot = Quaternion.identity;

        if (bodyTracking == null) return false;

        var skeleton = bodyTracking.GetComponent<OVRSkeleton>();
        if (skeleton == null || skeleton.Bones == null
            || skeleton.Bones.Count <= JOINT_LEFT_WRIST)
            return false;

        var wristBone = skeleton.Bones[JOINT_LEFT_WRIST];
        var elbowBone = skeleton.Bones[JOINT_LEFT_ARM_LOWER];

        if (wristBone == null || wristBone.Transform == null) return false;
        if (elbowBone == null || elbowBone.Transform == null) return false;

        wristPos = wristBone.Transform.position;
        elbowPos = elbowBone.Transform.position;
        wristRot = wristBone.Transform.rotation;

        if (wristPos.sqrMagnitude < 0.001f) return false;
        if (elbowPos.sqrMagnitude < 0.001f) return false;

        return true;
    }

    void LateUpdate()
    {
        if (!_meshBuilt) return;

        if (!TryGetBodyJoints(out _wristPos, out _elbowPos, out _wristRot))
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

    void UpdateCylinderVertices()
    {
        int rings = lengthSegments + 1;
        int vertsPerRing = circumferenceSegments + 1;

        Vector3 fullForearm = _elbowPos - _wristPos;
        float fullLength = fullForearm.magnitude;
        Vector3 forearmDir = fullForearm.normalized;
        float renderLength = fullLength * forearmCoverage;

        Vector3 wristUp = _wristRot * Vector3.up;
        Vector3 right = Vector3.Cross(forearmDir, wristUp).normalized;
        Vector3 up = Vector3.Cross(right, forearmDir).normalized;

        for (int ring = 0; ring < rings; ring++)
        {
            float t = (float)ring / lengthSegments;

            Vector3 ringCenter = _wristPos
                + forearmDir * (t * renderLength);
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
    /// Returns closest surface point and UV for touch mapping.
    /// </summary>
    public bool GetClosestSurfacePoint(Vector3 worldPoint,
        out Vector3 closestPoint, out Vector2 uv)
    {
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

        Vector3 toPoint = worldPoint - _wristPos;
        float axisT = Vector3.Dot(toPoint, forearmDir) / renderLength;
        axisT = Mathf.Clamp01(axisT);

        Vector3 ringCenter = _wristPos
            + forearmDir * (axisT * renderLength);
        float radius = Mathf.Lerp(calibrationManager.wristRadius, calibrationManager.elbowRadius, axisT)
            + skinOffset;

        Vector3 fromCenter = worldPoint - ringCenter;
        float rightComp = Vector3.Dot(fromCenter, right);
        float upComp = Vector3.Dot(fromCenter, up);
        float angle = Mathf.Atan2(upComp, rightComp);

        closestPoint = ringCenter
            + (Mathf.Cos(angle) * right + Mathf.Sin(angle) * up) * radius;

        float u = angle / (Mathf.PI * 2f);
        if (u < 0) u += 1f;
        uv = new Vector2(u, axisT);

        return true;
    }
}