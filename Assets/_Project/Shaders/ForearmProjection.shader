// ForearmProjection.shader
// URP transparent shader for the forearm display surface mesh.
//
// Input channels (set by MeshGenerator and ForearmDepthSurface.UpdateUnityMesh):
//   POSITION   — local-space vertex position (world hit transformed by WorldToLocal)
//   TEXCOORD0  — UV0: display coordinates (linear projection + pronation scroll + orientation rotation)
//
// Per-material properties (set from C# each frame):
//   _MainTex     — UI texture to display on the arm surface (set in Inspector)
//   _Color       — tint multiplied against the texture (white = no tint)
//   _TouchPoint  — set by ForearmInteraction.LateUpdate; drives the debug circle overlay
//   _TouchRadius — radius of the debug circle in UV space (Inspector)

Shader "Custom/ForearmProjection"
{
    Properties
    {
        _MainTex    ("UI Texture", 2D)                          = "white" {}
        _Color      ("Tint", Color)                             = (1,1,1,1)
        // Set by ForearmInteraction each LateUpdate: xy = touch UV, z = active (1/0), w = unused.
        _TouchPoint ("Touch Debug Point (uv.xy, active, _)", Vector) = (0,0,0,0)
        _TouchRadius("Touch Debug Radius (UV)", Float)          = 0.03
    }

    SubShader
    {
        // Transparent queue: renders after opaque geometry so alpha blending composites
        // correctly over the environment.
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        // Standard premultiplied-alpha blending.
        Blend SrcAlpha OneMinusSrcAlpha
        // No depth writes: transparent surfaces shouldn't occlude geometry behind them.
        ZWrite Off
        // Cull back faces: the surface is a thin shell wrapping the top of the arm, so the
        // underside is rarely seen.
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Enables GPU instancing support; required for XR multi-pass rendering.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // All per-material scalar/vector uniforms must be inside UnityPerMaterial CBUFFER
            // for URP's SRP Batcher. Without this block the draw call opts out of batching
            // and Unity uploads uniforms individually each frame — measurable cost on Quest.
            CBUFFER_START(UnityPerMaterial)
                half4  _Color;
                float4 _TouchPoint;
                float  _TouchRadius;
            CBUFFER_END

            // Vertex input: positions and two UV channels from the MeshBuffer NativeArrays.
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0; // UV0: display coordinates (see MeshGenerator.CalculateUV)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                // Required for Quest stereo rendering. Without this macro pair the mesh
                // renders correctly in only one eye.
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

                // Discard fragments whose UV falls outside [0,1]. This happens at the arm
                // patch boundary where pronation scroll or landscape offset pushes surface
                // vertices beyond the display region. Discarding prevents the texture from
                // being sampled with clamped/wrapped UV and showing a stretched edge.
                if (input.uv.x < 0.0 || input.uv.x > 1.0 ||
                    input.uv.y < 0.0 || input.uv.y > 1.0)
                    discard;

                float2 uv  = input.uv;
                half4  col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _Color;

                // Debug overlay: draw a filled green circle at the active touch UV.
                // _TouchPoint.z is the active flag set by ForearmInteraction each LateUpdate.
                if (_TouchPoint.z > 0.5)
                {
                    float2 diff   = uv - _TouchPoint.xy;
                    float  d      = length(diff);
                    float  r      = _TouchRadius;
                    // smoothstep produces a soft antialiased edge over a 0.005 UV-unit band.
                    float  circle = 1.0 - smoothstep(r - 0.005, r, d);
                    col.rgb = lerp(col.rgb, half3(0.0, 1.0, 0.0), circle * 0.85);
                    // max preserves circle visibility even where surface alpha is low (mesh edge).
                    col.a   = max(col.a, circle * 0.85);
                }

                return col;
            }
            ENDHLSL
        }
    }
}