// ForearmSurface.shader
// Semi-transparent forearm overlay with touch feedback.
// Renders a radial highlight at the touch point whose
// color/intensity varies by touch state (hover vs press).
//
// Setup:
//   1. Create this file in Assets/Shaders/
//   2. Create a Material, set its shader to "Custom/ForearmSurface"
//   3. Assign the Material to ArmSurfaceGenerator.forearmMaterial
//
// Uniforms set by VisualFeedbackController each frame:
//   _TouchUV     — UV of the current touch point on the arm mesh
//   _TouchState  — 0 = none, 1 = hover, 2 = press
//   _TouchRadius — radius of the feedback circle in UV space

Shader "Custom/ForearmSurface"
{
    Properties
    {
        // Inspector-tunable defaults
        _BaseColor ("Base Color", Color) = (0.1, 0.1, 0.1, 0.3)
        _HoverColor ("Hover Highlight Color", Color) = (0.3, 0.6, 1.0, 0.5)
        _PressColor ("Press Highlight Color", Color) = (0.2, 0.9, 0.4, 0.8)

        // Set by VisualFeedbackController — don't edit in Inspector
        _TouchUV ("Touch UV", Vector) = (0, 0, 0, 0)
        _TouchState ("Touch State", Float) = 0
        _TouchRadius ("Touch Radius", Float) = 0.05
    }

    SubShader
    {
        // Transparent rendering for passthrough MR overlay
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Properties
            fixed4 _BaseColor;
            fixed4 _HoverColor;
            fixed4 _PressColor;
            float4 _TouchUV;
            float _TouchState;
            float _TouchRadius;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = _BaseColor;

                // No feedback when touch state is none
                if (_TouchState < 0.5)
                    return col;

                // Distance from this fragment to the touch point in UV space.
                // UV wraps around the arm (U axis), so check the shortest
                // path across the seam at U=0/1.
                float2 touchUV = _TouchUV.xy;
                float2 delta = i.uv - touchUV;
                delta.x = min(abs(delta.x), 1.0 - abs(delta.x));
                float dist = length(delta);

                // Smooth falloff from center to edge of feedback radius
                float falloff = 1.0 - smoothstep(0.0, _TouchRadius, dist);

                // Pick highlight color based on state
                fixed4 highlight = _HoverColor;
                if (_TouchState > 1.5)
                    highlight = _PressColor;

                // Blend highlight over base color
                col = lerp(col, highlight, falloff * highlight.a);

                return col;
            }
            ENDCG
        }
    }

    FallBack "Unlit/Transparent"
}
