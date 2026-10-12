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

            // Voxel sun shadows come from the rays; the URP shadow map holds objects only.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "VoxelRaymarchCore.hlsl"

            // Water look - globals pushed by VoxelRenderSettings.
            float4 _VoxelWaterSurface;     // x: pixel noise, y: wave phase (advanced on the CPU), z: wave strength
            float4 _VoxelWaterWind;        // xy: wind direction (normalized, world xz)
            float4 _VoxelWaterReflection;  // x: mode (0 off, 1 sky, 2 screen space), y: strength, z: max distance (world), w: steps
            float4 _VoxelSkyZenith;        // sky colour straight up (reflection fallback)
            float4 _VoxelSkyHorizon;       // sky colour at the horizon
            float4 _VoxelWaterFoam;        // rgb: shore foam colour, a: foam width (water depth in blocks, 0 = off)

            struct TranslucentState
            {
                float3 color;         // premultiplied
                float  transmittance;
            };

            // Texels per block of the terrain textures (the Small Scale texel grid), so water pixels line
            // up with the terrain's.
            float WaterTexelsPerBlock()
            {
                return max(_VoxelTexelCount / max(_VoxelTexVariation.x, 0.01), 1.0);
            }

            // Centre of the texel containing posWS on a face (on the face plane), and the texel's grid cell.
            float3 WaterTexelCenter(int axis, float3 posWS, out int2 cell)
            {
                float texels = WaterTexelsPerBlock();
                cell = (int2)floor(VoxelWorldFaceCoords(axis, posWS) * texels);
                float2 c = ((float2)cell + 0.5) / texels;
                return axis == 0 ? float3(posWS.x, c.y, c.x) : (axis == 1 ? float3(c.x, posWS.y, c.y) : float3(c.x, c.y, posWS.z));
            }

            // Stationary noise in [-1, 1], one random value per grid cell on a water face (cellsPerBlock
            // cells along each block edge). Fades out where a cell gets smaller than a pixel (no shimmer
            // far away). dx / dy: pixel footprint in face coordinates (VoxelFaceFootprint).
            float WaterGridNoise(int axis, float3 posWS, float2 dx, float2 dy, float cellsPerBlock, int seed)
            {
                float cellsPerPixel = max(length(dx), length(dy)) * cellsPerBlock;
                float fade = saturate(2.0 - 1.5 * cellsPerPixel);
                if (fade <= 0.0)
                    return 0.0;

                // The face's own axis offsets the hash so the top and the sides of a block differ.
                int2 cell = (int2)floor(VoxelWorldFaceCoords(axis, posWS) * cellsPerBlock) + axis * 7919 + seed;
                return ((VoxelGrassHash(cell) & 0xFFFFu) / 32767.5 - 1.0) * fade;
            }

            // Pixel noise of the water surface: half per texel (the terrain's texture pixel grid), half
            // per block, so neighbouring blocks differ in tone like terrain blocks do.
            float WaterPixelNoise(int axis, float3 posWS, VoxelRayDiff rayDiff)
            {
                float2 dx, dy;
                VoxelFaceFootprint(rayDiff, axis, posWS, dx, dy);
                return 0.5 * WaterGridNoise(axis, posWS, dx, dy, WaterTexelsPerBlock(), 0)
                     + 0.5 * WaterGridNoise(axis, posWS, dx, dy, 1.0, 104729);
            }

            float2 WaveGradient(float2 q, float2 dir, float freq, float amp, float phase)
            {
                return dir * (amp * freq * cos(dot(dir, q) * freq + phase));
            }

            // Animated ripple normal for horizontal water faces: a few sine waves travelling with the wind
            // (spread around its direction), evaluated once per texture texel so the surface tilts in
            // pixel art facets. n: flat face normal.
            float3 WaterWaveNormal(float3 posWS, float3 n)
            {
                float strength = _VoxelWaterSurface.z;
                if (strength <= 0.0 || abs(n.y) < 0.5)
                    return n;

                float texelsPerBlock = WaterTexelsPerBlock();
                float2 q = (floor(posWS.xz * texelsPerBlock) + 0.5) / texelsPerBlock;
                float t = _VoxelWaterSurface.y;
                float2 w = _VoxelWaterWind.xy;
                float2 side = float2(-w.y, w.x);

                // Phases decrease with time so the crests move downwind.
                float2 g = WaveGradient(q, w, 0.9, 0.5, -t * 1.3)
                         + WaveGradient(q, normalize(w + side * 0.6), 1.7, 0.25, -t * 1.9)
                         + WaveGradient(q, normalize(w - side * 0.8), 3.1, 0.1, -t * 2.7);
                // Tilt in a few discrete steps, like a painted surface: reflections shift in bands.
                g = round(g * 3.0) / 3.0;
                return normalize(float3(-g.x * strength, 1.0, -g.y * strength)) * sign(n.y);
            }

            float3 SkyColor(float3 dirWS, Light light)
            {
                float3 sky = lerp(_VoxelSkyHorizon.rgb, _VoxelSkyZenith.rgb, saturate(dirWS.y * 0.5 + 0.5));
                return lerp(sky, light.color, pow(saturate(dot(dirWS, light.direction)), 16.0) * 0.3);
            }

            float2 ScreenUV(float4 positionCS)
            {
                float4 sp = ComputeScreenPos(positionCS);
                return sp.xy / sp.w;
            }

            // Hash of a texel cell in [0, 1).
            float WaterCellRandom(int2 cell, int seed)
            {
                return (VoxelGrassHash(cell + seed) & 0xFFFFu) / 65536.0;
            }

            float SceneEyeDepth(float2 uv)
            {
                float raw = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).r;
                return LinearEyeDepth(raw, _ZBufferParams);
            }

            // Screen space reflection of the opaque scene: marches the reflected ray against the depth
            // buffer with steps that grow with distance (fine near the surface, coarse far away), then
            // refines the crossing. rgb: reflected colour, a: confidence (0 = missed, use the sky).
            float4 ScreenSpaceReflection(float3 posWS, float3 r, float jitter)
            {
                if (unity_OrthoParams.w > 0.5)
                    return 0;

                int steps = (int)_VoxelWaterReflection.w;
                float maxDist = _VoxelWaterReflection.z;
                float farDepth = _ProjectionParams.z * 0.999;
                float prevT = 0.0;

                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float f = (i + jitter) / steps;
                    float t = maxDist * f * f + 0.05;
                    float4 cs = TransformWorldToHClip(posWS + r * t);
                    if (cs.w <= 0.0)
                        break;
                    float2 uv = ScreenUV(cs);
                    if (any(uv < 0.0) || any(uv > 1.0))
                        break;

                    float scene = SceneEyeDepth(uv);
                    float behind = cs.w - scene;
                    // Behind the depth buffer, but not by more than this step could have skipped:
                    // otherwise the ray passed behind an object and keeps going.
                    if (behind > 0.0 && behind < (t - prevT) * 2.0 + 0.5 && scene < farDepth)
                    {
                        float a = prevT, b = t;
                        [unroll]
                        for (int j = 0; j < 4; j++)
                        {
                            float m = (a + b) * 0.5;
                            float4 mcs = TransformWorldToHClip(posWS + r * m);
                            if (mcs.w > SceneEyeDepth(ScreenUV(mcs))) b = m; else a = m;
                        }
                        uv = ScreenUV(TransformWorldToHClip(posWS + r * b));
                        float2 edge = min(uv, 1.0 - uv);
                        float fade = saturate(min(edge.x, edge.y) * 12.0) * (1.0 - smoothstep(0.7, 1.0, b / maxDist));
                        float3 col = SAMPLE_TEXTURE2D_X_LOD(_CameraOpaqueTexture, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(uv), 0).rgb;
                        return float4(col, fade);
                    }
                    prevT = t;
                }
                return 0;
            }

            // fromAir: the ray enters the water here from the open air (reflections only apply there).
            void AddSurface(inout TranslucentState s, uint id, int axis, float3 posVoxel, float3 posWS, float3 rayDirWS, VoxelRayDiff rayDiff,
                            bool fromAir)
            {
                // Ripples, highlight and reflections are evaluated once per texel, at its centre, so they
                // change in whole texels like the terrain textures.
                int2 cell;
                float3 texelWS = WaterTexelCenter(axis, posWS, cell);
                float3 vt = unity_OrthoParams.w > 0.5 ? -rayDirWS : normalize(GetCameraPositionWS() - texelWS);

                float3 n = VoxelFaceNormal(axis, rayDirWS);
                float3 nw = WaterWaveNormal(posWS, n);
                // Painted block texture (stationary, with the terrain's large scale variation) plus pixel
                // noise: each texel and block slightly brighter / darker and more / less opaque.
                float3 tex = VoxelSampleBlockTexture(id, axis, posWS, rayDiff).rgb;
                float3 texMean = VoxelBlockTextureMean(id);
                float stroke = clamp(Luminance(tex) / max(Luminance(texMean), 1e-3), 0.4, 2.5);   // painted highlights > 1
                float noise = WaterPixelNoise(axis, posWS, rayDiff) * _VoxelWaterSurface.x;
                float3 albedo = tex * _VoxelTranslucentTint.rgb * (1.0 + noise);

                Light light = GetMainLight(TransformWorldToShadowCoord(posWS));
                float atten = light.shadowAttenuation * light.distanceAttenuation;
                if (atten > 0.0)
                    atten *= VoxelSunShadow(posWS, n, light.direction);
                float ndl = saturate(dot(n, light.direction));
                float3 h = normalize(light.direction + vt);
                float spec = pow(saturate(dot(nw, h)), 96.0) * _VoxelSpecular;

                // Painted strokes are more opaque than the dark texels between them.
                float a = saturate(_VoxelSurfaceOpacity * stroke * (1.0 + noise));

                // Shore foam: texels over shallow water (the opaque depth behind the surface, measured
                // vertically) turn to foam, with a dithered pixel edge.
                float foam = 0.0;
                if (fromAir && n.y > 0.5 && _VoxelWaterFoam.a > 0.0 && unity_OrthoParams.w < 0.5)
                {
                    float4 cs = TransformWorldToHClip(texelWS);
                    float behindEye = SceneEyeDepth(ScreenUV(cs)) - cs.w;
                    float depth = behindEye * distance(GetCameraPositionWS(), texelWS) / max(cs.w, 1e-3) * vt.y;
                    // Negative: the texel centre is hidden behind terrain in front of the water.
                    float shallow = behindEye < 0.0 ? 0.0 : 1.0 - depth / _VoxelWaterFoam.a;
                    foam = step(WaterCellRandom(cell, 31337) * 0.85 + 0.1, shallow);
                    albedo = lerp(albedo, _VoxelWaterFoam.rgb * (1.0 + noise * 0.5), foam);
                    a = lerp(a, 0.9, foam);
                    spec *= 1.0 - foam;
                }

                float3 diffuse = albedo * (SampleSH(n) + light.color * ndl * atten);

                // Fresnel reflection: faint looking down, a mirror at grazing angles.
                float3 reflection = 0;
                float fresnel = 0.0;
                if (fromAir && _VoxelWaterReflection.x > 0.5)
                {
                    float nv = saturate(dot(nw, vt));
                    fresnel = (0.02 + 0.98 * pow(1.0 - nv, 5.0)) * _VoxelWaterReflection.y;
                    float3 r = reflect(-vt, nw);
                    if (n.y > 0.5)
                        r.y = abs(r.y);   // a tilted ripple never reflects into the water
                    reflection = SkyColor(r, light);
                    if (_VoxelWaterReflection.x > 1.5 && fresnel > 0.01)
                    {
                        // Step offset per texel, changing every frame so TAA blends the banding away.
                        float jitter = frac((VoxelGrassHash(cell + axis * 7919) & 0xFFFFu) / 65536.0 + (uint)(_Time.y * 60.0) % 64u * 0.618034);
                        float4 ssr = ScreenSpaceReflection(texelWS + n * 0.02, r, jitter);
                        reflection = lerp(reflection, ssr.rgb, ssr.a);
                    }
                    // The painted strokes show on the reflection too, so it stays part of the painting.
                    reflection *= lerp(1.0, stroke, 0.5);
                    fresnel *= 1.0 - foam;
                }

                s.color += s.transmittance * ((1.0 - fresnel) * a * diffuse + fresnel * reflection + light.color * spec * atten);
                s.transmittance *= (1.0 - a) * (1.0 - fresnel);
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
                    bool isUniform = false;
                    uint id = VoxelFetchMarch(c, level, cur.p, isUniform);
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
                        bool fromAir = vis == VOXEL_VIS_TRANSLUCENT && prevVis == VOXEL_VIS_INVISIBLE;
                        AddSurface(s, surfaceId, faceAxis, pos, roWS + rdWS * (cur.t * vs), rdWS, rayDiff, fromAir);
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
                                AddSurface(s, id, cur.axis, o + d * cur.t, roWS + rdWS * (cur.t * vs), rdWS, rayDiff, false);
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
