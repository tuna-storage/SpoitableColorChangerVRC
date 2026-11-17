// Spoitable Color Changer の位置マップ用シェーダー。エディタからのみ使う。
// Pass 0: メッシュを UV の位置に描き、アバターのルートから見た位置を書く(アルファ 1 = メッシュが覆っている)
// Pass 1: 覆っていない画素を、周りの画素の平均で埋める(アルファ 0.5 = 埋めた画素)
// Pass 2: _SampleUv の画素を 1 × 1 に写す(CPU から 1 画素だけ読むため。ReadPixels の部分読みは上下の扱いが環境で違う)
Shader "Hidden/faketuna/SpoitableColorChanger/PositionMap"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        // 0: rasterize in UV space
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 _St;        // マテリアルの Tiling (xy) と Offset (zw)
            float4 _UvShift;   // 繰り返しの何枚目を描くか(xy を引く)
            float4x4 _ToAvatar;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 position : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float2 uv = v.uv * _St.xy + _St.zw - _UvShift.xy;
                // GL.LoadOrtho の下で UV を 0〜1 の平面上の点として描く(Graphics.Blit と同じ向きになる。
                // RenderTexture に描くときの上下の反転は、Unity が投影行列で吸収する)
                o.pos = UnityObjectToClipPos(float3(uv, 0.0));
                o.position = mul(_ToAvatar, float4(v.vertex.xyz, 1.0)).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.position, 1.0);
            }
            ENDCG
        }

        // 1: dilate
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                if (c.a > 0.0) return c;

                float3 sum = 0.0;
                float n = 0.0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float4 s = tex2Dlod(_MainTex, float4(i.uv + float2(x, y) * _MainTex_TexelSize.xy, 0, 0));
                        if (s.a > 0.0)
                        {
                            sum += s.xyz;
                            n += 1.0;
                        }
                    }
                }

                return n > 0.0 ? float4(sum / n, 0.5) : float4(0, 0, 0, 0);
            }
            ENDCG
        }

        // 2: sample one pixel
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _SampleUv;

            float4 frag(v2f_img i) : SV_Target
            {
                return tex2Dlod(_MainTex, float4(_SampleUv.xy, 0, 0));
            }
            ENDCG
        }
    }

    Fallback Off
}
