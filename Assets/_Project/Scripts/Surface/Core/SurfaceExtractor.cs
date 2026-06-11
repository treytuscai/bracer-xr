// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Isolates the forearm-plus-palm patch from the raw depth grid with a two-stage Seed + Flood.
    /// Owns no memory; reads and writes SurfaceBuffer.
    ///
    /// ACCEPT VOLUME: the union of two flat-capped cylinders — forearm (wrist -> past elbow, on the
    /// arm axis) and palm (wrist -> middle-finger MCP, on its own axis so it tracks a waved wrist).
    /// Flat caps (not capsule rounding) keep the forearm out of the hand and stop the palm at the
    /// knuckles, excluding the fingers; the thumb is kept. Each cylinder overruns the wrist by
    /// WristOverlap so their caps meet without a notch. No hand tracking -> forearm-only.
    ///
    /// WHY TWO STAGES: a single test would admit background cells depth noise places inside the
    /// volume, and wouldn't guarantee a contiguous patch. Seed (parallel) marks cells inside the
    /// tight radius (SeedRadialDist) as flood seeds; Flood (sequential BFS) grows to 8-connected
    /// neighbors that are depth-continuous (<= ConnectivityThreshold) and inside the wider radius
    /// (FloodRadialDist). The palm is seeded directly so a sharp wrist crease can't starve it.
    /// </summary>
    public class SurfaceExtractor
    {
        // ------------------------------------------------------------------
        // CONFIGURATION
        // ------------------------------------------------------------------
        /// <summary> Max radial distance for seed cells — tight inner cylinder that must be confident forearm (meters). </summary>
        public float SeedRadialDist;
        /// <summary> Max radial distance for flood cells — wider wall that stops runaway growth (meters). </summary>
        public float FloodRadialDist;
        /// <summary> How far past the elbow the forearm cylinder extends along the axis (meters). The
        /// wrist-side cap is flat (plus a small overlap), so the palm cylinder takes over there. </summary>
        public float MaxFromElbow;
        /// <summary> Max 3D distance between adjacent flood cells to count as depth-connected (meters). </summary>
        public float ConnectivityThreshold;

        // How far each cylinder overruns the wrist (m), bridging the notch where the two flat caps
        // meet at an angle when the wrist is waved.
        private const float WristOverlap = 0.02f;

        /// <summary>
        /// Stores cylinder and connectivity parameters. Values are pre-validated by
        /// the caller (ForearmDepthSurface) via Inspector range attributes.
        /// </summary>
        public SurfaceExtractor(
            float seedRadialDist,
            float floodRadialDist,
            float maxFromElbow,
            float connectivityThreshold)
        {
            SeedRadialDist        = seedRadialDist;
            FloodRadialDist       = floodRadialDist;
            MaxFromElbow          = maxFromElbow;
            ConnectivityThreshold = connectivityThreshold;
        }

        /// <summary>
        /// Clears the BFS queue, schedules SeedFromAxisJob (parallel), then chains
        /// FloodFromSeedsJob (single-thread BFS) as a dependency.
        /// Returns the flood job's handle for the caller to chain smoothing onto.
        /// </summary>
        public JobHandle Schedule(
            SurfaceBuffer buffer,
            int rows, int cols,
            Vector3 wristPos, Vector3 elbowPos, Vector3 axis,
            Vector3 palmCapPos, bool hasPalm,
            JobHandle dependency)
        {
            // Clear any leftover indices from the previous frame before seeding.
            buffer.BFSQueue.Clear();

            // Pre-square thresholds once here so the hot-path Execute methods
            // can use sqrMagnitude comparisons without sqrt.
            float seedRSq  = SeedRadialDist        * SeedRadialDist;
            float floodRSq = FloodRadialDist       * FloodRadialDist;
            float connSq   = ConnectivityThreshold * ConnectivityThreshold;

            // Forearm cylinder: along the arm axis from the wrist (axial 0) to past the elbow.
            // axis is unit and points wrist->elbow, so Dot(elbow-wrist, axis) is the bone length.
            float forearmMaxAxial = Vector3.Dot(elbowPos - wristPos, axis) + MaxFromElbow;

            // Palm cylinder: along its own wrist->MCP axis from the wrist (axial 0) to the MCP (palmLen).
            Vector3 palmVec  = palmCapPos - wristPos;
            float   palmLen  = palmVec.magnitude;
            bool    effPalm  = hasPalm && palmLen > 1e-6f;
            Vector3 palmAxis = effPalm ? palmVec / palmLen : axis;

            var seedJob = new SeedFromAxisJob
            {
                Hits            = buffer.Hits,
                HasDepth        = buffer.HasDepth,
                Kept            = buffer.IsSurface,
                BFSQueueWriter  = buffer.BFSQueue.AsParallelWriter(),
                WristPos        = wristPos,
                ForearmAxis     = axis,
                ForearmMaxAxial = forearmMaxAxial,
                PalmAxis        = palmAxis,
                PalmLen         = palmLen,
                HasPalm         = effPalm,
                RSq             = seedRSq
            };
            JobHandle seedHandle = seedJob.Schedule(rows * cols, 64, dependency);

            var floodJob = new FloodFromSeedsJob
            {
                BFSQueue        = buffer.BFSQueue,
                Hits            = buffer.Hits,
                HasDepth        = buffer.HasDepth,
                Kept            = buffer.IsSurface,
                Cols            = cols,
                Rows            = rows,
                WristPos        = wristPos,
                ForearmAxis     = axis,
                ForearmMaxAxial = forearmMaxAxial,
                PalmAxis        = palmAxis,
                PalmLen         = palmLen,
                HasPalm         = effPalm,
                ConnSq          = connSq,
                RSq             = floodRSq
            };

            return floodJob.Schedule(seedHandle);
        }

        // --------------------------------------------------------
        // CYLINDER CONTAINMENT (shared by seed + flood; Burst-inlined pure math)
        // --------------------------------------------------------

        /// <summary>
        /// True if p is inside the cylinder: axial position along axisDir (from a) within
        /// [minAxial, maxAxial] and perpendicular distance to the axis within sqrt(rSq). axisDir is unit.
        /// </summary>
        static bool InCylinder(Vector3 p, Vector3 a, Vector3 axisDir, float minAxial, float maxAxial, float rSq)
        {
            Vector3 rel   = p - a;
            float   axial = Vector3.Dot(rel, axisDir);
            if (axial < minAxial || axial > maxAxial) return false;
            Vector3 perp = rel - axial * axisDir;       // component perpendicular to the axis
            return Vector3.Dot(perp, perp) <= rSq;
        }

        /// <summary>
        /// True if p is inside the forearm cylinder, or the palm cylinder when the hand is tracked.
        /// Both share the stage radius and overrun the wrist by WristOverlap.
        /// </summary>
        static bool InArmVolume(
            Vector3 p, Vector3 wrist,
            Vector3 forearmAxis, float forearmMaxAxial,
            Vector3 palmAxis, float palmLen, bool hasPalm,
            float rSq)
        {
            if (InCylinder(p, wrist, forearmAxis, -WristOverlap, forearmMaxAxial, rSq)) return true;
            if (hasPalm && InCylinder(p, wrist, palmAxis, -WristOverlap, palmLen, rSq)) return true;
            return false;
        }

        // --------------------------------------------------------
        // SEED JOB — runs in parallel across all grid cells
        // --------------------------------------------------------

        /// <summary>
        /// Tests every depth cell against the arm cylinder(s) in parallel and enqueues
        /// cells inside the volume as flood seeds. Runs as IJobParallelFor because each
        /// cell is independent — no cell's result depends on another during this stage.
        /// </summary>
        [BurstCompile]
        struct SeedFromAxisJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    HasDepth;
            public NativeArray<bool> Kept;

            public NativeQueue<int>.ParallelWriter BFSQueueWriter;

            public Vector3 WristPos;
            public Vector3 ForearmAxis;
            public float   ForearmMaxAxial;
            public Vector3 PalmAxis;
            public float   PalmLen;
            public bool    HasPalm;
            public float   RSq;

            public void Execute(int index)
            {
                if (!HasDepth[index]) return;

                Vector3 p = Hits[index];
                if (!InArmVolume(p, WristPos, ForearmAxis, ForearmMaxAxial,
                                 PalmAxis, PalmLen, HasPalm, RSq)) return;

                Kept[index] = true;
                BFSQueueWriter.Enqueue(index);
            }
        }

        // --------------------------------------------------------
        // FLOOD JOB — runs on a single background worker thread
        // --------------------------------------------------------

        /// <summary>
        /// BFS from the seed cells, expanding to 8-connected grid neighbors that
        /// pass both the depth-connectivity test and the arm cylinder constraints.
        /// Must run as IJob (single-thread) because BFS is inherently sequential:
        /// each dequeue step depends on the queue state left by the previous step.
        /// </summary>
        [BurstCompile]
        struct FloodFromSeedsJob : IJob
        {
            public NativeQueue<int>  BFSQueue;
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    HasDepth;
            public NativeArray<bool> Kept;

            public int Rows;
            public int Cols;

            public Vector3 WristPos;
            public Vector3 ForearmAxis;
            public float   ForearmMaxAxial;
            public Vector3 PalmAxis;
            public float   PalmLen;
            public bool    HasPalm;
            public float   ConnSq;
            public float   RSq;

            public void Execute()
            {
                // 8-way (Moore) neighbor offsets for the pixel grid.
                // Allocator.Temp is valid inside Burst jobs and is freed when the job finishes.
                NativeArray<int> dRow = new NativeArray<int>(8, Allocator.Temp)
                    { [0]=-1, [1]=-1, [2]=-1, [3]=0, [4]=0, [5]=1, [6]=1, [7]=1 };
                NativeArray<int> dCol = new NativeArray<int>(8, Allocator.Temp)
                    { [0]=-1, [1]=0, [2]=1, [3]=-1, [4]=1, [5]=-1, [6]=0, [7]=1 };

                while (BFSQueue.TryDequeue(out int idx))
                {
                    int r = idx / Cols;
                    int c = idx % Cols;
                    Vector3 currentHit = Hits[idx];

                    for (int n = 0; n < 8; n++)
                    {
                        int nr = r + dRow[n];
                        int nc = c + dCol[n];

                        if (nr < 0 || nc < 0 || nr >= Rows || nc >= Cols) continue;

                        int nIdx = nr * Cols + nc;

                        // Skip cells already kept or with no depth (incl. hand pixels carved upstream).
                        if (Kept[nIdx] || !HasDepth[nIdx]) continue;

                        Vector3 neighborHit = Hits[nIdx];

                        // CONNECTIVITY GATE — reject neighbors too far in 3D, so the flood can't bridge
                        // a depth discontinuity (e.g. arm -> nearby table).
                        if ((neighborHit - currentHit).sqrMagnitude > ConnSq) continue;

                        // CYLINDER RE-CHECK — re-apply the geometry gate so the flood can't grow outside
                        // the arm/palm volume even when depth-connected to background at the edge.
                        if (!InArmVolume(neighborHit, WristPos, ForearmAxis, ForearmMaxAxial,
                                         PalmAxis, PalmLen, HasPalm, RSq)) continue;

                        Kept[nIdx] = true;
                        BFSQueue.Enqueue(nIdx);
                    }
                }
            }
        }
    }
}
