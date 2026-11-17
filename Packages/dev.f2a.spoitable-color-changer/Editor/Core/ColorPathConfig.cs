using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 色の経路(位置で色を並べる設定)のスナップショット。プレビューの変更検知とキャッシュのキーに使う。
    /// 経路の計算(弧長の比、Relative のストップの置き直し)もここにまとめる(描画とエディタの表示で同じ式を使うため)。
    /// </summary>
    internal sealed class ColorPathConfig : IEquatable<ColorPathConfig>
    {
        public static readonly ColorPathConfig None = new ColorPathConfig(null);

        public readonly ColorPathMode Mode;
        public readonly ColorPathSpace Space;

        /// <summary>線ごとの点の位置(lines の順。点が 2 個未満の線もそのまま含む)。テクスチャ上は xy が UV。</summary>
        public readonly Vector3[][] Lines;

        /// <summary>
        /// 線ごとの点のテクスチャ上の位置(テクスチャ上は位置の xy、モデル上は拾っておいた UV)。分からなければ (-1, -1)。
        /// 塗りマスクのどの塊に点が乗っているかの判定に使う。
        /// </summary>
        public readonly Vector2[][] Uvs;

        public readonly float Shading;

        private ColorPathConfig(ColorPath p)
        {
            Mode = p?.mode ?? ColorPathMode.Luminance;
            Space = p?.space ?? ColorPathSpace.Model;
            var lines = p?.lines?.Take(ColorPath.MaxLines).Select(l => l?.points ?? new List<ColorPathPoint>())
                .Select(points => points.Take(ColorPath.MaxPoints).ToArray())
                .ToArray() ?? Array.Empty<ColorPathPoint[]>();
            Lines = lines.Select(l => l.Select(q => q.position).ToArray()).ToArray();
            Uvs = lines.Select(l => l.Select(q => Space == ColorPathSpace.Texture ? (Vector2)q.position : q.uv).ToArray())
                .ToArray();
            Shading = Mathf.Clamp01(p?.shading ?? 1f);
        }

        public static ColorPathConfig Capture(ColorPath p)
        {
            return p == null || p.mode == ColorPathMode.Luminance ? None : new ColorPathConfig(p);
        }

        /// <summary>点が 2 個以上ある線(色を決めるのに使う線)の番号。</summary>
        public IEnumerable<int> ActiveLines => Enumerable.Range(0, Lines.Length).Where(i => Lines[i].Length >= 2);

        public int ActiveLineCount => Lines.Count(l => l.Length >= 2);

        /// <summary>経路で色を決める(点が 2 個以上ある線がある)。そうでなければ明るさで決める。</summary>
        public bool IsActive => Mode != ColorPathMode.Luminance && Lines.Any(l => l.Length >= 2);

        /// <summary>モデル上の位置を使う(位置マップが要る)。</summary>
        public bool UsesModel => IsActive && Space == ColorPathSpace.Model;

        public bool IsRelative => IsActive && Mode == ColorPathMode.Relative;

        /// <summary>線 line の各点までの弧長の比。size はテクスチャの画素数(テクスチャ上は画素で測るので)。</summary>
        public float[] Ratios(int line, Vector2 size)
        {
            return Ratios(Lines[line], Space, size);
        }

        /// <summary>
        /// 各点までの弧長の比(0〜1、先頭は 0、末尾は 1)。テクスチャ上は縦横比どおりに画素で測る。
        /// 全長がほぼ 0(点が重なっている)なら等間隔にする。
        /// </summary>
        public static float[] Ratios(IReadOnlyList<Vector3> points, ColorPathSpace space, Vector2 size)
        {
            var n = points?.Count ?? 0;
            var result = new float[n];
            if (n < 2) return result;
            var lengths = ArcLengths(points, space, size);
            var total = lengths[n - 1];
            for (var i = 0; i < n; i++)
            {
                result[i] = total > 1e-6f ? lengths[i] / total : (float)i / (n - 1);
            }

            result[n - 1] = 1f;
            return result;
        }

        /// <summary>先頭から各点までの長さ(距離を測る座標で)。</summary>
        public static float[] ArcLengths(IReadOnlyList<Vector3> points, ColorPathSpace space, Vector2 size)
        {
            var n = points?.Count ?? 0;
            var lengths = new float[n];
            for (var i = 1; i < n; i++)
            {
                lengths[i] = lengths[i - 1] + Vector3.Distance(Metric(points[i - 1], space, size),
                    Metric(points[i], space, size));
            }

            return lengths;
        }

        /// <summary>
        /// 位置 p が経路の上のどこ(0〜1)にあたるか。シェーダーの PathPosition と同じく、いちばん近い線分に射影し、
        /// 両端の点の弧長の比を補間する。点が 2 個未満なら 0。
        /// </summary>
        public static float ProjectT(IReadOnlyList<Vector3> points, ColorPathSpace space, Vector2 size, Vector3 p)
        {
            var n = points?.Count ?? 0;
            if (n < 2) return 0f;
            var ratios = Ratios(points, space, size);
            var segment = NearestSegment(points, space, size, p, out var s);
            return Mathf.Clamp01(Mathf.Lerp(ratios[segment], ratios[segment + 1], s));
        }

        /// <summary>p にいちばん近い線分の番号と、その線分の上の位置(0〜1)。点が 2 個未満なら -1。</summary>
        public static int NearestSegment(IReadOnlyList<Vector3> points, ColorPathSpace space, Vector2 size, Vector3 p,
            out float along)
        {
            along = 0f;
            var n = points?.Count ?? 0;
            if (n < 2) return -1;
            var q = Metric(p, space, size);
            var best = float.MaxValue;
            var result = -1;
            for (var i = 0; i + 1 < n; i++)
            {
                var a = Metric(points[i], space, size);
                var d = Metric(points[i + 1], space, size) - a;
                var s = Mathf.Clamp01(Vector3.Dot(q - a, d) / Mathf.Max(Vector3.Dot(d, d), 1e-12f));
                var distance = (q - (a + d * s)).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                result = i;
                along = s;
            }

            return result;
        }

        /// <summary>距離を測る座標(テクスチャ上は画素、モデル上はそのまま)。</summary>
        public static Vector3 Metric(Vector3 p, ColorPathSpace space, Vector2 size)
        {
            return space == ColorPathSpace.Texture ? new Vector3(p.x * size.x, p.y * size.y, 0f) : p;
        }

        /// <summary>ストップの番号を位置の順に並べたもの(同じ位置なら番号の順)。Relative で点との対応に使う。</summary>
        public static int[] RankOrder(IReadOnlyList<float> positions)
        {
            return Enumerable.Range(0, positions.Count).OrderBy(i => positions[i]).ThenBy(i => i).ToArray();
        }

        /// <summary>
        /// Relative のストップ: ストップを位置の順に並べて、k 番目を k 番目の点の比の位置に置いたもの。
        /// 数が合わなければ null(呼ぶ側は元のストップのまま描く)。
        /// </summary>
        public static (float position, Color color)[] RelativeStops(IReadOnlyList<(float position, Color color)> stops,
            float[] ratios)
        {
            if (stops == null || ratios == null || stops.Count != ratios.Length || stops.Count == 0) return null;
            var order = RankOrder(stops.Select(s => s.position).ToList());
            var result = new (float position, Color color)[stops.Count];
            for (var k = 0; k < order.Length; k++) result[k] = (ratios[k], stops[order[k]].color);
            return result;
        }

        /// <summary>Relative で、線 line の点の数がストップの数と合っている(その線は点の比の位置にストップを置いて描く)。</summary>
        public bool LineMatches(int line, int stopCount)
        {
            return IsRelative && line >= 0 && line < Lines.Length && Lines[line].Length >= 2 &&
                   Lines[line].Length == stopCount;
        }

        /// <summary>
        /// 線 line を描くときのストップ。Relative で数が合えば点の比の位置に置き直したもの、それ以外は null
        /// (レイヤーのストップのまま描く)。プリセットの値は書き換えない。
        /// </summary>
        public (float position, Color color)[] LineStops(int line, (float position, Color color)[] stops, Vector2 size)
        {
            return LineMatches(line, stops?.Length ?? 0) ? RelativeStops(stops, Ratios(line, size)) : null;
        }

        /// <summary>Relative なのに、点の数がストップと合っていない線(Normal として描いている)。</summary>
        public int[] MismatchedLines(int stopCount)
        {
            return IsRelative ? ActiveLines.Where(i => Lines[i].Length != stopCount).ToArray() : Array.Empty<int>();
        }

        public bool Equals(ColorPathConfig other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (Mode != other.Mode) return false;
            if (Mode == ColorPathMode.Luminance) return true;
            if (Space != other.Space || !Shading.Equals(other.Shading) || Lines.Length != other.Lines.Length) return false;
            for (var i = 0; i < Lines.Length; i++)
            {
                if (!Lines[i].SequenceEqual(other.Lines[i]) || !Uvs[i].SequenceEqual(other.Uvs[i])) return false;
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is ColorPathConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            if (Mode == ColorPathMode.Luminance) return 0;
            var h = HashCode.Combine((int)Mode, (int)Space, Shading);
            for (var i = 0; i < Lines.Length; i++)
            {
                h = HashCode.Combine(h, Lines[i].Length);
                foreach (var p in Lines[i]) h = HashCode.Combine(h, p);
                foreach (var uv in Uvs[i]) h = HashCode.Combine(h, uv);
            }

            return h;
        }
    }
}
