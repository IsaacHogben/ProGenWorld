// Used by LSystemPreviewWindow. Samples the block texture array (uv.z = slice, -1 = untextured)
// and multiplies by a baked per-face shade (uv.w). Untextured blocks, transparent texels and
// the "Show Textures" toggle being off all fall back to the vertex colour (average block colour).
Shader "Hidden/LSystemPreview"
{
    Properties
    {
        _Blocks ("Blocks", 2DArray) = "" {}
        _UseTextures ("Use Textures", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_Blocks);
            float _UseTextures;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float4 uv : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 col = i.color.rgb;
                if (_UseTextures > 0.5 && i.uv.z >= 0)
                {
                    float4 tex = UNITY_SAMPLE_TEX2DARRAY(_Blocks, i.uv.xyz);
                    col = lerp(col, tex.rgb * i.uv.w, tex.a);
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
