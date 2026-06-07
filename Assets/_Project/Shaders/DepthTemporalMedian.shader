// DepthTemporalMedian.shader
// Two-pass depth stabilization that removes the forearm-boundary flicker (see DepthReadback).
//
// Pass 0 EXTRACT: copy the left-eye slice (index 0) of Meta's _EnvironmentDepthTexture array
//   into a plain 2D R-channel target, so it can be kept as frame history. The (dilated) hand
//   silhouette is carved out here (written invalid) so the moving hand can never reproject onto
//   clean arm and corrupt the median in pass 1 — see _HandMaskTex below.
// Pass 1 MEDIAN3: per-texel median of the current frame + two history frames REPROJECTED into the
//   current head pose -> stabilized depth. The median rejects stereo "flying pixels" because they
//   are temporal OUTLIERS (a pixel that jumps to a wrong intermediate depth one frame is discarded),
//   unlike an EMA which would blend them in. Reprojection (pass 1 below) aligns the history to the
//   current pose so it holds up under head motion, not just a static head.
//
// RESIDUAL: reprojection assumes a static world, so a moving ARM lags the edge by ~1 frame, and
// newly-disoccluded texels fall back to the current frame until history fills — both minor.

Shader "Hidden/DepthTemporalMedian"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ---------------------------------------------------------------
        // PASS 0 — EXTRACT left-eye depth slice into a 2D R texture
        // ---------------------------------------------------------------
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Global set by Meta's EnvironmentDepthManager (same source MetaDepthCopy reads).
            TEXTURE2D_ARRAY(_EnvironmentDepthTexture);
            SAMPLER(sampler_EnvironmentDepthTexture);

            // Full depth-frame hand silhouette (1 = hand), rendered by DepthReadback BEFORE this pass.
            // Hand texels are written invalid (0) into history so they can't become a pass-1 median
            // sample: the hand MOVES, but pass 1 reprojects history as a static world, so last frame's
            // hand would otherwise land on this frame's clean arm and lift the edge.
            TEXTURE2D(_HandMaskTex);
            SAMPLER(sampler_HandMaskTex);

            // Dilation kernel, set per frame to MATCH MetaDepthCopy's reject exactly, so the carved-out
            // region equals the rejected one. One grid texel in depth UV = _CropUVScaleOffset.xy *
            // _GridTexelSize.xy; the radius is _MaskDilateTexels of those.
            float4 _CropUVScaleOffset;
            float4 _GridTexelSize;
            int    _MaskDilateTexels;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // 3x3 dilated hand-mask test in depth UV, matching MetaDepthCopy's SampleHandMaskDilated.
            float HandMaskDilated(float2 duv)
            {
                float2 texelStep = _CropUVScaleOffset.xy * _GridTexelSize.xy * _MaskDilateTexels;
                float m = 0.0;
                UNITY_UNROLL
                for (int dy = -1; dy <= 1; dy++)
                    UNITY_UNROLL
                    for (int dx = -1; dx <= 1; dx++)
                        m = max(m, SAMPLE_TEXTURE2D_LOD(_HandMaskTex, sampler_HandMaskTex,
                                                        duv + float2(dx, dy) * texelStep, 0).r);
                return m;
            }

            float frag(Varyings input) : SV_Target
            {
                // Carve the (dilated) hand out of history: store 0 (invalid) where the silhouette covers
                // this texel. Pass 1's invalid->fallback check (dh <= 0) then drops it. input.uv is depth UV.
                if (HandMaskDilated(input.uv) > 0.5)
                    return 0.0;

                // Slice 0 = left eye. LOD 0 (no derivatives) — no mips on the depth texture.
                return SAMPLE_TEXTURE2D_ARRAY_LOD(
                    _EnvironmentDepthTexture, sampler_EnvironmentDepthTexture, input.uv, 0, 0).r;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // PASS 1 — MEDIAN of the current frame + two REPROJECTED history frames
        //
        // The two history frames are warped into the current head pose before the median, so all
        // three samples line up on the same world point regardless of head motion. Per current
        // texel: unproject current depth -> world P; project P into each history frame, sample its
        // depth, unproject -> the surface point that frame saw along this ray; bring that point back
        // into the current frame as a raw depth; median the three. Reprojection that lands off-frame
        // or on an invalid history texel falls back to the current depth (disocclusion / frame edge),
        // so the median degrades to "no temporal" there instead of punching a hole.
        // Assumes a static world (only the camera moved) — a moving arm's history lands at its old
        // position, leaving a ~1-frame edge lag during arm motion.
        // ---------------------------------------------------------------
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TexCur); SAMPLER(sampler_TexCur); // current frame's raw depth
            TEXTURE2D(_TexH1);  SAMPLER(sampler_TexH1);  // history frame 1
            TEXTURE2D(_TexH2);  SAMPLER(sampler_TexH2);  // history frame 2

            // Maps the cropped output's [0,1] UV onto the forearm's sub-rect in the full depth frame.
            // The median computes only the crop; history stays full-frame for reprojection.
            float4 _CropUVScaleOffset;

            // Depth-frame world->clip VPs and their inverses, per frame (set by DepthReadback).
            float4x4 _CurVP;  float4x4 _CurInvVP;
            float4x4 _H1VP;   float4x4 _H1InvVP;
            float4x4 _H2VP;   float4x4 _H2InvVP;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // Raw [0,1] depth + screen UV -> world position, via a frame's inverse VP. Matches
            // MetaDepthCopy's reconstruction: clip = (uv, raw) * 2 - 1, then inverse-VP + w-divide.
            float3 UnprojectRaw(float2 uv, float raw, float4x4 invVP)
            {
                float4 clip  = float4(uv * 2.0 - 1.0, raw * 2.0 - 1.0, 1.0);
                float4 world = mul(invVP, clip);
                return world.xyz / world.w;
            }

            // Reproject world point P into a history frame, sample the surface it saw along that
            // direction, and express that point as a raw depth in the CURRENT frame. Returns
            // fallbackRaw when the lookup lands off-frame or on an invalid history texel.
            float ReprojToCurrentRaw(float3 P, float4x4 histVP, float4x4 histInvVP,
                                     Texture2D histTex, SamplerState histSampler, float fallbackRaw)
            {
                float4 hclip = mul(histVP, float4(P, 1.0));
                if (hclip.w <= 0.0) return fallbackRaw;
                float2 huv = (hclip.xy / hclip.w) * 0.5 + 0.5;
                if (any(huv < 0.0) || any(huv > 1.0)) return fallbackRaw;

                float dh = SAMPLE_TEXTURE2D_LOD(histTex, histSampler, huv, 0).r;
                if (dh <= 0.0 || dh >= 1.0) return fallbackRaw;

                float3 Ph    = UnprojectRaw(huv, dh, histInvVP);
                float4 cclip = mul(_CurVP, float4(Ph, 1.0));
                if (cclip.w <= 0.0) return fallbackRaw;
                return (cclip.z / cclip.w) * 0.5 + 0.5; // current-frame NDC z -> raw [0,1]
            }

            float frag(Varyings input) : SV_Target
            {
                // Output is the forearm crop; remap to the full-frame depth UV for the current frame.
                // Histories stay full-frame, sampled at the reprojected UV in ReprojToCurrentRaw.
                float2 duv = input.uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw;

                float dCur = SAMPLE_TEXTURE2D_LOD(_TexCur, sampler_TexCur, duv, 0).r;
                // Invalid current depth: pass through; MetaDepthCopy's (0,1) test rejects it.
                if (dCur <= 0.0 || dCur >= 1.0) return dCur;

                float3 P  = UnprojectRaw(duv, dCur, _CurInvVP);
                float  n1 = ReprojToCurrentRaw(P, _H1VP, _H1InvVP, _TexH1, sampler_TexH1, dCur);
                float  n2 = ReprojToCurrentRaw(P, _H2VP, _H2InvVP, _TexH2, sampler_TexH2, dCur);

                // median of three = max(min(a,b), min(max(a,b), c))
                return max(min(dCur, n1), min(max(dCur, n1), n2));
            }
            ENDHLSL
        }
    }
}
