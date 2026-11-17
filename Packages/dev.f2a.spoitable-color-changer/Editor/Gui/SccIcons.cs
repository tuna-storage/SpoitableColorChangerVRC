using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    internal enum SccIcon
    {
        RectSelect,
        Lasso,
        PenSelect,
        ColorSelect,
        UvIsland,
        Pen,
        Brush,
        Eraser,
        Bucket,
        Undo,
        Redo,
        Gradient,
        ColorPath,
    }

    /// <summary>
    /// 道具のアイコン。画像を同梱せず、16 × 16 の升目で決めた線をその場で描く(拡大率やテーマに合わせて線の色を変えられる)。
    /// Repaint のときだけ描く。
    /// </summary>
    internal static class SccIcons
    {
        private static readonly List<Vector3> Points = new List<Vector3>();
        private static Vector2 s_origin;
        private static float s_unit;
        private static Color s_color;

        public static void Draw(SccIcon icon, Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var size = Mathf.Min(rect.width, rect.height);
            s_unit = size / 16f;
            s_origin = new Vector2(rect.center.x - size * 0.5f, rect.center.y - size * 0.5f);
            s_color = color;
            var previous = Handles.color;

            switch (icon)
            {
                case SccIcon.RectSelect:
                    Dashed(true, P(2.5f, 3.5f), P(13.5f, 3.5f), P(13.5f, 12.5f), P(2.5f, 12.5f));
                    break;

                case SccIcon.Lasso:
                    Dashed(true, Ellipse(8.5f, 6.5f, 5.5f, 3.8f, 0f, 360f, 26));
                    Line(1.3f, P(4.4f, 9.3f), P(3.6f, 11.2f), P(4.9f, 12.8f), P(4.1f, 14.6f));
                    break;

                case SccIcon.PenSelect:
                    // ペンと、その後ろの点線(なぞった跡が選択範囲になる)
                    Line(1.3f, P(5.4f, 10.6f), P(6.3f, 8.1f), P(11.8f, 2.6f), P(13.4f, 4.2f), P(7.9f, 9.7f),
                        P(5.4f, 10.6f));
                    Line(1.1f, P(10.6f, 3.8f), P(12.2f, 5.4f));
                    Dashed(false, P(1.2f, 15f), P(2.2f, 12.2f), P(4.6f, 13.6f), P(5.4f, 11.6f));
                    break;

                case SccIcon.ColorSelect:
                    Line(2.2f, P(2.5f, 13.5f), P(9.2f, 6.8f));
                    Line(1.2f, P(12f, 1.2f), P(12f, 6.8f));
                    Line(1.2f, P(9.2f, 4f), P(14.8f, 4f));
                    Line(1f, P(10.3f, 2.3f), P(13.7f, 5.7f));
                    Line(1f, P(13.7f, 2.3f), P(10.3f, 5.7f));
                    break;

                case SccIcon.UvIsland:
                {
                    var outline = new[] { P(2f, 6f), P(6.5f, 2f), P(13.5f, 3.5f), P(14f, 11f), P(8f, 14f), P(2.5f, 11.5f) };
                    var center = P(8f, 8f);
                    s_color = new Color(color.r, color.g, color.b, color.a * 0.55f);
                    foreach (var v in outline) Line(0.9f, center, v);
                    s_color = color;
                    Line(1.4f, Closed(outline));
                    break;
                }

                case SccIcon.Pen:
                    Line(1.3f, P(2.2f, 13.8f), P(3.4f, 10.4f), P(10.8f, 3f), P(13f, 5.2f), P(5.6f, 12.6f), P(2.2f, 13.8f));
                    Line(1.1f, P(9.6f, 4.2f), P(11.8f, 6.4f));
                    Line(1.1f, P(3.4f, 10.4f), P(5.6f, 12.6f));
                    break;

                case SccIcon.Brush:
                    Line(2f, P(14f, 2f), P(9.4f, 6.6f));
                    Fill(P(8.4f, 6.1f), P(9.9f, 7.6f), P(8.7f, 10.9f), P(5.6f, 13.1f), P(2f, 14f), P(3.1f, 10.6f),
                        P(5.4f, 7.7f));
                    break;

                case SccIcon.Eraser:
                    Line(1.3f, P(2.5f, 10.5f), P(8.5f, 4.5f), P(13.5f, 9.5f), P(7.5f, 15.5f), P(2.5f, 10.5f));
                    Fill(P(2.5f, 10.5f), P(5.5f, 7.5f), P(10.5f, 12.5f), P(7.5f, 15.5f));
                    break;

                case SccIcon.Bucket:
                    Line(1.3f, P(2.5f, 8f), P(7.5f, 3f), P(12.5f, 8f), P(7.5f, 13f), P(2.5f, 8f));
                    Fill(P(2.5f, 8f), P(12.5f, 8f), P(7.5f, 13f));
                    Line(1.1f, P(12.5f, 8f), P(13.6f, 10.6f));
                    Fill(Ellipse(13.6f, 12.2f, 1.4f, 1.4f, 0f, 360f, 12));
                    break;

                case SccIcon.Gradient:
                {
                    // 枠の中を、左から右へ濃くなる縦線で埋め、始点と終点の印を置く
                    Line(1.2f, P(2f, 3f), P(14f, 3f), P(14f, 13f), P(2f, 13f), P(2f, 3f));
                    for (var i = 0; i < 5; i++)
                    {
                        var x = 4.5f + i * 2f;
                        s_color = new Color(color.r, color.g, color.b, color.a * (0.15f + 0.2f * i));
                        Line(1.6f, P(x, 4.5f), P(x, 11.5f));
                    }

                    s_color = color;
                    break;
                }

                case SccIcon.ColorPath:
                {
                    // 折れ線と、その上の点(点を結んだ線に沿って色が並ぶ)
                    var a = P(2.5f, 12.5f);
                    var b = P(6.5f, 4.5f);
                    var c = P(13.5f, 9.5f);
                    Line(1.2f, a, b, c);
                    Fill(Ellipse(2.5f, 12.5f, 2f, 2f, 0f, 360f, 12));
                    Fill(Ellipse(6.5f, 4.5f, 2f, 2f, 0f, 360f, 12));
                    Fill(Ellipse(13.5f, 9.5f, 2f, 2f, 0f, 360f, 12));
                    break;
                }

                case SccIcon.Undo:
                    Arrow(false);
                    break;

                case SccIcon.Redo:
                    Arrow(true);
                    break;
            }

            Handles.color = previous;
        }

        /// <summary>取り消し・やり直しの曲がった矢印。mirror でやり直し(左右反転)。</summary>
        private static void Arrow(bool mirror)
        {
            var arc = Ellipse(8f, 9f, 5f, 5f, 180f, -30f, 16);
            if (mirror)
            {
                for (var i = 0; i < arc.Length; i++) arc[i] = MirrorX(arc[i]);
            }

            Line(1.5f, arc);
            var tip = arc[0];
            var s = s_unit;
            var dir = mirror ? -1f : 1f;
            Line(1.5f, new Vector3(tip.x - 2.4f * s * dir, tip.y - 2f * s), new Vector3(tip.x, tip.y + 0.6f * s),
                new Vector3(tip.x + 2.4f * s * dir, tip.y - 2f * s));
        }

        private static Vector3 MirrorX(Vector3 p)
        {
            var center = s_origin.x + 8f * s_unit;
            return new Vector3(2f * center - p.x, p.y, 0f);
        }

        private static Vector3 P(float x, float y)
        {
            return new Vector3(s_origin.x + x * s_unit, s_origin.y + y * s_unit, 0f);
        }

        private static Vector3[] Closed(Vector3[] points)
        {
            var result = new Vector3[points.Length + 1];
            points.CopyTo(result, 0);
            result[points.Length] = points[0];
            return result;
        }

        /// <summary>楕円の弧(角度は度、0 が右、反時計回り。画面は y が下向きなので上へ回る)。</summary>
        private static Vector3[] Ellipse(float cx, float cy, float rx, float ry, float from, float to, int segments)
        {
            var result = new Vector3[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var a = Mathf.Lerp(from, to, (float)i / segments) * Mathf.Deg2Rad;
                result[i] = P(cx + rx * Mathf.Cos(a), cy - ry * Mathf.Sin(a));
            }

            return result;
        }

        private static void Line(float width, params Vector3[] points)
        {
            Handles.color = s_color;
            // 細すぎると暗い背景で読めないので、升目の幅より少し太らせる
            Handles.DrawAAPolyLine(Mathf.Max(1.2f, width * s_unit * 1.25f), points);
        }

        private static void Fill(params Vector3[] points)
        {
            Handles.color = s_color;
            Handles.DrawAAConvexPolygon(points);
        }

        /// <summary>点線(選択範囲の枠を表す)。</summary>
        private static void Dashed(bool closed, params Vector3[] points)
        {
            var path = closed ? Closed(points) : points;
            const float dash = 2.2f;
            const float gap = 1.5f;
            var on = true;
            var left = dash * s_unit;
            Points.Clear();
            Points.Add(path[0]);
            for (var i = 0; i + 1 < path.Length; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                var length = Vector3.Distance(a, b);
                var t = 0f;
                while (length - t > left)
                {
                    t += left;
                    var p = Vector3.Lerp(a, b, t / length);
                    if (on)
                    {
                        Points.Add(p);
                        Line(1.3f, Points.ToArray());
                    }

                    Points.Clear();
                    Points.Add(p);
                    on = !on;
                    left = (on ? dash : gap) * s_unit;
                }

                left -= length - t;
                Points.Add(b);
            }

            if (on && Points.Count > 1) Line(1.3f, Points.ToArray());
        }
    }
}
