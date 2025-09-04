Shader "Hidden/TI/BakeLinearDepth"
{
    SubShader
    {
        Tags{ "RenderType"="Opaque" }
        Pass
        {
            ZWrite On  ZTest LEqual  Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Near, _Far;

            struct v2f { float4 pos:SV_POSITION; float3 ws:TEXCOORD0; };
            v2f vert(appdata_full v){
                v2f o; o.pos = UnityObjectToClipPos(v.vertex);
                o.ws = mul(unity_ObjectToWorld, v.vertex).xyz; return o;
            }
            float Linear01(float3 wpos){
                float3 v = mul(UNITY_MATRIX_V, float4(wpos,1)).xyz;
                float z = -v.z;                              // camera-space forward distance
                return saturate((z - _Near)/(_Far - _Near));
            }
            float4 frag(v2f i):SV_Target { return float4(Linear01(i.ws),0,0,1); }
            ENDHLSL
        }
    }
    FallBack Off
}
