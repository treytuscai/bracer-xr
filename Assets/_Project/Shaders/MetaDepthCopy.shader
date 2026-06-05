// MetaDepthCopy.shader
// Grid-resolution blit that unprojects the stabilized environment depth into a world-space position
// per texel. Output (Vector4): xyz = world position, w = linear (metric) depth, or w = -1 (invalid).
// A bilateral pass denoises the surface interior; the boundary flicker was already removed upstream
// by the temporal median, so this shader only denoises and unprojects. The frag body documents the
// reconstruction steps inline.
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

            // Screen-space hand silhouette rendered each frame by DepthReadback via
            // CommandBuffer.DrawMesh before this blit. White = hand, black = clear.
            // Declared outside CBUFFER — textures cannot live inside a constant buffer.
            TEXTURE2D(_HandMaskTex);
            SAMPLER(sampler_HandMaskTex);

            // Temporally-stabilized depth: a 3-frame, motion-reprojected per-texel median of the
            // environment depth (native ~320x320 layout), produced by DepthReadback's
            // DepthTemporalMedian pre-pass. This is the only depth source — raw NDC [0,1] in R.
            TEXTURE2D(_StabilizedDepthTex);
            SAMPLER(sampler_StabilizedDepthTex);

            // Set globally by Meta's EnvironmentDepthManager. Encodes NDC depth -> linear metres:
            //   linear = 1 / (ndc + y) * x,  with ndc = rawDepth * 2 - 1.
            // Lets the bilateral threshold be a real distance instead of an opaque NDC value.
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
                // Edge-aware (bilateral) depth smoothing (applied in frag before unprojection).
                //   _DepthSmoothRadius    — neighborhood half-width in depth texels (0 = off, 1 = 3x3).
                //   _DepthSmoothThreshold — max LINEAR depth diff (metres) for a neighbor to average in.
                //   _DepthTexelSize       — (1/width, 1/height, width, height) of the depth texture.
                int    _DepthSmoothRadius;
                float  _DepthSmoothThreshold;
                float4 _DepthTexelSize;
            CBUFFER_END

            // NDC depth [0,1] -> linear eye-space distance in metres (Meta's global ZBuffer params).
            float LinearizeDepth(float rawDepth)
            {
                float ndc = rawDepth * 2.0 - 1.0;
                return (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
            }

            // Samples the stabilized (temporally-medianed) depth. Raw NDC [0,1] in R.
            // LOD 0: no mips, and the _LOD form takes no derivatives (safe inside the dynamic loop).
            float SampleDepthR(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_StabilizedDepthTex, sampler_StabilizedDepthTex, uv, 0).r;
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

                // Step 1: sample the stabilized depth (Vulkan NDC [0,1]).
                float centerDepth = SampleDepthR(duv);

                // Step 2: reject invalid depth. Must be strictly inside (0,1). Values at 0 (near
                // plane) or 1 (far plane / sky) are meaningless. w = -1 is the out-of-band sentinel
                // DepthUnprojectionJob checks via sample.w < 0.
                if (centerDepth <= 0.0 || centerDepth >= 1.0)
                    return float4(0, 0, 0, -1.0);

                // Step 2b: BILATERAL (edge-aware) depth smoothing. Average the (2R+1)^2 neighborhood,
                // but only neighbors within _DepthSmoothThreshold METRES of the center (a metric test,
                // not raw NDC which is nonlinear) — so the blur denoises the surface without bridging
                // the arm/background discontinuity. Averages NDC depth; only the test is metric.
                // _DepthSmoothRadius = 0 disables it.
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
                        // Explicit LOD 0 (no derivatives) — this sample is inside a dynamic loop.
                        float  nd  = SampleDepthR(nuv);
                        if (nd <= 0.0 || nd >= 1.0) continue;
                        if (abs(LinearizeDepth(nd) - centerLinear) < _DepthSmoothThreshold)
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

                // Hand mask: reject pixels the hand silhouette covers. _HandMaskTex is rendered at
                // grid resolution (crop remapped to fill [0,1]) so it samples at this fragment's own
                // uv, NOT the screen-space duv. The 3x3 max is a cheap dilation that grows the
                // effective mask by _MaskDilateTexels (in grid/depth-texel units) to cover readback
                // latency — the depth hand trails the current mesh — without fattening the silhouette.
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

                // Return world position in xyz; w carries the LINEAR (metric) depth, both as the
                // valid/invalid flag (w >= 0 valid, w = -1 sentinel) and as the true-depth signal
                // MeshGenerator uses to cut triangles across discontinuities.
                return float4(worldPos, LinearizeDepth(rawDepth));
            }
            ENDHLSL
        }
    }
}
