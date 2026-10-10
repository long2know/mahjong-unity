Shader "Mahjong/OriginalSurface"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Original texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Geometry+10" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float shade : TEXCOORD1; };
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 normal = UnityObjectToWorldNormal(v.normal);
                o.shade = 0.70 + 0.30 * saturate(dot(normal, normalize(float3(-0.3, 1, -0.4))));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv) * _Color;
                color.rgb *= i.shade;
                return color;
            }
            ENDCG
        }
    }
}
