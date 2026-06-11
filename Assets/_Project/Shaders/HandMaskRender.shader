// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

// Produces the GROWN hand mask in depth-frame UV space each dispatch, in two stages:
//   Pass 0 GEOMETRY: render the CPU-baked hand mesh as a solid white silhouette (R8).
//   Pass 1+2 DILATE: grow the silhouette with a separable max filter (horizontal, then
//     vertical) into two channels: R = within _OccMarginTexels of the hand (carve zone),
//     G = within _HandMarginTexels (the bleed-reconstruct ring). Separable passes compute
//     the exact (2m+1)^2 dilation in 2*(2m+1) taps per texel, so the margin holds even
//     around features narrower than itself (a pointing finger).
//
// The temporal median's extract pass (DepthTemporalMedian pass 0) is the sole consumer:
// it single-taps R to carve the occluded hole and G to reconstruct the lifted bleed ring.
//
// Pass 0 is rendered via CommandBuffer.DrawMesh with Meta's depth VP (_DepthVP =
// depthMatrices[0]) so the silhouette aligns with the depth texture's UV space, not Unity's
// camera space. The depth sensor is physically different from Unity's render camera; using
// Unity's VP would produce a fixed spatial offset in the mask. _ProjectionParams.x corrects
// the Vulkan render target Y flip when using a custom projection matrix.

Shader "Hidden/HandMaskRender"
{
    SubShader
    {
        // ZTest Always: write every fragment regardless of depth — we want the full
        // silhouette, not just the visible front face.
        // ZWrite Off: output goes to a color RenderTexture, not depth.
        // Cull Off: both front and back faces contribute to the silhouette so the
        // mask covers fingers curled over the forearm from any viewing angle.
        Cull Off ZWrite Off ZTest Always

        // ---------------------------------------------------------------
        // PASS 0 — hand mesh -> white silhouette (drawn with shaderPass 0 explicitly:
        // the dilate passes below must never rasterize the mesh)
        // ---------------------------------------------------------------
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Meta's depth camera VP at capture time — set from C# as depthMatrices[0].
            // Must be the depth frame's own VP (not Unity's camera VP) since the depth sensor
            // is a physically different camera from Unity's render camera.
            CBUFFER_START(UnityPerMaterial)
                float4x4 _DepthVP;
                float4   _DilateTexelSize;
                int      _OccMarginTexels;
                int      _HandMarginTexels;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
            };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float4 worldPos    = mul(UNITY_MATRIX_M, i.positionOS);
                float4 clipPos     = mul(_DepthVP, worldPos);
                clipPos.y         *= _ProjectionParams.x;
                o.positionCS       = clipPos;
                return o;
            }

            // Output solid white — the grown mask is thresholded at 0.5 in DepthTemporalMedian.
            float4 frag(Varyings i) : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // PASS 1 — horizontal max: R8 silhouette -> RG (R = ±_OccMarginTexels,
        // G = ±_HandMarginTexels).
        // ---------------------------------------------------------------
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Source for the separable max, bound per-blit via CommandBuffer.SetGlobalTexture —
            // a recorded, ordered command, so the two passes can chain raw -> tmp -> grown inside
            // one command buffer (a material-level texture resolves to its LAST value for both
            // recorded blits at execute time).
            TEXTURE2D(_DilateSrcTex);
            SAMPLER(sampler_DilateSrcTex);

            CBUFFER_START(UnityPerMaterial)
                float4x4 _DepthVP;
                // One mask texel in UV: (1/width, 1/height). The march and margins in the
                // median step by the same native texel, so the units line up.
                float4   _DilateTexelSize;
                int      _OccMarginTexels;
                int      _HandMarginTexels;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv         = i.uv;
                return o;
            }

            // One loop over the larger radius feeds both channels: the occlusion max only
            // accumulates taps within its smaller radius (occ stays below hand by contract).
            float2 frag(Varyings i) : SV_Target
            {
                float occ  = 0.0;
                float hand = 0.0;
                [loop]
                for (int s = -_HandMarginTexels; s <= _HandMarginTexels; s++)
                {
                    float m = SAMPLE_TEXTURE2D_LOD(_DilateSrcTex, sampler_DilateSrcTex,
                                                   i.uv + float2(s, 0) * _DilateTexelSize.xy, 0).r;
                    hand = max(hand, m);
                    if (abs(s) <= _OccMarginTexels) occ = max(occ, m);
                }
                return float2(occ, hand);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // PASS 2 — vertical max on the pass-1 intermediate, per channel with its own
        // radius. Horizontal-then-vertical max over a square window equals the full
        // (2m+1)^2 dilation exactly.
        // ---------------------------------------------------------------
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Same per-blit global as pass 1 — rebound to the pass-1 intermediate before this pass.
            TEXTURE2D(_DilateSrcTex);
            SAMPLER(sampler_DilateSrcTex);

            CBUFFER_START(UnityPerMaterial)
                float4x4 _DepthVP;
                float4   _DilateTexelSize;
                int      _OccMarginTexels;
                int      _HandMarginTexels;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv         = i.uv;
                return o;
            }

            float2 frag(Varyings i) : SV_Target
            {
                float occ  = 0.0;
                float hand = 0.0;
                [loop]
                for (int s = -_HandMarginTexels; s <= _HandMarginTexels; s++)
                {
                    float2 m = SAMPLE_TEXTURE2D_LOD(_DilateSrcTex, sampler_DilateSrcTex,
                                                    i.uv + float2(0, s) * _DilateTexelSize.xy, 0).rg;
                    hand = max(hand, m.g);
                    if (abs(s) <= _OccMarginTexels) occ = max(occ, m.r);
                }
                return float2(occ, hand);
            }
            ENDHLSL
        }
    }
}
