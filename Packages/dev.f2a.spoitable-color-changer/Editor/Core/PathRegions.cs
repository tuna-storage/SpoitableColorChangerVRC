using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 色の経路の線を、塗りマスクのつながった塊ごとに分けるための表。
    /// 画素ごとに「その塊で使う線」のビット(線 i が 1 &lt;&lt; i)を R8 で持つ。0 ならすべての線を使う
    /// (塗っていない画素と、点が 1 つも乗っていない塊)。
    /// </summary>
    internal sealed class PathRegions
    {
        /// <summary>表の大きさの上限(これより大きい塗りマスクは、ブロックごとにまとめてから塊を調べる)。</summary>
        private const int MaxSize = 1024;

        private const int CacheSize = 8;

        private static readonly List<(string key, PathRegions regions)> Cache = new List<(string, PathRegions)>();

        public Texture2D Map { get; private set; }

        /// <summary>テクスチャの UV から表の UV への倍率(ブロックにまとめた分の端数を合わせる)。</summary>
        public Vector2 UvScale { get; private set; }

        /// <summary>見つかった塊の数(確かめる用)。</summary>
        public int RegionCount { get; private set; }

        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static void Clear()
        {
            foreach (var (_, regions) in Cache) regions.Destroy();
            Cache.Clear();
        }

        private void Destroy()
        {
            if (Map != null) Object.DestroyImmediate(Map);
            Map = null;
        }

        /// <summary>
        /// 塊ごとに使う線の表。使う線が 2 本未満か、塗りマスクがなければ null(すべての線を使う)。
        /// 編集中の塗りマスクは確定した中身(ストロークの前)で作る。塗っている間は作り直さず(1 回 30ms ほどかかる)、
        /// ストロークの確定と取り消しのたびに作り直す。
        /// </summary>
        public static PathRegions Get(Texture paint, ColorPathConfig path)
        {
            if (paint == null || path == null || !path.IsActive || path.ActiveLineCount < 2) return null;
            var key = KeyOf(paint, path);
            for (var i = 0; i < Cache.Count; i++)
            {
                if (Cache[i].key != key) continue;
                var hit = Cache[i];
                if (hit.regions.Map == null)
                {
                    Cache.RemoveAt(i);
                    break;
                }

                Cache.RemoveAt(i);
                Cache.Insert(0, hit);
                return hit.regions;
            }

            var regions = Build(paint, path);
            if (regions == null) return null;
            Cache.Insert(0, (key, regions));
            while (Cache.Count > CacheSize)
            {
                Cache[Cache.Count - 1].regions.Destroy();
                Cache.RemoveAt(Cache.Count - 1);
            }

            return regions;
        }

        /// <summary>
        /// キャッシュのキー。編集中の塗りマスクは確定した中身の版、保存済みのアセットは中身のハッシュで変化を見る
        /// (外で塗り直して読み込み直されたアセットにも気づけるように)。
        /// </summary>
        private static string KeyOf(Texture paint, ColorPathConfig path)
        {
            var sb = new StringBuilder();
            var committed = PaintSession.CommittedVersion(paint);
            sb.Append(paint.GetInstanceID()).Append(':')
                .Append(committed >= 0 ? "s" + committed : paint.imageContentsHash.ToString()).Append(':')
                .Append(paint.width).Append('x').Append(paint.height).Append(':').Append((int)path.Space);
            for (var line = 0; line < path.Lines.Length; line++)
            {
                sb.Append('|');
                if (path.Lines[line].Length < 2) continue;
                foreach (var uv in path.Uvs[line])
                {
                    sb.Append(uv.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(uv.y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }
            }

            return sb.ToString();
        }

        private static PathRegions Build(Texture paint, ColorPathConfig path)
        {
            var data = PaintSession.ReadCommitted(paint, out var width, out var height);
            if (data == null || width <= 0 || height <= 0 || data.Length < width * height) return null;
            var labels = Label(data, width, height, out var cw, out var ch, out var count);
            var bits = new int[count + 1];
            var radius = Mathf.Max(2, Mathf.RoundToInt(Mathf.Max(cw, ch) / 128f));

            for (var line = 0; line < path.Lines.Length && line < ColorPath.MaxLines; line++)
            {
                var uvs = path.Uvs[line];
                if (path.Lines[line].Length < 2) continue;
                var bit = 1 << line;
                foreach (var uv in uvs)
                {
                    // 点がちょうど塊の縁の外に置かれることもあるので、少し離れた塊まで探す
                    var cell = NearestPainted(labels, cw, ch, uv, radius);
                    if (cell >= 0) bits[labels[cell]] |= bit;
                }

                if (path.Space != ColorPathSpace.Texture) continue;
                // テクスチャ上は線分も UV 上の直線なので、線が横切る塊にも使う(端点を塊の外に置いても効くように)
                for (var k = 0; k + 1 < uvs.Length; k++)
                {
                    var a = new Vector2(uvs[k].x * cw, uvs[k].y * ch);
                    var b = new Vector2(uvs[k + 1].x * cw, uvs[k + 1].y * ch);
                    var steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a, b)), 1, 4096);
                    for (var s = 0; s <= steps; s++)
                    {
                        var p = Vector2.Lerp(a, b, (float)s / steps);
                        var x = Mathf.FloorToInt(p.x);
                        var y = Mathf.FloorToInt(p.y);
                        if (x < 0 || y < 0 || x >= cw || y >= ch) continue;
                        var label = labels[y * cw + x];
                        if (label > 0) bits[label] |= bit;
                    }
                }
            }

            var pixels = new byte[cw * ch];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = labels[i] > 0 ? (byte)bits[labels[i]] : (byte)0;
            var texture = new Texture2D(cw, ch, TextureFormat.R8, false, true)
            {
                name = "SpoitableColorChanger Path Regions",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);

            var block = BlockSize(width, height);
            return new PathRegions
            {
                Map = texture,
                UvScale = new Vector2(width / (float)(block * cw), height / (float)(block * ch)),
                RegionCount = count,
            };
        }

        private static int BlockSize(int width, int height)
        {
            return Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(width, height) / (float)MaxSize));
        }

        /// <summary>
        /// 塗った画素(値が 0 より大きい)を 8 方向のつながりで塊に分ける。大きいマスクはブロックにまとめてから調べる
        /// (ブロックの中に 1 画素でも塗ってあれば塗ったブロック)。返す配列は塊の番号(1〜、塗っていなければ 0)。
        /// </summary>
        internal static int[] Label(byte[] data, int width, int height, out int cw, out int ch, out int count)
        {
            var block = BlockSize(width, height);
            cw = (width + block - 1) / block;
            ch = (height + block - 1) / block;
            // 塗ったブロックは -1(まだ番号がない)、塗っていないブロックは 0
            var labels = new int[cw * ch];
            var cellOfX = new int[width];
            for (var x = 0; x < width; x++) cellOfX[x] = x / block;
            for (var y = 0; y < height; y++)
            {
                var row = y / block * cw;
                var offset = y * width;
                for (var x = 0; x < width; x++)
                {
                    if (data[offset + x] != 0) labels[row + cellOfX[x]] = -1;
                }
            }

            var stack = new int[cw * ch];
            count = 0;
            for (var start = 0; start < labels.Length; start++)
            {
                if (labels[start] != -1) continue;
                count++;
                var top = 0;
                stack[top++] = start;
                labels[start] = count;
                while (top > 0)
                {
                    var i = stack[--top];
                    var x = i % cw;
                    var y = i / cw;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var ny = y + dy;
                        if (ny < 0 || ny >= ch) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            if (nx < 0 || nx >= cw) continue;
                            var n = ny * cw + nx;
                            if (labels[n] != -1) continue;
                            labels[n] = count;
                            stack[top++] = n;
                        }
                    }
                }
            }

            return labels;
        }

        /// <summary>uv(0〜1)にいちばん近い塗ったブロック(radius ブロック以内)。分からない UV・見つからなければ -1。</summary>
        private static int NearestPainted(int[] labels, int cw, int ch, Vector2 uv, int radius)
        {
            if (uv.x < 0f || uv.y < 0f || float.IsNaN(uv.x) || float.IsNaN(uv.y)) return -1;
            var cx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.x) * cw), 0, cw - 1);
            var cy = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.y) * ch), 0, ch - 1);
            if (labels[cy * cw + cx] > 0) return cy * cw + cx;
            var best = -1;
            var bestDistance = int.MaxValue;
            for (var y = Mathf.Max(0, cy - radius); y <= Mathf.Min(ch - 1, cy + radius); y++)
            {
                for (var x = Mathf.Max(0, cx - radius); x <= Mathf.Min(cw - 1, cx + radius); x++)
                {
                    if (labels[y * cw + x] == 0) continue;
                    var d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d > radius * radius || d >= bestDistance) continue;
                    bestDistance = d;
                    best = y * cw + x;
                }
            }

            return best;
        }
    }
}
