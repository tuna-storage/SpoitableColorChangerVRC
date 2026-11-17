using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 塗りマスク(1 画素 1 バイト、行 0 が下端)への操作。座標はマスクの画素単位。
    /// </summary>
    internal static class MaskOps
    {
        /// <summary>
        /// ブラシの 1 回分。1 回のストロークの中では重ね塗りで濃くならないよう、
        /// 画素ごとの最大の強さ(strokeAlpha)を覚えておき、ストローク開始時の値から target へ補間する。
        /// </summary>
        /// <returns>1 画素でも値が変わったら true</returns>
        public static bool Stamp(byte[] data, byte[] strokeBase, byte[] strokeAlpha, int width, int height,
            Vector2 center, float radius, float hardness, float strength, byte target, byte[] clip = null)
        {
            var changed = false;
            radius = Mathf.Max(0.5f, radius);
            var inner = radius * Mathf.Clamp01(hardness);
            var x0 = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            var x1 = Mathf.Min(width - 1, Mathf.CeilToInt(center.x + radius));
            var y0 = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            var y1 = Mathf.Min(height - 1, Mathf.CeilToInt(center.y + radius));

            for (var y = y0; y <= y1; y++)
            {
                var dy = y + 0.5f - center.y;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5f - center.x;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > radius) continue;

                    float falloff;
                    if (d <= inner) falloff = 1f;
                    else
                    {
                        var t = (d - inner) / Mathf.Max(1e-4f, radius - inner);
                        falloff = 1f - t * t * (3f - 2f * t);
                    }

                    var i = y * width + x;
                    // 選択範囲があれば、その中だけに効かせる(選択の濃さに応じて弱める)
                    var limit = clip != null ? clip[i] / 255f : 1f;
                    var a = (byte)Mathf.RoundToInt(Mathf.Clamp01(falloff * strength * limit) * 255f);
                    if (a <= strokeAlpha[i]) continue;
                    strokeAlpha[i] = a;
                    var value = (byte)Mathf.RoundToInt(Mathf.Lerp(strokeBase[i], target, a / 255f));
                    if (data[i] == value) continue;
                    data[i] = value;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>from から to まで、ブラシ半径に応じた間隔で Stamp する。</summary>
        /// <returns>1 画素でも値が変わったら true</returns>
        public static bool StampSegment(byte[] data, byte[] strokeBase, byte[] strokeAlpha, int width, int height,
            Vector2 from, Vector2 to, float radius, float hardness, float strength, byte target, byte[] clip = null)
        {
            var changed = false;
            var spacing = Mathf.Max(0.5f, radius * 0.25f);
            var distance = Vector2.Distance(from, to);
            var steps = Mathf.Max(1, Mathf.CeilToInt(distance / spacing));
            for (var s = 1; s <= steps; s++)
            {
                var p = Vector2.Lerp(from, to, (float)s / steps);
                changed |= Stamp(data, strokeBase, strokeAlpha, width, height, p, radius, hardness, strength, target,
                    clip);
            }

            return changed;
        }

        /// <summary>多角形の内側(偶奇規則)を value で塗る。矩形と投げ縄に使う。</summary>
        public static void FillPolygon(byte[] data, int width, int height, IReadOnlyList<Vector2> points, byte value)
        {
            if (points == null || points.Count < 3) return;

            var minY = float.MaxValue;
            var maxY = float.MinValue;
            foreach (var p in points)
            {
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }

            var y0 = Mathf.Max(0, Mathf.FloorToInt(minY));
            var y1 = Mathf.Min(height - 1, Mathf.CeilToInt(maxY));
            var crossings = new List<float>();

            for (var y = y0; y <= y1; y++)
            {
                var sy = y + 0.5f;
                crossings.Clear();
                for (var i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    if ((a.y <= sy && b.y > sy) || (b.y <= sy && a.y > sy))
                    {
                        crossings.Add(a.x + (sy - a.y) / (b.y - a.y) * (b.x - a.x));
                    }
                }

                crossings.Sort();
                for (var k = 0; k + 1 < crossings.Count; k += 2)
                {
                    var xs = Mathf.Max(0, Mathf.CeilToInt(crossings[k] - 0.5f));
                    var xe = Mathf.Min(width - 1, Mathf.FloorToInt(crossings[k + 1] - 0.5f));
                    var row = y * width;
                    for (var x = xs; x <= xe; x++) data[row + x] = value;
                }
            }
        }

        /// <summary>clip(選択範囲)があればその濃さに応じて、なければそのまま value を書く。</summary>
        private static void Apply(byte[] data, int i, byte value, byte[] clip)
        {
            if (clip == null)
            {
                data[i] = value;
                return;
            }

            var s = clip[i];
            if (s == 0) return;
            data[i] = s == 255 ? value : (byte)Mathf.RoundToInt(Mathf.Lerp(data[i], value, s / 255f));
        }

        /// <summary>選択範囲の濃さに応じて、マスクを value へ近づける(選択範囲を塗りつぶす・消去する)。</summary>
        public static void FillSelection(byte[] data, byte[] selection, byte value)
        {
            for (var i = 0; i < data.Length; i++)
            {
                var s = selection[i];
                if (s == 0) continue;
                data[i] = s == 255 ? value : (byte)Mathf.RoundToInt(Mathf.Lerp(data[i], value, s / 255f));
            }
        }

        public static void Fill(byte[] data, byte value)
        {
            for (var i = 0; i < data.Length; i++) data[i] = value;
        }

        public static void Invert(byte[] data)
        {
            for (var i = 0; i < data.Length; i++) data[i] = (byte)(255 - data[i]);
        }

        public static bool IsEmpty(byte[] data)
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != 0) return false;
            }

            return true;
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            // 4096² だと 1600 万バイトになる。Mono の SequenceEqual はベクトル化されないので、8 バイトずつ比べる
            var la = MemoryMarshal.Cast<byte, ulong>(a);
            var lb = MemoryMarshal.Cast<byte, ulong>(b);
            for (var i = 0; i < la.Length; i++)
            {
                if (la[i] != lb[i]) return false;
            }

            for (var i = la.Length * 8; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <summary>
        /// 色の近似選択。クリックした画素と OKLab での距離が tolerance 以内の画素を value にする。
        /// contiguous なら、クリックした画素からつながっている範囲だけ。透明な画素は対象外。
        /// </summary>
        /// <returns>選べなかった(画像の外・透明な画素)なら false。そのときマスクは変えない</returns>
        public static bool SelectSimilar(byte[] data, int width, int height, SourcePixels source, Vector2 pixel,
            float tolerance, bool contiguous, byte value, byte[] clip = null)
        {
            if (pixel.x < 0f || pixel.y < 0f || pixel.x >= width || pixel.y >= height) return false;
            var sx = Mathf.Clamp(Mathf.FloorToInt(pixel.x / width * source.Width), 0, source.Width - 1);
            var sy = Mathf.Clamp(Mathf.FloorToInt(pixel.y / height * source.Height), 0, source.Height - 1);
            var selected = source.Similar(sx, sy, tolerance, contiguous);
            if (selected == null) return false;

            if (width == source.Width && height == source.Height)
            {
                for (var i = 0; i < data.Length; i++)
                {
                    if (selected[i]) Apply(data, i, value, clip);
                }

                return true;
            }

            // マスクと元テクスチャの大きさが違うときは、最も近い画素で対応させる
            for (var y = 0; y < height; y++)
            {
                var srcY = Mathf.Min(source.Height - 1, (int)((y + 0.5f) / height * source.Height));
                for (var x = 0; x < width; x++)
                {
                    var srcX = Mathf.Min(source.Width - 1, (int)((x + 0.5f) / width * source.Width));
                    if (selected[srcY * source.Width + srcX]) Apply(data, y * width + x, value, clip);
                }
            }

            return true;
        }
    }

    /// <summary>元テクスチャの画素ごとの OKLab とアルファ。色の近似選択に使う。</summary>
    internal sealed class SourcePixels
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        private float[] _l;
        private float[] _a;
        private float[] _b;
        private byte[] _alpha;

        private static Texture s_texture;
        private static Hash128 s_hash;
        private static SourcePixels s_cached;

        /// <summary>大きなテクスチャだと数百 MB になるので、マスクエディタを閉じたら手放す。</summary>
        public static void ClearCache()
        {
            s_cached = null;
            s_texture = null;
            s_hash = default;
        }

        public static SourcePixels Get(Texture texture)
        {
            if (texture == null) return null;
            var hash = texture.imageContentsHash;
            if (s_cached != null && s_texture == texture && s_hash == hash) return s_cached;

            s_cached = Read(texture);
            s_texture = texture;
            s_hash = hash;
            return s_cached;
        }

        /// <summary>texture を読み込み済みならそれを返す。読み込みはしない(ホバー中の表示から呼ぶため)。</summary>
        public static SourcePixels Peek(Texture texture)
        {
            if (texture == null || s_cached == null || s_texture != texture) return null;
            return s_hash == texture.imageContentsHash ? s_cached : null;
        }

        private static SourcePixels Read(Texture texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            try
            {
                RecolorCore.BlitOklab(texture, rt);
                var readback = RecolorCore.ReadBack(rt, TextureFormat.RGBAFloat, false, true);
                var pixels = readback.GetPixelData<Color>(0);
                var result = new SourcePixels
                {
                    Width = texture.width,
                    Height = texture.height,
                    _l = new float[pixels.Length],
                    _a = new float[pixels.Length],
                    _b = new float[pixels.Length],
                    _alpha = new byte[pixels.Length],
                };
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    result._l[i] = p.r;
                    result._a[i] = p.g;
                    result._b[i] = p.b;
                    result._alpha[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(p.a * 255f), 0, 255);
                }

                Object.DestroyImmediate(readback);
                return result;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public bool[] Similar(int x, int y, float tolerance, bool contiguous)
        {
            var count = Width * Height;
            var seed = y * Width + x;
            if (_alpha[seed] < 128) return null;

            var rl = _l[seed];
            var ra = _a[seed];
            var rb = _b[seed];
            var tol2 = tolerance * tolerance;

            bool Match(int i)
            {
                if (_alpha[i] < 128) return false;
                var dl = _l[i] - rl;
                var da = _a[i] - ra;
                var db = _b[i] - rb;
                return dl * dl + da * da + db * db <= tol2;
            }

            var selected = new bool[count];
            if (!contiguous)
            {
                for (var i = 0; i < count; i++) selected[i] = Match(i);
                return selected;
            }

            var stack = new Stack<int>();
            stack.Push(seed);
            selected[seed] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var px = i % Width;
                var py = i / Width;
                if (px > 0) Visit(i - 1);
                if (px < Width - 1) Visit(i + 1);
                if (py > 0) Visit(i - Width);
                if (py < Height - 1) Visit(i + Width);
            }

            return selected;

            void Visit(int n)
            {
                if (selected[n] || !Match(n)) return;
                selected[n] = true;
                stack.Push(n);
            }
        }
    }
}
