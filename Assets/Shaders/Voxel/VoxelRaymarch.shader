// Opaque + alpha masked voxel terrain, raymarched directly from the chunk voxel data.
// Drawn by VoxelWorldRenderer with Graphics.RenderPrimitives (one box per draw cell).
Shader "ProGenWorld/VoxelRaymarch"
{
    Properties
    {
        [NoScaleOffset] _Textures ("Block Textures", 2DArray) = "" {}
        [NoScaleOffset] _AlphaMasks ("Alpha Masks", 2DArray) = "" {}
        // Look settings (smoothness, texture variation, shadows) live on the VoxelRenderSettings component.

        [HideInInspector][Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector][Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles gles3 glcore
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VoxelRaymarchCore.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma vertex VoxelVert
            #pragma fragment Frag

            #pragma multi_compile_local _ _VOXEL_INSIDE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VoxelLighting.hlsl"

            half4 Frag(VoxelVaryings input, out float depth : VOXEL_DEPTH_SEMANTIC) : SV_Target
            {
                depth = input.positionCS.z;
                VoxelHit hit; float3 hitWS, normalWS, rayDirWS;
                if (!VoxelTraceCamera(input, hit, hitWS, normalWS, rayDirWS))
                    discard;

                float4 positionCS = TransformWorldToHClip(hitWS);
                depth = VoxelConservativeDepth(positionCS.z / positionCS.w, input.positionCS.z);

                half4 albedo = VoxelSampleAlbedo(hit, hitWS);

                InputData inputData = (InputData)0;
                inputData.positionWS = hitWS;
                inputData.positionCS = positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = -rayDirWS;
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = ComputeScreenPos(positionCS);
                #else
                    inputData.shadowCoord = TransformWorldToShadowCoord(hitWS);
                #endif
                inputData.fogCoord = ComputeFogFactor(positionCS.z);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.alpha = 1;
                surface.metallic = 0;
                surface.smoothness = _VoxelSmoothness;
                surface.occlusion = 1;
                surface.normalTS = half3(0, 0, 1);

                // Voxel-on-voxel sun shadows are traced per pixel through the chunk grid.
                float3 sunDir = _MainLightPosition.xyz;
                half voxelShadow = VoxelSunShadow(hitWS, normalWS, sunDir);

                half4 color = VoxelFragmentPBR(inputData, surface, voxelShadow);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float3 ShadowLightDir(float3 positionWS)
            {
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                return normalize(_LightPosition - positionWS);
            #else
                return _LightDirection;
            #endif
            }

            float4 ShadowClip(float3 positionWS, float3 normalWS)
            {
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, ShadowLightDir(positionWS)));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            VoxelVaryings ShadowVert(VoxelAttributes input)
            {
                VoxelVaryings o;
                float3 n;
                VoxelBoxVertex(input.vertexID, input.instanceID, o.slot, o.cellBounds, o.level, o.positionWS, n);
                o.positionCS = ShadowClip(o.positionWS, n);
                return o;
            }

            // Conservative depth: the hit is never closer to the light than the rasterized box face,
            // so early depth testing still rejects boxes hidden behind already drawn casters.
            #if UNITY_REVERSED_Z
                #define VOXEL_SHADOW_DEPTH SV_DepthLessEqual
            #else
                #define VOXEL_SHADOW_DEPTH SV_DepthGreaterEqual
            #endif

            half ShadowFrag(VoxelVaryings input, out float depth : VOXEL_SHADOW_DEPTH) : SV_Target
            {
                depth = input.positionCS.z;
                VoxelChunk c = _VoxelChunks[input.slot];
                float vs = c.originSize.w * VoxelLevelScale(input.level);

            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 ro = _LightPosition;
                float3 rd = normalize(input.positionWS - _LightPosition);
            #else
                float3 rd = -_LightDirection;
                float3 ro = input.positionWS - rd * vs;
            #endif

                float3 bmin, bmax;
                VoxelLevelBounds(input.cellBounds, input.level, bmin, bmax);
                VoxelHit hit;
                float3 o = (ro - c.originSize.xyz) / vs;
                if (!VoxelMarchOpaque(c, input.level, bmin, bmax, o, rd, 0.0, hit))
                    discard;

                // Push the caster away from the light by the shadow ignore distance (at least
                // _VoxelShadowMapBias blocks). Voxel surfaces then never shadow themselves in the
                // map (acne) and nearby occluders are ignored the same way the voxel rays ignore
                // them; voxel-on-voxel shadows come from the per-pixel rays.
                float push = max(_VoxelShadowIgnoreDistance, _VoxelShadowMapBias);
                float3 hitWS = ro + rd * (hit.t * vs + push);
                float4 positionCS = ShadowClip(hitWS, VoxelFaceNormal(hit.axis, rd));
                depth = VoxelConservativeDepth(positionCS.z / positionCS.w, input.positionCS.z);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull [_Cull]
            ZWrite On
            ZTest [_ZTest]
            ColorMask R

            HLSLPROGRAM
            #pragma vertex VoxelVert
            #pragma fragment DepthFrag
            #pragma multi_compile_local _ _VOXEL_INSIDE

            half DepthFrag(VoxelVaryings input, out float depth : VOXEL_DEPTH_SEMANTIC) : SV_Target
            {
                depth = input.positionCS.z;
                VoxelHit hit; float3 hitWS, normalWS, rayDirWS;
                if (!VoxelTraceCamera(input, hit, hitWS, normalWS, rayDirWS))
                    discard;

                depth = VoxelConservativeDepth(VoxelClipDepth(hitWS), input.positionCS.z);
                return depth;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull [_Cull]
            ZWrite On
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma vertex VoxelVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_local _ _VOXEL_INSIDE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _WRITE_RENDERING_LAYERS

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            void DepthNormalsFrag(VoxelVaryings input,
                out half4 outNormalWS : SV_Target0,
            #if defined(_WRITE_RENDERING_LAYERS)
                out uint outRenderingLayers : SV_Target1,
            #endif
                out float depth : VOXEL_DEPTH_SEMANTIC)
            {
                depth = input.positionCS.z;
                outNormalWS = 0;
            #if defined(_WRITE_RENDERING_LAYERS)
                outRenderingLayers = 0;
            #endif
                VoxelHit hit; float3 hitWS, normalWS, rayDirWS;
                if (!VoxelTraceCamera(input, hit, hitWS, normalWS, rayDirWS))
                    discard;

                depth = VoxelConservativeDepth(VoxelClipDepth(hitWS), input.positionCS.z);

            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
            #else
                outNormalWS = half4(normalWS, 0.0);
            #endif

            #if defined(_WRITE_RENDERING_LAYERS)
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }
}
