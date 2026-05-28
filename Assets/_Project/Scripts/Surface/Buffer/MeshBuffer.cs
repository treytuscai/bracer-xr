using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    /// <summary>
    /// Output buffer written by MeshGenerator and consumed by ForearmDepthSurface.UpdateUnityMesh.
    /// Holds the vertex, UV, index, and auxiliary arrays needed for a single frame's mesh upload.
    ///
    /// SPARSE → DENSE COMPACTION
    /// The depth grid has (rows × cols) cells but only a subset are flagged as surface.
    /// MeshGenerator allocates arrays sized to the grid maximum (worst case: every cell
    /// is surface) and fills them densely from index 0 using atomic counters. Only
    /// [0 .. VertexCount) and [0 .. TriangleCount) are valid after generation completes;
    /// the rest of each array is pre-allocated but unused. ForearmDepthSurface uploads
    /// only the valid slice via SetVertices/SetIndices with explicit counts.
    ///
    /// ATOMIC COUNTER PATTERN
    /// VertexJob and TriangleJob run in parallel across thousands of cells. Each job
    /// claims its output slot via Interlocked.Increment/Add on Counter[0] and Counter[1]
    /// respectively, producing a unique write index without locks. After jobs complete,
    /// the counts are copied from Counter into VertexCount and TriangleCount so the main
    /// thread can pass them to Unity's Mesh API without touching the NativeArray again.
    ///
    /// WHY PUBLIC FIELDS INSTEAD OF PROPERTIES?
    /// All arrays are assigned via object initializers inside Burst job structs and written
    /// to by parallel jobs. Public fields are required for the Burst job struct pattern.
    /// </summary>
    public class MeshBuffer : IDisposable
    {
        // ------------------------------------------------------------------
        // MESH DATA ARRAYS  (size = rows × cols; only [0..VertexCount) is valid)
        // Written by MeshGenerator jobs; uploaded to Unity's Mesh API each frame.
        // ------------------------------------------------------------------

        /// <summary> Local-space vertex positions, transformed from world hits by WorldToLocal. </summary>
        public NativeArray<Vector3> Vertices;
        /// <summary>
        /// UV0: linear projection coordinates. U = distance along AxisRight from the arm's
        /// visible center, normalized by displayWidth. V = distance along Axis from wrist,
        /// normalized by displayHeight. Pronation scroll offset and orientation rotation
        /// are applied on top. See ArmFrame and MeshGenerator.CalculateUV for full details.
        /// </summary>
        public NativeArray<Vector2> UVs;
        /// <summary>
        /// UV1: X = distance from vertex to its row's nearest mesh boundary edge.
        /// ForearmProjection.shader reads this to drive a soft alpha fade at the mesh perimeter.
        /// </summary>
        public NativeArray<Vector2> EdgeDists;
        /// <summary>
        /// Triangle index buffer (size = (rows-1)*(cols-1)*6 maximum).
        /// Written atomically by TriangleJob; only [0..TriangleCount) is valid.
        /// </summary>
        public NativeArray<int> Triangles;

        // ------------------------------------------------------------------
        // INTERNAL GENERATION ARRAYS  (used only within MeshGenerator jobs)
        // ------------------------------------------------------------------

        /// <summary>
        /// Maps flat grid cell index → dense vertex array index.
        /// -1 for cells that are not on the surface and received no vertex.
        /// TriangleJob reads this to convert grid-space quad corners to vertex indices.
        /// </summary>
        public NativeArray<int> CellToVert;
        /// <summary> Minimum lateral projection (along AxisRight) of any surface cell in this row. </summary>
        public NativeArray<float> RowMin;
        /// <summary> Maximum lateral projection (along AxisRight) of any surface cell in this row. </summary>
        public NativeArray<float> RowMax;

        // ------------------------------------------------------------------
        // ATOMIC COUNTERS AND FINAL COUNTS
        // ------------------------------------------------------------------

        /// <summary>
        /// Two-element NativeArray used as atomic counters by parallel Burst jobs.
        /// Counter[0] = running vertex count (incremented by VertexJob via Interlocked).
        /// Counter[1] = running triangle index count (incremented by TriangleJob).
        /// Both are reset to 0 at the start of each Generate call, then copied into
        /// VertexCount and TriangleCount after all jobs complete.
        /// </summary>
        public NativeArray<int> Counter;

        /// <summary> Number of valid entries in Vertices/UVs/EdgeDists after the last Generate call. </summary>
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

            // RowMin/RowMax are indexed by row, so we must also verify rows hasn't changed
            // independently of totalCells. A row/col swap (e.g. 10×20 → 20×10) keeps the
            // same total but leaves RowMin/RowMax at the wrong length, causing out-of-bounds
            // writes in RowBoundsJob.
            if (Vertices.IsCreated  &&
                Vertices.Length  == totalCells &&
                Triangles.Length == maxTris    &&
                RowMin.Length    == rows) return;

            Dispose();

            Vertices   = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            UVs        = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            EdgeDists  = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            Triangles  = new NativeArray<int>(maxTris,        Allocator.Persistent);
            CellToVert = new NativeArray<int>(totalCells,     Allocator.Persistent);
            RowMin     = new NativeArray<float>(rows,         Allocator.Persistent);
            RowMax     = new NativeArray<float>(rows,         Allocator.Persistent);
            Counter    = new NativeArray<int>(2,              Allocator.Persistent);
        }

        /// <summary>
        /// Disposes all NativeArrays. Each disposal is guarded by IsCreated so this is safe
        /// to call on a partially or fully unallocated buffer.
        /// Note: VertexCount and TriangleCount are not reset here; they are overwritten at
        /// the start of every Generate call so stale values are never acted upon.
        /// </summary>
        public void Dispose()
        {
            if (Vertices.IsCreated)   Vertices.Dispose();
            if (UVs.IsCreated)        UVs.Dispose();
            if (EdgeDists.IsCreated)  EdgeDists.Dispose();
            if (Triangles.IsCreated)  Triangles.Dispose();
            if (CellToVert.IsCreated) CellToVert.Dispose();
            if (RowMin.IsCreated)     RowMin.Dispose();
            if (RowMax.IsCreated)     RowMax.Dispose();
            if (Counter.IsCreated)    Counter.Dispose();
        }
    }
}
