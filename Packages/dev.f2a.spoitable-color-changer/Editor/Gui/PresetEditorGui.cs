using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// プリセット(明るさのレンジとレイヤー)の編集欄。エディタのウィンドウとプリセットのインスペクタで共有する。
    /// レイヤー一覧(並べ替え・追加・複製・削除)、選んだレイヤーのプロパティ(名前・不透明度・グラデーション)、
    /// 明るさのレンジを、それぞれ別に描けるようにしてある。
    /// </summary>
    internal sealed class PresetEditorGui : IDisposable
    {
        private static readonly GUIContent RangeModeLabel = new GUIContent("明るさのレンジ",
            "元テクスチャの明るさ(OKLab の L)のどこからどこまでを、グラデーションの左端〜右端に割り当てるか");

        private static readonly GUIContent[] RangeModeOptions =
        {
            new GUIContent("自動", "元テクスチャの明るさの分布から決める"),
            new GUIContent("手動", "黒点と白点を手で決める(分布の上のつまみをドラッグ)"),
        };

        private readonly GradientPreviews _previews = new GradientPreviews();
        private readonly GradientEditorGui _gradient = new GradientEditorGui();
        private SerializedObject _so;
        private int _soUses; // _so を作ってからの Begin の回数
        private ReorderableList _list;
        private int _failedStatsTexture;
        private string _renameError;
        private string _renameErrorLayer; // 警告を出したレイヤーの名前(ほかのレイヤーでは出さない)
        private string _layerSignature; // レイヤーの名前の並び。並べ替え・追加・削除・Undo を見分ける

        /// <summary>レイヤーの右端に出す一言(マスクの有無など)。null なら出さない。</summary>
        public Func<string, string> Badge;

        /// <summary>一覧でレイヤーを選んだとき(レイヤー名)。</summary>
        public Action<string> Selected;

        /// <summary>レイヤー名を変えたとき(古い名前, 新しい名前)。マスクの割り当ての付け替えに使う。</summary>
        public Action<string, string> Renamed;

        /// <summary>名前を変えてよいか(古い名前, 新しい名前)。だめなら理由を返す。null なら変えてよい。</summary>
        public Func<string, string, string> CanRename;

        /// <summary>レイヤーのバーの表示の切り替え(色の経路の Relative など)。null を返せば今までどおり。</summary>
        public Func<string, StopLayout> Layout;

        public SerializedObject SerializedObject => _so;

        /// <summary>preset を編集する準備をする(毎フレーム、描く前に呼ぶ)。</summary>
        public void Begin(SpoitableColorPreset preset)
        {
            if (_so == null || _so.targetObject != preset)
            {
                _so?.Dispose();
                _so = preset != null ? new SerializedObject(preset) : null;
                _list = null;
                _previews.Clear();
                _renameError = null;
                _layerSignature = null;
                _soUses = 0;
            }
            else if (++_soUses > SccGui.SerializedObjectRenewInterval && SccGui.CanRenewSerializedObject)
            {
                // レイヤー一覧(ReorderableList)が中で呼ぶ GetArrayElementAtIndex の分だけ遅くなるので、ときどき作り直す。
                // 古いものは Dispose しない(開いているメニューのコールバックが握っていることがある)
                _so = new SerializedObject(preset);
                _list = null;
                _soUses = 0;
            }

            if (_so == null) return;
            _so.Update();

            // 番号で持っている表示用の状態(選んだストップ・HEX の下書き)は、レイヤーの並びが変わったら捨てる
            var signature = Signature();
            if (signature != _layerSignature)
            {
                if (_layerSignature != null)
                {
                    _gradient.Clear();
                    _previews.Clear();
                }

                _layerSignature = signature;
            }
        }

        private string Signature()
        {
            var layers = Layers;
            var names = new string[layers.arraySize];
            for (var i = 0; i < names.Length; i++) names[i] = NameAt(layers, i);
            return string.Join("\n", names);
        }

        /// <summary>描いた変更を確定する(毎フレーム、描いたあとに呼ぶ)。</summary>
        public void End()
        {
            _so?.ApplyModifiedProperties();
        }

        private SerializedProperty Layers => _so.FindProperty(nameof(SpoitableColorPreset.layers));

        public int IndexOf(string layerName)
        {
            var layers = Layers;
            for (var i = 0; i < layers.arraySize; i++)
            {
                if (NameAt(layers, i) == layerName) return i;
            }

            return -1;
        }

        private static string NameAt(SerializedProperty layers, int index)
        {
            return layers.ArrayElement(index).FindPropertyRelative(nameof(ColorLayer.name)).stringValue;
        }

        // ------------------------------------------------------------------ レイヤー一覧

        public void DrawLayerList(string selectedName)
        {
            if (_so == null) return;
            if (_list == null) CreateList();
            _list.serializedProperty = Layers;
            _list.index = IndexOf(selectedName);
            _list.DoLayoutList();
        }

        private void CreateList()
        {
            _list = new ReorderableList(_so, Layers, true, false, true, true)
            {
                elementHeight = 26f,
                footerHeight = 22f,
                drawElementCallback = DrawElement,
                drawElementBackgroundCallback = DrawElementBackground,
                onSelectCallback = list =>
                {
                    if (list.index >= 0 && list.index < list.serializedProperty.arraySize)
                    {
                        Selected?.Invoke(NameAt(list.serializedProperty, list.index));
                    }
                },
                drawFooterCallback = DrawFooter,
            };
        }

        private void DrawElementBackground(Rect rect, int index, bool active, bool focused)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (active) EditorGUI.DrawRect(rect, SccGui.SelectedRowColor);
            else if (rect.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(rect, SccGui.HoverRowColor);
        }

        private void DrawElement(Rect rect, int index, bool active, bool focused)
        {
            var layers = Layers;
            if (index >= layers.arraySize) return;
            var layer = layers.ArrayElement(index);
            var name = layer.FindPropertyRelative(nameof(ColorLayer.name)).stringValue;
            var visible = layer.FindPropertyRelative(nameof(ColorLayer.visible));
            var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));

            var eyeRect = new Rect(rect.x, rect.y + 5f, 16f, 16f);
            var eye = EditorGUIUtility.IconContent(visible.boolValue ? "scenevis_visible" : "scenevis_hidden");
            if (GUI.Button(eyeRect,
                    new GUIContent(eye.image, visible.boolValue ? "表示中(クリックで隠す)" : "非表示(クリックで表示)"),
                    GUIStyle.none))
            {
                visible.boolValue = !visible.boolValue;
            }

            var thumbRect = new Rect(eyeRect.xMax + 6f, rect.y + 7f, 36f, 12f);
            if (Event.current.type == EventType.Repaint && stops.arraySize > 0)
            {
                var texture = _previews.Get(layer.propertyPath, GradientEditorGui.ReadStops(stops));
                EditorGUI.DrawRect(new Rect(thumbRect.x - 1f, thumbRect.y - 1f, thumbRect.width + 2f, thumbRect.height + 2f),
                    SccGui.SeparatorColor);
                if (texture != null) GUI.DrawTexture(thumbRect, texture, ScaleMode.StretchToFill);
            }

            var badge = Badge?.Invoke(name);
            var badgeWidth = string.IsNullOrEmpty(badge) ? 0f : SccGui.Badge.CalcSize(new GUIContent(badge)).x + 4f;
            var nameRect = new Rect(thumbRect.xMax + 8f, rect.y, rect.xMax - thumbRect.xMax - 10f - badgeWidth, rect.height);
            var color = GUI.color;
            if (!visible.boolValue) GUI.color = new Color(color.r, color.g, color.b, 0.5f);
            GUI.Label(nameRect, name, active ? SelectedRowLabel : SccGui.RowLabel);
            GUI.color = color;
            if (badgeWidth > 0f) GUI.Label(new Rect(rect.xMax - badgeWidth, rect.y, badgeWidth, rect.height), badge, SccGui.Badge);
        }

        private static GUIStyle s_selectedRowLabel;

        private static GUIStyle SelectedRowLabel => s_selectedRowLabel ??= new GUIStyle(SccGui.RowLabel)
        {
            normal = { textColor = Color.white },
            fontStyle = FontStyle.Bold,
        };

        private void DrawFooter(Rect rect)
        {
            var width = 26f;
            var x = rect.xMax - 8f - width * 3f;
            var style = ReorderableList.defaultBehaviours.preButton;
            var bg = new Rect(x - 4f, rect.y, width * 3f + 8f, rect.height);
            if (Event.current.type == EventType.Repaint) ReorderableList.defaultBehaviours.footerBackground.Draw(bg, false, false, false, false);

            if (GUI.Button(new Rect(x, rect.y, width, 18f),
                    new GUIContent(EditorGUIUtility.IconContent("Toolbar Plus").image, "レイヤーを追加"), style))
            {
                Selected?.Invoke(AddLayer(-1));
            }

            var index = _list.index;
            var layers = Layers;
            using (new EditorGUI.DisabledScope(index < 0 || index >= layers.arraySize))
            {
                if (GUI.Button(new Rect(x + width, rect.y, width, 18f),
                        new GUIContent(EditorGUIUtility.IconContent("TreeEditor.Duplicate").image,
                            "選んだレイヤーを複製(色だけ。マスクは複製しない)"), style))
                {
                    Selected?.Invoke(AddLayer(index));
                }

                if (GUI.Button(new Rect(x + width * 2f, rect.y, width, 18f),
                        new GUIContent(EditorGUIUtility.IconContent("Toolbar Minus").image,
                            "選んだレイヤーを削除(Ctrl+Z で戻せる)"), style))
                {
                    layers.DeleteArrayElementAtIndex(index);
                    var next = Mathf.Clamp(index - 1, 0, layers.arraySize - 1);
                    if (layers.arraySize > 0) Selected?.Invoke(NameAt(layers, next));
                    _previews.Clear();
                }
            }
        }

        /// <summary>レイヤーを足す。source が 0 以上なら、そのレイヤーの複製を直後に入れる。足したレイヤーの名前を返す。</summary>
        private string AddLayer(int source)
        {
            var layers = Layers;
            var names = new HashSet<string>();
            for (var i = 0; i < layers.arraySize; i++) names.Add(NameAt(layers, i));

            string name;
            int index;
            if (source >= 0)
            {
                layers.InsertArrayElementAtIndex(source); // source と同じ中身の要素が source + 1 にできる
                index = source + 1;
                name = UniqueName(names, NameAt(layers, source) + " コピー");
            }
            else
            {
                index = layers.arraySize;
                layers.arraySize++;
                name = UniqueName(names, "レイヤー");
                var layer = layers.ArrayElement(index);
                layer.FindPropertyRelative(nameof(ColorLayer.visible)).boolValue = true;
                layer.FindPropertyRelative(nameof(ColorLayer.opacity)).floatValue = 1f;
                var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));
                stops.arraySize = 2;
                SetStop(stops.ArrayElement(0), 0f, Color.black);
                SetStop(stops.ArrayElement(1), 1f, Color.white);
            }

            layers.ArrayElement(index).FindPropertyRelative(nameof(ColorLayer.name)).stringValue = name;
            _previews.Clear();
            return name;
        }

        private static string UniqueName(HashSet<string> names, string baseName)
        {
            if (!names.Contains(baseName) && baseName != "レイヤー") return baseName;
            for (var i = names.Count + 1;; i++)
            {
                var candidate = $"{baseName}{i}";
                if (!names.Contains(candidate)) return candidate;
            }
        }

        private static void SetStop(SerializedProperty stop, float position, Color color)
        {
            stop.FindPropertyRelative(nameof(ColorStop.position)).floatValue = position;
            stop.FindPropertyRelative(nameof(ColorStop.color)).colorValue = color;
        }

        // ------------------------------------------------------------------ 選んだレイヤーのプロパティ

        /// <param name="layerName">編集するレイヤー</param>
        /// <param name="statsSource">明るさの分布を出すテクスチャ(なければ null)</param>
        public void DrawLayerProperties(string layerName, Texture statsSource)
        {
            if (_so == null) return;
            var index = IndexOf(layerName);
            if (index < 0) return;
            var layer = Layers.ArrayElement(index);
            var nameProp = layer.FindPropertyRelative(nameof(ColorLayer.name));
            var opacity = layer.FindPropertyRelative(nameof(ColorLayer.opacity));
            var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));

            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 64f;
            EditorGUI.BeginChangeCheck();
            var newName = EditorGUILayout.DelayedTextField(new GUIContent("名前", "マスクはこの名前で割り当てる"), nameProp.stringValue);
            if (EditorGUI.EndChangeCheck()) Rename(nameProp, newName);
            if (_renameError != null && _renameErrorLayer == nameProp.stringValue)
            {
                EditorGUILayout.HelpBox(_renameError, MessageType.Warning);
            }

            EditorGUILayout.Slider(opacity, 0f, 1f, new GUIContent("不透明度", "下のレイヤー(または元の色)に重ねる濃さ"));
            EditorGUIUtility.labelWidth = labelWidth;

            GUILayout.Space(4f);
            var stats = Stats(statsSource);
            Range(stats, out var lo, out var hi);
            _gradient.Draw(stops, _previews, stats, lo, hi, Layout?.Invoke(layerName));
        }

        private void Rename(SerializedProperty nameProp, string newName)
        {
            newName = (newName ?? "").Trim();
            var oldName = nameProp.stringValue;
            _renameErrorLayer = oldName;
            _renameError = null;
            if (newName == oldName) return; // 前後の空白を足しただけなど

            if (newName.Length == 0)
            {
                _renameError = "名前を空にはできません。";
                return;
            }

            if (IndexOf(newName) >= 0)
            {
                _renameError = $"「{newName}」というレイヤーはもうあります。マスクは名前で割り当てるので、別の名前にしてください。";
                return;
            }

            var blocked = CanRename?.Invoke(oldName, newName);
            if (blocked != null)
            {
                _renameError = blocked;
                return;
            }

            nameProp.stringValue = newName;
            // マスクの割り当てとプリセットの名前を、同じ 1 回の Undo で戻せるようにまとめて確定する
            _so.ApplyModifiedProperties();
            Renamed?.Invoke(oldName, newName);
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
        }

        // ------------------------------------------------------------------ 明るさのレンジ

        public LuminanceStats Stats(Texture texture)
        {
            if (texture == null) return null;
            // 失敗したテクスチャは、毎イベント再計算してログを流さないよう覚えておく
            if (_failedStatsTexture == texture.GetInstanceID()) return null;
            try
            {
                return LuminanceStats.Get(texture);
            }
            catch (Exception e)
            {
                _failedStatsTexture = texture.GetInstanceID();
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>今の設定での黒点と白点(自動なら分布から)。</summary>
        private void Range(LuminanceStats stats, out float lo, out float hi)
        {
            var mode = _so.FindProperty(nameof(SpoitableColorPreset.rangeMode));
            if (mode.enumValueIndex == (int)LuminanceRangeMode.Manual)
            {
                lo = _so.FindProperty(nameof(SpoitableColorPreset.blackPoint)).floatValue;
                hi = _so.FindProperty(nameof(SpoitableColorPreset.whitePoint)).floatValue;
            }
            else
            {
                lo = stats?.Low ?? 0f;
                hi = stats?.High ?? 1f;
            }
        }

        private static readonly int RangeHash = "SpoitableRangeEditor".GetHashCode();
        private int _rangeDrag = -1; // 0 = 黒点、1 = 白点

        /// <summary>明るさのレンジ。分布の上の 2 本のつまみで黒点と白点を決める(手動のとき)。</summary>
        public void DrawRange(Texture statsSource)
        {
            if (_so == null) return;
            var mode = _so.FindProperty(nameof(SpoitableColorPreset.rangeMode));
            var black = _so.FindProperty(nameof(SpoitableColorPreset.blackPoint));
            var white = _so.FindProperty(nameof(SpoitableColorPreset.whitePoint));
            var stats = Stats(statsSource);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(RangeModeLabel, GUILayout.Width(96f));
                EditorGUI.BeginChangeCheck();
                var next = GUILayout.Toolbar(mode.enumValueIndex, RangeModeOptions, EditorStyles.miniButton);
                if (EditorGUI.EndChangeCheck()) mode.enumValueIndex = next;
            }

            var manual = mode.enumValueIndex == (int)LuminanceRangeMode.Manual;
            Range(stats, out var lo, out var hi);

            var rect = GUILayoutUtility.GetRect(10f, 44f, GUILayout.ExpandWidth(true));
            var graph = new Rect(rect.x + 6f, rect.y + 2f, rect.width - 12f, rect.height - 4f);
            var id = GUIUtility.GetControlID(RangeHash, FocusType.Passive, rect);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!manual || e.button != 0 || !rect.Contains(e.mousePosition)) break;
                    var x0 = graph.x + graph.width * lo;
                    var x1 = graph.x + graph.width * hi;
                    _rangeDrag = Mathf.Abs(e.mousePosition.x - x0) <= Mathf.Abs(e.mousePosition.x - x1) ? 0 : 1;
                    GUIUtility.hotControl = id;
                    goto case EventType.MouseDrag;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id || _rangeDrag < 0) break;
                    var t = Mathf.Clamp01((e.mousePosition.x - graph.x) / graph.width);
                    if (_rangeDrag == 0) black.floatValue = Mathf.Min(t, white.floatValue - 0.01f);
                    else white.floatValue = Mathf.Max(t, black.floatValue + 0.01f);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    _rangeDrag = -1;
                    e.Use();
                    break;
                case EventType.Repaint:
                    DrawRangeGraph(graph, stats, lo, hi, manual);
                    if (manual)
                    {
                        EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
                    }

                    break;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6f);
                if (manual)
                {
                    var labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 30f;
                    // 確定したときだけ反映する(打っている途中の値で、もう片方を動かさない)。間は 0.01 空ける
                    EditorGUI.BeginChangeCheck();
                    var b = EditorGUILayout.DelayedFloatField("黒点", black.floatValue, GUILayout.Width(80f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        black.floatValue = Mathf.Clamp(b, 0f, Mathf.Max(0f, white.floatValue - 0.01f));
                    }

                    EditorGUI.BeginChangeCheck();
                    var w = EditorGUILayout.DelayedFloatField("白点", white.floatValue, GUILayout.Width(80f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        white.floatValue = Mathf.Clamp(w, Mathf.Min(1f, black.floatValue + 0.01f), 1f);
                    }

                    EditorGUIUtility.labelWidth = labelWidth;
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(stats == null))
                    {
                        if (GUILayout.Button(new GUIContent("自動の値に", "分布から求めた黒点と白点を写す"), EditorStyles.miniButton))
                        {
                            black.floatValue = stats.Low;
                            white.floatValue = stats.High;
                        }
                    }
                }
                else
                {
                    GUILayout.Label(stats != null
                            ? $"黒点 {lo:F3} / 白点 {hi:F3}(透明部分を除いた分布から)"
                            : "対象テクスチャが決まると明るさの分布を表示します",
                        SccGui.Hint);
                }
            }
        }

        private static void DrawRangeGraph(Rect rect, LuminanceStats stats, float lo, float hi, bool manual)
        {
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, SccGui.Pro ? 0.25f : 0.1f));
            if (stats != null)
            {
                var bins = stats.Histogram;
                var bw = rect.width / bins.Length;
                var color = SccGui.Pro ? new Color(0.75f, 0.75f, 0.75f, 0.9f) : new Color(0.3f, 0.3f, 0.3f, 0.8f);
                for (var i = 0; i < bins.Length; i++)
                {
                    var h = rect.height * bins[i];
                    EditorGUI.DrawRect(new Rect(rect.x + bw * i, rect.yMax - h, bw + 0.5f, h), color);
                }
            }

            var x0 = rect.x + rect.width * Mathf.Clamp01(lo);
            var x1 = rect.x + rect.width * Mathf.Clamp01(hi);
            var shade = new Color(0f, 0f, 0f, 0.45f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, x0 - rect.x, rect.height), shade);
            EditorGUI.DrawRect(new Rect(x1, rect.y, rect.xMax - x1, rect.height), shade);
            var marker = SccGui.AccentColor;
            var width = manual ? 3f : 2f;
            EditorGUI.DrawRect(new Rect(x0 - width * 0.5f, rect.y, width, rect.height), marker);
            EditorGUI.DrawRect(new Rect(x1 - width * 0.5f, rect.y, width, rect.height), marker);
        }

        public void Dispose()
        {
            _previews.Dispose();
            _so?.Dispose();
            _so = null;
            _list = null;
        }
    }
}
