using UnityEngine;
using Surface.Buffer;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System;

namespace Surface.Core
{
    /// <summary>
    /// Masks depth cells that fall inside the right hand's bounding volume,
    /// preventing hand-surface depth from contaminating the forearm mesh.
    ///
    /// Uses an axis-aligned bounding box (AABB) of all hand joint positions
    /// with configurable padding. This replaced per-capsule distance checks
    /// because capsules left gaps between fingers where hand depth leaked
    /// through, creating bumpy mesh artifacts at the hand-arm boundary.
    ///
    /// The AABB is intentionally generous — it masks more cells than strictly
    /// necessary, but SurfaceMemory fills the masked region with learned
    /// cloud points, so over-masking is harmless and prevents all contamination.
    ///
    /// Joint positions are captured in SnapshotJoints() at the same moment as
    /// the depth readback request (called from LateUpdate), so the mask aligns
    /// with the depth data despite async GPU readback timing.
    /// </summary>
    public class HandMask : IDisposable
    {
        // ------------------------------------------------------------------
        // CONFIGURATION
        // ------------------------------------------------------------------
        private float _padding;

        // ------------------------------------------------------------------
        // SKELETON REFERENCE
        // ------------------------------------------------------------------
        private OVRSkeleton _handSkeleton;

        // ------------------------------------------------------------------
        // NATIVE MEMORY
        // ------------------------------------------------------------------
        private NativeArray<Vector3> _jointPositions;
        private int _jointCount;
        private bool _isInitialized;

        // ------------------------------------------------------------------
        // AABB (computed fresh each frame in SnapshotJoints)
        // ------------------------------------------------------------------
        private Vector3 _boxMin;
        private Vector3 _boxMax;
        private bool _hasValidBox;

        public HandMask(OVRSkeleton handSkeleton, float maskRadius)
        {
            _handSkeleton = handSkeleton;
            _padding = maskRadius;
            _isInitialized = false;
            _hasValidBox = false;
        }

        /// <summary>
        /// Captures joint positions at schedule time and computes the padded
        /// AABB. Called from LateUpdate alongside DepthReadback.Schedule() so
        /// the mask matches the depth frame.
        /// </summary>
        public void SnapshotJoints()
        {
            if (!_isInitialized && !TryInit()) return;

            var bones = _handSkeleton.Bones;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            int validCount = 0;

            for (int i = 0; i < _jointCount; i++)
            {
                Vector3 pos = (i < bones.Count && bones[i].Transform != null)
                    ? bones[i].Transform.position
                    : Vector3.zero;

                _jointPositions[i] = pos;

                // Skip untracked joints (position stuck at origin)
                if (pos.sqrMagnitude < 0.001f) continue;

                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
                validCount++;
            }

            if (validCount < 3)
            {
                _hasValidBox = false;
                return;
            }

            // Expand by padding on all sides
            _boxMin = min - Vector3.one * _padding;
            _boxMax = max + Vector3.one * _padding;
            _hasValidBox = true;
        }

        /// <summary>
        /// Schedules the Burst job that flags depth cells inside the hand AABB.
        /// Returns the input dependency unchanged if no valid box exists.
        /// </summary>
        public JobHandle Schedule(SurfaceBuffer surfBuf, JobHandle dependency)
        {
            if (!_isInitialized && !TryInit()) return dependency;

            if (!_hasValidBox)
            {
                // No valid hand box — clear all mask flags so nothing is masked
                var clearJob = new ClearMaskJob { IsHandMasked = surfBuf.IsHandMasked };
                return clearJob.Schedule(surfBuf.IsHandMasked.Length, 128, dependency);
            }

            var job = new HandMaskJob
            {
                Hits         = surfBuf.Hits,
                HasDepth     = surfBuf.HasDepth,
                BoxMin       = _boxMin,
                BoxMax       = _boxMax,
                IsHandMasked = surfBuf.IsHandMasked
            };

            return job.Schedule(surfBuf.Hits.Length, 128, dependency);
        }

        public void Dispose()
        {
            if (_jointPositions.IsCreated) _jointPositions.Dispose();
        }

        // ------------------------------------------------------------------
        // INIT
        // ------------------------------------------------------------------

        private bool TryInit()
        {
            var bones = _handSkeleton.Bones;
            if (bones == null || bones.Count == 0) return false;

            _jointCount = bones.Count;
            _jointPositions = new NativeArray<Vector3>(_jointCount, Allocator.Persistent);
            _isInitialized = true;
            return true;
        }

        // ==================================================================
        // BURST JOBS
        // ==================================================================

        /// <summary>
        /// Flags depth cells whose 3D hit position falls inside the padded
        /// hand AABB. Six comparisons per cell — much faster than 28 capsule
        /// checks and covers the full hand volume with no gaps.
        /// </summary>
        [BurstCompile]
        struct HandMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> HasDepth;

            public Vector3 BoxMin;
            public Vector3 BoxMax;

            [WriteOnly] public NativeArray<bool> IsHandMasked;

            public void Execute(int index)
            {
                if (!HasDepth[index])
                {
                    IsHandMasked[index] = false;
                    return;
                }

                Vector3 p = Hits[index];
                IsHandMasked[index] =
                    p.x >= BoxMin.x && p.x <= BoxMax.x &&
                    p.y >= BoxMin.y && p.y <= BoxMax.y &&
                    p.z >= BoxMin.z && p.z <= BoxMax.z;
            }
        }

        /// <summary>
        /// Clears all mask flags when no valid hand box exists (hand not tracked).
        /// </summary>
        [BurstCompile]
        struct ClearMaskJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<bool> IsHandMasked;
            public void Execute(int index) { IsHandMasked[index] = false; }
        }
    }
}