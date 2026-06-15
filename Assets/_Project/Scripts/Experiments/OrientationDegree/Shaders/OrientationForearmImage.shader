// OrientationForearmImage.shader
// ForearmImageDisplay with native aspect ratio and in-plane rotation for OrientationDegree.
// _ImageScale sets total stamp height in mesh UV space; width = height * aspect.

Shader "Custom/OrientationForearmImage"
{
    Properties
    {
        _MainTex         ("Image (RGBA)", 2D)    = "white" {}
        _Tint            ("Tint",  Color)        = (1, 1, 1, 1)
        _ImageScale      ("Image Height (UV)",  Float)  = 0.6
        _ImageAspect     ("Image Aspect (width / height)", Float) = 1.0
        _ImageOffsetU    ("Image Offset U", Float) = 0.0
        _ImageOffsetV    ("Image Offset V", Float) = 0.0
        _ImageRotation   ("Image Rotation (rad)", Float) = 0.0
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
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4  _Tint;
                float  _ImageScale;
                float  _ImageAspect;
                float  _ImageOffsetU;
                float  _ImageOffsetV;
                float  _ImageRotation;
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

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (input.uv.x < 0.0 || input.uv.x > 1.0 ||
                    input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                float halfH   = max(_ImageScale * 0.5, 0.0001);
                float aspect  = max(_ImageAspect, 0.0001);
                float halfW   = halfH * aspect;
                float2 center = float2(0.5 + _ImageOffsetU, 0.5 + _ImageOffsetV);

                float2 p = input.uv - center;

                float s, c;
                sincos(_ImageRotation, s, c);
                float2 rotated = float2(c * p.x - s * p.y, s * p.x + c * p.y);

                float2 local = float2(rotated.x / (2.0 * halfW), rotated.y / (2.0 * halfH));
                if (local.x < -0.5 || local.x > 0.5 || local.y < -0.5 || local.y > 0.5)
                    discard;

                float2 imageUV = local + 0.5;
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, imageUV) * _Tint;
                return col;
            }
            ENDHLSL
        }
    }
}
