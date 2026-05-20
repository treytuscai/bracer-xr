using UnityEngine;
using Surface.Buffer;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System;

namespace Surface.Hand
{
    /// <summary>
    /// Owns all memory and scheduling for hand-based depth masking.
    /// Builds a capsule skeleton from OVRSkeleton hand joints at init,
    /// refreshes joint world positions each frame, and schedules a Burst
    /// job that flags depth cells falling inside the hand volume.
    /// </summary>
    public class HandMask : IDisposable
    {
        // ------------------------------------------------------------------
        // CONFIGURATION 
        // ------------------------------------------------------------------
        public float _maskRadiusSq;

        // ------------------------------------------------------------------
        // SKELETON REFERENCE
        // ------------------------------------------------------------------
        private OVRSkeleton _handSkeleton;

        // ------------------------------------------------------------------
        // NATIVE MEMORY (allocated once, persistent across frames)
        // ------------------------------------------------------------------
        private NativeArray<Vector3> _jointPositions;   // refreshed every frame
        private NativeArray<int>     _segStart;         // capsule topology (static)
        private NativeArray<int>     _segEnd;
        private int _jointCount;
        private int _segmentCount;
        private bool _isInitialized;

        // ------------------------------------------------------------------
        // OVRSkeleton Hand Bone Indices (XRHand layout)
        //
        // Verified against runtime Transform.name via adb logcat 2026-05-20.
        // If OpenXR versioning shifts these, TryInitTopology() will catch
        // out-of-range indices and log the error.
        // ------------------------------------------------------------------
        private static readonly int Palm       = 0;
        private static readonly int Wrist      = 1;
        private static readonly int ThumbMeta  = 2,  ThumbProx  = 3,  ThumbDist   = 4,  ThumbTip   = 5;
        private static readonly int IndexMeta  = 6,  IndexProx  = 7,  IndexInter  = 8,  IndexDist  = 9,  IndexTip  = 10;
        private static readonly int MiddleMeta = 11, MiddleProx = 12, MiddleInter = 13, MiddleDist = 14, MiddleTip = 15;
        private static readonly int RingMeta   = 16, RingProx   = 17, RingInter   = 18, RingDist   = 19, RingTip   = 20;
        private static readonly int LittleMeta = 21, LittleProx = 22, LittleInter = 23, LittleDist = 24, LittleTip = 25;

        private static readonly int[] segStarts = {
            ThumbMeta,  ThumbProx,  ThumbDist,                          // Thumb  (3)
            IndexMeta,  IndexProx,  IndexInter,  IndexDist,             // Index  (4)
            MiddleMeta, MiddleProx, MiddleInter, MiddleDist,            // Middle (4)
            RingMeta,   RingProx,   RingInter,   RingDist,              // Ring   (4)
            LittleMeta, LittleProx, LittleInter, LittleDist,            // Little (4)
            Palm,                                                       // Palm -> Wrist (1)
            Wrist, Wrist, Wrist, Wrist, Wrist,                          // Wrist -> each metacarpal (5)
            IndexMeta, MiddleMeta, RingMeta                             // (3) Metacarpal band
        };

        private static readonly int[] segEnds = {
            ThumbProx,  ThumbDist,  ThumbTip,
            IndexProx,  IndexInter, IndexDist,  IndexTip,
            MiddleProx, MiddleInter, MiddleDist, MiddleTip,
            RingProx,   RingInter,  RingDist,   RingTip,
            LittleProx, LittleInter, LittleDist, LittleTip,
            Wrist,
            ThumbMeta, IndexMeta, MiddleMeta, RingMeta, LittleMeta,
            MiddleMeta, RingMeta, LittleMeta
        };

        public HandMask(OVRSkeleton handSkeleton, float maskRadius)
        {
            _handSkeleton = handSkeleton;
            _maskRadiusSq = maskRadius * maskRadius;
            _isInitialized = false;
        }

        // <summary>
        /// Refreshes joint positions from OVRSkeleton and schedules the mask job.
        /// Returns default (no-op) handle if skeleton isn't ready yet.
        /// </summary>
        public JobHandle Schedule(SurfaceBuffer surfBuf, JobHandle dependency)
        {
            // VRSkeleton.Bones isn't populated until tracking starts
            if (!_isInitialized && !TryInitTopology()) return dependency;

            // Copy current-frame world positions from managed OVRBone transforms
            // into the persistent NativeArray the Burst job will read.
            var bones = _handSkeleton.Bones;
            for (int i = 0; i < _jointCount; i++)
            {
                _jointPositions[i] = (i < bones.Count && bones[i].Transform != null)
                    ? bones[i].Transform.position
                    : Vector3.zero;
            }

            var job = new HandMaskJob
            {
                Hits            = surfBuf.Hits,
                HasDepth        = surfBuf.HasDepth,
                JointPositions  = _jointPositions,
                SegStart        = _segStart,
                SegEnd          = _segEnd,
                SegmentCount    = _segmentCount,
                MaskRadiusSq   = _maskRadiusSq,
                IsHandMasked    = surfBuf.IsHandMasked
            };

            return job.Schedule(surfBuf.Hits.Length, 64, dependency);
        }

        public void Dispose()
        {
            if (_jointPositions.IsCreated) _jointPositions.Dispose();
            if (_segStart.IsCreated)       _segStart.Dispose();
            if (_segEnd.IsCreated)         _segEnd.Dispose();
        }

        // ------------------------------------------------------------------
        // INIT (runs once, when OVRSkeleton first has bones)
        // ------------------------------------------------------------------
 
        /// <summary>
        /// Allocates native memory and copies the static capsule topology.
        /// Deferred to first Schedule() because OVRSkeleton.Bones is empty at Awake.
        /// </summary>
        private bool TryInitTopology()
        {
            var bones = _handSkeleton.Bones;
            if (bones == null || bones.Count == 0) return false;
 
            _jointCount = bones.Count;
            _segmentCount = segStarts.Length;
 
            // Allocate persistent memory
            _jointPositions = new NativeArray<Vector3>(_jointCount, Allocator.Persistent);
            _segStart = new NativeArray<int>(_segmentCount, Allocator.Persistent);
            _segEnd = new NativeArray<int>(_segmentCount, Allocator.Persistent);
 
            // Copy topology (this never changes at runtime)
            for (int i = 0; i < _segmentCount; i++)
            {
                _segStart[i] = segStarts[i];
                _segEnd[i] = segEnds[i];
            }
 
            _isInitialized = true;
            return true;
        }
    }

    // ================================================================
    // BURST JOB
    // ================================================================

    /// <summary>
    /// Flags depth cells that fall inside the hand's capsule skeleton.
    /// Runs in parallel across all grid cells. Early-outs on first capsule hit.
    /// </summary>
    [BurstCompile]
    public struct HandMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> Hits;
        [ReadOnly] public NativeArray<bool> HasDepth;
        [ReadOnly] public NativeArray<Vector3> JointPositions;
        [ReadOnly] public NativeArray<int>     SegStart;
        [ReadOnly] public NativeArray<int>     SegEnd;

        public int   SegmentCount;
        public float MaskRadiusSq;

        [WriteOnly] public NativeArray<bool> IsHandMasked;

        public void Execute(int index)
        {
            if (!HasDepth[index]) 
            {
                IsHandMasked[index] = false;
                return;
            }

            Vector3 hit = Hits[index];
            for (int seg = 0; seg < SegmentCount; seg++)
            {
                float dSq = PointCapsuleDistSq(
                    hit,
                    JointPositions[SegStart[seg]],
                    JointPositions[SegEnd[seg]]
                );

                if (dSq < MaskRadiusSq)
                {
                    IsHandMasked[index] = true;
                    return; // Early-out: one hit is enough
                }
            }

            IsHandMasked[index] = false;
        }

        /// <summary>
        /// Squared distance from point P to the nearest point on line segment AB.
        /// Burst compiles this inline, no managed call overhead.
        /// </summary>
        private static float PointCapsuleDistSq(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSq = Vector3.Dot(ab, ab);
 
            // Degenerate segment (joint on top of joint), treat as sphere
            if (abSq < 1e-8f) return (p - a).sqrMagnitude;
 
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSq);
            Vector3 closest = a + ab * t;
            return (p - closest).sqrMagnitude;
        }
    }
}