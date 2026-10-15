// Distance based pixel art (VoxelFarLayer.cs).
// Pass 0 (main camera or a band camera, before its opaques): the band behind it as the
//   background, each band pixel a square of screen pixels, writing that band's depth so objects
//   and water in front of it blend and hide correctly. On the main camera optionally with the
//   Sharp filter: flat pixels whose edges blend over one target pixel.
// Pass 1 (main camera): all bands, each on its own grid, the nearest one with something of its own.
// Pass 2 (band camera, after its transparents): its depth as linear eye depth (0 = sky) per pixel.
Shader "Hidden/ProGenWorld/VoxelFarComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        ENDHLSL

        Pass
        {
            Name "Composite"
            ZTest LEqual
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_FarColor);
            TEXTURE2D_FLOAT(_FarDepth);
            float4 _FarTarget;   // xy: screen pixels across the target, zw: screen pixel at the target's bottom left corner
            float4 _FarSource;   // x: source pixel size (screen pixels), y: margin (screen pixels), zw: source size (pixels)
            float4 _FarMode;     // x: 0 nearest, 1 sharp; y: 1 = source supersampled (2x2 texels per pixel)

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return o;
            }

            half3 FarPixel(int2 c)
            {
                c = clamp(c, 0, (int2)_FarSource.zw - 1);
                if (_FarMode.y < 0.5)
                    return LOAD_TEXTURE2D(_FarColor, c).rgb;
                int2 t = c * 2;
                return 0.25 * (LOAD_TEXTURE2D(_FarColor, t).rgb + LOAD_TEXTURE2D(_FarColor, t + int2(1, 0)).rgb
                             + LOAD_TEXTURE2D(_FarColor, t + int2(0, 1)).rgb + LOAD_TEXTURE2D(_FarColor, t + int2(1, 1)).rgb);
            }

            half4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
            {
                float2 s = GetNormalizedScreenSpaceUV(input.positionCS) * _FarTarget.xy + _FarTarget.zw;
                float2 p = (s + _FarSource.y) / _FarSource.x;   // source pixels, continuous
                int2 c = (int2)floor(p);

                half3 color;
                if (_FarMode.x > 0.5)
                {
                    // Sharp: bilinear between pixel centres, squeezed into one target pixel at the edges.
                    float k = _FarSource.x * _ScreenParams.x / _FarTarget.x;   // target pixels per source pixel
                    float2 q = p - 0.5;
                    int2 i0 = (int2)floor(q);
                    float2 f = saturate((q - (float2)i0 - 0.5) * k + 0.5);
                    color = lerp(lerp(FarPixel(i0), FarPixel(i0 + int2(1, 0)), f.x),
                                 lerp(FarPixel(i0 + int2(0, 1)), FarPixel(i0 + int2(1, 1)), f.x), f.y);
                }
                else
                {
                    color = FarPixel(c);
                }

                float eye = LOAD_TEXTURE2D(_FarDepth, clamp(c, 0, (int2)_FarSource.zw - 1)).r;
                depth = eye > 0.0 ? (1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z : UNITY_RAW_FAR_CLIP_VALUE;
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "MainComposite"
            ZTest LEqual
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_FarColor0); TEXTURE2D(_FarColor1); TEXTURE2D(_FarColor2); TEXTURE2D(_FarColor3);
            TEXTURE2D_FLOAT(_FarDepth0); TEXTURE2D_FLOAT(_FarDepth1); TEXTURE2D_FLOAT(_FarDepth2); TEXTURE2D_FLOAT(_FarDepth3);
            float4 _FarTarget;      // xy: screen pixels across the screen, zw: unused
            float4 _FarSources[4];  // per band: x pixel size (screen pixels), y margin, zw size (pixels)
            float4 _FarModes[4];    // per band: x 0 nearest / 1 sharp, y supersampled, z end distance (0 = none)
            float4 _FarOffsets[4];  // per band: xy snap offset (screen pixels, real view minus the band's view)
            int _FarCount;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return o;
            }

            half3 Load(int band, int2 t)
            {
                if (band == 0) return LOAD_TEXTURE2D(_FarColor0, t).rgb;
                if (band == 1) return LOAD_TEXTURE2D(_FarColor1, t).rgb;
                if (band == 2) return LOAD_TEXTURE2D(_FarColor2, t).rgb;
                return LOAD_TEXTURE2D(_FarColor3, t).rgb;
            }

            float LoadDepth(int band, int2 c)
            {
                if (band == 0) return LOAD_TEXTURE2D(_FarDepth0, c).r;
                if (band == 1) return LOAD_TEXTURE2D(_FarDepth1, c).r;
                if (band == 2) return LOAD_TEXTURE2D(_FarDepth2, c).r;
                return LOAD_TEXTURE2D(_FarDepth3, c).r;
            }

            half3 BandPixel(int band, int2 c)
            {
                c = clamp(c, 0, (int2)_FarSources[band].zw - 1);
                if (_FarModes[band].y < 0.5)
                    return Load(band, c);
                int2 t = c * 2;
                return 0.25 * (Load(band, t) + Load(band, t + int2(1, 0)) + Load(band, t + int2(0, 1)) + Load(band, t + int2(1, 1)));
            }

            half3 BandColor(int band, float2 p)
            {
                if (_FarModes[band].x < 0.5)
                    return BandPixel(band, (int2)floor(p));
                // Sharp: bilinear between pixel centres, squeezed into one target pixel at the edges.
                float k = _FarSources[band].x * _ScreenParams.x / _FarTarget.x;   // target pixels per band pixel
                float2 q = p - 0.5;
                int2 i0 = (int2)floor(q);
                float2 f = saturate((q - (float2)i0 - 0.5) * k + 0.5);
                return lerp(lerp(BandPixel(band, i0), BandPixel(band, i0 + int2(1, 0)), f.x),
                            lerp(BandPixel(band, i0 + int2(0, 1)), BandPixel(band, i0 + int2(1, 1)), f.x), f.y);
            }

            half4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
            {
                float2 s = GetNormalizedScreenSpaceUV(input.positionCS) * _FarTarget.xy + _FarTarget.zw;

                // Nearest band with something of its own here (nearer than its end); the last band
                // (sky included) otherwise.
                int band = _FarCount - 1;
                float2 p = 0;
                float eye = 0;
                [loop]
                for (int i = 0; i < _FarCount; i++)
                {
                    float2 pi = (s + _FarOffsets[i].xy + _FarSources[i].y) / _FarSources[i].x;
                    float zi = LoadDepth(i, clamp((int2)floor(pi), 0, (int2)_FarSources[i].zw - 1));
                    if (i == _FarCount - 1 || (zi > 0.0 && zi < _FarModes[i].z))
                    {
                        band = i; p = pi; eye = zi;
                        break;
                    }
                }

                half3 color = BandColor(band, p);
                depth = eye > 0.0 ? (1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z : UNITY_RAW_FAR_CLIP_VALUE;
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthCopy"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float Frag(Varyings input) : SV_Target
            {
                float raw = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0).r;
            #if UNITY_REVERSED_Z
                bool sky = raw <= 0.0;
            #else
                bool sky = raw >= 1.0;
            #endif
                return sky ? 0.0 : LinearEyeDepth(raw, _ZBufferParams);
            }
            ENDHLSL
        }
    }
}
