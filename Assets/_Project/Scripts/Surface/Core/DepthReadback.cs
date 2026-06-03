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
    ///   1. HAND MASK: Render the hand mesh as a white silhouette into a grid-resolution
    ///                 RenderTexture via CommandBuffer.DrawMesh using Meta's depth VP,
    ///                 with the forearm crop remapped to fill the target so the silhouette
    ///                 is sampled 1:1 with the grid (no full-screen oversampling).
    ///   2. BLIT:      Run MetaDepthCopy.shader via Graphics.Blit into a grid-resolution RT
    ///                 (~one texel per forearm depth-texel). Hand pixels are rejected in the
    ///                 shader (w=-1) so they arrive HasDepth=false.
    ///   3. CROP:      Compute the forearm's screen-space bounding box; its depth-texel
    ///                 footprint sizes the grid RT, which is read back whole (async GPU).
    ///   4. UNPROJECT: On readback completion, schedule a Burst job (DepthUnprojectionJob)
    ///                 that copies the grid readback 1:1 into the flat world-space hit grid
    ///                 stored in SurfaceBuffer.
    ///   5. HAND-OFF:  Invoke the caller's callback with a JobHandle the downstream
    ///                 extraction pipeline can chain onto.
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
        // from the depth texture into the grid-resolution blit target.
        private Material _blitMaterial;
        // World positions are written to a per-frame pooled RenderTexture sized to the forearm
        // grid (RenderTexture.GetTemporary in TryDispatch), not a persistent full-screen RT — the
        // blit runs at grid resolution (~cols×rows fragments) instead of over the whole screen.
        // Set to true when AsyncGPUReadback.Request is enqueued, false at the
        // start of its callback. Reset at callback start (not end) so that
        // exceptions in downstream processing don't permanently stall the pipeline.
        private bool _isReadbackPending;

        // Native resolution of Meta's _EnvironmentDepthTexture (e.g. 320x320), cached once.
        // The forearm crop is sampled at ~1:1 with these texels (grid = crop's texel footprint).
        private int _depthTexW, _depthTexH;

        // ------------------------------------------------------------------
        // HAND MASK (GPU silhouette)
        // ------------------------------------------------------------------
        // HandMask provides the CPU-baked mesh and localToWorld each frame.
        private HandMask _handMaskSource;
        // Tuning, set once via the constructor from ForearmDepthSurface Inspector values:
        //   MaskDilateTexels     — mask dilation radius in mask texels, applied at sample time in
        //                          MetaDepthCopy (3x3 max) to cover readback latency.
        //   DepthSmoothRadius    — edge-aware depth blur radius (0 = off).
        //   DepthSmoothThreshold — max LINEAR depth diff (metres) for a neighbor to be averaged in.
        public float MaskDilateTexels     = 1f;
        public int   DepthSmoothRadius    = 1;
        public float DepthSmoothThreshold = 0.01f;
        // CommandBuffer that clears and re-draws the hand mesh each frame.
        private CommandBuffer _maskCmd;
        // Unlit white material used to render the silhouette (Hidden/HandMaskRender).
        private Material _handMaskMat;

        /// <summary>
        /// Loads the MetaDepthCopy and HandMaskRender shaders, creates materials, and stores the
        /// tuning parameters. Shaders must be present in the project and not stripped from builds.
        /// </summary>
        public DepthReadback(HandMask handMaskSource, float maskDilateTexels, int depthSmoothRadius, float depthSmoothThreshold)
        {
            MaskDilateTexels     = maskDilateTexels;
            DepthSmoothRadius    = depthSmoothRadius;
            DepthSmoothThreshold = depthSmoothThreshold;

            Shader shader = Shader.Find("Hidden/MetaDepthCopy");
            if (shader == null)
            {
                Debug.LogError("[Depth] MetaDepthCopy shader not found. Check shader is in project and not stripped from build.");
                return;
            }
            _blitMaterial = new Material(shader);

            _handMaskSource = handMaskSource;
            _maskCmd        = new CommandBuffer { name = "HandMaskRender" };

            Shader maskShader = Shader.Find("Hidden/HandMaskRender");
            if (maskShader != null)
                _handMaskMat = new Material(maskShader);
            else
                Debug.LogWarning("[Depth] HandMaskRender shader not found — hand masking disabled.");
        }

        /// <summary>
        /// Validates depth matrices, crops the forearm region, blits world positions,
        /// and enqueues an async GPU readback + Burst unproject job.
        /// Returns true only when a readback was actually enqueued; false on any
        /// early-out (readback in flight, no depth matrices, arm off-screen, shader missing).
        /// Callers must only arm their in-flight guard when this returns true.
        /// </summary>
        public bool TryDispatch(
            ArmFrame arm,
            float maxRadialDist,
            SurfaceBuffer buffer,
            Action<JobHandle, int, int> onComplete)
        {
            // Abort if the shader failed to load at construction, or a readback is still in flight.
            if (_blitMaterial == null) return false;
            if (_isReadbackPending) return false;

            // Meta sets _EnvironmentDepthReprojectionMatrices once per depth frame.
            // Index 0 is the left eye world->clip matrix for the depth camera's pose
            // at the time that depth frame was captured (not the current render pose).
            Matrix4x4[] depthMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            if (depthMatrices == null || depthMatrices.Length == 0) return false;

            // Depth texture dimensions are needed to size the grid; bail until it's bound.
            if (!TryCacheDepthDims()) return false;

            Camera cam = arm.Cam;
            Vector3 wristPos = arm.WristPos;
            Vector3 elbowPos = arm.ElbowPos;
            Vector3 camPos   = cam.transform.position;

            // Compute the screen-space bounding box of the forearm using the depth
            // camera's VP matrix so crop coordinates align with the depth texture.
            if (!CalculateArmBounds(
                ref depthMatrices[0], ref wristPos, ref elbowPos, ref camPos,
                cam.fieldOfView, cam.pixelWidth, cam.pixelHeight,
                maxRadialDist,
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

            // Render the hand mask + world-position blit at grid resolution; returns the pooled
            // world-position RT to read back (released in the readback callback).
            RenderTexture rt = DispatchReconstruction(
                depthMatrices[0], cropX, cropY, cropW, cropH, screenW, screenH, cols, rows);

            // Read back the whole grid-resolution RT — the RT *is* the grid, so no sub-region crop.
            _isReadbackPending = true;
            AsyncGPUReadback.Request(
                rt, 0,
                request => HandleReadback(request, rt, buffer, rows, cols, onComplete));
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
        /// writing Vector4 (xyz = world pos, w = rawDepth, or w = -1 for invalid). Returns the
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

            // Invert the depth frame's world->clip matrix once on the CPU (clip->world in the
            // shader). depthVP is the left-eye VP for the pose at depth-capture time.
            _blitMaterial.SetMatrix("_DepthInverseVP", depthVP.inverse);

            // Remap the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect, so
            // each output texel samples the depth texture at the correct screen position.
            // Shader does depthUV = uv * scale + offset.
            _blitMaterial.SetVector("_CropUVScaleOffset", new Vector4(scaleX, scaleY, offsetX, offsetY));

            // Edge-aware depth-smoothing params for the bilateral pass in MetaDepthCopy.
            _blitMaterial.SetInt("_DepthSmoothRadius", DepthSmoothRadius);
            _blitMaterial.SetFloat("_DepthSmoothThreshold", DepthSmoothThreshold);
            _blitMaterial.SetVector("_DepthTexelSize",
                new Vector4(1f / _depthTexW, 1f / _depthTexH, _depthTexW, _depthTexH));

            // Render the hand silhouette before the blit, at grid resolution with the crop
            // remapped to fill the target — so the blit samples it 1:1 at its own UV.
            RenderTexture maskRT = RenderHandMask(depthVP, scaleX, scaleY, offsetX, offsetY, cols, rows);

            // A pooled temporary RT avoids per-frame allocation churn as the crop size changes.
            RenderTexture rt = RenderTexture.GetTemporary(cols, rows, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(null, rt, _blitMaterial);

            // The blit has sampled the mask; return it to the pool now (the readback reads only
            // the world-position RT, not the mask).
            if (maskRT != null) RenderTexture.ReleaseTemporary(maskRT);

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
                HasDepth       = buffer.HasDepth,
                IsSurface      = buffer.IsSurface
            };

            onComplete?.Invoke(job.Schedule(rows * cols, 64), rows, cols);
        }

        /// <summary>
        /// Releases the blit material, mask material, and command buffer. Call when the
        /// component is destroyed. The mask RenderTexture is pooled (GetTemporary) and
        /// released each frame, so there is nothing persistent to free here.
        /// </summary>
        public void Dispose()
        {
            if (_blitMaterial != null) UnityEngine.Object.Destroy(_blitMaterial);
            if (_handMaskMat  != null) UnityEngine.Object.Destroy(_handMaskMat);
            _maskCmd?.Release();
        }

        // --------------------------------------------------------
        // HAND MASK RENDER
        // --------------------------------------------------------

        /// <summary>
        /// Renders the CPU-baked hand mesh as a white silhouette into a pooled grid-resolution
        /// (cols×rows) RenderTexture using CommandBuffer.DrawMesh with Meta's depth VP. A crop
        /// remap matrix maps the forearm's NDC sub-rect onto the full target, so the silhouette
        /// fills the mask at ~one texel per depth texel and MetaDepthCopy can sample it 1:1 at
        /// the blit's own UV (no full-screen oversampling, no screen-space crop remap).
        ///
        /// Returns the pooled RenderTexture (caller releases it after the blit), or null when
        /// there is no hand to draw — in which case _HandMaskTex is bound to black so the blit
        /// rejects nothing.
        /// </summary>
        private RenderTexture RenderHandMask(
            Matrix4x4 depthVP,
            float scaleX, float scaleY, float offsetX, float offsetY,
            int maskW, int maskH)
        {
            if (_handMaskSource == null || _handMaskMat == null ||
                _handMaskSource.BakedMesh == null || _handMaskSource.BakedMesh.vertexCount == 0)
            {
                _blitMaterial.SetTexture("_HandMaskTex", Texture2D.blackTexture);
                return null;
            }

            // Crop remap: maps the forearm crop's NDC sub-rect to full NDC so the silhouette
            // fills the grid-resolution target. Derived from the inverse of the blit's
            // depthUV = uv*scale + offset, expressed in clip space (x' = x/scale + b*w):
            //   ndc' = ndc/scale + (1 - 2*offset - scale)/scale.
            // Folded into the depth VP, then HandMaskRender applies the Vulkan Y flip last.
            Matrix4x4 crop = Matrix4x4.identity;
            crop.m00 = 1f / scaleX; crop.m03 = (1f - 2f * offsetX - scaleX) / scaleX;
            crop.m11 = 1f / scaleY; crop.m13 = (1f - 2f * offsetY - scaleY) / scaleY;
            Matrix4x4 maskVP = crop * depthVP;

            RenderTexture maskRT = RenderTexture.GetTemporary(maskW, maskH, 0, RenderTextureFormat.R8);
            _blitMaterial.SetTexture("_HandMaskTex", maskRT);
            // Set the texel size explicitly: Graphics.Blit binds the material outside the
            // normal SRP path, so don't rely on Unity auto-populating _HandMaskTex_TexelSize.
            _blitMaterial.SetVector("_HandMaskTex_TexelSize",
                new Vector4(1f / maskW, 1f / maskH, maskW, maskH));

            _handMaskMat.SetMatrix("_DepthVP", maskVP);
            // Sample-time dilation radius (now in grid/depth-texel units), applied in MetaDepthCopy's 3x3 max.
            _blitMaterial.SetFloat("_MaskDilateTexels", MaskDilateTexels);

            _maskCmd.Clear();
            _maskCmd.SetRenderTarget(maskRT);
            _maskCmd.ClearRenderTarget(false, true, Color.black);
            // DrawMesh with the CPU-baked mesh: vertex positions are already skinned.
            // UNITY_MATRIX_M is set from localToWorldMatrix by the DrawMesh call.
            _maskCmd.DrawMesh(_handMaskSource.BakedMesh, _handMaskSource.LocalToWorld, _handMaskMat);
            Graphics.ExecuteCommandBuffer(_maskCmd);
            return maskRT;
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
            float maxRadialDist,
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

            [WriteOnly] public NativeArray<Vector3> Hits;
            [WriteOnly] public NativeArray<bool>    HasDepth;
            [WriteOnly] public NativeArray<bool>    IsSurface;

            public void Execute(int index)
            {
                Vector4 sample = WorldPositions[index];

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
