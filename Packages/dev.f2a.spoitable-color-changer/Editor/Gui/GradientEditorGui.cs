using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// バーの表示と編集の切り替え(色の経路で使う)。null なら今までどおり(明るさで当てるバー)。
    /// </summary>
    internal sealed class StopLayout
    {
        /// <summary>ストップの番号ごとの表示位置(Relative で点の比から求めたもの)。null なら保存されている位置。</summary>
        public float[] Positions;

        /// <summary>位置と数を変えられないようにする(色だけ編集できる)。</summary>
        public bool LockPositions;

        /// <summary>経路の上の位置でバーを当てている(明るさの分布は出さず、位置の説明を経路のものにする)。</summary>
        public bool PathMode;

        /// <summary>ロック中に、バーの下に出す一言。</summary>
        public string Hint;
    }

    /// <summary>
    /// グラデーションのストップをバーの上で直接編集する欄。
    /// つまみをドラッグで移動、バーか下の帯をクリックで追加、つまみを上下に引き抜くか Delete で削除。
    /// 選んだストップの色・HEX・位置はバーの下に出す。値はすべて SerializedProperty を通す(Undo とプレビューのため)。
    /// </summary>
    internal sealed class GradientEditorGui
    {
        private static readonly int ControlHash = "SpoitableGradientEditor".GetHashCode();
        private static readonly char[] HexSeparators = { ' ', ',', '、', '→', '>', '-', '\t', '\n', '\r', '/', '|' };

        private const float HistogramHeight = 18f;
        private const float BarHeight = 22f;
        private const float HandleHeight = 16f;
        private const float RemoveDistance = 30f;

        private readonly Dictionary<string, int> _selected = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _hexDrafts = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _hexErrors = new Dictionary<string, string>();

        private string _dragKey;
        private int _dragIndex = -1;
        private bool _removing;
        private StopLayout _layout; // Draw の間だけ使う

        private bool Locked => _layout != null && _layout.LockPositions;

        /// <summary>
        /// 番号(propertyPath)で持っている表示用の状態を捨てる。レイヤーの並べ替え・削除・Undo のあとに呼ぶ
        /// (そのままだと、別のレイヤーに前のレイヤーの状態が出る)。
        /// </summary>
        public void Clear()
        {
            _selected.Clear();
            _hexDrafts.Clear();
            _hexErrors.Clear();
            if (GUIUtility.hotControl == 0)
            {
                _dragKey = null;
                _dragIndex = -1;
                _removing = false;
            }
        }

        /// <param name="stops">ColorStop のリスト</param>
        /// <param name="previews">バーの表示用テクスチャ</param>
        /// <param name="stats">元テクスチャの明るさの分布(なければ null)</param>
        /// <param name="lo">グラデーションの左端に当たる明るさ</param>
        /// <param name="hi">右端に当たる明るさ</param>
        /// <param name="layout">色の経路での表示位置の上書きとロック(なければ null)</param>
        public void Draw(SerializedProperty stops, GradientPreviews previews, LuminanceStats stats, float lo, float hi,
            StopLayout layout = null)
        {
            _layout = layout != null && layout.Positions != null && layout.Positions.Length != stops.arraySize
                ? new StopLayout { PathMode = layout.PathMode } // 数が合わない上書きは使わない
                : layout;
            if (_layout != null && _layout.PathMode) stats = null; // 明るさの分布はバーの軸と関係ない
            var key = stops.serializedObject.targetObject.GetInstanceID() + ":" + stops.propertyPath;
            if (!_selected.TryGetValue(key, out var selected)) selected = 0;
            selected = Mathf.Clamp(selected, 0, Mathf.Max(0, stops.arraySize - 1));

            var e = Event.current;
            var area = GUILayoutUtility.GetRect(10f, HistogramHeight + BarHeight + HandleHeight, GUILayout.ExpandWidth(true));
            var bar = new Rect(area.x + 6f, area.y + HistogramHeight, area.width - 12f, BarHeight);
            var histogram = new Rect(bar.x, area.y, bar.width, HistogramHeight - 2f);
            var handles = new Rect(bar.x - 6f, bar.yMax, bar.width + 12f, HandleHeight);
            var id = GUIUtility.GetControlID(ControlHash, FocusType.Keyboard, area);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                {
                    if (e.button != 0) break;
                    var interactive = new Rect(handles.x, bar.y, handles.width, bar.height + handles.height);
                    if (!interactive.Contains(e.mousePosition)) break;
                    var hit = HitHandle(stops, bar, handles, e.mousePosition, selected);
                    if (hit < 0)
                    {
                        if (Locked || stops.arraySize >= SpoitableColorPreset.MaxStops) break;
                        var t = Mathf.Clamp01((e.mousePosition.x - bar.x) / bar.width);
                        hit = AddStop(stops, t);
                    }

                    selected = hit;
                    if (Locked)
                    {
                        // 位置は動かせないので、選ぶだけ
                        GUIUtility.keyboardControl = id;
                        e.Use();
                        break;
                    }

                    _dragKey = key;
                    _dragIndex = hit;
                    _removing = false;
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = id;
                    e.Use();
                    break;
                }

                case EventType.MouseDrag:
                {
                    if (GUIUtility.hotControl != id || _dragKey != key || _dragIndex < 0 || _dragIndex >= stops.arraySize)
                    {
                        break;
                    }

                    var t = Mathf.Clamp01((e.mousePosition.x - bar.x) / bar.width);
                    Position(stops, _dragIndex).floatValue = t;
                    _removing = stops.arraySize > SpoitableColorPreset.MinStops &&
                                Mathf.Abs(e.mousePosition.y - handles.center.y) > RemoveDistance;
                    GUI.changed = true;
                    e.Use();
                    break;
                }

                case EventType.MouseUp:
                {
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    if (_dragKey == key && _removing && _dragIndex >= 0 && _dragIndex < stops.arraySize)
                    {
                        stops.DeleteArrayElementAtIndex(_dragIndex);
                        selected = Mathf.Clamp(_dragIndex - 1, 0, stops.arraySize - 1);
                        GUI.changed = true;
                    }

                    _dragKey = null;
                    _dragIndex = -1;
                    _removing = false;
                    e.Use();
                    break;
                }

                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl != id || Locked) break;
                    if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) &&
                        stops.arraySize > SpoitableColorPreset.MinStops)
                    {
                        stops.DeleteArrayElementAtIndex(selected);
                        selected = Mathf.Clamp(selected - 1, 0, stops.arraySize - 1);
                        GUI.changed = true;
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow)
                    {
                        var step = (e.shift ? 0.05f : 0.01f) * (e.keyCode == KeyCode.LeftArrow ? -1f : 1f);
                        var p = Position(stops, selected);
                        p.floatValue = Mathf.Clamp01(p.floatValue + step);
                        GUI.changed = true;
                        e.Use();
                    }

                    break;

                case EventType.Repaint:
                    DrawHistogram(histogram, stats, lo, hi);
                    DrawBar(bar, stops, previews, key);
                    DrawHandles(stops, bar, handles, selected, GUIUtility.keyboardControl == id,
                        _dragKey == key && _removing ? _dragIndex : -1);
                    break;
            }

            _selected[key] = selected;
            if (stops.arraySize > 0)
            {
                DrawSelectedStop(stops, key, selected);
                DrawTools(stops, key);
            }

            _layout = null;
        }

        // ------------------------------------------------------------------ 描画

        private static void DrawHistogram(Rect rect, LuminanceStats stats, float lo, float hi)
        {
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, SccGui.Pro ? 0.18f : 0.08f));
            if (stats == null || hi - lo < 1e-4f) return;
            var bins = stats.Histogram;
            var color = SccGui.Pro ? new Color(0.75f, 0.75f, 0.75f, 0.8f) : new Color(0.3f, 0.3f, 0.3f, 0.7f);
            // 分布は元の明るさの軸。グラデーションの左端 = lo、右端 = hi になるよう引き伸ばして描く
            for (var i = 0; i < bins.Length; i++)
            {
                var l0 = (float)i / bins.Length;
                var l1 = (float)(i + 1) / bins.Length;
                var t0 = Mathf.Clamp01((l0 - lo) / (hi - lo));
                var t1 = Mathf.Clamp01((l1 - lo) / (hi - lo));
                if (t1 <= t0) continue;
                var h = rect.height * bins[i];
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * t0, rect.yMax - h, Mathf.Max(1f, rect.width * (t1 - t0)), h),
                    color);
            }
        }

        private void DrawBar(Rect bar, SerializedProperty stops, GradientPreviews previews, string key)
        {
            EditorGUI.DrawRect(new Rect(bar.x - 1f, bar.y - 1f, bar.width + 2f, bar.height + 2f), SccGui.SeparatorColor);
            var texture = previews.Get(key, DisplayStops(stops));
            if (texture != null) GUI.DrawTexture(bar, texture, ScaleMode.StretchToFill);
        }

        private void DrawHandles(SerializedProperty stops, Rect bar, Rect handles, int selected, bool focused,
            int removing)
        {
            // 選んだつまみは最後に描いて、重なっても上に来るようにする
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < stops.arraySize; i++)
                {
                    if ((i == selected) != (pass == 1)) continue;
                    var x = bar.x + bar.width * PositionOf(stops, i);
                    var color = ColorOf(stops, i).colorValue;
                    var alpha = i == removing ? 0.35f : 1f;
                    var outline = i == selected
                        ? (focused ? SccGui.AccentColor : (SccGui.Pro ? Color.white : Color.black))
                        : new Color(0f, 0f, 0f, 0.85f);
                    outline.a *= alpha;

                    // バーの上の位置の線
                    EditorGUI.DrawRect(new Rect(x - 1f, bar.y, 2f, bar.height), new Color(0f, 0f, 0f, 0.6f * alpha));
                    EditorGUI.DrawRect(new Rect(x - 0.5f, bar.y, 1f, bar.height), new Color(1f, 1f, 1f, 0.8f * alpha));

                    // 下のつまみ(三角 + 色の四角)
                    var tip = new Vector3(x, handles.y + 1f);
                    var body = new Rect(x - 6f, handles.y + 5f, 12f, 10f);
                    var previous = Handles.color;
                    Handles.color = outline;
                    Handles.DrawAAConvexPolygon(tip, new Vector3(x + 5f, body.y), new Vector3(x - 5f, body.y));
                    Handles.color = previous;
                    EditorGUI.DrawRect(body, outline);
                    EditorGUI.DrawRect(new Rect(body.x + 2f, body.y + 2f, body.width - 4f, body.height - 4f),
                        new Color(color.r, color.g, color.b, alpha));
                }
            }
        }

        /// <summary>選んだストップの色・HEX・位置・削除。</summary>
        private void DrawSelectedStop(SerializedProperty stops, string key, int selected)
        {
            var color = ColorOf(stops, selected);
            var position = Position(stops, selected);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(4f);
                // ColorField には標準のスポイトが付いている
                EditorGUI.BeginChangeCheck();
                var c = EditorGUILayout.ColorField(new GUIContent("", "選んだストップの色(スポイトで画面から拾える)"),
                    color.colorValue, true, false, false, GUILayout.Width(56f));
                if (EditorGUI.EndChangeCheck()) color.colorValue = new Color(c.r, c.g, c.b, 1f);

                var hex = "#" + ColorUtility.ToHtmlStringRGB(color.colorValue);
                EditorGUI.BeginChangeCheck();
                var newHex = EditorGUILayout.DelayedTextField(hex, GUILayout.Width(70f));
                if (EditorGUI.EndChangeCheck() && ColorMath.TryParseHex(newHex, out var rgb))
                {
                    color.colorValue = new Color(rgb.R, rgb.G, rgb.B, 1f);
                }

                var pathMode = _layout != null && _layout.PathMode;
                GUILayout.Label(new GUIContent("位置", pathMode
                    ? "0 = 色の経路の始点(A)、1 = 終点"
                    : "0 = 元の暗い部分、1 = 明るい部分"), GUILayout.Width(28f));
                using (new EditorGUI.DisabledScope(Locked))
                {
                    EditorGUI.BeginChangeCheck();
                    var p = EditorGUILayout.FloatField(PositionOf(stops, selected), GUILayout.Width(44f));
                    if (EditorGUI.EndChangeCheck() && !Locked) position.floatValue = Mathf.Clamp01(p);
                }

                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(Locked || stops.arraySize <= SpoitableColorPreset.MinStops))
                {
                    if (GUILayout.Button(new GUIContent("削除", "選んだストップを消す(Delete キー、つまみを上下に引き抜いても消せる)"),
                            EditorStyles.miniButton, GUILayout.Width(40f)))
                    {
                        stops.DeleteArrayElementAtIndex(selected);
                        _selected[key] = Mathf.Clamp(selected - 1, 0, stops.arraySize - 1);
                    }
                }
            }
        }

        /// <summary>まとめての操作と、HEX の一括入力。</summary>
        private void DrawTools(SerializedProperty stops, string key)
        {
            if (Locked)
            {
                // 位置と数は点で決まるので、まとめての操作は出さない
                if (!string.IsNullOrEmpty(_layout.Hint)) GUILayout.Label(_layout.Hint, SccGui.Hint);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(stops.arraySize >= SpoitableColorPreset.MaxStops))
                {
                    if (GUILayout.Button(new GUIContent("+ ストップ", "いちばん広い隙間の真ん中に足す(バーのクリックでも足せる)"),
                            EditorStyles.miniButtonLeft))
                    {
                        _selected[key] = AddStop(stops, WidestGapCenter(stops));
                    }
                }

                if (GUILayout.Button(new GUIContent("均等に並べる", "位置の順のまま、等間隔にする"), EditorStyles.miniButtonMid))
                {
                    Distribute(stops);
                }

                if (GUILayout.Button(new GUIContent("左右反転", "暗い側と明るい側の色を入れ替える"), EditorStyles.miniButtonRight))
                {
                    for (var i = 0; i < stops.arraySize; i++)
                    {
                        var p = Position(stops, i);
                        p.floatValue = 1f - p.floatValue;
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(4f);
                _hexDrafts.TryGetValue(key, out var draft);
                _hexDrafts[key] = EditorGUILayout.TextField(draft ?? "");
                if (GUILayout.Button(new GUIContent("HEX で一括", "例: #6B9D99 → #8ABDB1 → #A8DBC6(暗→明の順に、均等に並べる)"),
                        EditorStyles.miniButton, GUILayout.Width(78f)))
                {
                    var error = ApplyHexList(stops, _hexDrafts[key]);
                    if (error == null)
                    {
                        _hexDrafts[key] = "";
                        _hexErrors.Remove(key);
                        _selected[key] = 0;
                    }
                    else
                    {
                        _hexErrors[key] = error;
                    }

                    // ストップの数やメッセージの有無が変わるので、このフレームはここまで
                    stops.serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }

            if (_hexErrors.TryGetValue(key, out var message))
            {
                EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
        }

        // ------------------------------------------------------------------ 操作

        private int HitHandle(SerializedProperty stops, Rect bar, Rect handles, Vector2 mouse, int selected)
        {
            // 選んでいるつまみを優先し、次にいちばん近いつまみ
            if (selected >= 0 && selected < stops.arraySize && HandleRect(stops, bar, handles, selected).Contains(mouse))
            {
                return selected;
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < stops.arraySize; i++)
            {
                var r = HandleRect(stops, bar, handles, i);
                if (!r.Contains(mouse)) continue;
                var d = Mathf.Abs(r.center.x - mouse.x);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = i;
            }

            return best;
        }

        private Rect HandleRect(SerializedProperty stops, Rect bar, Rect handles, int index)
        {
            var x = bar.x + bar.width * PositionOf(stops, index);
            // つまみの帯に加えて、バーの上の線もつかめるようにする
            return new Rect(x - 7f, bar.y, 14f, bar.height + handles.height);
        }

        /// <summary>位置 t に、今のグラデーションの色でストップを足す。足したストップの番号を返す。</summary>
        private static int AddStop(SerializedProperty stops, float t)
        {
            var color = Evaluate(ReadStops(stops), t);
            var index = stops.arraySize;
            stops.arraySize++;
            Position(stops, index).floatValue = t;
            ColorOf(stops, index).colorValue = color;
            return index;
        }

        private static float WidestGapCenter(SerializedProperty stops)
        {
            var positions = ReadStops(stops).Select(s => s.position).OrderBy(p => p).ToList();
            if (positions.Count == 0) return 0.5f;
            var best = (gap: positions[0], pos: positions[0] * 0.5f);
            for (var i = 0; i < positions.Count - 1; i++)
            {
                var gap = positions[i + 1] - positions[i];
                if (gap > best.gap) best = (gap, (positions[i] + positions[i + 1]) * 0.5f);
            }

            var tail = 1f - positions[positions.Count - 1];
            if (tail > best.gap) best = (tail, (positions[positions.Count - 1] + 1f) * 0.5f);
            return best.pos;
        }

        private static void Distribute(SerializedProperty stops)
        {
            var order = Enumerable.Range(0, stops.arraySize).OrderBy(i => Position(stops, i).floatValue).ToList();
            if (order.Count < 2) return;
            for (var rank = 0; rank < order.Count; rank++)
            {
                Position(stops, order[rank]).floatValue = (float)rank / (order.Count - 1);
            }
        }

        /// <returns>失敗したときのメッセージ。成功なら null</returns>
        private static string ApplyHexList(SerializedProperty stops, string text)
        {
            var colors = new List<ColorMath.Rgb>();
            var invalid = new List<string>();
            foreach (var token in (text ?? "").Split(HexSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ColorMath.TryParseHex(token, out var rgb)) colors.Add(rgb);
                else invalid.Add(token);
            }

            if (invalid.Count > 0) return "HEX として読めない値があります: " + string.Join(" ", invalid);
            if (colors.Count < SpoitableColorPreset.MinStops || colors.Count > SpoitableColorPreset.MaxStops)
            {
                return $"HEX は {SpoitableColorPreset.MinStops}〜{SpoitableColorPreset.MaxStops} 個必要です(読み取れた数: {colors.Count})。";
            }

            stops.arraySize = colors.Count;
            for (var i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                Position(stops, i).floatValue = (float)i / (colors.Count - 1);
                ColorOf(stops, i).colorValue = new Color(c.R, c.G, c.B, 1f);
            }

            return null;
        }

        /// <summary>位置 t での色(sRGB)。表示と同じく、線形の色空間で補間する。</summary>
        public static Color Evaluate(List<(float position, Color color)> stops, float t)
        {
            if (stops.Count == 0) return Color.gray;
            var lut = ColorMath.BuildLinearLut(
                stops.Select(s => (s.position, new ColorMath.Rgb(s.color.r, s.color.g, s.color.b))).ToList(),
                GradientLut.Width);
            var c = ColorMath.LinearToSrgb(lut[Mathf.Clamp(Mathf.RoundToInt(t * (GradientLut.Width - 1)), 0,
                GradientLut.Width - 1)]);
            return new Color(c.R, c.G, c.B, 1f);
        }

        /// <summary>表示に使う位置(上書きがあればそれ)。</summary>
        private float PositionOf(SerializedProperty stops, int index)
        {
            var positions = _layout?.Positions;
            return positions != null && index < positions.Length ? positions[index] : Position(stops, index).floatValue;
        }

        /// <summary>表示に使うストップ(位置は上書きがあればそれ)。</summary>
        private List<(float position, Color color)> DisplayStops(SerializedProperty stops)
        {
            var list = ReadStops(stops);
            var positions = _layout?.Positions;
            if (positions == null || positions.Length != list.Count) return list;
            for (var i = 0; i < list.Count; i++) list[i] = (positions[i], list[i].color);
            return list;
        }

        public static List<(float position, Color color)> ReadStops(SerializedProperty stops)
        {
            var list = new List<(float, Color)>(stops.arraySize);
            for (var i = 0; i < stops.arraySize; i++) list.Add((Position(stops, i).floatValue, ColorOf(stops, i).colorValue));
            return list;
        }

        private static SerializedProperty Position(SerializedProperty stops, int index)
        {
            return stops.ArrayElement(index).FindPropertyRelative(nameof(ColorStop.position));
        }

        private static SerializedProperty ColorOf(SerializedProperty stops, int index)
        {
            return stops.ArrayElement(index).FindPropertyRelative(nameof(ColorStop.color));
        }
    }
}
