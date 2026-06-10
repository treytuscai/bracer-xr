// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

// Grid-resolution blit that unprojects the stabilized environment depth into a world-space position
// per texel. Output (Vector4): xyz = world position, w = linear (metric) depth, or w = -1 (invalid).
// This shader only rejects invalid depth and unprojects — all hand handling (carving the finger,
// reconstructing the lifted bleed edge) happens upstream in DepthTemporalMedian, so the stabilized
// depth already has the finger as invalid and the lift replaced with clean arm. The frag just trusts
// it: 0 or 1 here is invalid (the carved finger is 0), anything strictly inside is real surface.
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
// is intentional — the depth is a bound texture, not Blit's source.

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
                // Maps the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect:
                //   depthUV = uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw
                // Set per frame by DepthReadback.Schedule (scaleX, scaleY, offsetX, offsetY) so
                // each output texel's clip-space xy lands at the correct screen position.
                float4 _CropUVScaleOffset;
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

            // Unprojects (duv, rawDepth) through the depth frame's historical inverse VP to world.
            // xyz = world position; w = linear (metric) depth — both the valid flag (w >= 0) and the
            // true-depth signal MeshGenerator reads to cut triangles.
            float4 UnprojectDepth(float2 duv, float rawDepth)
            {
                // (U, V, rawDepth) screen [0,1] -> clip [-1,1]; rawDepth becomes clip Z (skipping the Z
                // remap would pin every pixel near the camera — Vulkan NDC 0.92 sits near the far plane).
                // Then clip -> world via the depth frame's inverse VP, and a perspective divide.
                float3 clipXYZ  = float3(duv, rawDepth) * 2.0 - 1.0;
                float4 worldH   = mul(_DepthInverseVP, float4(clipXYZ, 1.0));
                float3 worldPos = worldH.xyz / worldH.w;
                return float4(worldPos, LinearizeDepth(rawDepth));
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
                // sub-rect) is the clip-space xy for the unprojection below.
                float2 duv = input.uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw;

                // Sample the stabilized depth and reject invalid. The finger is already carved to 0
                // upstream, so it falls out here with the near/far plane (0 and 1 are meaningless);
                // w = -1 is the out-of-band sentinel DepthUnprojectionJob checks. The lifted edge was
                // reconstructed upstream too, so it arrives as ordinary valid surface.
                float rawDepth = SampleDepthR(input.uv);
                if (rawDepth <= 0.0 || rawDepth >= 1.0)
                    return float4(0, 0, 0, -1.0);

                // Unproject (duv, rawDepth) to world. xyz = world pos, w = linear metres — the valid
                // flag and the triangle-cut depth MeshGenerator reads.
                return UnprojectDepth(duv, rawDepth);
            }
            ENDHLSL
        }
    }
}
