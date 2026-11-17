using System;
using System.Collections.Generic;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 色空間の変換と、OKLab で補間するグラデーション。
    /// Unity の API に頼らない純粋な C# なので、Unity の外でも検証できる。
    /// 変換式は Björn Ottosson による OKLab の定義に従う。
    /// </summary>
    internal static class ColorMath
    {
        public readonly struct Lab
        {
            public readonly float L;
            public readonly float A;
            public readonly float B;

            public Lab(float l, float a, float b)
            {
                L = l;
                A = a;
                B = b;
            }
        }

        public readonly struct Rgb
        {
            public readonly float R;
            public readonly float G;
            public readonly float B;

            public Rgb(float r, float g, float b)
            {
                R = r;
                G = g;
                B = b;
            }
        }

        private static float Pow(float x, float y)
        {
            return (float)Math.Pow(x, y);
        }

        private static float Cbrt(float x)
        {
            return x < 0f ? -(float)Math.Pow(-x, 1.0 / 3.0) : (float)Math.Pow(x, 1.0 / 3.0);
        }

        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            if (c <= 0f) return 0f;
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Pow(c, 1f / 2.4f) - 0.055f;
        }

        public static Rgb SrgbToLinear(Rgb c)
        {
            return new Rgb(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B));
        }

        public static Rgb LinearToSrgb(Rgb c)
        {
            return new Rgb(LinearToSrgb(c.R), LinearToSrgb(c.G), LinearToSrgb(c.B));
        }

        public static Lab LinearToOklab(Rgb c)
        {
            var l = 0.4122214708f * c.R + 0.5363325363f * c.G + 0.0514459929f * c.B;
            var m = 0.2119034982f * c.R + 0.6806995451f * c.G + 0.1073969566f * c.B;
            var s = 0.0883024619f * c.R + 0.2817188376f * c.G + 0.6299787005f * c.B;

            var l_ = Cbrt(l);
            var m_ = Cbrt(m);
            var s_ = Cbrt(s);

            return new Lab(
                0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
                1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
                0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_
            );
        }

        public static Rgb OklabToLinear(Lab c)
        {
            var l_ = c.L + 0.3963377774f * c.A + 0.2158037573f * c.B;
            var m_ = c.L - 0.1055613458f * c.A - 0.0638541728f * c.B;
            var s_ = c.L - 0.0894841775f * c.A - 1.2914855480f * c.B;

            var l = l_ * l_ * l_;
            var m = m_ * m_ * m_;
            var s = s_ * s_ * s_;

            return new Rgb(
                +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s
            );
        }

        /// <summary>OKLab の L(0〜1)。シェーダーの計算と同じ式。</summary>
        public static float OklabLightness(Rgb linear)
        {
            return LinearToOklab(linear).L;
        }

        /// <summary>
        /// sRGB のストップ列から、OKLab で補間した linear RGB の LUT を作る。
        /// 範囲外(最初のストップより前、最後より後)は端の色で埋める。
        /// </summary>
        /// <param name="stops">位置(0〜1)と sRGB 色。順不同でよい。</param>
        /// <param name="width">LUT の幅。</param>
        public static Rgb[] BuildLinearLut(IReadOnlyList<(float position, Rgb srgb)> stops, int width)
        {
            if (width < 2) throw new ArgumentOutOfRangeException(nameof(width));

            var result = new Rgb[width];
            if (stops == null || stops.Count == 0)
            {
                for (var i = 0; i < width; i++)
                {
                    var v = (float)i / (width - 1);
                    result[i] = new Rgb(v, v, v);
                }

                return result;
            }

            var sorted = new List<(float position, Lab lab)>(stops.Count);
            foreach (var (position, srgb) in stops)
            {
                var p = Clamp01(position);
                sorted.Add((p, LinearToOklab(SrgbToLinear(srgb))));
            }

            sorted.Sort((a, b) => a.position.CompareTo(b.position));

            for (var i = 0; i < width; i++)
            {
                var t = (float)i / (width - 1);
                result[i] = ClampRgb(OklabToLinear(Evaluate(sorted, t)));
            }

            return result;
        }

        private static Lab Evaluate(List<(float position, Lab lab)> sorted, float t)
        {
            if (t <= sorted[0].position) return sorted[0].lab;
            var last = sorted[sorted.Count - 1];
            if (t >= last.position) return last.lab;

            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var a = sorted[i];
                var b = sorted[i + 1];
                if (t > b.position) continue;

                var span = b.position - a.position;
                var f = span <= 1e-6f ? 1f : (t - a.position) / span;
                return new Lab(
                    a.lab.L + (b.lab.L - a.lab.L) * f,
                    a.lab.A + (b.lab.A - a.lab.A) * f,
                    a.lab.B + (b.lab.B - a.lab.B) * f
                );
            }

            return last.lab;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        private static Rgb ClampRgb(Rgb c)
        {
            return new Rgb(Clamp01(c.R), Clamp01(c.G), Clamp01(c.B));
        }

        /// <summary>"#RRGGBB" / "RRGGBB" / "#RGB" を sRGB として読む。</summary>
        public static bool TryParseHex(string text, out Rgb srgb)
        {
            srgb = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 3)
            {
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            }

            if (s.Length != 6 && s.Length != 8) return false;
            foreach (var ch in s)
            {
                if (!Uri.IsHexDigit(ch)) return false;
            }

            if (!TryHexByte(s, 0, out var r) || !TryHexByte(s, 2, out var g) || !TryHexByte(s, 4, out var b))
            {
                return false;
            }

            srgb = new Rgb(r / 255f, g / 255f, b / 255f);
            return true;
        }

        private static bool TryHexByte(string s, int index, out int value)
        {
            return int.TryParse(s.Substring(index, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        public static string ToHex(Rgb srgb)
        {
            return "#" + ToByte(srgb.R).ToString("X2") + ToByte(srgb.G).ToString("X2") + ToByte(srgb.B).ToString("X2");
        }

        public static int ToByte(float v)
        {
            var i = (int)Math.Round(Clamp01(v) * 255f);
            return i < 0 ? 0 : i > 255 ? 255 : i;
        }
    }
}
