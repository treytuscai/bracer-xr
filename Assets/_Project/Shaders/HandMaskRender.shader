// HandMaskRender.shader
// Renders the hand mesh as a solid white silhouette into a grayscale RenderTexture.
// Used by DepthReadback to build a screen-space hand mask each frame before the
// MetaDepthCopy blit. MetaDepthCopy samples this texture and rejects any depth pixel
// covered by the hand, outputting w=-1 so downstream stages treat it as invalid depth.
//
// Rendered via CommandBuffer.DrawRenderer with SetViewProjectionMatrices so the
// silhouette aligns with the current camera's screen space.

Shader "Hidden/HandMaskRender"
{
    SubShader
    {
        // ZTest Always: write every fragment regardless of depth — we want the full
        // silhouette, not just the visible front face.
        // ZWrite Off: output goes to a color RenderTexture, not depth.
        // Cull Off: both front and back faces contribute to the silhouette so the
        // mask covers fingers curled over the forearm from any viewing angle.
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Meta's depth camera VP at capture time — set from C# as depthMatrices[0].
            // Must match the VP used to sample _EnvironmentDepthTexture in MetaDepthCopy
            // since the depth sensor is a physically different camera from Unity's render camera.
            CBUFFER_START(UnityPerMaterial)
                float4x4 _DepthVP;
                // World-space inflation along vertex normals. Expands the silhouette outward
                // to compensate for the 1-2 frame AsyncGPUReadback latency: the depth texture
                // was captured when the hand was slightly behind its current tracked position,
                // so a small margin prevents depth bleed-through during fast hand movement.
                float _InflateAmount;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                // Push each vertex outward along its world-space normal before projecting.
                float3 inflated    = i.positionOS.xyz + i.normalOS * _InflateAmount;
                float4 worldPos    = mul(UNITY_MATRIX_M, float4(inflated, 1.0));
                float4 clipPos     = mul(_DepthVP, worldPos);
                clipPos.y         *= _ProjectionParams.x;
                o.positionCS       = clipPos;
                return o;
            }

            // Output solid white — the mask texture is thresholded at 0.5 in MetaDepthCopy.
            float4 frag(Varyings i) : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
