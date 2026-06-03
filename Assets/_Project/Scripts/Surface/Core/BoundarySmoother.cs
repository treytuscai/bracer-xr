using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Smooths the staircase artifact at the forearm mesh edge.
    ///
    /// The surface INTERIOR is denoised on the GPU (bilateral depth blur in MetaDepthCopy), so this
    /// stage only touches the boundary. It is fully parallel/Burst — not a sequential contour
    /// tracer (which fragmented the dense, noisy edge into short chains and skipped them, leaving
    /// unsmoothed segments that flickered). Instead: one pass marks boundary cells, then each
    /// boundary cell is averaged with nearby BOUNDARY cells only. Averaging boundary-with-boundary
    /// (not interior) smooths the silhouette along the edge without pulling the patch inward (no
    /// shrink), and every boundary cell is smoothed every frame (no fragmentation, no skips).
    /// </summary>
    public class BoundarySmoother
    {
        /// <summary> Number of boundary smoothing passes. 0 = no edge smoothing. </summary>
        public int Passes;
        /// <summary> Neighborhood half-width (in cells) averaged per pass. </summary>
        public int WindowRadius;

        public BoundarySmoother(int passes, int windowRadius)
        {
            Passes       = passes;
            WindowRadius = windowRadius;
        }

        /// <summary>
        /// Marks boundary cells, then runs Passes ping-pong smoothing passes, blocking until
        /// complete (MeshGenerator reads buffer.Hits on the main thread next). Each pass reads
        /// buffer.Hits and writes buffer.Smoothed, then the references are swapped — safe because
        /// each job captured its NativeArray pointers at Schedule time.
        /// </summary>
        public void Schedule(SurfaceBuffer buffer, int rows, int cols, JobHandle dependency)
        {
            JobHandle handle = dependency;

            if (Passes > 0 && WindowRadius > 0)
            {
                var maskJob = new BoundaryMaskJob
                {
                    IsSurface    = buffer.IsSurface,
                    BoundaryMask = buffer.BoundaryMask,
                    GridHeight   = rows,
                    GridWidth    = cols
                };
                handle = maskJob.Schedule(rows * cols, 64, handle);

                for (int p = 0; p < Passes; p++)
                {
                    var job = new BoundarySmoothJob
                    {
                        Hits         = buffer.Hits,
                        BoundaryMask = buffer.BoundaryMask,
                        Smoothed     = buffer.Smoothed,
                        GridHeight   = rows,
                        GridWidth    = cols,
                        Radius       = WindowRadius
                    };
                    handle = job.Schedule(rows * cols, 64, handle);
                    // Swap so the next pass reads this pass's output (safe: the job already
                    // captured its NativeArray pointers at Schedule time).
                    (buffer.Hits, buffer.Smoothed) = (buffer.Smoothed, buffer.Hits);
                }
            }

            // MeshGenerator reads buffer.Hits on the main thread next, so finish the chain.
            handle.Complete();
        }

        // ==================================================================
        // BURST JOBS
        // ==================================================================

        /// <summary>
        /// Marks each cell as a boundary cell: a surface cell with at least one 8-connected
        /// neighbor that is off-surface or off-grid. Computed once (IsSurface is stable across the
        /// smoothing passes) and reused by every BoundarySmoothJob pass.
        /// </summary>
        [BurstCompile]
        struct BoundaryMaskJob : IJobParallelFor
        {
            [ReadOnly]  public NativeArray<bool> IsSurface;
            [WriteOnly] public NativeArray<bool> BoundaryMask;
            public int GridHeight;
            public int GridWidth;

            public void Execute(int flatIndex)
            {
                if (!IsSurface[flatIndex])
                {
                    BoundaryMask[flatIndex] = false;
                    return;
                }

                int row = flatIndex / GridWidth;
                int col = flatIndex % GridWidth;

                bool boundary = false;
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = row + dr;
                        int nc = col + dc;
                        if (nr < 0 || nr >= GridHeight || nc < 0 || nc >= GridWidth ||
                            !IsSurface[nr * GridWidth + nc])
                        {
                            boundary = true;
                        }
                    }
                }

                BoundaryMask[flatIndex] = boundary;
            }
        }

        /// <summary>
        /// One boundary smoothing pass: each boundary cell becomes the average of itself and the
        /// boundary cells within Radius. Averaging only boundary cells (not interior) smooths the
        /// silhouette along the edge without pulling the patch inward. Non-boundary cells pass
        /// through verbatim so the ping-pong buffer stays consistent across the swap.
        /// </summary>
        [BurstCompile]
        struct BoundarySmoothJob : IJobParallelFor
        {
            [ReadOnly]  public NativeArray<Vector3> Hits;
            [ReadOnly]  public NativeArray<bool>    BoundaryMask;
            [WriteOnly] public NativeArray<Vector3> Smoothed;
            public int GridHeight;
            public int GridWidth;
            public int Radius;

            public void Execute(int flatIndex)
            {
                if (!BoundaryMask[flatIndex])
                {
                    Smoothed[flatIndex] = Hits[flatIndex];
                    return;
                }

                int row = flatIndex / GridWidth;
                int col = flatIndex % GridWidth;

                Vector3 sum    = Hits[flatIndex];
                float   weight = 1f;

                for (int dr = -Radius; dr <= Radius; dr++)
                {
                    for (int dc = -Radius; dc <= Radius; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = row + dr;
                        int nc = col + dc;
                        if (nr < 0 || nr >= GridHeight || nc < 0 || nc >= GridWidth) continue;
                        int n = nr * GridWidth + nc;
                        if (!BoundaryMask[n]) continue;   // average along the boundary only
                        sum    += Hits[n];
                        weight += 1f;
                    }
                }

                Smoothed[flatIndex] = sum / weight;
            }
        }
    }
}
