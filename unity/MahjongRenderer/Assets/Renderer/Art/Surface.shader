Shader "Mahjong/OriginalSurface"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Surface artwork", 2D) = "white" {}
        _Paper ("Artwork underlay", Color) = (1,1,1,0)
        _BaseColor ("Tile base", Color) = (0.1,0.3,0.35,1)
        _Band ("Colored tile base", Float) = 0
        _Gloss ("Satin highlight", Float) = 0.08
        _Unlit ("Unlit labels", Float) = 0
        _Effect ("Surface effect", Float) = 0
        _SrcBlend ("Source blend", Float) = 1
        _DstBlend ("Destination blend", Float) = 0
        _ZWrite ("Depth write", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ MAHJONG_FELT MAHJONG_WOOD MAHJONG_DECAL
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct appdata {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float2 height : TEXCOORD1;
            };
            struct v2f {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                half2 lighting : TEXCOORD1;
                float3 local : TEXCOORD2;
                float2 surfaceUv : TEXCOORD3;
            };
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _Paper, _BaseColor;
            float _Band, _Gloss, _Unlit, _Effect;
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.local = v.vertex.xyz;
                // Dynamic batching transforms positions; the normalized body height must not change.
                o.local.y = v.height.x;
                o.surfaceUv = v.uv;
                o.lighting = half2(1, 0);
                if (_Unlit < 0.5)
                {
                    half3 normal = UnityObjectToWorldNormal(v.normal);
                    half3 light = normalize(half3(-0.35, 1, -0.45));
                    o.lighting.x = 0.68 + 0.32 * saturate(dot(normal, light));
                    if (_Gloss > 0)
                    {
                        float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                        half3 view = normalize(_WorldSpaceCameraPos.xyz - world);
                        half highlight = saturate(dot(normal, normalize(light + view)));
                        half square = highlight * highlight;
                        half fourth = square * square;
                        half eighth = fourth * fourth;
                        half sixteenth = eighth * eighth;
                        o.lighting.y = sixteenth * sixteenth * fourth * _Gloss;
                    }
                }
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 texel = tex2D(_MainTex, i.uv);
                fixed4 color = texel * _Color;
                if (_Paper.a > 0)
                    color = fixed4(lerp(_Paper.rgb, texel.rgb * _Color.rgb, texel.a), _Color.a * _Paper.a);
                if (_Band > 0.5)
                    color.rgb = lerp(_BaseColor.rgb, color.rgb, smoothstep(-0.26, -0.23, i.local.y));
                #if defined(MAHJONG_FELT)
                    float weave = dot(texel.rgb, float3(0.2126, 0.7152, 0.0722));
                    float vignette = 1 - 0.20 * smoothstep(0.1, 0.70, length(i.surfaceUv - 0.5));
                    color.rgb = _Color.rgb * (0.78 + 0.52 * weave) * vignette;
                #elif defined(MAHJONG_WOOD)
                    float grain = sin(i.local.x * 170 + sin(i.local.z * 29) * 3);
                    color.rgb *= 0.96 + 0.04 * grain;
                #elif defined(MAHJONG_DECAL)
                if (_Effect > 0.5 && _Effect < 2.5)
                {
                    float2 p = abs(i.surfaceUv - 0.5) - 0.37;
                    float distance = length(max(p, 0)) + min(max(p.x, p.y), 0) - 0.10;
                    float edge = max(fwidth(distance), 0.001);
                    color.a *= _Effect < 1.5 ? 1 - smoothstep(-0.11, 0.03, distance) :
                        (1 - smoothstep(-edge, edge, distance)) * smoothstep(-0.055 - edge, -0.055 + edge, distance);
                }
                #endif
                color.rgb = color.rgb * i.lighting.x + i.lighting.y;
                return color;
            }
            ENDCG
        }
    }
}
