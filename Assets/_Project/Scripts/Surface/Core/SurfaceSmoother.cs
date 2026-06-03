using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Smooths the reconstructed forearm surface each frame.
    ///
    /// INTERIOR denoising now happens on the GPU (bilateral depth blur in MetaDepthCopy), so the
    /// optional world-space Laplacian (SmoothSurfaceJob) defaults OFF via SmoothPasses = 0. It is
    /// kept only as a quick A/B fallback.
    ///
    /// BOUNDARY smoothing reduces the staircase at the mesh edge. It is a parallel, Burst
    /// IJobParallelFor pass — NOT the old sequential contour tracer, which fragmented the dense,
    /// noisy edge into short chains and skipped them (leaving unsmoothed segments that flickered
    /// frame to frame). Instead: one pass marks boundary cells, then each boundary cell is averaged
    /// with nearby BOUNDARY cells only. Averaging boundary-with-boundary (not interior) smooths the
    /// silhouette along the edge without pulling the patch inward (no shrink), and every boundary
    /// cell is smoothed every frame (no fragmentation, no skips).
    /// </summary>
    public class SurfaceSmoother
    {
        /// <summary> Legacy world-space Laplacian passes. 0 = off (the GPU bilateral pass denoises the interior). </summary>
        public int SmoothPasses;
        /// <summary> Number of boundary smoothing passes. 0 = no edge smoothing. </summary>
        public int EdgeSmoothPasses;
        /// <summary> Neighborhood half-width (in cells) averaged per boundary pass. </summary>
        public int EdgeWindowRadius;

        public SurfaceSmoother(int smoothPasses, int edgeSmoothPasses, int edgeWindowRadius)
        {
            SmoothPasses     = smoothPasses;
            EdgeSmoothPasses = edgeSmoothPasses;
            EdgeWindowRadius = edgeWindowRadius;
        }

        /// <summary>
        /// Runs the smoothing pipeline and blocks until complete. After this returns, buffer.Hits
        /// holds the final positions. Passes ping-pong buffer.Hits and buffer.Smoothed and swap the
        /// references after each pass; the swap is safe because each job captured its NativeArray
        /// pointers at Schedule time.
        /// </summary>
        public void Schedule(SurfaceBuffer buffer, int rows, int cols, JobHandle dependency)
        {
            JobHandle handle = dependency;

            // Optional legacy world-space Laplacian over all surface cells (off when SmoothPasses = 0).
            for (int i = 0; i < SmoothPasses; i++)
            {
                var job = new SmoothSurfaceJob
                {
                    Hits       = buffer.Hits,
                    IsSurface  = buffer.IsSurface,
                    Smoothed   = buffer.Smoothed,
                    GridHeight = rows,
                    GridWidth  = cols
                };
                handle = job.Schedule(rows * cols, 64, handle);
                var t = buffer.Hits; buffer.Hits = buffer.Smoothed; buffer.Smoothed = t;
            }

            // Boundary smoothing (parallel, Burst): mark boundary cells once, then average each
            // boundary cell with nearby boundary cells over EdgeSmoothPasses ping-pong passes.
            if (EdgeSmoothPasses > 0 && EdgeWindowRadius > 0)
            {
                var maskJob = new BoundaryMaskJob
                {
                    IsSurface    = buffer.IsSurface,
                    BoundaryMask = buffer.BoundaryMask,
                    GridHeight   = rows,
                    GridWidth    = cols
                };
                handle = maskJob.Schedule(rows * cols, 64, handle);

                for (int p = 0; p < EdgeSmoothPasses; p++)
                {
                    var job = new BoundarySmoothJob
                    {
                        Hits         = buffer.Hits,
                        BoundaryMask = buffer.BoundaryMask,
                        Smoothed     = buffer.Smoothed,
                        GridHeight   = rows,
                        GridWidth    = cols,
                        Radius       = EdgeWindowRadius
                    };
                    handle = job.Schedule(rows * cols, 64, handle);
                    var t = buffer.Hits; buffer.Hits = buffer.Smoothed; buffer.Smoothed = t;
                }
            }

            // MeshGenerator reads buffer.Hits on the main thread next, so finish the chain.
            handle.Complete();
        }

        // ==================================================================
        // BURST JOBS
        // ==================================================================

        /// <summary>
        /// Legacy interior Laplacian: each surface cell is replaced by the centroid of itself and
        /// its surface neighbors (self-weight 1). Off by default (SmoothPasses = 0). Non-surface
        /// cells pass through verbatim to keep the ping-pong buffer consistent across the swap.
        /// </summary>
        [BurstCompile]
        struct SmoothSurfaceJob : IJobParallelFor
        {
            [ReadOnly]  public NativeArray<Vector3> Hits;
            [ReadOnly]  public NativeArray<bool>    IsSurface;
            [WriteOnly] public NativeArray<Vector3> Smoothed;
            public int GridHeight;
            public int GridWidth;

            public void Execute(int flatIndex)
            {
                if (!IsSurface[flatIndex])
                {
                    Smoothed[flatIndex] = Hits[flatIndex];
                    return;
                }

                int row = flatIndex / GridWidth;
                int col = flatIndex % GridWidth;

                Vector3 positionSum = Hits[flatIndex];
                float   totalWeight = 1f;

                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = row + dr;
                        int nc = col + dc;
                        if (nr < 0 || nr >= GridHeight || nc < 0 || nc >= GridWidth) continue;
                        int nFlat = nr * GridWidth + nc;
                        if (!IsSurface[nFlat]) continue;
                        positionSum += Hits[nFlat];
                        totalWeight += 1f;
                    }
                }

                Smoothed[flatIndex] = positionSum / totalWeight;
            }
        }

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
