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
            float4 _SunColor;
            float3 _SunDirection;

            float3 GetWorldPositionFromDepth(float2 uv, float rawDepth)
            {
                // Get linear eye depth (perpendicular distance to camera plane)
                float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                
                // Reconstruct view direction (unnormalized)
                float2 ndc = uv * 2.0 - 1.0;
                
                // FLIP Y for correct orientation
                #if UNITY_UV_STARTS_AT_TOP
                    ndc.y = -ndc.y;
                #endif
                
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
                
                #if UNITY_REVERSED_Z
                    float depth = rawDepth;
                #else
                    float depth = 1.0 - rawDepth;
                #endif
                
                // Check if this is skybox
                float isSkybox = depth <= 0.0001 ? 1.0 : 0.0;
                
                float distance;
                float3 worldPos;
                float3 cameraPos = _WorldSpaceCameraPos;
                
                if (isSkybox > 0.5)
                {
                    // For skybox, calculate view direction from UV
                    distance = _FogEndDistance * 2.0;
                    worldPos = GetWorldPositionFromDepth(input.texcoord, 0.0); // Use 0 depth for far plane
                }
                else
                {
                    // Regular geometry
                    worldPos = GetWorldPositionFromDepth(input.texcoord, depth);
                    
                    // Calculate actual 3D world-space distance
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
                // Use positive viewDir.y 
                float skyAmount = viewDir.y * 0.5 + 0.5; // Map from -1/+1 to 0/1
                
                float3 atmosphereColor = lerp(_FogColor.rgb, _SkyColor.rgb, skyAmount);
                
                // Apply Rayleigh scattering
                atmosphereColor += rayleighColor * _SunColor.rgb;
                
                // Apply sun glow
                float sunInfluence = pow(saturate(dot(viewDir, _SunDirection)), 16.0);
                atmosphereColor = lerp(atmosphereColor, _SunColor.rgb, sunInfluence * 0.3);
                
                // Final fog blend
                float3 finalColor = lerp(sceneColor.rgb, atmosphereColor, totalFogDensity);
                
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}