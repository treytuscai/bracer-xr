// ForearmGrid.shader
// Draws an adjustable grid over the forearm depth-surface mesh and tints each
// cell according to a per-cell RGBA state texture (one texel per cell).
//
// Material properties (driven by RevisedBoundingBoxController via C#):
//   _GridColumns / _GridRows — grid resolution. Larger = smaller squares.
//   _StateTex                — columns x rows RGBA, point-filtered. A > 0.5 = occupied.
//   _ContentAtlas            — optional per-cell image tiles (columns x rows layout in UV space).
//   _DefaultColor            — fill for an empty cell (semi-transparent over skin).
//   _LineColor / _LineThickness — grid line appearance.

Shader "Custom/ForearmGrid"
{
    Properties
    {
        _GridColumns   ("Grid Columns", Float) = 6
        _GridRows      ("Grid Rows",    Float) = 6
        _StateTex      ("Cell State (RGBA)", 2D) = "black" {}
        _ContentAtlas  ("Cell Content Atlas", 2D) = "black" {}

        _DefaultColor  ("Default Cell Color", Color) = (1, 1, 1, 0.15)
        _LineColor     ("Grid Line Color",    Color) = (1, 1, 1, 0.6)
        _LineThickness ("Line Thickness",     Range(0, 0.5)) = 0.04
        _HighlightCell ("Highlight Cell", Vector) = (0, 0, 0, 0)
        _HighlightColor ("Highlight Color", Color) = (0.2, 0.9, 0.35, 0.35)
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

            TEXTURE2D(_StateTex);
            SAMPLER(sampler_StateTex);
            TEXTURE2D(_ContentAtlas);
            SAMPLER(sampler_ContentAtlas);

            CBUFFER_START(UnityPerMaterial)
                float  _GridColumns;
                float  _GridRows;
                half4  _DefaultColor;
                half4  _LineColor;
                float  _LineThickness;
                float4 _HighlightCell;
                half4  _HighlightColor;
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

                float2 grid = float2(max(_GridColumns, 1.0), max(_GridRows, 1.0));

                float2 cell      = floor(input.uv * grid);
                float2 cellLocal = frac(input.uv * grid);

                float2 cellCenter = (cell + 0.5) / grid;
                half4  cellState  = SAMPLE_TEXTURE2D(_StateTex, sampler_StateTex, cellCenter);

                // Content atlas is laid out as a columns x rows tile grid in normalized UV space.
                float2 contentUV = (cell + cellLocal) / grid;
                half4  content   = SAMPLE_TEXTURE2D(_ContentAtlas, sampler_ContentAtlas, contentUV);

                half4 col = _DefaultColor;
                if (cellState.a > 0.5)
                {
                    col = (content.a > 0.01) ? content : cellState;
                }

                if (_HighlightCell.z > 0.5 &&
                    cell.x == _HighlightCell.x && cell.y == _HighlightCell.y)
                {
                    col.rgb = lerp(col.rgb, _HighlightColor.rgb, _HighlightColor.a);
                    col.a   = max(col.a, _HighlightColor.a);
                }

                float2 edgeDist = min(cellLocal, 1.0 - cellLocal);
                float  d        = min(edgeDist.x, edgeDist.y);
                float  aa       = fwidth(d);
                float  lineMask = 1.0 - smoothstep(_LineThickness, _LineThickness + aa, d);

                col.rgb = lerp(col.rgb, _LineColor.rgb, lineMask * _LineColor.a);
                col.a   = max(col.a, lineMask * _LineColor.a);

                return col;
            }
            ENDHLSL
        }
    }
}
