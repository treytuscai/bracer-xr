using UnityEngine;
using Surface.Buffer;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System;

namespace Surface.Core
{
    /// <summary>
    /// Masks depth cells that fall inside the interacting hand's volume using
    /// per-bone capsule segments derived from the skeleton hierarchy.
    ///
    /// Each frame SnapshotJoints() walks all bones, builds a capsule for every
    /// parent->child bone pair, and stores the results in both native arrays
    /// (for the Burst masking job) and managed Vector4 arrays (for the shader
    /// finger-occlusion upload in ForearmDepthSurface).
    ///
    /// Joint positions are captured at the same moment as the depth readback
    /// request (called from LateUpdate), so mask and depth frame stay aligned
    /// despite async GPU readback timing.
    /// </summary>
    public class HandMask : IDisposable
    {
        private const int MaxCapsules = 24;

        private float _padding;
        private OVRSkeleton _handSkeleton;
        private bool _isInitialized;

        // Native capsule arrays — fed into the Burst masking job each frame
        private NativeArray<Vector3> _nativeCapsuleA;
        private NativeArray<Vector3> _nativeCapsuleB;

        // Managed capsule arrays — exposed publicly for material SetVectorArray
        private readonly Vector4[] _managedCapsuleA = new Vector4[MaxCapsules];
        private readonly Vector4[] _managedCapsuleB = new Vector4[MaxCapsules];
        private int _capsuleCount;

        // Public accessors for ForearmDepthSurface shader upload
        public Vector4[] CapsuleA    => _managedCapsuleA;
        public Vector4[] CapsuleB    => _managedCapsuleB;
        public int       CapsuleCount => _capsuleCount;
        public float     Radius       => _padding;
        public bool      HasCapsules  => _capsuleCount > 0;

        public HandMask(OVRSkeleton handSkeleton, float maskRadius)
        {
            _handSkeleton = handSkeleton;
            _padding      = maskRadius;
        }

        /// <summary>
        /// Builds capsule segments from parent→child bone pairs and caches
        /// them in both native (Burst) and managed (shader) storage.
        /// Called from LateUpdate alongside DepthReadback.Schedule().
        /// </summary>
        public void SnapshotJoints()
        {
            if (!_isInitialized && !TryInit()) return;

            var bones = _handSkeleton.Bones;
            _capsuleCount = 0;

            for (int i = 0; i < bones.Count && _capsuleCount < MaxCapsules; i++)
            {
                if (bones[i]?.Transform == null) continue;

                int parentIdx = bones[i].ParentBoneIndex;
                if (parentIdx < 0 || parentIdx >= bones.Count) continue;
                if (bones[parentIdx]?.Transform == null) continue;

                Vector3 a = bones[parentIdx].Transform.position;
                Vector3 b = bones[i].Transform.position;

                // Skip untracked joints parked at world origin
                if (a.sqrMagnitude < 0.001f || b.sqrMagnitude < 0.001f) continue;

                int idx = _capsuleCount++;
                _nativeCapsuleA[idx] = a;
                _nativeCapsuleB[idx] = b;
                _managedCapsuleA[idx] = new Vector4(a.x, a.y, a.z, 0f);
                _managedCapsuleB[idx] = new Vector4(b.x, b.y, b.z, 0f);
            }
        }

        /// <summary>
        /// Schedules the Burst job that flags depth cells inside any hand capsule.
        /// Returns the input dependency unchanged if no capsules are valid.
        /// </summary>
        public JobHandle Schedule(SurfaceBuffer surfBuf, JobHandle dependency)
        {
            if (!_isInitialized && !TryInit()) return dependency;

            if (_capsuleCount == 0)
            {
                var clearJob = new ClearMaskJob { IsHandMasked = surfBuf.IsHandMasked };
                return clearJob.Schedule(surfBuf.IsHandMasked.Length, 128, dependency);
            }

            var job = new HandMaskJob
            {
                Hits         = surfBuf.Hits,
                HasDepth     = surfBuf.HasDepth,
                CapsuleA     = _nativeCapsuleA,
                CapsuleB     = _nativeCapsuleB,
                CapsuleCount = _capsuleCount,
                RadiusSq     = _padding * _padding,
                IsHandMasked = surfBuf.IsHandMasked
            };

            return job.Schedule(surfBuf.Hits.Length, 128, dependency);
        }

        public void Dispose()
        {
            if (_nativeCapsuleA.IsCreated) _nativeCapsuleA.Dispose();
            if (_nativeCapsuleB.IsCreated) _nativeCapsuleB.Dispose();
        }

        private bool TryInit()
        {
            var bones = _handSkeleton.Bones;
            if (bones == null || bones.Count == 0) return false;

            _nativeCapsuleA = new NativeArray<Vector3>(MaxCapsules, Allocator.Persistent);
            _nativeCapsuleB = new NativeArray<Vector3>(MaxCapsules, Allocator.Persistent);
            _isInitialized  = true;
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
            [ReadOnly] public NativeArray<Vector3> CapsuleA;
            [ReadOnly] public NativeArray<Vector3> CapsuleB;
            public int   CapsuleCount;
            public float RadiusSq;
            [WriteOnly] public NativeArray<bool> IsHandMasked;

            public void Execute(int index)
            {
                if (!HasDepth[index]) { IsHandMasked[index] = false; return; }

                Vector3 p = Hits[index];
                for (int ci = 0; ci < CapsuleCount; ci++)
                {
                    Vector3 ab   = CapsuleB[ci] - CapsuleA[ci];
                    Vector3 ap   = p - CapsuleA[ci];
                    float   abSq = ab.x*ab.x + ab.y*ab.y + ab.z*ab.z;
                    float   raw  = abSq > 1e-6f ? (ap.x*ab.x + ap.y*ab.y + ap.z*ab.z) / abSq : 0f;
                    float   t    = raw < 0f ? 0f : (raw > 1f ? 1f : raw);
                    Vector3 d    = p - (CapsuleA[ci] + t * ab);
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
