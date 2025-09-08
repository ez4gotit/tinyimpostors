Shader "Impostors/OctaImpostor"
{
    Properties{
        _AtlasColor ("Atlas Color", 2D) = "white" {}
        _AtlasNormal("Atlas Normal", 2D) = "bump"  {}
        _AtlasDepth ("Atlas Depth", 2D) = "black" {}
        _Tiles     ("Tiles", Int) = 8
        _TileRes   ("TileRes", Int) = 256
        _TilePad   ("TilePad", Int) = 2
        _HalfU     ("HalfU", Float) = 1
        _HalfV     ("HalfV", Float) = 1
        _Cutoff    ("Alpha Cutoff", Range(0,1)) = 0.05
        _OpacityBoost("Opacity Boost", Range(0,4)) = 2.0
        _DepthBG   ("Depth BG threshold (near 1 = farther)", Range(0.95, 1.0)) = 0.9995
    }

    // -------------------- URP --------------------
    SubShader
    {
        Tags{ "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        ZWrite On
        Cull Off
        AlphaToMask Off
        Blend One Zero

        Pass
        {
            Tags{ "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_AtlasColor);  SAMPLER(sampler_AtlasColor);
            TEXTURE2D(_AtlasNormal); SAMPLER(sampler_AtlasNormal);
            TEXTURE2D(_AtlasDepth);  SAMPLER(sampler_AtlasDepth);

            int _Tiles, _TileRes, _TilePad; float _HalfU, _HalfV, _Cutoff, _OpacityBoost, _DepthBG;

            struct Attributes{ float3 positionOS:POSITION; };
            struct Varyings{ float4 positionHCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 centerWS:TEXCOORD1; };

            Varyings vert(Attributes v){
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS);
                o.positionWS = ws;
                o.positionHCS = TransformWorldToHClip(ws);
                o.centerWS = TransformObjectToWorld(float3(0,0,0));
                return o;
            }

            float2 OctaEncode(float3 n){
                n/= (abs(n.x)+abs(n.y)+abs(n.z));
                float2 e=n.xy; if(n.z<0) e=(1-abs(e.yx))*sign(e.xy);
                return e*0.5+0.5;
            }
            void BuildBasis(float3 dir, out float3 U, out float3 V){
                float3 up = (abs(dir.y)>0.99)? float3(1,0,0) : float3(0,1,0);
                U = normalize(cross(up, dir)); V = normalize(cross(dir, U));
            }
            float2 SafeUV(int2 tile, float2 local01){
                int size=_Tiles*_TileRes + (_Tiles+1)*_TilePad;
                float2 px0=float2(tile.x*_TileRes+(tile.x+1)*_TilePad,
                                  tile.y*_TileRes+(tile.y+1)*_TilePad);
                float2 inner=local01*(_TileRes-1)+0.5;
                return (px0+inner)/size;
            }
            struct Sample{ float4 c; float3 n; float z; float a; };

            Sample SampleTile(int2 t, float2 l)
            {
                t = clamp(t, int2(0,0), int2(_Tiles-1,_Tiles-1));
                float2 uv = SafeUV(t,l);
                Sample s;
                s.c = SAMPLE_TEXTURE2D(_AtlasColor, sampler_AtlasColor, uv);
                s.n = normalize(SAMPLE_TEXTURE2D(_AtlasNormal, sampler_AtlasNormal, uv).rgb*2-1);
                s.z = SAMPLE_TEXTURE2D(_AtlasDepth, sampler_AtlasDepth, uv).r;

                // robust background: 1 inside, 0 for far bg (~1.0)
                float eps = max(fwidth(s.z)*4.0, 1.0/max(1.0,(_TileRes-1)));
                s.a = 1.0 - smoothstep(_DepthBG, _DepthBG + eps, s.z);
                return s;
            }

            float4 frag(Varyings i):SV_Target
            {
                float3 center=i.centerWS;

                // choose view slice in OBJECT space (follows object rotation)
                float3 viewWS = normalize(GetCameraPositionWS() - center);
                float3 viewOS = TransformWorldToObjectDir(viewWS);
                float2 oct = OctaEncode(normalize(viewOS));
                float2 g=oct*_Tiles; int2 t00=(int2)floor(g); float2 f=frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

                // ray -> plane (no ghosting)
                float3 U,V; BuildBasis(viewWS,U,V);
                float3 camWS = GetCameraPositionWS();
                float3 pixDir = normalize(i.positionWS - camWS);
                float denom = dot(viewWS, pixDir); if (abs(denom)<1e-4) discard;
                float t = dot(viewWS, (center - camWS)) / denom;
                float3 hit = camWS + pixDir * t;

                float2 plane = float2(dot(hit-center,U), dot(hit-center,V));
                float2 local01 = float2(plane.x/max(1e-5,2.0*_HalfU),
                                        plane.y/max(1e-5,2.0*_HalfV)) + 0.5;
                if(any(local01<0)||any(local01>1)) discard;

                Sample s00=SampleTile(t00,local01), s10=SampleTile(t10,local01);
                Sample s01=SampleTile(t01,local01), s11=SampleTile(t11,local01);
                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;

                float a00=s00.a, a10=s10.a, a01=s01.a, a11=s11.a;
                float W=(w00*a00+w10*a10+w01*a01+w11*a11)*_OpacityBoost;
                clip(W - _Cutoff);

                float3 col=(w00*a00*s00.c.rgb+w10*a10*s10.c.rgb+
                            w01*a01*s01.c.rgb+w11*a11*s11.c.rgb)/max(W,1e-4);
                float3 N=normalize(w00*a00*s00.n+w10*a10*s10.n+w01*a01*s01.n+w11*a11*s11.n);

                Light L = GetMainLight();
                float3 ambient = SampleSH(N);
                float  ndl = saturate(dot(N,-L.direction));
                float3 direct = L.color * ndl;

                return float4(col*(ambient+direct), 1);
            }
            ENDHLSL
        }
    }

    // -------------------- Built-in --------------------
    SubShader
    {
        Tags{ "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        ZWrite On
        Cull Off
        AlphaToMask Off
        Blend One Zero

        Pass
        {
            Tags{ "LightMode"="ForwardBase" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            sampler2D _AtlasColor,_AtlasNormal,_AtlasDepth;
            int _Tiles,_TileRes,_TilePad; float _HalfU,_HalfV,_Cutoff,_OpacityBoost,_DepthBG;

            struct appdata{ float4 vertex:POSITION; };
            struct v2f{ float4 pos:SV_POSITION; float3 ws:TEXCOORD0; float3 centerWS:TEXCOORD1; };

            v2f vert(appdata v){
                v2f o;
                float4 w = mul(unity_ObjectToWorld, v.vertex);
                o.ws = w.xyz; o.pos = UnityObjectToClipPos(v.vertex);
                o.centerWS = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                return o;
            }

            float2 OctaEncode(float3 n){
                n/= (abs(n.x)+abs(n.y)+abs(n.z));
                float2 e=n.xy; if(n.z<0) e=(1-abs(e.yx))*sign(e.xy); return e*0.5+0.5;
            }
            void Basis(float3 d, out float3 U, out float3 V){
                float3 up=(abs(d.y)>0.99)?float3(1,0,0):float3(0,1,0);
                U=normalize(cross(up,d)); V=normalize(cross(d,U));
            }
            float2 SafeUV(int2 t,float2 l){
                int size=_Tiles*_TileRes+(_Tiles+1)*_TilePad;
                float2 px0=float2(t.x*_TileRes+(t.x+1)*_TilePad, t.y*_TileRes+(t.y+1)*_TilePad);
                float2 inner=l*(_TileRes-1)+0.5; return (px0+inner)/size;
            }

            float4 frag(v2f i):SV_Target
            {
                float3 center=i.centerWS;
                float3 viewWS=normalize(_WorldSpaceCameraPos - center);
                float3 viewOS=mul((float3x3)unity_WorldToObject, viewWS);
                float2 oct=OctaEncode(normalize(viewOS));
                float2 g=oct*_Tiles; int2 t00=(int2)floor(g); float2 f=frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

                float3 U,V; Basis(viewWS,U,V);
                float3 cam=_WorldSpaceCameraPos;
                float3 pixDir=normalize(i.ws - cam);
                float denom=dot(viewWS,pixDir); if(abs(denom)<1e-4) discard;
                float t=dot(viewWS,(center-cam))/denom;
                float3 hit=cam + pixDir*t;

                float2 plane=float2(dot(hit-center,U), dot(hit-center,V));
                float2 local01=float2(plane.x/max(1e-5,2.0*_HalfU), plane.y/max(1e-5,2.0*_HalfV))+0.5;
                if(any(local01<0)||any(local01>1)) discard;

                float2 uv00=SafeUV(t00,local01), uv10=SafeUV(t10,local01);
                float2 uv01=SafeUV(t01,local01), uv11=SafeUV(t11,local01);
                float4 c00=tex2D(_AtlasColor,uv00), c10=tex2D(_AtlasColor,uv10);
                float4 c01=tex2D(_AtlasColor,uv01), c11=tex2D(_AtlasColor,uv11);
                float3 n00=normalize(tex2D(_AtlasNormal,uv00).rgb*2-1);
                float3 n10=normalize(tex2D(_AtlasNormal,uv10).rgb*2-1);
                float3 n01=normalize(tex2D(_AtlasNormal,uv01).rgb*2-1);
                float3 n11=normalize(tex2D(_AtlasNormal,uv11).rgb*2-1);
                float  z00=tex2D(_AtlasDepth,uv00).r, z10=tex2D(_AtlasDepth,uv10).r;
                float  z01=tex2D(_AtlasDepth,uv01).r, z11=tex2D(_AtlasDepth,uv11).r;

                float eps00 = max(fwidth(z00)*4.0, 1.0/max(1.0,(_TileRes-1)));
                float eps10 = max(fwidth(z10)*4.0, 1.0/max(1.0,(_TileRes-1)));
                float eps01 = max(fwidth(z01)*4.0, 1.0/max(1.0,(_TileRes-1)));
                float eps11 = max(fwidth(z11)*4.0, 1.0/max(1.0,(_TileRes-1)));
                float a00 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps00, z00);
                float a10 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps10, z10);
                float a01 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps01, z01);
                float a11 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps11, z11);

                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;
                float W=(w00*a00+w10*a10+w01*a01+w11*a11)*_OpacityBoost;
                clip(W - _Cutoff);

                float3 col=(w00*a00*c00.rgb+w10*a10*c10.rgb+w01*a01*c01.rgb+w11*a11*c11.rgb)/max(W,1e-4);
                float3 N=normalize(w00*a00*n00+w10*a10*n10+w01*a01*n01+w11*a11*n11);

                float3 ambient=ShadeSH9(float4(N,1));
                float3 L=normalize(_WorldSpaceLightPos0.xyz);
                float ndl=saturate(dot(N,L));
                float3 direct=_LightColor0.rgb*ndl;
                return float4(col*(ambient+direct), 1);
            }
            ENDHLSL
        }
    }

    // -------------------- HDRP (unlit cutout) --------------------
    SubShader
    {
        Tags{ "RenderPipeline"="HDRenderPipeline" "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        ZWrite On
        Cull Off
        AlphaToMask Off
        Blend One Zero

        Pass
        {
            Tags{ "LightMode"="Forward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ShaderVariables.hlsl"

            sampler2D _AtlasColor,_AtlasNormal,_AtlasDepth;
            int _Tiles,_TileRes,_TilePad; float _HalfU,_HalfV,_Cutoff,_OpacityBoost,_DepthBG;

            struct appdata{ float3 positionOS:POSITION; };
            struct v2f{ float4 pos:SV_POSITION; float3 ws:TEXCOORD0; float3 centerWS:TEXCOORD1; };

            v2f vert(appdata v){
                v2f o;
                float3 ws = mul(GetObjectToWorldMatrix(), float4(v.positionOS,1)).xyz;
                o.ws = ws; o.centerWS = mul(GetObjectToWorldMatrix(), float4(0,0,0,1)).xyz;
                o.pos = mul(UNITY_MATRIX_VP, float4(ws,1));
                return o;
            }

            float2 OctaEncode(float3 n){
                n/=(abs(n.x)+abs(n.y)+abs(n.z));
                float2 e=n.xy; if(n.z<0) e=(1-abs(e.yx))*sign(e.xy); return e*0.5+0.5;
            }
            float2 SafeUV(int2 t,float2 l){
                int size=_Tiles*_TileRes+(_Tiles+1)*_TilePad;
                float2 px0=float2(t.x*_TileRes+(t.x+1)*_TilePad, t.y*_TileRes+(t.y+1)*_TilePad);
                float2 inner=l*(_TileRes-1)+0.5; return (px0+inner)/size;
            }

            float4 frag(v2f i):SV_Target
            {
                float3 center=i.centerWS;
                float3 viewWS=normalize(_WorldSpaceCameraPos - center);
                float3 viewOS=mul((float3x3)GetWorldToObjectMatrix(), viewWS);
                float2 oct=OctaEncode(normalize(viewOS));
                float2 g=oct*_Tiles; int2 t00=(int2)floor(g); float2 f=frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

                float3 up=(abs(viewWS.y)>0.99)?float3(1,0,0):float3(0,1,0);
                float3 U=normalize(cross(up,viewWS));
                float3 V=normalize(cross(viewWS,U));
                float3 cam=_WorldSpaceCameraPos;
                float3 pixDir=normalize(i.ws - cam);
                float denom=dot(viewWS,pixDir); if(abs(denom)<1e-4) discard;
                float t=dot(viewWS,(center-cam))/denom;
                float3 hit=cam + pixDir*t;

                float2 plane=float2(dot(hit-center,U), dot(hit-center,V));
                float2 local01=float2(plane.x/max(1e-5,2.0*_HalfU), plane.y/max(1e-5,2.0*_HalfV))+0.5;
                if(any(local01<0)||any(local01>1)) discard;

                float2 uv00=SafeUV(t00,local01), uv10=SafeUV(t10,local01);
                float2 uv01=SafeUV(t01,local01), uv11=SafeUV(t11,local01);
                float4 c00=tex2D(_AtlasColor,uv00), c10=tex2D(_AtlasColor,uv10);
                float4 c01=tex2D(_AtlasColor,uv01), c11=tex2D(_AtlasColor,uv11);
                float  z00=tex2D(_AtlasDepth,uv00).r, z10=tex2D(_AtlasDepth,uv10).r;
                float  z01=tex2D(_AtlasDepth,uv01).r, z11=tex2D(_AtlasDepth,uv11).r;

                float eps = 1.0/max(1.0,(_TileRes-1));
                float a00 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps, z00);
                float a10 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps, z10);
                float a01 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps, z01);
                float a11 = 1.0 - smoothstep(_DepthBG, _DepthBG + eps, z11);

                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;
                float W=(w00*a00+w10*a10+w01*a01+w11*a11)*_OpacityBoost;
                clip(W - _Cutoff);

                // unlit color in HDRP for broad compatibility
                return w00*c00 + w10*c10 + w01*c01 + w11*c11;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
