using UnityEngine;
using Surface.Buffer;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System;
using System.Collections.Generic;

namespace Surface.Core
{
    /// <summary>
    /// Masks depth cells that fall inside the interacting hand's volume by sampling
    /// world-space vertices baked from the hand's SkinnedMeshRenderer each frame.
    ///
    /// Dense vertex coverage replaces per-bone capsule segments: no parent-bone
    /// hierarchy tracking is needed, and the Burst job reduces to simple sphere tests.
    /// The same vertex array is exposed publicly for the shader's soft occlusion fade.
    ///
    /// SnapshotMesh() is called from LateUpdate alongside DepthReadback.Schedule()
    /// so the mask stays aligned with the depth frame despite async GPU readback.
    /// </summary>
    public class HandMask : IDisposable
    {
        private const int MaxVerts = 64;

        private float _padding;
        private SkinnedMeshRenderer _handMesh;
        private bool _isInitialized;

        private Mesh _bakedMesh;
        private readonly List<Vector3> _vertList = new List<Vector3>(1024);

        // Native vertex array — fed into the Burst masking job each frame
        private NativeArray<Vector3> _nativeVerts;

        // Managed vertex array — exposed publicly for material SetVectorArray
        private readonly Vector4[] _managedVerts = new Vector4[MaxVerts];
        private int _vertCount;

        public Vector4[] Vertices    => _managedVerts;
        public int       VertexCount => _vertCount;
        public bool      HasVertices => _vertCount > 0;
        public float     Radius      => _padding;

        public HandMask(SkinnedMeshRenderer handMesh, float maskRadius)
        {
            _handMesh = handMesh;
            _padding  = maskRadius;
        }

        /// <summary>
        /// Bakes the hand mesh for the current pose, downsamples to MaxVerts world-space
        /// points, and caches them in both native (Burst) and managed (shader) storage.
        /// Called from LateUpdate alongside DepthReadback.Schedule().
        /// </summary>
        public void SnapshotMesh()
        {
            if (_handMesh == null) return;
            if (!_isInitialized && !TryInit()) return;

            _handMesh.BakeMesh(_bakedMesh);
            _bakedMesh.GetVertices(_vertList);

            int total = _vertList.Count;
            int step  = Mathf.Max(1, total / MaxVerts);
            Matrix4x4 l2w = _handMesh.transform.localToWorldMatrix;
            _vertCount = 0;

            for (int i = 0; i < total && _vertCount < MaxVerts; i += step)
            {
                Vector3 world = l2w.MultiplyPoint3x4(_vertList[i]);
                if (world.sqrMagnitude < 0.001f) continue;
                _nativeVerts[_vertCount]   = world;
                _managedVerts[_vertCount]  = new Vector4(world.x, world.y, world.z, 0f);
                _vertCount++;
            }
        }

        /// <summary>
        /// Schedules the Burst job that flags depth cells inside the hand volume.
        /// Returns the input dependency unchanged when no vertices are available.
        /// </summary>
        public JobHandle Schedule(SurfaceBuffer surfBuf, JobHandle dependency)
        {
            if (_vertCount == 0)
            {
                var clearJob = new ClearMaskJob { IsHandMasked = surfBuf.IsHandMasked };
                return clearJob.Schedule(surfBuf.IsHandMasked.Length, 128, dependency);
            }

            var job = new HandMaskJob
            {
                Hits         = surfBuf.Hits,
                HasDepth     = surfBuf.HasDepth,
                Verts        = _nativeVerts,
                VertCount    = _vertCount,
                RadiusSq     = _padding * _padding,
                IsHandMasked = surfBuf.IsHandMasked
            };

            return job.Schedule(surfBuf.Hits.Length, 128, dependency);
        }

        public void Dispose()
        {
            if (_nativeVerts.IsCreated) _nativeVerts.Dispose();
            if (_bakedMesh != null) UnityEngine.Object.Destroy(_bakedMesh);
        }

        private bool TryInit()
        {
            if (_handMesh == null) return false;
            _handMesh.updateWhenOffscreen = true;
            _bakedMesh   = new Mesh();
            _nativeVerts = new NativeArray<Vector3>(MaxVerts, Allocator.Persistent);
            _isInitialized = true;
            return true;
        }

        // ==================================================================
        // BURST JOBS
        // ==================================================================

        [BurstCompile]
        struct HandMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    HasDepth;
            [ReadOnly] public NativeArray<Vector3> Verts;
            public int   VertCount;
            public float RadiusSq;
            [WriteOnly] public NativeArray<bool> IsHandMasked;

            public void Execute(int index)
            {
                if (!HasDepth[index]) { IsHandMasked[index] = false; return; }

                Vector3 p = Hits[index];
                for (int vi = 0; vi < VertCount; vi++)
                {
                    Vector3 d = p - Verts[vi];
                    if (d.x*d.x + d.y*d.y + d.z*d.z < RadiusSq)
                    {
                        IsHandMasked[index] = true;
                        return;
                    }
                }

                IsHandMasked[index] = false;
            }
        }

        [BurstCompile]
        struct ClearMaskJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<bool> IsHandMasked;
            public void Execute(int index) { IsHandMasked[index] = false; }
        }
    }
}
