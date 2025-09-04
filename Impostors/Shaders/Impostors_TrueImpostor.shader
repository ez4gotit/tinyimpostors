Shader "Impostors/TrueImpostor"
{
    Properties{
        _AlbedoTex("Albedo", 2D) = "white" {}
        _NormalTex("World Normal (encoded)", 2D) = "bump" {}
        _DepthRG("Depth Front/Back (RG)", 2D) = "black" {}
        _Near("Near", Float) = 0.0
        _Far ("Far",  Float) = 10.0
        _RaymarchSteps("Raymarch Steps", Int) = 32
        _BinarySteps("Binary Steps", Int) = 4
    }
    SubShader
    {
        Tags{ "Queue"="AlphaTest" "RenderType"="Opaque" }
        Cull Off ZWrite On ZTest LEqual
        Pass
        {
            HLSLPROGRAM
            // Use at least SM4.0 so the driver doesn’t insist on full unroll.
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _AlbedoTex, _NormalTex, _DepthRG;
            float _Near, _Far;
            int _RaymarchSteps, _BinarySteps;

            // Keep conservative upper bounds; the loop will early-break.
            static const int MAX_RAY_STEPS    = 64;
            static const int MAX_BINARY_STEPS = 8;

            struct appdata {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };
            struct v2f {
                float4 pos : SV_POSITION;
                float3 ws  : TEXCOORD0;
                float3 wv  : TEXCOORD1;
                float3x3 TBN : TEXCOORD2;
                float2 uv : TEXCOORD5;
            };

            void BuildBasis(float3 camDir, out float3 U, out float3 V, out float3 W){
                W = normalize(camDir);
                V = float3(0,1,0);
                if (abs(dot(V,W)) > 0.99) V = float3(1,0,0);
                U = normalize(cross(V,W));
                V = normalize(cross(W,U));
            }

            v2f vert (appdata v)
            {
                v2f o;
                float3 ws = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 camPos = _WorldSpaceCameraPos;
                float3 camDir = normalize(camPos - ws);

                float3 U,V,W;
                BuildBasis(camDir, U,V,W);
                o.TBN = float3x3(U,V,W);

                o.ws = ws;
                o.wv = camPos - ws;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float2 SampleFrontBack(float2 uv)
            {
                float4 rg = tex2D(_DepthRG, uv);
                return rg.rg; // R=front, G=back (linear01)
            }

            float Lin01ToMetric(float v) { return lerp(_Near, _Far, v); }

            float4 frag (v2f i) : SV_Target
{
    // Basis from the rotating quad (object faces camera)
    float3 camPos = _WorldSpaceCameraPos;
    float3x3 M = i.TBN;

    // UN-normalized direction from camera to this pixel on the quad
    float3 dirWS = i.ws - camPos;
    float3 dirIS = mul(transpose(M), dirWS); // to impostor space

    // March along +W from Near→Far in metric units
    float wMetric   = _Near;
    float stepLen   = (_Far - _Near) / max(1, _RaymarchSteps);
    float2 uv       = i.uv;
    float2 dUVperW  = dirIS.xy / max(1e-5, dirIS.z); // Δuv per +1 unit of W (metric)
    float2 dUV      = dUVperW * stepLen;

    bool   hit   = false;
    float2 hitUV = 0;

    for (int s = 0; s < MAX_RAY_STEPS; ++s)
    {
        if (s >= _RaymarchSteps) break;

        float2 fb = tex2D(_DepthRG, uv).rg;            // [0..1]
        float frontW = lerp(_Near,_Far, fb.x);          // metric
        float backW  = lerp(_Near,_Far, fb.y);

        if (wMetric >= frontW && wMetric <= backW)
        {
            // refine between previous and current step
            float2 uvA = uv - dUV; float wA = wMetric - stepLen;
            float2 uvB = uv;        float wB = wMetric;

            for (int b = 0; b < MAX_BINARY_STEPS; ++b)
            {
                if (b >= _BinarySteps) break;
                float2 midUV = (uvA + uvB)*0.5;
                float  midW  = (wA + wB)*0.5;

                float2 fbm = tex2D(_DepthRG, midUV).rg;
                float frontM = lerp(_Near,_Far,fbm.x);
                float backM  = lerp(_Near,_Far,fbm.y);
                bool insideM = (midW >= frontM && midW <= backM);

                if (insideM){ uvB = midUV; wB = midW; }
                else        { uvA = midUV; wA = midW; }
            }

            hit   = true;
            hitUV = uvB;   // use the refined UV!
            break;
        }

        wMetric += stepLen;
        uv      += dUV;

        if (any(uv < 0) || any(uv > 1)) break;
    }

    if (!hit) discard;

    float3 albedo = tex2D(_AlbedoTex, hitUV).rgb;
    float3 N = normalize(tex2D(_NormalTex, hitUV).rgb * 2 - 1);

    // simple lighting that always works (fallback dir if built-in var is 0)
    float3 L = normalize(_WorldSpaceLightPos0.xyz + float3(0.4,0.6,0.3));
    float ndl = saturate(dot(N, L));
    float3 col = albedo * (0.25 + 0.75*ndl);

    return float4(col, 1);
}
            ENDHLSL
        }
    }
    FallBack Off
}
