Shader "Hidden/TI/BakeDepthBack"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        // Back-most depth: accept fragments BEHIND the front pass and keep the deepest.
        // Write ONLY G channel.
        Pass
        {
            ZWrite On
            ZTest Greater
            Cull Off
            ColorMask G
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Near, _Far;

            struct v2f{ float4 pos:SV_POSITION; float3 wpos:TEXCOORD0; };

            v2f vert(appdata_full v){
                v2f o;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos  = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float Linear01Depth(float3 wpos){
                float3 cpos = mul(UNITY_MATRIX_V, float4(wpos,1)).xyz;
                float z = -cpos.z;
                return saturate( (z - _Near) / (_Far - _Near) );
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = Linear01Depth(i.wpos);
                return float4(0,d,0,1); // G = back
            }
            ENDHLSL
        }
    }
}
