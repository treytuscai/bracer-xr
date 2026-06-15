// OneHForearmUvImage.shader — 1H experiment only.
// Stamps the source texture at a mesh UV center at native aspect; _ImageSize scales uniformly.

Shader "Custom/OneHForearmUvImage"
{
    Properties
    {
        _MainTex       ("Image (RGBA)", 2D) = "white" {}
        _Tint          ("Tint", Color) = (1, 1, 1, 1)
        _ImageCenterUV ("Image Center (mesh UV)", Vector) = (0.25, 0.5, 0, 0)
        _ImageSize     ("Image Half Height (UV)", Float) = 0.08
        _ImageAspect   ("Image Aspect (width / height)", Float) = 0.57
        _ImageRotation ("Image Rotation (turns)", Float) = 0
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

            CBUFFER_START(UnityPerMaterial)
                half4  _Tint;
                float4 _ImageCenterUV;
                float  _ImageSize;
                float  _ImageAspect;
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

            float2 RotateAroundOrigin(float2 p, float rotRad)
            {
                float cosR = cos(-rotRad);
                float sinR = sin(-rotRad);
                return float2(cosR * p.x - sinR * p.y, sinR * p.x + cosR * p.y);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (input.uv.x < 0.0 || input.uv.x > 1.0 ||
                    input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                float halfH = max(_ImageSize, 0.0001);
                float aspect = max(_ImageAspect, 0.0001);
                float halfW = halfH * aspect;

                // Offset from anchor, then rotate into image axes before aspect sizing.
                float2 p = input.uv - _ImageCenterUV.xy;
                float rotRad = _ImageRotation * 6.2831853;
                p = RotateAroundOrigin(p, rotRad);

                float2 local = float2(p.x / halfW, p.y / halfH);
                if (local.x < -0.5 || local.x > 0.5 || local.y < -0.5 || local.y > 0.5)
                    discard;

                float2 imageUV = local + 0.5;
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, imageUV) * _Tint;
            }
            ENDHLSL
        }
    }
}
