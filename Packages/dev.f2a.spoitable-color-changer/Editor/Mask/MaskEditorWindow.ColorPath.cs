using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    // 色の経路(点を結んだ線に沿ってレイヤーの色を並べる): 点と線の編集、キャンバスとシーンビューへの表示、右パネルの経路タブ。
    // 線は複数持てる(A¹B¹…、A²B²…)。塗りマスクの塊ごとに、点が乗っている線だけが効く(PathRegions)。
    internal sealed partial class MaskEditorWindow
    {
        private static readonly ColorPathMode[] PathModes =
        {
            ColorPathMode.Luminance,
            ColorPathMode.Normal,
            ColorPathMode.Relative,
        };

        private static readonly GUIContent[] PathModeLabels =
        {
            new GUIContent("明るさ", "元テクスチャの明るさでバーの色を当てる(今までどおり)"),
            new GUIContent("Normal Grade", "点を結んだ線に沿って色を並べる。線の全長をバーの左端〜右端に割り当てる"),
            new GUIContent("Relative Grade",
                "点 1 つにバーのストップ 1 つを対応させる。点の間の距離の比でストップの位置が決まり、点を動かすとバーも動く"),
        };

        private static readonly GUIContent[] PathSpaceLabels =
        {
            new GUIContent("テクスチャ上", "点をテクスチャの上(UV)に置く"),
            new GUIContent("モデル上", "点をモデルの上に置く。キャンバスのクリックで置き、シーンビューのハンドルで動かせる"),
        };

        private static readonly string[] Superscripts = { "⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹" };

        private const float PathInsertDistance = 6f;

        /// <summary>モデルの表面からこれ以上離れた点は、キャンバスに薄く出す(いちばん近い表面の位置に)。</summary>
        private const float PathOffSurfaceDistance = 0.003f;

        private const string RelativeHint =
            "Relative Grade: ストップの位置と数は点で決まります。点を足す・消す・動かすと変わります(色はここで変えられます)。";

        private int _pathLine; // 今の線(クリックで点を足す線)
        private int _pathPoint = -1; // 選んでいる点(_pathLine の線の中の番号)
        private string _pathPointLayer;
        private bool _pathDragging;
        private bool _pathMoved; // 押してから 3px 動かしたか(動かすまでは書き込まない)
        private Vector2 _pathDownMouse;

        // モデル上の点の、いちばん近い表面の UV と表面からの距離。形やテクスチャが変わったら捨てる
        private readonly Dictionary<Vector3, (Vector2 uv, float distance)> _pathUvCache =
            new Dictionary<Vector3, (Vector2 uv, float distance)>();

        private SceneMesh _pathUvMesh;
        private Texture _pathUvSource;

        /// <summary>選んでいる点を忘れる(対象・テクスチャの切り替えや Undo のあとは、番号が別の点を指すことがある)。</summary>
        private void ForgetPathPoint()
        {
            _pathPoint = -1;
            _pathPointLayer = null;
        }

        // ------------------------------------------------------------------ 値の読み書き

        /// <summary>今のレイヤーの colorPath の SerializedProperty。create なら割り当てを作る。</summary>
        private SerializedProperty PathProperty(bool create)
        {
            var component = ComponentObject();
            var element = create ? MaskBindingGui.Ensure(component, layerName) : MaskBindingGui.Find(component, layerName);
            return element?.FindPropertyRelative(nameof(MaskBinding.colorPath));
        }

        private static ColorPathMode PathModeOf(SerializedProperty path)
        {
            return path != null
                ? (ColorPathMode)path.FindPropertyRelative(nameof(ColorPath.mode)).intValue
                : ColorPathMode.Luminance;
        }

        private static ColorPathSpace PathSpaceOf(SerializedProperty path)
        {
            return path != null
                ? (ColorPathSpace)path.FindPropertyRelative(nameof(ColorPath.space)).intValue
                : new ColorPath().space;
        }

        private static SerializedProperty PathLines(SerializedProperty path)
        {
            return path.FindPropertyRelative(nameof(ColorPath.lines));
        }

        private static int LineCount(SerializedProperty path)
        {
            return path != null ? PathLines(path).arraySize : 0;
        }

        private static SerializedProperty LinePoints(SerializedProperty path, int line)
        {
            return PathLines(path).ArrayElement(line).FindPropertyRelative(nameof(ColorPathLine.points));
        }

        private static int PointCount(SerializedProperty path, int line)
        {
            return path != null && line >= 0 && line < LineCount(path) ? LinePoints(path, line).arraySize : 0;
        }

        private static int TotalPoints(SerializedProperty path)
        {
            var total = 0;
            for (var i = 0; i < LineCount(path); i++) total += PointCount(path, i);
            return total;
        }

        private static SerializedProperty PathPointField(SerializedProperty path, int line, int index, string name)
        {
            return LinePoints(path, line).ArrayElement(index).FindPropertyRelative(name);
        }

        private static List<Vector3> ReadLinePositions(SerializedProperty path, int line)
        {
            var count = PointCount(path, line);
            var list = new List<Vector3>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(PathPointField(path, line, i, nameof(ColorPathPoint.position)).vector3Value);
            }

            return list;
        }

        /// <summary>今の線の番号(線の数に収める)。線がなければ 0。</summary>
        private int CurrentLine(SerializedProperty path)
        {
            var count = LineCount(path);
            return count == 0 ? 0 : Mathf.Clamp(_pathLine, 0, count - 1);
        }

        /// <summary>選んでいる点(今の線の中の番号)。選んでいなければ -1。</summary>
        private int SelectedPathPoint(SerializedProperty path)
        {
            if (path == null || _pathPointLayer != layerName) return -1;
            if (_pathLine < 0 || _pathLine >= LineCount(path)) return -1;
            return _pathPoint >= 0 && _pathPoint < PointCount(path, _pathLine) ? _pathPoint : -1;
        }

        /// <summary>
        /// Relative のストップと対応させる線: 今の線(点が 2 個以上なら)、なければ点が 2 個以上ある最初の線。なければ -1。
        /// </summary>
        private int ReferenceLine(SerializedProperty path)
        {
            if (path == null) return -1;
            var current = CurrentLine(path);
            if (PointCount(path, current) >= 2) return current;
            for (var i = 0; i < LineCount(path); i++)
            {
                if (PointCount(path, i) >= 2) return i;
            }

            return -1;
        }

        /// <summary>点の名前。線が 2 本以上なら、線の番号を右上に付ける(A¹, B¹, A²…)。</summary>
        private static string PathLabel(int line, int index, int lineCount)
        {
            var letter = ((char)('A' + index)).ToString();
            return lineCount > 1 ? letter + Superscript(line + 1) : letter;
        }

        private static string Superscript(int n)
        {
            return string.Concat(n.ToString().Select(c => Superscripts[c - '0']));
        }

        /// <summary>今のレイヤー(プリセット側)。なければ null。</summary>
        private ColorLayer CurrentColorLayer(string name)
        {
            return target != null && target.preset != null
                ? target.preset.layers.FirstOrDefault(l => l != null && l.name == name)
                : null;
        }

        private static (float position, Color color)[] StopsOf(ColorLayer layer)
        {
            return layer?.stops == null
                ? Array.Empty<(float, Color)>()
                : layer.stops.Select(s => (Mathf.Clamp01(s.position), s.color)).ToArray();
        }

        /// <summary>今表示しているテクスチャ。なければ null。</summary>
        private Texture CurrentSourceTexture()
        {
            var textures = target != null ? TargetResolver.TargetTextures(target) : null;
            if (textures == null || textures.Count == 0) return null;
            return textures[Mathf.Clamp(textureIndex, 0, textures.Count - 1)].texture;
        }

        /// <summary>今表示しているテクスチャの大きさ(テクスチャ上の経路の距離を画素で測るのに使う)。</summary>
        private Vector2 CurrentTextureSize()
        {
            return TextureSize(CurrentSourceTexture());
        }

        private static Vector2 TextureSize(Texture texture)
        {
            return texture != null ? new Vector2(texture.width, texture.height) : Vector2.one;
        }

        /// <summary>stops を位置 ts で評価した色(sRGB)。表示と同じく線形の色空間(OKLab)で補間する。</summary>
        private static Color[] EvaluateStops(IReadOnlyList<(float position, Color color)> stops, IReadOnlyList<float> ts)
        {
            var result = new Color[ts.Count];
            if (stops.Count == 0)
            {
                for (var i = 0; i < result.Length; i++) result[i] = Color.gray;
                return result;
            }

            var lut = ColorMath.BuildLinearLut(
                stops.Select(s => (s.position, new ColorMath.Rgb(s.color.r, s.color.g, s.color.b))).ToList(),
                GradientLut.Width);
            for (var i = 0; i < result.Length; i++)
            {
                var x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(ts[i]) * (GradientLut.Width - 1)), 0, GradientLut.Width - 1);
                var c = ColorMath.LinearToSrgb(lut[x]);
                result[i] = new Color(c.R, c.G, c.B, 1f);
            }

            return result;
        }

        /// <summary>
        /// 線 line の点ごとの色(sRGB)。描かれる色と同じく、Relative で数が合えば各ストップの色、それ以外はバーを点の比で
        /// 評価した色。経路が使われていない(明るさ・点が 2 個未満)ときは灰色。
        /// </summary>
        private Color[] PathPointColors(SerializedProperty path, int line, Vector2 size)
        {
            var positions = ReadLinePositions(path, line);
            var mode = PathModeOf(path);
            var stops = StopsOf(CurrentColorLayer(layerName));
            if (mode == ColorPathMode.Luminance || positions.Count < 2 || stops.Length == 0)
            {
                return Enumerable.Repeat(new Color(0.6f, 0.6f, 0.6f, 1f), positions.Count).ToArray();
            }

            var ratios = ColorPathConfig.Ratios(positions, PathSpaceOf(path), size);
            var effective = mode == ColorPathMode.Relative
                ? ColorPathConfig.RelativeStops(stops, ratios) ?? stops
                : stops;
            return EvaluateStops(effective, ratios);
        }

        /// <summary>
        /// 今のレイヤーのストップを、colors(位置の順)と positions で作り直す準備をする。
        /// 返したプリセットの SerializedObject は ApplyPathChange で確定する。プリセットがなければ null。
        /// </summary>
        private SerializedObject RebuildStops(IReadOnlyList<Color> colors, IReadOnlyList<float> positions)
        {
            if (target == null || target.preset == null) return null;
            var so = new SerializedObject(target.preset);
            var layers = so.FindProperty(nameof(SpoitableColorPreset.layers));
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.ArrayElement(i);
                if (layer.FindPropertyRelative(nameof(ColorLayer.name)).stringValue != layerName) continue;
                var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));
                // 配列を伸ばすと末尾が複製されるが、すべて書き直すので問題ない
                stops.arraySize = colors.Count;
                for (var k = 0; k < colors.Count; k++)
                {
                    var stop = stops.ArrayElement(k);
                    stop.FindPropertyRelative(nameof(ColorStop.position)).floatValue = Mathf.Clamp01(positions[k]);
                    var c = colors[k];
                    stop.FindPropertyRelative(nameof(ColorStop.color)).colorValue = new Color(c.r, c.g, c.b, 1f);
                }

                return so;
            }

            so.Dispose();
            return null;
        }

        /// <summary>
        /// コンポーネント(点)と、あればプリセット(ストップ)の変更を確定する。両方を 1 回の Undo で戻せるようにまとめる
        /// (片方だけ戻ると、点とストップの数がずれる)。
        /// </summary>
        private void ApplyPathChange(SerializedObject preset, string undoName)
        {
            var group = Undo.GetCurrentGroup();
            ComponentObject().ApplyModifiedProperties();
            if (preset != null)
            {
                preset.ApplyModifiedProperties();
                preset.Dispose();
            }

            Undo.CollapseUndoOperations(group);
            Undo.SetCurrentGroupName(undoName);
        }

        // ------------------------------------------------------------------ モデル上の点のテクスチャ上の位置

        /// <summary>
        /// モデル上の位置 position(アバターのルートから見た位置)に、いちばん近い表面の点の UV(テクスチャの折り返しどおりに
        /// 0〜1 に収めたもの)と、表面からの距離。対象の形が見つからなければ false。
        /// </summary>
        private bool TryMapPath(Vector3 position, Texture source, out Vector2 uv, out float distance)
        {
            uv = NoUv;
            distance = float.MaxValue;
            if (target == null || source == null) return false;
            var mesh = SceneMesh.Get(target, source);
            if (mesh != _pathUvMesh || source != _pathUvSource || _pathUvCache.Count > 512)
            {
                _pathUvCache.Clear();
                _pathUvMesh = mesh;
                _pathUvSource = source;
            }

            if (_pathUvCache.TryGetValue(position, out var cached))
            {
                uv = cached.uv;
                distance = cached.distance;
                return uv.x >= 0f;
            }

            var root = TargetResolver.AvatarRootOf(target);
            if (mesh != null && root != null && mesh.ClosestPoint(root.localToWorldMatrix.MultiplyPoint3x4(position), out var hit))
            {
                uv = FoldUv(hit.Uv, source);
                distance = hit.Distance;
            }

            _pathUvCache[position] = (uv, distance);
            return uv.x >= 0f;
        }

        /// <summary>UV をテクスチャの折り返しどおりに 0〜1 に収める(Clamp は端に寄せる)。</summary>
        private static Vector2 FoldUv(Vector2 uv, Texture source)
        {
            if (!WrapCoordinate(ref uv.x, source.wrapModeU)) uv.x = Mathf.Clamp01(uv.x);
            if (!WrapCoordinate(ref uv.y, source.wrapModeV)) uv.y = Mathf.Clamp01(uv.y);
            return uv;
        }

        private Vector2 MapPathUv(Vector3 position, Texture source)
        {
            return TryMapPath(position, source, out var uv, out _) ? uv : NoUv;
        }

        /// <summary>
        /// モデル上の点のテクスチャ上の位置(表示と、塗りマスクの塊の判定に使う)を source のものにそろえる。
        /// 別のテクスチャで拾った位置は今のテクスチャでは別の場所を指すので拾い直し、分からない点も拾う。
        /// </summary>
        private void EnsurePathHints(SerializedProperty path, Texture source)
        {
            if (source == null || PathSpaceOf(path) != ColorPathSpace.Model) return;
            var texture = path.FindPropertyRelative(nameof(ColorPath.uvHintTexture));
            var remap = texture.objectReferenceValue != source;
            texture.objectReferenceValue = source;
            for (var line = 0; line < LineCount(path); line++)
            {
                for (var i = 0; i < PointCount(path, line); i++)
                {
                    var uv = PathPointField(path, line, i, nameof(ColorPathPoint.uv));
                    if (!remap && uv.vector2Value.x >= 0f) continue;
                    uv.vector2Value =
                        MapPathUv(PathPointField(path, line, i, nameof(ColorPathPoint.position)).vector3Value, source);
                }
            }
        }

        /// <summary>キャンバスに出す点の位置(テクスチャ上は位置そのもの、モデル上は拾った UV か、いちばん近い表面の UV)。</summary>
        private Vector2? PathPointUv(SerializedProperty path, int line, int index, Texture source)
        {
            var position = PathPointField(path, line, index, nameof(ColorPathPoint.position)).vector3Value;
            if (PathSpaceOf(path) == ColorPathSpace.Texture) return (Vector2)position;
            if (path.FindPropertyRelative(nameof(ColorPath.uvHintTexture)).objectReferenceValue == source)
            {
                var uv = PathPointField(path, line, index, nameof(ColorPathPoint.uv)).vector2Value;
                if (uv.x >= 0f) return uv;
            }

            return TryMapPath(position, source, out var mapped, out _) ? mapped : (Vector2?)null;
        }

        /// <summary>モデル上の点が、表面から離れている(シーンビューで浮かせた)か。</summary>
        private bool PathPointOffSurface(SerializedProperty path, int line, int index, Texture source)
        {
            if (PathSpaceOf(path) != ColorPathSpace.Model) return false;
            var position = PathPointField(path, line, index, nameof(ColorPathPoint.position)).vector3Value;
            return TryMapPath(position, source, out _, out var distance) && distance > PathOffSurfaceDistance;
        }

        // ------------------------------------------------------------------ 点と線の追加・削除・置き直し

        /// <summary>
        /// 点を書く。モデル上で uv が分からなければ、いちばん近い表面の UV を拾う(塊の判定とキャンバスの表示に使う)。
        /// </summary>
        private void WritePoint(SerializedProperty path, int line, int index, Vector3 position, Vector2 uv, Texture source)
        {
            if (PathSpaceOf(path) == ColorPathSpace.Model)
            {
                EnsurePathHints(path, source);
                if (uv.x < 0f && source != null) uv = MapPathUv(position, source);
            }
            else
            {
                uv = NoUv;
            }

            PathPointField(path, line, index, nameof(ColorPathPoint.position)).vector3Value = position;
            PathPointField(path, line, index, nameof(ColorPathPoint.uv)).vector2Value = uv;
        }

        private void InsertPoint(SerializedProperty path, int line, int index, Vector3 position, Vector2 uv, Texture source)
        {
            var points = LinePoints(path, line);
            index = Mathf.Clamp(index, 0, points.arraySize);
            points.InsertArrayElementAtIndex(index);
            WritePoint(path, line, index, position, uv, source);
        }

        /// <summary>空の線を末尾に足して、その番号を返す。</summary>
        private static int AddLine(SerializedProperty path)
        {
            var lines = PathLines(path);
            lines.arraySize++;
            var line = lines.arraySize - 1;
            // 配列を伸ばすと末尾が複製されるので、点を空にする
            LinePoints(path, line).arraySize = 0;
            return line;
        }

        /// <summary>点のなくなった線を消す(今の線の番号もずらす)。</summary>
        private void RemoveEmptyLines(SerializedProperty path)
        {
            for (var line = LineCount(path) - 1; line >= 0; line--)
            {
                if (PointCount(path, line) > 0) continue;
                PathLines(path).DeleteArrayElementAtIndex(line);
                if (_pathLine > line) _pathLine--;
            }

            _pathLine = CurrentLine(path);
        }

        /// <summary>線 line の点を a → b の直線上の比 ts の位置に置き直す。端(0 と 1)の表示用の位置だけは引き継ぐ。</summary>
        private void PlacePathLine(SerializedProperty path, int line, Vector3 a, Vector3 b, Vector2 uvA, Vector2 uvB,
            IReadOnlyList<float> ts, Texture source)
        {
            LinePoints(path, line).arraySize = ts.Count;
            for (var i = 0; i < ts.Count; i++)
            {
                var t = Mathf.Clamp01(ts[i]);
                var uv = t <= 1e-4f ? uvA : t >= 1f - 1e-4f ? uvB : NoUv;
                WritePoint(path, line, i, Vector3.Lerp(a, b, t), uv, source);
            }
        }

        /// <summary>線 line を、形を保ったまま弧長の比 ts の位置の点に置き直す(Relative で点の数をストップにそろえる)。</summary>
        private void ResampleLine(SerializedProperty path, int line, IReadOnlyList<float> ts, Texture source)
        {
            var before = ReadLinePositions(path, line);
            if (before.Count < 2) return;
            var lengths = ColorPathConfig.ArcLengths(before, PathSpaceOf(path), TextureSize(source));
            var total = lengths[lengths.Length - 1];
            var result = new List<Vector3>(ts.Count);
            foreach (var t in ts)
            {
                var at = Mathf.Clamp01(t) * total;
                var k = 0;
                while (k + 2 < before.Count && lengths[k + 1] < at) k++;
                var span = lengths[k + 1] - lengths[k];
                var s = span > 1e-9f ? Mathf.Clamp01((at - lengths[k]) / span) : 0f;
                result.Add(Vector3.Lerp(before[k], before[k + 1], s));
            }

            LinePoints(path, line).arraySize = result.Count;
            for (var i = 0; i < result.Count; i++) WritePoint(path, line, i, result[i], NoUv, source);
        }

        /// <summary>Relative で、reference 以外の点が 2 個以上ある線の点の数を ts にそろえる(形は保つ)。</summary>
        private void SyncOtherLines(SerializedProperty path, int reference, IReadOnlyList<float> ts, Texture source)
        {
            for (var line = 0; line < LineCount(path); line++)
            {
                if (line == reference) continue;
                var count = PointCount(path, line);
                if (count >= 2 && count != ts.Count) ResampleLine(path, line, ts, source);
            }
        }

        /// <summary>
        /// 線の末尾から、その線の全長の q 倍だけ、最後の線分の向きに伸ばした点
        /// (Relative で、ほかの線の末尾に足した点と同じ割合で伸ばす)。テクスチャ上は画像の中に収める。
        /// </summary>
        private static Vector3 ExtendLine(IReadOnlyList<Vector3> points, float q, ColorPathSpace space, Vector2 size)
        {
            var n = points.Count;
            var last = ColorPathConfig.Metric(points[n - 1], space, size);
            var dir = last - ColorPathConfig.Metric(points[n - 2], space, size);
            if (dir.sqrMagnitude < 1e-12f) dir = last - ColorPathConfig.Metric(points[0], space, size);
            if (dir.sqrMagnitude < 1e-12f) dir = Vector3.down;
            var lengths = ColorPathConfig.ArcLengths(points, space, size);
            var metric = last + dir.normalized * (q * lengths[n - 1]);
            if (space == ColorPathSpace.Model) return metric;
            return new Vector3(Mathf.Clamp01(metric.x / Mathf.Max(size.x, 1f)), Mathf.Clamp01(metric.y / Mathf.Max(size.y, 1f)),
                0f);
        }

        /// <summary>既定の線(テクスチャ上は画像の上端→下端、モデル上は対象のメッシュの上端→下端)の端点。</summary>
        private void DefaultPathLine(ColorPathSpace space, Texture source, out Vector3 a, out Vector3 b)
        {
            if (space == ColorPathSpace.Texture)
            {
                a = new Vector3(0.5f, 1f, 0f);
                b = new Vector3(0.5f, 0f, 0f);
                return;
            }

            var bounds = ModelBounds(source);
            a = bounds.center + Vector3.up * bounds.extents.y;
            b = bounds.center - Vector3.up * bounds.extents.y;
        }

        /// <summary>今のレイヤーのストップの位置を、小さい順に。</summary>
        private float[] SortedStopPositions()
        {
            return StopsOf(CurrentColorLayer(layerName)).Select(s => s.position).OrderBy(p => p).ToArray();
        }

        /// <summary>
        /// 線を 1 本にして、既定の線に点を置き直す。Relative なら、ストップの数だけストップの位置に置く(見た目が変わらないように)。
        /// </summary>
        private void ResetPathPoints(SerializedProperty path, Texture source, bool relative)
        {
            DefaultPathLine(PathSpaceOf(path), source, out var a, out var b);
            var positions = SortedStopPositions();
            var ts = relative && positions.Length >= SpoitableColorPreset.MinStops &&
                     positions.Length <= SpoitableColorPreset.MaxStops
                ? (IReadOnlyList<float>)positions
                : new[] { 0f, 1f };
            PathLines(path).arraySize = 1;
            path.FindPropertyRelative(nameof(ColorPath.uvHintTexture)).objectReferenceValue = null;
            PlacePathLine(path, 0, a, b, NoUv, NoUv, ts, source);
            _pathLine = 0;
        }

        private void SetPathMode(SerializedProperty path, ColorPathMode next, Texture source)
        {
            SerializedObject preset = null;
            var size = TextureSize(source);
            var reference = ReferenceLine(path);
            if (PathModeOf(path) == ColorPathMode.Relative && next == ColorPathMode.Normal && reference >= 0)
            {
                // Relative から Normal に戻すときは、今見えている位置(今の線の点の比)をストップに書いて、見た目を保つ。
                // 明るさに戻すときは書かない(ストップの位置は明るさのしきい値なので、比を書くと色の当たり方が変わる)
                var ratios = ColorPathConfig.Ratios(ReadLinePositions(path, reference), PathSpaceOf(path), size);
                var relative = ColorPathConfig.RelativeStops(StopsOf(CurrentColorLayer(layerName)), ratios);
                if (relative != null)
                {
                    preset = RebuildStops(relative.Select(s => s.color).ToList(), relative.Select(s => s.position).ToList());
                }
            }

            if (next != ColorPathMode.Luminance && TotalPoints(path) == 0)
            {
                // 点がまだ 1 つもなければ、既定の線を置いてすぐ色が並ぶようにする
                // (1 点だけの線があるときは、置いた点を消さずにそのまま切り替える)
                ResetPathPoints(path, source, next == ColorPathMode.Relative);
            }
            else if (next == ColorPathMode.Relative && reference >= 0)
            {
                // 別のテクスチャで拾った表示用の位置が残っていたら、先に今のテクスチャのものにそろえる
                // (下で端の点の位置を引き継ぐので、古い位置のまま今のテクスチャのものとして書かないように)
                EnsurePathHints(path, source);
                var stops = StopsOf(CurrentColorLayer(layerName));
                var n = PointCount(path, reference);
                if (n > SpoitableColorPreset.MaxStops)
                {
                    SetMessage($"Relative Grade の点は {SpoitableColorPreset.MaxStops} 個までです(今の経路は {n} 個)。点を減らしてから切り替えてください");
                    return;
                }

                IReadOnlyList<float> ts = stops.Select(s => s.position).OrderBy(p => p).ToArray();
                if (n != stops.Length)
                {
                    var positions = ReadLinePositions(path, reference);
                    if (n == 2 && stops.Length >= SpoitableColorPreset.MinStops)
                    {
                        // 直線なら、ストップの位置に点を置き直す(色の並びが今と同じになる)
                        var uvA = PathPointField(path, reference, 0, nameof(ColorPathPoint.uv)).vector2Value;
                        var uvB = PathPointField(path, reference, 1, nameof(ColorPathPoint.uv)).vector2Value;
                        PlacePathLine(path, reference, positions[0], positions[1], uvA, uvB, ts, source);
                    }
                    else
                    {
                        // 折れ線なら点は動かさず、今の点の位置の色でストップを作り直す
                        var ratios = ColorPathConfig.Ratios(positions, PathSpaceOf(path), size);
                        preset = RebuildStops(EvaluateStops(stops, ratios), ratios);
                        ts = ratios;
                    }
                }

                // ほかの線も、形を保ったまま同じ数の点にそろえる(同じ文字の点が同じストップに対応する)
                SyncOtherLines(path, reference, ts, source);
            }

            path.FindPropertyRelative(nameof(ColorPath.mode)).intValue = (int)next;
            ApplyPathChange(preset, "色の並べ方を変える");
        }

        private void SetPathSpace(SerializedProperty path, ColorPathSpace next, Texture source)
        {
            if (PathSpaceOf(path) == next) return;
            path.FindPropertyRelative(nameof(ColorPath.space)).intValue = (int)next;
            // テクスチャ上(UV)とモデル上(メートル)では座標の意味が違うので、既定の線 1 本に置き直す
            ResetPathPoints(path, source, PathModeOf(path) == ColorPathMode.Relative);
            ApplyPathChange(null, "色の経路の点の場所を変える");
        }

        /// <summary>
        /// Relative で点とストップの数が違う線をそろえる。今の線が合っていなければ、今の線の点の位置の色でストップを作り直し
        /// (描いているのは Normal としての色なので、見た目はほぼそのまま)、ほかの線は形を保ったまま点を置き直す。
        /// </summary>
        private void MatchStopsToPath(SerializedProperty path, Texture source)
        {
            var reference = ReferenceLine(path);
            if (reference < 0) return;
            var positions = ReadLinePositions(path, reference);
            var stops = StopsOf(CurrentColorLayer(layerName));
            SerializedObject preset = null;
            IReadOnlyList<float> ts;
            if (positions.Count != stops.Length)
            {
                if (positions.Count < SpoitableColorPreset.MinStops || positions.Count > SpoitableColorPreset.MaxStops)
                {
                    SetMessage($"Relative Grade の点は {SpoitableColorPreset.MinStops}〜{SpoitableColorPreset.MaxStops} 個にしてください");
                    return;
                }

                var ratios = ColorPathConfig.Ratios(positions, PathSpaceOf(path), TextureSize(source));
                preset = RebuildStops(EvaluateStops(stops, ratios), ratios);
                ts = ratios;
            }
            else
            {
                ts = stops.Select(s => s.position).OrderBy(p => p).ToArray();
            }

            SyncOtherLines(path, reference, ts, source);
            ApplyPathChange(preset, "点とストップの数をそろえる");
        }

        /// <summary>今の線の点の並びを逆にする(色の向きが反対になる)。</summary>
        private void ReversePath(SerializedProperty path)
        {
            var line = CurrentLine(path);
            var n = PointCount(path, line);
            if (n < 2) return;
            var points = new List<(Vector3 position, Vector2 uv)>(n);
            for (var i = 0; i < n; i++)
            {
                points.Add((PathPointField(path, line, i, nameof(ColorPathPoint.position)).vector3Value,
                    PathPointField(path, line, i, nameof(ColorPathPoint.uv)).vector2Value));
            }

            for (var i = 0; i < n; i++)
            {
                PathPointField(path, line, i, nameof(ColorPathPoint.position)).vector3Value = points[n - 1 - i].position;
                PathPointField(path, line, i, nameof(ColorPathPoint.uv)).vector2Value = points[n - 1 - i].uv;
            }

            var selected = SelectedPathPoint(path);
            if (selected >= 0) _pathPoint = n - 1 - selected;
            ApplyPathChange(null, "色の経路を逆向きにする");
        }

        /// <summary>新しい線を始める(次のクリックからその線に点を置く)。空の線が残っていれば、それを使う。</summary>
        private void StartNewLine()
        {
            var path = PathProperty(true);
            for (var line = 0; line < LineCount(path); line++)
            {
                if (PointCount(path, line) != 0) continue;
                _pathLine = line;
                ForgetPathPoint();
                SetMessage($"キャンバスをクリックすると、経路 {line + 1} の点を置けます");
                return;
            }

            if (LineCount(path) >= ColorPath.MaxLines)
            {
                SetMessage($"経路は {ColorPath.MaxLines} 本までです");
                return;
            }

            var added = AddLine(path);
            ApplyPathChange(null, "色の経路を足す");
            _pathLine = added;
            ForgetPathPoint();
            SetMessage($"キャンバスをクリックすると、経路 {added + 1} の点({PathLabel(added, 0, added + 1)}, " +
                       $"{PathLabel(added, 1, added + 1)}…)を置けます");
        }

        /// <summary>今の線を消す。</summary>
        private void DeleteCurrentLine()
        {
            var path = PathProperty(false);
            if (path == null || LineCount(path) == 0) return;
            var line = CurrentLine(path);
            PathLines(path).DeleteArrayElementAtIndex(line);
            _pathLine = Mathf.Max(0, line - 1);
            ForgetPathPoint();
            ApplyPathChange(null, "色の経路を消す");
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>
        /// 線 line の点 index を消す。Relative で点とストップの数が合っている線なら、対応するストップと、ほかの線の同じ文字の点も
        /// 消す(1 回の Undo にまとめる)。消したら true。
        /// </summary>
        private bool DeletePathPoint(int line, int index, Texture source)
        {
            var path = PathProperty(false);
            var count = PointCount(path, line);
            if (index < 0 || index >= count) return false;
            var size = TextureSize(source);
            var stops = StopsOf(CurrentColorLayer(layerName));
            SerializedObject preset = null;

            if (PathModeOf(path) == ColorPathMode.Relative && count >= 2 && count == stops.Length)
            {
                if (count <= SpoitableColorPreset.MinStops)
                {
                    SetMessage($"Relative Grade では点を {SpoitableColorPreset.MinStops} 個より少なくできません");
                    return false;
                }

                var relative = ColorPathConfig.RelativeStops(stops,
                    ColorPathConfig.Ratios(ReadLinePositions(path, line), PathSpaceOf(path), size));
                // 同じ文字の点を、ストップと数が合っているほかの線からも消す(足すときと同じ線だけ。
                // 数の合っていない線や作りかけの線は、ストップと対応していないので触らない)
                for (var other = 0; other < LineCount(path); other++)
                {
                    if (other == line || PointCount(path, other) != count) continue;
                    LinePoints(path, other).DeleteArrayElementAtIndex(index);
                }

                LinePoints(path, line).DeleteArrayElementAtIndex(index);

                if (relative != null)
                {
                    var colors = relative.Select(s => s.color).ToList();
                    colors.RemoveAt(index);
                    preset = RebuildStops(colors,
                        ColorPathConfig.Ratios(ReadLinePositions(path, line), PathSpaceOf(path), size));
                }
            }
            else
            {
                LinePoints(path, line).DeleteArrayElementAtIndex(index);
            }

            _pathLine = line;
            if (LineCount(path) > 1) RemoveEmptyLines(path);
            ForgetPathPoint();
            ApplyPathChange(preset, "色の経路の点を消す");
            SceneView.RepaintAll();
            Repaint();
            // 点の数で右パネルの欄(警告やバーのロック)が変わるので、このフレームはここまで
            GUIUtility.ExitGUI();
            return true;
        }

        private bool DeleteSelectedPathPoint(Texture source)
        {
            var path = PathProperty(false);
            var selected = SelectedPathPoint(path);
            return selected >= 0 && DeletePathPoint(CurrentLine(path), selected, source);
        }

        // ------------------------------------------------------------------ キャンバスでの入力

        /// <summary>マウスの下の点(選んでいる点を優先)。なければ false。</summary>
        private bool HitPathPoint(SerializedProperty path, Texture source, out int hitLine, out int hitIndex)
        {
            hitLine = -1;
            hitIndex = -1;
            var limit = GradientHandleRadius + 2f;
            var selected = SelectedPathPoint(path);
            if (selected >= 0)
            {
                var uv = PathPointUv(path, _pathLine, selected, source);
                if (uv.HasValue && Vector2.Distance(UvToScreen(uv.Value), _mouse) <= limit)
                {
                    hitLine = _pathLine;
                    hitIndex = selected;
                    return true;
                }
            }

            var bestDistance = limit;
            for (var line = 0; line < LineCount(path); line++)
            {
                for (var i = 0; i < PointCount(path, line); i++)
                {
                    var uv = PathPointUv(path, line, i, source);
                    if (!uv.HasValue) continue;
                    var d = Vector2.Distance(UvToScreen(uv.Value), _mouse);
                    if (d > bestDistance) continue;
                    bestDistance = d;
                    hitLine = line;
                    hitIndex = i;
                }
            }

            return hitIndex >= 0;
        }

        /// <summary>マウスが線分の近く(画面で PathInsertDistance 以内)なら、その線・線分の番号と線分の上の位置(画面で 0〜1)。</summary>
        private bool HitPathSegment(SerializedProperty path, Texture source, out int hitLine, out int segment)
        {
            hitLine = -1;
            segment = -1;
            var bestDistance = PathInsertDistance;
            for (var line = 0; line < LineCount(path); line++)
            {
                for (var k = 0; k + 1 < PointCount(path, line); k++)
                {
                    var uvA = PathPointUv(path, line, k, source);
                    var uvB = PathPointUv(path, line, k + 1, source);
                    if (!uvA.HasValue || !uvB.HasValue) continue;
                    var a = UvToScreen(uvA.Value);
                    var b = UvToScreen(uvB.Value);
                    var d = b - a;
                    var s = d.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(_mouse - a, d) / d.sqrMagnitude) : 0f;
                    var distance = Vector2.Distance(_mouse, a + d * s);
                    if (distance > bestDistance) continue;
                    bestDistance = distance;
                    hitLine = line;
                    segment = k;
                }
            }

            return segment >= 0;
        }

        private void BeginColorPath(Texture source)
        {
            _pathDragging = false;
            _pathMoved = false;
            _pathDownMouse = _mouse;

            // 割り当てを作るのは、実際に書き込むときだけ(クリックしただけで割り当てを増やさない)
            var path = PathProperty(false);
            if (path != null && HitPathPoint(path, source, out var hitLine, out var hitIndex))
            {
                if (Event.current.control || Event.current.command)
                {
                    DeletePathPoint(hitLine, hitIndex, source);
                    return;
                }

                _pathLine = hitLine;
                _pathPoint = hitIndex;
                _pathPointLayer = layerName;
                _pathDragging = true;
                _toolDragging = true;
                SceneView.RepaintAll();
                return;
            }

            var mode = PathModeOf(path);
            var startedFromLuminance = mode == ColorPathMode.Luminance;
            var space = PathSpaceOf(path);
            var size = TextureSize(source);

            // 線分の近くなら、その線の間に入れる。それ以外は今の線の末尾に足す
            var segment = -1;
            var segmentLine = -1;
            var line = CurrentLine(path);
            var inserting = path != null && HitPathSegment(path, source, out segmentLine, out segment);
            if (inserting) line = segmentLine;
            var count = PointCount(path, line);
            var index = inserting ? segment + 1 : count;

            var stops = StopsOf(CurrentColorLayer(layerName));
            // Relative で点とストップの数が合っている線に足すなら、ストップも足し、ほかの合っている線にも同じ文字の点を足す
            var sync = mode == ColorPathMode.Relative && count >= 2 && count == stops.Length;
            var syncLines = sync
                ? Enumerable.Range(0, LineCount(path)).Where(l => l != line && PointCount(path, l) == count).ToList()
                : new List<int>();
            var lineLimit = mode == ColorPathMode.Relative ? SpoitableColorPreset.MaxStops : ColorPath.MaxPoints;
            if (count >= lineLimit)
            {
                SetMessage(mode == ColorPathMode.Relative
                    ? $"Relative Grade の点はストップと同じ {lineLimit} 個までです"
                    : $"1 本の経路の点は {lineLimit} 個までです");
                return;
            }

            if (TotalPoints(path) + 1 + syncLines.Count > ColorPath.MaxTotalPoints)
            {
                SetMessage($"点はすべての経路を合わせて {ColorPath.MaxTotalPoints} 個までです");
                return;
            }

            var uv = ToUv(_mouse);
            Vector3 position = uv;
            if (space == ColorPathSpace.Model && !ModelPositionAt(source, uv, out position)) return;

            List<Color> relativeColors = null;
            var along = 0f; // 入れた点の、線分の上の位置(ほかの線の同じ線分にも同じ割合で入れる)
            var extend = 0f; // 末尾に足した点の、線の全長に対する伸ばした長さ(ほかの線も同じ割合で伸ばす)
            if (sync)
            {
                var before = ReadLinePositions(path, line);
                var ratios = ColorPathConfig.Ratios(before, space, size);
                var relative = ColorPathConfig.RelativeStops(stops, ratios);
                if (relative != null)
                {
                    // 足す前の経路で、その点が今どの位置(t)にあるか。シェーダーと同じく、いちばん近い線分への射影で求める
                    var t = inserting ? ColorPathConfig.ProjectT(before, space, size, position) : 1f;
                    relativeColors = relative.Select(s => s.color).ToList();
                    relativeColors.Insert(index, EvaluateStops(relative, new[] { t })[0]);
                }

                var q = ColorPathConfig.Metric(position, space, size);
                if (inserting)
                {
                    var a = ColorPathConfig.Metric(before[segment], space, size);
                    var d = ColorPathConfig.Metric(before[segment + 1], space, size) - a;
                    along = Mathf.Clamp01(Vector3.Dot(q - a, d) / Mathf.Max(Vector3.Dot(d, d), 1e-12f));
                }
                else
                {
                    var total = ColorPathConfig.ArcLengths(before, space, size)[before.Count - 1];
                    var last = ColorPathConfig.Metric(before[before.Count - 1], space, size);
                    extend = total > 1e-9f ? Vector3.Distance(last, q) / total : 0f;
                }
            }

            path = PathProperty(true);
            if (mode == ColorPathMode.Luminance)
            {
                // 明るさのままクリックしたら、Normal Grade にして点を置き始める
                path.FindPropertyRelative(nameof(ColorPath.mode)).intValue = (int)ColorPathMode.Normal;
                mode = ColorPathMode.Normal;
            }

            if (LineCount(path) == 0) AddLine(path);
            line = Mathf.Clamp(line, 0, LineCount(path) - 1);
            InsertPoint(path, line, index, position, space == ColorPathSpace.Model ? uv : NoUv, source);

            SerializedObject preset = null;
            if (relativeColors != null)
            {
                foreach (var other in syncLines)
                {
                    var points = ReadLinePositions(path, other);
                    var added = inserting
                        ? Vector3.Lerp(points[segment], points[segment + 1], along)
                        : ExtendLine(points, extend, space, size);
                    InsertPoint(path, other, index, added, NoUv, source);
                }

                preset = RebuildStops(relativeColors, ColorPathConfig.Ratios(ReadLinePositions(path, line), space, size));
            }

            ApplyPathChange(preset, inserting ? "色の経路に点を入れる" : "色の経路に点を足す");
            _pathLine = line;
            _pathPoint = index;
            _pathPointLayer = layerName;
            _pathDragging = true;
            _toolDragging = true;
            if (PointCount(path, line) == 1) SetMessage("もう 1 点置くと、点を結んだ線に沿って色が並びます");
            else if (syncLines.Count > 0) SetMessage($"ほかの経路 {syncLines.Count} 本にも同じ文字の点を足しました");
            if (startedFromLuminance)
            {
                // 明るさから切り替えた最初の 1 回は置くだけにする(以前は設定バーの欄が増えてキャンバスの ID が変わり、
                // つかんだままだと MouseUp を受け取れずに残った。設定バーはなくなったが、念のため同じ扱いにしておく)
                EndColorPath();
                GUIUtility.hotControl = 0;
            }

            SceneView.RepaintAll();
            Repaint();
            // 点の数や種類で右パネルの欄(警告やバーのロック)が変わるので、このフレームはここまで
            GUIUtility.ExitGUI();
        }

        private void DragColorPath(Texture source)
        {
            if (!_pathDragging) return;
            if (!_pathMoved)
            {
                // 少し動かすまでは、クリックしただけとして何も書かない
                if (Vector2.Distance(_mouse, _pathDownMouse) < 3f) return;
                _pathMoved = true;
            }

            var path = PathProperty(false);
            var selected = SelectedPathPoint(path);
            if (selected < 0) return;
            var uv = ToUv(_mouse);
            if (PathSpaceOf(path) == ColorPathSpace.Model)
            {
                // モデルの表面から外れたら、最後の位置のままにする
                if (!ModelPositionAt(source, uv, out var position)) return;
                WritePoint(path, _pathLine, selected, position, uv, source);
                ComponentObject().ApplyModifiedProperties();
                SceneView.RepaintAll();
            }
            else
            {
                WritePoint(path, _pathLine, selected, uv, NoUv, source);
                ComponentObject().ApplyModifiedProperties();
            }
        }

        private void EndColorPath()
        {
            _pathDragging = false;
            _pathMoved = false;
            _toolDragging = false;
        }

        // ------------------------------------------------------------------ キャンバスへの表示

        /// <summary>
        /// 折れ線と点(A, B, C…。線が複数なら A¹, A²…)を描く。今の線ではない線は薄く、モデルから浮かせた点は
        /// いちばん近い表面の位置に薄く描く。キャンバスの外にはみ出す部分は描かない。
        /// </summary>
        private void DrawColorPathOverlay(Texture source)
        {
            if (_pathPointLayer != layerName) ForgetPathPoint();
            var path = PathProperty(false);
            var lines = LineCount(path);
            if (lines == 0) return;
            var size = TextureSize(source);
            var current = CurrentLine(path);
            var selected = SelectedPathPoint(path);
            var previous = Handles.color;

            for (var line = 0; line < lines; line++)
            {
                var count = PointCount(path, line);
                if (count == 0) continue;
                var colors = PathPointColors(path, line, size);
                var uvs = new Vector2?[count];
                for (var i = 0; i < count; i++) uvs[i] = PathPointUv(path, line, i, source);
                var lineAlpha = PathLineAlpha(line, current, lines);

                for (var k = 0; k + 1 < count; k++)
                {
                    if (uvs[k].HasValue && uvs[k + 1].HasValue)
                    {
                        DrawPathSegment(UvToScreen(uvs[k].Value), UvToScreen(uvs[k + 1].Value), lineAlpha);
                    }
                }

                var guiColor = GUI.color;
                for (var i = 0; i < count; i++)
                {
                    if (!uvs[i].HasValue) continue;
                    var p = UvToScreen(uvs[i].Value);
                    // モデルから離した点は、さらに薄く
                    var alpha = PathPointOffSurface(path, line, i, source) ? lineAlpha * 0.55f : lineAlpha;
                    if (!DrawPathDisc(p, colors[i], line == current && i == selected, alpha)) continue;
                    var label = new Rect(p.x + GradientHandleRadius + 2f, p.y - 9f, 30f, 18f);
                    if (_canvas.Contains(label.min) && _canvas.Contains(label.max))
                    {
                        GUI.color = new Color(guiColor.r, guiColor.g, guiColor.b, guiColor.a * alpha);
                        GUI.Label(label, PathLabel(line, i, lines), PointLabel);
                        GUI.color = guiColor;
                    }
                }
            }

            Handles.color = previous;
        }

        /// <summary>選んでいない経路の不透明度(線・点・名前)。</summary>
        private const float UnselectedPathAlpha = 0.3f;

        private static float PathLineAlpha(int line, int current, int lineCount)
        {
            return lineCount > 1 && line != current ? UnselectedPathAlpha : 1f;
        }

        private void DrawPathSegment(Vector2 a, Vector2 b, float alpha)
        {
            if (!ClipSegment(ref a, ref b, _canvas)) return;
            Handles.color = new Color(0f, 0f, 0f, 0.8f * alpha);
            Handles.DrawAAPolyLine(4f, a, b);
            Handles.color = new Color(1f, 1f, 1f, alpha);
            Handles.DrawAAPolyLine(2f, a, b);
        }

        /// <summary>点の色で塗った丸いつまみ。alpha で全体を薄くする。キャンバスに収まらないものは描かない(描いたら true)。</summary>
        private bool DrawPathDisc(Vector2 center, Color color, bool selected, float alpha)
        {
            var r = GradientHandleRadius + 2f;
            if (center.x - r < _canvas.xMin || center.x + r > _canvas.xMax || center.y - r < _canvas.yMin ||
                center.y + r > _canvas.yMax)
            {
                return false;
            }

            Handles.color = selected ? SccGui.AccentColor : new Color(0f, 0f, 0f, alpha);
            Handles.DrawSolidDisc(center, Vector3.forward, r);
            Handles.color = new Color(1f, 1f, 1f, alpha);
            Handles.DrawSolidDisc(center, Vector3.forward, GradientHandleRadius);
            Handles.color = new Color(color.r, color.g, color.b, alpha);
            Handles.DrawSolidDisc(center, Vector3.forward, GradientHandleRadius - 2f);
            return true;
        }

        // ------------------------------------------------------------------ シーンビュー

        /// <summary>モデル上の経路のとき、点と線をシーンビューに出し、ハンドルで動かせるようにする。</summary>
        private void OnSceneGUIColorPath(SceneView view)
        {
            if (tool != Tool.ColorPath || target == null) return;
            var component = ComponentObject();
            component.Update();
            var path = PathProperty(false);
            if (path == null || PathSpaceOf(path) != ColorPathSpace.Model) return;
            var lines = LineCount(path);
            if (lines == 0) return;
            if (_pathPointLayer != layerName) ForgetPathPoint();
            var root = TargetResolver.AvatarRootOf(target);
            if (root == null) return;
            var source = CurrentSourceTexture();
            var current = CurrentLine(path);
            var selected = SelectedPathPoint(path);

            var previous = Handles.color;
            var matrix = Handles.matrix;
            Handles.matrix = root.localToWorldMatrix;
            EditorGUI.BeginChangeCheck();

            for (var line = 0; line < lines; line++)
            {
                // モデル上は距離をメートルで測るので、テクスチャの大きさは使わない
                var positions = ReadLinePositions(path, line);
                if (positions.Count == 0) continue;
                var colors = PathPointColors(path, line, Vector2.one);
                var alpha = PathLineAlpha(line, current, lines);
                if (positions.Count > 1)
                {
                    Handles.color = new Color(0f, 0f, 0f, 0.6f * alpha);
                    Handles.DrawAAPolyLine(5f, positions.ToArray());
                    Handles.color = new Color(1f, 1f, 1f, alpha);
                    Handles.DrawAAPolyLine(3f, positions.ToArray());
                }

                var guiColor = GUI.color;
                for (var i = 0; i < positions.Count; i++)
                {
                    var size = HandleUtility.GetHandleSize(positions[i]) * 0.08f;
                    var c = colors[i];
                    Handles.color = line == current && i == selected ? SccGui.AccentColor : new Color(c.r, c.g, c.b, alpha);
                    if (Handles.Button(positions[i], Quaternion.identity, size, size * 1.2f, Handles.SphereHandleCap))
                    {
                        _pathLine = line;
                        _pathPoint = i;
                        _pathPointLayer = layerName;
                        Repaint();
                    }

                    GUI.color = new Color(guiColor.r, guiColor.g, guiColor.b, guiColor.a * alpha);
                    Handles.Label(positions[i], " " + PathLabel(line, i, lines), SceneLabel);
                    GUI.color = guiColor;
                }
            }

            if (selected >= 0)
            {
                var position = PathPointField(path, current, selected, nameof(ColorPathPoint.position)).vector3Value;
                var moved = Handles.PositionHandle(position, Quaternion.identity);
                if (moved != position)
                {
                    // テクスチャ上の位置は、いちばん近い表面の UV を拾い直す(表面から離れても、キャンバスに出せるように)
                    WritePoint(path, current, selected, moved, NoUv, source);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                component.ApplyModifiedProperties();
                Repaint();
            }

            Handles.matrix = matrix;
            Handles.color = previous;
        }

        // ------------------------------------------------------------------ 右パネル(経路タブ)

        private void DrawPathModePopup(SerializedProperty path, ColorPathMode mode, float width, Texture source)
        {
            var index = Array.IndexOf(PathModes, mode);
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.Popup(Mathf.Max(0, index), PathModeLabels, GUILayout.Width(width));
            if (!EditorGUI.EndChangeCheck() || next == index) return;
            if (path == null) path = PathProperty(true);
            SetPathMode(path, PathModes[next], source);
            ForgetPathPoint();
            SceneView.RepaintAll();
            GUIUtility.ExitGUI(); // 種類ごとに欄が変わるので、このフレームはここまで
        }

        private void DrawPathSpacePopup(SerializedProperty path, float width, Texture source)
        {
            var space = PathSpaceOf(path);
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.Popup((int)space, PathSpaceLabels, GUILayout.Width(width));
            if (!EditorGUI.EndChangeCheck() || next == (int)space) return;
            SetPathSpace(path, (ColorPathSpace)next, source);
            ForgetPathPoint();
            SceneView.RepaintAll();
            GUIUtility.ExitGUI();
        }

        private const float PathRowHeight = 22f;

        private static ReorderableList.Defaults s_listDefaults;

        /// <summary>
        /// レイヤー一覧と同じ見た目の部品。ReorderableList をまだ一度も描いていないと defaultBehaviours が null のことがあるので、
        /// そのときは自前で作る。
        /// </summary>
        private static ReorderableList.Defaults ListDefaults =>
            ReorderableList.defaultBehaviours ?? (s_listDefaults ??= new ReorderableList.Defaults());

        /// <summary>
        /// 経路の一覧(レイヤー一覧と同じ形)。行のクリックで今の経路を選び、下の +/− で足す・消す。
        /// mismatched は Relative で点とストップの数が合わない経路(行の点の数を警告の色にする)。
        /// </summary>
        private void DrawPathLineList(SerializedProperty path, int[] mismatched, int stopCount)
        {
            var lines = LineCount(path);
            var rows = Mathf.Max(1, lines);
            var current = CurrentLine(path);
            var e = Event.current;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent("経路",
                        "1 つのレイヤーに経路を何本も置ける(A¹B¹…、A²B²…)。塗りマスクの塊ごとに、点が乗っている経路が効く"),
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{lines}/{ColorPath.MaxLines} 本", EditorStyles.miniLabel);
            }

            var box = GUILayoutUtility.GetRect(0f, rows * PathRowHeight + 4f, GUILayout.ExpandWidth(true));
            if (e.type == EventType.Repaint)
            {
                ListDefaults.boxBackground.Draw(box, false, false, false, false);
            }

            for (var i = 0; i < rows; i++)
            {
                var row = new Rect(box.x + 1f, box.y + 2f + i * PathRowHeight, box.width - 2f, PathRowHeight);
                var exists = i < lines;
                var selected = exists && i == current;
                if (e.type == EventType.Repaint)
                {
                    if (selected) EditorGUI.DrawRect(row, SccGui.SelectedRowColor);
                    else if (exists && row.Contains(e.mousePosition)) EditorGUI.DrawRect(row, SccGui.HoverRowColor);
                }

                var count = exists ? PointCount(path, i) : 0;
                GUI.Label(new Rect(row.x + 6f, row.y, 52f, row.height), $"経路 {i + 1}",
                    selected ? EditorStyles.boldLabel : EditorStyles.label);
                GUI.Label(new Rect(row.x + 60f, row.y, row.width - 112f, row.height),
                    count > 0 ? PathPointNames(i, count, lines) : selected || !exists ? "キャンバスをクリックして点を置く" : "点なし",
                    PathRowNames);

                var countRect = new Rect(row.xMax - 50f, row.y, 46f, row.height);
                if (Array.IndexOf(mismatched, i) >= 0)
                {
                    GUI.Label(countRect, new GUIContent($"{count} 点",
                        $"Relative Grade のストップ({stopCount} 個)と数が合わないので、Normal Grade として描いています"), PathRowCountWarning);
                }
                else
                {
                    GUI.Label(countRect, $"{count} 点", PathRowCount);
                }

                if (!exists || e.type != EventType.MouseDown || e.button != 0 || !row.Contains(e.mousePosition)) continue;
                if (_pathLine != i)
                {
                    _pathLine = i;
                    ForgetPathPoint();
                    SceneView.RepaintAll();
                }

                e.Use();
                Repaint();
            }

            // 下の +/−(レイヤー一覧と同じ見た目)
            var footer = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            const float width = 26f;
            var x = footer.xMax - 8f - width * 2f;
            if (e.type == EventType.Repaint)
            {
                ListDefaults.footerBackground.Draw(new Rect(x - 4f, footer.y, width * 2f + 8f, footer.height),
                    false, false, false, false);
            }

            var style = ListDefaults.preButton;
            if (GUI.Button(new Rect(x, footer.y, width, 18f),
                    new GUIContent(EditorGUIUtility.IconContent("Toolbar Plus").image,
                        $"新しい経路を足す(次のクリックからその経路に点を置く。{ColorPath.MaxLines} 本まで)"), style))
            {
                StartNewLine();
                SceneView.RepaintAll();
                Repaint();
                GUIUtility.ExitGUI(); // 行の数が変わるので、このフレームはここまで
            }

            using (new EditorGUI.DisabledScope(lines == 0))
            {
                if (GUI.Button(new Rect(x + width, footer.y, width, 18f),
                        new GUIContent(EditorGUIUtility.IconContent("Toolbar Minus").image, "選んでいる経路を消す(Ctrl+Z で戻せる)"),
                        style))
                {
                    DeleteCurrentLine();
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>行に出す点の名前。多いときは先頭と末尾だけ(A¹ B¹ C¹ … P¹)。</summary>
        private static string PathPointNames(int line, int count, int lineCount)
        {
            if (count <= 6) return string.Join(" ", Enumerable.Range(0, count).Select(k => PathLabel(line, k, lineCount)));
            return string.Join(" ", Enumerable.Range(0, 3).Select(k => PathLabel(line, k, lineCount))) + " … " +
                   PathLabel(line, count - 1, lineCount);
        }

        private static GUIStyle s_pathRowNames;
        private static GUIStyle s_pathRowCount;
        private static GUIStyle s_pathRowCountWarning;

        private static GUIStyle PathRowNames => s_pathRowNames ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        };

        private static GUIStyle PathRowCount => s_pathRowCount ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };

        private static GUIStyle PathRowCountWarning => s_pathRowCountWarning ??= new GUIStyle(PathRowCount)
        {
            normal = { textColor = SccGui.WarningColor },
        };

        /// <summary>右パネルの経路タブ(色の並べ方と、経路・点の設定)。</summary>
        private void DrawColorPathPanel(Texture source)
        {
            var path = PathProperty(false);
            var mode = PathModeOf(path);
            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 72f;
            GUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("色の並べ方",
                    "バーの色を何で当てるか。明るさ(今までどおり)か、点を結んだ線に沿った位置(Normal / Relative Grade)"));
                DrawPathModePopup(path, mode, 112f, source);
                if (GUILayout.Button(new GUIContent("編集", "キャンバスで点を置く道具(色の経路)にする"), EditorStyles.miniButton))
                {
                    tool = Tool.ColorPath;
                    SceneView.RepaintAll();
                }
            }

            if (path == null || mode == ColorPathMode.Luminance)
            {
                GUILayout.Label("点を結んだ線に沿って色を並べられます(根元→毛先など)。「編集」でキャンバスをクリックすると点を置けます。",
                    SccGui.Hint);
                EditorGUIUtility.labelWidth = labelWidth;
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("点の場所", "点をテクスチャの上に置くか、モデルの上に置くか"));
                DrawPathSpacePopup(path, 112f, source);
            }

            var shading = path.FindPropertyRelative(nameof(ColorPath.shading));
            EditorGUILayout.Slider(shading, 0f, 1f,
                new GUIContent("陰影", "元の明暗をどれだけ残すか(0 で平らな色、1 で元の明暗差のまま)"));

            var mask = target.FindMask(layerName);
            var config = ColorPathConfig.Capture(mask?.colorPath);
            var stopCount = StopsOf(CurrentColorLayer(layerName)).Length;
            var mismatched = config.MismatchedLines(stopCount);

            GUILayout.Space(6f);
            DrawPathLineList(path, mismatched, stopCount);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("選んでいる経路", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("逆向き", "今の経路の点の並びを逆にする(色の向きが反対になる)"),
                        EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)))
                {
                    ReversePath(path);
                    SceneView.RepaintAll();
                    GUIUtility.ExitGUI();
                }

                using (new EditorGUI.DisabledScope(SelectedPathPoint(path) < 0))
                {
                    if (GUILayout.Button(new GUIContent("点を消す", "選んでいる点を消す(Delete キー、Ctrl+クリックでも消せる)"),
                            EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)))
                    {
                        DeleteSelectedPathPoint(source);
                        GUIUtility.ExitGUI();
                    }
                }

                var limit = mode == ColorPathMode.Relative ? SpoitableColorPreset.MaxStops : ColorPath.MaxPoints;
                GUILayout.Label($"点 {PointCount(path, CurrentLine(path))}/{limit}", EditorStyles.miniLabel);
            }

            if (GUI.changed && PathSpaceOf(path) == ColorPathSpace.Model) SceneView.RepaintAll();

            if (!config.IsActive)
            {
                EditorGUILayout.HelpBox("点を 2 個以上置くと、点を結んだ線に沿って色が並びます(今は明るさで色を決めています)。", MessageType.Info);
            }
            else if (mismatched.Length > 0)
            {
                var names = string.Join("・", mismatched.Select(i => $"経路 {i + 1}(点 {config.Lines[i].Length} 個)"));
                EditorGUILayout.HelpBox(
                    $"Relative Grade は点とストップの数をそろえる必要があります(ストップ {stopCount} 個)。" +
                    $"{names}は今は Normal Grade として描いています。", MessageType.Warning);
                if (GUILayout.Button(new GUIContent("点とストップの数をそろえる",
                        "今の経路の点の数が違えば、その点の位置の色でストップを作り直す。ほかの経路は形を保ったまま点を置き直す")))
                {
                    MatchStopsToPath(path, source);
                    GUIUtility.ExitGUI();
                }
            }
            else if (config.UsesModel && Positions(source) == null)
            {
                EditorGUILayout.HelpBox("このテクスチャを使うメッシュが見つからないので、明るさで色を決めています。", MessageType.Warning);
            }

            if (config.ActiveLineCount >= 2)
            {
                GUILayout.Label(mask?.paintMask != null
                        ? "経路が複数あるときは、塗りマスクのつながった塊ごとに、その塊に点が乗っている経路だけを使います" +
                          "(塊の中に複数あれば近いほう。点が乗っていない塊は、すべての経路から近いほう)。"
                        : "塗りマスクがないので、すべての経路から画素ごとにいちばん近いものを使います。",
                    SccGui.Hint);
                if (config.UsesModel && config.ActiveLines.Any(i => config.Uvs[i].Any(uv => uv.x < 0f)))
                {
                    EditorGUILayout.HelpBox("テクスチャ上の位置が分からない点があり、その点は塊の判定に使えません。点を少し動かすと拾い直します。",
                        MessageType.Info);
                }
            }

            if (PathSpaceOf(path) == ColorPathSpace.Model)
            {
                GUILayout.Label("キャンバスのクリックでモデル上に点を置き、シーンビューのハンドルで動かせます。" +
                                "モデルから離した点は、いちばん近い表面の位置に薄く出します。" +
                                "UV を左右で共有しているメッシュでは、左右で違う色にはできません。", SccGui.Hint);
            }

            EditorGUIUtility.labelWidth = labelWidth;
        }

        /// <summary>
        /// 色の経路を使っているレイヤーのバーの表示(Relative は今の経路の点の比の位置に出して、位置と数をロックする)。
        /// 今の経路の点の数が合っていなければ、合っている最初の経路の比を出す。
        /// </summary>
        private StopLayout PathStopLayout(string name)
        {
            if (target == null) return null;
            var config = ColorPathConfig.Capture(target.FindMask(name)?.colorPath);
            if (!config.IsActive) return null; // 明るさで色を決めている
            var layer = CurrentColorLayer(name);
            if (!config.IsRelative || layer == null) return new StopLayout { PathMode = true };
            var count = layer.stops.Count;
            var line = -1;
            if (name == layerName && config.LineMatches(_pathLine, count)) line = _pathLine;
            else
            {
                foreach (var i in config.ActiveLines)
                {
                    if (!config.LineMatches(i, count)) continue;
                    line = i;
                    break;
                }
            }

            if (line < 0) return new StopLayout { PathMode = true };
            var ratios = config.Ratios(line, CurrentTextureSize());
            var order = ColorPathConfig.RankOrder(layer.stops.Select(s => Mathf.Clamp01(s.position)).ToList());
            var positions = new float[order.Length];
            for (var k = 0; k < order.Length; k++) positions[order[k]] = ratios[k];
            return new StopLayout { Positions = positions, LockPositions = true, PathMode = true, Hint = RelativeHint };
        }

        private string ColorPathHint()
        {
            var path = PathProperty(false);
            if (PathModeOf(path) == ColorPathMode.Luminance) return "クリック: 点を置いて色の経路を始める";
            const string common = "クリック: 点を足す(線の近くなら間に)\nドラッグ: 動かす\nCtrl+クリック / Delete: 消す\n経路タブの一覧の +: 新しい経路";
            return PathSpaceOf(path) == ColorPathSpace.Model ? common + "\nシーンビュー: ハンドルで動かす" : common;
        }
    }
}
