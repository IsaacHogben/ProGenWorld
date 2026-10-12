// Screen space sun shadows for everything on screen. Drawn by VoxelScreenPass (VoxelWorldRenderer)
// right after the depth + normals prepass.
//
// Pass 0 (full, half or quarter resolution): traces the voxel shadow ray of every pixel's surface,
// terrain and objects alike. Output: r = sun visibility, g = texel key (pixelated shadows, -1 for
// anything that is not a voxel face), b = linear eye depth, a = voxel face (VoxelFaceId, -1 objects).
//
// Pass 1 (full resolution): upsamples pass 0 and multiplies in URP's main light shadow map, which
// only holds objects (voxels never render into it). The result is URP's _ScreenSpaceShadowmapTexture:
// URP Lit objects read it in screen space shadow mode and the voxel terrain reads it directly, so
// voxels shadow objects and themselves through the rays only, and objects shadow both through the map.
Shader "Hidden/ProGenWorld/VoxelScreenShadows"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles gles3 glcore

        #define VOXEL_NO_MATERIAL_CBUFFER
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
        ENDHLSL

        Pass
        {
            Name "VoxelScreenShadows"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "../VoxelRaymarchCore.hlsl"
            #include "../VoxelPrepass.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                if (VoxelDepthIsFar(rawDepth))
                    return float4(1, -1, 1e9, -2);

                float3 normalWS; int axis;
                float3 hitWS = VoxelPrepassPosition(uv, rawDepth);
                float eye = LinearEyeDepth(hitWS, UNITY_MATRIX_V);
                float3 sunDir = _MainLightPosition.xyz;

                if (!VoxelPrepassNormal(uv, normalWS, axis))
                {
                    // An object (player, props): voxel shadow ray without ignore distance, so walls
                    // and trees right next to it still shadow it.
                    float3 n = SafeNormalize(SampleSceneNormals(uv));
                    return float4(VoxelSunShadowForObject(hitWS, n, sunDir), -1, eye, -1);
                }

                // The chunk behind the surface. A surface in the air above grass is a blade: blades
                // are lit as if facing up (VoxelGrassLightNormal), so their shadow uses that normal.
                float3 inside = hitWS - normalWS * 0.01;
                int slot = _VoxelChunkWorldSize > 0.0 ? VoxelLookupChunk((int3)floor(inside / _VoxelChunkWorldSize)) : -1;
                VoxelChunk c = (VoxelChunk)0;
                bool blade = false;
                if (slot >= 0)
                {
                    c = _VoxelChunks[slot];
                    int3 q = (int3)floor((inside - c.originSize.xyz) / c.originSize.w);
                    blade = VoxelGrassAir(c, q, VoxelFetchAny(c, 0u, q));
                }
                float3 lightNormal = blade ? float3(0, 1, 0) : normalWS;

                float shadow = VoxelSunShadow(VoxelShadowOrigin(hitWS, lightNormal), lightNormal, sunDir);

                // Relief columns shadowing each other (needs the refined hit of the chunk).
                if (shadow > 0.0 && _VoxelReliefShadows > 0.5 && slot >= 0 && !blade)
                {
                    VoxelHit hit = (VoxelHit)0;
                    hit.axis = axis;
                    VoxelResolveRelief(c, hitWS, normalWS, hit);
                    shadow *= VoxelReliefSunShadow(c, hit, hitWS, normalWS, sunDir);
                }
                // Grass blades shadowing the ground and each other.
                if (shadow > 0.0 && slot >= 0)
                    shadow *= VoxelGrassSunShadow(c, hitWS, sunDir);

                float key = _VoxelShadowPixelated > 0.5 ? VoxelShadowKey(hitWS, normalWS) : -1.0;
                return float4(shadow, key, eye, VoxelFaceId(normalWS));
            }
            ENDHLSL
        }

        Pass
        {
            Name "VoxelShadowResolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "../VoxelRaymarchCore.hlsl"
            #include "../VoxelPrepass.hlsl"

            float _VoxelObjectShadows;   // 1 when URP renders main light shadows (objects only)

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                if (VoxelDepthIsFar(rawDepth))
                    return 1;

                float3 hitWS = VoxelPrepassPosition(uv, rawDepth);
                float3 normalWS; int axis;
                bool voxelFace = VoxelPrepassNormal(uv, normalWS, axis);
                half shadow = VoxelSampleScreenShadow(uv, hitWS, normalWS, voxelFace);

                if (_VoxelObjectShadows > 0.5 && shadow > 0.0)
                    shadow *= MainLightRealtimeShadow(TransformWorldToShadowCoord(hitWS));
                return shadow;
            }
            ENDHLSL
        }
    }
}
