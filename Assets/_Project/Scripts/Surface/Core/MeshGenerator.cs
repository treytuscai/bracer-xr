using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe; // Required for GetUnsafePtr
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;
using System.Threading; // Required for Interlocked operations
using System;

namespace Surface.Core
{
    /// <summary>
    /// Executes the multi-threaded Burst pipeline to transform segmented depth hits into a renderable mesh.
    /// Handles boundary smoothing, UV generation, and edge-gap rejection.
    /// </summary>
    public class MeshGenerator : IDisposable
    {
        // ------------------------------------------------------------------
        // CONFIGURATION PARAMETERS (Passed in from the main controller)
        // ------------------------------------------------------------------
        public float MaxQuadEdgeSq;
        public float DisplayOffset;
        public float DisplayWidth;
        public float DisplayHeight;

        // Persistent arrays to store partial summation data across worker threads
        private NativeArray<float> _partialSums;
        private NativeArray<int> _partialCounts;

        public MeshGenerator(
            float maxQuadEdge,
            float displayOffset,
            float displayWidth,
            float displayHeight)
        {
            MaxQuadEdgeSq   = maxQuadEdge * maxQuadEdge;
            DisplayOffset   = displayOffset;
            DisplayWidth    = displayWidth;
            DisplayHeight   = displayHeight;
        }

        /// <summary>
        /// Main entry point for the mesh generation pipeline. Schedules and executes a chain of Burst jobs.
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
            // 1. Ensure output memory is properly sized
            meshBuf.ResizeIfNeeded(rows, cols);
            
            // 2. Setup Atomic Counters for Vertices and Triangles
            // Index 0: Vertex Count | Index 1: Triangle Count
            meshBuf.Counter[0] = 0; // Vertices
            meshBuf.Counter[1] = 0; // Triangles

            int totalCells = rows * cols;
            
            // --------------------------------------------------------------
            // PASS 1: Projected Center Reduction
            // Sums up the projected extent of the arm using a parallel batch job.
            // --------------------------------------------------------------
            int batchSize = 64;
            int numBatches = (totalCells + batchSize - 1) / batchSize;
            
            if (!_partialSums.IsCreated || _partialSums.Length < numBatches)
            {
                if(_partialSums.IsCreated) { _partialSums.Dispose(); _partialCounts.Dispose(); }
                _partialSums = new NativeArray<float>(numBatches, Allocator.Persistent);
                _partialCounts = new NativeArray<int>(numBatches, Allocator.Persistent);
            }

            var centerJob = new ComputeProjectedCenterJob {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                WristPos = wristPos, AxisRight = axisRight,
                Sums = _partialSums, Counts = _partialCounts,
                BatchSize = batchSize
            };
            // Schedule and instantly wait for it, because we need finalProjCenter right now
            JobHandle handle = centerJob.ScheduleBatch(totalCells, batchSize);
            handle.Complete();  

            // Aggregate the batch sums on the main thread
            float totalSum = 0; 
            int totalCount = 0;
            for(int i = 0; i < numBatches; i++) { 
                totalSum += _partialSums[i]; 
                totalCount += _partialCounts[i]; 
            }
            finalProjCenter = totalCount > 0 ? totalSum / totalCount : 0f;

            // --------------------------------------------------------------
            // PASS 2: Row Bounds Job
            // Computes min/max projection boundaries per row for smooth shader fade.
            // --------------------------------------------------------------
            var boundsJob = new RowBoundsJob {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                Rows = rows, Cols = cols, WristPos = wristPos, AxisRight = axisRight,
                RowMin = meshBuf.RowMin, RowMax = meshBuf.RowMax
            };
            handle = boundsJob.Schedule(rows, 32);

            // --------------------------------------------------------------
            // PASS 3: Vertex and UV Generation
            // Converts hits to vertices, computes UI UVs, and allocates compressed indices.
            // --------------------------------------------------------------
            var vertJob = new VertexJob {
                Hits = surfBuf.Hits, IsSurface = surfBuf.IsSurface,
                RowMin = meshBuf.RowMin, RowMax = meshBuf.RowMax,
                Rows = rows, Cols = cols,
                WristPos = wristPos, Axis = axis, AxisRight = axisRight,
                ProjCenter = finalProjCenter, Pronation = pronation, Orientation = orientation,
                Offset = DisplayOffset, Width = DisplayWidth, Height = DisplayHeight,
                WorldToLocal = worldToLocal,
                OutVerts = meshBuf.Vertices, OutUVs = meshBuf.UVs, OutEdgeDists = meshBuf.EdgeDists,
                CellToVert = meshBuf.CellToVert,
                Counter = meshBuf.Counter
            };
            handle = vertJob.Schedule(totalCells, 64, handle);

            // --------------------------------------------------------------
            // PASS 4: Triangle Generation
            // Walks 2x2 grid blocks and emits indices using atomic counters.
            // --------------------------------------------------------------
            var triJob = new TriangleJob {
                Hits = surfBuf.Hits, CellToVert = meshBuf.CellToVert,
                Rows = rows, Cols = cols, MaxSq = MaxQuadEdgeSq,
                OutTris = meshBuf.Triangles, Counter = meshBuf.Counter
            };
            handle = triJob.Schedule((rows - 1) * (cols - 1), 32, handle);

            // Wait for all geometry processing to finish
            handle.Complete();

            // SYNC STEP: Now that the jobs are totally done, copy the final atomic 
            // counts from the unmanaged NativeArray back to the standard C# properties.
            meshBuf.VertexCount = meshBuf.Counter[0];
            meshBuf.TriangleCount = meshBuf.Counter[1];
        }

        public void Dispose()
        {
            if (_partialSums.IsCreated) _partialSums.Dispose();
            if (_partialCounts.IsCreated) _partialCounts.Dispose();
        }

        // =======================================================================================
        // BURST COMPILED JOBS
        // =======================================================================================

        /// <summary>
        /// Computes partial sums of the physical projections to find the optical center of the UI.
        /// Uses IJobParallelForBatch to minimize memory writes and keep execution in the L1 cache.
        /// </summary>
        [BurstCompile]
        struct ComputeProjectedCenterJob : IJobParallelForBatch
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> IsSurface;
            public Vector3 WristPos, AxisRight;
            [WriteOnly] public NativeArray<float> Sums;
            [WriteOnly] public NativeArray<int> Counts;
            public int BatchSize;

            public void Execute(int startIndex, int count)
            {
                float localSum = 0;
                int localCount = 0;
                
                // Process the chunk assigned to this thread
                for (int i = startIndex; i < startIndex + count; i++)
                {
                    if (!IsSurface[i]) continue;
                    localSum += Vector3.Dot(Hits[i] - WristPos, AxisRight);
                    localCount++;
                }
                
                // Write the result to the specific batch slot
                int batchIdx = startIndex / BatchSize;
                Sums[batchIdx] = localSum;
                Counts[batchIdx] = localCount;
            }
        }

        /// <summary>
        /// Calculates the minimum and maximum projection extents for each row.
        /// Used by the shader to create a soft alpha fade at the irregular mesh boundaries.
        /// </summary>
        [BurstCompile]
        struct RowBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> IsSurface;
            public int Rows, Cols;
            public Vector3 WristPos, AxisRight;
            [WriteOnly] public NativeArray<float> RowMin;
            [WriteOnly] public NativeArray<float> RowMax;

            public void Execute(int r)
            {
                float min = float.MaxValue;
                float max = float.MinValue;
                bool found = false;

                // Scan across the entire row
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
        /// Populates the dense Vertex arrays from the sparse depth grid.
        /// Calculates UV projections and atomic vertex allocation.
        /// </summary>
        [BurstCompile]
        struct VertexJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> IsSurface;
            [ReadOnly] public NativeArray<float> RowMin, RowMax;
            public int Rows, Cols;
            public Vector3 WristPos, Axis, AxisRight;
            public float ProjCenter, Pronation, Orientation, Offset, Width, Height;
            public Matrix4x4 WorldToLocal;

            // Arrays disabled from restriction because we write to non-linear atomic indices
            [NativeDisableParallelForRestriction] public NativeArray<Vector3> OutVerts;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> OutUVs;
            [NativeDisableParallelForRestriction] public NativeArray<Vector2> OutEdgeDists;
            
            [WriteOnly] public NativeArray<int> CellToVert;
            
            [NativeDisableUnsafePtrRestriction] // Needed for safe multithreaded pointer access
            public NativeArray<int> Counter;

            public unsafe void Execute(int i)
            {
                // If not a segmented cell, mark as -1 so the triangle job ignores it
                if (!IsSurface[i]) { CellToVert[i] = -1; return; }

                // ATOMIC ALLOCATION: Claim a slot in the dense vertex arrays
                // Interlocked returns the value AFTER incrementing, so we subtract 1 for a 0-based index
                int vIdx = Interlocked.Increment(ref ((int*)Counter.GetUnsafePtr())[0]) - 1;
                CellToVert[i] = vIdx;

                Vector3 hit = Hits[i];
                int r = i / Cols;
                
                // Distance to the nearest boundary edge for this row
                float projR = Vector3.Dot(hit - WristPos, AxisRight);
                float dist = Mathf.Max(0f, Mathf.Min(projR - RowMin[r], RowMax[r] - projR));

                // Transform to Unity's local Mesh space
                OutVerts[vIdx] = WorldToLocal.MultiplyPoint3x4(hit);
                OutUVs[vIdx] = CalculateUV(hit);
                OutEdgeDists[vIdx] = new Vector2(dist, 0);
            }

            /// <summary>
            /// Generates cylindrical UV coordinates that compensate for arm orientation and twist.
            /// </summary>
            private Vector2 CalculateUV(Vector3 pt)
            {
                Vector3 fromWrist = pt - WristPos;
                bool isLandscape = Mathf.Abs(Orientation) > Mathf.PI * 0.25f;
                
                float distAlong = Vector3.Dot(fromWrist, Axis);
                float v = 1f - (((distAlong - Offset) / Mathf.Max(isLandscape ? Width : Height, 1e-4f)) + 0.5f);
                
                float projR = Vector3.Dot(fromWrist, AxisRight);
                float u = ((projR - ProjCenter) / Mathf.Max(isLandscape ? Height : Width, 1e-4f)) + 0.5f;
                
                u += (Pronation + (isLandscape ? Mathf.PI * 0.75f : 0f)) / Mathf.PI;

                float cu = u - 0.5f, cv = v - 0.5f;
                float cosA = Mathf.Cos(Orientation), sinA = Mathf.Sin(Orientation);
                return new Vector2(cu * cosA - cv * sinA + 0.5f, cu * sinA + cv * cosA + 0.5f);
            }
        }

        /// <summary>
        /// Iterates over every 2x2 grid block to form faces. Checks edge distances to prevent 
        /// connecting points across sharp depth gaps (e.g., from the arm to a table).
        /// </summary>
        [BurstCompile]
        struct TriangleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<int> CellToVert;
            public int Rows, Cols;
            public float MaxSq;
            
            [NativeDisableParallelForRestriction] public NativeArray<int> OutTris;
            [NativeDisableUnsafePtrRestriction] public NativeArray<int> Counter;

            public unsafe void Execute(int i)
            {
                // i ranges from 0 to ((Rows-1) * (Cols-1))
                int r = i / (Cols - 1);
                int c = i % (Cols - 1);

                // 1D Array indices for the 4 corners of the 2x2 block
                int idxTL = r * Cols + c;
                int idxTR = r * Cols + (c + 1);
                int idxBL = (r + 1) * Cols + c;
                int idxBR = (r + 1) * Cols + (c + 1);

                // Condensed Vertex Array Indices (-1 if empty)
                int tl = CellToVert[idxTL];
                int tr = CellToVert[idxTR];
                int bl = CellToVert[idxBL];
                int br = CellToVert[idxBR];

                // Count valid corners
                int vCount = (tl >= 0 ? 1 : 0) + (tr >= 0 ? 1 : 0) + (bl >= 0 ? 1 : 0) + (br >= 0 ? 1 : 0);
                
                // Cannot form a face with less than 3 valid points
                if (vCount < 3) return;

                // --------------------------------------------------
                // SCENARIO 1: Full Quad (All 4 corners present)
                // --------------------------------------------------
                if (vCount == 4)
                {
                    // Check the perimeter and the interior diagonal
                    if (CheckQuad(Hits[idxTL], Hits[idxTR], Hits[idxBL], Hits[idxBR]))
                    {
                        // Atomically claim 6 slots for two triangles
                        int start = Interlocked.Add(ref ((int*)Counter.GetUnsafePtr())[1], 6) - 6;
                        
                        // Counter-Clockwise winding
                        OutTris[start] = tl; OutTris[start + 1] = bl; OutTris[start + 2] = tr;
                        OutTris[start + 3] = tr; OutTris[start + 4] = bl; OutTris[start + 5] = br;
                    }
                }
                // --------------------------------------------------
                // SCENARIO 2: Single Triangle (Exactly 3 corners present)
                // --------------------------------------------------
                else
                {
                    if (tl < 0 && CheckTri(Hits[idxTR], Hits[idxBL], Hits[idxBR])) 
                        WriteTri(tr, bl, br); // Missing Top-Left
                    else if (tr < 0 && CheckTri(Hits[idxTL], Hits[idxBL], Hits[idxBR])) 
                        WriteTri(tl, bl, br); // Missing Top-Right
                    else if (bl < 0 && CheckTri(Hits[idxTL], Hits[idxBR], Hits[idxTR])) 
                        WriteTri(tl, br, tr); // Missing Bottom-Left
                    else if (br < 0 && CheckTri(Hits[idxTL], Hits[idxBL], Hits[idxTR])) 
                        WriteTri(tl, bl, tr); // Missing Bottom-Right
                }
            }

            /// <summary> Helper to allocate 3 slots and write counter-clockwise indices. </summary>
            private unsafe void WriteTri(int a, int b, int c) 
            {
                int start = Interlocked.Add(ref ((int*)Counter.GetUnsafePtr())[1], 3) - 3;
                OutTris[start] = a; OutTris[start + 1] = b; OutTris[start + 2] = c;
            }

            /// <summary> Checks if 4 points form a valid, non-stretched quad. Includes a diagonal check. </summary>
            bool CheckQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
                (a - b).sqrMagnitude < MaxSq && 
                (a - c).sqrMagnitude < MaxSq && 
                (c - d).sqrMagnitude < MaxSq && 
                (b - d).sqrMagnitude < MaxSq;

            /// <summary> Checks if 3 points form a valid, non-stretched triangle. </summary>
            bool CheckTri(Vector3 a, Vector3 b, Vector3 c) =>
                (a - b).sqrMagnitude < MaxSq && (b - c).sqrMagnitude < MaxSq && 
                (a - c).sqrMagnitude < MaxSq;
        }
    }
}