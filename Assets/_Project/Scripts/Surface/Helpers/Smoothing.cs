using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Surface.Helpers
{
    /// <summary>
    /// Applies a Laplacian smoothing pass across the reconstructed surface.
    /// This runs in parallel on the GPU-optimized background threads using Burst.
    /// </summary>
    [BurstCompile]
    public struct SmoothSurfaceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> Hits;
        [WriteOnly] public NativeArray<Vector3> Smoothed;
        [ReadOnly] public NativeArray<bool> IsSurface;

        public int GridHeight; // Rows
        public int GridWidth;  // Columns

        public void Execute(int flatIndex)
        {
            // 1. Validate if this point is part of the tracked arm surface.
            // If not, we don't smooth it, just pass the raw data through.
            if (!IsSurface[flatIndex])
            {
                Smoothed[flatIndex] = Smoothed[flatIndex];
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