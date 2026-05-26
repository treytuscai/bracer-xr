Shader "Custom/ForearmProjection"
{
    Properties
    {
        _MainTex   ("UI Texture", 2D)             = "white" {}
        _Color     ("Tint", Color)                = (1,1,1,1)
        _FadeWidth ("Edge Fade Width (m)", Float) = 0.015
        _DepthBias ("Depth Occlusion Bias", Float) = 0.15
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
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Set from material properties
            half4 _Color;
            float _FadeWidth;
            float _DepthBias;

            // Set every frame by ForearmDepthSurface — not in Properties
            float  _FingerRadius;
            float4 _FingerCapsuleA[24];
            float4 _FingerCapsuleB[24];
            int    _FingerCapsuleCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 edgeDist   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS     : SV_POSITION;
                float2 uv             : TEXCOORD0;
                float  edgeDist       : TEXCOORD1;
                META_DEPTH_VERTEX_OUTPUT(2)
                float3 fingerWorldPos : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS     = TransformObjectToHClip(input.positionOS.xyz);
                output.uv             = input.uv;
                output.edgeDist       = input.edgeDist.x;
                output.fingerWorldPos = TransformObjectToWorld(input.positionOS.xyz);

                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.positionOS);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                float2 uv = float2(frac(input.uv.x), input.uv.y);
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _Color;
                col.a *= smoothstep(0.0, _FadeWidth, input.edgeDist);

                // Soft finger occlusion: find the minimum distance from the view ray
                // (camera -> this arm fragment) to each finger capsule axis, then fade
                // col.a based on that distance. Gives a smooth edge that hides size mismatch.
                float3 fragPos  = input.fingerWorldPos;
                float3 camPos   = _WorldSpaceCameraPos;
                float3 rd       = normalize(fragPos - camPos);
                float  fragDist = length(fragPos - camPos);
                float  minDist  = 1e9f;

                for (int fi = 0; fi < _FingerCapsuleCount; fi++)
                {
                    float3 A  = _FingerCapsuleA[fi].xyz;
                    float3 B  = _FingerCapsuleB[fi].xyz;
                    float3 ab = B - A;
                    float3 cv = camPos - A;
                    float  e  = dot(rd, ab);
                    float  h  = dot(ab, ab);
                    float  f  = dot(cv, rd);
                    float  g  = dot(cv, ab);

                    // Closest parameter on the segment, then clamp t to [0, fragDist]
                    float denom = h - e * e;
                    float s = (denom > 1e-6f) ? clamp((g - f * e) / denom, 0.0f, 1.0f) : 0.0f;
                    float t = clamp(s * e - f, 0.0f, fragDist);
                    // Refine s given the clamped ray parameter
                    s = clamp(dot(camPos + t * rd - A, ab) / max(h, 1e-6f), 0.0f, 1.0f);

                    float dist = length((camPos + t * rd) - (A + s * ab));
                    minDist = min(minDist, dist);
                }

                // Fades from fully occluded at the finger centre out to clear at 2x the radius
                col.a *= smoothstep(0.0f, _FingerRadius * 2.0f, minDist);

                // Occlude by any other real geometry in front of the arm surface.
                META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(input, col, _DepthBias);

                return col;
            }
            ENDHLSL
        }
    }
}
