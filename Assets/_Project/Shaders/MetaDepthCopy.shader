// MetaDepthCopy.shader
// Full-screen blit shader that reconstructs a world-space position for every screen pixel
// from Meta's environment depth texture. Outputs a Vector4 render texture where:
//   xyz = world-space position
//   w   = raw depth value ∈ (0,1) for valid pixels, -1 as an invalid sentinel
//
// DEPTH SOURCE
// Meta's environment depth API computes depth from the Quest's two main RGB cameras via
// stereo reconstruction — not a dedicated IR depth sensor. The output is a standard
// [0,1] NDC depth texture in the R channel. Only R is sampled; Meta does not pack any
// additional data into G/B/A.
//
// WHY A BLIT SHADER, NOT A REGULAR RENDERING SHADER
// A standard rendering shader runs in the current render frame's coordinate space.
// The depth texture was captured at an earlier head pose (the depth sensor frame), so
// its UV and depth values were encoded relative to that historical pose — not the
// current camera pose. Unprojecting with the current VP would misplace every pixel.
// This shader receives the inverse of Meta's historical VP via _DepthInverseVP (inverted
// in C# by DepthReadback.Schedule before the blit) and reconstructs positions in the
// depth frame's coordinate space, which is already in world space.
//
// ANTI-SWIM
// _DepthInverseVP is the inverse of _EnvironmentDepthReprojectionMatrices[0], which
// Meta captures at the exact moment the depth frame was rendered. Using this historical
// matrix eliminates the pose desync between the depth frame and the current render frame,
// preventing the reconstructed surface from drifting as the user moves their head.
//
// RECONSTRUCTION STEPS (community-verified)
//   1. Sample R channel of _EnvironmentDepthTexture (slice 0 = left eye) -> rawDepth ∈ (0,1).
//   2. Reject rawDepth ≤ 0 or ≥ 1 (sky, near-plane, out-of-range) -> output w = -1.
//   3. Remap (U, V, rawDepth) from [0,1] to clip-space [-1,1] via * 2 - 1.
//   4. Mul by _DepthInverseVP (clip -> world for the depth frame's pose).
//   5. Perspective divide (xyz / w) -> world position.
//
// CALLED FROM: DepthReadback.Schedule() via Graphics.Blit(null, _worldPosRT, _blitMaterial).
// The null source is intentional — the shader reads _EnvironmentDepthTexture as a global,
// not from Unity's Blit source texture. Graphics.Blit bypasses the SRP Batcher, so the
// CBUFFER below is for correctness and consistency rather than batching performance.

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

            // Meta's stereo depth texture array; slice 0 = left eye, slice 1 = right eye.
            // Declared as a global (outside CBUFFER) — set by Meta's EnvironmentDepthManager,
            // not as a per-material property.
            TEXTURE2D_ARRAY(_EnvironmentDepthTexture);
            SAMPLER(sampler_EnvironmentDepthTexture);

            // Screen-space hand silhouette rendered each frame by DepthReadback via
            // CommandBuffer.DrawMesh before this blit. White = hand, black = clear.
            // Declared outside CBUFFER — textures cannot live inside a constant buffer.
            TEXTURE2D(_HandMaskTex);
            SAMPLER(sampler_HandMaskTex);

            CBUFFER_START(UnityPerMaterial)
                // Inverse of _EnvironmentDepthReprojectionMatrices[0]: left-eye world->clip
                // matrix for the depth frame's historical pose. Inverted once per frame on
                // the CPU by DepthReadback.Schedule to convert clip->world in this shader.
                float4x4 _DepthInverseVP;
                // (1/width, 1/height, width, height) of _HandMaskTex, auto-populated by
                // Unity when the texture is bound. Used to step the dilation kernel by texels.
                float4 _HandMaskTex_TexelSize;
                // Mask dilation radius in mask texels. The kernel samples a neighborhood and
                // takes the max, growing the EFFECTIVE silhouette by this many texels without
                // re-bloating the rendered mesh or lowering the 0.5 threshold. Compensates for
                // the 1-2 frame readback latency (the depth hand lags the current mesh).
                float _MaskDilateTexels;
            CBUFFER_END

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

                // Step 1: sample the R channel of the left-eye depth slice (index 0).
                // rawDepth is in Vulkan NDC [0,1]. Only R is used — Meta does not
                // pack additional data into G/B/A.
                float rawDepth = SAMPLE_TEXTURE2D_ARRAY(
                    _EnvironmentDepthTexture, sampler_EnvironmentDepthTexture,
                    input.uv, 0).r;

                // Step 2: reject invalid depth. rawDepth must be strictly inside (0,1).
                // Values at 0 (near plane) or 1 (far plane / sky) are meaningless.
                // w = -1 is the out-of-band sentinel; DepthUnprojectionJob checks sample.w < 0.
                if (rawDepth <= 0.0 || rawDepth >= 1.0)
                    return float4(0, 0, 0, -1.0);

                // Step 3: remap (U, V, rawDepth) from screen-space [0,1] to clip-space [-1,1].
                // The UV becomes the XY of the clip-space point and rawDepth becomes Z.
                // Skipping the Z remap would land every pixel near the camera — Vulkan NDC
                // depth like 0.92 sits close to Z = 1.0 (far plane) without remapping.
                float3 clipXYZ = float3(input.uv, rawDepth) * 2.0 - 1.0;
                float4 clipPos  = float4(clipXYZ, 1.0);

                // Step 4: transform clip -> world using the depth frame's historical inverse VP.
                float4 worldH = mul(_DepthInverseVP, clipPos);

                // Step 5: perspective divide — converts homogeneous coordinates to world position.
                float3 worldPos = worldH.xyz / worldH.w;

                // Hand mask: reject pixels covered by the hand silhouette.
                // _HandMaskTex is a screen-space grayscale texture rendered each frame
                // via CommandBuffer.DrawMesh before this blit — white where the hand mesh
                // covers the screen, black elsewhere.
                //
                // Rather than one tap, sample a 3x3 neighborhood and take the max — a cheap
                // morphological dilation. This grows the effective mask by _MaskDilateTexels
                // texels to cover the readback-latency gap (the depth hand trails the current
                // mesh) WITHOUT fattening the rendered silhouette or dropping the 0.5 threshold,
                // so a stationary hand keeps tight while a moving one stays covered.
                float2 texelStep = _HandMaskTex_TexelSize.xy * _MaskDilateTexels;
                float mask = 0.0;
                UNITY_UNROLL
                for (int dy = -1; dy <= 1; dy++)
                    UNITY_UNROLL
                    for (int dx = -1; dx <= 1; dx++)
                        mask = max(mask, SAMPLE_TEXTURE2D(_HandMaskTex, sampler_HandMaskTex,
                                                          input.uv + float2(dx, dy) * texelStep).r);
                if (mask > 0.5)
                    return float4(0, 0, 0, -1.0);

                // Return world position in xyz; w carries rawDepth so DepthReadback can
                // distinguish valid pixels (w >= 0) from the invalid sentinel (w = -1).
                return float4(worldPos, rawDepth);
            }
            ENDHLSL
        }
    }
}
