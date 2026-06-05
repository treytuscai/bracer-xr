using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    /// <summary>
    /// Shared NativeArray data bus flowing between every pipeline stage within a single frame.
    /// All arrays are flat (rows × cols) buffers — the depth grid dimensions from DepthReadback —
    /// indexed as (row * cols + col); rows/cols are passed to each stage separately.
    ///
    /// Fields are public (not properties) because BoundarySmoother ping-pongs Hits and Smoothed by
    /// swapping the references directly, which a private setter would block.
    /// </summary>
    public class SurfaceBuffer : IDisposable
    {
        // ------------------------------------------------------------------
        // PER-CELL ARRAYS  (size = rows × cols, Allocator.Persistent, reallocated on grid resize)
        // ------------------------------------------------------------------

        /// <summary> World-space 3D hit position for each grid cell. </summary>
        public NativeArray<Vector3> Hits;
        /// <summary> Ping-pong target for BoundarySmoother: it writes here, then swaps with Hits. </summary>
        public NativeArray<Vector3> Smoothed;
        /// <summary> True if this cell was classified as forearm surface by seed+flood. </summary>
        public NativeArray<bool> IsSurface;
        /// <summary> True if the depth sensor returned a valid depth sample for this cell. </summary>
        public NativeArray<bool> HasDepth;
        /// <summary> BFS work queue for the flood-fill stage (dynamic size, unknown seed count). </summary>
        public NativeQueue<int> BFSQueue;
        /// <summary> Surface cell on the patch edge. Written by BoundaryMaskJob, read by BoundarySmoothJob. </summary>
        public NativeArray<bool> BoundaryMask;

        // Total cell count from the last allocation; -1 when unallocated.
        private int _currentSize = -1;

        /// <summary>
        /// Allocates all arrays for a (rows × cols) grid if the total cell count has
        /// changed since the last allocation. Disposes the previous allocation first.
        /// Safe to call every frame; returns immediately when the size is unchanged.
        /// </summary>
        public void ResizeIfNeeded(int rows, int cols)
        {
            int total = rows * cols;
            if (_currentSize == total) return;

            Dispose();

            Hits            = new NativeArray<Vector3>(total, Allocator.Persistent);
            Smoothed        = new NativeArray<Vector3>(total, Allocator.Persistent);
            IsSurface       = new NativeArray<bool>(total,    Allocator.Persistent);
            HasDepth        = new NativeArray<bool>(total,    Allocator.Persistent);
            BFSQueue        = new NativeQueue<int>(Allocator.Persistent);
            BoundaryMask    = new NativeArray<bool>(total,    Allocator.Persistent);

            _currentSize = total;
        }

        /// <summary>
        /// Disposes all NativeArrays and the NativeQueue. Safe to call when
        /// arrays are unallocated — each disposal is guarded by IsCreated.
        /// </summary>
        public void Dispose()
        {
            if (Hits.IsCreated)            Hits.Dispose();
            if (Smoothed.IsCreated)        Smoothed.Dispose();
            if (IsSurface.IsCreated)       IsSurface.Dispose();
            if (HasDepth.IsCreated)        HasDepth.Dispose();
            if (BoundaryMask.IsCreated) BoundaryMask.Dispose();
            if (BFSQueue.IsCreated)        BFSQueue.Dispose();
            _currentSize = -1;
        }
    }
}
