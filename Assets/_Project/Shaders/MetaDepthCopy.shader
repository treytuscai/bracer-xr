Shader "Hidden/MetaDepthCopy"
{
    // Samples Meta's environment depth texture (left eye, slice 0) and reconstructs
    // a world-space position per pixel. Output is a Vector4 per pixel where:
    //   xyz = world position
    //   w   = raw depth sample for valid pixels, -1 for invalid
    //
    // Math recipe (per Meta's depth API, confirmed by community implementations):
    //   1. Sample rawDepth from _EnvironmentDepthTexture (left eye = slice 0).
    //      The value is in [0, 1], Vulkan NDC convention.
    //   2. Build a clip-space point: (U, V, rawDepth) remapped to [-1, 1].
    //      All three components get the same * 2 - 1 transform.
    //   3. Apply the inverse of Meta's _EnvironmentDepthReprojectionMatrices[0],
    //      which is the depth-frame world->clip matrix (Meta provides world->clip,
    //      we invert to get clip->world). C# computes the inverse and passes it
    //      via _DepthInverseVP because matrix inversion in HLSL is expensive.
    //   4. Perspective divide by w gives the world position.
    //
    // Using Meta's matrix is the key to eliminating swim: the matrix is captured
    // at the moment the depth frame was sampled (not the current render frame),
    // so pose desync between depth and render frame is automatically corrected.
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
            struct v2f     { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            // Meta's depth texture; populated by EnvironmentDepthManager.
            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);

            // Composed inverse(V * P) for the left eye, set per-frame from C#.
            float4x4 _DepthInverseVP;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Sample raw NDC depth from the left-eye slice.
                float rawDepth = UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, float3(i.uv, 0)).r;

                // Reject obviously invalid depth. We use -1 as an out-of-band
                // sentinel in w so we can use the in-band [0, 1] range to carry
                // the raw depth value back to C# for diagnostic logging.
                if (rawDepth <= 0.0 || rawDepth >= 1.0)
                {
                    return float4(0, 0, 0, -1.0);
                }

                // Build clip-space point. All three components are remapped
                // from screen-space [0, 1] to clip-space [-1, 1]: the UV
                // becomes the XY of the clip-space point, and rawDepth (which
                // is in Vulkan [0, 1] NDC) becomes the Z. Without this Z
                // remap the inverse matrix lands every pixel near the camera
                // because rawDepth values like 0.92 sit near the matrix's pole.
                float3 clipXYZ = float3(i.uv, rawDepth) * 2.0 - 1.0;
                float4 clipPos = float4(clipXYZ, 1.0);

                // _DepthInverseVP is the inverse of Meta's
                // _EnvironmentDepthReprojectionMatrices[0] (which is the
                // depth-frame world->clip matrix), composed in C# at blit
                // time. This maps clip -> world directly, with the depth
                // frame's pose baked in (so head motion doesn't swim).
                float4 worldH = mul(_DepthInverseVP, clipPos);
                float3 worldPos = worldH.xyz / worldH.w;

                // w carries the raw depth sample for diagnostic readout.
                // Anything with w >= 0 is a valid pixel.
                return float4(worldPos, rawDepth);
            }
            ENDCG
        }
    }
}
