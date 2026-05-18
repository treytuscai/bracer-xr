using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Surface.Helpers
{
    // --------------------------------------------------------
    // SEED JOB (Runs in parallel across all cells)
    // --------------------------------------------------------
    [BurstCompile]
    public struct SeedFromAxisJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> Hits;
        [ReadOnly] public NativeArray<bool> HasDepth;
        
        public NativeArray<bool> Kept;
        public NativeQueue<int>.ParallelWriter BFSQueueWriter;

        public Vector3 WristPos;
        public Vector3 ElbowPos;
        public Vector3 Axis;
        
        public float MaxRSq;
        public float MinFromWrist;
        public float MaxFromElbow;

        public void Execute(int index)
        {
            if (!HasDepth[index]) return;

            Vector3 p = Hits[index];

            float fromWrist = Vector3.Dot(p - WristPos, Axis);
            if (fromWrist < MinFromWrist) return;

            float fromElbow = Vector3.Dot(p - ElbowPos, Axis);
            if (fromElbow > MaxFromElbow) return;

            float radialSq = Vector3.Cross(p - ElbowPos, Axis).sqrMagnitude;

            if (radialSq <= MaxRSq)
            {
                Kept[index] = true;
                // Instantly enqueue this seed for the Flood pass
                BFSQueueWriter.Enqueue(index);
            }
        }
    }

    // --------------------------------------------------------
    // FLOOD JOB (Runs on a single background worker thread)
    // --------------------------------------------------------
    [BurstCompile]
    public struct FloodFromSeedsJob : IJob
    {
        public NativeQueue<int> BFSQueue;
        [ReadOnly] public NativeArray<Vector3> Hits;
        [ReadOnly] public NativeArray<bool> HasDepth;
        public NativeArray<bool> Kept;

        public int Cols;
        public int Rows;

        public Vector3 WristPos;
        public Vector3 ElbowPos;
        public Vector3 Axis;

        public float ConnSq;
        public float MaxFloodRadialSq;
        public float MinFromWrist;
        public float MaxFromElbow;

        public void Execute()
        {
            // Standard 8-way neighbor offsets
            NativeArray<int> dRow = new NativeArray<int>(8, Allocator.Temp) { [0]=-1, [1]=-1, [2]=-1, [3]=0, [4]=0, [5]=1, [6]=1, [7]=1 };
            NativeArray<int> dCol = new NativeArray<int>(8, Allocator.Temp) { [0]=-1, [1]=0, [2]=1, [3]=-1, [4]=1, [5]=-1, [6]=0, [7]=1 };

            while (BFSQueue.TryDequeue(out int idx))
            {
                int r = idx / Cols;
                int c = idx % Cols;
                Vector3 currentHit = Hits[idx];

                for (int n = 0; n < 8; n++)
                {
                    int nr = r + dRow[n];
                    int nc = c + dCol[n];

                    if (nr < 0 || nc < 0 || nr >= Rows || nc >= Cols) continue;

                    int nIdx = nr * Cols + nc;

                    if (Kept[nIdx] || !HasDepth[nIdx]) continue;

                    Vector3 neighborHit = Hits[nIdx];

                    if ((neighborHit - currentHit).sqrMagnitude > ConnSq) continue;

                    float radialSq = Vector3.Cross(neighborHit - ElbowPos, Axis).sqrMagnitude;
                    if (radialSq > MaxFloodRadialSq) continue;

                    float fromWrist = Vector3.Dot(neighborHit - WristPos, Axis);
                    if (fromWrist < MinFromWrist) continue;
                    
                    float fromElbow = Vector3.Dot(neighborHit - ElbowPos, Axis);
                    if (fromElbow > MaxFromElbow) continue;

                    Kept[nIdx] = true;
                    BFSQueue.Enqueue(nIdx);
                }
            }
        }
    }
}