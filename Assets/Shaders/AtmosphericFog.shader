Shader "Custom/AtmosphericFogURP"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
    }

    SubShader
    {
        Tags 
        { 
            "RenderType"="Opaque" 
            "RenderPipeline"="UniversalPipeline"
        }
        
        Cull Off 
        ZWrite Off 
        ZTest Always

        Pass
        {
            Name "AtmosphericFog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Distance fog
            float _FogStartDistance;
            float _FogEndDistance;
            float _MaxFogDensity;
            
            // Height fog
            float _FogBaseHeight;
            float _FogFalloff;
            float _HeightFogDensity;
            
            // Sky/Fog blend
            float _HorizonHeight; // World-space Y height of horizon transition
            
            // Rayleigh scattering
            float _RayleighIntensity;
            float3 _ScatteringCoeff;
            
            // Colors
            float4 _FogColor;
            float4 _SkyColor;
            float _FogKeepBackground;   // 1 on the near camera of distance based pixel art (VoxelFarLayer.cs)
            float4 _SunColor;
            float3 _SunDirection;

            // Pixel art clouds on the sky (AtmosphericFogController, Clouds): a 2D cloud map over the
            // sky in azimuth / elevation, snapped to square texels of _CloudShape.x degrees, so the
            // clouds are chunky pixels fixed to the sky (they do not crawl when the camera turns).
            // Cumulus columns rise from the horizon with billowy tops; thin streaks float higher.
            // Each texel takes one of four flat palette tones from how much cloud lies between it
            // and the sun (lit tops, blue undersides), with dithered edges.
            float4 _CloudShape;   // x: texel size (degrees, 0 = off), y: clouds around the horizon, z: coverage, w: tower height (degrees)
            float4 _CloudLayer;   // x: base elevation (degrees), y: drift (degrees / second), z: streak coverage, w: streak elevation (degrees)
            float4 _CloudExtra;   // x: streak thickness (degrees), y: horizon haze, z: haze height (degrees), w: edge dither (texels)
            float4 _CloudPuff;    // x: round puff size (degrees), y: roundness (0 lumpy .. 1 round puffs), z: lump size (degrees)
            float4 _CloudLit, _CloudLight, _CloudMid, _CloudShadow;   // tones, lit to shadowed

            float CloudHash(int2 c)
            {
                uint h = (uint)c.x * 73856093u ^ (uint)c.y * 19349663u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return (float)(h >> 8) / 16777216.0;
            }

            // Value noise wrapping every `period` cells along x (the azimuth goes all the way round).
            float CloudNoise(float2 p, int period)
            {
                int2 i = (int2)floor(p);
                float2 f = p - (float2)i;
                float2 u = f * f * (3.0 - 2.0 * f);
                int x0 = ((i.x % period) + period) % period;
                int x1 = (x0 + 1) % period;
                float a = CloudHash(int2(x0, i.y)), b = CloudHash(int2(x1, i.y));
                float c = CloudHash(int2(x0, i.y + 1)), d = CloudHash(int2(x1, i.y + 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float CloudFbm(float2 p, int period)
            {
                float sum = 0.0, amp = 0.5;
                [unroll]
                for (int o = 0; o < 4; o++)
                {
                    sum += CloudNoise(p, period) * amp;
                    p *= 2.0;
                    period *= 2;
                    amp *= 0.5;
                }
                return sum / 0.9375;
            }

            // fbm of p at a frequency around the horizon that still wraps exactly once around the
            // sky (a whole number of noise cells), so there is no seam where the azimuth wraps.
            float CloudFbmAround(float2 p, int period, float2 freq, float yOffset)
            {
                int cells = max((int)round(period * freq.x), 1);
                return CloudFbm(float2(p.x * (cells / (float)period), p.y * freq.y + yOffset), cells);
            }

            // fbm bunches up around 0.5: spread it over 0..1.
            float CloudSpread(float n) { return saturate((n - 0.5) * 2.2 + 0.5); }

            // Lumps (the noise puffs) _CloudPuff.z degrees across, whatever the cloud count: a whole
            // number of noise cells around the sky, the same scale vertically. scale: relative size.
            float CloudLumps(float2 p, int period, float scale, float yOffset)
            {
                int cells = max((int)round(360.0 / max(_CloudPuff.z * scale, 0.5)), 1);
                float f = cells / (float)period;
                return CloudSpread(CloudFbm(float2(p.x * f, p.y * f + yOffset), cells));
            }

            // Overlapping discs on a jittered grid (wrapping every `period` cells along x): 1 at a
            // disc's centre, falling to 0 at its rim. Gives clouds round, cauliflower bumps.
            // offset: where p lies on the front disc (in its radii); lower discs are in front, so
            // every overlap shows the round outline of the disc in front.
            float CloudBubbles(float2 p, int period, int seed, out float2 offset)
            {
                int2 i = (int2)floor(p);
                float best = 0.0;
                float front = -1e9;
                offset = 0;
                [unroll]
                for (int dy = -1; dy <= 1; dy++)
                {
                    [unroll]
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int2 cell = i + int2(dx, dy);
                        int wx = ((cell.x % period) + period) % period;
                        float jx = CloudHash(int2(wx * 3 + seed, cell.y * 5 + 1));
                        float jy = CloudHash(int2(wx * 7 + 2, cell.y * 3 + seed));
                        float jr = CloudHash(int2(wx + 9 * seed, cell.y * 11 + 3));
                        float2 centre = (float2)cell + 0.2 + 0.6 * float2(jx, jy);
                        float radius = 0.5 + 0.4 * jr;
                        float v = saturate(1.0 - length(p - centre) / radius);
                        float z = 0.6 * jr - centre.y;
                        if (v > 0.0 && z > front)
                        {
                            front = z;
                            offset = (p - centre) / radius;
                        }
                        best = max(best, v);
                    }
                }
                return best;
            }

            // Cumulus at cloud map point p (x: around the horizon, one unit per cloud cell; y: the
            // elevation in the same units): round bubbles packed inside a dome rising from a flat
            // base, tallest where the column noise is high, the bubbles bulging out of its top.
            // Returns density - threshold (> 0 inside); t: height in the dome (0 base .. 1 top),
            // lumps: the lumpy (noise) puffs, puffOffset: where p lies on the front round puff (both
            // for the shading). Roundness (_CloudPuff.y) blends lumpy noise puffs (0) into round
            // ones (1), which are _CloudPuff.x degrees across whatever the cloud count.
            float CloudCumulus(float2 p, int period, float baseY, float towerY, out float t, out float lumps, out float2 puffOffset)
            {
                float roundness = saturate(_CloudPuff.y);
                float column = CloudSpread(CloudFbmAround(p, period, float2(0.6, 0.0), 3.7));
                int puffPeriod = max((int)round(360.0 / max(_CloudPuff.x, 0.5)), 1);
                float puffScale = puffPeriod / (float)period;
                float round1 = sqrt(CloudBubbles(p * puffScale, puffPeriod, 1, puffOffset));
                float2 unused;
                float round2 = sqrt(CloudBubbles(p * puffScale * 2.0, puffPeriod * 2, 2, unused));
                lumps = CloudLumps(p, period, 1.0, 11.0);
                float lumps2 = CloudLumps(p, period, 2.5 / 1.6, 11.0);
                float puffHeight = lerp(0.55 + 0.9 * lumps, 0.75 + 0.5 * round1, roundness);
                float height = towerY * (0.2 + 0.8 * column) * puffHeight;
                t = (p.y - baseY) / max(height, 1e-4);
                float envelope = t < 0.0 ? 0.0 : saturate(1.0 - t * t * t);
                float billow = lerp(lumps2, round2, roundness);
                float density = envelope * (0.35 + 0.35 * column + 0.3 * billow);
                return density - (0.75 - 0.5 * saturate(_CloudShape.z));
            }

            // Thin streaks around a higher elevation.
            float CloudStreaks(float2 p, int period, float centreY, float halfY)
            {
                float band = 1.0 - abs(p.y - centreY) / max(halfY, 1e-3);
                float s = CloudSpread(CloudFbmAround(p, period, float2(1.5, 7.0), 40.0));
                return band > 0.0 ? s * band - (1.0 - saturate(_CloudLayer.z)) : -1.0;
            }

            // Cloud colour for a sky direction; a = 1 where there is a cloud.
            float4 PixelClouds(float3 viewDir, float3 sunDir, float3 atmosphere)
            {
                if (_CloudShape.x <= 0.0)
                    return 0;
                const float TAU = 6.2831853;
                float texel = radians(_CloudShape.x);
                int period = max((int)round(_CloudShape.y), 1);
                float unitsPerRad = period / TAU;

                // Square texels of the sky: rows of elevation, azimuth steps widening with it. The
                // wind moves the map in whole texel steps.
                float el = asin(clamp(viewDir.y, -1.0, 1.0));
                float row = floor(el / texel);
                float elC = (row + 0.5) * texel;
                float azStep = texel / max(cos(elC), 0.2);
                float drift = floor(radians(_CloudLayer.y) * _Time.y / azStep) * azStep;
                float az = atan2(viewDir.z, viewDir.x) + drift;
                float col = floor(az / azStep);
                float azC = (col + 0.5) * azStep;
                float2 p = float2(azC, elC) * unitsPerRad;

                float baseY = radians(_CloudLayer.x) * unitsPerRad;
                float towerY = radians(_CloudShape.w) * unitsPerRad;
                float edge = _CloudExtra.w * CloudHash(int2((int)col, (int)row)) * texel * unitsPerRad;

                float t, lumps;
                float2 puffOffset;
                float d = CloudCumulus(p, period, baseY, towerY, t, lumps, puffOffset);
                bool cumulus = d > edge;
                bool streak = !cumulus && CloudStreaks(p, period, radians(_CloudLayer.w) * unitsPerRad,
                                                       radians(_CloudExtra.x) * 0.5 * unitsPerRad) > edge * 0.5;
                if (!cumulus && !streak)
                    return 0;

                // Tone, brighter higher up in the cloud, towards the sun (mostly above, leaning to
                // the side the sun is on): lumpy puffs darker where a lump bulges between the texel
                // and the sun, round puffs shaded like spheres; blended by the roundness.
                int tone = 1;
                if (cumulus)
                {
                    float sunAz = atan2(sunDir.z, sunDir.x) + drift;
                    float2 toSun = normalize(float2(clamp(sin(sunAz - azC), -0.7, 0.7), 1.0));
                    float2 q = p + toSun * (2.0 * texel * unitsPerRad);
                    float lumpsSun = CloudLumps(q, period, 1.0, 11.0);
                    float lumpy = 0.25 + 0.75 * saturate(t) + (lumps - lumpsSun) * 2.5;
                    float nz = sqrt(saturate(1.0 - dot(puffOffset, puffOffset)));
                    float roundLight = 0.2 + 0.3 * saturate(t) + 0.55 * (0.75 * dot(puffOffset, toSun) + 0.66 * nz);
                    float light = lerp(lumpy, roundLight, saturate(_CloudPuff.y));
                    tone = 3 - (int)floor(clamp(light, 0.0, 0.999) * 4.0);
                }

                float3 c = tone == 0 ? _CloudLit.rgb : (tone == 1 ? _CloudLight.rgb : (tone == 2 ? _CloudMid.rgb : _CloudShadow.rgb));
                // Darker at night, the sun's colour on the lit tones.
                float day = saturate(sunDir.y * 4.0 + 0.3);
                c *= lerp(0.2, 1.0, day);
                float3 sunTint = _SunColor.rgb / max(max(_SunColor.r, _SunColor.g), max(_SunColor.b, 1e-3));
                c *= lerp(float3(1, 1, 1), sunTint, tone <= 1 ? 0.15 : 0.05);
                // Low clouds fade into the horizon haze, completely towards their base (seen from
                // high up, the base would otherwise end in a straight line above the haze).
                float hazeH = max(_CloudExtra.z, 1e-3);
                float haze = _CloudExtra.y * (1.0 - saturate(degrees(elC) / hazeH));
                float aboveBase = saturate((degrees(elC) - _CloudLayer.x) / (hazeH * 0.5));
                haze = max(haze, (1.0 - aboveBase) * (1.0 - aboveBase));
                c = lerp(c, atmosphere, saturate(haze));
                return float4(c, 1.0);
            }

            float3 GetWorldPositionFromDepth(float2 uv, float rawDepth)
            {
                // Get linear eye depth (perpendicular distance to camera plane)
                float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                
                // Reconstruct view direction (unnormalized)
                float2 ndc = uv * 2.0 - 1.0;
                
                // Flip BOTH X and Y for correct orientation
                ndc.x = -ndc.x;
                ndc.y = -ndc.y;
                
                float3 viewDir = mul(unity_CameraInvProjection, float4(ndc, 1.0, 1.0)).xyz;
                
                // Scale view direction by linear depth
                float3 viewPos = (viewDir / viewDir.z) * linearDepth;
                
                // Transform to world space
                float3 worldPos = mul(unity_CameraToWorld, float4(viewPos, 1.0)).xyz;
                
                return worldPos;
            }

            float CalculateDistanceFog(float distance)
            {
                float fogAmount = saturate((distance - _FogStartDistance) / (_FogEndDistance - _FogStartDistance));
                fogAmount = pow(fogAmount, 2.0);
                return fogAmount * _MaxFogDensity;
            }

            float CalculateHeightFog(float3 worldPos, float3 cameraPos)
            {
                float relativeHeight = worldPos.y - _FogBaseHeight;
                float cameraRelativeHeight = cameraPos.y - _FogBaseHeight;
                float avgHeight = (relativeHeight + cameraRelativeHeight) * 0.5;
                float heightDensity = exp(-max(0, avgHeight) * _FogFalloff);
                
                return heightDensity * _HeightFogDensity;
            }

            float3 CalculateRayleighScattering(float3 viewDir, float distance)
            {
                float cosTheta = dot(viewDir, _SunDirection);
                float phase = 0.75 * (1.0 + cosTheta * cosTheta);
                float3 scattering = _ScatteringCoeff * phase;
                float scatterAmount = 1.0 - exp(-distance * 0.001);
                
                return scattering * scatterAmount * _RayleighIntensity;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Sample scene color
                half4 sceneColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);
                
                // Sample depth
                float rawDepth = SampleSceneDepth(input.texcoord);
                
                // Skybox = nothing drawn before the far plane. Compared in linear depth: a fixed
                // raw depth threshold (the old "depth <= 0.0001") also caught distant terrain,
                // because reversed Z raw depth falls to ~0.0001 well before the far plane.
                float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float isSkybox = linearDepth >= _ProjectionParams.z * 0.9999 ? 1.0 : 0.0;
                
                float distance;
                float3 worldPos;
                float3 cameraPos = _WorldSpaceCameraPos;
                
                // Distance based pixel art: the background is the far camera's image, already fogged.
                if (isSkybox > 0.5 && _FogKeepBackground > 0.5)
                    return half4(sceneColor.rgb, 1.0);

                if (isSkybox > 0.5)
                {
                    // For skybox, calculate view direction from UV
                    distance = _FogEndDistance * 2.0;
                    worldPos = GetWorldPositionFromDepth(input.texcoord, UNITY_RAW_FAR_CLIP_VALUE);
                }
                else
                {
                    // Regular geometry
                    worldPos = GetWorldPositionFromDepth(input.texcoord, rawDepth);
                    
                    // Calculate actual 3D world-space distance (not just depth)
                    distance = length(worldPos - cameraPos);
                }
                
                // Calculate view direction from world position (works for both skybox and geometry)
                float3 viewDir = normalize(worldPos - cameraPos);
                
                // Calculate fog components
                float distanceFog = CalculateDistanceFog(distance);
                float heightFog = CalculateHeightFog(worldPos, _WorldSpaceCameraPos);
                
                // Combine fog densities
                float totalFogDensity = saturate(distanceFog + heightFog);
                
                // For skybox, always use full fog density
                if (isSkybox > 0.5)
                {
                    totalFogDensity = 1.0;
                }
                
                // Calculate Rayleigh scattering
                float3 rayleighColor = CalculateRayleighScattering(viewDir, distance);
                
                // Blend based on view direction Y
                float skyAmount = viewDir.y * 0.5 + 0.5;
                
                float3 atmosphereColor = lerp(_FogColor.rgb, _SkyColor.rgb, skyAmount);
                
                // Apply Rayleigh scattering
                atmosphereColor += rayleighColor * _SunColor.rgb;
                
                // Apply sun glow
                float sunInfluence = pow(saturate(dot(viewDir, _SunDirection)), 16.0);
                atmosphereColor = lerp(atmosphereColor, _SunColor.rgb, sunInfluence * 0.3);
                
                // Pixel art clouds on the sky.
                if (isSkybox > 0.5)
                {
                    float4 clouds = PixelClouds(viewDir, _SunDirection, atmosphereColor);
                    atmosphereColor = lerp(atmosphereColor, clouds.rgb, clouds.a);
                }

                // Final fog blend
                float3 finalColor = lerp(sceneColor.rgb, atmosphereColor, totalFogDensity);
                
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}