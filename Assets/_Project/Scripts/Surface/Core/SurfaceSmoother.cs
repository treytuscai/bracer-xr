using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using System.Collections.Generic;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Schedules Laplacian smoothing passes and runs boundary contour
    /// smoothing on the main thread. Owns the managed Lists used for
    /// boundary chain tracing.
    /// </summary>
    public class SurfaceSmoother
    {
        // Configuration (set from Inspector values each frame before Run)
        public int SmoothPasses;
        public int EdgeSmoothPasses;
        public int EdgeWindowRadius;
 
        // Owned memory for boundary tracing (managed, reused across frames)
        private readonly List<int> _chain = new List<int>(512);
        private readonly List<Vector3> _chainSmoothed = new List<Vector3>(512);

        public SurfaceSmoother(int smoothPasses, int edgeSmoothPasses, int edgeWindowRadius)
        {
            SmoothPasses     = smoothPasses;
            EdgeSmoothPasses = edgeSmoothPasses;
            EdgeWindowRadius = edgeWindowRadius;
        }
 
        /// <summary>
        /// Runs the full smoothing pipeline: Laplacian passes (Burst, parallel)
        /// followed by boundary contour smoothing (main thread, sequential).
        /// Blocks internally. After this returns, buffer.Hits is final.
        /// </summary>
        public void Schedule(
            SurfaceBuffer buffer,
            int rows, int cols,
            JobHandle dependency)
        {
            // Laplacian passes
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
 
                // Swap references for next iteration.
                // The job already captured its pointers at Schedule time.
                var temp = buffer.Hits;
                buffer.Hits = buffer.Smoothed;
                buffer.Smoothed = temp;
            }
 
            // Boundary smoothing (main thread, sequential)
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

        /// <summary>
        /// Applies a Laplacian smoothing pass across the reconstructed surface.
        /// This runs in parallel on the GPU-optimized background threads using Burst.
        /// </summary>
        [BurstCompile]
        struct SmoothSurfaceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            public NativeArray<Vector3> Smoothed;
            [ReadOnly] public NativeArray<bool> IsSurface;

            public int GridHeight; // Rows
            public int GridWidth;  // Columns

            public void Execute(int flatIndex)
            {
                // 1. Validate if this point is part of the tracked arm surface.
                // If not, we don't smooth it, just pass the raw data through.
                if (!IsSurface[flatIndex])
                {
                    Smoothed[flatIndex] = Hits[flatIndex];
                    return;
                }

                // 2. Decode the 1D flat index back into 2D Grid Coordinates.
                int currentRow = flatIndex / GridWidth;
                int currentCol = flatIndex % GridWidth;

                // 3. Initialize the accumulator with the current point's position (Self-Weight).
                // Starting with a weight of 1.0 prevents the mesh from shrinking too aggressively.
                Vector3 positionSum = Hits[flatIndex];
                float totalWeight = 1f;

                // 4. Sample the 3x3 Neighborhood (Moore Neighborhood).
                // We use nested loops to check all 8 surrounding neighbors.
                for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
                {
                    for (int colOffset = -1; colOffset <= 1; colOffset++)
                    {
                        // Skip the center cell because it's already in the 'positionSum'.
                        if (rowOffset == 0 && colOffset == 0) continue;

                        int neighborRow = currentRow + rowOffset;
                        int neighborCol = currentCol + colOffset;

                        // 5. Boundary Check: Ensure the neighbor is within the grid bounds.
                        if (neighborRow >= 0 && neighborRow < GridHeight && 
                            neighborCol >= 0 && neighborCol < GridWidth)
                        {
                            // Encode 2D neighbor coordinates back to 1D for array access.
                            int neighborFlatIndex = neighborRow * GridWidth + neighborCol;
                            
                            // 6. Connectivity Check: Only average with cells that actually 
                            // exist on the arm (ignore holes or background depth).
                            if (IsSurface[neighborFlatIndex])
                            {
                                positionSum += Hits[neighborFlatIndex];
                                totalWeight += 1f;
                            }
                        }
                    }
                }

                // 7. Calculate the Centroid.
                // This pulls the vertex toward the average position of its neighbors.
                Smoothed[flatIndex] = positionSum / totalWeight;
            }
        }
    }

    /// <summary>
    /// Utility class for extracting and smoothing the outer boundaries of the reconstructed mesh.
    /// Runs on the main thread because greedy pathfinding is heavily sequential.
    /// </summary>
    public static class BoundarySmoother
    {
        // 8-way connectivity offsets (Top-Left, Top, Top-Right, Left, Right, Bottom-Left, Bottom, Bottom-Right)
        // Indices 1, 3, 4, and 6 represent the 4-connected (Up, Left, Right, Down) neighbors.
        private static readonly int[] dRow = { -1, -1, -1,  0, 0,  1, 1, 1 };
        private static readonly int[] dCol = { -1,  0,  1, -1, 1, -1, 0, 1 };

        public static void ProcessBoundary(
            int passes, int windowRadius, int rows, int cols,
            NativeArray<Vector3> hits, NativeArray<bool> isSurface, NativeArray<bool> visited,
            List<int> chain, List<Vector3> chainSmoothed)
        {
            if (passes <= 0) return;

            for (int pass = 0; pass < passes; pass++)
            {
                // 1. Fast clear of the visited array (Zero Allocation)
                for (int i = 0; i < visited.Length; i++) 
                {
                    visited[i] = false;
                }

                // 2. Find and trace all unconnected boundary contours
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int flatIdx = r * cols + c;

                        if (visited[flatIdx] || !IsBoundaryCell(r, c, rows, cols, isSurface)) continue;

                        // 3. Trace an ordered line along the edge
                        TraceChain(r, c, rows, cols, isSurface, visited, chain);

                        if (chain.Count < 3) continue;

                        // 4. Apply a 1D Moving Average along the chain
                        chainSmoothed.Clear();
                        for (int i = 0; i < chain.Count; i++)
                        {
                            Vector3 sum = Vector3.zero;
                            int count = 0;

                            int lo = Mathf.Max(0, i - windowRadius);
                            int hi = Mathf.Min(chain.Count - 1, i + windowRadius);

                            for (int j = lo; j <= hi; j++)
                            {
                                sum += hits[chain[j]]; // Look up the 3D position using the flat index
                                count++;
                            }

                            chainSmoothed.Add(sum / count);
                        }

                        // 5. Write smoothed positions back to the main buffer
                        for (int i = 0; i < chain.Count; i++)
                        {
                            hits[chain[i]] = chainSmoothed[i];
                        }
                    }
                }
            }
        }

        private static bool IsBoundaryCell(int r, int c, int rows, int cols, NativeArray<bool> isSurface)
        {
            if (!isSurface[r * cols + c]) return false;

            // A cell is a boundary if ANY of its 8 neighbors are missing (either out of bounds or not surface)
            for (int n = 0; n < 8; n++)
            {
                int nr = r + dRow[n];
                int nc = c + dCol[n];

                if (nr < 0 || nc < 0 || nr >= rows || nc >= cols || !isSurface[nr * cols + nc])
                {
                    return true;
                }
            }
            return false;
        }

        private static void TraceChain(int startR, int startC, int rows, int cols, NativeArray<bool> isSurface, NativeArray<bool> visited, List<int> chain)
        {
            chain.Clear();
            int r = startR;
            int c = startC;

            while (true)
            {
                int flatIdx = r * cols + c;
                visited[flatIdx] = true;
                chain.Add(flatIdx); // Store the 1D index instead of creating a Vector2Int struct

                int bestR = -1;
                int bestC = -1;
                bool found = false;

                // Look for the next unvisited boundary neighbor
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

                    // Prefer 4-connected neighbors (Up, Left, Right, Down) to produce straighter, less jagged chains
                    if (n == 1 || n == 3 || n == 4 || n == 6) break;
                }

                if (!found) break; // Reached the end of the contour line
                
                r = bestR;
                c = bestC;
            }
        }
    }
}