using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;
using System.Threading;
using System;

namespace Surface.Core
{
    /// <summary>
    /// Converts the segmented depth hits in SurfaceBuffer into a renderable mesh in MeshBuffer
    /// via a Burst job chain. Schedule() returns the chain's handle WITHOUT blocking; the caller
    /// completes it later (frame-pipelined) and calls Finish() to read the results off it.
    ///
    /// JOB CHAIN
    ///   1. ComputeProjectedCenterJob — sums each batch's lateral projection along AxisRight.
    ///   2. FinalizeCenterJob — aggregates the per-batch sums into the mean "projected center"
    ///      (written to a NativeArray), which keeps the UV window centered on the visible arm
    ///      patch. In the job graph so VertexJob can depend on it without a mid-pipeline Complete.
    ///   3. VertexJob — for each surface cell, atomically claims a dense vertex slot and
    ///      computes the local-space position and UV0 (reading the finalized center).
    ///   4. TriangleJob — for each 2×2 grid block, emits up to two triangles, each edge-tested
    ///      independently against MaxQuadEdgeSq so a single far corner drops only its triangle.
    ///
    /// UV DESIGN (see ArmFrame.cs for full rationale)
    /// U is a linear projection along camera-fixed AxisRight, normalized by DisplayWidth
    /// and centered on the arm's visible patch. V is a linear projection along Axis
    /// (wrist->elbow), normalized by DisplayHeight. A pronation scroll offset is added to U
    /// so wrist rotation reveals new content rather than spinning the image. A 2D rotation
    /// compensates for portrait vs. landscape arm orientation.
    ///
    /// ATOMIC COUNTER PATTERN
    /// VertexJob and TriangleJob run in parallel across all cells. Each thread claims its
    /// output slot via Interlocked.Increment/Add on Counter[0]/Counter[1] without locks.
    /// NativeDisableUnsafePtrRestriction is required to expose the raw pointer to Interlocked.
    /// NativeDisableParallelForRestriction is required on the output arrays because writes
    /// are at non-sequential atomic indices, not the job's own linear index.
    /// </summary>
    public class MeshGenerator : IDisposable
    {
        // ------------------------------------------------------------------
        // CONFIGURATION
        // Stored pre-squared where applicable to avoid sqrt in the hot path.
        // ------------------------------------------------------------------
        // Squared max world-space edge length. Quads/tris exceeding this are rejected,
        // preventing stretched faces across depth discontinuities (e.g. arm to table).
        public float MaxQuadEdgeSq;
        // Shifts the UV display window along the arm axis from the wrist (meters).
        public float DisplayOffset;
        // Physical width of the display region on the arm; normalizes the U axis (meters).
        public float DisplayWidth;
        // Physical height of the display region along the arm; normalizes the V axis (meters).
        public float DisplayHeight;

        // Persistent partial-sum arrays for the batch center reduction job.
        // Sized to numBatches; grow-only — never shrunk to avoid allocation churn
        // when the grid temporarily shrinks between frames.
        private NativeArray<float> _partialSums;
        private NativeArray<int>   _partialCounts;
        // Single-element result of the center reduction, written by FinalizeCenterJob and read by
        // VertexJob — keeps the reduction in the job graph so no main-thread Complete() is needed
        // mid-pipeline. Persistent (size 1).
        private NativeArray<float> _projCenter;

        /// <summary>
        /// Stores configuration. maxQuadEdge is squared immediately so edge checks
        /// in TriangleJob can use sqrMagnitude without a sqrt.
        /// </summary>
        public MeshGenerator(
            float maxQuadEdge,
            float displayOffset,
            float displayWidth,
            float displayHeight)
        {
            MaxQuadEdgeSq = maxQuadEdge * maxQuadEdge;
            DisplayOffset = displayOffset;
            DisplayWidth  = displayWidth;
            DisplayHeight = displayHeight;
        }

        /// <summary>
        /// Schedules the Burst job chain (center reduction -> finalize -> vertices -> triangles)
        /// that populates MeshBuffer, and returns the final JobHandle WITHOUT completing it. The
        /// caller completes it later (deferred to the harvest step, off the main thread) and then
        /// calls Finish() to read the results. The center reduction flows through the job graph
        /// (FinalizeCenterJob), so the whole chain is one schedulable unit with no main-thread sync.
        /// </summary>
        public JobHandle Schedule(
            MeshBuffer meshBuf,
            SurfaceBuffer surfBuf,
            int rows, int cols,
            Vector3 wristPos, Vector3 axis, Vector3 axisRight,
            float pronation, float orientation,
            Matrix4x4 worldToLocal,
            JobHandle dependency)
        {
            meshBuf.ResizeIfNeeded(rows, cols);

            // Reset atomic counters before any job writes to them.
            meshBuf.Counter[0] = 0; // vertex count
            meshBuf.Counter[1] = 0; // triangle index count

            int totalCells = rows * cols;

            // ------------------------------------------------------------------
            // PASS 1: Projected center reduction
            // Uses IJobParallelForBatch so each thread sums a local chunk of cells
            // and writes exactly one (sum, count) pair — no per-element atomics needed.
            // FinalizeCenterJob aggregates the pairs in the job graph (no main-thread Complete).
            // ------------------------------------------------------------------
            int batchSize  = 64;
            int numBatches = (totalCells + batchSize - 1) / batchSize;

            if (!_partialSums.IsCreated || _partialSums.Length < numBatches)
            {
                if (_partialSums.IsCreated) { _partialSums.Dispose(); _partialCounts.Dispose(); }
                _partialSums   = new NativeArray<float>(numBatches, Allocator.Persistent);
                _partialCounts = new NativeArray<int>(numBatches,   Allocator.Persistent);
            }
            if (!_projCenter.IsCreated) _projCenter = new NativeArray<float>(1, Allocator.Persistent);

            var centerJob = new ComputeProjectedCenterJob
            {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                WristPos = wristPos, AxisRight = axisRight,
                Sums = _partialSums, Counts = _partialCounts,
                BatchSize = batchSize
            };
            JobHandle handle = centerJob.ScheduleBatch(totalCells, batchSize, dependency);

            // Aggregate the per-batch (sum, count) into the final center IN THE JOB GRAPH, so
            // VertexJob can depend on it without a main-thread Complete() mid-pipeline.
            var finalizeJob = new FinalizeCenterJob
            {
                Sums       = _partialSums,
                Counts     = _partialCounts,
                NumBatches = numBatches,
                ProjCenter = _projCenter
            };
            handle = finalizeJob.Schedule(handle);

            // ------------------------------------------------------------------
            // PASS 2: Vertex and UV generation
            // ------------------------------------------------------------------
            var vertJob = new VertexJob
            {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                Cols = cols,
                WristPos = wristPos, Axis = axis, AxisRight = axisRight,
                ProjCenter = _projCenter,
                PronationScroll = pronation / (2f * Mathf.PI),
                CosOrientation  = Mathf.Cos(orientation),
                SinOrientation  = Mathf.Sin(orientation),
                Offset = DisplayOffset, Width = DisplayWidth, Height = DisplayHeight,
                WorldToLocal = worldToLocal,
                OutVerts = meshBuf.Vertices, OutUVs = meshBuf.UVs,
                CellToVert = meshBuf.CellToVert,
                Counter = meshBuf.Counter
            };
            handle = vertJob.Schedule(totalCells, 64, handle);

            // ------------------------------------------------------------------
            // PASS 3: Triangle generation
            // Each job element is one 2×2 grid block, emitting up to two triangles. Every candidate
            // triangle is tested independently against MaxQuadEdgeSq, so a single far corner drops
            // only its triangle (the rest still fill) rather than discarding the whole block — while
            // still rejecting any triangle that would span a real depth discontinuity.
            // ------------------------------------------------------------------
            var triJob = new TriangleJob
            {
                Hits = surfBuf.Hits, CellToVert = meshBuf.CellToVert,
                Cols = cols, MaxSq = MaxQuadEdgeSq,
                OutTris = meshBuf.Triangles, Counter = meshBuf.Counter
            };
            JobHandle triHandle = triJob.Schedule((rows - 1) * (cols - 1), 32, handle);

            // ------------------------------------------------------------------
            // PASS 4: Per-vertex normals from grid neighbors (parallel). Independent of triangles
            // (both only depend on VertexJob's CellToVert + dense slots), so it runs alongside
            // PASS 3 — replacing the main-thread Mesh.RecalculateNormals().
            // ------------------------------------------------------------------
            var normalsJob = new NormalsJob
            {
                Hits         = surfBuf.Hits,
                IsSurface    = surfBuf.IsSurface,
                CellToVert   = meshBuf.CellToVert,
                Cols         = cols,
                Rows         = rows,
                WorldToLocal = worldToLocal,
                OutNormals   = meshBuf.Normals
            };
            JobHandle normHandle = normalsJob.Schedule(totalCells, 64, handle);

            // Return the combined handle (triangles + normals) without completing — the caller
            // completes it at harvest time, then Finish().
            return JobHandle.CombineDependencies(triHandle, normHandle);
        }

        /// <summary>
        /// Reads the results after the Schedule() handle has completed: the finalized projected
        /// center, and the atomic vertex/triangle counts copied from the NativeArray into managed
        /// ints for the Mesh API. Call only once the returned handle is complete.
        /// </summary>
        public void Finish(MeshBuffer meshBuf, out float finalProjCenter)
        {
            finalProjCenter       = _projCenter[0];
            meshBuf.VertexCount   = meshBuf.Counter[0];
            meshBuf.TriangleCount = meshBuf.Counter[1];
        }

        /// <summary>
        /// Disposes the persistent partial-sum arrays.
        /// </summary>
        public void Dispose()
        {
            if (_partialSums.IsCreated)   _partialSums.Dispose();
            if (_partialCounts.IsCreated) _partialCounts.Dispose();
            if (_projCenter.IsCreated)    _projCenter.Dispose();
        }

        // =======================================================================================
        // BURST JOBS
        // =======================================================================================

        /// <summary>
        /// Computes the mean lateral projection of all surface hits along AxisRight.
        /// Each batch element sums its chunk locally and writes one (sum, count) pair,
        /// avoiding per-element atomic accumulation. FinalizeCenterJob then aggregates those
        /// pairs in the job graph (no main-thread Complete) to produce the center.
        /// </summary>
        [BurstCompile]
        struct ComputeProjectedCenterJob : IJobParallelForBatch
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    IsSurface;
            public Vector3 WristPos, AxisRight;
            [WriteOnly] public NativeArray<float> Sums;
            [WriteOnly] public NativeArray<int>   Counts;
            public int BatchSize;

            public void Execute(int startIndex, int count)
            {
                float localSum   = 0;
                int   localCount = 0;

                for (int i = startIndex; i < startIndex + count; i++)
                {
                    if (!IsSurface[i]) continue;
                    // Dot product projects each hit onto AxisRight, measuring its
                    // lateral distance from the wrist along the camera-facing axis.
                    localSum += Vector3.Dot(Hits[i] - WristPos, AxisRight);
                    localCount++;
                }

                int batchIdx = startIndex / BatchSize;
                Sums[batchIdx]   = localSum;
                Counts[batchIdx] = localCount;
            }
        }

        /// <summary>
        /// Aggregates the per-batch (sum, count) pairs from ComputeProjectedCenterJob into the
        /// final mean center and writes it to ProjCenter[0]. Single-threaded but tiny (numBatches
        /// entries). Runs in the job graph so VertexJob can depend on it without the main thread
        /// blocking mid-pipeline.
        /// </summary>
        [BurstCompile]
        struct FinalizeCenterJob : IJob
        {
            [ReadOnly] public NativeArray<float> Sums;
            [ReadOnly] public NativeArray<int>   Counts;
            public int NumBatches;
            [WriteOnly] public NativeArray<float> ProjCenter;

            public void Execute()
            {
                float sum   = 0f;
                int   count = 0;
                for (int i = 0; i < NumBatches; i++)
                {
                    sum   += Sums[i];
                    count += Counts[i];
                }
                ProjCenter[0] = count > 0 ? sum / count : 0f;
            }
        }

        /// <summary>
        /// Converts each surface grid cell into a dense vertex with position and UV.
        /// Non-surface cells write CellToVert = -1 so TriangleJob skips them.
        /// </summary>
        [BurstCompile]
        struct VertexJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    IsSurface;
            public int     Cols;
            public Vector3 WristPos, Axis, AxisRight;
            // Orientation and Pronation are pre-computed outside the job so CalculateUV
            // does not call Mathf.Cos/Sin or divide on every surface cell.
            // [0] = finalized projected center (written by FinalizeCenterJob in the job graph).
            [ReadOnly] public NativeArray<float> ProjCenter;
            public float   PronationScroll, CosOrientation, SinOrientation;
            public float   Offset, Width, Height;
            public Matrix4x4 WorldToLocal;

            // Non-sequential write indices (atomic slot allocation) require disabling
            // Burst's parallel-write safety check on these output arrays.
            [NativeDisableParallelForRestriction] public NativeArray<Vector3> OutVerts;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> OutUVs;

            [WriteOnly] public NativeArray<int> CellToVert;

            // Interlocked requires a raw int*, which Burst's safety system blocks by default.
            [NativeDisableUnsafePtrRestriction]
            public NativeArray<int> Counter;

            public unsafe void Execute(int i)
            {
                if (!IsSurface[i]) { CellToVert[i] = -1; return; }

                // Atomically claim the next available vertex slot.
                // Interlocked.Increment returns the value AFTER the increment,
                // so subtract 1 to get a 0-based index.
                int vIdx = Interlocked.Increment(ref ((int*)Counter.GetUnsafePtr())[0]) - 1;
                CellToVert[i] = vIdx;

                OutVerts[vIdx] = WorldToLocal.MultiplyPoint3x4(Hits[i]);
                OutUVs[vIdx]   = CalculateUV(Hits[i]);
            }

            /// <summary>
            /// Computes UV0 for a surface hit using linear projection onto the arm axes.
            ///
            /// TWO-PANEL LAYOUT: U=[0,0.5] = dorsal (palm-down) panel, U=[0.5,1] = palmar panel.
            /// Set DisplayWidth = DisplayHeight in the Inspector for undistorted (square-pixel)
            /// rendering — both express the physical size of the visible display window in meters.
            ///
            /// V — along arm axis (Axis, wrist->elbow):
            ///   Dot(fromWrist, Axis) gives physical distance along the arm. Divided by
            ///   Height and centered, then flipped (1f -) so V=0 is the elbow-side and V=1
            ///   is the wrist-side of the display window.
            ///
            /// U — across arm axis (AxisRight, camera-fixed):
            ///   Dot(fromWrist, AxisRight) gives lateral distance from the arm center.
            ///   Divided by Width and offset to 0.25 (dorsal panel center). A 180° wrist
            ///   rotation adds Pronation/(2*PI) = 0.5, shifting the center to 0.75 (palmar
            ///   panel). The camera-fixed AxisRight keeps the viewport upright; wrist rotation
            ///   scrolls content rather than spinning the UV frame.
            ///
            /// Orientation rotation — a 2D rotation of the UV pair around (0.5, 0.5):
            ///   Portrait  (Orientation ≈ 0):    no change.
            ///   Landscape (Orientation ≈ -PI/2): U and V swap so the display reads
            ///   left-to-right across the horizontally-held arm.
            /// </summary>
            private Vector2 CalculateUV(Vector3 pt)
            {
                Vector3 fromWrist = pt - WristPos;

                float distAlong = Vector3.Dot(fromWrist, Axis);
                float v = 1f - (((distAlong - Offset) / Mathf.Max(Height, 1e-4f)) + 0.5f);

                float projR = Vector3.Dot(fromWrist, AxisRight);
                float u = ((projR - ProjCenter[0]) / Mathf.Max(Width, 1e-4f)) + 0.25f;

                u += PronationScroll;

                // Rotate UV around the center point (0.5, 0.5).
                float cu = u - 0.5f, cv = v - 0.5f;
                return new Vector2(cu * CosOrientation - cv * SinOrientation + 0.5f,
                                   cu * SinOrientation + cv * CosOrientation + 0.5f);
            }
        }

        /// <summary>
        /// Tessellates 2×2 grid blocks into quads or triangles with counter-clockwise winding.
        /// Uses CellToVert to skip non-surface corners and rejects faces whose world-space
        /// edges exceed MaxSq to prevent bridging across depth discontinuities.
        /// </summary>
        [BurstCompile]
        struct TriangleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<int>     CellToVert;
            public int   Cols;
            public float MaxSq;

            [NativeDisableParallelForRestriction] public NativeArray<int> OutTris;
            [NativeDisableUnsafePtrRestriction]   public NativeArray<int> Counter;

            public unsafe void Execute(int i)
            {
                // Each job element maps to one 2×2 block; decode to top-left (r, c).
                int r = i / (Cols - 1);
                int c = i % (Cols - 1);

                int idxTL = r       * Cols + c;
                int idxTR = r       * Cols + c + 1;
                int idxBL = (r + 1) * Cols + c;
                int idxBR = (r + 1) * Cols + c + 1;

                // CellToVert holds -1 for non-surface cells.
                int tl = CellToVert[idxTL];
                int tr = CellToVert[idxTR];
                int bl = CellToVert[idxBL];
                int br = CellToVert[idxBR];

                int vCount = (tl >= 0 ? 1 : 0) + (tr >= 0 ? 1 : 0) +
                             (bl >= 0 ? 1 : 0) + (br >= 0 ? 1 : 0);
                if (vCount < 3) return;

                if (vCount == 4)
                {
                    // All 4 corners present: split into two CCW triangles and test each
                    // independently. A single far corner (e.g. the grazing corner at a step edge)
                    // then drops only its own triangle, so the near triangle still bridges the gap
                    // instead of the whole quad being rejected. The per-triangle edge check still
                    // rejects a face spanning a real depth discontinuity, so this won't bridge
                    // arm->background — it's strictly less aggressive than the old all-or-nothing quad.
                    if (CheckTri(Hits[idxTL], Hits[idxBL], Hits[idxTR])) WriteTri(tl, bl, tr);
                    if (CheckTri(Hits[idxTR], Hits[idxBL], Hits[idxBR])) WriteTri(tr, bl, br);
                }
                else
                {
                    // Exactly 3 corners: emit one CCW triangle for the present corners.
                    if      (tl < 0 && CheckTri(Hits[idxTR], Hits[idxBL], Hits[idxBR])) WriteTri(tr, bl, br);
                    else if (tr < 0 && CheckTri(Hits[idxTL], Hits[idxBL], Hits[idxBR])) WriteTri(tl, bl, br);
                    else if (bl < 0 && CheckTri(Hits[idxTL], Hits[idxBR], Hits[idxTR])) WriteTri(tl, br, tr);
                    else if (br < 0 && CheckTri(Hits[idxTL], Hits[idxBL], Hits[idxTR])) WriteTri(tl, bl, tr);
                }
            }

            /// <summary> Atomically claims 3 index slots and writes CCW triangle indices. </summary>
            private unsafe void WriteTri(int a, int b, int c)
            {
                int start = Interlocked.Add(ref ((int*)Counter.GetUnsafePtr())[1], 3) - 3;
                OutTris[start] = a; OutTris[start + 1] = b; OutTris[start + 2] = c;
            }

            /// <summary> Returns true if all 3 edges of the triangle are within MaxSq. </summary>
            bool CheckTri(Vector3 a, Vector3 b, Vector3 c) =>
                (a - b).sqrMagnitude < MaxSq &&
                (b - c).sqrMagnitude < MaxSq &&
                (a - c).sqrMagnitude < MaxSq;
        }

        /// <summary>
        /// Computes a per-vertex normal for each surface cell from its grid neighbors (central
        /// difference of the world hits along the row/col directions), transforms it to local
        /// space, and writes it to the cell's dense vertex slot. Parallel and scatter-free — the
        /// regular grid hands each cell its neighbors directly, so no adjacency build or atomics
        /// are needed (unlike Mesh.RecalculateNormals, which is single-threaded on the main thread).
        /// </summary>
        [BurstCompile]
        struct NormalsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    IsSurface;
            [ReadOnly] public NativeArray<int>     CellToVert;
            public int Cols;
            public int Rows;
            public Matrix4x4 WorldToLocal;

            [NativeDisableParallelForRestriction] public NativeArray<Vector3> OutNormals;

            public void Execute(int i)
            {
                int v = CellToVert[i];
                if (v < 0) return;   // non-surface cell has no vertex

                int row = i / Cols;
                int col = i % Cols;
                Vector3 p = Hits[i];

                // Neighbor hits along each grid axis, falling back to the cell itself when a
                // neighbor is off-surface or off-grid (one-sided difference at the patch edge).
                Vector3 right = (col + 1 < Cols && IsSurface[i + 1])    ? Hits[i + 1]    : p;
                Vector3 left  = (col - 1 >= 0   && IsSurface[i - 1])    ? Hits[i - 1]    : p;
                Vector3 down  = (row + 1 < Rows && IsSurface[i + Cols]) ? Hits[i + Cols] : p;
                Vector3 up    = (row - 1 >= 0   && IsSurface[i - Cols]) ? Hits[i - Cols] : p;

                // Surface normal = cross of the two grid tangents, transformed to local space.
                Vector3 nLocal = WorldToLocal.MultiplyVector(Vector3.Cross(down - up, right - left));

                float m = nLocal.magnitude;
                OutNormals[v] = m > 1e-6f ? nLocal / m : Vector3.up;
            }
        }
    }
}
