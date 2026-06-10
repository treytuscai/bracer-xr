// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    /// <summary>
    /// Output buffer written by MeshGenerator and consumed by ForearmDepthSurface.UpdateUnityMesh:
    /// the vertex/UV/normal/index arrays for one frame's mesh upload.
    ///
    /// Arrays are sized to the grid maximum (every cell a surface vertex) and filled densely from 0
    /// by parallel jobs that claim slots via Interlocked on Counter[0]/[1]. Only [0..VertexCount)
    /// and [0..TriangleCount) are valid; the upload uses those explicit counts. Fields are public
    /// because Burst job structs capture them by object initializer.
    /// </summary>
    public class MeshBuffer : IDisposable
    {
        // ------------------------------------------------------------------
        // MESH DATA ARRAYS  (size = rows × cols; only [0..VertexCount) is valid)
        // Written by MeshGenerator jobs; uploaded to Unity's Mesh API each frame.
        // ------------------------------------------------------------------

        /// <summary> Local-space vertex positions, transformed from world hits by WorldToLocal. </summary>
        public NativeArray<Vector3> Vertices;
        /// <summary> UV0 from MeshGenerator.CalculateUV (linear projection along the arm axes). </summary>
        public NativeArray<Vector2> UVs;
        /// <summary> Local-space per-vertex normals, computed from grid neighbors by NormalsJob. </summary>
        public NativeArray<Vector3> Normals;
        /// <summary> Triangle index buffer (max (rows-1)*(cols-1)*6); only [0..TriangleCount) valid. </summary>
        public NativeArray<int> Triangles;

        // ------------------------------------------------------------------
        // INTERNAL GENERATION ARRAYS  (used only within MeshGenerator jobs)
        // ------------------------------------------------------------------

        /// <summary>
        /// Flat grid cell index -> dense vertex index, or -1 for non-surface cells. TriangleJob
        /// reads it to resolve quad corners to vertex indices.
        /// </summary>
        public NativeArray<int> CellToVert;

        // ------------------------------------------------------------------
        // ATOMIC COUNTERS AND FINAL COUNTS
        // ------------------------------------------------------------------

        /// <summary>
        /// Atomic slot counters for the parallel jobs: [0] = vertex count (VertexJob), [1] = triangle
        /// index count (TriangleJob). Reset to 0 each frame, then copied into VertexCount/TriangleCount.
        /// </summary>
        public NativeArray<int> Counter;

        /// <summary> Number of valid entries in Vertices/UVs after the last Generate call. </summary>
        public int VertexCount;
        /// <summary> Number of valid entries in Triangles after the last Generate call. </summary>
        public int TriangleCount;

        /// <summary>
        /// Allocates all arrays for a (rows × cols) grid if the current allocation no longer
        /// matches the required dimensions. Disposes the previous allocation first.
        /// Safe to call every frame; returns immediately when dimensions are unchanged.
        /// </summary>
        public void ResizeIfNeeded(int rows, int cols)
        {
            int totalCells = rows * cols;
            // A grid of (R × C) cells forms at most (R-1)×(C-1) quads; each quad = 2 tris = 6 indices.
            int maxTris = (rows - 1) * (cols - 1) * 6;

            if (Vertices.IsCreated  &&
                Vertices.Length  == totalCells &&
                Triangles.Length == maxTris) return;

            Dispose();

            Vertices   = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            UVs        = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            Normals    = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            Triangles  = new NativeArray<int>(maxTris,        Allocator.Persistent);
            CellToVert = new NativeArray<int>(totalCells,     Allocator.Persistent);
            Counter    = new NativeArray<int>(2,              Allocator.Persistent);
        }

        /// <summary>
        /// Disposes all NativeArrays (each guarded by IsCreated). VertexCount/TriangleCount are not
        /// reset — they are overwritten every Generate call.
        /// </summary>
        public void Dispose()
        {
            if (Vertices.IsCreated)   Vertices.Dispose();
            if (UVs.IsCreated)        UVs.Dispose();
            if (Normals.IsCreated)    Normals.Dispose();
            if (Triangles.IsCreated)  Triangles.Dispose();
            if (CellToVert.IsCreated) CellToVert.Dispose();
            if (Counter.IsCreated)    Counter.Dispose();
        }
    }
}
