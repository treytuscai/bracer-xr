using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Isolates the forearm patch from the raw depth grid using a two-stage
    /// Seed + Flood algorithm. Owns no memory; reads and writes SurfaceBuffer.
    ///
    /// WHY TWO STAGES?
    /// A single parallel cylinder test would include every depth cell that falls
    /// inside the arm-shaped volume — but depth noise can place background cells
    /// inside the cylinder, and surface cells near the arm's edge may not form a
    /// contiguous patch. The two-stage design solves both:
    ///
    ///   Seed (parallel): tests every cell against the cylinder simultaneously.
    ///   Cells inside are "definitely forearm" and are enqueued as flood seeds.
    ///
    ///   Flood (sequential BFS): grows outward from seeds to 8-connected neighbors
    ///   that are (a) depth-continuous (≤ ConnectivityThreshold apart in 3D) AND
    ///   (b) still inside the cylinder. The connectivity gate rejects isolated noise
    ///   cells that happen to land in the arm volume; the cylinder gate prevents the
    ///   flood from reaching background geometry that is depth-adjacent to the arm.
    ///
    /// CYLINDER GEOMETRY
    /// The cylinder is defined along the wrist→elbow axis with three constraints:
    ///   Radial:  perpendicular distance from axis ≤ MaxRadialDist.
    ///   Axial:   cell must be ≥ MinFromWrist past the wrist (negative = hand side)
    ///            and ≤ MaxFromElbow past the elbow (positive = upper arm side).
    /// MinFromWrist is negative so the cylinder extends slightly toward the hand,
    /// compensating for the wrist bone's position offset from the visible skin edge.
    /// </summary>
    public class SurfaceExtractor
    {
        // ------------------------------------------------------------------
        // CONFIGURATION
        // Public so they can be tuned at runtime via Inspector or test code.
        // Values are squared on each Schedule call so runtime changes take effect
        // without requiring reconstruction.
        // ------------------------------------------------------------------
        /// <summary> Max perpendicular distance from the arm axis (cylinder radius, meters). </summary>
        public float MaxRadialDist;
        /// <summary> Min signed distance along the axis from the wrist (negative = hand side, meters). </summary>
        public float MinFromWrist;
        /// <summary> Max signed distance along the axis past the elbow (meters). </summary>
        public float MaxFromElbow;
        /// <summary> Max 3D distance between adjacent flood cells to count as depth-connected (meters). </summary>
        public float ConnectivityThreshold;

        /// <summary>
        /// Stores cylinder and connectivity parameters. Values are pre-validated by
        /// the caller (ForearmDepthSurface) via Inspector range attributes.
        /// </summary>
        public SurfaceExtractor(
            float maxRadialDist,
            float minFromWrist,
            float maxFromElbow,
            float connectivityThreshold)
        {
            MaxRadialDist         = maxRadialDist;
            MinFromWrist          = minFromWrist;
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
            JobHandle dependency)
        {
            // Clear any leftover indices from the previous frame before seeding.
            buffer.BFSQueue.Clear();

            // Pre-square thresholds once here so the hot-path Execute methods
            // can use sqrMagnitude comparisons without sqrt.
            float maxRSq = MaxRadialDist         * MaxRadialDist;
            float connSq = ConnectivityThreshold * ConnectivityThreshold;

            var seedJob = new SeedFromAxisJob
            {
                Hits           = buffer.Hits,
                HasDepth       = buffer.HasDepth,
                Kept           = buffer.IsSurface,
                IsHandMasked   = buffer.IsHandMasked,
                BFSQueueWriter = buffer.BFSQueue.AsParallelWriter(),
                WristPos       = wristPos,
                ElbowPos       = elbowPos,
                Axis           = axis,
                MaxRSq         = maxRSq,
                MinFromWrist   = MinFromWrist,
                MaxFromElbow   = MaxFromElbow
            };
            JobHandle seedHandle = seedJob.Schedule(rows * cols, 64, dependency);

            var floodJob = new FloodFromSeedsJob
            {
                BFSQueue     = buffer.BFSQueue,
                Hits         = buffer.Hits,
                HasDepth     = buffer.HasDepth,
                Kept         = buffer.IsSurface,
                IsHandMasked = buffer.IsHandMasked,
                Cols         = cols,
                Rows         = rows,
                WristPos     = wristPos,
                ElbowPos     = elbowPos,
                Axis         = axis,
                ConnSq       = connSq,
                MaxRSq       = maxRSq,
                MinFromWrist = MinFromWrist,
                MaxFromElbow = MaxFromElbow
            };

            return floodJob.Schedule(seedHandle);
        }

        // --------------------------------------------------------
        // SEED JOB — runs in parallel across all grid cells
        // --------------------------------------------------------

        /// <summary>
        /// Tests every depth cell against the arm cylinder in parallel and enqueues
        /// cells that pass all three geometry constraints as flood seeds.
        /// Runs as IJobParallelFor because each cell is independent — no cell's result
        /// depends on another cell's result during this stage.
        /// </summary>
        [BurstCompile]
        struct SeedFromAxisJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    HasDepth;
            [ReadOnly] public NativeArray<bool>    IsHandMasked;
            public NativeArray<bool> Kept;

            public NativeQueue<int>.ParallelWriter BFSQueueWriter;

            public Vector3 WristPos;
            public Vector3 ElbowPos;
            public Vector3 Axis;
            public float   MaxRSq;
            public float   MinFromWrist;
            public float   MaxFromElbow;

            public void Execute(int index)
            {
                if (!HasDepth[index] || IsHandMasked[index]) return;

                Vector3 p = Hits[index];

                // AXIAL CHECK 1 — signed distance along the arm axis from the wrist.
                // Dot(p - WristPos, Axis): positive = toward elbow, negative = toward hand.
                // MinFromWrist is negative, so this accepts cells slightly behind the wrist.
                float fromWrist = Vector3.Dot(p - WristPos, Axis);
                if (fromWrist < MinFromWrist) return;

                // AXIAL CHECK 2 — signed distance along the arm axis past the elbow.
                // Dot(p - ElbowPos, Axis): positive = past the elbow toward upper arm.
                float fromElbow = Vector3.Dot(p - ElbowPos, Axis);
                if (fromElbow > MaxFromElbow) return;

                // RADIAL CHECK — perpendicular distance from the arm axis.
                // |Cross(v, Axis)| = |v| * sin(θ) = perpendicular distance to the axis line.
                // (Axis is a unit vector so |Cross(v, Axis)| reduces to the perpendicular component.)
                float radialSq = Vector3.Cross(p - ElbowPos, Axis).sqrMagnitude;
                if (radialSq > MaxRSq) return;

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
            [ReadOnly] public NativeArray<bool>    IsHandMasked;
            public NativeArray<bool> Kept;

            public int Rows;
            public int Cols;

            public Vector3 WristPos;
            public Vector3 ElbowPos;
            public Vector3 Axis;
            public float   ConnSq;
            public float   MaxRSq;
            public float   MinFromWrist;
            public float   MaxFromElbow;

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

                        // Skip cells already kept, missing depth, or inside the hand mask.
                        if (Kept[nIdx] || !HasDepth[nIdx] || IsHandMasked[nIdx]) continue;

                        // CONNECTIVITY GATE — reject if the neighbor is too far in 3D.
                        // This prevents the flood from bridging depth discontinuities
                        // (e.g. the arm transitioning to a nearby table surface).
                        if ((Hits[nIdx] - currentHit).sqrMagnitude > ConnSq) continue;

                        Vector3 neighborHit = Hits[nIdx];

                        // CYLINDER RE-CHECK — the flood still enforces all three geometry
                        // constraints so it cannot grow outside the arm volume even when
                        // depth-connected to background geometry at the arm's edge.
                        float radialSq = Vector3.Cross(neighborHit - ElbowPos, Axis).sqrMagnitude;
                        if (radialSq > MaxRSq) continue;

                        float fromWrist = Vector3.Dot(neighborHit - WristPos, Axis);
                        if (fromWrist < MinFromWrist) continue;

                        float fromElbow = Vector3.Dot(neighborHit - ElbowPos, Axis);
                        if (fromElbow > MaxFromElbow) continue;

                        Kept[nIdx] = true;
                        BFSQueue.Enqueue(nIdx);
                    }
                }
            }
        }
    }
}
