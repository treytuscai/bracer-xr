// MetaDepthCopy.shader
// Grid-resolution blit that unprojects the stabilized environment depth into a world-space position
// per texel. Output (Vector4): xyz = world position, w = linear (metric) depth, or w = -1 (invalid).
// Denoising is handled upstream by the temporal median (DepthTemporalMedian); this shader only masks
// the hand, rejects invalid depth, and unprojects. The frag body documents the steps inline.
//
// DEPTH SOURCE: _StabilizedDepthTex — a 3-frame reprojected median of Meta's stereo-camera depth
// (not IR), raw NDC [0,1] in R, produced by DepthReadback's pre-pass.
//
// HISTORICAL VP (anti-swim): the depth was captured at an earlier head pose, so it is unprojected
// with the inverse of THAT frame's VP — _DepthInverseVP = inverse of
// _EnvironmentDepthReprojectionMatrices[0], inverted on the CPU in DepthReadback (HLSL per-pixel
// inversion is expensive). Using the current camera VP instead would misplace every pixel and make
// the surface swim as the head moves; this lands the result directly in world space.
//
// Called from DepthReadback.DispatchReconstruction via Graphics.Blit(null, rt, mat). The null source
// is intentional — depth and mask are bound textures, not Blit's source.

Shader "Hidden/MetaDepthCopy"
{
    // "Hidden/" prefix: shader does not appear in the material dropdown.
    SubShader
    {
        // Standard full-screen blit state.
        // Cull Off: quad faces any direction, both sides rendered.
        // ZWrite Off: output goes to a color RenderTexture, not depth.
        // ZTest Always: always overwrite regardless of existing depth values.
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Full depth-frame hand silhouette rendered each frame by DepthReadback via
            // CommandBuffer.DrawMesh, BEFORE the temporal median's extract pass (which carves the hand
            // out of depth history) and this blit. White = hand, black = clear. Sampled in depth UV
            // (duv). Declared outside CBUFFER — textures cannot live inside a constant buffer.
            TEXTURE2D(_HandMaskTex);
            SAMPLER(sampler_HandMaskTex);

            // Temporally-stabilized depth: a 3-frame, motion-reprojected per-texel median of the
            // environment depth (native ~320x320 layout), produced by DepthReadback's
            // DepthTemporalMedian pre-pass. This is the only depth source — raw NDC [0,1] in R.
            TEXTURE2D(_StabilizedDepthTex);
            SAMPLER(sampler_StabilizedDepthTex);

            // Set globally by Meta's EnvironmentDepthManager. Encodes NDC depth -> linear metres:
            //   linear = 1 / (ndc + y) * x,  with ndc = rawDepth * 2 - 1.
            // Used to emit the metric depth in the output w (the triangle-cut signal MeshGenerator reads).
            float4 _EnvironmentDepthZBufferParams;

            CBUFFER_START(UnityPerMaterial)
                // Inverse of _EnvironmentDepthReprojectionMatrices[0]: left-eye world->clip
                // matrix for the depth frame's historical pose. Inverted once per frame on
                // the CPU by DepthReadback.Schedule to convert clip->world in this shader.
                float4x4 _DepthInverseVP;
                // Mask dilation radius in WHOLE grid texels (int — a fraction of a texel is meaningless).
                // The 3x3 kernel takes the max, growing the EFFECTIVE silhouette by this many texels
                // without re-bloating the rendered mesh or lowering the 0.5 threshold. Compensates for
                // the 1-2 frame readback latency (the depth hand lags the current mesh).
                int _MaskDilateTexels;
                // Maps the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect:
                //   depthUV = uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw
                // Set per frame by DepthReadback.Schedule (scaleX, scaleY, offsetX, offsetY) so
                // each output texel samples the depth texture at the correct screen position.
                float4 _CropUVScaleOffset;
                // (1/cols, 1/rows, cols, rows) of the crop grid (= the stabilized depth resolution).
                // Used to step the hand-mask dilation kernel by one grid texel (mapped into depth UV
                // via _CropUVScaleOffset). See SampleHandMaskDilated.
                float4 _GridTexelSize;
            CBUFFER_END

            // NDC depth [0,1] -> linear eye-space distance in metres (Meta's global ZBuffer params).
            float LinearizeDepth(float rawDepth)
            {
                float ndc = rawDepth * 2.0 - 1.0;
                return (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
            }

            // Samples the stabilized (temporally-medianed) depth. Raw NDC [0,1] in R. LOD 0: no mips.
            float SampleDepthR(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_StabilizedDepthTex, sampler_StabilizedDepthTex, uv, 0).r;
            }

            // Samples the hand silhouette mask (full depth-frame resolution; 1 = hand, 0 = clear),
            // in DEPTH UV (duv). LOD 0 — no mips on the mask.
            float SampleHandMask(float2 duv)
            {
                return SAMPLE_TEXTURE2D_LOD(_HandMaskTex, sampler_HandMaskTex, duv, 0).r;
            }

            // Dilated hand-mask test in DEPTH UV: 3x3 max stepped by _MaskDilateTexels whole grid texels.
            // One grid texel is mapped into depth UV as _CropUVScaleOffset.xy * _GridTexelSize.xy (the
            // grid is the crop, so a grid step scales by the crop's UV size). Grows the EFFECTIVE
            // silhouette to cover (a) the 1-2 frame readback latency (the depth hand trails the live
            // mesh) and (b) imperfect-mesh peek-through. MUST match the median's extract carve so the
            // rejected region and the carved-from-history region are identical. Returns max coverage.
            float SampleHandMaskDilated(float2 duv)
            {
                float2 texelStep = _CropUVScaleOffset.xy * _GridTexelSize.xy * _MaskDilateTexels;
                float m = 0.0;
                UNITY_UNROLL
                for (int dy = -1; dy <= 1; dy++)
                    UNITY_UNROLL
                    for (int dx = -1; dx <= 1; dx++)
                        m = max(m, SampleHandMask(duv + float2(dx, dy) * texelStep));
                return m;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv         = input.uv;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // The stabilized depth is the forearm CROP rendered at grid resolution, so it is sampled
                // 1:1 at this output texel's own [0,1] uv — NOT remapped. duv (the forearm's screen-UV
                // sub-rect) is the depth-frame UV: it is the NDC xy for the clip-space reconstruction
                // (Step 3) AND the lookup into the full-frame hand mask (Step 1).
                float2 duv = input.uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw;

                // Step 1: HAND MASK FIRST — reject pixels the hand silhouette covers. Dilated (see
                // SampleHandMaskDilated) to cover readback latency, imperfect-mesh peek-through, AND the
                // stereo-depth bleed ring: arm texels just outside a hovering finger that Meta's stereo
                // pulls toward the finger (the lifted edge). The dilation eats that ring; it is the same
                // radius the median's extract pass carved out of history. The mask is full depth-frame,
                // so sample it in depth UV (duv).
                if (SampleHandMaskDilated(duv) > 0.5)
                    return float4(0, 0, 0, -1.0);

                // Step 2: sample the stabilized (crop-resolution) depth at this texel (Vulkan NDC [0,1]),
                // and reject invalid. Must be strictly inside (0,1); 0 (near plane) or 1 (far plane /
                // sky) are meaningless. w = -1 is the out-of-band sentinel DepthUnprojectionJob checks.
                float rawDepth = SampleDepthR(input.uv);
                if (rawDepth <= 0.0 || rawDepth >= 1.0)
                    return float4(0, 0, 0, -1.0);

                // Step 3: remap (U, V, rawDepth) from screen-space [0,1] to clip-space [-1,1].
                // The UV becomes the XY of the clip-space point and rawDepth becomes Z.
                // Skipping the Z remap would land every pixel near the camera — Vulkan NDC
                // depth like 0.92 sits close to Z = 1.0 (far plane) without remapping.
                float3 clipXYZ = float3(duv, rawDepth) * 2.0 - 1.0;
                float4 clipPos  = float4(clipXYZ, 1.0);

                // Step 4: transform clip -> world using the depth frame's historical inverse VP.
                float4 worldH = mul(_DepthInverseVP, clipPos);

                // Step 5: perspective divide — converts homogeneous coordinates to world position.
                float3 worldPos = worldH.xyz / worldH.w;

                // Return world position in xyz; w carries the LINEAR (metric) depth, both as the
                // valid/invalid flag (w >= 0 valid, w = -1 sentinel) and as the true-depth signal
                // MeshGenerator uses to cut triangles across discontinuities.
                return float4(worldPos, LinearizeDepth(rawDepth));
            }
            ENDHLSL
        }
    }
}