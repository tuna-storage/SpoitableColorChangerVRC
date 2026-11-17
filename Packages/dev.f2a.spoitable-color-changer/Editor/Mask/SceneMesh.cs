using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>シーンビューでモデルにレイを当てた結果。</summary>
    internal struct SceneHit
    {
        /// <summary>当たった位置(ワールド座標)。</summary>
        public Vector3 Point;

        /// <summary>当たった三角形の法線(ワールド座標。カメラ側を向いているとは限らない)。</summary>
        public Vector3 Normal;

        /// <summary>テクスチャ上の位置(Tiling/Offset を掛けたあと、0〜1 に折り返す前)。</summary>
        public Vector2 Uv;

        public float Distance;

        /// <summary>当たった三角形での、UV 1 あたりのワールドの長さ(面積の比から求めた目安)。</summary>
        public float WorldPerUv;
    }

    /// <summary>
    /// シーンビューで使う、対象のテクスチャを描いているメッシュ(サブメッシュ単位)。
    /// SkinnedMeshRenderer は今のポーズで焼いたものを持ち、レイの判定とモデルの上への重ね描きに使う。
    /// 対象・テクスチャ・Renderer の位置が変わらない間は作り直さない(確かめるのも間引く)。
    /// </summary>
    internal sealed class SceneMesh
    {
        /// <summary>1 つの Renderer の 1 つのサブメッシュ。</summary>
        internal sealed class Part
        {
            public UvSource Source;

            /// <summary>描く形(焼いたメッシュか元のメッシュ)。</summary>
            public Mesh Mesh;

            public Matrix4x4 LocalToWorld;

            /// <summary>頂点のワールド座標。</summary>
            public Vector3[] World;

            /// <summary>UV0(Tiling/Offset を掛ける前)。</summary>
            public Vector2[] Uv;

            /// <summary>サブメッシュの三角形(頂点番号 3 つずつ)。</summary>
            public int[] Triangles;

            public Bounds Bounds;

            /// <summary>三角形 ChunkSize 個ずつの範囲(レイの判定で、当たらない塊を飛ばすため)。</summary>
            public Bounds[] Chunks;
        }

        private const int ChunkSize = 64;

        /// <summary>Renderer の位置などが変わっていないかを確かめる間隔(秒)。</summary>
        private const double CheckInterval = 0.5;

        private readonly List<Part> _parts = new List<Part>();
        private readonly List<Mesh> _owned = new List<Mesh>();

        public IReadOnlyList<Part> Parts => _parts;

        private static SceneMesh s_cached;
        private static string s_key;
        private static SpoitableColorChanger s_component;
        private static Texture s_texture;
        private static double s_checked;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        /// <summary>component が texture を塗り替えるメッシュ。対象がなければ null。</summary>
        public static SceneMesh Get(SpoitableColorChanger component, Texture texture)
        {
            if (component == null || texture == null) return null;
            var now = EditorApplication.timeSinceStartup;
            var same = s_cached != null && s_component == component && s_texture == texture;
            if (same && now - s_checked < CheckInterval) return s_cached;

            s_checked = now;
            var sources = TargetResolver.PositionSources(component, texture);
            var key = KeyOf(sources);
            if (same && key == s_key) return s_cached;

            Clear();
            s_cached = Build(sources);
            s_key = key;
            s_component = component;
            s_texture = texture;
            s_checked = now;
            return s_cached;
        }

        public static void Clear()
        {
            if (s_cached != null)
            {
                foreach (var m in s_cached._owned)
                {
                    if (m != null) Object.DestroyImmediate(m);
                }

                s_cached._owned.Clear();
                s_cached._parts.Clear();
            }

            s_cached = null;
            s_key = null;
            s_component = null;
            s_texture = null;
        }

        private static string KeyOf(List<(Renderer renderer, UvSource source)> sources)
        {
            var sb = new StringBuilder();
            foreach (var (renderer, source) in sources)
            {
                if (renderer == null || source.Mesh == null) continue;
                sb.Append('|').Append(Visible(renderer) ? 'v' : 'h').Append(renderer.GetInstanceID()).Append(':').Append(source.Mesh.GetInstanceID())
                    .Append(':').Append(source.Mesh.vertexCount).Append(':').Append(source.SubMesh).Append(':')
                    .Append(source.St.ToString("R"));
                var m = renderer.transform.localToWorldMatrix;
                for (var i = 0; i < 16; i++) sb.Append(',').Append(m[i].ToString("F4", CultureInfo.InvariantCulture));
                // SkinnedMeshRenderer はボーンが動いても Transform は変わらないので、範囲(ルートのボーンに付いて動く)も見る
                if (renderer is SkinnedMeshRenderer)
                {
                    var b = renderer.bounds;
                    sb.Append('#').Append(b.center.ToString("F3")).Append(b.extents.ToString("F3"));
                }
            }

            return sb.ToString();
        }

        private static bool Visible(Renderer renderer)
        {
            return renderer.enabled && renderer.gameObject.activeInHierarchy;
        }

        private static SceneMesh Build(List<(Renderer renderer, UvSource source)> sources)
        {
            var result = new SceneMesh();
            var baked = new Dictionary<Renderer, Mesh>();
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            foreach (var (renderer, source) in sources)
            {
                // 隠している衣装など、シーンビューに描かれていないものは当てない・重ねない(見えている面の手前で当たってしまう)
                if (renderer == null || source.Mesh == null || !Visible(renderer)) continue;
                try
                {
                    var hadBaked = baked.ContainsKey(renderer);
                    var mesh = PositionMap.MeshFor(renderer, source.Mesh, baked);
                    if (!hadBaked && baked.TryGetValue(renderer, out var made) && made != null) result._owned.Add(made);
                    if (mesh == null || source.SubMesh >= mesh.subMeshCount) continue;
                    if (mesh.GetTopology(source.SubMesh) != MeshTopology.Triangles) continue;

                    vertices.Clear();
                    mesh.GetVertices(vertices);
                    uvs.Clear();
                    mesh.GetUVs(0, uvs);
                    if (uvs.Count != vertices.Count || vertices.Count == 0) continue;

                    var matrix = renderer.transform.localToWorldMatrix;
                    var world = new Vector3[vertices.Count];
                    for (var i = 0; i < world.Length; i++) world[i] = matrix.MultiplyPoint3x4(vertices[i]);

                    var triangles = mesh.GetTriangles(source.SubMesh);
                    if (triangles.Length < 3) continue;
                    var part = new Part
                    {
                        Source = source,
                        Mesh = mesh,
                        LocalToWorld = matrix,
                        World = world,
                        Uv = uvs.ToArray(),
                        Triangles = triangles,
                    };
                    BuildChunks(part);
                    result._parts.Add(part);
                }
                catch (Exception e)
                {
                    // 読めないメッシュ(Read/Write が無効など)は飛ばす。毎回出さないよう、作り直すときだけ出る
                    Debug.LogWarning($"[SpoitableColorChanger] {renderer.name} の形を読めなかったので、シーンビューでは扱いません: {e.Message}");
                }
            }

            return result;
        }

        private static void BuildChunks(Part part)
        {
            var count = part.Triangles.Length / 3;
            var chunks = new Bounds[(count + ChunkSize - 1) / ChunkSize];
            var all = new Bounds();
            for (var c = 0; c < chunks.Length; c++)
            {
                var start = c * ChunkSize;
                var end = Mathf.Min(count, start + ChunkSize);
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (var t = start; t < end; t++)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var index = part.Triangles[t * 3 + k];
                        if (index < 0 || index >= part.World.Length) continue;
                        var p = part.World[index];
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }

                var b = new Bounds();
                b.SetMinMax(min, max);
                // 板ポリゴンは厚みが 0 になるので、少しだけ広げる
                b.Expand(1e-4f);
                chunks[c] = b;
                if (c == 0) all = b;
                else all.Encapsulate(b);
            }

            part.Chunks = chunks;
            part.Bounds = all;
        }

        /// <summary>
        /// p(ワールド座標)にいちばん近い表面の点。Point にその点、Distance に p からの距離、Uv にその点の UV が入る。
        /// 形がなければ false。範囲の塊(64 三角形ずつ)が今の最短より遠ければ飛ばす。
        /// </summary>
        public bool ClosestPoint(Vector3 p, out SceneHit hit)
        {
            hit = default;
            var best = float.MaxValue; // 距離の 2 乗
            Part bestPart = null;
            var bestTri = -1;
            var bestV = 0f;
            var bestW = 0f;
            var bestPoint = Vector3.zero;

            foreach (var part in _parts)
            {
                if (part.Bounds.SqrDistance(p) >= best) continue;
                var tris = part.Triangles;
                var world = part.World;
                var count = tris.Length / 3;
                for (var c = 0; c < part.Chunks.Length; c++)
                {
                    if (part.Chunks[c].SqrDistance(p) >= best) continue;
                    var end = Mathf.Min(count, (c + 1) * ChunkSize);
                    for (var t = c * ChunkSize; t < end; t++)
                    {
                        var i0 = tris[t * 3];
                        var i1 = tris[t * 3 + 1];
                        var i2 = tris[t * 3 + 2];
                        if ((uint)i0 >= (uint)world.Length || (uint)i1 >= (uint)world.Length ||
                            (uint)i2 >= (uint)world.Length) continue;
                        var q = ClosestOnTriangle(p, world[i0], world[i1], world[i2], out var v, out var w);
                        var d = (p - q).sqrMagnitude;
                        if (d >= best) continue;
                        best = d;
                        bestPart = part;
                        bestTri = t;
                        bestV = v;
                        bestW = w;
                        bestPoint = q;
                    }
                }
            }

            if (bestPart == null) return false;
            var a = bestPart.Triangles[bestTri * 3];
            var b = bestPart.Triangles[bestTri * 3 + 1];
            var c2 = bestPart.Triangles[bestTri * 3 + 2];
            var st = bestPart.Source.St;
            Vector2 Uv(int i) => new Vector2(bestPart.Uv[i].x * st.x + st.z, bestPart.Uv[i].y * st.y + st.w);
            var cross = Vector3.Cross(bestPart.World[b] - bestPart.World[a], bestPart.World[c2] - bestPart.World[a]);
            hit = new SceneHit
            {
                Point = bestPoint,
                Normal = cross.sqrMagnitude > 0f ? cross.normalized : Vector3.up,
                Uv = Uv(a) * (1f - bestV - bestW) + Uv(b) * bestV + Uv(c2) * bestW,
                Distance = Mathf.Sqrt(best),
            };
            return true;
        }

        /// <summary>
        /// 三角形 abc の上で p にいちばん近い点(Ericson, Real-Time Collision Detection 5.1.5)。
        /// v, w はその点の b, c の重み(a の重みは 1 - v - w)。
        /// </summary>
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float v, out float w)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                v = 0f;
                w = 0f;
                return a;
            }

            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                v = 1f;
                w = 0f;
                return b;
            }

            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                v = Safe(d1, d1 - d3);
                w = 0f;
                return a + ab * v;
            }

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                v = 0f;
                w = 1f;
                return c;
            }

            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                v = 0f;
                w = Safe(d2, d2 - d6);
                return a + ac * w;
            }

            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                w = Safe(d4 - d3, d4 - d3 + (d5 - d6));
                v = 1f - w;
                return b + (c - b) * w;
            }

            var sum = va + vb + vc;
            v = Safe(vb, sum);
            w = Safe(vc, sum);
            return a + ab * v + ac * w;
        }

        private static float Safe(float numerator, float denominator)
        {
            return Mathf.Abs(denominator) > 1e-20f ? Mathf.Clamp01(numerator / denominator) : 0f;
        }

        /// <summary>いちばん手前の当たり(両面)。どこにも当たらなければ false。</summary>
        public bool Raycast(Ray ray, out SceneHit hit)
        {
            hit = default;
            var best = float.MaxValue;
            Part bestPart = null;
            var bestTri = -1;
            var bestU = 0f;
            var bestV = 0f;

            var ox = ray.origin.x;
            var oy = ray.origin.y;
            var oz = ray.origin.z;
            var dx = ray.direction.x;
            var dy = ray.direction.y;
            var dz = ray.direction.z;

            foreach (var part in _parts)
            {
                if (!part.Bounds.IntersectRay(ray, out var enter) || enter > best) continue;
                var tris = part.Triangles;
                var world = part.World;
                var count = tris.Length / 3;
                for (var c = 0; c < part.Chunks.Length; c++)
                {
                    if (!part.Chunks[c].IntersectRay(ray, out var chunkEnter) || chunkEnter > best) continue;
                    var end = Mathf.Min(count, (c + 1) * ChunkSize);
                    for (var t = c * ChunkSize; t < end; t++)
                    {
                        var i0 = tris[t * 3];
                        var i1 = tris[t * 3 + 1];
                        var i2 = tris[t * 3 + 2];
                        if ((uint)i0 >= (uint)world.Length || (uint)i1 >= (uint)world.Length ||
                            (uint)i2 >= (uint)world.Length) continue;
                        var v0 = world[i0];
                        var v1 = world[i1];
                        var v2 = world[i2];

                        // Möller–Trumbore(両面)
                        var e1x = v1.x - v0.x;
                        var e1y = v1.y - v0.y;
                        var e1z = v1.z - v0.z;
                        var e2x = v2.x - v0.x;
                        var e2y = v2.y - v0.y;
                        var e2z = v2.z - v0.z;
                        var px = dy * e2z - dz * e2y;
                        var py = dz * e2x - dx * e2z;
                        var pz = dx * e2y - dy * e2x;
                        var det = e1x * px + e1y * py + e1z * pz;
                        if (det > -1e-14f && det < 1e-14f) continue;
                        var inv = 1f / det;
                        var tx = ox - v0.x;
                        var ty = oy - v0.y;
                        var tz = oz - v0.z;
                        var u = (tx * px + ty * py + tz * pz) * inv;
                        if (u < 0f || u > 1f) continue;
                        var qx = ty * e1z - tz * e1y;
                        var qy = tz * e1x - tx * e1z;
                        var qz = tx * e1y - ty * e1x;
                        var v = (dx * qx + dy * qy + dz * qz) * inv;
                        if (v < 0f || u + v > 1f) continue;
                        var distance = (e2x * qx + e2y * qy + e2z * qz) * inv;
                        if (distance <= 1e-6f || distance >= best) continue;
                        best = distance;
                        bestPart = part;
                        bestTri = t;
                        bestU = u;
                        bestV = v;
                    }
                }
            }

            if (bestPart == null) return false;

            var a = bestPart.Triangles[bestTri * 3];
            var b = bestPart.Triangles[bestTri * 3 + 1];
            var c2 = bestPart.Triangles[bestTri * 3 + 2];
            var w0 = 1f - bestU - bestV;
            var st = bestPart.Source.St;
            Vector2 Uv(int i) => new Vector2(bestPart.Uv[i].x * st.x + st.z, bestPart.Uv[i].y * st.y + st.w);
            var uv0 = Uv(a);
            var uv1 = Uv(b);
            var uv2 = Uv(c2);
            var p0 = bestPart.World[a];
            var p1 = bestPart.World[b];
            var p2 = bestPart.World[c2];
            var cross = Vector3.Cross(p1 - p0, p2 - p0);
            var worldArea = cross.magnitude * 0.5f;
            var uvArea = Mathf.Abs((uv1.x - uv0.x) * (uv2.y - uv0.y) - (uv1.y - uv0.y) * (uv2.x - uv0.x)) * 0.5f;

            hit = new SceneHit
            {
                Point = ray.origin + ray.direction * best,
                Normal = cross.sqrMagnitude > 0f ? cross.normalized : -ray.direction,
                Uv = uv0 * w0 + uv1 * bestU + uv2 * bestV,
                Distance = best,
                WorldPerUv = uvArea > 1e-12f ? Mathf.Sqrt(worldArea / uvArea) : 0f,
            };
            return true;
        }
    }

    /// <summary>シーンビューで、マスク(R8)をモデルの上に色で重ねて描く。</summary>
    internal static class SceneOverlay
    {
        public const string ShaderName = "Hidden/faketuna/SpoitableColorChanger/SceneOverlay";

        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int StId = Shader.PropertyToID("_St");
        private static readonly int WrapId = Shader.PropertyToID("_Wrap");

        private static Material s_material;
        private static bool s_warned;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        private static Material Material
        {
            get
            {
                if (s_material != null) return s_material;
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    if (!s_warned)
                    {
                        Debug.LogWarning($"[SpoitableColorChanger] シェーダー {ShaderName} が見つかりません。Shaders フォルダを確認してください。");
                        s_warned = true;
                    }

                    return null;
                }

                s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return s_material;
            }
        }

        public static void Clear()
        {
            if (s_material != null) Object.DestroyImmediate(s_material);
            s_material = null;
        }

        /// <summary>mesh の上に mask を color で重ねる。シーンビューの Repaint の中で呼ぶ。</summary>
        public static void Draw(SceneMesh mesh, Texture mask, Color color, Texture source)
        {
            if (mesh == null || mask == null || source == null) return;
            var mat = Material;
            if (mat == null) return;
            mat.SetTexture(MaskTexId, mask);
            mat.SetColor(ColorId, color);
            mat.SetVector(WrapId, new Vector4(WrapValue(source.wrapModeU), WrapValue(source.wrapModeV), 0f, 0f));
            foreach (var part in mesh.Parts)
            {
                if (part.Mesh == null || part.Source.SubMesh >= part.Mesh.subMeshCount) continue;
                mat.SetVector(StId, part.Source.St);
                mat.SetPass(0);
                Graphics.DrawMeshNow(part.Mesh, part.LocalToWorld, part.Source.SubMesh);
            }

            mat.SetTexture(MaskTexId, null);
        }

        /// <summary>シェーダーの _Wrap の値(0 Clamp, 1 Repeat, 2 Mirror)。</summary>
        private static float WrapValue(TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat:
                    return 1f;
                case TextureWrapMode.Mirror:
                    return 2f;
                default:
                    return 0f;
            }
        }
    }
}
