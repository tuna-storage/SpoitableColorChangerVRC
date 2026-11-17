using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 再配色コア。元テクスチャと設定から RenderTexture に描く。
    /// NDMF のプレビューとビルド処理の両方がここを通るので、見えた色とアップロードされる色が一致する。
    /// </summary>
    internal static class RecolorCore
    {
        public const string ShaderName = "Hidden/faketuna/SpoitableColorChanger/Recolor";

        private const int PassDecode = 0;
        private const int PassLayer = 1;
        private const int PassEncode = 2;
        private const int PassStats = 3;
        private const int PassOverlay = 4;
        private const int PassOklab = 5;
        private const int PassGradient = 6;

        private static readonly int PrevTexId = Shader.PropertyToID("_PrevTex");
        private static readonly int LutTexId = Shader.PropertyToID("_LutTex");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int LutParamsId = Shader.PropertyToID("_LutParams");
        private static readonly int RangeId = Shader.PropertyToID("_Range");
        private static readonly int MaskWeightsId = Shader.PropertyToID("_MaskWeights");
        private static readonly int MaskParamsId = Shader.PropertyToID("_MaskParams");
        private static readonly int SourceIsLinearDataId = Shader.PropertyToID("_SourceIsLinearData");
        private static readonly int PaintTexId = Shader.PropertyToID("_PaintTex");
        private static readonly int PaintParamsId = Shader.PropertyToID("_PaintParams");
        private static readonly int OverlayColorId = Shader.PropertyToID("_OverlayColor");
        private static readonly int OverlayParamsId = Shader.PropertyToID("_OverlayParams");
        private static readonly int SelectionTexId = Shader.PropertyToID("_SelectionTex");
        private static readonly int PosTexId = Shader.PropertyToID("_PosTex");
        private static readonly int GradParamsId = Shader.PropertyToID("_GradParams");
        private static readonly int GradAId = Shader.PropertyToID("_GradA");
        private static readonly int GradBId = Shader.PropertyToID("_GradB");
        private static readonly int GradSizeId = Shader.PropertyToID("_GradSize");
        private static readonly int GradPointsId = Shader.PropertyToID("_GradPoints");
        private static readonly int PathParamsId = Shader.PropertyToID("_PathParams");
        private static readonly int PathPointsId = Shader.PropertyToID("_PathPoints");
        private static readonly int PathLineVId = Shader.PropertyToID("_PathLineV");
        private static readonly int PathRegionTexId = Shader.PropertyToID("_PathRegionTex");
        private static readonly int PathRegionParamsId = Shader.PropertyToID("_PathRegionParams");

        // SetVectorArray は最初に渡した長さで固定されるので、いつも上限の数で渡す
        private static readonly Vector4[] GradPoints = new Vector4[GradientMask.MaxPoints];
        private static readonly Vector4[] PathPoints = new Vector4[ColorPath.MaxTotalPoints];
        private static readonly float[] PathLineV = new float[ColorPath.MaxLines];
        private static bool s_warnedNoPositions;
        private static bool s_warnedNoPathPositions;

        private static Material s_material;

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
                // static はドメインリロードで消えるので、その前に破棄しておく
                AssemblyReloadEvents.beforeAssemblyReload += DestroyMaterial;
                return s_material;
            }
        }

        private static void DestroyMaterial()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= DestroyMaterial;
            if (s_material != null) Object.DestroyImmediate(s_material);
            s_material = null;
        }

        public static bool IsSrgb(Texture texture)
        {
            return texture != null && GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        }

        public static bool HasMipMaps(Texture texture)
        {
            return texture != null && texture.mipmapCount > 1;
        }

        /// <summary>元テクスチャと同じ大きさ・色空間の出力先を作る。</summary>
        public static RenderTexture CreateTarget(Texture source)
        {
            var mips = HasMipMaps(source);
            var desc = new RenderTextureDescriptor(source.width, source.height, RenderTextureFormat.ARGB32, 0)
            {
                sRGB = IsSrgb(source),
                useMipMap = mips,
                autoGenerateMips = mips,
                msaaSamples = 1,
            };

            var rt = new RenderTexture(desc)
            {
                name = source.name + " (Spoitable)",
                hideFlags = HideFlags.DontSave,
                wrapModeU = source.wrapModeU,
                wrapModeV = source.wrapModeV,
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel,
            };
            rt.Create();
            return rt;
        }

        /// <summary>明るさの正規化に使う (黒点, 白点)。</summary>
        public static Vector2 ResolveRange(Texture source, RecolorSettings settings)
        {
            float black, white;
            if (settings != null && settings.RangeMode == LuminanceRangeMode.Manual)
            {
                black = Mathf.Clamp01(Mathf.Min(settings.BlackPoint, settings.WhitePoint));
                white = Mathf.Clamp01(Mathf.Max(settings.BlackPoint, settings.WhitePoint));
            }
            else
            {
                var stats = LuminanceStats.Get(source);
                black = stats?.Low ?? 0f;
                white = stats?.High ?? 1f;
            }

            if (white - black < 1e-3f) white = Mathf.Min(1f, black + 1e-3f);
            if (white - black < 1e-3f) black = white - 1e-3f;
            return new Vector2(black, white);
        }

        /// <summary>
        /// source を settings で再配色して dest に描く。positions はモデル上のグラデーションマスクに使う
        /// 位置マップ(PositionMap)。要らない設定なら null でよい。
        /// </summary>
        public static void Render(Texture source, RecolorSettings settings, RenderTexture dest, Texture positions = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (dest == null) throw new ArgumentNullException(nameof(dest));

            var mat = Material;
            var range = ResolveRange(source, settings);
            var layers = settings?.Layers ?? Array.Empty<LayerSettings>();
            var size = new Vector2(source.width, source.height);
            // 経路を使わずに明るさで描くとき(モデル上で位置が求められないとき)は、Relative の線の行も作らない
            var rows = LutRows(layers, size, positions, out var layerRows, out var lineRows);
            var lut = GradientLut.Create(rows);

            // Blit は出力先を RenderTexture.active に残すので、終わったら呼び出し前の状態に戻す
            var previousActive = RenderTexture.active;
            var a = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear);
            var b = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear);
            a.filterMode = FilterMode.Point;
            b.filterMode = FilterMode.Point;

            try
            {
                mat.SetFloat(SourceIsLinearDataId, IsSrgb(source) ? 0f : 1f);
                Graphics.Blit(source, a, mat, PassDecode);

                mat.SetTexture(LutTexId, lut);
                mat.SetVector(RangeId, new Vector4(range.x, 1f / (range.y - range.x), 0f, 0f));

                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    mat.SetTexture(PrevTexId, a);
                    mat.SetVector(LutParamsId, new Vector4(GradientLut.RowV(layerRows[i], rows.Count), GradientLut.Width, 0f, 0f));

                    if (layer.Mask != null)
                    {
                        mat.SetTexture(MaskTexId, layer.Mask);
                        mat.SetVector(MaskWeightsId, ChannelWeights(layer.Channel));
                        mat.SetVector(MaskParamsId,
                            new Vector4(1f, layer.Invert ? 1f : 0f, IsSrgb(layer.Mask) ? 1f : 0f, layer.Opacity));
                    }
                    else
                    {
                        mat.SetTexture(MaskTexId, Texture2D.whiteTexture);
                        mat.SetVector(MaskParamsId, new Vector4(0f, 0f, 0f, layer.Opacity));
                    }

                    if (layer.PaintMask != null)
                    {
                        mat.SetTexture(PaintTexId, layer.PaintMask);
                        mat.SetVector(PaintParamsId, new Vector4(1f, IsSrgb(layer.PaintMask) ? 1f : 0f, 0f, 0f));
                    }
                    else
                    {
                        mat.SetTexture(PaintTexId, Texture2D.whiteTexture);
                        mat.SetVector(PaintParamsId, Vector4.zero);
                    }

                    SetGradient(mat, layer.Gradient, source, positions);
                    // 位置マップは SetGradient で渡したものを共有する(どちらかがモデル上なら渡してある)
                    SetPath(mat, layer, source, positions, layerRows[i], lineRows[i], rows.Count);
                    Graphics.Blit(source, b, mat, PassLayer);
                    (a, b) = (b, a);
                }

                Graphics.Blit(a, dest, mat, PassEncode);
            }
            finally
            {
                RenderTexture.active = previousActive;
                mat.SetTexture(PrevTexId, null);
                mat.SetTexture(MaskTexId, null);
                mat.SetTexture(PaintTexId, null);
                mat.SetTexture(LutTexId, null);
                mat.SetTexture(PosTexId, null);
                mat.SetTexture(PathRegionTexId, null);
                RenderTexture.ReleaseTemporary(a);
                RenderTexture.ReleaseTemporary(b);
                Object.DestroyImmediate(lut);
            }
        }

        /// <summary>グラデーションマスクの値をシェーダーに渡す。モデル上の種類で位置マップがなければ、マスクなしとして扱う。</summary>
        private static void SetGradient(Material mat, GradientMaskConfig gradient, Texture source, Texture positions)
        {
            var g = gradient ?? GradientMaskConfig.None;
            var mode = g.IsNone ? 0 : (int)g.Mode;
            if (g.UsesModel && positions == null)
            {
                if (!s_warnedNoPositions)
                {
                    Debug.LogWarning("[SpoitableColorChanger] モデル上のグラデーションマスクに使う位置が求められなかったので、このマスクは使わずに描きます。");
                    s_warnedNoPositions = true;
                }

                mode = 0;
            }

            mat.SetVector(GradParamsId, new Vector4(mode, g.Invert ? 1f : 0f, g.Smooth ? 1f : 0f, g.Sharpness));
            mat.SetVector(GradAId, g.Start);
            mat.SetVector(GradBId, g.End);
            for (var i = 0; i < GradPoints.Length; i++) GradPoints[i] = i < g.Points.Length ? g.Points[i] : Vector4.zero;

            mat.SetVectorArray(GradPointsId, GradPoints);
            mat.SetVector(GradSizeId, new Vector4(source.width, source.height, Mathf.Min(g.Points.Length, GradPoints.Length), 0f));
            mat.SetTexture(PosTexId, g.UsesModel && positions != null ? positions : Texture2D.blackTexture);
        }

        /// <summary>経路を使って描けるか(点が 2 個以上の線があり、モデル上なら位置マップがある)。</summary>
        private static bool PathUsable(ColorPathConfig path, Texture positions)
        {
            return path != null && path.IsActive && !(path.UsesModel && positions == null);
        }

        /// <summary>
        /// LUT の行を並べる。レイヤーごとに 1 行(レイヤーのストップ)と、Relative で点とストップの数が合う線ごとに 1 行
        /// (その線の点の比の位置にストップを置き直したもの)。lineRows[レイヤー][線] は線の行(なければ -1)。
        /// </summary>
        private static List<(float position, Color color)[]> LutRows(LayerSettings[] layers, Vector2 size,
            Texture positions, out int[] layerRows, out int[][] lineRows)
        {
            var rows = new List<(float position, Color color)[]>();
            layerRows = new int[layers.Length];
            lineRows = new int[layers.Length][];
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                layerRows[i] = rows.Count;
                rows.Add(layer.Stops);
                var p = layer.Path;
                if (!PathUsable(p, positions) || !p.IsRelative) continue;
                lineRows[i] = Enumerable.Repeat(-1, ColorPath.MaxLines).ToArray();
                for (var line = 0; line < p.Lines.Length && line < ColorPath.MaxLines; line++)
                {
                    var stops = p.LineStops(line, layer.Stops, size);
                    if (stops == null) continue;
                    lineRows[i][line] = rows.Count;
                    rows.Add(stops);
                }
            }

            return rows;
        }

        /// <summary>
        /// 色の経路をシェーダーに渡す。点が足りないか、モデル上で位置マップがなければ明るさで色を決める。
        /// _PathPoints の w は「線の番号 × 2 + その点までの弧長の比」(t は比を線分の上で補間して求める)。
        /// 線が複数あれば、塗りマスクの塊ごとに使う線の表も渡す。
        /// </summary>
        private static void SetPath(Material mat, LayerSettings layer, Texture source, Texture positions,
            int layerRow, int[] lineRows, int rowCount)
        {
            var p = layer.Path ?? ColorPathConfig.None;
            var use = PathUsable(p, positions);
            if (p.UsesModel && positions == null && !s_warnedNoPathPositions)
            {
                Debug.LogWarning("[SpoitableColorChanger] モデル上の色の経路に使う位置が求められなかったので、明るさで色を決めて描きます。");
                s_warnedNoPathPositions = true;
            }

            var size = new Vector2(source.width, source.height);
            var count = 0;
            if (use)
            {
                for (var line = 0; line < p.Lines.Length && line < ColorPath.MaxLines; line++)
                {
                    var points = p.Lines[line];
                    if (points.Length < 2) continue;
                    if (count + points.Length > PathPoints.Length) break; // 合計の上限を超える線は使わない
                    var ratios = ColorPathConfig.Ratios(points, p.Space, size);
                    for (var k = 0; k < points.Length; k++)
                    {
                        PathPoints[count++] = new Vector4(points[k].x, points[k].y, points[k].z, line * 2f + ratios[k]);
                    }
                }
            }

            for (var i = count; i < PathPoints.Length; i++) PathPoints[i] = Vector4.zero;
            for (var line = 0; line < PathLineV.Length; line++)
            {
                var row = lineRows != null && line < lineRows.Length && lineRows[line] >= 0 ? lineRows[line] : layerRow;
                PathLineV[line] = GradientLut.RowV(row, rowCount);
            }

            mat.SetVectorArray(PathPointsId, PathPoints);
            mat.SetFloatArray(PathLineVId, PathLineV);
            mat.SetVector(PathParamsId, new Vector4(use && count >= 2 ? 1f : 0f,
                p.Space == ColorPathSpace.Model ? 1f : 0f, count, p.Shading));

            var regions = use ? PathRegions.Get(layer.PaintMask, p) : null;
            mat.SetTexture(PathRegionTexId, regions != null ? regions.Map : (Texture)Texture2D.blackTexture);
            mat.SetVector(PathRegionParamsId, regions != null
                ? new Vector4(1f, regions.UvScale.x, regions.UvScale.y, 0f)
                : new Vector4(0f, 1f, 1f, 0f));
            // モデル上の経路なら、グラデーションマスクが位置マップを使っていなくても渡す
            if (use && p.UsesModel) mat.SetTexture(PosTexId, positions);
        }

        /// <summary>グラデーションマスクの値(0〜1)を dest に描く。エディタで今のグラデーションを見せるのに使う。</summary>
        public static void BlitGradient(Texture source, GradientMaskConfig gradient, Texture positions, RenderTexture dest)
        {
            var mat = Material;
            var previousActive = RenderTexture.active;
            SetGradient(mat, gradient, source, positions);
            Graphics.Blit(source, dest, mat, PassGradient);
            mat.SetTexture(PosTexId, null);
            RenderTexture.active = previousActive;
        }

        /// <summary>元テクスチャの (OKLab の L, a, b, アルファ) を dest に描く。色の近似選択に使う。</summary>
        public static void BlitOklab(Texture source, RenderTexture dest)
        {
            var mat = Material;
            var previousActive = RenderTexture.active;
            mat.SetFloat(SourceIsLinearDataId, IsSrgb(source) ? 0f : 1f);
            Graphics.Blit(source, dest, mat, PassOklab);
            RenderTexture.active = previousActive;
        }

        /// <summary>
        /// マスクエディタの表示用。baseTexture に mask を色で重ね、selection があればその外を暗くして dest に描く。
        /// </summary>
        public static void BlitOverlay(Texture baseTexture, Texture mask, Texture selection, Color overlayColor,
            float overlayOpacity, RenderTexture dest)
        {
            var mat = Material;
            var previousActive = RenderTexture.active;
            mat.SetTexture(PaintTexId, mask != null ? mask : Texture2D.blackTexture);
            mat.SetTexture(SelectionTexId, selection != null ? selection : Texture2D.whiteTexture);
            mat.SetColor(OverlayColorId, overlayColor.linear);
            mat.SetVector(OverlayParamsId,
                new Vector4(mask != null ? overlayOpacity : 0f, selection != null ? 1f : 0f, 0f, 0f));
            Graphics.Blit(baseTexture, dest, mat, PassOverlay);
            mat.SetTexture(PaintTexId, null);
            mat.SetTexture(SelectionTexId, null);
            RenderTexture.active = previousActive;
        }

        /// <summary>(OKLab の L, アルファ) を dest に描く。</summary>
        public static void BlitStats(Texture source, RenderTexture dest)
        {
            var mat = Material;
            var previousActive = RenderTexture.active;
            mat.SetFloat(SourceIsLinearDataId, IsSrgb(source) ? 0f : 1f);
            Graphics.Blit(source, dest, mat, PassStats);
            RenderTexture.active = previousActive;
        }

        /// <summary>RenderTexture の内容を CPU 側の Texture2D に読み戻す。</summary>
        public static Texture2D ReadBack(RenderTexture rt, TextureFormat format, bool mipChain, bool linear)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, format, mipChain, linear);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(mipChain, false);
                return tex;
            }
            catch
            {
                Object.DestroyImmediate(tex);
                throw;
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        /// <summary>
        /// 再配色したテクスチャを Texture2D として作る(ビルドと PNG 書き出し用)。
        /// compress が true なら元テクスチャと同じ形式に圧縮する。
        /// </summary>
        public static Texture2D Bake(Texture source, RecolorSettings settings, bool compress, Texture positions = null)
        {
            var rt = CreateTarget(source);
            try
            {
                Render(source, settings, rt, positions);

                var tex = ReadBack(rt, TextureFormat.RGBA32, HasMipMaps(source), !IsSrgb(source));
                try
                {
                    tex.name = source.name + "_recolored";
                    tex.wrapModeU = source.wrapModeU;
                    tex.wrapModeV = source.wrapModeV;
                    tex.filterMode = source.filterMode;
                    tex.anisoLevel = source.anisoLevel;
                    tex.mipMapBias = source.mipMapBias;

                    if (compress) CompressLike(tex, source);
                    EnableStreamingMipmaps(tex);
                    return tex;
                }
                catch
                {
                    Object.DestroyImmediate(tex);
                    throw;
                }
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static void CompressLike(Texture2D tex, Texture source)
        {
            if (!(source is Texture2D src)) return;

            var format = Uncrunched(src.format);
            if (!GraphicsFormatUtility.IsCompressedFormat(format)) return;

            try
            {
                EditorUtility.CompressTexture(tex, format, TextureCompressionQuality.Normal);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SpoitableColorChanger] {source.name} を {format} に圧縮できませんでした。非圧縮のまま使います: {e.Message}");
            }
        }

        // Crunch はエディタ上で再圧縮できないことがあるので、同じブロック形式の非 Crunch にする
        private static TextureFormat Uncrunched(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1Crunched: return TextureFormat.DXT1;
                case TextureFormat.DXT5Crunched: return TextureFormat.DXT5;
                case TextureFormat.ETC_RGB4Crunched: return TextureFormat.ETC_RGB4;
                case TextureFormat.ETC2_RGBA8Crunched: return TextureFormat.ETC2_RGBA8;
                default: return format;
            }
        }

        // NDMF の CheckMipStreamingPass は、mip 付きの一時テクスチャでこれが false だとエラーにする
        private static void EnableStreamingMipmaps(Texture2D tex)
        {
            if (tex.mipmapCount <= 1) return;

            var so = new SerializedObject(tex);
            var prop = so.FindProperty("m_StreamingMipmaps");
            if (prop == null) return;
            prop.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Vector4 ChannelWeights(MaskChannel channel)
        {
            switch (channel)
            {
                case MaskChannel.Red: return new Vector4(1f, 0f, 0f, 0f);
                case MaskChannel.Green: return new Vector4(0f, 1f, 0f, 0f);
                case MaskChannel.Blue: return new Vector4(0f, 0f, 1f, 0f);
                case MaskChannel.Alpha: return new Vector4(0f, 0f, 0f, 1f);
                default: return new Vector4(0.2126f, 0.7152f, 0.0722f, 0f);
            }
        }
    }
}
