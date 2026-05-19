using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Manages the GPU side of the depth pipeline: blits Meta's environment
    /// depth texture through MetaDepthCopy.shader (which reconstructs world
    /// position per pixel) and AsyncGPUReadback's the cropped region back to
    /// the CPU as a NativeArray of world-space positions.
    /// </summary>
    public class DepthReadback : IDisposable
    {
        // World position is reconstructed in the shader, so the readback is a
        // float4 per pixel (xyz = world pos, w = valid flag). RGBAFloat is the
        // matching texture format.
        private const RenderTextureFormat WorldPosFormat = RenderTextureFormat.ARGBFloat;

        private Material _blitMaterial;
        private RenderTexture _worldPosRT;
        private bool _isReadbackPending = false;

        public DepthReadback()
        {
            Shader shader = Shader.Find("Hidden/MetaDepthCopy");
            if (shader == null)
            {
                Debug.LogError("[Depth] MetaDepthCopy shader not found. Check shader is in project and not stripped from build.");
                return;
            }
            _blitMaterial = new Material(shader);
            Debug.Log("[Depth] MetaDepthCopy shader loaded.");
        }

        // ID for Meta's depth-frame world->clip matrices, one per eye.
        // [0] is left eye, [1] is right eye. Set as a shader global by
        // EnvironmentDepthManager once per frame.
        private static readonly int s_ReprojectionMatricesId =
            Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");

        public void RequestDepth(
            int screenWidth, int screenHeight, 
            int xMin, int yMin, int width, int height, 
            Action<NativeArray<Vector4>, int, int, int, int> onComplete) // Note: Vector4!
        {
            if (_isReadbackPending) return;

            if (_worldPosRT == null || _worldPosRT.width != screenWidth || _worldPosRT.height != screenHeight)
            {
                if (_worldPosRT != null) _worldPosRT.Release();
                
                // CRITICAL FIX: Must be ARGBFloat to hold the X,Y,Z,W data from the shader!
                _worldPosRT = new RenderTexture(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBFloat);
                _worldPosRT.Create();
            }

            // CRITICAL FIX: Pass the physical depth camera matrix to the shader!
            Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            if (depthMatrices != null && depthMatrices.Length > 0)
            {
                _blitMaterial.SetMatrix("_DepthInverseVP", depthMatrices[0].inverse);
            }
            else
            {
                Debug.LogWarning("[Depth] Waiting for Meta's Depth API to provide matrices...");
                return;
            }

            Graphics.Blit(null, _worldPosRT, _blitMaterial);

            _isReadbackPending = true;
            AsyncGPUReadback.Request(_worldPosRT, 0, xMin, width, yMin, height, 0, 1, request =>
            {
                _isReadbackPending = false;

                if (request.hasError) 
                {
                    Debug.LogError("[Depth] GPU Readback Error!");
                    onComplete?.Invoke(default, xMin, yMin, width, height); 
                    return;
                }

                // Get the Vector4 array!
                NativeArray<Vector4> rawDepth = request.GetData<Vector4>();
                onComplete?.Invoke(rawDepth, xMin, yMin, width, height);
            });
        }

        public void Dispose()
        {
            if (_worldPosRT != null) _worldPosRT.Release();
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
        }
    }

    // --------------------------------------------------------
    // SCHEDULER (Main Thread)
    // --------------------------------------------------------
    public static class DepthSampler
    {
        /// <summary>
        /// Schedules a Burst job that samples the readback buffer at fixed pixel
        /// strides and copies world positions into the surface buffer. No camera
        /// math involved — the shader already produced world-space coordinates.
        /// </summary>
        public static JobHandle ScheduleUnprojection(
            NativeArray<Vector4> worldPositions,
            int croppedWidth,
            int croppedHeight,
            SurfaceBuffer buffer,
            int pixelStride,
            out int rows,
            out int cols)
        {
            cols = Mathf.Max(2, Mathf.CeilToInt((float)croppedWidth / pixelStride));
            rows = Mathf.Max(2, Mathf.CeilToInt((float)croppedHeight / pixelStride));

            buffer.ResizeIfNeeded(rows, cols);

            var job = new DepthUnprojectionJob
            {
                WorldPositions = worldPositions,
                DepthWidth = croppedWidth,
                DepthHeight = croppedHeight,
                PixelStride = pixelStride,
                Cols = cols,
                Hits = buffer.Hits,
                HasDepth = buffer.HasDepth,
                IsSurface = buffer.IsSurface
            };

            return job.Schedule(rows * cols, 64);
        }
    }

    // --------------------------------------------------------
    // BURST JOB (parallel background threads)
    // --------------------------------------------------------
    /// <summary>
    /// Reads world-space positions from the readback buffer at fixed strides.
    /// The shader has already done all unprojection work via Meta's depth-frame
    /// reprojection matrix, so this job is pure indexed copy + validity check.
    /// </summary>
    [BurstCompile]
    public struct DepthUnprojectionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector4> WorldPositions;

        public int DepthWidth;
        public int DepthHeight;
        public int PixelStride;
        public int Cols;

        [WriteOnly] public NativeArray<Vector3> Hits;
        [WriteOnly] public NativeArray<bool> HasDepth;
        [WriteOnly] public NativeArray<bool> IsSurface;

        public void Execute(int index)
        {
            int r = index / Cols;
            int c = index % Cols;

            int dx = c * PixelStride;
            int dy = r * PixelStride;

            // Clamp to prevent out-of-bounds at the bottom/right edges of
            // the cropped readback when (cols * stride) overshoots width.
            if (dx >= DepthWidth) dx = DepthWidth - 1;
            if (dy >= DepthHeight) dy = DepthHeight - 1;

            Vector4 sample = WorldPositions[dy * DepthWidth + dx];

            // The shader uses w < 0 as the invalid sentinel; valid pixels carry
            // their raw depth in w (which is in [0, 1]) for diagnostic logging.
            if (sample.w < 0f)
            {
                HasDepth[index] = false;
                IsSurface[index] = false;
                Hits[index] = Vector3.zero;
                return;
            }

            Hits[index] = new Vector3(sample.x, sample.y, sample.z);
            HasDepth[index] = true;
            IsSurface[index] = false;
        }
    }
}