Shader "Hidden/Universal Render Pipeline/CameraMotionBlur"
{
    HLSLINCLUDE
        #pragma exclude_renderers gles

        #pragma multi_compile _ _USE_DRAW_PROCEDURAL
        #pragma multi_compile _ _CAMERA_ONLY

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D_X(_SourceTex);
        TEXTURE2D(_MotionVectorTexture);
        SAMPLER(sampler_MotionVectorTexture);

#if defined(USING_STEREO_MATRICES)
        float4x4 _PrevViewProjMStereo[2];
#define _PrevViewProjM  _PrevViewProjMStereo[unity_StereoEyeIndex]
#define _ViewProjM unity_MatrixVP
#else
        float4x4 _ViewProjM;
        float4x4 _PrevViewProjM;
#endif
        half _Intensity;
        half _Clamp;
        half4 _SourceSize;

        struct VaryingsCMB
        {
            float4 positionCS    : SV_POSITION;
            float4 uv            : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        VaryingsCMB VertCMB(Attributes input)
        {
            VaryingsCMB output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

#if _USE_DRAW_PROCEDURAL
            GetProceduralQuad(input.vertexID, output.positionCS, output.uv.xy);
#else
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv.xy = input.uv;
#endif
            float4 projPos = output.positionCS * 0.5;
            projPos.xy = projPos.xy + projPos.w;
            output.uv.zw = projPos.xy;

            return output;
        }

        half2 ClampVelocity(half2 velocity, half maxVelocity)
        {
            half len = length(velocity);
            return (len > 0.0) ? min(len, maxVelocity) * (velocity * rcp(len)) : 0.0;
        }

        half2 GetVelocity(float4 uv)
        #if _CAMERA_ONLY
        // Per-pixel camera velocity
        {
            float depth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_PointClamp, uv.xy).r;

        #if UNITY_REVERSED_Z
            depth = 1.0 - depth;
        #endif

            depth = 2.0 * depth - 1.0;

            float3 viewPos = ComputeViewSpacePosition(uv.zw, depth, unity_CameraInvProjection);
            float4 worldPos = float4(mul(unity_CameraToWorld, float4(viewPos, 1.0)).xyz, 1.0);
            float4 prevPos = worldPos;

            float4 prevClipPos = mul(_PrevViewProjM, prevPos);
            float4 curClipPos = mul(_ViewProjM, worldPos);

            half2 prevPosCS = prevClipPos.xy / prevClipPos.w;
            half2 curPosCS = curClipPos.xy / curClipPos.w;

            return ClampVelocity(prevPosCS - curPosCS, _Clamp);
        }
        #else
        {
           return SAMPLE_TEXTURE2D(_MotionVectorTexture, sampler_MotionVectorTexture, uv).rg;
        }
        #endif

        half3 GatherSample(half sampleNumber, half2 velocity, half invSampleCount, float2 centerUV, half randomVal, half velocitySign)
        {
            half  offsetLength = (sampleNumber + 0.5h) + (velocitySign * (randomVal - 0.5h));
            float2 sampleUV = centerUV + (offsetLength * invSampleCount) * velocity * velocitySign;
            return SAMPLE_TEXTURE2D_X(_SourceTex, sampler_PointClamp, sampleUV).xyz;
        }

        half4 DoMotionBlur(VaryingsCMB input, int iterations)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv.xy);
            half2 velocity = GetVelocity(float4(uv, input.uv.zw)) * _Intensity;
            half randomVal = InterleavedGradientNoise(uv * _SourceSize.xy, 0);
            half invSampleCount = rcp(iterations * 2.0);

            half3 color = 0.0;

            UNITY_UNROLL
            for (int i = 0; i < iterations; i++)
            {
                color += GatherSample(i, velocity, invSampleCount, uv, randomVal, -1.0);
                color += GatherSample(i, velocity, invSampleCount, uv, randomVal,  1.0);
            }

            return half4(color * invSampleCount, 1.0);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Motion Blur - Low Quality"

            HLSLPROGRAM
                #pragma vertex VertCMB
                #pragma fragment Frag

                half4 Frag(VaryingsCMB input) : SV_Target
                {
                    return DoMotionBlur(input, 2);
                }

            ENDHLSL
        }

        Pass
        {
            Name "Motion Blur - Medium Quality"

            HLSLPROGRAM
                #pragma vertex VertCMB
                #pragma fragment Frag

                half4 Frag(VaryingsCMB input) : SV_Target
                {
                    return DoMotionBlur(input, 3);
                }
            ENDHLSL
        }

        Pass
        {
            Name "Motion Blur - High Quality"

            HLSLPROGRAM
                #pragma vertex VertCMB
                #pragma fragment Frag

                half4 Frag(VaryingsCMB input) : SV_Target
                {
                    return DoMotionBlur(input, 4);
                }

            ENDHLSL
        }
    }
}
