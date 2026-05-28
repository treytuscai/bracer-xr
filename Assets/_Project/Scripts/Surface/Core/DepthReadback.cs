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
    /// Manages the full GPU->CPU depth pipeline each frame:
    ///   1. BLIT:     Run MetaDepthCopy.shader via Graphics.Blit to reconstruct a
    ///                world-space position for every pixel into a full-screen RenderTexture.
    ///   2. CROP:     Compute the screen-space bounding box of the forearm and request
    ///                an async GPU readback of only that region.
    ///   3. UNPROJECT:On readback completion, schedule a Burst job (DepthUnprojectionJob)
    ///                that downsamples the crop at pixelStride intervals into the flat
    ///                world-space hit grid stored in SurfaceBuffer.
    ///   4. HAND-OFF: Invoke the caller's callback with a JobHandle the downstream
    ///                pipeline (masking, extraction) can chain dependencies onto.
    ///
    /// DEPTH SOURCE
    /// Despite the name, Meta's environment depth API does not use a dedicated IR depth
    /// sensor. It computes depth from the Quest's two main RGB cameras via stereo
    /// reconstruction. The output is still a standard [0,1] NDC depth texture — the
    /// source hardware doesn't change any of the math.
    ///
    /// RECONSTRUCTION (community-verified, see MetaDepthCopy.shader for implementation)
    /// Each depth pixel can be unprojected to a world position in four steps:
    ///   1. Sample the R channel of _EnvironmentDepthTexture -> rawDepth ∈ (0,1).
    ///      Only the R channel is used. There is no packed overflow into G/B/A —
    ///      Meta's depth texture uses a float format where the full value fits in R.
    ///   2. Build a homogeneous clip-space point: (U*2-1, V*2-1, rawDepth*2-1, 1).
    ///      UV becomes XY, raw depth becomes Z — all remapped from [0,1] to [-1,1].
    ///   3. Multiply by the inverse of _EnvironmentDepthReprojectionMatrices[0].
    ///      This matrix is the depth frame's world->clip VP; its inverse goes clip->world.
    ///   4. Perspective divide: worldPos = homogenousResult.xyz / homogenousResult.w.
    ///
    /// WHY META'S VP MATRIX, NOT UNITY'S CAMERA VP?
    /// Meta captures _EnvironmentDepthReprojectionMatrices at the exact moment the depth
    /// frame was captured. Using Unity's camera VP (current render pose) would desync the
    /// reconstruction — depth pixels correspond to an earlier head pose, so they'd project
    /// to the wrong world positions and the surface would visually swim as the user moves.
    ///
    /// WHY INVERT ON CPU BEFORE THE BLIT?
    /// The shader needs the inverse to go clip->world. Matrix inversion in HLSL is
    /// expensive per-pixel. Computing it once on the CPU costs one 4×4 inversion per frame.
    /// </summary>
    public class DepthReadback : IDisposable
    {
        // ------------------------------------------------------------------
        // STATE
        // ------------------------------------------------------------------
        // Material wrapping MetaDepthCopy.shader; reconstructs world positions
        // from the depth texture into _worldPosRT during the blit step.
        private Material _blitMaterial;
        // Full-screen ARGBFloat RenderTexture holding one world-space Vector4
        // per pixel (xyz = world position, w = raw depth / -1 if invalid).
        // Full-screen because the depth texture covers the full view; the crop
        // is applied during AsyncGPUReadback.Request, not during the blit.
        private RenderTexture _worldPosRT;
        // Set to true when AsyncGPUReadback.Request is enqueued, false at the
        // start of its callback. Reset at callback start (not end) so that
        // exceptions in downstream processing don't permanently stall the pipeline.
        private bool _isReadbackPending;

        /// <summary>
        /// Loads the MetaDepthCopy shader and creates the blit material.
        /// The shader must be present in the project and not stripped from builds.
        /// </summary>
        public DepthReadback()
        {
            Shader shader = Shader.Find("Hidden/MetaDepthCopy");
            if (shader == null)
            {
                Debug.LogError("[Depth] MetaDepthCopy shader not found. Check shader is in project and not stripped from build.");
                return;
            }
            _blitMaterial = new Material(shader);
        }

        /// <summary>
        /// Validates depth matrices, crops the forearm region, blits world positions,
        /// and enqueues an async GPU readback + Burst unproject job.
        /// Returns true only when a readback was actually enqueued; false on any
        /// early-out (readback in flight, no depth matrices, arm off-screen, shader missing).
        /// Callers must only arm their in-flight guard when this returns true.
        /// </summary>
        public bool Schedule(
            ArmFrame arm,
            float maxRadialDist, int pixelStride,
            SurfaceBuffer buffer,
            Action<JobHandle, int, int> onComplete)
        {
            // Abort if the shader failed to load at construction.
            if (_blitMaterial == null) return false;
            if (_isReadbackPending) return false;

            // Meta sets _EnvironmentDepthReprojectionMatrices once per depth frame.
            // Index 0 is the left eye world->clip matrix for the depth camera's pose
            // at the time that depth frame was captured (not the current render pose).
            Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            if (depthMatrices == null || depthMatrices.Length == 0) return false;

            Camera cam = arm.Cam;
            Vector3 wristPos = arm.WristPos;
            Vector3 elbowPos = arm.ElbowPos;
            Vector3 camPos   = cam.transform.position;

            // Compute the screen-space bounding box of the forearm using the depth
            // camera's VP matrix so crop coordinates align with the depth texture.
            if (!CalculateArmBounds(
                ref depthMatrices[0], ref wristPos, ref elbowPos, ref camPos,
                cam.fieldOfView, cam.pixelWidth, cam.pixelHeight,
                maxRadialDist, pixelStride,
                out int cropX, out int cropY, out int cropW, out int cropH))
            {
                return false; // Arm behind camera or off-screen
            }

            // Resize the world-position RenderTexture if the screen resolution changed.
            int screenW = cam.pixelWidth;
            int screenH = cam.pixelHeight;
            if (_worldPosRT == null || _worldPosRT.width != screenW || _worldPosRT.height != screenH)
            {
                if (_worldPosRT != null) _worldPosRT.Release();
                // ARGBFloat = 4×32-bit floats per pixel, needed to store world-space XYZ + depth.
                _worldPosRT = new RenderTexture(screenW, screenH, 0, RenderTextureFormat.ARGBFloat);
                _worldPosRT.Create();
            }

            // Invert the depth frame's world->clip matrix once on the CPU.
            // The shader uses this to go clip->world (steps 3–4 of the reconstruction).
            // depthMatrices[0] is the left-eye VP for the pose at depth-capture time.
            _blitMaterial.SetMatrix("_DepthInverseVP", depthMatrices[0].inverse);
            // Blit runs MetaDepthCopy.shader over every screen pixel, executing the full
            // reconstruction pipeline (sample R -> build clip point -> inverse VP -> perspective
            // divide) and writing the resulting world position into each pixel of _worldPosRT
            // as a Vector4 (xyz = world pos, w = rawDepth, or w = -1 for invalid pixels).
            // Null source: the shader reads _EnvironmentDepthTexture as a global directly.
            Graphics.Blit(null, _worldPosRT, _blitMaterial);

            // Request only the crop sub-region to minimize PCIe/memory transfer.
            // Parameters: source, mip, startX, width, startY, height, startZ, depth, callback.
            _isReadbackPending = true;
            AsyncGPUReadback.Request(
                _worldPosRT, 0, cropX, cropW, cropY, cropH, 0, 1,
                request =>
                {
                    // Reset before any processing so exceptions downstream
                    // don't permanently stall the pipeline.
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

                    // Compute grid dimensions from the crop size and stride.
                    // Minimum of 2 in each dimension: a 1×N grid produces no quads,
                    // and boundary smoothing downstream requires at least 2 cells.
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
            return true;
        }

        /// <summary>
        /// Releases the RenderTexture and blit material. Call when the component is destroyed.
        /// </summary>
        public void Dispose()
        {
            if (_worldPosRT != null) _worldPosRT.Release();
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
        }

        // --------------------------------------------------------
        // SCREEN-SPACE CROP
        // --------------------------------------------------------

        /// <summary>
        /// Projects the wrist and elbow into screen space using the depth camera's VP matrix,
        /// expands the bounding box by a perspective-correct margin derived from maxRadialDist,
        /// and clamps to screen bounds. Returns false if either bone is behind the camera or
        /// the resulting crop is smaller than one pixel stride.
        ///
        /// Uses ref parameters throughout to avoid copying Matrix4x4 (64 bytes) and
        /// Vector3 (12 bytes) structs on every call.
        /// </summary>
        private static bool CalculateArmBounds(
            ref Matrix4x4 depthVP,
            ref Vector3 wristPos, ref Vector3 elbowPos, ref Vector3 camPos,
            float fov, int pixelWidth, int pixelHeight,
            float maxRadialDist, float pixelStride,
            out int xMin, out int yMin, out int width, out int height)
        {
            xMin = yMin = width = height = 0;

            float halfWidth  = pixelWidth  * 0.5f;
            float halfHeight = pixelHeight * 0.5f;

            // Project wrist and elbow into pixel space using the depth frame's VP.
            // We use depthVP (not Unity's camera VP) so crop coordinates align with
            // the depth texture, which was captured at a different head pose.
            ProjectPoint(ref depthVP, ref wristPos, halfWidth, halfHeight,
                         out float wristX, out float wristY, out float wristW);
            if (wristW <= 0f) return false;

            ProjectPoint(ref depthVP, ref elbowPos, halfWidth, halfHeight,
                         out float elbowX, out float elbowY, out float elbowW);
            if (elbowW <= 0f) return false;

            // Compute the arm midpoint's distance from the camera in world space.
            // Unrolled manually to avoid allocating a Vector3 struct on the heap.
            float midX = (wristPos.x + elbowPos.x) * 0.5f - camPos.x;
            float midY = (wristPos.y + elbowPos.y) * 0.5f - camPos.y;
            float midZ = (wristPos.z + elbowPos.z) * 0.5f - camPos.z;
            float armMidDist = Mathf.Sqrt(midX * midX + midY * midY + midZ * midZ);

            // Convert maxRadialDist (world meters) to screen pixels at the arm's depth.
            // focalPx is the pinhole camera focal length in pixels: f = h / (2 * tan(fov/2)).
            // Hardcoding Deg2Rad (0.0174532924f) avoids a Unity constant lookup per call.
            float focalPx       = pixelHeight / (2f * Mathf.Tan(fov * 0.5f * 0.0174532924f));
            // At distance d, a world-space radius r subtends r/d radians, so r/d * f pixels.
            float dynamicPadding = (maxRadialDist / armMidDist) * focalPx;

            // Bounding box of the two projected points, expanded by the padding.
            float minX = wristX < elbowX ? wristX : elbowX;
            float maxX = wristX > elbowX ? wristX : elbowX;
            float minY = wristY < elbowY ? wristY : elbowY;
            float maxY = wristY > elbowY ? wristY : elbowY;

            float fXMin = minX - dynamicPadding;
            float fXMax = maxX + dynamicPadding;
            float fYMin = minY - dynamicPadding;
            float fYMax = maxY + dynamicPadding;

            // Clamp to screen bounds using branchless ternary (avoids Mathf.Clamp overhead).
            fXMin = fXMin > 0f ? fXMin : 0f;
            fXMax = fXMax < pixelWidth  ? fXMax : pixelWidth;
            fYMin = fYMin > 0f ? fYMin : 0f;
            fYMax = fYMax < pixelHeight ? fYMax : pixelHeight;

            if (fXMax - fXMin < pixelStride || fYMax - fYMin < pixelStride) return false;

            xMin   = (int)fXMin;
            yMin   = (int)fYMin;
            width  = (int)(fXMax - fXMin);
            height = (int)(fYMax - fYMin);

            return true;
        }

        /// <summary>
        /// Projects a single world-space point through the VP matrix into pixel coordinates.
        /// Only computes X, Y, and W — the Z row is skipped since depth is not needed for cropping.
        /// Returns pxX = pxY = 0 and w ≤ 0 if the point is behind the camera.
        /// </summary>
        private static void ProjectPoint(
            ref Matrix4x4 vp, ref Vector3 pos,
            float halfW, float halfH,
            out float pxX, out float pxY, out float w)
        {
            // Compute the homogeneous W component first. In clip space, W represents depth
            // relative to the camera: W ≤ 0 means the point is behind the near plane.
            w = vp.m30 * pos.x + vp.m31 * pos.y + vp.m32 * pos.z + vp.m33;
            if (w <= 0.0001f) { pxX = pxY = 0f; return; }

            // Compute clip-space X and Y via manual matrix row dot products.
            // Skipping row 2 (Z) entirely since screen position only needs X and Y.
            float x = vp.m00 * pos.x + vp.m01 * pos.y + vp.m02 * pos.z + vp.m03;
            float y = vp.m10 * pos.x + vp.m11 * pos.y + vp.m12 * pos.z + vp.m13;

            // Convert clip-space to pixel coordinates.
            // Standard NDC->pixel: px = (x/w * 0.5 + 0.5) * pixelWidth
            // Rearranged to avoid two divisions: (x/w + 1) * half = (x + w) * half / w
            float invW = 1f / w;
            pxX = (x + w) * halfW * invW;
            pxY = (y + w) * halfH * invW;
        }

        // --------------------------------------------------------
        // BURST JOB
        // --------------------------------------------------------

        /// <summary>
        /// Downsamples the readback crop at pixelStride intervals into the flat hit grid.
        /// Each job element maps one grid cell (row, col) to a pixel in the crop and reads
        /// the world position pre-computed by MetaDepthCopy.shader.
        ///
        /// The shader encodes validity in the Vector4 w component:
        ///   w >= 0 -> valid world position (w = rawDepth sampled from R channel, ∈ (0,1))
        ///   w  < 0 -> invalid pixel (sky, too close, or out of sensor range); shader outputs w = -1
        /// Only the R channel of the depth texture is sampled. Meta uses a float format
        /// so the full [0,1] depth value fits in R with no packed overflow into G/B/A.
        ///
        /// IsSurface is reset to false for every cell so the downstream seed+flood
        /// starts from a clean slate regardless of the previous frame's result.
        /// </summary>
        [BurstCompile]
        private struct DepthUnprojectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector4> WorldPositions;
            // Dimensions of the crop in pixels (not the grid — grid = crop / stride).
            // Must be public: Burst job structs are assigned via object initializer externally.
            public int DepthWidth, DepthHeight, PixelStride, Cols;

            [WriteOnly] public NativeArray<Vector3> Hits;
            [WriteOnly] public NativeArray<bool>    HasDepth;
            [WriteOnly] public NativeArray<bool>    IsSurface;

            public void Execute(int index)
            {
                // Decode the flat grid index back to 2D grid coordinates, then to crop pixels.
                int r = index / Cols;
                int c = index % Cols;

                int dx = c * PixelStride;
                int dy = r * PixelStride;

                // Clamp to the last valid pixel rather than skipping: the final row/column
                // of the grid may overshoot the crop size by up to (PixelStride - 1) pixels.
                if (dx >= DepthWidth)  dx = DepthWidth  - 1;
                if (dy >= DepthHeight) dy = DepthHeight - 1;

                // WorldPositions is the linearized crop: row-major, width = DepthWidth.
                Vector4 sample = WorldPositions[dy * DepthWidth + dx];

                // w < 0 is the sentinel written by MetaDepthCopy.shader for invalid pixels
                // (sky, depth too close/far, or out of the sensor's range).
                if (sample.w < 0f)
                {
                    HasDepth[index]  = false;
                    IsSurface[index] = false;
                    Hits[index]      = Vector3.zero;
                    return;
                }

                Hits[index]      = new Vector3(sample.x, sample.y, sample.z);
                HasDepth[index]  = true;
                IsSurface[index] = false; // Cleared here; seed+flood sets it next.
            }
        }
    }
}
