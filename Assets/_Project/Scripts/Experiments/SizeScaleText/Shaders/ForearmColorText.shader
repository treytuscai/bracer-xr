// ForearmColorText.shader
// Color-picker experiment surface shader. Replaces the ChooseColor experiment.
// Renders a text glyph (e.g. "Hello World") onto the forearm display surface with
// two independently adjustable layers, each with its own opacity:
//
//   Background ("canvas") — _BgColor (rgb + a). a = 0 → text sits directly on skin;
//                           raise a to fill the display region behind the text.
//   Text (foreground)     — _TextColor (rgb + a), masked by the alpha of _TextTex.
//
// Per-material properties (driven by the color pickers via C#):
//   _TextTex   — glyph mask. Alpha channel = text coverage (white text on transparent).
//   _TextColor — foreground text color; a = text opacity.
//   _BgColor   — background canvas color; a = background opacity.
//
// Geometry/UV are identical to Custom/ForearmProjection (same forearm display mesh),
// so this just swaps the compositing. No touch-debug overlay — the color picker UI
// lives on a separate panel, not on the arm surface.

Shader "Custom/ForearmColorText"
{
    Properties
    {
        _TextTex   ("Text (alpha = glyph)", 2D) = "black" {}
        _TextColor ("Text Color", Color)        = (1, 1, 1, 1)
        _BgColor   ("Background Color", Color)   = (1, 1, 1, 0)
    }

    SubShader
    {
        // Transparent so the skin (passthrough) shows through wherever alpha < 1.
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Required for Quest stereo rendering.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TextTex);
            SAMPLER(sampler_TextTex);

            // All per-material uniforms inside UnityPerMaterial for the SRP Batcher.
            CBUFFER_START(UnityPerMaterial)
                half4 _TextColor;
                half4 _BgColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0; // forearm display UV (see MeshGenerator.CalculateUV)
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

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Outside the display patch (pronation scroll / landscape offset push verts
                // past [0,1]): discard so the texture isn't sampled clamped at the edge.
                if (input.uv.x < 0.0 || input.uv.x > 1.0 ||
                    input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                // Text coverage from the glyph texture's alpha (white text on transparent bg).
                half mask  = SAMPLE_TEXTURE2D(_TextTex, sampler_TextTex, input.uv).a;
                half textA = mask * _TextColor.a;

                // Composite: start from the background canvas, lay the text over it.
                // textA = 0 (no glyph) leaves the background (or transparent skin if _BgColor.a = 0).
                half3 rgb = lerp(_BgColor.rgb, _TextColor.rgb, textA);
                half  a   = max(_BgColor.a, textA);

                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
}
