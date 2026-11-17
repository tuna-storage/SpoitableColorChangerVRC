// Spoitable Color Changer のシーンビュー表示用シェーダー。エディタからのみ使う(Graphics.DrawMeshNow 用)。
// メッシュの UV でマスク(R8)を読み、その濃さに応じて _Color を半透明で重ねる(選択範囲・ホバー中の範囲の表示)。
Shader "Hidden/faketuna/SpoitableColorChanger/SceneOverlay"
{
    Properties
    {
        _MaskTex ("Mask", 2D) = "black" {}
        _Color ("Color", Color) = (1, 0, 0, 0.5)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            // 元の面と同じ深さに描くので、少し手前に寄せて奥行きの競合を避ける
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MaskTex;
            float4 _Color;
            float4 _St;   // マテリアルの Tiling (xy) と Offset (zw)
            float4 _Wrap; // x: U の扱い(0 Clamp, 1 Repeat, 2 Mirror), y: V の扱い

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _St.xy + _St.zw;
                return o;
            }

            // Repeat と Mirror は折り返し、Clamp は 0〜1 の外を描かない(テクスチャも端の色を引き伸ばすだけなので)
            float WrapCoordinate(float v, float mode)
            {
                if (mode > 1.5)
                {
                    float f = v - 2.0 * floor(v * 0.5);
                    return f > 1.0 ? 2.0 - f : f;
                }
                if (mode > 0.5) return frac(v);
                if (v < 0.0 || v > 1.0) discard;
                return v;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = float2(WrapCoordinate(i.uv.x, _Wrap.x), WrapCoordinate(i.uv.y, _Wrap.y));

                float m = tex2Dlod(_MaskTex, float4(uv, 0, 0)).r;
                if (m <= 0.004) discard;
                return float4(_Color.rgb, _Color.a * m);
            }
            ENDCG
        }
    }
}
