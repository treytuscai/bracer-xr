Shader "Custom/ForearmProjection"
{
    Properties
    {
        _MainTex ("UI Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FadeWidth ("Edge Fade Width (m)", Float) = 0.015
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _FadeWidth;

            struct appdata
            {
                float4 vertex   : POSITION;
                float2 uv       : TEXCOORD0;
                float  edgeDist : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float  edgeDist : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos      = UnityObjectToClipPos(v.vertex);
                o.uv       = v.uv;
                o.edgeDist = v.edgeDist;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                if (i.uv.y < 0.0 || i.uv.y > 1.0)
                    discard;

                float2 uv = float2(frac(i.uv.x), i.uv.y);
                fixed4 col = tex2D(_MainTex, uv) * _Color;

                // Per-fragment smoothstep on the interpolated physical distance
                // from the mesh edge. Produces uniform-width fade strips.
                col.a *= smoothstep(0.0, _FadeWidth, i.edgeDist);

                return col;
            }
            ENDCG
        }
    }
}
