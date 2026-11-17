using System.Collections.Generic;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>ストップから LUT テクスチャを作る。補間は OKLab で行い、値は linear RGB で持つ。</summary>
    internal static class GradientLut
    {
        public const int Width = 256;

        /// <summary>
        /// 1 レイヤー 1 行の LUT。RGBAHalf の linear テクスチャ(8bit だと暗部に段が出るため)。
        /// rows はレイヤーごとのストップ(Relative の置き直しなどを済ませたもの)。
        /// </summary>
        public static Texture2D Create(IReadOnlyList<(float position, Color color)[]> rows)
        {
            var height = Mathf.Max(1, rows.Count);
            var tex = new Texture2D(Width, height, TextureFormat.RGBAHalf, false, true)
            {
                name = "SpoitableColorChanger LUT",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color[Width * height];
            for (var y = 0; y < rows.Count; y++)
            {
                var lut = ColorMath.BuildLinearLut(ToSrgbStops(rows[y]), Width);
                for (var x = 0; x < Width; x++)
                {
                    var c = lut[x];
                    pixels[y * Width + x] = new Color(c.R, c.G, c.B, 1f);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>LUT の y 行目の中心の v 座標。</summary>
        public static float RowV(int row, int height)
        {
            return (row + 0.5f) / Mathf.Max(1, height);
        }

        /// <summary>インスペクタ表示用に、sRGB の 1 行テクスチャへ書き込む。</summary>
        public static void FillDisplayTexture(Texture2D target, IReadOnlyList<(float position, Color color)> stops)
        {
            var lut = ColorMath.BuildLinearLut(ToSrgbStops(stops), target.width);
            var pixels = new Color32[target.width * target.height];
            for (var x = 0; x < target.width; x++)
            {
                var s = ColorMath.LinearToSrgb(lut[x]);
                var c = new Color32((byte)ColorMath.ToByte(s.R), (byte)ColorMath.ToByte(s.G),
                    (byte)ColorMath.ToByte(s.B), 255);
                for (var y = 0; y < target.height; y++) pixels[y * target.width + x] = c;
            }

            target.SetPixels32(pixels);
            target.Apply(false, false);
        }

        private static List<(float position, ColorMath.Rgb srgb)> ToSrgbStops(
            IReadOnlyList<(float position, Color color)> stops)
        {
            var list = new List<(float, ColorMath.Rgb)>(stops.Count);
            foreach (var (position, color) in stops)
            {
                // Color の値は sRGB(カラーピッカーや HEX と同じ)
                list.Add((position, new ColorMath.Rgb(color.r, color.g, color.b)));
            }

            return list;
        }
    }
}
