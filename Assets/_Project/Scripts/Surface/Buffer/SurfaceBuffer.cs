using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    /// <summary>
    /// Shared NativeArray data bus that flows between every stage of the
    /// forearm surface reconstruction pipeline within a single frame.
    ///
    /// All arrays are flat 1D buffers of size (rows × cols), where rows and cols
    /// are the depth grid dimensions computed by DepthReadback each frame. The 2D
    /// grid coordinates are encoded as (row * cols + col) at each use site; no 2D
    /// structure is stored here. Rows and cols are passed separately to each stage.
    ///
    /// FIELD OWNERSHIP — who writes each array:
    ///   Hits            — DepthUnprojectionJob (world positions), then SmoothSurfaceJob
    ///                     (Laplacian pass output via ping-pong swap)
    ///   Smoothed        — SmoothSurfaceJob (ping-pong write target; swapped with Hits
    ///                     after each pass so the next pass reads the smoothed result)
    ///   HasDepth        — DepthUnprojectionJob (false for hand pixels rejected by MetaDepthCopy)
    ///   IsSurface       — DepthUnprojectionJob (reset to false), SeedFromAxisJob +
    ///                     FloodFromSeedsJob (set true for forearm cells)
    ///   BFSQueue        — SeedFromAxisJob (enqueue), FloodFromSeedsJob (dequeue/enqueue)
    ///   BoundaryMask    — BoundaryMaskJob (marks boundary cells), read by BoundarySmoothJob
    ///
    /// WHY PUBLIC FIELDS INSTEAD OF PROPERTIES?
    /// SurfaceSmoother ping-pongs Hits and Smoothed by directly swapping the references:
    ///   var tmp = buffer.Hits; buffer.Hits = buffer.Smoothed; buffer.Smoothed = tmp;
    /// Properties with private setters would prevent this swap. Public fields are
    /// intentional and required by the double-buffer smoothing pattern.
    /// </summary>
    public class SurfaceBuffer : IDisposable
    {
        // ------------------------------------------------------------------
        // PER-CELL ARRAYS  (size = rows × cols, reallocated on grid resize)
        // All use Allocator.Persistent because they live for the component's
        // lifetime and are accessed by Burst jobs across multiple frames.
        // ------------------------------------------------------------------

        /// <summary> World-space 3D hit position for each grid cell. </summary>
        public NativeArray<Vector3> Hits;
        /// <summary>
        /// Ping-pong write target for Laplacian smoothing passes.
        /// SurfaceSmoother writes the smoothed result here, then swaps
        /// the Hits and Smoothed references so the next pass reads it.
        /// </summary>
        public NativeArray<Vector3> Smoothed;
        /// <summary> True if this cell was classified as forearm surface by seed+flood. </summary>
        public NativeArray<bool> IsSurface;
        /// <summary> True if the depth sensor returned a valid depth sample for this cell. </summary>
        public NativeArray<bool> HasDepth;
        /// <summary>
        /// BFS work queue for the flood-fill stage. NativeQueue rather than NativeArray
        /// because the number of seed cells is unknown ahead of time and the queue must
        /// grow dynamically as the flood expands to neighbours.
        /// </summary>
        public NativeQueue<int> BFSQueue;
        /// <summary> Per-cell boundary flag: true if this is a surface cell on the patch edge.
        /// Written by BoundaryMaskJob, read by BoundarySmoothJob. </summary>
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
