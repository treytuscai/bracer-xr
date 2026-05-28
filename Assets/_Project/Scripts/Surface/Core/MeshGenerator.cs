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
    /// via a four-pass Burst pipeline. All passes run on Burst worker threads; Generate()
    /// blocks on the main thread only at the two necessary sync points.
    ///
    /// PASS OVERVIEW
    ///   1. ComputeProjectedCenterJob — finds the mean lateral position of all surface hits
    ///      along AxisRight. This "projected center" keeps the UV window centered on the
    ///      visible arm patch rather than on the wrist origin. Must complete synchronously
    ///      before Pass 3 because VertexJob needs the scalar result.
    ///   2. RowBoundsJob — per-row min/max lateral extents. VertexJob uses these to compute
    ///      each vertex's distance to its row's nearest mesh edge, written to UV1 for the
    ///      shader's soft alpha fade at the irregular mesh boundary.
    ///   3. VertexJob — for each surface cell, atomically claims a dense vertex slot and
    ///      computes the local-space position, UV0, and UV1 edge distance.
    ///   4. TriangleJob — for each 2×2 grid block, emits a quad (2 tris) or triangle if
    ///      3 of 4 corners are present, rejecting faces whose edges exceed MaxQuadEdgeSq.
    ///
    /// UV DESIGN (see ArmFrame.cs for full rationale)
    /// U is a linear projection along camera-fixed AxisRight, normalized by DisplayWidth
    /// and centered on the arm's visible patch. V is a linear projection along Axis
    /// (wrist→elbow), normalized by DisplayHeight. A pronation scroll offset is added to U
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
        // Additional U offset applied in landscape orientation to shift the texture start
        // away from the inner forearm seam (UV turns, i.e. 0.5 = half-wrap).
        public float LandscapeUOffset;

        // Persistent partial-sum arrays for the batch center reduction job.
        // Sized to numBatches; grow-only — never shrunk to avoid allocation churn
        // when the grid temporarily shrinks between frames.
        private NativeArray<float> _partialSums;
        private NativeArray<int>   _partialCounts;

        /// <summary>
        /// Stores configuration. maxQuadEdge is squared immediately so edge checks
        /// in TriangleJob can use sqrMagnitude without a sqrt.
        /// </summary>
        public MeshGenerator(
            float maxQuadEdge,
            float displayOffset,
            float displayWidth,
            float displayHeight,
            float landscapeUOffset)
        {
            MaxQuadEdgeSq    = maxQuadEdge * maxQuadEdge;
            DisplayOffset    = displayOffset;
            DisplayWidth     = displayWidth;
            DisplayHeight    = displayHeight;
            LandscapeUOffset = landscapeUOffset;
        }

        /// <summary>
        /// Runs the full four-pass Burst pipeline and populates MeshBuffer with valid geometry.
        /// Blocks the main thread twice: after Pass 1 (to read finalProjCenter) and after
        /// Pass 4 (to copy atomic counts into VertexCount/TriangleCount).
        /// </summary>
        public void Generate(
            MeshBuffer meshBuf,
            SurfaceBuffer surfBuf,
            int rows, int cols,
            Vector3 wristPos, Vector3 axis, Vector3 axisRight,
            float pronation, float orientation,
            Matrix4x4 worldToLocal,
            out float finalProjCenter)
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
            // The result is aggregated on the main thread after Complete().
            // ------------------------------------------------------------------
            int batchSize  = 64;
            int numBatches = (totalCells + batchSize - 1) / batchSize;

            if (!_partialSums.IsCreated || _partialSums.Length < numBatches)
            {
                if (_partialSums.IsCreated) { _partialSums.Dispose(); _partialCounts.Dispose(); }
                _partialSums   = new NativeArray<float>(numBatches, Allocator.Persistent);
                _partialCounts = new NativeArray<int>(numBatches,   Allocator.Persistent);
            }

            var centerJob = new ComputeProjectedCenterJob
            {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                WristPos = wristPos, AxisRight = axisRight,
                Sums = _partialSums, Counts = _partialCounts,
                BatchSize = batchSize
            };
            // Must complete synchronously: VertexJob needs finalProjCenter as a scalar
            // input and there is no way to pass a NativeArray reduction result to a
            // downstream job without first reading it on the main thread.
            JobHandle handle = centerJob.ScheduleBatch(totalCells, batchSize);
            handle.Complete();

            float totalSum   = 0;
            int   totalCount = 0;
            for (int i = 0; i < numBatches; i++)
            {
                totalSum   += _partialSums[i];
                totalCount += _partialCounts[i];
            }
            finalProjCenter = totalCount > 0 ? totalSum / totalCount : 0f;

            // ------------------------------------------------------------------
            // PASS 2: Row bounds
            // Each job element processes one full row, writing the min and max
            // lateral projections along AxisRight. VertexJob reads these to compute
            // each vertex's distance to its row's mesh edge for the shader fade.
            // ------------------------------------------------------------------
            var boundsJob = new RowBoundsJob
            {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                Cols = cols, WristPos = wristPos, AxisRight = axisRight,
                RowMin = meshBuf.RowMin, RowMax = meshBuf.RowMax
            };
            handle = boundsJob.Schedule(rows, 32);

            // ------------------------------------------------------------------
            // PASS 3: Vertex and UV generation
            // Atomically claims a dense vertex slot for each surface cell and
            // computes world→local position, UV0, and UV1 edge distance.
            // NativeDisableParallelForRestriction: writes land at atomic indices,
            //   not the job's own sequential index.
            // NativeDisableUnsafePtrRestriction: Interlocked requires a raw pointer
            //   to Counter that Burst's safety system would otherwise block.
            // ------------------------------------------------------------------
            var vertJob = new VertexJob
            {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                RowMin = meshBuf.RowMin, RowMax = meshBuf.RowMax,
                Cols = cols,
                WristPos = wristPos, Axis = axis, AxisRight = axisRight,
                ProjCenter = finalProjCenter, Pronation = pronation, Orientation = orientation,
                Offset = DisplayOffset, Width = DisplayWidth, Height = DisplayHeight,
                LandscapeUOffset = LandscapeUOffset,
                WorldToLocal = worldToLocal,
                OutVerts = meshBuf.Vertices, OutUVs = meshBuf.UVs, OutEdgeDists = meshBuf.EdgeDists,
                CellToVert = meshBuf.CellToVert,
                Counter = meshBuf.Counter
            };
            handle = vertJob.Schedule(totalCells, 64, handle);

            // ------------------------------------------------------------------
            // PASS 4: Triangle generation
            // Each job element is one 2×2 grid block. Emits a full quad (2 tris)
            // when all 4 corners are present, or a single triangle when exactly 3
            // are present. Faces whose world-space edges exceed MaxQuadEdgeSq are
            // discarded to prevent connecting depth discontinuities.
            // ------------------------------------------------------------------
            var triJob = new TriangleJob
            {
                Hits = surfBuf.Hits, CellToVert = meshBuf.CellToVert,
                Cols = cols, MaxSq = MaxQuadEdgeSq,
                OutTris = meshBuf.Triangles, Counter = meshBuf.Counter
            };
            handle = triJob.Schedule((rows - 1) * (cols - 1), 32, handle);

            handle.Complete();

            // Copy atomic counts from NativeArray to managed ints so UpdateUnityMesh
            // can pass them to the Mesh API without touching the NativeArray again.
            meshBuf.VertexCount    = meshBuf.Counter[0];
            meshBuf.TriangleCount  = meshBuf.Counter[1];
        }

        /// <summary>
        /// Disposes the persistent partial-sum arrays.
        /// </summary>
        public void Dispose()
        {
            if (_partialSums.IsCreated)   _partialSums.Dispose();
            if (_partialCounts.IsCreated) _partialCounts.Dispose();
        }

        // =======================================================================================
        // BURST JOBS
        // =======================================================================================

        /// <summary>
        /// Computes the mean lateral projection of all surface hits along AxisRight.
        /// Each batch element sums its chunk locally and writes one (sum, count) pair,
        /// avoiding per-element atomic accumulation. The main thread aggregates batches
        /// after Complete() to produce finalProjCenter.
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
        /// Scans each row and records the min and max lateral projections (along AxisRight)
        /// of all surface cells in that row. VertexJob reads these to compute per-vertex
        /// edge distance for the shader's soft alpha fade at the irregular mesh perimeter.
        /// </summary>
        [BurstCompile]
        struct RowBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    IsSurface;
            public int     Cols;
            public Vector3 WristPos, AxisRight;
            [WriteOnly] public NativeArray<float> RowMin;
            [WriteOnly] public NativeArray<float> RowMax;

            public void Execute(int r)
            {
                float min  = float.MaxValue;
                float max  = float.MinValue;
                bool  found = false;

                for (int c = 0; c < Cols; c++)
                {
                    int idx = r * Cols + c;
                    if (!IsSurface[idx]) continue;

                    float proj = Vector3.Dot(Hits[idx] - WristPos, AxisRight);
                    if (proj < min) min = proj;
                    if (proj > max) max = proj;
                    found = true;
                }

                RowMin[r] = found ? min : 0;
                RowMax[r] = found ? max : 0;
            }
        }

        /// <summary>
        /// Converts each surface grid cell into a dense vertex with position, UV, and edge distance.
        /// Non-surface cells write CellToVert = -1 so TriangleJob skips them.
        /// </summary>
        [BurstCompile]
        struct VertexJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    IsSurface;
            [ReadOnly] public NativeArray<float>   RowMin, RowMax;
            public int     Cols;
            public Vector3 WristPos, Axis, AxisRight;
            public float   ProjCenter, Pronation, Orientation, Offset, Width, Height, LandscapeUOffset;
            public Matrix4x4 WorldToLocal;

            // Non-sequential write indices (atomic slot allocation) require disabling
            // Burst's parallel-write safety check on these output arrays.
            [NativeDisableParallelForRestriction] public NativeArray<Vector3> OutVerts;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> OutUVs;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> OutEdgeDists;

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

                Vector3 hit = Hits[i];
                int r = i / Cols;

                // Edge distance: how far this vertex sits from its row's nearest boundary.
                // Min(left distance, right distance) — clamped to 0 for any out-of-row vertex.
                // The shader reads this from UV1.x to fade alpha toward the mesh perimeter.
                float projR = Vector3.Dot(hit - WristPos, AxisRight);
                float dist  = Mathf.Max(0f, Mathf.Min(projR - RowMin[r], RowMax[r] - projR));

                OutVerts[vIdx]     = WorldToLocal.MultiplyPoint3x4(hit);
                OutUVs[vIdx]       = CalculateUV(hit);
                OutEdgeDists[vIdx] = new Vector2(dist, 0f);
            }

            /// <summary>
            /// Computes UV0 for a surface hit using linear projection onto the arm axes.
            ///
            /// V — along arm axis (Axis, wrist→elbow):
            ///   Dot(fromWrist, Axis) gives physical distance along the arm. Divided by
            ///   Height and centered, then flipped (1f -) so V=0 is the elbow-side of
            ///   the display window and V=1 is the wrist-side.
            ///
            /// U — across arm axis (AxisRight, camera-fixed):
            ///   Dot(fromWrist, AxisRight) gives lateral distance from the wrist origin.
            ///   ProjCenter is subtracted to center the window on the visible arm patch
            ///   rather than the wrist. Divided by Width and offset to [0,1].
            ///   Pronation/PI is then added as a linear scroll: rotating the wrist shifts
            ///   which column of the texture is visible without rotating the UV frame.
            ///   LandscapeUOffset shifts the seam away from the inner forearm in landscape.
            ///
            /// Orientation rotation — a 2D rotation of the UV pair around (0.5, 0.5):
            ///   Portrait  (Orientation ≈ 0):    no change.
            ///   Landscape (Orientation ≈ -PI/2): U and V swap so the display reads
            ///   left-to-right across the horizontally-held arm.
            /// </summary>
            private readonly Vector2 CalculateUV(Vector3 pt)
            {
                Vector3 fromWrist = pt - WristPos;

                float distAlong = Vector3.Dot(fromWrist, Axis);
                float v = 1f - (((distAlong - Offset) / Mathf.Max(Height, 1e-4f)) + 0.5f);

                float projR = Vector3.Dot(fromWrist, AxisRight);
                float u = ((projR - ProjCenter) / Mathf.Max(Width, 1e-4f)) + 0.5f;

                bool isLandscape = Mathf.Abs(Orientation) > Mathf.PI * 0.25f;
                u += Pronation / Mathf.PI + (isLandscape ? LandscapeUOffset : 0f);

                // Rotate UV around the centre point (0.5, 0.5).
                float cu = u - 0.5f, cv = v - 0.5f;
                float cosA = Mathf.Cos(Orientation), sinA = Mathf.Sin(Orientation);
                return new Vector2(cu * cosA - cv * sinA + 0.5f,
                                   cu * sinA + cv * cosA + 0.5f);
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
                    // All 4 corners: emit two CCW triangles forming a quad.
                    // CheckQuad tests all 4 perimeter edges; by triangle inequality the
                    // diagonals are bounded to ≤ 2×MaxSq, sufficient for depth grid data.
                    if (CheckQuad(Hits[idxTL], Hits[idxTR], Hits[idxBL], Hits[idxBR]))
                    {
                        // Atomically claim 6 consecutive index slots.
                        int start = Interlocked.Add(ref ((int*)Counter.GetUnsafePtr())[1], 6) - 6;
                        OutTris[start]     = tl; OutTris[start + 1] = bl; OutTris[start + 2] = tr;
                        OutTris[start + 3] = tr; OutTris[start + 4] = bl; OutTris[start + 5] = br;
                    }
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

            /// <summary>
            /// Returns true if all 4 perimeter edges of the quad are within MaxSq.
            /// Parameters: a=TL, b=TR, c=BL, d=BR.
            /// </summary>
            bool CheckQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
                (a - b).sqrMagnitude < MaxSq &&
                (a - c).sqrMagnitude < MaxSq &&
                (c - d).sqrMagnitude < MaxSq &&
                (b - d).sqrMagnitude < MaxSq;

            /// <summary> Returns true if all 3 edges of the triangle are within MaxSq. </summary>
            bool CheckTri(Vector3 a, Vector3 b, Vector3 c) =>
                (a - b).sqrMagnitude < MaxSq &&
                (b - c).sqrMagnitude < MaxSq &&
                (a - c).sqrMagnitude < MaxSq;
        }
    }
}
