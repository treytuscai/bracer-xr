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

            // Set globally by Meta's EnvironmentDepthManager. Encodes NDC depth -> linear metres:
            //   linear = 1 / (ndc + y) * x,  with ndc = rawDepth * 2 - 1.
            // Lets the smoothing threshold be a real distance instead of an opaque NDC value.
            float4 _EnvironmentDepthZBufferParams;

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
                // Maps the grid-resolution blit's [0,1] UV onto the forearm's screen-UV sub-rect:
                //   depthUV = uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw
                // Set per frame by DepthReadback.Schedule (scaleX, scaleY, offsetX, offsetY) so
                // each output texel samples the depth texture at the correct screen position.
                float4 _CropUVScaleOffset;
                // Edge-aware depth smoothing (applied in frag before unprojection).
                //   _DepthSmoothRadius    — neighborhood half-width in depth texels (0 = off, 1 = 3x3).
                //   _DepthSmoothThreshold — max NDC depth diff for a neighbor to be averaged in.
                //   _DepthTexelSize       — (1/width, 1/height, width, height) of the depth texture.
                int    _DepthSmoothRadius;
                float  _DepthSmoothThreshold;   // max LINEAR depth diff (metres) to average a neighbor
                float4 _DepthTexelSize;
            CBUFFER_END

            // NDC depth [0,1] -> linear eye-space distance in metres (Meta's global ZBuffer params).
            float LinearizeDepth(float rawDepth)
            {
                float ndc = rawDepth * 2.0 - 1.0;
                return (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
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

                // Remap this grid-resolution output texel's [0,1] UV onto the forearm's screen-UV
                // sub-rect. duv is used for ALL screen-space sampling below (depth + hand mask) and
                // as the depth-frame NDC xy — it replaces the old full-screen input.uv everywhere.
                float2 duv = input.uv * _CropUVScaleOffset.xy + _CropUVScaleOffset.zw;

                // Step 1: sample the R channel of the left-eye depth slice (index 0).
                // Depth is in Vulkan NDC [0,1]. Only R is used — Meta does not pack into G/B/A.
                float centerDepth = SAMPLE_TEXTURE2D_ARRAY(
                    _EnvironmentDepthTexture, sampler_EnvironmentDepthTexture,
                    duv, 0).r;

                // Step 2: reject invalid depth. Must be strictly inside (0,1). Values at 0 (near
                // plane) or 1 (far plane / sky) are meaningless. w = -1 is the out-of-band sentinel
                // DepthUnprojectionJob checks via sample.w < 0.
                if (centerDepth <= 0.0 || centerDepth >= 1.0)
                    return float4(0, 0, 0, -1.0);

                // Step 2b: BILATERAL (edge-aware) depth smoothing. Average the (2R+1)^2 depth-texel
                // neighborhood, but include only neighbors whose LINEAR depth is within
                // _DepthSmoothThreshold METRES of the center — so the blur denoises the surface
                // WITHOUT crossing the arm/background discontinuity (a naive blur would bridge the
                // arm into the background). The threshold is a real distance (via Meta's ZBuffer
                // params), so it behaves consistently across the depth range — unlike a raw NDC diff,
                // which is nonlinear. We still average the NDC depth (what unprojection consumes);
                // only the inclusion test is metric. _DepthSmoothRadius = 0 disables it.
                float centerLinear = LinearizeDepth(centerDepth);
                float depthSum     = centerDepth;   // accumulate NDC depth for unprojection
                float weightSum    = 1.0;
                [loop]
                for (int ny = -_DepthSmoothRadius; ny <= _DepthSmoothRadius; ny++)
                {
                    [loop]
                    for (int nx = -_DepthSmoothRadius; nx <= _DepthSmoothRadius; nx++)
                    {
                        if (nx == 0 && ny == 0) continue;
                        float2 nuv = duv + float2(nx, ny) * _DepthTexelSize.xy;
                        float  nd  = SAMPLE_TEXTURE2D_ARRAY(
                            _EnvironmentDepthTexture, sampler_EnvironmentDepthTexture, nuv, 0).r;
                        if (nd > 0.0 && nd < 1.0 &&
                            abs(LinearizeDepth(nd) - centerLinear) < _DepthSmoothThreshold)
                        {
                            depthSum  += nd;
                            weightSum += 1.0;
                        }
                    }
                }
                float rawDepth = depthSum / weightSum;

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

                // Hand mask: reject pixels covered by the hand silhouette.
                // _HandMaskTex is rendered each frame via CommandBuffer.DrawMesh before this
                // blit at GRID resolution with the crop remapped to fill [0,1] — so it is
                // sampled at this fragment's own uv (1:1 with the grid), NOT the screen-space
                // crop duv. White where the hand mesh covers the forearm crop, black elsewhere.
                //
                // Rather than one tap, sample a 3x3 neighborhood and take the max — a cheap
                // morphological dilation. This grows the effective mask by _MaskDilateTexels
                // texels to cover the readback-latency gap (the depth hand trails the current
                // mesh) WITHOUT fattening the rendered silhouette or dropping the 0.5 threshold,
                // so a stationary hand keeps tight while a moving one stays covered.
                // NOTE: a mask texel now equals a grid cell (~one depth texel), so dilation is
                // in grid/depth-texel units, not the old half-screen-pixel units.
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
