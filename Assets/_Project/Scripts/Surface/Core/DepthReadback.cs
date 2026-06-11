// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections.Generic;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Drives the GPU->CPU depth pipeline each dispatch, producing SurfaceBuffer's world-space hit grid:
    ///   1. CROP:      the forearm's screen-space bounding box sizes the grid (~one cell per depth texel).
    ///   2. STABILIZE: DepthStabilizer renders the grown hand mask and the 3-frame reprojected
    ///                 median; its crop-sized output is the readback source. The hand is carved
    ///                 out of the depth there, so the rest of the pipeline never sees it.
    ///   3. UNPROJECT: the stabilized depth is read back async (RFloat — one float per texel) and a
    ///                 Burst job unprojects each texel to a world position on the CPU; the carved
    ///                 finger arrives as 0 and drops out as invalid. The caller chains the extraction
    ///                 pipeline onto the returned JobHandle.
    ///
    /// DEPTH SOURCE: Meta's environment depth is stereo-reconstructed from the two RGB cameras (not a
    /// dedicated IR sensor), a [0,1] NDC depth in R. The unprojection math and the historical-VP /
    /// anti-swim reasoning live in DepthUnprojectionJob (below).
    /// </summary>
    public class DepthReadback : IDisposable
    {
        // ------------------------------------------------------------------
        // SHADER GLOBALS (set by Meta's EnvironmentDepthManager; IDs cached)
        // ------------------------------------------------------------------
        private static readonly int ReprojectionMatricesID = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        private static readonly int EnvironmentDepthTexID  = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int DepthZBufferParamsID   = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

        // ------------------------------------------------------------------
        // STATE
        // ------------------------------------------------------------------
        // Set to true when AsyncGPUReadback.Request is enqueued, false at the
        // start of its callback. Reset at callback start (not end) so that
        // exceptions in downstream processing don't permanently stall the pipeline.
        private bool _isReadbackPending;
        // Set by Dispose(). AsyncGPUReadback can invoke its callback after teardown; HandleReadback
        // checks this before touching SurfaceBuffer or scheduling the unproject job.
        private bool _isDisposed;
        // State of the single readback in flight, consumed by HandleReadback via the cached
        // _readbackCallback delegate — fields instead of a per-dispatch closure so dispatching
        // doesn't allocate. _isReadbackPending guarantees one request at a time.
        private readonly Action<AsyncGPUReadbackRequest> _readbackCallback;
        private RenderTexture _pendingRT;
        private SurfaceBuffer _pendingBuffer;
        private int           _pendingRows, _pendingCols;
        private Action<JobHandle, int, int> _pendingOnComplete;
        // Unprojection inputs captured at dispatch for the CPU job: the depth frame's inverse VP,
        // the crop's screen-UV sub-rect, and Meta's NDC->metres params. Captured here (not read at
        // callback time) so the job uses the values of the frame it reads back, not a newer one.
        private Matrix4x4 _pendingInvVP;
        private Vector4   _pendingCropUV;
        private Vector4   _pendingZParams;

        // Native resolution of Meta's _EnvironmentDepthTexture (e.g. 320x320), cached once.
        // The forearm crop is sampled at ~1:1 with these texels (grid = crop's texel footprint).
        private int _depthTexW, _depthTexH;

        // GPU half of the dispatch: grown hand mask + temporal median (see DepthStabilizer).
        private readonly DepthStabilizer _stabilizer;
        // Provides the baked hand mesh; baked here only on committed dispatches.
        private readonly HandMask _handMaskSource;

        // ------------------------------------------------------------------
        // SKIP-REDUNDANT DISPATCH
        // ------------------------------------------------------------------
        // Depth VP of the last reconstructed frame. Meta republishes the reprojection matrices
        // every render frame but recomputes them from descriptors frozen with each depth frame,
        // so the value is bit-identical until a new depth frame arrives (~25 Hz). Matching it
        // means the frame is already reconstructed; it also keeps duplicate frames out of the
        // median's history ring, where median(cur, cur, h) = cur passes outliers through.
        private Matrix4x4 _lastReconstructedMatrix;
        // Scratch for the non-allocating GetGlobalMatrixArray overload; TryDispatch runs every
        // render frame.
        private readonly List<Matrix4x4> _depthMatrices = new List<Matrix4x4>(2);

        /// <summary>
        /// Creates the DepthStabilizer (GPU mask + median) and stores the readback callback.
        /// </summary>
        public DepthReadback(HandMask handMaskSource, int handMarginTexels, int occlusionMarginTexels, float borrowDepthBand)
        {
            _readbackCallback = HandleReadback;
            _handMaskSource   = handMaskSource;
            _stabilizer       = new DepthStabilizer(
                handMaskSource, handMarginTexels, occlusionMarginTexels, borrowDepthBand);
        }

        /// <summary>
        /// Validates depth matrices, crops the forearm region, stabilizes the depth on the GPU,
        /// and enqueues an async GPU readback + Burst unproject job.
        /// Returns true only when a readback was actually enqueued; false on any early-out
        /// (readback in flight, no depth matrices, no NEW depth frame since the last
        /// reconstruction, arm off-screen, shader missing).
        /// Callers must only arm their in-flight guard when this returns true.
        /// </summary>
        public bool TryDispatch(
            ArmFrame arm,
            float maxFloodDist,
            SurfaceBuffer buffer,
            Action<JobHandle, int, int> onComplete)
        {
            // Abort if the median shader failed to load at construction (its output IS the
            // readback source), or a readback is still in flight.
            if (!_stabilizer.IsReady) return false;
            if (_isReadbackPending) return false;

            // Meta sets _EnvironmentDepthReprojectionMatrices once per depth frame.
            // Index 0 is the left eye world->clip matrix for the depth camera's pose
            // at the time that depth frame was captured (not the current render pose).
            _depthMatrices.Clear();
            Shader.GetGlobalMatrixArray(ReprojectionMatricesID, _depthMatrices);
            if (_depthMatrices.Count == 0) return false;
            Matrix4x4 depthVP = _depthMatrices[0];

            // SKIP-REDUNDANT: an unchanged matrix means this depth frame is already reconstructed —
            // retry next frame. Caps reconstruction at the depth rate (~25 Hz) and keeps the
            // median's history ring on distinct frames. Recentering the tracking space changes the
            // matrix without new depth — one harmless extra dispatch.
            if (MatrixEquals(depthVP, _lastReconstructedMatrix)) return false;

            // Depth texture dimensions are needed to size the grid; bail until it's bound.
            if (!TryCacheDepthDims()) return false;

            Camera cam = arm.Cam;
            Vector3 wristPos = arm.WristPos;
            Vector3 elbowPos = arm.ElbowPos;
            Vector3 palmCap  = arm.PalmCapPos;
            Vector3 camPos   = cam.transform.position;

            // Compute the screen-space bounding box of the forearm using the depth
            // camera's VP matrix so crop coordinates align with the depth texture.
            // The palm cap is folded in (when tracked) so the crop covers the palm too.
            if (!CalculateArmBounds(
                ref depthVP, ref wristPos, ref elbowPos, ref palmCap, arm.HasPalm, ref camPos,
                cam.pixelWidth, cam.pixelHeight,
                maxFloodDist,
                out int cropX, out int cropY, out int cropW, out int cropH))
            {
                return false; // Arm behind camera or off-screen
            }

            // Sample the forearm crop at the depth buffer's NATIVE resolution: one grid cell per
            // real depth texel spanning the crop (no screen-pixel stride). The per-axis footprint
            // handles the anisotropic screen->texel mapping — the render is double-wide while the
            // depth texture is square.
            int screenW = cam.pixelWidth;
            int screenH = cam.pixelHeight;
            // Minimum 2 per axis: a 1×N grid produces no quads and boundary smoothing needs ≥2 cells.
            int cols = Mathf.Max(2, Mathf.RoundToInt((float)cropW / screenW * _depthTexW));
            int rows = Mathf.Max(2, Mathf.RoundToInt((float)cropH / screenH * _depthTexH));

            // Bake the hand silhouette only on a committed dispatch — BakeMesh is too expensive
            // to run on every (skipped) render frame.
            _handMaskSource?.BakeSilhouette();

            // Forearm crop as a screen-UV sub-rect (scaleX, scaleY, offsetX, offsetY), and the
            // depth VP inverted once — both shared by the GPU median reprojection and the CPU
            // unprojection job.
            Vector4 cropUVScaleOffset = new Vector4(
                (float)cropW / screenW, (float)cropH / screenH,
                (float)cropX / screenW, (float)cropY / screenH);
            Matrix4x4 depthInvVP = depthVP.inverse;

            // Render the hand mask + the two median passes; returns the pooled crop-sized
            // stabilized-depth RT to read back (released in the readback callback).
            RenderTexture rt = _stabilizer.RenderStabilizedCrop(
                depthVP, depthInvVP, cropUVScaleOffset, cols, rows, _depthTexW, _depthTexH);

            // Read back the whole grid-resolution RT — the RT *is* the grid, so no sub-region crop.
            // The unprojection inputs are captured alongside so the job sees this frame's values.
            _isReadbackPending = true;
            _pendingRT         = rt;
            _pendingBuffer     = buffer;
            _pendingRows       = rows;
            _pendingCols       = cols;
            _pendingOnComplete = onComplete;
            _pendingInvVP      = depthInvVP;
            _pendingCropUV     = cropUVScaleOffset;
            _pendingZParams    = Shader.GetGlobalVector(DepthZBufferParamsID);
            AsyncGPUReadback.Request(rt, 0, _readbackCallback);

            // Mark the frame consumed only on commit, so an early-out above (e.g. arm off-screen)
            // leaves it eligible for a later attempt.
            _lastReconstructedMatrix = depthVP;
            return true;
        }

        /// <summary>
        /// Exact matrix equality for the skip-redundant check. Matrix4x4's == is epsilon-based and
        /// could mistake a real new depth frame for a republish when the head is nearly still;
        /// republished matrices are bit-identical, so exact comparison is the right test.
        /// </summary>
        private static bool MatrixEquals(in Matrix4x4 a, in Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Caches the native depth texture dimensions (e.g. 320×320) on first success. They are
        /// constant for the session. Returns false until _EnvironmentDepthTexture is bound by
        /// Meta's EnvironmentDepthManager, signalling the caller to retry next frame.
        /// </summary>
        private bool TryCacheDepthDims()
        {
            if (_depthTexW != 0) return true;

            Texture depthTex = Shader.GetGlobalTexture(EnvironmentDepthTexID);
            if (depthTex == null) return false;

            _depthTexW = depthTex.width;
            _depthTexH = depthTex.height;
            return true;
        }

        /// <summary>
        /// AsyncGPUReadback completion handler, reading the in-flight request's state from the
        /// _pending* fields: releases the pooled RT, then schedules the Burst unproject job and
        /// hands its JobHandle (plus grid dimensions) to the pending onComplete. Invokes it with
        /// a default handle on error or empty readback so the caller's pipeline can still
        /// advance. Runs on the main thread during Unity's readback callback.
        /// </summary>
        private void HandleReadback(AsyncGPUReadbackRequest request)
        {
            // Reset + release the pooled RT before any processing so exceptions downstream
            // don't permanently stall the pipeline or leak the temporary.
            _isReadbackPending = false;
            if (_pendingRT != null)
            {
                RenderTexture.ReleaseTemporary(_pendingRT);
                _pendingRT = null;
            }

            // The callback can fire after Dispose(), once SurfaceBuffer is already disposed. Bail
            // before touching the buffer or scheduling the unproject job — the readback array is
            // only valid inside this callback. onComplete is intentionally not invoked.
            if (_isDisposed) return;

            SurfaceBuffer buffer = _pendingBuffer;
            Action<JobHandle, int, int> onComplete = _pendingOnComplete;
            int rows = _pendingRows;
            int cols = _pendingCols;

            if (request.hasError)
            {
                onComplete?.Invoke(default, 0, 0);
                return;
            }

            NativeArray<float> raw = request.GetData<float>();
            if (!raw.IsCreated || raw.Length == 0)
            {
                onComplete?.Invoke(default, 0, 0);
                return;
            }

            buffer.ResizeIfNeeded(rows, cols);

            // RawDepth is the grid itself (row-major, width = cols): cell (r, c) lives at index
            // r*cols + c. The unprojection inputs were captured at dispatch (_pending* fields).
            var job = new DepthUnprojectionJob
            {
                RawDepth          = raw,
                Cols              = cols,
                InvCols           = 1f / cols,
                InvRows           = 1f / rows,
                CropUVScaleOffset = _pendingCropUV,
                DepthInverseVP    = _pendingInvVP,
                ZBufferParams     = _pendingZParams,
                Hits              = buffer.Hits,
                Depth             = buffer.Depth,
                HasDepth          = buffer.HasDepth,
                IsSurface         = buffer.IsSurface
            };

            onComplete?.Invoke(job.Schedule(rows * cols, 64), rows, cols);
        }

        /// <summary>
        /// Disposes the DepthStabilizer's GPU resources. Arms the teardown guard first so an
        /// in-flight readback completing after this point is dropped.
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;
            _stabilizer.Dispose();
        }

        // --------------------------------------------------------
        // SCREEN-SPACE CROP
        // --------------------------------------------------------

        /// <summary>
        /// Projects the wrist and elbow into screen space using the depth camera's VP matrix,
        /// expands the bounding box per axis by maxFloodDist measured through that same VP,
        /// and clamps to screen bounds. Returns false if either bone is behind the camera or
        /// the resulting crop is degenerate (a few pixels).
        ///
        /// Uses ref parameters throughout to avoid copying Matrix4x4 (64 bytes) and
        /// Vector3 (12 bytes) structs on every call.
        /// </summary>
        private static bool CalculateArmBounds(
            ref Matrix4x4 depthVP,
            ref Vector3 wristPos, ref Vector3 elbowPos, ref Vector3 palmCap, bool hasPalm, ref Vector3 camPos,
            int pixelWidth, int pixelHeight,
            float maxFloodDist,
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

            // PADDING — convert maxFloodDist (world metres) to pixels by measurement: project the
            // arm midpoint plus two maxFloodDist offsets perpendicular to the view ray, and pad
            // each axis by the larger projected distance. Measuring through depthVP itself keeps
            // the padding exact for the depth camera's own frustum (wider FOV than the render
            // camera, asymmetric) and for this pixel space's anisotropy (square depth NDC scaled
            // by the double-wide screen dimensions) — one scalar focal can't serve both axes.
            Vector3 mid     = (wristPos + elbowPos) * 0.5f;
            Vector3 viewDir = (mid - camPos).normalized;
            // World-space perpendiculars to the view ray; fallback for looking straight up/down.
            Vector3 padRight = Vector3.Cross(viewDir, Vector3.up);
            if (padRight.sqrMagnitude < 1e-6f) padRight = Vector3.Cross(viewDir, Vector3.right);
            padRight.Normalize();
            Vector3 padUp = Vector3.Cross(padRight, viewDir);   // unit: cross of orthonormal pair

            ProjectPoint(ref depthVP, ref mid, halfWidth, halfHeight,
                         out float midPx, out float midPy, out float midW);
            if (midW <= 0f) return false;

            Vector3 offR = mid + padRight * maxFloodDist;
            Vector3 offU = mid + padUp    * maxFloodDist;
            ProjectPoint(ref depthVP, ref offR, halfWidth, halfHeight,
                         out float rPx, out float rPy, out float rW);
            ProjectPoint(ref depthVP, ref offU, halfWidth, halfHeight,
                         out float uPx, out float uPy, out float uW);
            if (rW <= 0f || uW <= 0f) return false;

            // Either offset can land diagonally in pixel space (head roll), so take the larger
            // component per axis — the padding then covers the flood radius in every direction.
            float padX = Mathf.Max(Mathf.Abs(rPx - midPx), Mathf.Abs(uPx - midPx));
            float padY = Mathf.Max(Mathf.Abs(rPy - midPy), Mathf.Abs(uPy - midPy));

            // Bounding box of the two projected points, expanded by the padding.
            float minX = wristX < elbowX ? wristX : elbowX;
            float maxX = wristX > elbowX ? wristX : elbowX;
            float minY = wristY < elbowY ? wristY : elbowY;
            float maxY = wristY > elbowY ? wristY : elbowY;

            // Fold in the palm cap so the crop covers the palm (which the seed+flood now reaches).
            // Skip silently if the cap is untracked or behind the camera — the box stays wrist+elbow.
            if (hasPalm)
            {
                ProjectPoint(ref depthVP, ref palmCap, halfWidth, halfHeight,
                             out float capX, out float capY, out float capW);
                if (capW > 0f)
                {
                    if (capX < minX) minX = capX;
                    if (capX > maxX) maxX = capX;
                    if (capY < minY) minY = capY;
                    if (capY > maxY) maxY = capY;
                }
            }

            float fXMin = minX - padX;
            float fXMax = maxX + padX;
            float fYMin = minY - padY;
            float fYMax = maxY + padY;

            // Clamp to screen bounds.
            fXMin = fXMin > 0f ? fXMin : 0f;
            fXMax = fXMax < pixelWidth  ? fXMax : pixelWidth;
            fYMin = fYMin > 0f ? fYMin : 0f;
            fYMax = fYMax < pixelHeight ? fYMax : pixelHeight;

            // Reject degenerate crops (a few screen pixels); the grid clamps to a 2×2 minimum.
            if (fXMax - fXMin < 4f || fYMax - fYMin < 4f) return false;

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
        /// Unprojects the stabilized-depth readback into the flat world-space hit grid. Running the
        /// reconstruction math on the CPU keeps the readback to one float per texel and the GPU
        /// free of an unprojection pass. RawDepth is the grid itself (row-major, width = Cols);
        /// each element maps 1:1 to a cell. Per valid texel:
        ///   1. Texel centre -> the crop's screen-UV sub-rect (uv * cropScale + cropOffset) — the
        ///      same remap the median's pass 1 rendered this texel with.
        ///   2. (u, v, raw) [0,1] -> clip [-1,1]; the raw depth becomes clip Z.
        ///   3. Multiply by the depth frame's inverse VP + perspective divide. The VP is the
        ///      HISTORICAL pose the depth was captured at — unprojecting with the current camera
        ///      VP instead would desync from the capture pose and make the surface swim.
        ///   4. Linearize the raw NDC depth to metres via Meta's ZBuffer params — the true-depth
        ///      signal MeshGenerator cuts triangles on.
        ///
        /// raw 0 or 1 is invalid (near/far plane, no stereo match); the finger carved upstream by
        /// the median's extract pass arrives as 0 and drops out here. IsSurface is reset to false
        /// for every cell so the downstream seed+flood starts from a clean slate.
        /// </summary>
        [BurstCompile]
        private struct DepthUnprojectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> RawDepth;

            public int       Cols;
            // 1/cols and 1/rows, precomputed so Execute multiplies instead of dividing per cell.
            public float     InvCols, InvRows;
            public Vector4   CropUVScaleOffset;
            public Matrix4x4 DepthInverseVP;
            // Meta's _EnvironmentDepthZBufferParams: linear = x / (ndc + y), ndc = raw*2-1.
            public Vector4   ZBufferParams;

            [WriteOnly] public NativeArray<Vector3> Hits;
            [WriteOnly] public NativeArray<float>   Depth;
            [WriteOnly] public NativeArray<bool>    HasDepth;
            [WriteOnly] public NativeArray<bool>    IsSurface;

            public void Execute(int index)
            {
                float raw = RawDepth[index];

                // 0 and 1 are the invalid band: near/far plane, no stereo match, the carved finger.
                if (raw <= 0f || raw >= 1f)
                {
                    HasDepth[index]  = false;
                    IsSurface[index] = false;
                    Hits[index]      = Vector3.zero;
                    Depth[index]     = -1f;
                    return;
                }

                int r = index / Cols;
                int c = index - r * Cols;

                // Texel centre in the grid's [0,1] UV, then onto the crop's screen-UV sub-rect.
                float uGrid = (c + 0.5f) * InvCols;
                float vGrid = (r + 0.5f) * InvRows;
                float u = uGrid * CropUVScaleOffset.x + CropUVScaleOffset.z;
                float v = vGrid * CropUVScaleOffset.y + CropUVScaleOffset.w;

                // (u, v, raw) [0,1] -> clip [-1,1] -> world via the historical inverse VP +
                // perspective divide.
                Vector4 world = DepthInverseVP * new Vector4(
                    u * 2f - 1f, v * 2f - 1f, raw * 2f - 1f, 1f);
                float invW = 1f / world.w;

                Hits[index]      = new Vector3(world.x * invW, world.y * invW, world.z * invW);
                Depth[index]     = ZBufferParams.x / (raw * 2f - 1f + ZBufferParams.y);
                HasDepth[index]  = true;
                IsSurface[index] = false; // Cleared here; seed+flood sets it next.
            }
        }
    }
}
