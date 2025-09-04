Shader "Hidden/TI/BakeNormalAlbedo"
{
    Properties { _MainTex ("Albedo", 2D) = "white" {}  _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite On  ZTest LEqual  Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            int _OutputMode;                 // 0=albedo, 1=world normal
            sampler2D _MainTex; float4 _MainTex_ST;
            float4 _Color;

            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float3 nrmWS:TEXCOORD1; };

            v2f vert (appdata v){
                v2f o; o.pos=UnityObjectToClipPos(v.vertex);
                o.uv=TRANSFORM_TEX(v.uv,_MainTex);
                o.nrmWS = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag (v2f i):SV_Target
            {
                if (_OutputMode==0){
                    float4 c = tex2D(_MainTex, i.uv) * _Color;
                    c.a = 1.0;                      // solid alpha for mesh pixels
                    return c;
                } else {
                    float3 N = normalize(i.nrmWS)*0.5 + 0.5;
                    return float4(N,1);
                }
            }
            ENDHLSL
        }
    }
    FallBack Off
}
