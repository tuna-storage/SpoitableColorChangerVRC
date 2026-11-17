using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 位置マップ。テクスチャの各画素が、モデル上のどこに貼られているか(アバターのルートから見た位置)を持つ
    /// RenderTexture(ARGBHalf)。モデル上のグラデーションマスクに使う。
    /// アルファは 1 = メッシュが覆っている画素、0.5 = 隙間を周りから埋めた画素、0 = どこにも使われていない画素。
    /// UV を左右で共有している(ミラー)メッシュでは、後から描いた側の位置になる。
    /// </summary>
    internal static class PositionMap
    {
        public const string ShaderName = "Hidden/faketuna/SpoitableColorChanger/PositionMap";

        /// <summary>位置マップの一辺の上限。位置はなめらかに変わるので、元テクスチャより粗くてよい。</summary>
        private const int MaxSize = 1024;

        private const int DilateIterations = 8;
        private const int CacheSize = 6;

        /// <summary>この秒数のうちに使った位置マップは追い出さない(同じプレビューの組み立ての中で、先に取ったものを壊さないため)。</summary>
        private const double KeepSeconds = 5.0;

        /// <summary>使用中で追い出せないときでも、これを超えたら古いものから捨てる。</summary>
        private const int HardLimit = 32;

        private static readonly int StId = Shader.PropertyToID("_St");
        private static readonly int UvShiftId = Shader.PropertyToID("_UvShift");
        private static readonly int ToAvatarId = Shader.PropertyToID("_ToAvatar");
        private static readonly int SampleUvId = Shader.PropertyToID("_SampleUv");

        private sealed class Entry
        {
            public string Key;
            public RenderTexture Map;
            public int LastUse;
            public double LastUseTime;
        }

        private static readonly List<Entry> Cache = new List<Entry>();
        private static int s_useCounter;
        private static Material s_material;

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
                    throw new InvalidOperationException(
                        $"[SpoitableColorChanger] シェーダー {ShaderName} が見つかりません。Shaders フォルダを確認してください。");
                }

                s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return s_material;
            }
        }

        public static void Clear()
        {
            foreach (var e in Cache) Release(e.Map);
            Cache.Clear();
            if (s_material != null) Object.DestroyImmediate(s_material);
            s_material = null;
        }

        /// <summary>
        /// component が texture を塗り替えるときの位置マップ(キャッシュする)。使う Renderer・メッシュ・
        /// サブメッシュ・Tiling/Offset・位置が変わらない間は描き直さない。対象のメッシュがなければ null。
        /// 返した RenderTexture はキャッシュのものなので、呼び出し側で破棄しない。
        /// </summary>
        public static RenderTexture Get(SpoitableColorChanger component, Texture texture)
        {
            if (component == null || texture == null) return null;
            var root = TargetResolver.AvatarRootOf(component);
            var sources = TargetResolver.PositionSources(component, texture);
            if (sources.Count == 0) return null;

            var size = SizeFor(texture);
            var key = KeyOf(root, sources, size);
            foreach (var e in Cache)
            {
                if (e.Key != key || e.Map == null) continue;
                e.LastUse = ++s_useCounter;
                e.LastUseTime = EditorApplication.timeSinceStartup;
                return e.Map;
            }

            var map = Render(root, sources, size.x, size.y);
            if (map == null) return null;
            Evict();
            Cache.Add(new Entry
            {
                Key = key, Map = map, LastUse = ++s_useCounter, LastUseTime = EditorApplication.timeSinceStartup,
            });
            return map;
        }

        /// <summary>
        /// 上限を超えていたら、いちばん古く使ったものを捨てる。ただし最近使ったものは、呼び出し側がまだ持っている
        /// (プレビューの Plan で取って、あとで描くのに使う)ので、HardLimit までは残す。
        /// </summary>
        private static void Evict()
        {
            var now = EditorApplication.timeSinceStartup;
            while (Cache.Count >= CacheSize)
            {
                Entry oldest = null;
                foreach (var e in Cache)
                {
                    if (oldest == null || e.LastUse < oldest.LastUse) oldest = e;
                }

                if (oldest == null) return;
                if (now - oldest.LastUseTime < KeepSeconds && Cache.Count < HardLimit) return;
                Cache.Remove(oldest);
                Release(oldest.Map);
            }
        }

        public static Vector2Int SizeFor(Texture texture)
        {
            var scale = Mathf.Min(1f, (float)MaxSize / Mathf.Max(texture.width, texture.height));
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(texture.width * scale)),
                Mathf.Max(1, Mathf.RoundToInt(texture.height * scale)));
        }

        private static string KeyOf(Transform root, List<(Renderer renderer, UvSource source)> sources, Vector2Int size)
        {
            var sb = new StringBuilder();
            sb.Append(size.x).Append('x').Append(size.y);
            var toAvatar = root != null ? root.worldToLocalMatrix : Matrix4x4.identity;
            foreach (var (renderer, source) in sources)
            {
                sb.Append('|').Append(renderer.GetInstanceID()).Append(':').Append(source.Mesh.GetInstanceID())
                    .Append(':').Append(source.SubMesh).Append(':').Append(source.St.ToString("R"));
                var m = toAvatar * renderer.transform.localToWorldMatrix;
                for (var i = 0; i < 16; i++) sb.Append(',').Append(m[i].ToString("F4", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 位置マップを新しく描く(キャッシュしない。呼び出し側で Release する)。ビルドではこちらを使う。
        /// </summary>
        public static RenderTexture Render(Transform root, IReadOnlyList<(Renderer renderer, UvSource source)> sources,
            int width, int height)
        {
            if (sources.Count == 0) return null;
            var mat = Material;
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0)
            {
                sRGB = false,
                useMipMap = false,
                msaaSamples = 1,
            };
            var map = new RenderTexture(desc)
            {
                name = "Spoitable position map",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            map.Create();

            var previousActive = RenderTexture.active;
            var baked = new Dictionary<Renderer, Mesh>();
            GL.PushMatrix();
            try
            {
                Graphics.SetRenderTarget(map);
                GL.Clear(true, true, Color.clear);
                // シェーダーは UV を (0〜1, 0〜1) の平面上の点として描くので、Blit と同じ正射影にする
                GL.LoadOrtho();
                var toAvatar = root != null ? root.worldToLocalMatrix : Matrix4x4.identity;

                foreach (var (renderer, source) in sources)
                {
                    var mesh = MeshFor(renderer, source.Mesh, baked);
                    if (mesh == null || source.SubMesh >= mesh.subMeshCount) continue;
                    mat.SetMatrix(ToAvatarId, toAvatar * renderer.transform.localToWorldMatrix);
                    mat.SetVector(StId, source.St);
                    foreach (var shift in Tiles(source))
                    {
                        mat.SetVector(UvShiftId, new Vector4(shift.x, shift.y, 0f, 0f));
                        mat.SetPass(0);
                        Graphics.DrawMeshNow(mesh, Matrix4x4.identity, source.SubMesh);
                    }
                }

                Dilate(map);
                return map;
            }
            catch
            {
                Release(map);
                throw;
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previousActive;
                foreach (var m in baked.Values)
                {
                    if (m != null) Object.DestroyImmediate(m);
                }
            }
        }

        /// <summary>
        /// 描く形のメッシュ。SkinnedMeshRenderer は今のポーズで焼いたもの(Transform で描けば見た目どおりの位置)。
        /// 焼けなければ元のメッシュ(バインドポーズ)を使う。焼いたメッシュは baked に入るので、呼び出し側で破棄する。
        /// </summary>
        internal static Mesh MeshFor(Renderer renderer, Mesh shared, Dictionary<Renderer, Mesh> baked)
        {
            if (!(renderer is SkinnedMeshRenderer skinned)) return shared;
            if (baked.TryGetValue(renderer, out var mesh)) return mesh ? mesh : shared;
            try
            {
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(mesh, true);
                baked[renderer] = mesh;
                return mesh;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[SpoitableColorChanger] {renderer.name} の今のポーズを読めなかったので、元のメッシュの形で位置を求めます: {e.Message}");
                if (mesh != null) Object.DestroyImmediate(mesh);
                baked[renderer] = null;
                return shared;
            }
        }

        /// <summary>UV が 0〜1 をはみ出していれば、繰り返しの分も描く(3 × 3 枚まで)。</summary>
        private static IEnumerable<Vector2> Tiles(UvSource source)
        {
            var min = Vector2.zero;
            var max = Vector2.one;
            var mesh = source.Mesh;
            if (mesh.isReadable)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(0, uvs);
                var indices = mesh.GetIndices(source.SubMesh);
                if (indices.Length > 0 && uvs.Count > 0)
                {
                    min = new Vector2(float.MaxValue, float.MaxValue);
                    max = new Vector2(float.MinValue, float.MinValue);
                    foreach (var i in indices)
                    {
                        if (i >= uvs.Count) continue;
                        var uv = new Vector2(uvs[i].x * source.St.x + source.St.z, uvs[i].y * source.St.y + source.St.w);
                        min = Vector2.Min(min, uv);
                        max = Vector2.Max(max, uv);
                    }
                }
            }

            var x0 = Mathf.Max(-1, Mathf.FloorToInt(min.x));
            var x1 = Mathf.Min(1, Mathf.CeilToInt(max.x) - 1);
            var y0 = Mathf.Max(-1, Mathf.FloorToInt(min.y));
            var y1 = Mathf.Min(1, Mathf.CeilToInt(max.y) - 1);
            for (var y = y0; y <= Mathf.Max(y0, y1); y++)
            {
                for (var x = x0; x <= Mathf.Max(x0, x1); x++) yield return new Vector2(x, y);
            }
        }

        private static void Dilate(RenderTexture map)
        {
            var temp = RenderTexture.GetTemporary(map.descriptor);
            temp.filterMode = FilterMode.Point;
            var previousFilter = map.filterMode;
            map.filterMode = FilterMode.Point;
            try
            {
                var src = map;
                var dst = temp;
                for (var i = 0; i < DilateIterations; i++)
                {
                    Graphics.Blit(src, dst, Material, 1);
                    (src, dst) = (dst, src);
                }

                if (src != map) Graphics.Blit(src, map);
            }
            finally
            {
                map.filterMode = previousFilter;
                RenderTexture.ReleaseTemporary(temp);
            }
        }

        /// <summary>
        /// 位置マップの uv の画素を読む。メッシュが覆っている画素なら covered が true(埋めた画素や空の画素は false)。
        /// 何も描かれていない画素なら false を返す。
        /// </summary>
        public static bool Read(RenderTexture map, Vector2 uv, out Vector3 position, out bool covered)
        {
            position = Vector3.zero;
            covered = false;
            if (map == null || uv.x < 0f || uv.y < 0f || uv.x >= 1f || uv.y >= 1f) return false;

            // シェーダーで 1 × 1 に写してから全体を読む(部分読みの ReadPixels は、環境によって上下が逆になる)
            var center = new Vector2((Mathf.Floor(uv.x * map.width) + 0.5f) / map.width,
                (Mathf.Floor(uv.y * map.height) + 0.5f) / map.height);
            var previous = RenderTexture.active;
            var previousFilter = map.filterMode;
            var one = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try
            {
                map.filterMode = FilterMode.Point;
                Material.SetVector(SampleUvId, center);
                Graphics.Blit(map, one, Material, 2);
                RenderTexture.active = one;
                pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                pixel.Apply(false, false);
                var c = pixel.GetPixel(0, 0);
                position = new Vector3(c.r, c.g, c.b);
                covered = c.a > 0.75f;
                return c.a > 0.25f;
            }
            finally
            {
                map.filterMode = previousFilter;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(one);
                Object.DestroyImmediate(pixel);
            }
        }

        public static void Release(RenderTexture map)
        {
            if (map == null) return;
            map.Release();
            Object.DestroyImmediate(map);
        }
    }
}
