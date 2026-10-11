// Translucent voxels (water, glass, ice...) raymarched from the same voxel data as the
// opaque terrain. Chunks are drawn back to front; within a chunk the ray accumulates
// surface reflections at every translucent boundary and volumetric absorption while it
// travels through translucent voxels, and stops at the first opaque surface.
Shader "ProGenWorld/VoxelRaymarchTranslucent"
{
    Properties
    {
        [NoScaleOffset] _Textures ("Block Textures", 2DArray) = "" {}
        [NoScaleOffset] _AlphaMasks ("Alpha Masks", 2DArray) = "" {}
        // Water look settings live on the VoxelRenderSettings component.
        [HideInInspector][Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector][Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardTranslucent"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite Off
            ZTest [_ZTest]
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore
            #pragma vertex VoxelVert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VoxelRaymarchCore.hlsl"

            struct TranslucentState
            {
                float3 color;         // premultiplied
                float  transmittance;
            };

            void AddSurface(inout TranslucentState s, uint id, int axis, float3 posVoxel, float3 posWS, float3 rayDirWS, VoxelRayDiff rayDiff)
            {
                float3 n = VoxelFaceNormal(axis, rayDirWS);
                float3 albedo = VoxelSampleBlockTexture(id, axis, posWS, rayDiff).rgb * _VoxelTranslucentTint.rgb;

                Light light = GetMainLight(TransformWorldToShadowCoord(posWS));
                float atten = light.shadowAttenuation * light.distanceAttenuation;
                if (atten > 0.0)
                    atten *= VoxelSunShadow(posWS, n, light.direction);
                float3 v = -rayDirWS;
                float ndl = saturate(dot(n, light.direction));
                float3 h = normalize(light.direction + v);
                float spec = pow(saturate(dot(n, h)), 96.0) * _VoxelSpecular;

                float3 diffuse = albedo * (SampleSH(n) + light.color * ndl * atten);
                float a = _VoxelSurfaceOpacity;

                s.color += s.transmittance * (a * diffuse + light.color * spec * atten);
                s.transmittance *= 1.0 - a;
            }

            void Absorb(inout TranslucentState s, float lengthWS, float3 inscatter)
            {
                float tr = exp(-_VoxelAbsorption * lengthWS);
                s.color += s.transmittance * (1.0 - tr) * inscatter;
                s.transmittance *= tr;
            }

            float4 MarchTranslucent(VoxelChunk c, uint level, float3 bmin, float3 bmax, float3 o, float3 d, float tMin, float3 roWS, float3 rdWS, VoxelRayDiff rayDiff)
            {
                TranslucentState s;
                s.color = 0;
                s.transmittance = 1;

                d = VoxelSafeDir(d);
                float3 invD = 1.0 / d;
                float vs = c.originSize.w * VoxelLevelScale(level);

                VoxelCursor cur;
                if (!VoxelBeginRay(bmin, bmax, o, d, invD, tMin, cur))
                    return 0;

                int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
                int majorAxis = VoxelMajorAxis(d);

                Light mainLight = GetMainLight();
                float3 inscatter = _VoxelDeepColor.rgb * (SampleSH(float3(0, 1, 0)) + mainLight.color * 0.25);

                // What was on the other side of the face we entered through?
                bool startedInside = cur.axis < 0;
                uint prevId = 0u;
                if (!startedInside)
                {
                    int3 before = cur.p - stepDir * VoxelAxisMask(cur.axis);
                    prevId = VoxelFetchAny(c, level, before);
                }

                [loop]
                for (int i = 0; i < VOXEL_MAX_STEPS; i++)
                {
                    uint entry = VoxelFetchBrick(c, level, cur.p >> 3);
                    bool isUniform = (entry & VOXEL_BRICK_UNIFORM) != 0u;
                    uint id = isUniform ? (entry & 0xFFu) : VoxelFetchInBrick(entry, cur.p & 7);
                    uint vis = VoxelBlockVisibility(id);
                    if (i == 0 && startedInside)
                        prevId = id;

                    int faceAxis = cur.axis >= 0 ? cur.axis : majorAxis;
                    float3 pos = o + d * cur.t;

                    // Opaque content was already drawn by the opaque pass.
                    if (vis == VOXEL_VIS_OPAQUE || (vis == VOXEL_VIS_STACKED && VoxelMaskPass(id, faceAxis, pos)))
                        break;

                    uint prevVis = VoxelBlockVisibility(prevId);
                    bool prevOpen = prevVis == VOXEL_VIS_INVISIBLE || prevVis == VOXEL_VIS_TRANSLUCENT;
                    bool curOpen = vis == VOXEL_VIS_INVISIBLE || vis == VOXEL_VIS_TRANSLUCENT;
                    if (id != prevId && prevOpen && curOpen && (vis == VOXEL_VIS_TRANSLUCENT || prevVis == VOXEL_VIS_TRANSLUCENT))
                    {
                        uint surfaceId = vis == VOXEL_VIS_TRANSLUCENT ? id : prevId;
                        AddSurface(s, surfaceId, faceAxis, pos, roWS + rdWS * (cur.t * vs), rdWS, rayDiff);
                    }

                    float3 cellMin, cellMax;
                    VoxelStepBounds(cur.p, isUniform && vis != VOXEL_VIS_STACKED, bmin, bmax, cellMin, cellMax);
                    float tEnter = cur.t;
                    VoxelAdvance(cur, o, d, invD, stepDir, cellMin, cellMax);

                    if (vis == VOXEL_VIS_TRANSLUCENT)
                        Absorb(s, (cur.t - tEnter) * vs, inscatter);

                    if (VoxelOutside(cur.p, bmin, bmax))
                    {
                        if (vis == VOXEL_VIS_TRANSLUCENT)
                        {
                            uint nextId = VoxelFetchAny(c, level, cur.p);
                            uint nextVis = VoxelBlockVisibility(nextId);
                            if (nextId != id && (nextVis == VOXEL_VIS_INVISIBLE || nextVis == VOXEL_VIS_TRANSLUCENT))
                                AddSurface(s, id, cur.axis, o + d * cur.t, roWS + rdWS * (cur.t * vs), rdWS, rayDiff);
                        }
                        break;
                    }

                    prevId = id;
                    if (s.transmittance < 0.004)
                        break;
                }

                return float4(s.color, 1.0 - s.transmittance);
            }

            half4 Frag(VoxelVaryings input) : SV_Target
            {
                VoxelChunk c = _VoxelChunks[input.slot];
                VoxelRayDiff rayDiff = VoxelCameraRayDiff(input.positionWS);
                float3 ro, rd; float tMinWS;
                VoxelCameraRay(input.positionWS, ro, rd, tMinWS);

                float vs = c.originSize.w * VoxelLevelScale(input.level);
                float3 o = (ro - c.originSize.xyz) / vs;
                float3 bmin, bmax;
                VoxelLevelBounds(input.cellBounds, input.level, bmin, bmax);
                float4 result = MarchTranslucent(c, input.level, bmin, bmax, o, rd, tMinWS / vs, ro, rd, rayDiff);
                if (result.a <= 0.0)
                    discard;

                // Fog over the premultiplied result.
                float4 positionCS = TransformWorldToHClip(input.positionWS);
                float fogFactor = ComputeFogFactor(positionCS.z);
                result.rgb = MixFogColor(result.rgb, unity_FogColor.rgb * result.a, fogFactor);
                return result;
            }
            ENDHLSL
        }
    }
}
