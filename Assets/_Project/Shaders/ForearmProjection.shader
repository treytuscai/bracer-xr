Shader "Custom/ForearmProjection"
{
    Properties
    {
        _MainTex    ("UI Texture", 2D)                          = "white" {}
        _Color      ("Tint", Color)                             = (1,1,1,1)
        _FadeWidth  ("Edge Fade Width (m)", Float)              = 0.015
        // xy = UV coordinate, z = active (1) / inactive (0), w = unused
        _TouchPoint ("Touch Debug Point (uv.xy, active, _)", Vector) = (0,0,0,0)
        _TouchRadius("Touch Debug Radius (UV)", Float)          = 0.02
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            half4  _Color;
            float  _FadeWidth;
            float4 _TouchPoint;
            float  _TouchRadius;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 edgeDist   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float  edgeDist   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv         = input.uv;
                output.edgeDist   = input.edgeDist.x;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (input.uv.x < 0.0 || input.uv.x > 1.0 ||
                    input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                float2 uv = input.uv;
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _Color;
                col.a *= smoothstep(0.0, _FadeWidth, input.edgeDist);

                // Touch debug: draw a filled green circle at the touch UV
                if (_TouchPoint.z > 0.5)
                {
                    float2 diff = uv - _TouchPoint.xy;
                    float  d    = length(diff);
                    float  r    = _TouchRadius;
                    // Filled circle with soft 0.005 UV-unit antialiased edge
                    float circle = 1.0 - smoothstep(r - 0.005, r, d);
                    col.rgb = lerp(col.rgb, half3(0.0, 1.0, 0.0), circle * 0.85);
                    col.a   = max(col.a, circle * 0.85);
                }

                return col;
            }
            ENDHLSL
        }
    }
}
