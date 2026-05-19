using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    /// <summary>
    /// Manages the memory buffers required for generating the forearm mesh.
    /// Uses NativeArrays to allow high-performance, multi-threaded access via the Burst Compiler.
    /// Memory is kept persistent across frames to eliminate Garbage Collection overhead.
    /// </summary>
    public class MeshBuffer : IDisposable
    {
        // ------------------------------------------------------------------
        // MESH DATA ARRAYS
        // These correspond directly to the data Unity's Mesh API expects.
        // ------------------------------------------------------------------
        
        /// <summary> Local-space vertex positions. </summary>
        public NativeArray<Vector3> Vertices;
        
        /// <summary> UV0 channel: Contains the projected and rotated UI coordinates. </summary>
        public NativeArray<Vector2> UVs;
        
        /// <summary> UV1 channel: X stores the physical distance to the row's mesh edge for smooth shader fading. </summary>
        public NativeArray<Vector2> EdgeDists;
        
        /// <summary> Triangle indices pointing to the Vertices array. </summary>
        public NativeArray<int> Triangles;

        // ------------------------------------------------------------------
        // LOOKUP & BOUNDARY ARRAYS
        // Used internally by the generation jobs to process the grid.
        // ------------------------------------------------------------------
        
        /// <summary> Maps a 1D grid index to its compressed index in the Vertices array. Holds -1 if the cell is empty. </summary>
        public NativeArray<int> CellToVert;
        
        /// <summary> The minimum projected extent of the mesh for a specific row. </summary>
        public NativeArray<float> RowMin;
        
        /// <summary> The maximum projected extent of the mesh for a specific row. </summary>
        public NativeArray<float> RowMax;

        // ------------------------------------------------------------------
        // FINAL COUNTS
        // Populated by the generator so the main thread knows how much data to upload to the GPU.
        // ------------------------------------------------------------------
        public NativeArray<int> Counter; // Index 0 = Vertices, Index 1 = Triangles
        public int VertexCount;
        public int TriangleCount;

        /// <summary>
        /// Checks if the arrays match the required dimensions for the current frame.
        /// If not (or if they are uninitialized), it disposes the old memory and allocates new buffers.
        /// </summary>
        /// <param name="rows">Number of rows in the depth grid.</param>
        /// <param name="cols">Number of columns in the depth grid.</param>
        public void ResizeIfNeeded(int rows, int cols)
        {
            int totalCells = rows * cols;
            
            // A grid of (R x C) vertices can form at most (R-1)*(C-1) quads.
            // Each quad is 2 triangles = 6 indices.
            int maxTris = (rows - 1) * (cols - 1) * 6;

            // If arrays are already the correct size, do nothing.
            if (Vertices.IsCreated && Vertices.Length == totalCells && Triangles.Length == maxTris) return;

            // Free previous allocations to prevent memory leaks
            Dispose();

            // Allocate persistent memory (lives across frames)
            Vertices = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            UVs = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            EdgeDists = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            Triangles = new NativeArray<int>(maxTris, Allocator.Persistent);
            CellToVert = new NativeArray<int>(totalCells, Allocator.Persistent);
            RowMin = new NativeArray<float>(rows, Allocator.Persistent);
            RowMax = new NativeArray<float>(rows, Allocator.Persistent);

            if (Counter.IsCreated && Counter.Length == 2) return;
            if (Counter.IsCreated) Counter.Dispose();
            // Allocate 2 integers: [0] for vertices, [1] for triangles
            Counter = new NativeArray<int>(2, Allocator.Persistent);
        }

        /// <summary>
        /// Safely disposes all unmanaged NativeArray memory.
        /// Must be called when the component is destroyed to avoid memory leaks.
        /// </summary>
        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (UVs.IsCreated) UVs.Dispose();
            if (EdgeDists.IsCreated) EdgeDists.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (CellToVert.IsCreated) CellToVert.Dispose();
            if (RowMin.IsCreated) RowMin.Dispose();
            if (RowMax.IsCreated) RowMax.Dispose();
            if (Counter.IsCreated) Counter.Dispose();
        }
    }
}