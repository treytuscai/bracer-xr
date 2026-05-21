using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Surface.Buffer;
using Unity.Mathematics;

namespace Surface.Core
{
    public class TemporalInfill
    {
        private const int UV_V = SurfaceBuffer.AtlasV;
        private const int UV_U = SurfaceBuffer.AtlasU;

        public JobHandle Schedule(
            SurfaceBuffer buffer, int rows, int cols,
            Vector3 cameraPos,
            Vector3 wristPos, Vector3 axis, Vector3 sRight, Vector3 sUp,
            float boneLength, float maxRadialDist, JobHandle dependency)
        {
            var updateJob = new UpdateAtlasJob
            {
                Hits = buffer.Hits,
                HasDepth = buffer.HasDepth,
                IsHandMasked = buffer.IsHandMasked,
                AtlasRadius = buffer.AtlasRadius,
                AtlasWeights = buffer.AtlasWeights,
                WristPos = wristPos, Axis = axis, 
                StableRight = sRight, StableUp = sUp,
                BoneLength = boneLength,
                MaxRadialSq = maxRadialDist * maxRadialDist,
                LerpRate = 0.15f
            };
            var updateHandle = updateJob.Schedule(rows * cols, 64, dependency);

            var infillJob = new InfillFromAtlasJob
            {
                Hits = buffer.Hits,
                HasDepth = buffer.HasDepth,
                IsHandMasked = buffer.IsHandMasked,
                AtlasRadius = buffer.AtlasRadius,
                AtlasWeights = buffer.AtlasWeights,
                CameraPos = cameraPos,
                WristPos = wristPos, Axis = axis, 
                StableRight = sRight, StableUp = sUp,
                BoneLength = boneLength
            };
            return infillJob.Schedule(rows * cols, 64, updateHandle);
        }

        [BurstCompile]
        struct UpdateAtlasJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> HasDepth;
            [ReadOnly] public NativeArray<bool> IsHandMasked;

            public NativeArray<float> AtlasRadius;
            public NativeArray<float> AtlasWeights;

            public Vector3 WristPos, Axis, StableRight, StableUp;
            public float BoneLength, MaxRadialSq, LerpRate;

            public void Execute(int i)
            {
                if (!HasDepth[i] || IsHandMasked[i]) return;

                Vector3 localPos = Hits[i] - WristPos;
                float v = Vector3.Dot(localPos, Axis);
                float rR = Vector3.Dot(localPos, StableRight);
                float rU = Vector3.Dot(localPos, StableUp);
                
                float radiusSq = rR * rR + rU * rU;
                if (radiusSq > MaxRadialSq) return;

                float vNorm = v / BoneLength;
                if (vNorm < 0 || vNorm > 1) return;

                float uNorm = (Mathf.Atan2(rU, rR) + Mathf.PI) / (2f * Mathf.PI);

                int uIdx = (int)(uNorm * (UV_U - 1));
                int vIdx = (int)(vNorm * (UV_V - 1));
                int atlasIdx = vIdx * UV_U + uIdx;

                float currentRadius = Mathf.Sqrt(radiusSq);

                if (AtlasWeights[atlasIdx] < 0.01f) {
                    AtlasRadius[atlasIdx] = currentRadius;
                    AtlasWeights[atlasIdx] = 1.0f;
                } else {
                    AtlasRadius[atlasIdx] = Mathf.Lerp(AtlasRadius[atlasIdx], currentRadius, LerpRate);
                }
            }
        }

        [BurstCompile]
        struct InfillFromAtlasJob : IJobParallelFor
        {
            public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool> HasDepth;
            public NativeArray<bool> IsHandMasked;

            [ReadOnly] public NativeArray<float> AtlasRadius;
            [ReadOnly] public NativeArray<float> AtlasWeights;

            public Vector3 CameraPos, WristPos, Axis, StableRight, StableUp;
            public float BoneLength;

            public void Execute(int i)
            {
                if (!HasDepth[i] || !IsHandMasked[i]) return;

                // 1. Transform Camera and Ray into Arm-Local Space
                Vector3 rayDirWorld = math.normalize(Hits[i] - CameraPos);
                Vector3 localCam = CameraPos - WristPos;
                
                float cx = math.dot(localCam, StableRight);
                float cy = math.dot(localCam, StableUp);
                float cz = math.dot(localCam, Axis);

                float dx = math.dot(rayDirWorld, StableRight);
                float dy = math.dot(rayDirWorld, StableUp);
                float dz = math.dot(rayDirWorld, Axis);

                // Quadratic Equation components for Ray-Cylinder Intersection
                float A = dx * dx + dy * dy;
                float B = 2f * (cx * dx + cy * dy);
                float C_base = cx * cx + cy * cy;

                if (A < 1e-5f) return; // Ray is parallel to bone, skip

                float currentRadius = 0.045f; // Guess: 4.5cm bounding cylinder
                float finalS = -1f;
                bool foundHit = false;

                // 2. ITERATIVE RAY MARCH (Converges in 2-3 steps)
                for (int step = 0; step < 3; step++)
                {
                    float C = C_base - currentRadius * currentRadius;
                    float discriminant = B * B - 4f * A * C;
                    
                    if (discriminant < 0f) break; // Ray missed

                    // Distance along camera ray to hit the current cylinder
                    float s = (-B - math.sqrt(discriminant)) / (2f * A);
                    
                    // Local hit coordinates
                    float px = cx + s * dx;
                    float py = cy + s * dy;
                    float pz = cz + s * dz;

                    float vNorm = pz / BoneLength;
                    if (vNorm < 0f || vNorm > 1f) break;

                    float uNorm = (math.atan2(py, px) + math.PI) / (2f * math.PI);

                    // Check actual learned skin depth at this coordinate
                    float sampledRadius = SampleAtlas(uNorm, vNorm, out float weight);
                    
                    if (weight < 0.1f) {
                        // Unseen area. Accept the fallback radius.
                        finalS = s;
                        foundHit = true;
                        break; 
                    }

                    // If the ray intersected the geometry exactly where we expected, we're done!
                    if (math.abs(sampledRadius - currentRadius) < 0.001f) {
                        finalS = s;
                        foundHit = true;
                        break;
                    }

                    // Update guess to the true radius and shoot the ray again
                    currentRadius = sampledRadius;
                    finalS = s; 
                    foundHit = true;
                }

                // 3. APPLY EXACT RECONSTRUCTION
                if (foundHit && finalS > 0f)
                {
                    // CRUCIAL: Point is pushed perfectly down the camera ray!
                    // This is why the quads will no longer collapse or fragment.
                    Hits[i] = CameraPos + rayDirWorld * finalS;
                    IsHandMasked[i] = false;
                }
            }

            private float SampleAtlas(float u, float v, out float weight)
            {
                float uIdx = math.clamp(u, 0, 1) * (UV_U - 1);
                float vIdx = math.clamp(v, 0, 1) * (UV_V - 1);

                int u0 = (int)uIdx; 
                int u1 = (u0 + 1) % UV_U;
                int v0 = (int)vIdx; 
                int v1 = math.min(v0 + 1, UV_V - 1);

                float fU = uIdx - u0;
                float fV = vIdx - v0;

                int i00 = v0 * UV_U + u0; int i10 = v0 * UV_U + u1;
                int i01 = v1 * UV_U + u0; int i11 = v1 * UV_U + u1;

                weight = AtlasWeights[i00];

                return math.lerp(
                    math.lerp(AtlasRadius[i00], AtlasRadius[i10], fU),
                    math.lerp(AtlasRadius[i01], AtlasRadius[i11], fU), fV);
            }
        }
    }
}