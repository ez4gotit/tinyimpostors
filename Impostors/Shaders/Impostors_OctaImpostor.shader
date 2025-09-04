// Assets/Impostors/Shaders/Impostors_OctaImpostor.shader
Shader "Impostors/OctaImpostor"
{
    Properties{
        _AtlasColor("Atlas Color", 2D) = "white" {}
        _AtlasNormal("Atlas Normal", 2D) = "bump" {}
        _AtlasDepth("Atlas Depth", 2D) = "black" {}
        _Tiles("Tiles (N)", Int) = 8
        _TileRes("Tile Resolution", Int) = 256
        _TilePad("Tile Padding", Int) = 2
        _Near("Near", Float) = 0.01
        _Far("Far", Float) = 50.0
    }
    SubShader
    {
        Tags{ "Queue"="AlphaTest" "RenderType"="Opaque" }
        Cull Off ZWrite On ZTest LEqual
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _AtlasColor, _AtlasNormal, _AtlasDepth;
            int _Tiles, _TileRes, _TilePad;
            float _Near, _Far;

            struct appdata{ float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f{ float4 pos:SV_POSITION; float3 ws:TEXCOORD0; float2 uv:TEXCOORD1; };

            v2f vert(appdata v){
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.ws = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = v.uv;
                return o;
            }

            float2 OctaEncode(float3 n){
                n /= (abs(n.x) + abs(n.y) + abs(n.z));
                float2 enc = n.xy;
                if (n.z < 0){
                    enc = (1 - abs(enc.yx)) * (float2(enc.x >= 0 ? 1 : -1, enc.y >= 0 ? 1 : -1));
                }
                return enc * 0.5 + 0.5;
            }

            float2 TileUV(float2 octUV, out int2 tile)
            {
                // choose nearest tile index
                float2 f = octUV * _Tiles;
                tile = (int2)floor(f);
                float2 local = f - tile; // 0..1 in tile
                // Apply padding into atlas UVs later
                return local;
            }

            float2 AtlasUV(int2 tile, float2 local)
            {
                int size = _Tiles * _TileRes + (_Tiles + 1) * _TilePad;
                float2 px = float2(tile.x * _TileRes + (tile.x + 1) * _TilePad,
                                   tile.y * _TileRes + (tile.y + 1) * _TilePad);
                float2 uvPx = px + local * _TileRes;
                return uvPx / size;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 camDir = normalize(_WorldSpaceCameraPos - i.ws);
                float2 oct = OctaEncode(-camDir); // look dir from object to camera
                int2 tile; float2 loc = TileUV(oct, tile);
                float2 uvAtlas = AtlasUV(tile, loc);

                float3 albedo = tex2D(_AtlasColor, uvAtlas).rgb;
                float3 nrm = tex2D(_AtlasNormal, uvAtlas).rgb * 2 - 1;
                float zlin = tex2D(_AtlasDepth, uvAtlas).r; // linear01 depth from bake camera
                float z = lerp(_Near, _Far, zlin);

                // simple lighting
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = saturate(dot(nrm, L));
                float3 col = albedo*(0.25 + 0.75*ndl);

                // optional soft alpha using depth (cut empty texels)
float alpha = 1.0 - step(0.999, zlin); // zlin≈1 means "no mesh" → alpha=0
clip(alpha - 0.001);

                return float4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
