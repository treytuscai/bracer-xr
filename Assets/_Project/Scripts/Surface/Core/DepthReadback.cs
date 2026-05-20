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
    /// Manages the full GPU->CPU depth pipeline: blits Meta's environment
    /// depth texture through MetaDepthCopy.shader, reads back the cropped
    /// region via AsyncGPUReadback, then schedules a Burst job to unproject
    /// the raw pixels into world-space hits in SurfaceBuffer.
    ///
    /// The callback fires with a JobHandle the caller can chain into
    /// downstream jobs (masking, extraction, etc.) plus the grid dimensions.
    /// On error, fires with (default, 0, 0) so the caller can bail cleanly.
    /// </summary>
    public class DepthReadback : IDisposable
    {
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

        /// <summary>
        /// Requests an async GPU readback of the cropped depth region.
        /// When the readback completes, schedules the unprojection job
        /// and invokes onComplete with the resulting JobHandle and grid size.
        /// </summary>
        public void Schedule(
            int screenWidth, int screenHeight,
            int xMin, int yMin, int width, int height,
            SurfaceBuffer buffer, int pixelStride,
            Action<JobHandle, int, int> onComplete)
        {
            if (_isReadbackPending) return;

            if (_worldPosRT == null || _worldPosRT.width != screenWidth || _worldPosRT.height != screenHeight)
            {
                if (_worldPosRT != null) _worldPosRT.Release();
                
                _worldPosRT = new RenderTexture(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBFloat);
                _worldPosRT.Create();
            }

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
                        onComplete?.Invoke(default, 0, 0);
                        return;
                    }

                    NativeArray<Vector4> raw = request.GetData<Vector4>();
                    if (!raw.IsCreated || raw.Length == 0)
                    {
                        onComplete?.Invoke(default, 0, 0);
                        return;
                    }

                    // Schedule the unprojection job that fills buffer.Hits
                    int cols = Mathf.Max(2, Mathf.CeilToInt((float)width / pixelStride));
                    int rows = Mathf.Max(2, Mathf.CeilToInt((float)height / pixelStride));
                    buffer.ResizeIfNeeded(rows, cols);

                    var job = new DepthUnprojectionJob
                    {
                        WorldPositions = raw,
                        DepthWidth     = width,
                        DepthHeight    = height,
                        PixelStride    = pixelStride,
                        Cols           = cols,
                        Hits           = buffer.Hits,
                        HasDepth       = buffer.HasDepth,
                        IsSurface      = buffer.IsSurface
                    };

                    JobHandle handle = job.Schedule(rows * cols, 64);
                    onComplete?.Invoke(handle, rows, cols);
                });
        }

        public void Dispose()
        {
            if (_worldPosRT != null) _worldPosRT.Release();
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
        }

        // --------------------------------------------------------
        // BURST JOB
        // --------------------------------------------------------
        /// <summary>
        /// Reads world-space positions from the readback buffer at fixed strides.
        /// The shader has already done all unprojection work via Meta's depth-frame
        /// reprojection matrix, so this job is pure indexed copy + validity check.
        /// </summary>
        [BurstCompile]
        private struct DepthUnprojectionJob : IJobParallelFor
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

                if (dx >= DepthWidth) dx = DepthWidth - 1;
                if (dy >= DepthHeight) dy = DepthHeight - 1;

                Vector4 sample = WorldPositions[dy * DepthWidth + dx];

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
}