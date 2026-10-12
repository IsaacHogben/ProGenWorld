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
            // No main light shadow keywords: the sun shadow comes from the resolved screen space
            // shadows (voxel rays + object shadow map), see VoxelScreenShadows.shader.
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "VoxelLighting.hlsl"
            #include "VoxelPrepass.hlsl"

            half4 Frag(VoxelVaryings input, out float depth : VOXEL_DEPTH_SEMANTIC) : SV_Target
            {
                depth = input.positionCS.z;
                VoxelRayDiff rayDiff = VoxelCameraRayDiff(input.positionWS);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                VoxelChunk chunk = _VoxelChunks[input.slot];

                // The prepass already traced this pixel: rebuild its hit instead of marching again.
                float3 bmin, bmax;
                VoxelLevelBounds(input.cellBounds, input.level, bmin, bmax);
                float rawDepth = SampleSceneDepth(screenUV);
                VoxelHit hit; float3 hitWS, normalWS, rayDirWS;
                int resolved = VoxelResolvePrepass(chunk, input.level, bmin, bmax, screenUV, rawDepth, hit, hitWS, normalWS);
                if (resolved == VOXEL_RESOLVE_OTHER)
                    discard;

                float4 positionCS;
                if (resolved == VOXEL_RESOLVE_HIT)
                {
                    rayDirWS = rayDiff.rd;
                    positionCS = TransformWorldToHClip(hitWS);
                    depth = VoxelConservativeDepth(rawDepth, input.positionCS.z);
                }
                else
                {
                    if (!VoxelTraceCamera(input, hit, hitWS, normalWS, rayDirWS))
                        discard;
                    positionCS = TransformWorldToHClip(hitWS);
                    depth = VoxelConservativeDepth(positionCS.z / positionCS.w, input.positionCS.z);
                }

                half4 albedo = VoxelSampleAlbedo(hit, hitWS, rayDiff);
                albedo.rgb *= VoxelGrassFarShade(hit.id, hitWS, normalWS);

                // Grass blades are thin cards: lit like the ground they grow from, from both sides.
                float3 lightNormalWS = hit.grass != 0u ? float3(0, 1, 0) : normalWS;

                InputData inputData = (InputData)0;
                inputData.positionWS = hitWS;
                inputData.positionCS = positionCS;
                inputData.normalWS = lightNormalWS;
                inputData.viewDirectionWS = -rayDirWS;
                inputData.fogCoord = ComputeFogFactor(positionCS.z);
                inputData.bakedGI = SampleSH(lightNormalWS);
                inputData.normalizedScreenSpaceUV = screenUV;
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.alpha = 1;
                surface.metallic = 0;
                surface.smoothness = hit.grass != 0u ? 0.0 : _VoxelSmoothness;   // no grazing sheen on blades
                surface.occlusion = (hit.refined != 0u ? hit.occlusion : 1)
                    * VoxelCornerOcclusion(chunk, input.level, hit, rayDirWS);
                surface.normalTS = half3(0, 0, 1);

                // Sun shadow: voxel rays (terrain on terrain) times the object shadow map, resolved
                // per pixel before the opaques. Without it, trace the voxel rays here (no objects).
                half voxelShadow = _VoxelScreenShadows > 0.5
                    ? LOAD_TEXTURE2D(_ScreenSpaceShadowmapTexture, (uint2)input.positionCS.xy).r
                    : VoxelTraceSurfaceShadow(chunk, hit, hitWS, normalWS, _MainLightPosition.xyz);

                half4 color = VoxelFragmentPBR(inputData, surface, voxelShadow);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1;
                return color;
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
