// HandDepthMask.shader
// Depth pre-pass that occludes the forearm surface display wherever the hand is in front.
//
// TECHNIQUE — depth-only render
// The forearm surface mesh (ForearmProjection.shader) is transparent (Queue=3000) and
// does not write depth. Without intervention it renders on top of the hand regardless
// of whether the hand is in front or behind the arm.
//
// This shader renders the hand mesh geometry at Queue=Geometry+1 (2001), which fires
// before all transparent objects. It writes the hand's depth values into the Z-buffer
// (ZWrite On) but writes nothing to the color buffer (ColorMask 0), so the hand
// itself remains invisible.
//
// When the arm surface draws at Queue=3000, ZTest LEqual compares each fragment against
// the already-written hand depth. Arm surface fragments behind the hand fail and are
// discarded, producing correct occlusion with zero overdraw cost on the color buffer.
//
// This is separate from the CPU-side HandMask (which prevents hand-contaminated depth
// cells from entering the surface reconstruction). HandMask fixes the mesh; this shader
// fixes the visual occlusion of the final rendered result.

Shader "Custom/HandDepthMask"
{
    SubShader
    {
        // Geometry+1 (2001) ensures this runs after opaque scene geometry but before
        // all transparent objects (3000+), so depth is populated when the arm surface draws.
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }
        // Core purpose: write hand geometry into the depth buffer.
        ZWrite On
        // Standard depth test; handles hand self-occlusion correctly.
        ZTest LEqual
        // Write nothing to color — the hand should remain invisible.
        ColorMask 0
        // Both faces written to depth: ensures the underside of the hand also occludes
        // the arm surface when the hand is partially curled over the forearm.
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Position only — no UVs, normals, or color needed for a depth-only pass.
            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // Required for Quest stereo: without this macro pair the depth write
                // only affects one eye's depth buffer.
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            // ColorMask 0 means this output is never written. The return value is a
            // required placeholder — the depth write happens implicitly from positionCS.
            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
