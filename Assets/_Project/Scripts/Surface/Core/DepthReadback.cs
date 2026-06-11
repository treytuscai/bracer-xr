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
    /// Drives the full GPU->CPU depth pipeline each frame, producing SurfaceBuffer's world-space hit grid:
    ///   1. CROP:      the forearm's screen-space bounding box sizes a grid RT (~one texel per depth texel).
    ///   2. HAND MASK: render the hand mesh as a white silhouette at FULL depth-frame resolution (depth UV).
    ///   3. STABILIZE: 3-frame reprojected median (UpdateTemporalDepth). Its extract pass (pass 0) carves
    ///                 the finger out of history AND reconstructs the bleed ring around it from clean arm,
    ///                 so the moving hand can't corrupt the median and the lift is fixed once, in history.
    ///   4. BLIT:      MetaDepthCopy unprojects the stabilized depth into the grid RT — no hand logic here:
    ///                 the finger arrives as 0 (rejected to w=-1), the ring as ordinary arm. Read back async.
    ///   5. UNPROJECT: a Burst job copies the readback 1:1 into SurfaceBuffer; the caller chains the
    ///                 extraction pipeline onto the returned JobHandle.
    ///
    /// DEPTH SOURCE: Meta's environment depth is stereo-reconstructed from the two RGB cameras (not a
    /// dedicated IR sensor), a [0,1] NDC depth in R. The unprojection math and the historical-VP /
    /// anti-swim reasoning live in MetaDepthCopy.
    ///
    /// TEMPORAL STABILIZATION: each dispatch medians the depth over 3 frames (DepthTemporalMedian.shader)
    /// before the blit — rejecting stereo "flying pixels" (temporal outliers) so the boundary stops
    /// flickering, with history reprojected into the current head pose so it holds under head motion.
    /// Replaced an earlier mixed-pixel reject that eroded the silhouette.
    /// </summary>
    public class DepthReadback : IDisposable
    {
        // ------------------------------------------------------------------
        // STATE
        // ------------------------------------------------------------------
        // Material wrapping MetaDepthCopy.shader; reconstructs world positions
        // from the depth texture into the grid-resolution blit target.
        private Material _blitMaterial;
        // World positions are written to a per-frame pooled RenderTexture sized to the forearm
        // grid (RenderTexture.GetTemporary in TryDispatch), not a persistent full-screen RT — the
        // blit runs at grid resolution (~cols×rows fragments) instead of over the whole screen.
        // Set to true when AsyncGPUReadback.Request is enqueued, false at the
        // start of its callback. Reset at callback start (not end) so that
        // exceptions in downstream processing don't permanently stall the pipeline.
        private bool _isReadbackPending;
        // Set by Dispose(). AsyncGPUReadback can invoke its callback after teardown; HandleReadback
        // checks this before touching SurfaceBuffer or scheduling the unproject job.
        private bool _isDisposed;

        // Native resolution of Meta's _EnvironmentDepthTexture (e.g. 320x320), cached once.
        // The forearm crop is sampled at ~1:1 with these texels (grid = crop's texel footprint).
        private int _depthTexW, _depthTexH;

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

        // ------------------------------------------------------------------
        // HAND MASK (GPU silhouette)
        // ------------------------------------------------------------------
        // HandMask provides the CPU-baked mesh and localToWorld each frame.
        private HandMask _handMaskSource;
        // Tuning, set once via the constructor from ForearmDepthSurface Inspector values. All three feed
        // the median's extract pass (pass 0), where all hand handling lives:
        //   HandMarginTexels      — native depth texels the hand silhouette is grown by (3x3 max), covering
        //                            the stereo bleed/lift. The bleed ring inside it is reconstructed.
        //   OcclusionMarginTexels — inner cushion (texels) kept as a hole, covering the real hand peeking
        //                            past the mask so peek-through isn't flattened to arm. Below the margin.
        //   BorrowDepthBand       — depth window (m) for the ring reconstruction: a borrowed sample within
        //                            this of the nearest counts as the same surface (rejects a background).
        public int   HandMarginTexels;
        public int   OcclusionMarginTexels;
        public float BorrowDepthBand;
        // One CommandBuffer recording the whole GPU depth-reconstruction pass each frame (hand-mask
        // draw + the three depth blits), wrapped in named samples and submitted once. The named
        // scopes surface in RenderDoc / the GPU Profiler; the single submit replaces four separate ones.
        private CommandBuffer _depthCmd;
        // Unlit white material used to render the silhouette (Hidden/HandMaskRender).
        private Material _handMaskMat;

        // ------------------------------------------------------------------
        // TEMPORAL MEDIAN
        // Each dispatch (≈ once per depth frame) computes a 3-frame per-texel median of the depth,
        // which MetaDepthCopy then samples. The median rejects stereo "flying pixels" (temporal
        // outliers), killing the boundary flicker without the erosion the old mixed-pixel reject
        // caused. The two history frames are reprojected into the current head pose first
        // (UpdateTemporalDepth), so it holds under head motion, not just a static head.
        //
        // FRAME COUNT is 3 — the smallest odd window (a median needs odd to reject a 1-frame
        // outlier) and proven sufficient. A larger window trades more lag/disocclusion for marginal
        // stability; raising it means growing this ring AND the median pass's sample count.
        // ------------------------------------------------------------------
        // Hidden/DepthTemporalMedian: pass 0 extracts the left-eye slice (carving the finger and
        // reconstructing the bleed ring), pass 1 medians 3 frames.
        private Material _medianMat;
        // Ring of the last 3 depth frames (native depth res, R float), kept full-frame for reprojection.
        private RenderTexture[] _depthHist;
        // Per-slot depth-frame VP (and inverse) captured when each frame was extracted, so the
        // median pass can reproject the two history frames into the current head pose.
        private Matrix4x4[] _histVP;
        private Matrix4x4[] _histInvVP;
        private int  _histWrite;
        private bool _histInit;

        /// <summary>
        /// Loads the MetaDepthCopy and HandMaskRender shaders, creates materials, and stores the
        /// tuning parameters. Shaders must be present in the project and not stripped from builds.
        /// </summary>
        public DepthReadback(HandMask handMaskSource, int handMarginTexels, int occlusionMarginTexels, float borrowDepthBand)
        {
            HandMarginTexels      = handMarginTexels;
            OcclusionMarginTexels = occlusionMarginTexels;
            BorrowDepthBand       = borrowDepthBand;

            Shader shader = Shader.Find("Hidden/MetaDepthCopy");
            if (shader == null)
            {
                Debug.LogError("[Depth] MetaDepthCopy shader not found. Check shader is in project and not stripped from build.");
                return;
            }
            _blitMaterial = new Material(shader);

            _handMaskSource = handMaskSource;
            _depthCmd       = new CommandBuffer { name = "DepthReconstruction" };

            Shader maskShader = Shader.Find("Hidden/HandMaskRender");
            if (maskShader != null)
                _handMaskMat = new Material(maskShader);
            else
                Debug.LogWarning("[Depth] HandMaskRender shader not found — hand masking disabled.");

            Shader medianShader = Shader.Find("Hidden/DepthTemporalMedian");
            if (medianShader != null)
                _medianMat = new Material(medianShader);
            else
                Debug.LogError("[Depth] DepthTemporalMedian shader not found. Add it to Always Included Shaders — depth stabilization will be broken without it.");
        }

        /// <summary>
        /// Validates depth matrices, crops the forearm region, blits world positions,
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
            // Abort if the shader failed to load at construction, or a readback is still in flight.
            if (_blitMaterial == null) return false;
            if (_isReadbackPending) return false;

            // Meta sets _EnvironmentDepthReprojectionMatrices once per depth frame.
            // Index 0 is the left eye world->clip matrix for the depth camera's pose
            // at the time that depth frame was captured (not the current render pose).
            _depthMatrices.Clear();
            Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices", _depthMatrices);
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
                cam.fieldOfView, cam.pixelWidth, cam.pixelHeight,
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

            // Render the hand mask + world-position blit at grid resolution; returns the pooled
            // world-position RT to read back (released in the readback callback).
            RenderTexture rt = DispatchReconstruction(
                depthVP, cropX, cropY, cropW, cropH, screenW, screenH, cols, rows);

            // Read back the whole grid-resolution RT — the RT *is* the grid, so no sub-region crop.
            _isReadbackPending = true;
            AsyncGPUReadback.Request(
                rt, 0,
                request => HandleReadback(request, rt, buffer, rows, cols, onComplete));

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

            Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            if (depthTex == null) return false;

            _depthTexW = depthTex.width;
            _depthTexH = depthTex.height;
            return true;
        }

        /// <summary>
        /// Sets the blit material parameters, renders the hand silhouette, and runs the
        /// MetaDepthCopy blit at GRID resolution (cols×rows) rather than full screen: each of the
        /// ~cols*rows fragments samples R -> builds clip -> inverse VP -> perspective divide,
        /// writing Vector4 (xyz = world pos, w = linear depth, or w = -1 for invalid). Returns the
        /// pooled world-position RenderTexture for readback; the mask RT is released here since
        /// the blit has already sampled it.
        /// </summary>
        private RenderTexture DispatchReconstruction(
            Matrix4x4 depthVP,
            int cropX, int cropY, int cropW, int cropH,
            int screenW, int screenH,
            int cols, int rows)
        {
            // Forearm crop expressed as a screen-UV sub-rect: (scaleX, scaleY, offsetX, offsetY).
            float scaleX  = (float)cropW / screenW;
            float scaleY  = (float)cropH / screenH;
            float offsetX = (float)cropX / screenW;
            float offsetY = (float)cropY / screenH;
            Vector4 cropUVScaleOffset = new Vector4(scaleX, scaleY, offsetX, offsetY);

            // Invert the depth frame's world->clip matrix once on the CPU (clip->world in the
            // shader). depthVP is the left-eye VP for the pose at depth-capture time. Reused for
            // both the temporal reprojection and the main blit's _DepthInverseVP.
            Matrix4x4 depthInvVP = depthVP.inverse;

            // Record the whole pass into one buffer (cleared here, submitted once at the end). Hand
            // mask, the two median passes, and the final blit append named samples to _depthCmd.
            _depthCmd.Clear();

            // Render the FULL-FRAME hand silhouette FIRST: the temporal median's extract pass (pass 0) is
            // its sole consumer — it carves the finger out of depth history (so the moving hand can't
            // reproject onto clean arm and corrupt the median) and reconstructs the bleed ring around it,
            // baking both into the stabilized depth. RenderHandMask binds it to _medianMat. Must precede
            // UpdateTemporalDepth, whose pass 0 reads it.
            RenderTexture maskRT = RenderHandMask(depthVP);

            // Stabilize the depth (3-frame reprojected median, computed over the CROP only — pass 1
            // renders cols×rows, not the full 320×320, so the bulk of the reprojection work is skipped)
            // and bind it for the blit. Pass 0 carves the hand out of history (see above). Returns a
            // pooled temp we release after the blit has sampled it.
            RenderTexture stab = UpdateTemporalDepth(depthVP, depthInvVP, cols, rows, cropUVScaleOffset);

            _blitMaterial.SetMatrix("_DepthInverseVP", depthInvVP);

            // Remap the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect, so each
            // output texel's clip-space xy lands at the correct screen position. Shader does
            // depthUV = uv * scale + offset. (The blit doesn't sample the hand mask; all hand handling is
            // baked into the stabilized depth upstream by DepthTemporalMedian.)
            _blitMaterial.SetVector("_CropUVScaleOffset", cropUVScaleOffset);

            // A pooled temporary RT avoids per-frame allocation churn as the crop size changes.
            RenderTexture rt = RenderTexture.GetTemporary(cols, rows, 0, RenderTextureFormat.ARGBFloat);
            _depthCmd.BeginSample("MetaDepthCopy");
            _depthCmd.Blit(null, rt, _blitMaterial);
            _depthCmd.EndSample("MetaDepthCopy");

            // Submit the whole recorded pass once: hand mask -> extract -> median -> blit, in order.
            Graphics.ExecuteCommandBuffer(_depthCmd);

            // Now that the GPU work is queued, the blit has sampled the mask and the stabilized depth;
            // release both pooled temps (the readback reads only the world-position RT). Releasing
            // before the submit would risk the pool reusing them under the still-pending GPU work.
            if (maskRT != null) RenderTexture.ReleaseTemporary(maskRT);
            if (stab   != null) RenderTexture.ReleaseTemporary(stab);

            return rt;
        }

        /// <summary>
        /// AsyncGPUReadback completion handler: releases the pooled RT, then schedules the Burst
        /// unproject job and hands its JobHandle (plus grid dimensions) to onComplete. Invokes
        /// onComplete with a default handle on error or empty readback so the caller's pipeline
        /// can still advance. Runs on the main thread during Unity's readback callback.
        /// </summary>
        private void HandleReadback(
            AsyncGPUReadbackRequest request,
            RenderTexture rt,
            SurfaceBuffer buffer,
            int rows, int cols,
            Action<JobHandle, int, int> onComplete)
        {
            // Reset + release the pooled RT before any processing so exceptions downstream
            // don't permanently stall the pipeline or leak the temporary.
            _isReadbackPending = false;
            RenderTexture.ReleaseTemporary(rt);

            // The callback can fire after Dispose(), once SurfaceBuffer is already disposed. Bail
            // before touching the buffer or scheduling the unproject job — the readback array is
            // only valid inside this callback. onComplete is intentionally not invoked.
            if (_isDisposed) return;

            if (request.hasError)
            {
                onComplete?.Invoke(default, 0, 0);
                return;
            }

            NativeArray<Vector4> raw = request.GetData<Vector4>();
            if (!raw.IsCreated || raw.Length == 0)
            {
                onComplete?.Invoke(default, 0, 0);
                return;
            }

            buffer.ResizeIfNeeded(rows, cols);

            // WorldPositions is the grid itself (row-major, width = cols): cell
            // (r, c) lives at index r*cols + c — no stride/crop remap needed.
            var job = new DepthUnprojectionJob
            {
                WorldPositions = raw,
                Hits           = buffer.Hits,
                Depth          = buffer.Depth,
                HasDepth       = buffer.HasDepth,
                IsSurface      = buffer.IsSurface
            };

            onComplete?.Invoke(job.Schedule(rows * cols, 64), rows, cols);
        }

        /// <summary>
        /// Computes the 3-frame per-texel median of the depth and binds it for the blit to sample.
        /// Pass 0 extracts the current frame into the ring's write slot at FULL resolution (history
        /// stays full-frame so the reprojection can sample anywhere); pass 1 medians the current frame
        /// against the two reprojected histories but renders only the forearm crop (cols×rows) to keep
        /// the per-texel reprojection cheap. Returns the pooled crop-sized stabilized RT; the caller
        /// releases it after the blit.
        /// </summary>
        private RenderTexture UpdateTemporalDepth(
            Matrix4x4 depthVP, Matrix4x4 depthInvVP, int cols, int rows, Vector4 cropUVScaleOffset)
        {
            // Required shader missing (logged at construction): nothing to stabilize with.
            if (_medianMat == null) return null;

            EnsureTemporalRTs();

            // Pass 0 (extract) carves the finger and reconstructs the bleed ring around it (estimated from
            // clean arm), so the stabilized depth already has the finger as 0 and the lift as arm. Pass 0
            // is full-frame, so its margins step in native depth texels (_DepthTexelSize); _CropUVScaleOffset
            // is the pass-1 crop->depth remap. _HandMaskTex is already bound on _medianMat by RenderHandMask.
            // All set before the blits below.
            _medianMat.SetVector("_CropUVScaleOffset", cropUVScaleOffset);
            _medianMat.SetVector("_DepthTexelSize", new Vector4(1f / _depthTexW, 1f / _depthTexH, _depthTexW, _depthTexH));
            _medianMat.SetInteger("_HandMarginTexels", HandMarginTexels);
            _medianMat.SetInteger("_OcclusionMarginTexels", OcclusionMarginTexels);
            _medianMat.SetFloat("_BorrowDepthBand", BorrowDepthBand);

            int cur = _histWrite;

            // Extract the current raw left-eye depth slice into the write slot (pass 0), and record
            // the pose it was captured at for reprojection.
            _depthCmd.BeginSample("DepthExtract");
            _depthCmd.Blit(null, _depthHist[cur], _medianMat, 0);
            _depthCmd.EndSample("DepthExtract");
            _histVP[cur]    = depthVP;
            _histInvVP[cur] = depthInvVP;

            // First frame: prime the other two slots (texture + pose) with the current frame so the
            // median and its reprojection aren't computed against uninitialised history.
            if (!_histInit)
            {
                for (int k = 1; k <= 2; k++)
                {
                    int s = (cur + k) % 3;
                    _depthCmd.Blit(null, _depthHist[s], _medianMat, 0);
                    _histVP[s]    = depthVP;
                    _histInvVP[s] = depthInvVP;
                }
                _histInit = true;
            }

            // The two history slots (the frames that are not the current one).
            int h1 = (cur + 1) % 3;
            int h2 = (cur + 2) % 3;

            // Median of current + two reprojected histories -> stabilized depth (pass 1).
            _medianMat.SetTexture("_TexCur", _depthHist[cur]);
            _medianMat.SetTexture("_TexH1",  _depthHist[h1]);
            _medianMat.SetTexture("_TexH2",  _depthHist[h2]);
            _medianMat.SetMatrix("_CurVP",    depthVP);
            _medianMat.SetMatrix("_CurInvVP", depthInvVP);
            _medianMat.SetMatrix("_H1VP",     _histVP[h1]);
            _medianMat.SetMatrix("_H1InvVP",  _histInvVP[h1]);
            _medianMat.SetMatrix("_H2VP",     _histVP[h2]);
            _medianMat.SetMatrix("_H2InvVP",  _histInvVP[h2]);
            // _CropUVScaleOffset already set above (before pass 0) — pass 1 reuses the same value.

            // Pass 1 renders only the crop (cols×rows — the region the blit consumes), reading the
            // full-frame histories at reprojected UVs. Pooled temp; caller releases after the blit.
            RenderTexture stab = RenderTexture.GetTemporary(cols, rows, 0, RenderTextureFormat.RFloat);
            stab.filterMode = FilterMode.Point;
            _depthCmd.BeginSample("DepthMedian");
            _depthCmd.Blit(null, stab, _medianMat, 1);
            _depthCmd.EndSample("DepthMedian");

            _histWrite = (_histWrite + 1) % 3;

            _blitMaterial.SetTexture("_StabilizedDepthTex", stab);
            return stab;
        }

        /// <summary>
        /// Lazily allocates the 3-frame depth history ring at the native depth resolution (R float,
        /// point-sampled), kept full-frame so reprojection can sample anywhere. The stabilized output
        /// is a per-dispatch pooled crop-sized temp (see UpdateTemporalDepth), not allocated here.
        /// </summary>
        private void EnsureTemporalRTs()
        {
            if (_depthHist != null) return;

            _depthHist = new RenderTexture[3];
            for (int i = 0; i < 3; i++)
            {
                _depthHist[i] = new RenderTexture(_depthTexW, _depthTexH, 0, RenderTextureFormat.RFloat)
                    { filterMode = FilterMode.Point, name = $"DepthHist{i}" };
                _depthHist[i].Create();
            }

            _histVP    = new Matrix4x4[3];
            _histInvVP = new Matrix4x4[3];

            _histWrite = 0;
            _histInit  = false;
        }

        /// <summary>
        /// Releases the blit material, mask material, command buffer, and temporal-median resources.
        /// Call when the component is destroyed. The mask RenderTexture is pooled (GetTemporary) and
        /// released each frame, so there is nothing persistent to free for it. Arms the teardown
        /// guard first so an in-flight readback completing after this point is dropped.
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;

            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
            if (_handMaskMat  != null) UnityEngine.Object.Destroy(_handMaskMat);
            if (_medianMat    != null) UnityEngine.Object.Destroy(_medianMat);
            _depthCmd?.Release();

            // Release frees the GPU memory; Destroy frees the RenderTexture objects.
            if (_depthHist != null)
            {
                foreach (var rt in _depthHist)
                {
                    if (rt == null) continue;
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                _depthHist = null;
            }
        }

        // --------------------------------------------------------
        // HAND MASK RENDER
        // --------------------------------------------------------

        /// <summary>
        /// Renders the CPU-baked hand mesh as a white silhouette into a pooled FULL depth-frame
        /// (_depthTexW×_depthTexH) RenderTexture using CommandBuffer.DrawMesh with Meta's depth VP
        /// (no crop remap), so the mask lives in the depth texture's own UV space. Bound to _medianMat
        /// only: the median's extract pass (pass 0) is the sole consumer — it carves the finger out of
        /// history and reconstructs the bleed ring, baking both into the stabilized depth the blit reads.
        ///
        /// Returns the pooled RenderTexture (caller releases it after the blit), or null when there is
        /// no hand to draw — in which case _HandMaskTex is bound to black so nothing is carved.
        /// </summary>
        private RenderTexture RenderHandMask(Matrix4x4 depthVP)
        {
            if (_handMaskSource == null || _handMaskMat == null ||
                _handMaskSource.BakedMesh == null || _handMaskSource.BakedMesh.vertexCount == 0)
            {
                if (_medianMat != null) _medianMat.SetTexture("_HandMaskTex", Texture2D.blackTexture);
                return null;
            }

            // Full-frame silhouette at the native depth resolution, in the depth camera's UV space.
            // HandMaskRender applies the Vulkan Y flip; no crop matrix — the median's extract pass samples
            // it at its full-frame uv.
            RenderTexture maskRT = RenderTexture.GetTemporary(_depthTexW, _depthTexH, 0, RenderTextureFormat.R8);
            if (_medianMat != null) _medianMat.SetTexture("_HandMaskTex", maskRT);

            _handMaskMat.SetMatrix("_DepthVP", depthVP);

            // Appends to _depthCmd (cleared and submitted once by DispatchReconstruction); the mask
            // draw is recorded first so the median's extract and the blit sample a populated mask.
            _depthCmd.BeginSample("HandMask");
            _depthCmd.SetRenderTarget(maskRT);
            _depthCmd.ClearRenderTarget(false, true, Color.black);
            // DrawMesh with the CPU-baked mesh: vertex positions are already skinned.
            // UNITY_MATRIX_M is set from localToWorldMatrix by the DrawMesh call.
            _depthCmd.DrawMesh(_handMaskSource.BakedMesh, _handMaskSource.LocalToWorld, _handMaskMat);
            _depthCmd.EndSample("HandMask");
            return maskRT;
        }

        // --------------------------------------------------------
        // SCREEN-SPACE CROP
        // --------------------------------------------------------

        /// <summary>
        /// Projects the wrist and elbow into screen space using the depth camera's VP matrix,
        /// expands the bounding box by a perspective-correct margin derived from maxFloodDist,
        /// and clamps to screen bounds. Returns false if either bone is behind the camera or
        /// the resulting crop is smaller than one pixel stride.
        ///
        /// Uses ref parameters throughout to avoid copying Matrix4x4 (64 bytes) and
        /// Vector3 (12 bytes) structs on every call.
        /// </summary>
        private static bool CalculateArmBounds(
            ref Matrix4x4 depthVP,
            ref Vector3 wristPos, ref Vector3 elbowPos, ref Vector3 palmCap, bool hasPalm, ref Vector3 camPos,
            float fov, int pixelWidth, int pixelHeight,
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

            // Compute the arm midpoint's distance from the camera in world space.
            // Unrolled manually to avoid allocating a Vector3 struct on the heap.
            float midX = (wristPos.x + elbowPos.x) * 0.5f - camPos.x;
            float midY = (wristPos.y + elbowPos.y) * 0.5f - camPos.y;
            float midZ = (wristPos.z + elbowPos.z) * 0.5f - camPos.z;
            float armMidDist = Mathf.Sqrt(midX * midX + midY * midY + midZ * midZ);

            // Convert maxFloodDist (world meters) to screen pixels at the arm's depth.
            // focalPx is the pinhole camera focal length in pixels: f = h / (2 * tan(fov/2)).
            // Hardcoding Deg2Rad (0.0174532924f) avoids a Unity constant lookup per call.
            float focalPx       = pixelHeight / (2f * Mathf.Tan(fov * 0.5f * 0.0174532924f));
            // At distance d, a world-space radius r subtends r/d radians, so r/d * f pixels.
            float dynamicPadding = (maxFloodDist / armMidDist) * focalPx;

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

            float fXMin = minX - dynamicPadding;
            float fXMax = maxX + dynamicPadding;
            float fYMin = minY - dynamicPadding;
            float fYMax = maxY + dynamicPadding;

            // Clamp to screen bounds using branchless ternary (avoids Mathf.Clamp overhead).
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
        /// Copies the grid-resolution readback into the flat hit grid. The blit now renders at
        /// grid resolution, so WorldPositions is the grid itself (row-major, width = Cols) and
        /// each job element maps 1:1 to a cell — no crop/stride remap.
        ///
        /// The shader encodes validity and the depth signal in the Vector4 w component:
        ///   w >= 0 -> valid; w is the linear (metric) depth, stored into Depth[] for the triangle cut
        ///   w  < 0 -> invalid pixel (sky, too close, or out of sensor range); shader outputs w = -1
        ///
        /// IsSurface is reset to false for every cell so the downstream seed+flood
        /// starts from a clean slate regardless of the previous frame's result.
        /// </summary>
        [BurstCompile]
        private struct DepthUnprojectionJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector4> WorldPositions;

            [WriteOnly] public NativeArray<Vector3> Hits;
            [WriteOnly] public NativeArray<float>   Depth;
            [WriteOnly] public NativeArray<bool>    HasDepth;
            [WriteOnly] public NativeArray<bool>    IsSurface;

            public void Execute(int index)
            {
                Vector4 sample = WorldPositions[index];

                // w < 0 is the sentinel written by MetaDepthCopy.shader for invalid pixels
                // (sky, depth too close/far, or out of the sensor's range). Otherwise w is the
                // linear (metric) depth.
                if (sample.w < 0f)
                {
                    HasDepth[index]  = false;
                    IsSurface[index] = false;
                    Hits[index]      = Vector3.zero;
                    Depth[index]     = -1f;
                    return;
                }

                Hits[index]      = new Vector3(sample.x, sample.y, sample.z);
                Depth[index]     = sample.w;
                HasDepth[index]  = true;
                IsSurface[index] = false; // Cleared here; seed+flood sets it next.
            }
        }
    }
}
