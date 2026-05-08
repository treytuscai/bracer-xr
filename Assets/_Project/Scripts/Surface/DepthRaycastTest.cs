using UnityEngine;
using System.Collections.Generic;
using Meta.XR;

/// <summary>
/// High-density forearm surface mesh via radial depth raycasts.
///
/// Casts rays inward from a ring around the forearm at each station.
/// 360° coverage — the depth buffer naturally limits hits to the
/// camera-facing side. No centerEye dependency.
/// </summary>
public class DepthRaycastTest : MonoBehaviour
{
    [Header("References")]
    public OVRSkeleton bodySkeleton;
    public EnvironmentRaycastManager raycastManager;

    [Header("Sampling Grid")]
    [Range(5, 40)]  public int stations = 25;
    [Range(5, 50)]  public int angles = 40;
    [Range(0.03f, 0.10f)] public float rayStartRadius = 0.06f;
    public float maxRayDistance = 0.12f;

    private const int JOINT_LEFT_ARM_LOWER = 11;
    private const int JOINT_LEFT_WRIST = 19;

    private Transform _elbow, _wrist;
    private bool _bonesResolved;
    private MeshFilter _meshFilter;

    private Vector3[,] _hits;
    private bool[,] _valid;
    private List<Vector3> _verts;
    private List<int> _tris;
    private int _frameCount;

    void Start()
    {
        _meshFilter = gameObject.AddComponent<MeshFilter>();
        var mr = gameObject.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0f, 1f, 1f, 0.7f);
        mr.material = mat;

        AllocateBuffers();
    }

    void AllocateBuffers()
    {
        _hits = new Vector3[stations, angles];
        _valid = new bool[stations, angles];
        _verts = new List<Vector3>(stations * angles);
        _tris = new List<int>(stations * angles * 6);
    }

    void LateUpdate()
    {
        if (raycastManager == null || bodySkeleton == null) return;

        if (!_bonesResolved)
        {
            _elbow = bodySkeleton.Bones[JOINT_LEFT_ARM_LOWER].Transform;
            _wrist = bodySkeleton.Bones[JOINT_LEFT_WRIST].Transform;
            _bonesResolved = true;
        }
        if (_elbow == null || _wrist == null) return;

        if (_hits.GetLength(0) != stations || _hits.GetLength(1) != angles)
            AllocateBuffers();

        SampleSurface();
        BuildMesh();

        _frameCount++;
        if (_frameCount % 60 == 0)
        {
            int hits = 0;
            foreach (bool v in _valid) if (v) hits++;
            Debug.Log($"[DepthTest] Hits: {hits}/{stations * angles}");
        }
    }

    void SampleSurface()
    {
        Vector3 elbowPos = _elbow.position;
        Vector3 wristPos = _wrist.position;
        Vector3 axis = (wristPos - elbowPos).normalized;
        float armLen = Vector3.Distance(elbowPos, wristPos);

        // Perpendicular frame
        Vector3 refDir = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f
            ? Vector3.right : Vector3.up;
        Vector3 pA = Vector3.Cross(axis, refDir).normalized;
        Vector3 pB = Vector3.Cross(axis, pA).normalized;

        for (int s = 0; s < stations; s++)
        {
            float t = (float)s / (stations - 1);
            Vector3 center = Vector3.Lerp(elbowPos, wristPos, t);

            for (int a = 0; a < angles; a++)
            {
                float rad = a * (2f * Mathf.PI / angles);
                Vector3 radial = pA * Mathf.Cos(rad) + pB * Mathf.Sin(rad);

                // Start outside, cast inward
                Vector3 rayOrigin = center + radial * rayStartRadius;
                Vector3 rayDir = -radial;

                Ray ray = new Ray(rayOrigin, rayDir);
                _valid[s, a] = false;

                if (raycastManager.Raycast(ray, out var hit, maxRayDistance))
                {
                    Vector3 toHit = hit.point - elbowPos;
                    float proj = Vector3.Dot(toHit, axis);
                    if (proj >= -0.02f && proj <= armLen + 0.02f)
                    {
                        _hits[s, a] = hit.point;
                        _valid[s, a] = true;
                    }
                }
            }
        }
    }

    void BuildMesh()
    {
        _verts.Clear();
        _tris.Clear();

        int[,] idx = new int[stations, angles];

        for (int s = 0; s < stations; s++)
            for (int a = 0; a < angles; a++)
            {
                if (_valid[s, a])
                {
                    idx[s, a] = _verts.Count;
                    _verts.Add(_hits[s, a]);
                }
                else idx[s, a] = -1;
            }

        for (int s = 0; s < stations - 1; s++)
        {
            for (int a = 0; a < angles - 1; a++)
            {
                int i00 = idx[s, a], i10 = idx[s + 1, a];
                int i11 = idx[s + 1, a + 1], i01 = idx[s, a + 1];
                if (i00 < 0 || i10 < 0 || i11 < 0 || i01 < 0) continue;

                _tris.Add(i00); _tris.Add(i01); _tris.Add(i11);
                _tris.Add(i00); _tris.Add(i11); _tris.Add(i10);
            }
        }

        Mesh mesh = _meshFilter.mesh;
        mesh.Clear();
        mesh.SetVertices(_verts);
        mesh.SetTriangles(_tris, 0);
        mesh.RecalculateNormals();
    }
}