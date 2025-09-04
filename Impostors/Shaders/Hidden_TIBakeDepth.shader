// Assets/Impostors/Shaders/Hidden_TIBakeDepth.shader
Shader "Hidden/TI/BakeDepth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite On ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Near, _Far;
            float _CullSign; // +1 for front, -1 for back

            struct v2f{ float4 pos:SV_POSITION; float3 ws:NORMAL; float3 wpos:TEXCOORD0; };

            v2f vert(appdata_full v){
                v2f o;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float Linear01Depth(float3 wpos){
                float3 cpos = mul(UNITY_MATRIX_V, float4(wpos,1)).xyz;
                float z = -cpos.z; // camera forward is -z in view
                return saturate((z - _Near) / (_Far - _Near));
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = Linear01Depth(i.wpos);

                // Front/back via face sign (requires Cull Off)
                float front = step(0, _CullSign); // pass selects which to write
                float back  = step(_CullSign, 0);
                return float4(d*front, d*back, 0, 1);
            }
            ENDHLSL
        }
    }
}
