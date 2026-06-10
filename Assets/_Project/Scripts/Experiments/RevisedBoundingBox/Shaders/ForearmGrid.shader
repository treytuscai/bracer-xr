// ForearmGrid.shader
// Draws an adjustable grid over the forearm depth-surface mesh and tints each
// cell according to a per-cell RGBA state texture (one texel per cell).
//
// Material properties (driven by RevisedBoundingBoxController via C#):
//   _GridColumns / _GridRows — grid resolution. Larger = smaller squares.
//   _StateTex                — columns x rows RGBA, point-filtered. A > 0.5 = occupied.
//   _ContentAtlas            — sparse packed tile atlas (only occupied cells allocate space).
//   _AtlasRectTex            — per-cell normalized atlas UV rect (offset.xy, size.zw).
//   _TransformTex            — per-cell scale (R) and rotation (G), decoded with _MaxContentScale.
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
        _AtlasRectTex  ("Cell Atlas Rect (UV)", 2D) = "black" {}
        _TransformTex  ("Cell Transform (RG)", 2D) = "black" {}
        _MaxContentScale ("Max Content Scale", Float) = 4

        _DefaultColor  ("Default Cell Color", Color) = (1, 1, 1, 0.15)
        _LineColor     ("Grid Line Color",    Color) = (1, 1, 1, 0.6)
        _LineThickness ("Line Thickness",     Range(0, 0.5)) = 0.04
        _HighlightCell ("Highlight Cell", Vector) = (0, 0, 0, 0)
        _HighlightColor ("Highlight Color", Color) = (0.2, 0.9, 0.35, 0.35)
        _EditSelectionCell ("Edit Selection Cell", Vector) = (0, 0, 0, 0)
        _EditTintColor ("Edit Tint Color", Color) = (0.2, 0.95, 0.35, 0.35)
        _ContentAlphaCutoff ("Content Alpha Cutoff", Range(0, 0.25)) = 0.04
        _ContentAlphaSoftness ("Content Alpha Softness", Range(0.02, 0.3)) = 0.14
        _PreviewCell ("Placement Preview Cell", Vector) = (0, 0, 0, 0)
        _PreviewTransform ("Placement Preview Transform (RG)", Vector) = (0, 0, 0, 0)
        _PreviewAlpha ("Placement Preview Alpha", Range(0, 1)) = 0.45
        _PreviewAtlas ("Placement Preview Atlas", 2D) = "black" {}
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
            TEXTURE2D(_AtlasRectTex);
            SAMPLER(sampler_AtlasRectTex);
            TEXTURE2D(_TransformTex);
            SAMPLER(sampler_TransformTex);
            TEXTURE2D(_PreviewAtlas);
            SAMPLER(sampler_PreviewAtlas);

            CBUFFER_START(UnityPerMaterial)
                float  _GridColumns;
                float  _GridRows;
                float  _MaxContentScale;
                half4  _DefaultColor;
                half4  _LineColor;
                float  _LineThickness;
                float4 _HighlightCell;
                half4  _HighlightColor;
                float4 _EditSelectionCell;
                half4  _EditTintColor;
                float  _ContentAlphaCutoff;
                float  _ContentAlphaSoftness;
                float4 _PreviewCell;
                float4 _PreviewTransform;
                float  _PreviewAlpha;
            CBUFFER_END

            half SoftContentAlpha(half a)
            {
                if (a <= _ContentAlphaCutoff)
                    return 0.0;

                half softness = max(_ContentAlphaSoftness, 0.02);
                half edge1 = _ContentAlphaCutoff + softness;
                if (a >= edge1)
                    return a;

                return a * smoothstep(_ContentAlphaCutoff, edge1, a);
            }

            // Baked atlas pixels are already feathered; only discard fringe alpha at sample time.
            half4 ApplyContentAlpha(half4 tex)
            {
                if (tex.a <= _ContentAlphaCutoff)
                    tex.a = 0.0;
                return tex;
            }

            half GetEditSelectionContentAlpha(float2 grid, float2 meshUV)
            {
                if (_EditSelectionCell.z <= 0.5)
                    return 0.0;

                int2 coord = int2(_EditSelectionCell.xy);
                float2 cellCenter = (float2(coord) + 0.5) / grid;
                half4 cellState = SAMPLE_TEXTURE2D(_StateTex, sampler_StateTex, cellCenter);
                if (cellState.a <= 0.5)
                    return 0.0;

                half4 xf = SAMPLE_TEXTURE2D(_TransformTex, sampler_TransformTex, cellCenter);
                float scale = max(xf.r * _MaxContentScale, 0.01);
                float rotRad = xf.g * 6.2831853;

                float2 editCellLocal = meshUV * grid - float2(coord);
                float2 p = editCellLocal - 0.5;
                float cosR = cos(-rotRad);
                float sinR = sin(-rotRad);
                float2 srcLocal = float2(cosR * p.x - sinR * p.y, sinR * p.x + cosR * p.y) / scale + 0.5;

                if (srcLocal.x < 0.0 || srcLocal.x > 1.0 || srcLocal.y < 0.0 || srcLocal.y > 1.0)
                    return 0.0;

                half4 atlasRect = SAMPLE_TEXTURE2D(_AtlasRectTex, sampler_AtlasRectTex, cellCenter);
                if (atlasRect.z <= 0.0001 || atlasRect.w <= 0.0001)
                    return 0.0;

                float2 atlasUV = atlasRect.xy + srcLocal * atlasRect.zw;
                half4 content = SAMPLE_TEXTURE2D(_ContentAtlas, sampler_ContentAtlas, atlasUV);
                return (content.a <= _ContentAlphaCutoff) ? 0.0 : content.a;
            }

            half4 SampleTransformedCellContent(float2 grid, float2 meshUV, int2 cellCoord)
            {
                float2 cellCenter = (float2(cellCoord) + 0.5) / grid;
                half4 cellState = SAMPLE_TEXTURE2D(_StateTex, sampler_StateTex, cellCenter);
                if (cellState.a <= 0.5)
                    return half4(0, 0, 0, 0);

                half4 xf = SAMPLE_TEXTURE2D(_TransformTex, sampler_TransformTex, cellCenter);
                float scale = max(xf.r * _MaxContentScale, 0.01);
                float rotRad = xf.g * 6.2831853;

                float2 cellLocal = meshUV * grid - float2(cellCoord);
                float2 p = cellLocal - 0.5;
                float cosR = cos(-rotRad);
                float sinR = sin(-rotRad);
                float2 srcLocal = float2(cosR * p.x - sinR * p.y, sinR * p.x + cosR * p.y) / scale + 0.5;

                // Stay inside this cell's atlas tile — overflow onto the mesh is handled by the
                // neighbor composite loop, not by sampling adjacent tiles in the atlas (which
                // looks like every other image rotating with the selection).
                if (srcLocal.x < 0.0 || srcLocal.x > 1.0 || srcLocal.y < 0.0 || srcLocal.y > 1.0)
                    return half4(0, 0, 0, 0);

                half4 atlasRect = SAMPLE_TEXTURE2D(_AtlasRectTex, sampler_AtlasRectTex, cellCenter);
                if (atlasRect.z <= 0.0001 || atlasRect.w <= 0.0001)
                    return half4(0, 0, 0, 0);

                float2 atlasUV = atlasRect.xy + srcLocal * atlasRect.zw;
                return ApplyContentAlpha(
                    SAMPLE_TEXTURE2D(_ContentAtlas, sampler_ContentAtlas, atlasUV));
            }

            // Must cover ceil(maxScale * 0.5) cells around each fragment so large placements
            // (e.g. 5x) are not clipped to a 3x3 block.
            #define MAX_COMPOSITE_RADIUS 6

            int CompositeSearchRadius()
            {
                return min((int)ceil(_MaxContentScale * 0.5), MAX_COMPOSITE_RADIUS);
            }

            half4 CompositeCellContent(float2 grid, float2 meshUV, float2 primaryCell)
            {
                half4 composite = half4(0, 0, 0, 0);
                int2 primary = int2(primaryCell);
                int radius = CompositeSearchRadius();

                [loop]
                for (int dy = -MAX_COMPOSITE_RADIUS; dy <= MAX_COMPOSITE_RADIUS; dy++)
                {
                    if (dy < -radius || dy > radius)
                        continue;

                    [loop]
                    for (int dx = -MAX_COMPOSITE_RADIUS; dx <= MAX_COMPOSITE_RADIUS; dx++)
                    {
                        if (dx < -radius || dx > radius)
                            continue;

                        int2 cellCoord = primary + int2(dx, dy);
                        if (cellCoord.x < 0 || cellCoord.y < 0 ||
                            cellCoord.x >= (int)grid.x || cellCoord.y >= (int)grid.y)
                            continue;

                        half4 tex = SampleTransformedCellContent(grid, meshUV, cellCoord);
                        if (tex.a <= 0.01)
                            continue;

                        composite.rgb = tex.rgb * tex.a + composite.rgb * (1.0 - tex.a);
                        composite.a   = tex.a + composite.a * (1.0 - tex.a);
                    }
                }

                return composite;
            }

            half4 SamplePreviewCellContent(float2 grid, float2 meshUV, int2 previewCoord)
            {
                if (_PreviewCell.z <= 0.5)
                    return half4(0, 0, 0, 0);
                if (previewCoord.x != (int)_PreviewCell.x || previewCoord.y != (int)_PreviewCell.y)
                    return half4(0, 0, 0, 0);

                float scale = max(_PreviewTransform.x * _MaxContentScale, 0.01);
                float rotRad = _PreviewTransform.y * 6.2831853;

                float2 cellLocal = meshUV * grid - float2(previewCoord);
                float2 p = cellLocal - 0.5;
                float cosR = cos(-rotRad);
                float sinR = sin(-rotRad);
                float2 srcLocal = float2(cosR * p.x - sinR * p.y, sinR * p.x + cosR * p.y) / scale + 0.5;

                if (srcLocal.x < 0.0 || srcLocal.x > 1.0 || srcLocal.y < 0.0 || srcLocal.y > 1.0)
                    return half4(0, 0, 0, 0);

                half4 tex = ApplyContentAlpha(
                    SAMPLE_TEXTURE2D(_PreviewAtlas, sampler_PreviewAtlas, srcLocal));
                tex.a *= _PreviewAlpha;
                return tex;
            }

            half4 CompositePreviewContent(float2 grid, float2 meshUV)
            {
                if (_PreviewCell.z <= 0.5)
                    return half4(0, 0, 0, 0);

                return SamplePreviewCellContent(grid, meshUV, int2(_PreviewCell.xy));
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
                half4  content    = CompositeCellContent(grid, input.uv, cell);
                half4  preview    = CompositePreviewContent(grid, input.uv);

                half4 col = _DefaultColor;
                if (content.a > 0.01)
                {
                    col.rgb = content.rgb * content.a + _DefaultColor.rgb * (1.0 - content.a);
                    col.a   = max(_DefaultColor.a, content.a);
                }
                else if (cellState.a > 0.5 && dot(cellState.rgb, cellState.rgb) > 0.0001)
                {
                    col = cellState;
                }

                if (preview.a > 0.01)
                {
                    col.rgb = preview.rgb * preview.a + col.rgb * (1.0 - preview.a);
                    col.a   = max(col.a, preview.a);
                }

                if (_HighlightCell.z > 0.5 &&
                    cell.x == _HighlightCell.x && cell.y == _HighlightCell.y)
                {
                    col.rgb = lerp(col.rgb, _HighlightColor.rgb, _HighlightColor.a);
                    col.a   = max(col.a, _HighlightColor.a);
                }

                half editMask = GetEditSelectionContentAlpha(grid, input.uv);
                if (editMask > 0.01)
                {
                    col.rgb = lerp(col.rgb, _EditTintColor.rgb, _EditTintColor.a * editMask);
                    col.a   = max(col.a, editMask * _EditTintColor.a);
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
