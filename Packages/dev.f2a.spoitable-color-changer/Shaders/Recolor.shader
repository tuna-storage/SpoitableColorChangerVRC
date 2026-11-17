// Spoitable Color Changer の再配色用シェーダー。エディタからのみ使う(Graphics.Blit 用)。
// Pass 0: 元テクスチャを linear RGB に展開する
// Pass 1: 1 レイヤー分のグラデーションマップを、前の結果にマスクで重ねる(色の経路があれば位置でバーを当てる)
// Pass 2: 結果を出力先の色空間に戻す
// Pass 3: 明るさの統計用に (OKLab の L, アルファ) を出す
// Pass 4: マスクエディタの表示(塗りマスクを色で重ねる)
// Pass 5: 色の近似選択用に (OKLab, アルファ) を出す
// Pass 6: グラデーションマスクの値だけを出す(エディタの表示用)
Shader "Hidden/faketuna/SpoitableColorChanger/Recolor"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _PrevTex ("Previous", 2D) = "white" {}
        _LutTex ("LUT", 2D) = "white" {}
        _MaskTex ("Mask", 2D) = "white" {}
        _PaintTex ("Paint Mask", 2D) = "white" {}
        _SelectionTex ("Selection", 2D) = "white" {}
        _PosTex ("Position Map", 2D) = "black" {}
        _PathRegionTex ("Path Regions", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    sampler2D _PrevTex;
    sampler2D _LutTex;
    sampler2D _MaskTex;
    sampler2D _PaintTex;
    sampler2D _SelectionTex;
    sampler2D _PosTex;
    sampler2D _PathRegionTex; // 塗りマスクの塊ごとに、使う線のビット(値 × 255)。0 ならすべての線を使う

    float4 _LutParams;   // x: LUT の行の v 座標, y: LUT の幅
    float4 _Range;       // x: 黒点, y: 1 / (白点 - 黒点)
    float4 _MaskWeights; // マスクのどのチャンネルを使うか
    float4 _MaskParams;  // x: マスクを使う, y: 反転, z: マスクが sRGB, w: 不透明度
    float4 _PaintParams; // x: 塗りマスクを使う, y: 塗りマスクが sRGB
    float4 _OverlayColor;  // マスク表示の色(linear)
    float4 _OverlayParams; // x: マスク表示の濃さ, y: 選択範囲があるか
    float _SourceIsLinearData; // 元テクスチャが sRGB でないとき 1(値を sRGB として解釈し直す)

    // グラデーションマスク
    float4 _GradParams; // x: 種類(0 なし, 1 直線, 2 円形, 3 テクスチャ上の点, 4 モデル上の方向, 5 モデル上の点)
                        // y: 反転, z: なめらか, w: 点の補間の指数
    float4 _GradA;      // 始点(テクスチャ上は UV、モデル上はアバターのルートから見た位置)
    float4 _GradB;      // 終点
    float4 _GradSize;   // xy: テクスチャの画素数(距離を縦横比どおりに測るため), z: 点の数
    float4 _GradPoints[16]; // xyz: 位置, w: 値

    // 色の経路(位置でバーの色を並べる)
    float4 _PathParams;     // x: 使う, y: モデル上, z: 点の数(すべての線の合計), w: 陰影の強さ(元の明暗をどれだけ残すか)
    // xyz: 位置(テクスチャ上は UV、モデル上はアバターのルートから見た位置)
    // w: 線の番号 × 2 + その点までの弧長の比(0〜1)。線は続けて並べ、番号の違う点どうしはつながない
    float4 _PathPoints[64];
    float _PathLineV[8];      // 線ごとの LUT の行の v 座標(Relative で数が合う線は専用の行、それ以外はレイヤーの行)
    float4 _PathRegionParams; // x: 塊ごとに使う線を分ける, yz: UV から塊の表の UV への倍率

    float3 SrgbToLinearExact(float3 c)
    {
        c = max(c, 0.0);
        float3 lo = c / 12.92;
        float3 hi = pow((c + 0.055) / 1.055, 2.4);
        return lerp(hi, lo, step(c, 0.04045));
    }

    float3 LinearToSrgbExact(float3 c)
    {
        c = max(c, 0.0);
        float3 lo = c * 12.92;
        float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        return lerp(hi, lo, step(c, 0.0031308));
    }

    float CbrtSigned(float x)
    {
        return sign(x) * pow(abs(x), 1.0 / 3.0);
    }

    // OKLab(ColorMath.LinearToOklab と同じ式)
    float3 LinearToOklab(float3 c)
    {
        float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
        float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
        float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
        float l_ = CbrtSigned(l);
        float m_ = CbrtSigned(m);
        float s_ = CbrtSigned(s);
        return float3(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    // OKLab → linear RGB(ColorMath.OklabToLinear と同じ式)
    float3 OklabToLinear(float3 c)
    {
        float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
        float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
        float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;
        return float3(
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    // OKLab の L(ColorMath.OklabLightness と同じ式)
    float OklabL(float3 c)
    {
        float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
        float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
        float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
        return 0.2104542553 * CbrtSigned(l) + 0.7936177850 * CbrtSigned(m) - 0.0040720468 * CbrtSigned(s);
    }

    // グラデーションマスクの値(0〜1)。種類が 0 なら 1(マスクなし)
    float GradientMask(float2 uv)
    {
        int mode = (int)(_GradParams.x + 0.5);
        if (mode == 0) return 1.0;

        bool model = mode >= 4;
        // テクスチャ上は画素の座標、モデル上は位置マップの位置で距離を測る
        float4 pm = tex2Dlod(_PosTex, float4(uv, 0, 0));
        // どのメッシュにも使われていない画素(位置がない)は、モデル上の種類ではマスクなし(0)にする
        if (model && pm.a <= 0.0) return 0.0;
        float3 p = model ? pm.xyz : float3(uv * _GradSize.xy, 0);
        float3 a = model ? _GradA.xyz : float3(_GradA.xy * _GradSize.xy, 0);
        float3 b = model ? _GradB.xyz : float3(_GradB.xy * _GradSize.xy, 0);

        float g;
        if (mode == 1 || mode == 4)
        {
            float3 d = b - a;
            g = dot(p - a, d) / max(dot(d, d), 1e-10);
        }
        else if (mode == 2)
        {
            g = length(p - a) / max(length(b - a), 1e-5);
        }
        else
        {
            // 逆距離加重。重みはいちばん近い点との距離の比で求める((最短距離 / 距離)^指数。最大 1)。
            // 1 / 距離^指数 のままだと、メートル単位で指数が大きいと桁あふれや下限の頭打ちが起きるため。
            // 点の上ではその点の値になる
            int count = (int)(_GradSize.z + 0.5);
            float dmin = 1e30;
            float nearest = 0.0;
            for (int k = 0; k < 16; k++)
            {
                if (k >= count) break;
                float3 q = model ? _GradPoints[k].xyz : float3(_GradPoints[k].xy * _GradSize.xy, 0);
                float d = distance(p, q);
                if (d < dmin)
                {
                    dmin = d;
                    nearest = _GradPoints[k].w;
                }
            }

            if (count <= 0)
            {
                g = 0.0;
            }
            else if (dmin <= 1e-7)
            {
                g = nearest;
            }
            else
            {
                float sw = 0.0;
                float sv = 0.0;
                for (int j = 0; j < 16; j++)
                {
                    if (j >= count) break;
                    float3 q = model ? _GradPoints[j].xyz : float3(_GradPoints[j].xy * _GradSize.xy, 0);
                    float w = pow(dmin / max(distance(p, q), dmin), _GradParams.w);
                    sw += w;
                    sv += w * _GradPoints[j].w;
                }

                g = sv / sw;
            }
        }

        g = saturate(g);
        if (_GradParams.z > 0.5) g = smoothstep(0.0, 1.0, g);
        if (_GradParams.y > 0.5) g = 1.0 - g;
        return g;
    }

    // 色の経路の上の位置(0〜1)。いちばん近い線分に射影し、両端の点の弧長の比を補間する。
    // テクスチャ上は画素の座標(縦横比どおり、_GradSize.xy は SetGradient が毎レイヤー渡す)、モデル上は位置マップで測る。
    // モデル上で位置のない画素は covered = 0。
    // 線が複数あれば、その画素の塊で使う線(_PathRegionTex)のうち、いちばん近い線分のものを使う。lineIndex はその線の番号(なければ -1)
    float PathPosition(float2 uv, out float covered, out float lineIndex)
    {
        bool model = _PathParams.y > 0.5;
        int count = (int)(_PathParams.z + 0.5);
        float4 pm = tex2Dlod(_PosTex, float4(uv, 0, 0));
        covered = (!model || pm.a > 0.0) ? 1.0 : 0.0;
        float3 p = model ? pm.xyz : float3(uv * _GradSize.xy, 0);
        float bits = 0.0;
        if (_PathRegionParams.x > 0.5)
        {
            bits = floor(tex2Dlod(_PathRegionTex, float4(uv * _PathRegionParams.yz, 0, 0)).r * 255.0 + 0.5);
        }

        float best = 1e30;
        float t = 0.0;
        lineIndex = -1.0;
        [loop]
        for (int k = 0; k < 63; k++)
        {
            if (k + 1 >= count) break;
            float4 pa = _PathPoints[k];
            float4 pb = _PathPoints[k + 1];
            float la = floor(pa.w * 0.5);
            if (floor(pb.w * 0.5) != la) continue; // 別の線の点どうしはつながない
            if (bits > 0.5 && fmod(floor(bits / exp2(la)), 2.0) < 0.5) continue; // この塊では使わない線
            float3 a = model ? pa.xyz : float3(pa.xy * _GradSize.xy, 0);
            float3 b = model ? pb.xyz : float3(pb.xy * _GradSize.xy, 0);
            float3 d = b - a;
            float s = saturate(dot(p - a, d) / max(dot(d, d), 1e-12));
            float3 e = p - (a + d * s);
            float dist = dot(e, e);
            if (dist < best)
            {
                best = dist;
                t = lerp(pa.w - 2.0 * la, pb.w - 2.0 * la, s);
                lineIndex = la;
            }
        }

        return saturate(t);
    }

    float4 SampleSourceLinear(float2 uv)
    {
        float4 s = tex2Dlod(_MainTex, float4(uv, 0, 0));
        if (_SourceIsLinearData > 0.5) s.rgb = SrgbToLinearExact(s.rgb);
        return s;
    }
    ENDCG

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        // 0: decode
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                return SampleSourceLinear(i.uv);
            }
            ENDCG
        }

        // 1: layer
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float4 src = SampleSourceLinear(i.uv);
                float lsrc = OklabL(src.rgb);
                float t = saturate((lsrc - _Range.x) * _Range.y);
                bool usePath = _PathParams.x > 0.5;
                float pathCovered = 1.0;
                float pathLine = -1.0;
                if (usePath) t = PathPosition(i.uv, pathCovered, pathLine);
                float u = (t * (_LutParams.y - 1.0) + 0.5) / _LutParams.y;
                float lutV = _LutParams.x;
                if (pathLine > -0.5) lutV = _PathLineV[(int)(pathLine + 0.5)];
                float3 grad = tex2Dlod(_LutTex, float4(u, lutV, 0, 0)).rgb;
                if (usePath)
                {
                    // 陰影: バーの色の明るさ(OKLab の L)に、元の明るさのレンジの中央からの差を足す(a, b はバーの色のまま)
                    float3 lab = LinearToOklab(grad);
                    float mid = _Range.x + 0.5 / _Range.y;
                    lab.x = saturate(lab.x + (lsrc - mid) * _PathParams.w);
                    grad = max(OklabToLinear(lab), 0.0);
                }
                float4 prev = tex2Dlod(_PrevTex, float4(i.uv, 0, 0));

                // 分岐の外でサンプルする(マスクを使わないときは白テクスチャが入っている)
                float4 mk = tex2D(_MaskTex, i.uv);
                float4 pk = tex2D(_PaintTex, i.uv);
                float m = 1.0;
                if (_MaskParams.x > 0.5)
                {
                    if (_MaskParams.z > 0.5) mk.rgb = LinearToSrgbExact(mk.rgb);
                    float g = saturate(dot(mk, _MaskWeights));
                    m = _MaskParams.y > 0.5 ? 1.0 - g : g;
                }
                if (_PaintParams.x > 0.5)
                {
                    float p = _PaintParams.y > 0.5 ? LinearToSrgbExact(pk.rrr).r : pk.r;
                    m *= saturate(p);
                }
                m *= GradientMask(i.uv);
                m *= pathCovered;
                m = saturate(m * _MaskParams.w);

                return float4(lerp(prev.rgb, grad, m), src.a);
            }
            ENDCG
        }

        // 2: encode
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                if (_SourceIsLinearData > 0.5) c.rgb = LinearToSrgbExact(c.rgb);
                return c;
            }
            ENDCG
        }

        // 3: stats
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float4 s = SampleSourceLinear(i.uv);
                return float4(OklabL(s.rgb), s.a, 0, 1);
            }
            ENDCG
        }

        // 4: マスクエディタの表示用。透明部分を暗くし、塗りマスクを色で重ねる
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float4 b = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                float3 rgb = lerp(float3(0.03, 0.03, 0.03), b.rgb, saturate(b.a * 2.0));
                float m = tex2Dlod(_PaintTex, float4(i.uv, 0, 0)).r * _OverlayParams.x;
                rgb = lerp(rgb, _OverlayColor.rgb, saturate(m));
                if (_OverlayParams.y > 0.5)
                {
                    float sel = tex2Dlod(_SelectionTex, float4(i.uv, 0, 0)).r;
                    rgb *= lerp(0.25, 1.0, sel);
                }
                return float4(rgb, 1);
            }
            ENDCG
        }

        // 5: 色の近似選択用に (OKLab の L, a, b, アルファ) を出す
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float4 s = SampleSourceLinear(i.uv);
                return float4(LinearToOklab(s.rgb), s.a);
            }
            ENDCG
        }

        // 6: グラデーションマスクの値だけを出す(エディタの表示用)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f_img i) : SV_Target
            {
                float g = GradientMask(i.uv);
                return float4(g, g, g, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
