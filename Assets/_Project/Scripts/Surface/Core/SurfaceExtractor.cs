using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Isolates the forearm patch from the raw depth grid with a two-stage Seed + Flood. Owns no
    /// memory; reads and writes SurfaceBuffer.
    ///
    /// WHY TWO STAGES: a single parallel cylinder test would admit background cells that depth noise
    /// places inside the arm volume, and wouldn't guarantee a contiguous patch. Instead, Seed
    /// (parallel) marks cells confidently inside a tight inner cylinder (SeedRadialDist) as flood
    /// seeds; Flood (sequential BFS) grows from them to 8-connected neighbors that are both
    /// depth-continuous (≤ ConnectivityThreshold in 3D) and still inside a wider outer cylinder
    /// (FloodRadialDist) — the connectivity gate drops isolated noise, the cylinder gate stops the
    /// flood reaching adjacent background.
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
        /// <summary> Min signed axial distance from the wrist (negative = hand side; negative value
        /// extends the cylinder toward the hand to offset the wrist bone's position). </summary>
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
            float seedRadialDist,
            float floodRadialDist,
            float minFromWrist,
            float maxFromElbow,
            float connectivityThreshold)
        {
            SeedRadialDist        = seedRadialDist;
            FloodRadialDist       = floodRadialDist;
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
            float seedRSq  = SeedRadialDist       * SeedRadialDist;
            float floodRSq = FloodRadialDist       * FloodRadialDist;
            float connSq   = ConnectivityThreshold * ConnectivityThreshold;

            var seedJob = new SeedFromAxisJob
            {
                Hits           = buffer.Hits,
                HasDepth       = buffer.HasDepth,
                Kept           = buffer.IsSurface,
                BFSQueueWriter = buffer.BFSQueue.AsParallelWriter(),
                WristPos       = wristPos,
                ElbowPos       = elbowPos,
                Axis           = axis,
                MaxRSq         = seedRSq,
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
                Cols         = cols,
                Rows         = rows,
                WristPos     = wristPos,
                ElbowPos     = elbowPos,
                Axis         = axis,
                ConnSq       = connSq,
                MaxRSq       = floodRSq,
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
                if (!HasDepth[index]) return;

                Vector3 p = Hits[index];

                // AXIAL CHECK — signed distance along the axis: Dot(p - bone, Axis), positive toward
                // elbow. Accept [MinFromWrist (negative, hand side) .. MaxFromElbow past the elbow].
                float fromWrist = Vector3.Dot(p - WristPos, Axis);
                if (fromWrist < MinFromWrist) return;
                float fromElbow = Vector3.Dot(p - ElbowPos, Axis);
                if (fromElbow > MaxFromElbow) return;

                // RADIAL CHECK — |Cross(v, Axis)| is the perpendicular distance to the axis (Axis is unit).
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

                        // Skip cells already kept or with no depth (incl. hand pixels masked by MetaDepthCopy).
                        if (Kept[nIdx] || !HasDepth[nIdx]) continue;

                        Vector3 neighborHit = Hits[nIdx];

                        // CONNECTIVITY GATE — reject neighbors too far in 3D, so the flood can't bridge
                        // a depth discontinuity (e.g. arm -> nearby table).
                        if ((neighborHit - currentHit).sqrMagnitude > ConnSq) continue;

                        // CYLINDER RE-CHECK — re-apply the geometry gates so the flood can't grow
                        // outside the arm volume even when depth-connected to background at the edge.
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
