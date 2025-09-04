// Assets/Impostors/Shaders/Hidden_TIBakeNormalAlbedo.shader
Shader "Hidden/TI/BakeNormalAlbedo"
{
    SubShader
    {
        Tags{ "RenderType"="Opaque" }
        Pass
        {
            ZWrite On ZTest LEqual Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            int _OutputMode; // 0 = color (albedo from main tex/color), 1 = world normal

            sampler2D _MainTex; float4 _MainTex_ST;
            float4 _Color;

            struct v2f { float4 pos:SV_POSITION; float3 nrm:TEXCOORD0; float2 uv:TEXCOORD1; };

            v2f vert(appdata_full v){
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.nrm = UnityObjectToWorldNormal(v.normal);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if(_OutputMode==0){
                    float4 albedo = tex2D(_MainTex, i.uv) * _Color;
                    return float4(albedo.rgb, 1);
                } else {
                    float3 n = normalize(i.nrm) * 0.5 + 0.5;
                    return float4(n, 1);
                }
            }
            ENDHLSL
        }
    }
}
