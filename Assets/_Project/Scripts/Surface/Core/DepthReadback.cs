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
    /// Manages the full GPU->CPU depth pipeline each frame:
    ///   1. HAND MASK: Render the hand mesh as a white silhouette into a half-res
    ///                 RenderTexture via CommandBuffer.DrawMesh using Meta's depth VP.
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
        // Grayscale RenderTexture containing the hand silhouette in screen space.
        // White = hand, black = clear. Sampled by MetaDepthCopy to reject hand pixels.
        // Half-resolution: silhouette masking doesn't need full precision.
        private RenderTexture _handMaskRT;
        // CommandBuffer that clears and re-draws the hand mesh each frame.
        private CommandBuffer _maskCmd;
        // Unlit white material used to render the silhouette (Hidden/HandMaskRender).
        private Material _handMaskMat;

        // ------------------------------------------------------------------
        // DIAGNOSTICS (temporary — for the optimization pass)
        // ------------------------------------------------------------------
        // When true, TryDispatch() logs depth-buffer characterization: texture size vs
        // render resolution (-> the pixelStride below which we double-sample the depth
        // buffer), the forearm grid vs real depth-texel count, and the depth update
        // rate (render frames per depth frame). Toggle from ForearmDepthSurface; set
        // false (default) for zero overhead. Remove this block once measured.
        public bool LogDiagnostics = false;
        // When true, TryDispatch() logs diagnostics then early-returns, skipping ALL reconstruction
        // GPU work (hand-mask render + full-screen blit + readback + downstream). Isolates whether
        // this pipeline is the fps bottleneck: fps still logs, but the surface stops updating.
        public bool SkipReconstruction = false;
        private Matrix4x4 _lastDepthMatrix;   // previous depth reprojection matrix (change = new depth frame)
        private int _depthFrameChanges;       // depth-matrix changes seen in the current window
        private int _diagRenderFrames;        // render frames (TryDispatch calls) in the current window
        private bool _loggedStaticInfo;       // one-shot guard for the size/oversampling log

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
                maxRadialDist,
                out int cropX, out int cropY, out int cropW, out int cropH))
            {
                return false; // Arm behind camera or off-screen
            }

            // Sample the forearm crop at the depth buffer's NATIVE resolution: one grid cell per
            // real depth texel spanning the crop (no screen-pixel stride). The per-axis footprint
            // handles the anisotropic screen->texel mapping — the render is double-wide while the
            // depth texture is square. Depth dims are constant; cache them once.
            if (_depthTexW == 0)
            {
                Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
                if (depthTex == null) return false; // not bound yet; reconstruct next frame
                _depthTexW = depthTex.width;
                _depthTexH = depthTex.height;
            }

            int screenW = cam.pixelWidth;
            int screenH = cam.pixelHeight;
            // Minimum 2 per axis: a 1×N grid produces no quads and boundary smoothing needs ≥2 cells.
            int cols = Mathf.Max(2, Mathf.RoundToInt((float)cropW / screenW * _depthTexW));
            int rows = Mathf.Max(2, Mathf.RoundToInt((float)cropH / screenH * _depthTexH));

            // DIAGNOSTICS (temporary): characterize depth-buffer size and update rate.
            if (LogDiagnostics) Diagnostics(cam, depthMatrices[0], cropX, cropY, cropW, cropH, cols, rows);

            // DIAGNOSTICS (temporary): bail before any reconstruction GPU work to test if this
            // pipeline is the fps bottleneck. Returns false so the caller's in-flight guard stays clear.
            if (SkipReconstruction) return false;

            // Invert the depth frame's world->clip matrix once on the CPU (clip->world in the
            // shader). depthMatrices[0] is the left-eye VP for the pose at depth-capture time.
            _blitMaterial.SetMatrix("_DepthInverseVP", depthMatrices[0].inverse);

            // Remap the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect, so
            // each output texel samples the depth texture at the correct screen position.
            // Layout: (scaleX, scaleY, offsetX, offsetY); shader does depthUV = uv * scale + offset.
            // NOTE: if the reconstructed surface comes out vertically mirrored, flip V here —
            //   set scaleY = -(float)cropH / screenH and offsetY = (float)(cropY + cropH) / screenH.
            _blitMaterial.SetVector("_CropUVScaleOffset", new Vector4(
                (float)cropW / screenW, (float)cropH / screenH,
                (float)cropX / screenW, (float)cropY / screenH));

            // Edge-aware depth-smoothing params for the bilateral pass in MetaDepthCopy.
            _blitMaterial.SetInt("_DepthSmoothRadius", DepthSmoothRadius);
            _blitMaterial.SetFloat("_DepthSmoothThreshold", DepthSmoothThreshold);
            _blitMaterial.SetVector("_DepthTexelSize",
                new Vector4(1f / _depthTexW, 1f / _depthTexH, _depthTexW, _depthTexH));

            // Render the hand silhouette before the blit. It stays in full screen-UV space; the
            // blit samples it at the remapped crop UV. depthMatrices[0] aligns the silhouette
            // with the depth texture's UV space.
            RenderHandMask(depthMatrices[0], screenW, screenH);

            // Blit MetaDepthCopy at GRID resolution (cols×rows) rather than full screen: it runs
            // ~cols*rows fragments instead of the full ~9M, each doing sample R -> build clip ->
            // inverse VP -> perspective divide, writing Vector4 (xyz = world pos, w = rawDepth, or
            // w = -1 for invalid). A pooled temporary RT avoids per-frame allocation churn as the
            // crop size changes; it is released in the readback callback.
            RenderTexture rt = RenderTexture.GetTemporary(cols, rows, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(null, rt, _blitMaterial);

            // Read back the whole grid-resolution RT — the RT *is* the grid, so no sub-region crop.
            _isReadbackPending = true;
            AsyncGPUReadback.Request(
                rt, 0,
                request =>
                {
                    // Reset + release the pooled RT before any processing so exceptions downstream
                    // don't permanently stall the pipeline or leak the temporary.
                    _isReadbackPending = false;
                    RenderTexture.ReleaseTemporary(rt);

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

                    buffer.ResizeIfNeeded(rows, cols);

                    // WorldPositions is now the grid itself (row-major, width = cols): cell
                    // (r, c) lives at index r*cols + c — no stride/crop remap needed.
                    var job = new DepthUnprojectionJob
                    {
                        WorldPositions = raw,
                        Hits           = buffer.Hits,
                        HasDepth       = buffer.HasDepth,
                        IsSurface      = buffer.IsSurface
                    };

                    onComplete?.Invoke(job.Schedule(rows * cols, 64), rows, cols);
                });
            return true;
        }

        // ------------------------------------------------------------------
        // DIAGNOSTICS (temporary instrumentation for the optimization pass)
        // ------------------------------------------------------------------
        /// <summary>
        /// Logs depth-buffer characterization to confirm two things before tuning:
        ///   (A) Depth update rate — Meta rewrites _EnvironmentDepthReprojectionMatrices
        ///       once per depth frame, so counting render frames between changes gives the
        ///       render-frames-per-depth-frame ratio (and implied depth Hz). Summarized ~1/sec.
        ///   (B) Size / oversampling — the raw depth texture is only ~320px across the full
        ///       FOV, but we sample the full-res reconstruction every pixelStride SCREEN pixels.
        ///       If our grid has more cells than there are real depth texels under the crop,
        ///       we re-read the same texels (double-sample). Logged once. The px-per-texel
        ///       figure assumes the depth texture and render share the same FOV (approx).
        /// </summary>
        private void Diagnostics(Camera cam, Matrix4x4 depthMatrix, int cropX, int cropY, int cropW, int cropH, int cols, int rows)
        {
            // (A) Depth update rate — count matrix changes over a window, summarize once per ~90 frames.
            _diagRenderFrames++;
            if (depthMatrix != _lastDepthMatrix)
            {
                _depthFrameChanges++;
                _lastDepthMatrix = depthMatrix;
            }
            if (_diagRenderFrames >= 90)
            {
                float avgGap   = _depthFrameChanges > 0 ? (float)_diagRenderFrames / _depthFrameChanges : 0f;
                float renderHz = Time.deltaTime > 0f ? 1f / Time.deltaTime : 0f;
                float depthHz  = avgGap > 0f ? renderHz / avgGap : 0f;
                Debug.Log($"[DepthDiag] {_depthFrameChanges} depth updates over {_diagRenderFrames} render frames " +
                          $"-> ~{avgGap:F1} render frames/depth frame (~{depthHz:F0} Hz depth at ~{renderHz:F0} Hz render)");
                _diagRenderFrames  = 0;
                _depthFrameChanges = 0;
            }

            // (B) Size / sampling — one-shot. We now size the grid to the crop's depth-texel
            // footprint, so this should report ~1:1. Empirical, assumption-free check: replicate
            // each grid cell -> crop pixel -> screen UV -> depth texel and count DISTINCT texels.
            if (_loggedStaticInfo) return;

            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            var hitTexels = new HashSet<long>();
            for (int r = 0; r < rows; r++)
            {
                int dy = Mathf.Min(cropH - 1, (int)((r + 0.5f) / rows * cropH));
                int ty = Mathf.Clamp((int)(((cropY + dy) / (float)screenH) * _depthTexH), 0, _depthTexH - 1);
                for (int c = 0; c < cols; c++)
                {
                    int dx = Mathf.Min(cropW - 1, (int)((c + 0.5f) / cols * cropW));
                    int tx = Mathf.Clamp((int)(((cropX + dx) / (float)screenW) * _depthTexW), 0, _depthTexW - 1);
                    hitTexels.Add(((long)ty << 32) | (uint)tx);
                }
            }
            int gridCells = rows * cols, uniqueTexels = hitTexels.Count;
            Debug.Log(
                $"[DepthDiag] depthTex={_depthTexW}x{_depthTexH}  render={screenW}x{screenH}  crop={cropW}x{cropH}px -> grid {rows}x{cols}\n" +
                $"[DepthDiag] {gridCells} grid cells hit {uniqueTexels} distinct depth texels " +
                $"-> {(float)gridCells / Mathf.Max(1, uniqueTexels):F2}x sampling " +
                $"({(gridCells > uniqueTexels + uniqueTexels / 20 ? "still oversampling" : "~1:1, native")})");

            _loggedStaticInfo = true;
        }

        /// <summary>
        /// Releases the RenderTexture and blit material. Call when the component is destroyed.
        /// </summary>
        public void Dispose()
        {
            if (_handMaskRT  != null) _handMaskRT.Release();
            if (_blitMaterial  != null) UnityEngine.Object.Destroy(_blitMaterial);
            if (_handMaskMat   != null) UnityEngine.Object.Destroy(_handMaskMat);
            _maskCmd?.Release();
        }

        // --------------------------------------------------------
        // HAND MASK RENDER
        // --------------------------------------------------------

        /// <summary>
        /// Renders the CPU-baked hand mesh as a white silhouette into _handMaskRT using
        /// CommandBuffer.DrawMesh with Meta's depth VP. The silhouette aligns with the
        /// depth texture's UV space so MetaDepthCopy can reject hand pixels with a single
        /// texture sample. Half-resolution is sufficient for silhouette masking.
        /// </summary>
        private void RenderHandMask(Matrix4x4 depthVP, int screenW, int screenH)
        {
            if (_handMaskSource == null || _handMaskMat == null) return;

            Mesh bakedMesh = _handMaskSource.BakedMesh;
            if (bakedMesh == null || bakedMesh.vertexCount == 0) return;

            int maskW = screenW / 2;
            int maskH = screenH / 2;
            if (_handMaskRT == null || _handMaskRT.width != maskW || _handMaskRT.height != maskH)
            {
                if (_handMaskRT != null) _handMaskRT.Release();
                _handMaskRT = new RenderTexture(maskW, maskH, 0, RenderTextureFormat.R8);
                _handMaskRT.Create();
                _blitMaterial.SetTexture("_HandMaskTex", _handMaskRT);
                // Set the texel size explicitly: Graphics.Blit binds the material outside the
                // normal SRP path, so don't rely on Unity auto-populating _HandMaskTex_TexelSize.
                _blitMaterial.SetVector("_HandMaskTex_TexelSize",
                    new Vector4(1f / maskW, 1f / maskH, maskW, maskH));
            }

            // Pass Meta's depth camera VP so the shader projects hand vertices into the
            // same UV space as the depth texture. HandMaskRender handles the Vulkan Y flip.
            _handMaskMat.SetMatrix("_DepthVP", depthVP);
            // Sample-time dilation radius (texels), applied in MetaDepthCopy's 3x3 max.
            _blitMaterial.SetFloat("_MaskDilateTexels", MaskDilateTexels);

            _maskCmd.Clear();
            _maskCmd.SetRenderTarget(_handMaskRT);
            _maskCmd.ClearRenderTarget(false, true, Color.black);
            // DrawMesh with the CPU-baked mesh: vertex positions are already skinned.
            // UNITY_MATRIX_M is set from localToWorldMatrix by the DrawMesh call.
            _maskCmd.DrawMesh(bakedMesh, _handMaskSource.LocalToWorld, _handMaskMat);
            Graphics.ExecuteCommandBuffer(_maskCmd);
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
