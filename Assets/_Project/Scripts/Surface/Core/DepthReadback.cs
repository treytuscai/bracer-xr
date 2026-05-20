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
    /// Manages the full GPU->CPU depth pipeline: validates Meta's depth matrices,
    /// computes the screen-space crop around the forearm, blits through
    /// MetaDepthCopy.shader, reads back via AsyncGPUReadback, then schedules
    /// a Burst job to unproject into world-space hits in SurfaceBuffer.
    ///
    /// The callback fires with a JobHandle the caller can chain into
    /// downstream jobs (masking, extraction, etc.) plus the grid dimensions.
    /// On error or if arm is off-screen, silently returns without invoking.
    /// </summary>
    public class DepthReadback : IDisposable
    {
        private Material _blitMaterial;
        private RenderTexture _worldPosRT;
        private bool _isReadbackPending;

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
        /// Computes the crop region from ArmFrame, validates depth matrices,
        /// requests the GPU readback, and schedules unprojection on completion.
        /// Silently returns if a readback is in flight or the arm is off-screen.
        /// </summary>
        public void Schedule(
            ArmFrame arm,
            float maxRadialDist, int pixelStride,
            SurfaceBuffer buffer,
            Action<JobHandle, int, int> onComplete)
        {
            if (_isReadbackPending) return;

            // Depth matrix validation (Meta updates per head pose)
            Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            if (depthMatrices == null || depthMatrices.Length == 0) return;

            // Compute screen-space crop around the forearm
            Camera cam = arm.Cam;
            Vector3 wristPos = arm.WristPos;
            Vector3 elbowPos = arm.ElbowPos;
            Vector3 camPos   = cam.transform.position;

            if (!CalculateArmBounds(
                ref depthMatrices[0], ref wristPos, ref elbowPos, ref camPos,
                cam.fieldOfView, cam.pixelWidth, cam.pixelHeight,
                maxRadialDist, pixelStride,
                out int cropX, out int cropY, out int cropW, out int cropH))
            {
                return; // Arm behind camera or off-screen
            }

            // Ensure render target matches screen dimensions
            int screenW = cam.pixelWidth;
            int screenH = cam.pixelHeight;
            if (_worldPosRT == null || _worldPosRT.width != screenW || _worldPosRT.height != screenH)
            {
                if (_worldPosRT != null) _worldPosRT.Release();
                _worldPosRT = new RenderTexture(screenW, screenH, 0, RenderTextureFormat.ARGBFloat);
                _worldPosRT.Create();
            }

            _blitMaterial.SetMatrix("_DepthInverseVP", depthMatrices[0].inverse);
            Graphics.Blit(null, _worldPosRT, _blitMaterial);

            _isReadbackPending = true;
            AsyncGPUReadback.Request(
                _worldPosRT, 0, cropX, cropW, cropY, cropH, 0, 1,
                request =>
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

                    int cols = Mathf.Max(2, Mathf.CeilToInt((float)cropW / pixelStride));
                    int rows = Mathf.Max(2, Mathf.CeilToInt((float)cropH / pixelStride));
                    buffer.ResizeIfNeeded(rows, cols);

                    var job = new DepthUnprojectionJob
                    {
                        WorldPositions = raw,
                        DepthWidth     = cropW,
                        DepthHeight    = cropH,
                        PixelStride    = pixelStride,
                        Cols           = cols,
                        Hits           = buffer.Hits,
                        HasDepth       = buffer.HasDepth,
                        IsSurface      = buffer.IsSurface
                    };

                    onComplete?.Invoke(job.Schedule(rows * cols, 64), rows, cols);
                });
        }

        public void Dispose()
        {
            if (_worldPosRT != null) _worldPosRT.Release();
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
        }

        // --------------------------------------------------------
        // SCREEN-SPACE CROP
        // --------------------------------------------------------

        private static bool CalculateArmBounds(
            ref Matrix4x4 depthVP,
            ref Vector3 wristPos, ref Vector3 elbowPos, ref Vector3 camPos,
            float fov, int pixelWidth, int pixelHeight,
            float maxRadialDist, float pixelStride,
            out int xMin, out int yMin, out int width, out int height)
        {
            xMin = yMin = width = height = 0;

            float halfWidth = pixelWidth * 0.5f;
            float halfHeight = pixelHeight * 0.5f;

            // Project Wrist
            ProjectPoint(ref depthVP, ref wristPos, halfWidth, halfHeight,
                         out float wristX, out float wristY, out float wristW);
            if (wristW <= 0f) return false;

            // Project Elbos
            ProjectPoint(ref depthVP, ref elbowPos, halfWidth, halfHeight,
                         out float elbowX, out float elbowY, out float elbowW);
            if (elbowW <= 0f) return false;

            // Unrolled distance calculation (avoids Vector3 struct allocation)
            float midX = (wristPos.x + elbowPos.x) * 0.5f - camPos.x;
            float midY = (wristPos.y + elbowPos.y) * 0.5f - camPos.y;
            float midZ = (wristPos.z + elbowPos.z) * 0.5f - camPos.z;
            float armMidDist = Mathf.Sqrt(midX * midX + midY * midY + midZ * midZ);

            // Hardcode Deg2Rad (0.01745329f) to avoid Unity constant lookup
            float focalPx = pixelHeight / (2f * Mathf.Tan(fov * 0.5f * 0.0174532924f));
            float dynamicPadding = (maxRadialDist / armMidDist) * focalPx;

            // Fast inline Min/Max using ternary operators
            float minX = wristX < elbowX ? wristX : elbowX;
            float maxX = wristX > elbowX ? wristX : elbowX;
            float minY = wristY < elbowY ? wristY : elbowY;
            float maxY = wristY > elbowY ? wristY : elbowY;

            // Fast clamp to screen bounds
            float fXMin = minX - dynamicPadding;
            float fXMax = maxX + dynamicPadding;
            float fYMin = minY - dynamicPadding;
            float fYMax = maxY + dynamicPadding;

            fXMin = fXMin > 0f ? fXMin : 0f;
            fXMax = fXMax < pixelWidth ? fXMax : pixelWidth;
            fYMin = fYMin > 0f ? fYMin : 0f;
            fYMax = fYMax < pixelHeight ? fYMax : pixelHeight;

            if (fXMax - fXMin < pixelStride || fYMax - fYMin < pixelStride) return false;

            xMin   = (int)fXMin;
            yMin   = (int)fYMin;
            width  = (int)(fXMax - fXMin);
            height = (int)(fYMax - fYMin);

            return true;
        }

        private static void ProjectPoint(
            ref Matrix4x4 vp, ref Vector3 pos,
            float halfW, float halfH,
            out float pxX, out float pxY, out float w)
        {
            // Calculate W first to check if the point is behind the camera
            w = vp.m30 * pos.x + vp.m31 * pos.y + vp.m32 * pos.z + vp.m33;
            if (w <= 0.0001f) { pxX = pxY = 0f; return; }

            // Manually inline matrix multiplication (Skipping the Z-Row entirely)
            float x = vp.m00 * pos.x + vp.m01 * pos.y + vp.m02 * pos.z + vp.m03;
            float y = vp.m10 * pos.x + vp.m11 * pos.y + vp.m12 * pos.z + vp.m13;

            // Mathematically simplified UV conversion: (clip / w + 1) * halfSize
            float invW = 1f / w;
            pxX = (x + w) * halfW * invW;
            pxY = (y + w) * halfH * invW;
        }

        // --------------------------------------------------------
        // BURST JOB
        // --------------------------------------------------------

        [BurstCompile]
        private struct DepthUnprojectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector4> WorldPositions;
            public int DepthWidth, DepthHeight, PixelStride, Cols;

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