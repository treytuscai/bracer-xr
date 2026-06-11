// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Surface.Core
{
    /// <summary>
    /// Owns the GPU half of a depth dispatch, recorded into one command buffer and submitted once:
    ///   1. HAND MASK: render the hand mesh as a white silhouette at full depth-frame resolution
    ///      (depth UV) and grow it into a two-zone mask — R = no-trust cushion, G = bleed ring
    ///      (Hidden/HandMaskRender).
    ///   2. STABILIZE: 3-frame reprojected median (Hidden/DepthTemporalMedian). Its extract pass
    ///      (pass 0) carves the silhouette out of history AND rebuilds the cushion and bleed ring
    ///      around it from clean arm, so the moving hand can't corrupt the median and the lift is
    ///      fixed once, in history. Pass 1 renders the crop-sized stabilized depth.
    ///
    /// Returns the pooled crop-sized stabilized RT — DepthReadback's async-readback source.
    ///
    /// The median rejects stereo "flying pixels" (temporal outliers) so the forearm boundary
    /// stops flickering, with history reprojected into the current head pose so it holds under
    /// head motion. FRAME COUNT is 3 — the smallest odd window (a median needs odd to reject a
    /// 1-frame outlier) and proven sufficient; a larger window trades more lag/disocclusion for
    /// marginal stability and means growing the ring AND the median pass's sample count.
    /// </summary>
    public class DepthStabilizer : IDisposable
    {
        // ------------------------------------------------------------------
        // SHADER PROPERTY IDS (cached — set every dispatch)
        // ------------------------------------------------------------------
        private static readonly int HandMaskTexID       = Shader.PropertyToID("_HandMaskTex");
        private static readonly int HandSilhouetteTexID = Shader.PropertyToID("_HandSilhouetteTex");
        private static readonly int DepthVPID          = Shader.PropertyToID("_DepthVP");
        private static readonly int DilateTexelSizeID  = Shader.PropertyToID("_DilateTexelSize");
        private static readonly int DilateSrcTexID     = Shader.PropertyToID("_DilateSrcTex");
        private static readonly int OccMarginTexelsID  = Shader.PropertyToID("_OccMarginTexels");
        private static readonly int HandMarginTexelsID = Shader.PropertyToID("_HandMarginTexels");
        private static readonly int CropUVScaleOffsetID = Shader.PropertyToID("_CropUVScaleOffset");
        private static readonly int DepthTexelSizeID   = Shader.PropertyToID("_DepthTexelSize");
        private static readonly int BorrowDepthBandID  = Shader.PropertyToID("_BorrowDepthBand");
        private static readonly int TexCurID = Shader.PropertyToID("_TexCur");
        private static readonly int TexH1ID  = Shader.PropertyToID("_TexH1");
        private static readonly int TexH2ID  = Shader.PropertyToID("_TexH2");
        private static readonly int CurVPID    = Shader.PropertyToID("_CurVP");
        private static readonly int CurInvVPID = Shader.PropertyToID("_CurInvVP");
        private static readonly int H1VPID     = Shader.PropertyToID("_H1VP");
        private static readonly int H1InvVPID  = Shader.PropertyToID("_H1InvVP");
        private static readonly int H2VPID     = Shader.PropertyToID("_H2VP");
        private static readonly int H2InvVPID  = Shader.PropertyToID("_H2InvVP");

        // ------------------------------------------------------------------
        // TUNING (set once via the constructor from ForearmDepthSurface Inspector values)
        // All three feed the median's extract pass (pass 0), where all hand handling lives.
        // ------------------------------------------------------------------
        // Native depth texels the hand silhouette is grown by; the bleed ring inside it is reconstructed.
        public int HandMarginTexels;
        // Inner cushion (texels) where measured depth is never trusted (strongest bleed + the real
        // hand peeking past the mask); rebuilt at borrowed arm depth instead of carved.
        public int OcclusionMarginTexels;
        // Depth window (m) for the ring reconstruction: a borrowed sample within this of the
        // nearest counts as the same surface (rejects a background).
        public float BorrowDepthBand;

        // ------------------------------------------------------------------
        // GPU RESOURCES
        // ------------------------------------------------------------------
        // HandMask provides the CPU-baked mesh and localToWorld each dispatch.
        private readonly HandMask _handMaskSource;
        // One CommandBuffer recording the whole GPU depth pass each dispatch (hand-mask chain + the
        // two median blits), wrapped in named samples and submitted once. The named scopes surface
        // in RenderDoc / the GPU Profiler.
        private readonly CommandBuffer _depthCmd;
        // Hidden/HandMaskRender: pass 0 renders the silhouette, passes 1+2 grow it (separable max).
        private Material _handMaskMat;
        // Hidden/DepthTemporalMedian: pass 0 extracts the left-eye slice (carving the silhouette
        // and rebuilding the cushion/bleed ring around it), pass 1 medians 3 frames.
        private Material _medianMat;
        // The mask chain's pooled temps (silhouette, horizontal-dilate intermediate, grown RG
        // result), alive from RenderHandMask until ReleaseMaskTemps after the submit.
        private RenderTexture _maskRawRT, _maskTmpRT, _maskGrownRT;

        // Ring of the last 3 depth frames (native depth res, R float), kept full-frame for reprojection.
        private RenderTexture[] _depthHist;
        // Per-slot depth-frame VP (and inverse) captured when each frame was extracted, so the
        // median pass can reproject the two history frames into the current head pose.
        private Matrix4x4[] _histVP;
        private Matrix4x4[] _histInvVP;
        private int  _histWrite;
        private bool _histInit;

        // Native depth texture dimensions, supplied by DepthReadback each dispatch.
        private int _depthTexW, _depthTexH;

        /// <summary> True once the required median shader has loaded (its pass 1 output IS the readback source). </summary>
        public bool IsReady => _medianMat != null;

        /// <summary>
        /// Loads the HandMaskRender and DepthTemporalMedian shaders, creates materials, and stores
        /// the tuning parameters. Shaders must be present in the project and not stripped from builds.
        /// </summary>
        public DepthStabilizer(HandMask handMaskSource, int handMarginTexels, int occlusionMarginTexels, float borrowDepthBand)
        {
            HandMarginTexels      = handMarginTexels;
            OcclusionMarginTexels = occlusionMarginTexels;
            BorrowDepthBand       = borrowDepthBand;

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
        /// Records and submits the GPU half of a dispatch: the grown hand mask chain, then the two
        /// temporal median passes. Returns the pooled crop-sized stabilized-depth RT (RFloat) —
        /// the async-readback source, which the caller releases once consumed. The mask temps are
        /// released here since the median has sampled them. Returns null until IsReady.
        /// </summary>
        public RenderTexture RenderStabilizedCrop(
            Matrix4x4 depthVP, Matrix4x4 depthInvVP, Vector4 cropUVScaleOffset,
            int cols, int rows, int depthTexW, int depthTexH)
        {
            if (_medianMat == null) return null;

            _depthTexW = depthTexW;
            _depthTexH = depthTexH;

            // Record the whole pass into one buffer (cleared here, submitted once at the end). The
            // mask chain and the two median passes append named samples to _depthCmd.
            _depthCmd.Clear();

            // Build the FULL-FRAME grown hand mask FIRST: the temporal median's extract pass
            // (pass 0) is its sole consumer — it carves the hand silhouette out of depth history
            // (so the moving hand can't reproject onto clean arm and corrupt the median) and
            // rebuilds the cushion and bleed ring around it, baking all of it into the stabilized
            // depth.
            RenderHandMask(depthVP);

            // Stabilize the depth (3-frame reprojected median, computed over the CROP only — pass 1
            // renders cols×rows, not the full depth frame, so the bulk of the reprojection work is
            // skipped). Pass 0 carves the hand out of history (see above).
            RenderTexture stab = UpdateTemporalDepth(depthVP, depthInvVP, cols, rows, cropUVScaleOffset);

            // Submit the whole recorded pass once: mask chain -> extract -> median, in order.
            Graphics.ExecuteCommandBuffer(_depthCmd);

            // The median has sampled the grown mask now that the GPU work is queued; release the
            // mask chain's temps. (stab is NOT released here — it is the readback source.)
            // Releasing before the submit would risk the pool reusing them under the still-pending
            // GPU work.
            ReleaseMaskTemps();

            return stab;
        }

        /// <summary>
        /// Builds the GROWN hand mask in depth-frame UV space, full depth-frame resolution:
        /// renders the CPU-baked hand mesh as a white silhouette (pass 0, Meta's depth VP so the
        /// mask lives in the depth texture's own UV space), then grows it with a separable max
        /// filter (passes 1+2: horizontal, then vertical) into RG — R = within
        /// OcclusionMarginTexels of the hand, G = within HandMarginTexels. Bound to _medianMat
        /// only: the median's extract pass (pass 0) is the sole consumer — it carves the raw
        /// silhouette (also bound, as _HandSilhouetteTex), rebuilds the R cushion at borrowed arm
        /// depth, and depth-discriminates the G ring.
        ///
        /// The pooled temps land in _mask*RT for ReleaseMaskTemps() after the submit. When there
        /// is no hand to draw, both textures are bound to black so nothing is carved.
        /// </summary>
        private void RenderHandMask(Matrix4x4 depthVP)
        {
            if (_handMaskSource == null || _handMaskMat == null ||
                _handMaskSource.BakedMesh == null || _handMaskSource.BakedMesh.vertexCount == 0)
            {
                _medianMat.SetTexture(HandMaskTexID,       Texture2D.blackTexture);
                _medianMat.SetTexture(HandSilhouetteTexID, Texture2D.blackTexture);
                return;
            }

            _maskRawRT   = RenderTexture.GetTemporary(_depthTexW, _depthTexH, 0, RenderTextureFormat.R8);
            _maskTmpRT   = RenderTexture.GetTemporary(_depthTexW, _depthTexH, 0, RenderTextureFormat.RG16);
            _maskGrownRT = RenderTexture.GetTemporary(_depthTexW, _depthTexH, 0, RenderTextureFormat.RG16);
            _maskRawRT.filterMode   = FilterMode.Point;
            _maskTmpRT.filterMode   = FilterMode.Point;
            _maskGrownRT.filterMode = FilterMode.Point;
            _medianMat.SetTexture(HandMaskTexID,       _maskGrownRT);
            _medianMat.SetTexture(HandSilhouetteTexID, _maskRawRT);

            _handMaskMat.SetMatrix(DepthVPID, depthVP);
            _handMaskMat.SetVector(DilateTexelSizeID, new Vector4(1f / _depthTexW, 1f / _depthTexH, 0f, 0f));
            _handMaskMat.SetInteger(OccMarginTexelsID, OcclusionMarginTexels);
            _handMaskMat.SetInteger(HandMarginTexelsID, HandMarginTexels);

            // Appends to _depthCmd (cleared and submitted once by RenderStabilizedCrop); the mask
            // chain is recorded first so the median's extract pass samples a populated grown mask.
            _depthCmd.BeginSample("HandMask");
            _depthCmd.SetRenderTarget(_maskRawRT);
            _depthCmd.ClearRenderTarget(false, true, Color.black);
            // DrawMesh with the CPU-baked mesh: vertex positions are already skinned.
            // UNITY_MATRIX_M is set from localToWorldMatrix by the DrawMesh call. shaderPass is
            // pinned to 0 — the default (-1) draws ALL passes, including the dilate blits.
            _depthCmd.DrawMesh(_handMaskSource.BakedMesh, _handMaskSource.LocalToWorld, _handMaskMat, 0, 0);
            // Separable dilation. The source is bound per-blit with a RECORDED SetGlobalTexture —
            // ordered within the buffer — so the two passes can chain raw -> tmp -> grown despite
            // sharing one material (a material-level texture would resolve to its last value for
            // both blits at execute time).
            _depthCmd.SetGlobalTexture(DilateSrcTexID, _maskRawRT);
            _depthCmd.Blit(null, _maskTmpRT, _handMaskMat, 1);
            _depthCmd.SetGlobalTexture(DilateSrcTexID, _maskTmpRT);
            _depthCmd.Blit(null, _maskGrownRT, _handMaskMat, 2);
            _depthCmd.EndSample("HandMask");
        }

        /// <summary>
        /// Computes the 3-frame per-texel median of the depth. Pass 0 extracts the current frame
        /// into the ring's write slot at FULL resolution (history stays full-frame so the
        /// reprojection can sample anywhere); pass 1 medians the current frame against the two
        /// reprojected histories but renders only the forearm crop (cols×rows) to keep the
        /// per-texel reprojection cheap. Returns the pooled crop-sized stabilized RT.
        /// </summary>
        private RenderTexture UpdateTemporalDepth(
            Matrix4x4 depthVP, Matrix4x4 depthInvVP, int cols, int rows, Vector4 cropUVScaleOffset)
        {
            EnsureTemporalRTs();

            // Pass 0 (extract) carves the silhouette and rebuilds the cushion and bleed ring around it
            // from clean arm, so the stabilized depth already has the hand as 0 and the lift as arm. Pass 0
            // is full-frame, so the borrow march steps in native depth texels (_DepthTexelSize);
            // _CropUVScaleOffset is the pass-1 crop->depth remap. The mask growth is baked into
            // _HandMaskTex (bound by RenderHandMask) — the margin here only bounds the march.
            // All set before the blits below.
            _medianMat.SetVector(CropUVScaleOffsetID, cropUVScaleOffset);
            _medianMat.SetVector(DepthTexelSizeID, new Vector4(1f / _depthTexW, 1f / _depthTexH, _depthTexW, _depthTexH));
            _medianMat.SetInteger(HandMarginTexelsID, HandMarginTexels);
            _medianMat.SetFloat(BorrowDepthBandID, BorrowDepthBand);

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
            _medianMat.SetTexture(TexCurID, _depthHist[cur]);
            _medianMat.SetTexture(TexH1ID,  _depthHist[h1]);
            _medianMat.SetTexture(TexH2ID,  _depthHist[h2]);
            _medianMat.SetMatrix(CurVPID,    depthVP);
            _medianMat.SetMatrix(CurInvVPID, depthInvVP);
            _medianMat.SetMatrix(H1VPID,     _histVP[h1]);
            _medianMat.SetMatrix(H1InvVPID,  _histInvVP[h1]);
            _medianMat.SetMatrix(H2VPID,     _histVP[h2]);
            _medianMat.SetMatrix(H2InvVPID,  _histInvVP[h2]);
            // _CropUVScaleOffset already set above (before pass 0) — pass 1 reuses the same value.

            // Pass 1 renders only the crop (cols×rows — the grid the readback consumes), reading
            // the full-frame histories at reprojected UVs. Pooled temp; the caller releases it
            // once the readback has consumed it.
            RenderTexture stab = RenderTexture.GetTemporary(cols, rows, 0, RenderTextureFormat.RFloat);
            stab.filterMode = FilterMode.Point;
            _depthCmd.BeginSample("DepthMedian");
            _depthCmd.Blit(null, stab, _medianMat, 1);
            _depthCmd.EndSample("DepthMedian");

            _histWrite = (_histWrite + 1) % 3;
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
        /// Releases the mask chain's pooled temps. Called by RenderStabilizedCrop after the
        /// command buffer submits — releasing earlier would risk the pool reusing them under the
        /// still-pending GPU work.
        /// </summary>
        private void ReleaseMaskTemps()
        {
            if (_maskRawRT   != null) { RenderTexture.ReleaseTemporary(_maskRawRT);   _maskRawRT   = null; }
            if (_maskTmpRT   != null) { RenderTexture.ReleaseTemporary(_maskTmpRT);   _maskTmpRT   = null; }
            if (_maskGrownRT != null) { RenderTexture.ReleaseTemporary(_maskGrownRT); _maskGrownRT = null; }
        }

        /// <summary>
        /// Releases the materials, command buffer, and temporal-median history. The mask and
        /// stabilized RTs are pooled (GetTemporary) and released each dispatch, so there is
        /// nothing persistent to free for them.
        /// </summary>
        public void Dispose()
        {
            if (_handMaskMat != null) { UnityEngine.Object.Destroy(_handMaskMat); _handMaskMat = null; }
            if (_medianMat   != null) { UnityEngine.Object.Destroy(_medianMat);   _medianMat   = null; }
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
    }
}
