// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

// Renders the hand mesh as a solid white silhouette into a grayscale RenderTexture.
// Used by DepthReadback to build a depth-frame-space hand mask each dispatch. The temporal
// median's extract pass (DepthTemporalMedian pass 0) is the sole consumer: it carves the
// finger out of depth history and reconstructs the lifted bleed ring around it, so the
// hand arrives invalid in the stabilized depth that downstream stages consume.
//
// Rendered via CommandBuffer.DrawMesh with Meta's depth VP (_DepthVP = depthMatrices[0])
// so the silhouette aligns with the depth texture's UV space, not Unity's camera space.
// The depth sensor is physically different from Unity's render camera, using Unity's VP
// would produce a fixed spatial offset in the mask. _ProjectionParams.x corrects the
// Vulkan render target Y flip when using a custom projection matrix.

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
            // Must be the depth frame's own VP (not Unity's camera VP) since the depth sensor
            // is a physically different camera from Unity's render camera.
            CBUFFER_START(UnityPerMaterial)
                float4x4 _DepthVP;
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
            };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float4 worldPos    = mul(UNITY_MATRIX_M, i.positionOS);
                float4 clipPos     = mul(_DepthVP, worldPos);
                clipPos.y         *= _ProjectionParams.x;
                o.positionCS       = clipPos;
                return o;
            }

            // Output solid white — the mask texture is thresholded at 0.5 in DepthTemporalMedian.
            float4 frag(Varyings i) : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
