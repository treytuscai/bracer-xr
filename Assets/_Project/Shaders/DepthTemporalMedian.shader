// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

// Two-pass depth stabilization that removes the forearm-boundary flicker (see DepthReadback).
//
// Pass 0 EXTRACT: copy the left-eye slice (index 0) of Meta's _EnvironmentDepthTexture array into a
//   plain 2D R-channel target kept as frame history. Hand handling happens here so it lands in history:
//   the silhouette is carved to invalid (a moving hand must not reproject onto clean arm and corrupt
//   the pass-1 median; the carved hole is also what keeps the surface off the visible hand), the
//   occlusion cushion around it is rebuilt at borrowed arm depth, and the bleed ring beyond that is
//   reconstructed from clean arm (TryBorrowArmDepth). All of it is then medianed in pass 1.
//   See _HandMaskTex / _HandSilhouetteTex below.
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

            // Global set by Meta's EnvironmentDepthManager — the raw per-eye depth array.
            TEXTURE2D_ARRAY(_EnvironmentDepthTexture);
            SAMPLER(sampler_EnvironmentDepthTexture);

            // Full depth-frame GROWN hand mask, built by HandMaskRender's separable max passes
            // BEFORE this pass: R = silhouette dilated by the occlusion margin (the no-trust
            // cushion), G = dilated by the hand margin (the bleed-reconstruct ring). The finger is
            // written invalid into history so it can't become a pass-1 median sample: pass 1
            // reprojects history as a static world, so a moving hand's old position would
            // otherwise land on this frame's clean arm and lift the edge.
            TEXTURE2D(_HandMaskTex);
            SAMPLER(sampler_HandMaskTex);

            // The UNGROWN silhouette (HandMaskRender pass 0 output), distinguishing the hand
            // itself (carved to a hole) from the cushion R adds around it (rebuilt at arm depth).
            TEXTURE2D(_HandSilhouetteTex);
            SAMPLER(sampler_HandSilhouetteTex);

            // NDC->metres params (global, set by Meta). Used by the lifted-edge borrow to tell arm
            // from a farther background; matches the CPU unprojection's linearization (DepthReadback).
            float4 _EnvironmentDepthZBufferParams;

            // One native depth texel in depth UV: _DepthTexelSize.xy = (1/_depthTexW, 1/_depthTexH).
            // The borrow march steps by this, so pass 0 stays fully full-frame native — the crop
            // is only a spatial restriction on later passes, never a unit in here.
            float4 _DepthTexelSize;
            // The ring's width in depth texels — only the march's step bound here; the growth
            // itself is baked into _HandMaskTex by HandMaskRender.
            int    _HandMarginTexels;
            // Depth window (m): a borrowed sample within this of the nearest still counts as the same
            // surface when reconstructing the ring (rejects a farther background).
            float  _BorrowDepthBand;

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

            // Left-eye (slice 0) raw NDC depth at a depth UV. LOD 0 — no mips.
            float SampleDepthRaw(float2 duv)
            {
                return SAMPLE_TEXTURE2D_ARRAY_LOD(_EnvironmentDepthTexture,
                                                  sampler_EnvironmentDepthTexture, duv, 0, 0).r;
            }

            // NDC [0,1] -> linear metres (Meta's global ZBuffer params; matches DepthUnprojectionJob).
            float LinearizeDepth(float raw)
            {
                float ndc = raw * 2.0 - 1.0;
                return (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
            }

            // Grown-mask tap (dilation baked upstream by HandMaskRender):
            // r = no-trust cushion (occlusion margin), g = reconstruct ring (hand margin).
            float2 SampleGrownMask(float2 duv)
            {
                return SAMPLE_TEXTURE2D_LOD(_HandMaskTex, sampler_HandMaskTex, duv, 0).rg;
            }

            // LIFTED-EDGE RECONSTRUCTION: estimate the local arm depth for a ring texel. March 8 rays
            // outward, each stopping at the first texel past the margin mask (O(margin) per ray, so a wide
            // margin stays cheap) — that first clean sample is the arm in that direction, with the lift
            // skipped. Take the nearest of the 8 and average the ones within _BorrowDepthBand of it (a
            // farther sample is background). Returns false when no ray reaches clean arm (finger off arm).
            bool TryBorrowArmDepth(float2 duv, out float armRaw)
            {
                float2 stepUV   = _DepthTexelSize.xy;       // one native depth texel
                int    maxSteps = _HandMarginTexels + 2;    // far enough to clear the ring

                // 8 outward directions (integer steps, so a fixed maxSteps clears the ring in every one).
                float2 dirs[8] = {
                    float2( 1, 0), float2(-1, 0), float2( 0, 1), float2( 0,-1),
                    float2( 1, 1), float2( 1,-1), float2(-1, 1), float2(-1,-1)
                };

                float found[8];          // first clean depth on each ray, -1 if the ray never cleared
                float minLin = 1e30;

                [unroll]
                for (int i = 0; i < 8; i++)
                {
                    found[i] = -1.0;
                    [loop]
                    for (int s = 1; s <= maxSteps; s++)
                    {
                        float2 o = duv + dirs[i] * float(s) * stepUV;
                        if (SampleGrownMask(o).g > 0.5) continue;   // still in ring -> keep going
                        float r = SampleDepthRaw(o);
                        if (r > 0.0 && r < 1.0)                                    // first clean, valid arm
                        {
                            found[i] = r;
                            minLin   = min(minLin, LinearizeDepth(r));
                        }
                        break;                                                    // cleared the ring -> stop ray
                    }
                }

                if (minLin > 1e29) { armRaw = -1.0; return false; }   // no ray reached clean arm

                // Average the boundary samples on the nearest surface; drop any that sit a background away.
                float sumRaw = 0.0;
                float count  = 0.0;
                [unroll]
                for (int j = 0; j < 8; j++)
                {
                    if (found[j] < 0.0) continue;
                    if (LinearizeDepth(found[j]) > minLin + _BorrowDepthBand) continue;
                    sumRaw += found[j];
                    count  += 1.0;
                }
                armRaw = sumRaw / count;   // count >= 1: the nearest sample always qualifies
                return true;
            }

            float frag(Varyings input) : SV_Target
            {
                float2 duv = input.uv;   // pass 0 renders full-frame, so input.uv IS depth UV

                // Hand silhouette: carved to 0 -> a hole that occludes (the consumer rejects 0),
                // so no surface renders over the hand. Pass 1 keeps it via its invalid-input
                // fallback (dCur <= 0).
                if (SAMPLE_TEXTURE2D_LOD(_HandSilhouetteTex, sampler_HandSilhouetteTex, duv, 0).r > 0.5)
                    return 0.0;

                float2 grown = SampleGrownMask(duv);

                // Occlusion cushion (R, outside the silhouette): the strongest bleed plus the real
                // hand peeking past the rendered mask — measured depth here is never trusted (at
                // touch distance hand depth ≈ arm depth, so no per-cell test can tell them apart).
                // Rebuild it at borrowed arm depth so the canvas continues up to the silhouette
                // instead of widening the hole; with nothing clean to borrow, carve as before.
                if (grown.r > 0.5)
                {
                    float armRaw;
                    if (TryBorrowArmDepth(duv, armRaw)) return armRaw;
                    return 0.0;
                }

                // Margin ring (G, outside the occlusion cushion): a mix of lifted bleed and real
                // surface the wide margin reaches over (e.g. the arm in the gap to the thumb). Estimate
                // the local arm, then decide per cell by depth so only raised cells move:
                //   - nearer than the arm -> lifted bleed -> pull onto the arm.
                //   - at/behind the arm   -> real surface -> keep its own depth (no bridging across gaps).
                //   - invalid             -> fill from the estimate.
                // The result enters history, so pass 1 medians it.
                if (grown.g > 0.5)
                {
                    float cellRaw   = SampleDepthRaw(duv);
                    bool  cellValid = (cellRaw > 0.0 && cellRaw < 1.0);

                    float armRaw;
                    if (TryBorrowArmDepth(duv, armRaw))
                    {
                        if (cellValid && LinearizeDepth(cellRaw) >= LinearizeDepth(armRaw))
                            return cellRaw;   // real surface at/behind arm (gap to thumb/fist) -> keep
                        return armRaw;        // lifted (or invalid) -> flatten to the local arm
                    }
                    // Nothing clean to borrow: keep real surface if present (don't bridge), else hole.
                    return cellValid ? cellRaw : 0.0;
                }

                // Clean: left-eye (slice 0) raw depth.
                return SampleDepthRaw(duv);
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

            // Raw [0,1] depth + screen UV -> world position, via a frame's inverse VP. Matches the
            // CPU unprojection (DepthUnprojectionJob): clip = (uv, raw) * 2 - 1, inverse-VP, w-divide.
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
                // Invalid current depth: pass through; the CPU unprojection's (0,1) test rejects it.
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
