// DepthTemporalMedian.shader
// Two-pass depth stabilization that removes the forearm-boundary flicker (see DepthReadback).
//
// Pass 0 EXTRACT: copy the left-eye slice (index 0) of Meta's _EnvironmentDepthTexture array
//   into a plain 2D R-channel target, so it can be kept as frame history.
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

            float frag(Varyings input) : SV_Target
            {
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
                float dCur = SAMPLE_TEXTURE2D_LOD(_TexCur, sampler_TexCur, input.uv, 0).r;
                // Invalid current depth: pass through; MetaDepthCopy's (0,1) test rejects it.
                if (dCur <= 0.0 || dCur >= 1.0) return dCur;

                float3 P  = UnprojectRaw(input.uv, dCur, _CurInvVP);
                float  n1 = ReprojToCurrentRaw(P, _H1VP, _H1InvVP, _TexH1, sampler_TexH1, dCur);
                float  n2 = ReprojToCurrentRaw(P, _H2VP, _H2InvVP, _TexH2, sampler_TexH2, dCur);

                // median of three = max(min(a,b), min(max(a,b), c))
                return max(min(dCur, n1), min(max(dCur, n1), n2));
            }
            ENDHLSL
        }
    }
}
