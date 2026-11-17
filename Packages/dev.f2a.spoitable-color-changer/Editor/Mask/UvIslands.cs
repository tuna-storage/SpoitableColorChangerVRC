using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// UV アイランドを求める元になる、メッシュとサブメッシュの組。
    /// St はマテリアルのテクスチャの Tiling(xy)と Offset(zw)。UV に掛けるとテクスチャ上の位置になる。
    /// </summary>
    internal readonly struct UvSource : IEquatable<UvSource>
    {
        public readonly Mesh Mesh;
        public readonly int SubMesh;
        public readonly Vector4 St;

        public UvSource(Mesh mesh, int subMesh, Vector4 st)
        {
            Mesh = mesh;
            SubMesh = subMesh;
            St = st;
        }

        public bool Equals(UvSource other)
        {
            return ReferenceEquals(Mesh, other.Mesh) && SubMesh == other.SubMesh && St.Equals(other.St);
        }

        public override bool Equals(object obj)
        {
            return obj is UvSource other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ((((Mesh != null ? Mesh.GetInstanceID() : 0) * 397) ^ SubMesh) * 397) ^ St.GetHashCode();
        }
    }

    /// <summary>
    /// メッシュの UV0 から求めた UV アイランド。クリックした位置のアイランドを探し、マスクの画素に塗る。
    /// 頂点は (位置, UV) が一致するものを同じとみなす。法線の分割などで複製された頂点をつなぎ、
    /// UV 上で接しているだけの別のアイランドはつながない。
    /// </summary>
    internal sealed class UvIslands
    {
        public int SourceCount { get; private set; }
        public int IslandCount { get; private set; }
        public int TriangleCount => _island.Length;

        /// <summary>三角形ごとに 3 点の UV。</summary>
        private Vector2[] _uv;

        /// <summary>三角形ごとのアイランド番号。</summary>
        private int[] _island;

        /// <summary>アイランドの輪郭の線分(2 点で 1 本)。アイランドの番号順に並ぶ。</summary>
        public Vector2[] Outline { get; private set; }

        /// <summary>アイランド i の輪郭は Outline の [_outlineStart[i], _outlineStart[i + 1])。</summary>
        private int[] _outlineStart;

        /// <summary>すべての辺の線分(2 点で 1 本、重複なし)。</summary>
        public Vector2[] Edges { get; private set; }

        // クリック判定用の格子。セルごとに、そのセルに掛かる三角形を持つ
        private Vector2 _gridMin;
        private Vector2 _gridCell;
        private int _gridW;
        private int _gridH;
        private int[] _cellStart;
        private int[] _cellTris;

        private static readonly List<UvSource> s_key = new List<UvSource>();
        private static readonly List<int> s_keyVertexCounts = new List<int>();
        private static UvIslands s_cached;
        private static string s_error;

        /// <summary>
        /// sources のアイランドを返す。同じ組み合わせなら前回の結果を使い回す。
        /// 読めなかったときは null と、その理由を返す。
        /// </summary>
        public static UvIslands Get(List<UvSource> sources, out string error)
        {
            if (!SameKey(sources))
            {
                s_key.Clear();
                s_keyVertexCounts.Clear();
                foreach (var s in sources)
                {
                    s_key.Add(s);
                    s_keyVertexCounts.Add(s.Mesh != null ? s.Mesh.vertexCount : 0);
                }

                try
                {
                    s_cached = Build(sources);
                    s_error = null;
                }
                catch (Exception e)
                {
                    // 毎フレーム同じ例外を出さないよう、失敗も覚えておく
                    Debug.LogException(e);
                    s_cached = null;
                    s_error = "メッシュの UV を読めませんでした: " + e.Message;
                }
            }

            error = s_error;
            return s_cached;
        }

        [InitializeOnLoadMethod]
        private static void Init()
        {
            // メッシュを UV だけ直して再インポートすると、頂点数が同じでキャッシュのキーでは気づけないので、
            // アセットが変わったら捨てる(作り直しは数十 ms)
            EditorApplication.projectChanged += ClearCache;
        }

        public static void ClearCache()
        {
            s_key.Clear();
            s_keyVertexCounts.Clear();
            s_cached = null;
            s_error = null;
        }

        private static bool SameKey(List<UvSource> sources)
        {
            if (sources.Count != s_key.Count) return false;
            for (var i = 0; i < sources.Count; i++)
            {
                if (!sources[i].Equals(s_key[i])) return false;
                // 再インポートで中身が変わったときに気づけるよう、頂点数も比べる
                var count = sources[i].Mesh != null ? sources[i].Mesh.vertexCount : 0;
                if (count != s_keyVertexCounts[i]) return false;
            }

            return true;
        }

        private readonly struct WeldKey : IEquatable<WeldKey>
        {
            private readonly Vector3 _position;
            private readonly Vector2 _uv;

            public WeldKey(Vector3 position, Vector2 uv)
            {
                _position = position;
                _uv = uv;
            }

            public bool Equals(WeldKey other)
            {
                return _position.Equals(other._position) && _uv.Equals(other._uv);
            }

            public override bool Equals(object obj)
            {
                return obj is WeldKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = (uint)_position.x.GetHashCode();
                    h = h * 0x9E3779B1u ^ (uint)_position.y.GetHashCode();
                    h = h * 0x9E3779B1u ^ (uint)_position.z.GetHashCode();
                    h = h * 0x9E3779B1u ^ (uint)_uv.x.GetHashCode();
                    h = h * 0x9E3779B1u ^ (uint)_uv.y.GetHashCode();
                    return (int)(h ^ (h >> 15));
                }
            }
        }

        public static UvIslands Build(IReadOnlyList<UvSource> sources)
        {
            // 同じメッシュの複数のサブメッシュは頂点を共有するので、メッシュ(と Tiling/Offset)ごとにまとめて溶接する
            var subMeshes = new Dictionary<(Mesh mesh, Vector4 st), List<int>>();
            var groups = new List<(Mesh mesh, Vector4 st)>();
            foreach (var s in sources)
            {
                if (s.Mesh == null) continue;
                var group = (s.Mesh, s.St);
                if (!subMeshes.TryGetValue(group, out var list))
                {
                    list = new List<int>();
                    subMeshes[group] = list;
                    groups.Add(group);
                }

                if (!list.Contains(s.SubMesh)) list.Add(s.SubMesh);
            }

            var triVerts = new List<int>(); // 溶接後の頂点番号(三角形ごとに 3 つ)
            var weldUv = new List<Vector2>(); // 溶接後の頂点の UV
            var uvs = new List<Vector2>();
            var positions = new List<Vector3>();
            var indices = new List<int>();
            var used = 0;

            foreach (var group in groups)
            {
                var mesh = group.mesh;
                uvs.Clear();
                mesh.GetUVs(0, uvs);
                if (uvs.Count == 0 || uvs.Count != mesh.vertexCount) continue;
                var st = group.st;
                for (var i = 0; i < uvs.Count; i++)
                {
                    uvs[i] = new Vector2(uvs[i].x * st.x + st.z, uvs[i].y * st.y + st.w);
                }

                positions.Clear();
                mesh.GetVertices(positions);

                var weld = new Dictionary<WeldKey, int>();
                var map = new int[uvs.Count];
                for (var i = 0; i < map.Length; i++) map[i] = -1;

                int Weld(int v)
                {
                    var id = map[v];
                    if (id >= 0) return id;
                    var key = new WeldKey(positions[v], uvs[v]);
                    if (!weld.TryGetValue(key, out id))
                    {
                        id = weldUv.Count;
                        weldUv.Add(uvs[v]);
                        weld[key] = id;
                    }

                    map[v] = id;
                    return id;
                }

                void AddTriangle(int a, int b, int c)
                {
                    if (a < 0 || b < 0 || c < 0 || a >= map.Length || b >= map.Length || c >= map.Length) return;
                    triVerts.Add(Weld(a));
                    triVerts.Add(Weld(b));
                    triVerts.Add(Weld(c));
                }

                var any = false;
                foreach (var sub in subMeshes[group])
                {
                    if (sub < 0 || sub >= mesh.subMeshCount) continue;
                    var topology = mesh.GetTopology(sub);
                    if (topology != MeshTopology.Triangles && topology != MeshTopology.Quads) continue;
                    indices.Clear();
                    mesh.GetIndices(indices, sub);
                    if (topology == MeshTopology.Triangles)
                    {
                        for (var k = 0; k + 2 < indices.Count; k += 3)
                        {
                            AddTriangle(indices[k], indices[k + 1], indices[k + 2]);
                        }
                    }
                    else
                    {
                        for (var k = 0; k + 3 < indices.Count; k += 4)
                        {
                            AddTriangle(indices[k], indices[k + 1], indices[k + 2]);
                            AddTriangle(indices[k], indices[k + 2], indices[k + 3]);
                        }
                    }

                    any = true;
                }

                if (any) used++;
            }

            var result = new UvIslands { SourceCount = used };
            var triCount = triVerts.Count / 3;
            result._uv = new Vector2[triCount * 3];
            for (var i = 0; i < triVerts.Count; i++) result._uv[i] = weldUv[triVerts[i]];

            // 三角形の頂点どうしをつないで、アイランドを求める(union-find)
            var parent = new int[weldUv.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b) parent[a] = b;
            }

            for (var t = 0; t < triCount; t++)
            {
                Union(triVerts[t * 3], triVerts[t * 3 + 1]);
                Union(triVerts[t * 3 + 1], triVerts[t * 3 + 2]);
            }

            var islandOfRoot = new int[weldUv.Count];
            for (var i = 0; i < islandOfRoot.Length; i++) islandOfRoot[i] = -1;
            var islandCount = 0;
            result._island = new int[triCount];
            for (var t = 0; t < triCount; t++)
            {
                var root = Find(triVerts[t * 3]);
                if (islandOfRoot[root] < 0) islandOfRoot[root] = islandCount++;
                result._island[t] = islandOfRoot[root];
            }

            result.IslandCount = islandCount;
            result.BuildEdges(triVerts, weldUv, v => islandOfRoot[Find(v)]);
            result.BuildGrid();
            return result;
        }

        /// <summary>
        /// 辺のキー用。long の既定のハッシュは上位と下位の XOR なので、番号の近い頂点の辺がほぼ同じ値に集まり、
        /// 辞書が極端に遅くなる。掛け算で混ぜてから使う。
        /// </summary>
        private sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();

            public bool Equals(long x, long y)
            {
                return x == y;
            }

            public int GetHashCode(long key)
            {
                unchecked
                {
                    var h = (ulong)key * 0x9E3779B97F4A7C15UL;
                    return (int)(h >> 32) ^ (int)h;
                }
            }
        }

        /// <summary>辺を数え、1 つの三角形にしか使われていない辺をアイランドの輪郭とする。</summary>
        private void BuildEdges(List<int> triVerts, List<Vector2> weldUv, Func<int, int> islandOf)
        {
            var counts = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            var order = new List<long>();
            for (var i = 0; i < triVerts.Count; i += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = triVerts[i + k];
                    var b = triVerts[i + (k + 1) % 3];
                    if (a == b) continue;
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (counts.TryGetValue(key, out var c))
                    {
                        counts[key] = c + 1;
                    }
                    else
                    {
                        counts[key] = 1;
                        order.Add(key);
                    }
                }
            }

            Edges = new Vector2[order.Count * 2];
            var outlineCount = new int[IslandCount + 1];
            for (var i = 0; i < order.Count; i++)
            {
                var key = order[i];
                var a = (int)(key >> 32);
                var b = (int)(key & 0xffffffff);
                Edges[i * 2] = weldUv[a];
                Edges[i * 2 + 1] = weldUv[b];
                if (counts[key] == 1) outlineCount[islandOf(a) + 1] += 2;
            }

            // アイランドの番号順に並べる(数え上げソート)
            _outlineStart = new int[IslandCount + 1];
            for (var i = 0; i < IslandCount; i++) _outlineStart[i + 1] = _outlineStart[i] + outlineCount[i + 1];
            Outline = new Vector2[_outlineStart[IslandCount]];
            var cursor = (int[])_outlineStart.Clone();
            foreach (var key in order)
            {
                if (counts[key] != 1) continue;
                var a = (int)(key >> 32);
                var b = (int)(key & 0xffffffff);
                var island = islandOf(a);
                Outline[cursor[island]++] = weldUv[a];
                Outline[cursor[island]++] = weldUv[b];
            }
        }

        private void BuildGrid()
        {
            var triCount = _island.Length;
            if (triCount == 0)
            {
                _gridW = _gridH = 0;
                _cellStart = new int[1];
                _cellTris = new int[0];
                return;
            }

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in _uv)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            var n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(triCount / 4f)), 1, 256);
            _gridW = n;
            _gridH = n;
            _gridMin = min;
            _gridCell = new Vector2(Mathf.Max(1e-6f, (max.x - min.x) / n), Mathf.Max(1e-6f, (max.y - min.y) / n));

            var counts = new int[n * n + 1];
            for (var t = 0; t < triCount; t++)
            {
                CellRange(t, out var x0, out var y0, out var x1, out var y1);
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    counts[y * n + x + 1]++;
            }

            _cellStart = new int[n * n + 1];
            for (var i = 0; i < n * n; i++) _cellStart[i + 1] = _cellStart[i] + counts[i + 1];
            _cellTris = new int[_cellStart[n * n]];
            var cursor = (int[])_cellStart.Clone();
            for (var t = 0; t < triCount; t++)
            {
                CellRange(t, out var x0, out var y0, out var x1, out var y1);
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    _cellTris[cursor[y * n + x]++] = t;
            }
        }

        private void CellRange(int t, out int x0, out int y0, out int x1, out int y1)
        {
            var a = _uv[t * 3];
            var b = _uv[t * 3 + 1];
            var c = _uv[t * 3 + 2];
            var min = Vector2.Min(a, Vector2.Min(b, c));
            var max = Vector2.Max(a, Vector2.Max(b, c));
            x0 = CellX(min.x);
            x1 = CellX(max.x);
            y0 = CellY(min.y);
            y1 = CellY(max.y);
        }

        private int CellX(float u)
        {
            return Mathf.Clamp(Mathf.FloorToInt((u - _gridMin.x) / _gridCell.x), 0, _gridW - 1);
        }

        private int CellY(float v)
        {
            return Mathf.Clamp(Mathf.FloorToInt((v - _gridMin.y) / _gridCell.y), 0, _gridH - 1);
        }

        /// <summary>uv を含むアイランド。左右対称などで UV が重なっていれば、重なっているものをすべて返す。</summary>
        public List<int> IslandsAt(Vector2 uv)
        {
            var result = new List<int>();
            if (_gridW == 0) return result;
            var fx = (uv.x - _gridMin.x) / _gridCell.x;
            var fy = (uv.y - _gridMin.y) / _gridCell.y;
            if (fx < 0f || fy < 0f || fx > _gridW || fy > _gridH) return result;

            var cell = CellY(uv.y) * _gridW + CellX(uv.x);
            for (var k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
            {
                var t = _cellTris[k];
                var island = _island[t];
                if (result.Contains(island)) continue;
                if (Contains(_uv[t * 3], _uv[t * 3 + 1], _uv[t * 3 + 2], uv)) result.Add(island);
            }

            return result;
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static bool Contains(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            if (Mathf.Abs(Cross(b - a, c - a)) < 1e-12f) return false;
            var d1 = Cross(b - a, p - a);
            var d2 = Cross(c - b, p - b);
            var d3 = Cross(a - c, p - c);
            var negative = d1 < 0f || d2 < 0f || d3 < 0f;
            var positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        /// <summary>アイランド i の輪郭の範囲(Outline の添字)。</summary>
        public void OutlineRange(int island, out int start, out int end)
        {
            start = _outlineStart[island];
            end = _outlineStart[island + 1];
        }

        /// <summary>
        /// islands に含まれる三角形を、width × height のマスクに 255 で塗る(画素の中心が三角形の中にあれば塗る)。
        /// wrap が true の向きは、0〜1 の外の UV を折り返して塗る。
        /// </summary>
        public byte[] Rasterize(ICollection<int> islands, int width, int height, bool wrapU, bool wrapV)
        {
            var mask = new byte[width * height];
            var selected = new bool[IslandCount];
            foreach (var i in islands)
            {
                if (i >= 0 && i < IslandCount) selected[i] = true;
            }

            var scale = new Vector2(width, height);
            for (var t = 0; t < _island.Length; t++)
            {
                if (!selected[_island[t]]) continue;
                FillTriangle(mask, width, height, Vector2.Scale(_uv[t * 3], scale),
                    Vector2.Scale(_uv[t * 3 + 1], scale), Vector2.Scale(_uv[t * 3 + 2], scale), wrapU, wrapV);
            }

            return mask;
        }

        private static void FillTriangle(byte[] mask, int width, int height, Vector2 a, Vector2 b, Vector2 c,
            bool wrapU, bool wrapV)
        {
            void Plot(int x, int y)
            {
                if (wrapU) x = ((x % width) + width) % width;
                else if (x < 0 || x >= width) return;
                if (wrapV) y = ((y % height) + height) % height;
                else if (y < 0 || y >= height) return;
                mask[y * width + x] = 255;
            }

            // 細い三角形が画素の中心にかからなくても消えないよう、頂点の画素は必ず塗る
            Plot(Mathf.FloorToInt(a.x), Mathf.FloorToInt(a.y));
            Plot(Mathf.FloorToInt(b.x), Mathf.FloorToInt(b.y));
            Plot(Mathf.FloorToInt(c.x), Mathf.FloorToInt(c.y));

            var area = Cross(b - a, c - a);
            if (Mathf.Abs(area) < 1e-6f) return;
            if (area < 0f)
            {
                var swap = b;
                b = c;
                c = swap;
            }

            var x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            var x1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            var y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
            var y1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
            // 折り返さない向きは画像の中だけ、折り返す向きも 3 周分までに抑える(壊れた UV で止まらないように)
            x0 = wrapU ? Mathf.Max(x0, -width) : Mathf.Max(x0, 0);
            x1 = wrapU ? Mathf.Min(x1, width * 2) : Mathf.Min(x1, width - 1);
            y0 = wrapV ? Mathf.Max(y0, -height) : Mathf.Max(y0, 0);
            y1 = wrapV ? Mathf.Min(y1, height * 2) : Mathf.Min(y1, height - 1);

            for (var y = y0; y <= y1; y++)
            {
                var py = y + 0.5f;
                for (var x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, py);
                    if (Cross(b - a, p - a) < 0f || Cross(c - b, p - b) < 0f || Cross(a - c, p - c) < 0f) continue;
                    Plot(x, y);
                }
            }
        }

        /// <summary>0 以外の画素を、radius 画素の正方形の範囲まで 255 に広げる。</summary>
        public static void Dilate(byte[] mask, int width, int height, int radius)
        {
            if (radius <= 0) return;
            const int far = 1 << 28;
            var tmp = new byte[mask.Length];
            var near = new int[Mathf.Max(width, height)];

            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                var last = -far;
                for (var x = 0; x < width; x++)
                {
                    if (mask[row + x] != 0) last = x;
                    near[x] = x - last;
                }

                last = far;
                for (var x = width - 1; x >= 0; x--)
                {
                    if (mask[row + x] != 0) last = x;
                    tmp[row + x] = Mathf.Min(near[x], last - x) <= radius ? (byte)255 : (byte)0;
                }
            }

            for (var x = 0; x < width; x++)
            {
                var last = -far;
                for (var y = 0; y < height; y++)
                {
                    if (tmp[y * width + x] != 0) last = y;
                    near[y] = y - last;
                }

                last = far;
                for (var y = height - 1; y >= 0; y--)
                {
                    if (tmp[y * width + x] != 0) last = y;
                    mask[y * width + x] = Mathf.Min(near[y], last - y) <= radius ? (byte)255 : (byte)0;
                }
            }
        }
    }
}
