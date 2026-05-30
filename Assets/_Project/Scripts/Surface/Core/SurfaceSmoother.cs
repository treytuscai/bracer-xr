using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using System.Collections.Generic;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Applies two complementary smoothing passes to the forearm surface each frame.
    ///
    /// WHY TWO ALGORITHMS?
    ///   Laplacian (Burst, parallel): replaces each interior surface cell with the
    ///   weighted centroid of itself and its surface neighbors. Reduces depth sensor
    ///   noise across the whole patch but treats every cell equally — it does not
    ///   specifically target the staircase artifact that appears at the mesh boundary
    ///   where the arm patch transitions to empty grid cells.
    ///
    ///   Boundary contour (main thread, sequential): finds the outer edge of the surface
    ///   patch, traces it as ordered chains of cells, and applies a 1D moving average
    ///   along each chain. Directly attacks the staircase without disturbing the interior.
    ///
    /// WHY BOUNDARY SMOOTHING IS MAIN-THREAD SEQUENTIAL
    /// The chain tracer is a greedy walk: each step picks the next unvisited boundary
    /// neighbor based on what was visited in the previous step. This is fundamentally
    /// sequential and cannot be parallelised without a graph-connectivity pre-pass.
    /// Running it on the main thread after Complete() avoids job overhead for a
    /// workload that is already fast (O(boundary cells), not O(all cells)).
    /// </summary>
    public class SurfaceSmoother
    {
        // ------------------------------------------------------------------
        // CONFIGURATION
        // ------------------------------------------------------------------
        /// <summary> Number of Laplacian passes. 0 = raw depth, higher = smoother but more lag. </summary>
        public int SmoothPasses;
        /// <summary> Number of boundary contour smoothing passes. 0 = no edge smoothing. </summary>
        public int EdgeSmoothPasses;
        /// <summary> Half-width of the 1D moving-average window along each boundary chain. </summary>
        public int EdgeWindowRadius;

        // Managed lists owned here and reused across frames to avoid per-frame allocation.
        private readonly List<int>     _chain         = new List<int>(512);
        private readonly List<Vector3> _chainSmoothed = new List<Vector3>(512);

        /// <summary>
        /// Stores smoothing parameters. Set once at construction from Inspector values.
        /// </summary>
        public SurfaceSmoother(int smoothPasses, int edgeSmoothPasses, int edgeWindowRadius)
        {
            SmoothPasses     = smoothPasses;
            EdgeSmoothPasses = edgeSmoothPasses;
            EdgeWindowRadius = edgeWindowRadius;
        }

        /// <summary>
        /// Runs the full smoothing pipeline and blocks until complete.
        /// After this returns, buffer.Hits holds the final smoothed world positions.
        ///
        /// Laplacian passes use a ping-pong pattern: SmoothSurfaceJob reads from
        /// buffer.Hits and writes to buffer.Smoothed, then the two references are
        /// swapped on the main thread. The swap is safe because the job captured its
        /// NativeArray pointers at Schedule time — swapping the wrapper references
        /// afterwards does not affect the scheduled job's memory access.
        /// </summary>
        public void Schedule(
            SurfaceBuffer buffer,
            int rows, int cols,
            JobHandle dependency)
        {
            JobHandle lastPass = dependency;
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

                lastPass = job.Schedule(rows * cols, 64, lastPass);

                // Swap so the next pass reads this pass's output as its input.
                // Safe to do immediately — the job already captured its pointers above.
                var temp       = buffer.Hits;
                buffer.Hits    = buffer.Smoothed;
                buffer.Smoothed = temp;
            }

            // Boundary smoothing writes directly to buffer.Hits (a NativeArray),
            // so all Burst jobs must be complete before the main thread touches it.
            lastPass.Complete();

            BoundarySmoother.ProcessBoundary(
                EdgeSmoothPasses,
                EdgeWindowRadius,
                rows, cols,
                buffer.Hits,
                buffer.IsSurface,
                buffer.BoundaryVisited,
                _chain,
                _chainSmoothed
            );
        }

        // ==================================================================
        // BURST JOB
        // ==================================================================

        /// <summary>
        /// One Laplacian smoothing pass: each surface cell is replaced by the centroid
        /// of itself and all surface neighbors in the 3×3 Moore neighborhood.
        /// The cell's own position is included with weight 1 (self-weight) to prevent
        /// the mesh from shrinking inward with each pass.
        /// Non-surface cells are copied verbatim to keep the ping-pong buffer consistent:
        /// after the swap, the next pass reads these cells from the new Hits array and
        /// must see correct values rather than stale data from a previous frame.
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

                // Seed the accumulator with the cell's own position (self-weight = 1).
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
    }

    /// <summary>
    /// Traces the outer boundary of the surface patch as ordered contour chains and
    /// applies a 1D moving average along each chain to reduce the staircase artifact
    /// at the mesh edge. Runs on the main thread after all Burst jobs have completed.
    /// </summary>
    public static class BoundarySmoother
    {
        // 8-way connectivity offsets ordered: TL, T, TR, L, R, BL, B, BR.
        // Indices 1(T), 3(L), 4(R), 6(B) are the 4-connected directions.
        private static readonly int[] dRow = { -1, -1, -1,  0, 0,  1, 1, 1 };
        private static readonly int[] dCol = { -1,  0,  1, -1, 1, -1, 0, 1 };

        /// <summary>
        /// Runs <paramref name="passes"/> boundary smoothing passes over the surface patch.
        /// Each pass: clears visited flags, scans for unvisited boundary cells, traces each
        /// boundary cell into an ordered chain, applies a symmetric moving average of half-width
        /// <paramref name="windowRadius"/> along the chain, then writes the result back to hits.
        /// </summary>
        public static void ProcessBoundary(
            int passes, int windowRadius, int rows, int cols,
            NativeArray<Vector3> hits, NativeArray<bool> isSurface, NativeArray<bool> visited,
            List<int> chain, List<Vector3> chainSmoothed)
        {
            if (passes <= 0) return;

            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < visited.Length; i++)
                    visited[i] = false;

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int flatIdx = r * cols + c;
                        if (visited[flatIdx] || !IsBoundaryCell(r, c, rows, cols, isSurface)) continue;

                        TraceChain(r, c, rows, cols, isSurface, visited, chain);

                        // Chains shorter than 3 can't benefit from a moving average —
                        // the window would reduce to the single center cell anyway.
                        if (chain.Count < 3) continue;

                        // 1D symmetric moving average: each chain position is replaced by
                        // the mean of its [i-windowRadius, i+windowRadius] window, clamped
                        // to the chain ends.
                        chainSmoothed.Clear();
                        for (int i = 0; i < chain.Count; i++)
                        {
                            Vector3 sum   = Vector3.zero;
                            int     count = 0;
                            int     lo    = Mathf.Max(0, i - windowRadius);
                            int     hi    = Mathf.Min(chain.Count - 1, i + windowRadius);

                            for (int j = lo; j <= hi; j++)
                            {
                                sum += hits[chain[j]];
                                count++;
                            }

                            chainSmoothed.Add(sum / count);
                        }

                        for (int i = 0; i < chain.Count; i++)
                            hits[chain[i]] = chainSmoothed[i];
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the cell at (r, c) is on the surface but has at least one
        /// 8-connected neighbor that is off the surface or outside the grid.
        /// </summary>
        private static bool IsBoundaryCell(int r, int c, int rows, int cols, NativeArray<bool> isSurface)
        {
            if (!isSurface[r * cols + c]) return false;

            for (int n = 0; n < 8; n++)
            {
                int nr = r + dRow[n];
                int nc = c + dCol[n];
                if (nr < 0 || nc < 0 || nr >= rows || nc >= cols || !isSurface[nr * cols + nc])
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Greedily walks the boundary starting at (startR, startC), appending flat cell
        /// indices to <paramref name="chain"/> in traversal order.
        /// At each step, 4-connected neighbors (T, L, R, B) are preferred over diagonals
        /// to produce straighter, less jagged chains. The walk ends when no unvisited
        /// boundary neighbor remains.
        /// </summary>
        private static void TraceChain(
            int startR, int startC, int rows, int cols,
            NativeArray<bool> isSurface, NativeArray<bool> visited, List<int> chain)
        {
            chain.Clear();
            int r = startR;
            int c = startC;

            while (true)
            {
                int flatIdx = r * cols + c;
                visited[flatIdx] = true;
                chain.Add(flatIdx);

                int  bestR  = -1;
                int  bestC  = -1;
                bool found  = false;

                for (int n = 0; n < 8; n++)
                {
                    int nr = r + dRow[n];
                    int nc = c + dCol[n];

                    if (nr < 0 || nc < 0 || nr >= rows || nc >= cols) continue;

                    int nIdx = nr * cols + nc;
                    if (visited[nIdx] || !IsBoundaryCell(nr, nc, rows, cols, isSurface)) continue;

                    bestR = nr;
                    bestC = nc;
                    found = true;

                    // Prefer 4-connected (T=1, L=3, R=4, B=6) — break early if found to
                    // avoid overwriting bestR/bestC with a diagonal candidate at n > 6.
                    if (n == 1 || n == 3 || n == 4 || n == 6) break;
                }

                if (!found) break;
                r = bestR;
                c = bestC;
            }
        }
    }
}
