Shader "Hidden/MetaDepthCopy"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            // Meta's global depth texture (often a Texture2DArray in VR)
            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // Sample the left eye (slice 0) depth
                float depth = UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, float3(i.uv, 0)).r;
                // Output raw depth in meters
                return float4(depth, 0, 0, 1); 
            }
            ENDCG
        }
    }
}