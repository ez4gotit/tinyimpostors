Shader "Impostors/OctaImpostor"
{
    Properties{
        _AtlasColor ("Atlas Color", 2D) = "white" {}
        _AtlasNormal("Atlas Normal", 2D) = "bump"  {}
        _AtlasDepth ("Atlas Depth", 2D) = "black" {}
        _Tiles     ("Tiles (N)", Int)      = 8
        _TileRes   ("Tile Resolution", Int)= 256
        _TilePad   ("Tile Padding", Int)   = 2
        _HalfU     ("HalfU", Float)        = 1.0
        _HalfV     ("HalfV", Float)        = 1.0
        _Near      ("Near",  Float)        = 0.01
        _Far       ("Far",   Float)        = 50.0
        _Radius    ("Radius",Float)        = 1.0
        _BakeDistMul("BakeDistMul",Float)  = 2.0
        _Cutoff    ("Alpha Cutoff", Range(0,1)) = 0.05
        _OpacityBoost("Opacity Boost", Range(0,4)) = 2.0
    }

    /***********************************************************************
        UNIVERSAL RENDER PIPELINE
    ************************************************************************/
    SubShader
    {
        Tags{
            "RenderPipeline"="UniversalPipeline"
            "Queue"="AlphaTest"              // cutout queue
            "RenderType"="TransparentCutout"
            "IgnoreProjector"="True"
        }
        ZWrite On
        Cull Off
        AlphaToMask Off                 // needs MSAA to smooth the cutout edge
        // no alpha blending for a crisp, opaque result
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
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_AtlasColor);  SAMPLER(sampler_AtlasColor);
            TEXTURE2D(_AtlasNormal); SAMPLER(sampler_AtlasNormal);
            TEXTURE2D(_AtlasDepth);  SAMPLER(sampler_AtlasDepth);
            int _Tiles, _TileRes, _TilePad;
            float _HalfU, _HalfV, _Near, _Far, _Radius, _BakeDistMul, _Cutoff, _OpacityBoost;

            struct Attributes { float3 positionOS: POSITION; };
            struct Varyings   { float4 positionHCS: SV_POSITION; float3 positionWS: TEXCOORD0; float3 centerWS: TEXCOORD1; };

            Varyings vert(Attributes v){
                Varyings o;
                float3 posWS = TransformObjectToWorld(v.positionOS);
                o.positionHCS = TransformWorldToHClip(posWS);
                o.positionWS  = posWS;
                o.centerWS    = TransformObjectToWorld(float3(0,0,0));
                return o;
            }

            float2 OctaEncode(float3 n){
                n /= (abs(n.x)+abs(n.y)+abs(n.z));
                float2 e = n.xy;
                if (n.z < 0) e = (1 - abs(e.yx)) * sign(e.xy);
                return e*0.5 + 0.5;
            }
            void BuildBasis(float3 dir, out float3 U, out float3 V){
                float3 up = (abs(dir.y)>0.99) ? float3(1,0,0) : float3(0,1,0);
                U = normalize(cross(up, dir)); V = normalize(cross(dir, U));
            }
            float2 SafeAtlasUV(int2 tile, float2 local01){
                int size = _Tiles*_TileRes + (_Tiles+1)*_TilePad;
                float2 px0 = float2(tile.x*_TileRes + (tile.x+1)*_TilePad,
                                    tile.y*_TileRes + (tile.y+1)*_TilePad);
                float2 inner = local01*(_TileRes-1) + 0.5; // keep inside tile
                return (px0 + inner) / size;
            }

            struct Sample { float4 col; float3 N; float zlin; float a; };
            Sample SampleTile(int2 tile, float2 local01){
                tile = clamp(tile, int2(0,0), int2(_Tiles-1,_Tiles-1));
                float2 uv = SafeAtlasUV(tile, local01);
                Sample s;
                s.col  = SAMPLE_TEXTURE2D(_AtlasColor,  sampler_AtlasColor,  uv);
                s.N    = normalize(SAMPLE_TEXTURE2D(_AtlasNormal, sampler_AtlasNormal, uv).rgb*2-1);
                s.zlin = SAMPLE_TEXTURE2D(_AtlasDepth,  sampler_AtlasDepth,  uv).r;
                float af = max(fwidth(s.zlin), 1e-3);
                s.a = 1.0 - smoothstep(1.0-af, 1.0, s.zlin); // 1 in mesh, 0 in bg
                return s;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 center = i.centerWS;
                float3 viewDirWS = normalize(GetCameraPositionWS() - center);
                float3 viewDirOS = TransformWorldToObjectDir(viewDirWS);

                // tile from object-space view
                float2 oct = OctaEncode(normalize(viewDirOS));
                float2 g = oct * _Tiles; int2 t00=(int2)floor(g); float2 f = frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

// build orthonormal basis on the view plane
float3 U, V; 
float3 up = (abs(viewDirWS.y) > 0.99) ? float3(1,0,0) : float3(0,1,0);
U = normalize(cross(up, viewDirWS));
V = normalize(cross(viewDirWS, U));

// ray from camera through this pixel
float3 camWS  = /* URP: */ GetCameraPositionWS();    // Built-in/HDRP: _WorldSpaceCameraPos
float3 pixDir = normalize(i.positionWS - camWS);

// intersect with plane through 'center' with normal = viewDirWS
float denom = dot(viewDirWS, pixDir);
if (abs(denom) < 1e-4) discard;
float  t   = dot(viewDirWS, (center - camWS)) / denom;
float3 hit = camWS + pixDir * t;

// project hit onto U/V on that plane
float2 plane   = float2(dot(hit - center, U), dot(hit - center, V));
float2 local01 = float2(plane.x / max(1e-5, 2.0 * _HalfU),
                        plane.y / max(1e-5, 2.0 * _HalfV)) + 0.5;

// kill anything outside the current tile rectangle
if (any(local01 < 0.0) || any(local01 > 1.0)) discard;


                // 4-way alpha-weighted blend
                Sample s00=SampleTile(t00,local01), s10=SampleTile(t10,local01);
                Sample s01=SampleTile(t01,local01), s11=SampleTile(t11,local01);
                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;

// NEW — use only depth-derived coverage
float a00 = s00.a, a10 = s10.a, a01 = s01.a, a11 = s11.a;


                float W = (w00*a00 + w10*a10 + w01*a01 + w11*a11) * _OpacityBoost;
W = saturate(W + 1e-3);    // small nudge against precision loss
clip(W - _Cutoff);


                float3 col=(w00*a00*s00.col.rgb + w10*a10*s10.col.rgb +
                            w01*a01*s01.col.rgb + w11*a11*s11.col.rgb) / max(W,1e-4);
                float3 N  = normalize(w00*a00*s00.N + w10*a10*s10.N +
                                      w01*a01*s01.N + w11*a11*s11.N);

                // dynamic lighting (ambient + main directional)
                Light mainLight = GetMainLight();
                float3 ambient = SampleSH(N);
                float  ndl     = saturate(dot(N,-mainLight.direction));
                float3 direct  = mainLight.color * ndl;

                return float4(col*(ambient+direct), 1); // opaque color
            }
            ENDHLSL
        }
    }

    /***********************************************************************
        BUILT-IN RENDER PIPELINE
    ************************************************************************/
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
            #pragma target 4.0
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            sampler2D _AtlasColor,_AtlasNormal,_AtlasDepth;
            int _Tiles,_TileRes,_TilePad;
            float _HalfU,_HalfV,_Near,_Far,_Radius,_BakeDistMul,_Cutoff,_OpacityBoost;

            struct appdata{ float4 vertex:POSITION; };
            struct v2f{ float4 pos:SV_POSITION; float3 ws:TEXCOORD0; float3 centerWS:TEXCOORD1; };

            v2f vert(appdata v){
                v2f o;
                float4 w = mul(unity_ObjectToWorld, v.vertex);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.ws  = w.xyz;
                o.centerWS = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                return o;
            }

            float2 OctaEncode(float3 n){
                n/=(abs(n.x)+abs(n.y)+abs(n.z));
                float2 e=n.xy; if(n.z<0) e=(1-abs(e.yx))*sign(e.xy); return e*0.5+0.5;
            }
            void BuildBasis(float3 dir,out float3 U,out float3 V){
                float3 up=(abs(dir.y)>0.99)?float3(1,0,0):float3(0,1,0);
                U=normalize(cross(up,dir)); V=normalize(cross(dir,U));
            }
            float2 SafeAtlasUV(int2 tile,float2 local01){
                int size=_Tiles*_TileRes+(_Tiles+1)*_TilePad;
                float2 px0=float2(tile.x*_TileRes+(tile.x+1)*_TilePad,
                                  tile.y*_TileRes+(tile.y+1)*_TilePad);
                float2 inner=local01*(_TileRes-1)+0.5;
                return (px0+inner)/size;
            }
            struct Sample{ float4 col; float3 N; float zlin; float a; };
            Sample SampleTile(int2 tile,float2 local01){
                tile=clamp(tile,int2(0,0),int2(_Tiles-1,_Tiles-1));
                float2 uv=SafeAtlasUV(tile,local01);
                Sample s;
                s.col=tex2D(_AtlasColor,uv);
                s.N=normalize(tex2D(_AtlasNormal,uv).rgb*2-1);
                s.zlin=tex2D(_AtlasDepth,uv).r;
                float af=max(fwidth(s.zlin),1e-3);
                s.a=1.0 - smoothstep(1.0-af,1.0,s.zlin);
                return s;
            }

            float4 frag(v2f i):SV_Target
            {
                float3 center=i.centerWS;
                float3 viewDirWS=normalize(_WorldSpaceCameraPos - center);
                float3 viewDirOS=mul((float3x3)unity_WorldToObject, viewDirWS);

                float2 oct=OctaEncode(normalize(viewDirOS));
                float2 g=oct*_Tiles; int2 t00=(int2)floor(g); float2 f=frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

// build orthonormal basis on the view plane
float3 U, V; 
float3 up = (abs(viewDirWS.y) > 0.99) ? float3(1,0,0) : float3(0,1,0);
U = normalize(cross(up, viewDirWS));
V = normalize(cross(viewDirWS, U));

// ray from camera through this pixel
float3 camWS  = /* URP: */ GetCameraPositionWS();    // Built-in/HDRP: _WorldSpaceCameraPos
float3 pixDir = normalize(i.positionWS - camWS);

// intersect with plane through 'center' with normal = viewDirWS
float denom = dot(viewDirWS, pixDir);
if (abs(denom) < 1e-4) discard;
float  t   = dot(viewDirWS, (center - camWS)) / denom;
float3 hit = camWS + pixDir * t;

// project hit onto U/V on that plane
float2 plane   = float2(dot(hit - center, U), dot(hit - center, V));
float2 local01 = float2(plane.x / max(1e-5, 2.0 * _HalfU),
                        plane.y / max(1e-5, 2.0 * _HalfV)) + 0.5;

// kill anything outside the current tile rectangle
if (any(local01 < 0.0) || any(local01 > 1.0)) discard;


                Sample s00=SampleTile(t00,local01), s10=SampleTile(t10,local01);
                Sample s01=SampleTile(t01,local01), s11=SampleTile(t11,local01);
                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;
// NEW — use only depth-derived coverage
float a00 = s00.a, a10 = s10.a, a01 = s01.a, a11 = s11.a;

   float W = (w00*a00 + w10*a10 + w01*a01 + w11*a11) * _OpacityBoost;
W = saturate(W + 1e-3);    // small nudge against precision loss
clip(W - _Cutoff);


                float3 col=(w00*a00*s00.col.rgb+w10*a10*s10.col.rgb+
                            w01*a01*s01.col.rgb+w11*a11*s11.col.rgb)/max(W,1e-4);
                float3 N=normalize(w00*a00*s00.N+w10*a10*s10.N+w01*a01*s01.N+w11*a11*s11.N);

                float3 ambient=ShadeSH9(float4(N,1));
                float3 L=normalize(_WorldSpaceLightPos0.xyz);
                float  ndl=saturate(dot(N,L));
                float3 direct=_LightColor0.rgb*ndl;

                return float4(col*(ambient+direct), 1);
            }
            ENDHLSL
        }
    }

    /***********************************************************************
        HDRP (transparent cutout, unlit so it compiles across versions)
    ************************************************************************/
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
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ShaderVariables.hlsl"

            sampler2D _AtlasColor,_AtlasNormal,_AtlasDepth;
            int _Tiles,_TileRes,_TilePad; float _HalfU,_HalfV,_Cutoff,_OpacityBoost;

            struct appdata{ float3 positionOS:POSITION; };
            struct v2f{ float4 pos:SV_POSITION; float3 ws:TEXCOORD0; float3 centerWS:TEXCOORD1; };

            v2f vert(appdata v){
                v2f o;
                float3 ws = mul(GetObjectToWorldMatrix(), float4(v.positionOS,1)).xyz;
                o.pos      = mul(UNITY_MATRIX_VP, float4(ws,1));
                o.ws       = ws;
                o.centerWS = mul(GetObjectToWorldMatrix(), float4(0,0,0,1)).xyz;
                return o;
            }

            float2 OctaEncode(float3 n){
                n/=(abs(n.x)+abs(n.y)+abs(n.z));
                float2 e=n.xy; if(n.z<0) e=(1-abs(e.yx))*sign(e.xy); return e*0.5+0.5;
            }
            float2 SafeAtlasUV(int2 tile,float2 local01){
                int size=_Tiles*_TileRes+(_Tiles+1)*_TilePad;
                float2 px0=float2(tile.x*_TileRes+(tile.x+1)*_TilePad,
                                  tile.y*_TileRes+(tile.y+1)*_TilePad);
                float2 inner=local01*(_TileRes-1)+0.5;
                return (px0+inner)/size;
            }

            float4 frag(v2f i):SV_Target
            {
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - i.centerWS);
                float3 viewDirOS = mul((float3x3)GetWorldToObjectMatrix(), viewDirWS);
                float2 oct = OctaEncode(normalize(viewDirOS));

                float2 g=oct*_Tiles; int2 t00=(int2)floor(g); float2 f=frac(g);
                int2 t10=t00+int2(1,0), t01=t00+int2(0,1), t11=t00+int2(1,1);

float3 up = (abs(viewDirWS.y)>0.99)? float3(1,0,0) : float3(0,1,0);
// build orthonormal basis on the view plane
float3 U, V; 
float3 up = (abs(viewDirWS.y) > 0.99) ? float3(1,0,0) : float3(0,1,0);
U = normalize(cross(up, viewDirWS));
V = normalize(cross(viewDirWS, U));

// ray from camera through this pixel
float3 camWS  = /* URP: */ GetCameraPositionWS();    // Built-in/HDRP: _WorldSpaceCameraPos
float3 pixDir = normalize(i.positionWS - camWS);

// intersect with plane through 'center' with normal = viewDirWS
float denom = dot(viewDirWS, pixDir);
if (abs(denom) < 1e-4) discard;
float  t   = dot(viewDirWS, (center - camWS)) / denom;
float3 hit = camWS + pixDir * t;

// project hit onto U/V on that plane
float2 plane   = float2(dot(hit - center, U), dot(hit - center, V));
float2 local01 = float2(plane.x / max(1e-5, 2.0 * _HalfU),
                        plane.y / max(1e-5, 2.0 * _HalfV)) + 0.5;

// kill anything outside the current tile rectangle
if (any(local01 < 0.0) || any(local01 > 1.0)) discard;


                float2 uv00=SafeAtlasUV(t00,local01), uv10=SafeAtlasUV(t10,local01);
                float2 uv01=SafeAtlasUV(t01,local01), uv11=SafeAtlasUV(t11,local01);
                float4 c00=tex2D(_AtlasColor,uv00), c10=tex2D(_AtlasColor,uv10);
                float4 c01=tex2D(_AtlasColor,uv01), c11=tex2D(_AtlasColor,uv11);
                float w00=(1-f.x)*(1-f.y), w10=f.x*(1-f.y), w01=(1-f.x)*f.y, w11=f.x*f.y;

                // unlit cutout in HDRP
float W = (w00*a00 + w10*a10 + w01*a01 + w11*a11) * _OpacityBoost;
W = saturate(W + 1e-3);    // small nudge against precision loss
clip(W - _Cutoff);

                return w00*c00 + w10*c10 + w01*c01 + w11*c11;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
