using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using System;

namespace Surface.Helpers
{
    public class DepthReadback : IDisposable
    {
        private Material _blitMaterial;
        private RenderTexture _depthRT;
        private bool _isReadbackPending = false;

        public DepthReadback()
        {
            // Load the shader we just created
            Shader shader = Shader.Find("Hidden/MetaDepthCopy");
            _blitMaterial = new Material(shader);
        }

        public void RequestDepth(
            int screenWidth, int screenHeight, 
            int xMin, int yMin, int width, int height, 
            Action<NativeArray<float>, int, int, int, int> onComplete)
        {
            if (_isReadbackPending) return;

            if (_depthRT == null || _depthRT.width != screenWidth || _depthRT.height != screenHeight)
            {
                if (_depthRT != null) _depthRT.Release();
                _depthRT = new RenderTexture(screenWidth, screenHeight, 0, RenderTextureFormat.RFloat);
                _depthRT.Create();
            }

            Graphics.Blit(null, _depthRT, _blitMaterial);

            _isReadbackPending = true;
            int gpuY = screenHeight - yMin - height;
            AsyncGPUReadback.Request(_depthRT, 0, xMin, width, gpuY, height, 0, 1, request =>
            {
                _isReadbackPending = false;

                if (request.hasError) 
                {
                    Debug.LogError("[Depth] GPU Readback Error!");
                    // Pass the bounds back even on failure
                    onComplete?.Invoke(default, xMin, yMin, width, height);
                    return;
                }

                NativeArray<float> rawDepth = request.GetData<float>();
                // Pass the bounds back along with the data!
                onComplete?.Invoke(rawDepth, xMin, yMin, width, height);
            });
        }

        public void Dispose()
        {
            if (_depthRT != null) _depthRT.Release();
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
        }
    }

    // --------------------------------------------------------
    // THE SCHEDULER (Runs on the Main Thread)
    // --------------------------------------------------------
    public static class DepthSampler
    {
        public static JobHandle ScheduleUnprojection(
            Matrix4x4 cameraLocalToWorld, 
            Matrix4x4 cameraProjection, 
            int screenWidth, 
            int screenHeight,
            NativeArray<float> rawDepthArray, 
            int startX, 
            int startY, 
            int width, 
            int height,
            SurfaceBuffer buffer, 
            float pixelStride,
            out int rows, 
            out int cols)
        {
            // Grid size based on the cropped region
            cols = Mathf.Max(2, Mathf.CeilToInt(width / pixelStride));
            rows = Mathf.Max(2, Mathf.CeilToInt(height / pixelStride));

            buffer.ResizeIfNeeded(rows, cols);

            var job = new DepthUnprojectionJob
            {
                DepthArray = rawDepthArray,
                DepthWidth = width,   
                DepthHeight = height, 
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                StartX = startX,
                StartY = startY,
                PixelStride = (int)pixelStride,
                Cols = cols,
                LocalToWorld = cameraLocalToWorld,
                ProjM00 = cameraProjection.m00,
                ProjM11 = cameraProjection.m11,
                ProjM02 = cameraProjection.m02,
                ProjM12 = cameraProjection.m12,
                Hits = buffer.Hits,
                HasDepth = buffer.HasDepth,
                IsSurface = buffer.IsSurface
            };

            return job.Schedule(rows * cols, 64);
        }
    }

    // --------------------------------------------------------
    // THE BURST JOB (Runs in parallel across background cores)
    // --------------------------------------------------------
    [BurstCompile]
    public struct DepthUnprojectionJob : IJobParallelFor
    {
        // Raw depth array from Meta XR EnvironmentDepthManager
        [ReadOnly] public NativeArray<float> DepthArray; 
        
        public int DepthWidth;
        public int DepthHeight;
        public int ScreenWidth;
        public int ScreenHeight;

        public int StartX;
        public int StartY;
        public int PixelStride;
        public int Cols;

        // Camera Math for Unprojection
        public Matrix4x4 LocalToWorld;
        public float ProjM00; // camera.projectionMatrix[0, 0]
        public float ProjM11; // camera.projectionMatrix[1, 1]
       public float ProjM02;
       public float ProjM12;

        // Output Buffers
        [WriteOnly] public NativeArray<Vector3> Hits;
        [WriteOnly] public NativeArray<bool> HasDepth;
        [WriteOnly] public NativeArray<bool> IsSurface;

        public void Execute(int index)
        {
            int r = index / Cols;
            int c = index % Cols;

            // Screen pixel coordinate for the FULL screen
            int px = StartX + c * PixelStride;
            int py = StartY + r * PixelStride;

            // 1. Camera Math needs the FULL screen UVs (0.0 to 1.0)
            float u = (float)px / ScreenWidth;
            float v = (float)py / ScreenHeight;

            // 2. Array is CROPPED! We index it locally, relative to the bounding box
            int dx = c * PixelStride;
            int dy = r * PixelStride; 

            // Clamp to prevent out-of-bounds crashes
            dx = dx >= DepthWidth ? DepthWidth - 1 : (dx < 0 ? 0 : dx);
            dy = dy >= DepthHeight ? DepthHeight - 1 : (dy < 0 ? 0 : dy);

            int depthIndex = dy * DepthWidth + dx;
            float depthMeters = DepthArray[depthIndex];

            // 3. Reject invalid depth
            if (depthMeters <= 0.001f)
            {
                HasDepth[index] = false;
                IsSurface[index] = false;
                Hits[index] = Vector3.zero;
                return;
            }

            // 4. Unproject 2D -> 3D Local Camera Space
            float ndcX = u * 2f - 1f;
            float ndcY = v * 2f - 1f;

            // FIX 1: Algebraic sign for lens distortion offset is ADD (+)
            // FIX 2: Unity View Space looks down NEGATIVE Z!
            float localX = ((ndcX + ProjM02) / ProjM00) * depthMeters;
            float localY = ((ndcY + ProjM12) / ProjM11) * depthMeters;
            Vector3 localPos = new Vector3(localX, localY, -depthMeters);

            // FIX 3: Multiply by the true Left Eye Inverse View Matrix
            Hits[index] = LocalToWorld.MultiplyPoint3x4(localPos);
            HasDepth[index] = true;
            IsSurface[index] = false; 
        }
    }
}