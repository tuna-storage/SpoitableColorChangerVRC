using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// Spoitable Color Changer のエディタ(Photoshop 風の配置)。対象テクスチャを表示し、レイヤーの色とマスクを編集する。
    /// 上: 対象と表示 / その下: 選んだ道具の設定 / 左: 道具 / 右: レイヤー・色・マスク・選択範囲 / 下: 状態。
    /// 編集はすぐに NDMF のプレビューへ反映される(塗りマスクは保存前でも)。
    /// </summary>
    internal sealed partial class MaskEditorWindow : EditorWindow
    {
        private enum Tool
        {
            Pen = 0,
            Brush = 1,
            Eraser = 2,
            Bucket = 3,
            Rect = 4,
            Lasso = 5,
            ColorSelect = 6,
            // シリアライズ済みの値をずらさないよう、新しい道具は末尾に足す
            SelectBrush = 7,
            UvIsland = 8,
            Gradient = 9,
            ColorPath = 10,
        }

        private enum ViewMode
        {
            Recolored = 0,
            Source = 1,
        }

        private enum UvDisplay
        {
            IslandTool = 0,
            Always = 1,
            Off = 2,
        }

        /// <summary>右パネルのタブ(選んでいるレイヤーの設定)。</summary>
        private enum PanelTab
        {
            Color = 0,
            Path = 1,
            Mask = 2,
            Other = 3,
        }

        private const int OpNew = 0;
        private const int OpAdd = 1;
        private const int OpSubtract = 2;

        private const float ToolStripWidth = 38f;
        private const float RightPanelWidth = 280f;

        [SerializeField] private SpoitableColorChanger target;
        [SerializeField] private bool followSelection = true;
        [SerializeField] private string layerName;
        [SerializeField] private int textureIndex;
        [SerializeField] private Tool tool = Tool.Pen;
        [SerializeField] private ViewMode viewMode = ViewMode.Recolored;
        [SerializeField] private float brushRadius = 24f;
        [SerializeField] private float brushHardness = 0.5f;
        [SerializeField] private float brushStrength = 1f;
        [SerializeField] private int selectionOp = OpNew;
        [SerializeField] private float tolerance = 0.06f;
        [SerializeField] private bool contiguous = true;
        [SerializeField] private float overlayOpacity = 0.5f;
        [SerializeField] private Color overlayColor = new Color(1f, 0.2f, 0.55f, 1f);
        [SerializeField] private bool selectBrushSubtract;
        [SerializeField] private int islandGrow = 2;
        [SerializeField] private UvDisplay uvDisplay = UvDisplay.IslandTool;
        [SerializeField] private bool uvAllEdges;
        [SerializeField] private bool showDisplaySection;
        [SerializeField] private bool showPresetSection = true;
        [SerializeField] private float zoom = -1f; // 0 以下なら次の描画で全体表示にする
        [SerializeField] private Vector2 pan;
        [SerializeField] private PanelTab panelTab = PanelTab.Color;

        // 右パネルのタブと道具の欄を、どの道具に合わせて組んだか(Layout のときに決める。道具を変えたらタブも合わせる)
        [SerializeField] private Tool panelTool = (Tool)(-1);
        private bool _panelGradientPoints; // Layout のときのグラデーションマスクが点の種類か(道具の欄の行の数が変わる)

        private Vector2 _panelScroll;
        private int _layerIndex; // 選んでいるレイヤーの位置(名前が見つからなくなったときの代わりに使う)
        private string _pendingLayerName; // レイヤー一覧で選んだが、まだ切り替えていないレイヤー
        private PresetEditorGui _presetGui;
        private SerializedObject _componentObject;
        private int _componentObjectUses; // _componentObject を作ってからの OnGUI の回数

        // ステータスバー: マウスの下の値と、操作の結果の一言(しばらく出してから消す)
        private string _hover = "";
        private string _message = "";
        private double _messageTime;

        [MenuItem("Tools/faketuna/Spoitable Color Changer")]
        private static void OpenFromMenu()
        {
            var c = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<SpoitableColorChanger>()
                : null;
            Open(c, null);
        }

        public static void Open(SpoitableColorChanger component, string layer)
        {
            var window = GetWindow<MaskEditorWindow>();
            window.titleContent = new GUIContent("Spoitable Color Changer");
            window.minSize = new Vector2(820f, 480f);
            if (component != null && component != window.target) window.SetTarget(component);
            if (!string.IsNullOrEmpty(layer))
            {
                window.layerName = layer;
                window._pendingLayerName = null;
            }
            window.Show();
            window.Focus();
        }

        private void SetTarget(SpoitableColorChanger component)
        {
            if (component == target) return;
            var previous = target;
            target = component;
            zoom = -1f;
            ForgetGradientPoint();
            ForgetPathPoint();
            // 選択範囲と塗りマスクの取り消し履歴は対象ごと。別のアバターに持ち越すと、取り消したときに
            // 前の対象の選択範囲が戻ってしまうので、両方とも捨てる(塗りマスクの編集内容そのものは残る)
            if (previous != null) MaskHistory.Reset();
            else MaskSelection.Clear();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            titleContent = new GUIContent("Spoitable Color Changer");
            SetupPresetGui();
            Undo.undoRedoPerformed += OnUndoRedo;
            SceneView.duringSceneGui += OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUIColorPath;
            MaskHistory.Restored += OnMaskRestored;
            MaskHistory.EditAborted += OnEditAborted;
            ShortcutManager.instance.shortcutBindingChanged += OnShortcutBindingChanged;
            s_keys = null;
            EnableScene();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui -= OnSceneGUIColorPath;
            SceneView.RepaintAll();
            MaskHistory.Restored -= OnMaskRestored;
            MaskHistory.EditAborted -= OnEditAborted;
            ShortcutManager.instance.shortcutBindingChanged -= OnShortcutBindingChanged;
            DisableScene(); // シーンビューで塗りかけのストロークは、保存の前に履歴へ積む
            PaintSession.SaveAll();
            ReleaseTextures();
            ReleaseGradientView();
            _presetGui?.Dispose();
            _presetGui = null;
            _componentObject?.Dispose();
            _componentObject = null;
        }

        private void OnDestroy()
        {
            // ウィンドウを閉じたら保存して、作業用データと履歴を手放す(プレビューは保存した PNG に戻る)
            PaintSession.SaveAndCloseAll();
            SourcePixels.ClearCache();
            UvIslands.ClearCache();
            _uv = null;
            _uvSources = null;
            _uvLines = null;
            _uvInnerLines = null;
            _uvLinesSource = null;
            _strokeBase = null;
            _strokeAlpha = null;
        }

        private void OnUndoRedo()
        {
            // 点の追加・削除が戻ると、選んでいた番号が別の点を指すことがあるので選び直してもらう
            ForgetGradientPoint();
            ForgetPathPoint();
            Repaint();
        }

        private void OnSelectionChange()
        {
            if (!followSelection || Selection.activeGameObject == null) return;
            var c = Selection.activeGameObject.GetComponent<SpoitableColorChanger>();
            if (c == null || c == target) return;
            SetTarget(c);
            Repaint();
        }

        /// <summary>別のレイヤーの編集を取り消したら、何が戻ったか見えるようにそのレイヤーを表示する。</summary>
        private void OnMaskRestored(PaintSession session)
        {
            if (session != null && target != null && target.masks != null)
            {
                foreach (var m in target.masks)
                {
                    if (m == null || m.paintMask != session.Asset) continue;
                    if (m.layerName != layerName) layerName = m.layerName;
                    break;
                }
            }

            Repaint();
        }

        private void OnEditAborted()
        {
            _toolDragging = false;
            _strokeSession = null;
            _lasso.Clear();
            GUIUtility.hotControl = 0;
            Repaint();
        }

        private void SetMessage(string message)
        {
            _message = message;
            _messageTime = EditorApplication.timeSinceStartup;
        }

        // ------------------------------------------------------------------ 全体

        private void OnGUI()
        {
            var e = Event.current;
            // レイヤー一覧で選んだレイヤーは、次の Layout から使う(Selected の説明を参照)
            if (e.type == EventType.Layout && _pendingLayerName != null)
            {
                layerName = _pendingLayerName;
                _pendingLayerName = null;
            }

            // ブラシの輪の色を Ctrl の状態に合わせる。ほかのキーは Shortcut Manager(下)で受ける
            if ((e.type == EventType.KeyDown || e.type == EventType.KeyUp) &&
                (e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl ||
                 e.keyCode == KeyCode.LeftCommand || e.keyCode == KeyCode.RightCommand))
            {
                Repaint();
            }

            var textures = target != null ? TargetResolver.TargetTextures(target) : null;
            // コンポーネントの SerializedObject はこのイベントの最初に 1 回だけ読み直す(途中で読み直すと書きかけの値が消える)
            if (target != null) ComponentObjectForEvent().Update();
            DrawToolbar(textures);

            if (target == null)
            {
                DrawNoTarget();
                return;
            }

            var preset = target.preset;
            if (preset == null)
            {
                DrawNoPreset();
                return;
            }

            if (preset.layers.Count == 0)
            {
                DrawEmpty("レイヤーがありません", "プリセットにレイヤーを足すと、色とマスクを編集できます。", "レイヤーを追加",
                    () =>
                    {
                        using (var so = new SerializedObject(preset))
                        {
                            var layers = so.FindProperty(nameof(SpoitableColorPreset.layers));
                            layers.arraySize++;
                            var layer = layers.ArrayElement(layers.arraySize - 1);
                            var d = SpoitableColorPreset.CreateDefaultLayer("ベース");
                            layer.FindPropertyRelative(nameof(ColorLayer.name)).stringValue = d.name;
                            layer.FindPropertyRelative(nameof(ColorLayer.visible)).boolValue = true;
                            layer.FindPropertyRelative(nameof(ColorLayer.opacity)).floatValue = 1f;
                            var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));
                            stops.arraySize = d.stops.Count;
                            for (var i = 0; i < d.stops.Count; i++)
                            {
                                var stop = stops.ArrayElement(i);
                                stop.FindPropertyRelative(nameof(ColorStop.position)).floatValue = d.stops[i].position;
                                stop.FindPropertyRelative(nameof(ColorStop.color)).colorValue = d.stops[i].color;
                            }

                            so.ApplyModifiedProperties();
                        }

                        GUIUtility.ExitGUI();
                    });
                return;
            }

            if (textures.Count == 0)
            {
                DrawEmpty("対象のテクスチャが見つかりません",
                    "インスペクタで、対象(Renderer かマテリアル)とテクスチャプロパティを確認してください。", "インスペクタで開く",
                    () => Selection.activeObject = target.gameObject);
                return;
            }

            textureIndex = Mathf.Clamp(textureIndex, 0, textures.Count - 1);
            var source = textures[textureIndex].texture;
            EnsureLayer(preset);
            var binding = target.FindMask(layerName);
            var session = binding != null && binding.paintMask != null ? PaintSession.Open(binding.paintMask) : null;

            // ドラッグ中に Undo などで塗っていたマスクの割り当てが外れたら、その操作を打ち切る
            // (このままだと MouseUp が来ても終われず、ドラッグ中のまま残る)
            if (_toolDragging && tool != Tool.Gradient && tool != Tool.ColorPath &&
                (session == null || (_strokeSession != null && _strokeSession != session)))
            {
                MaskHistory.Cancel();
                OnEditAborted();
            }

            // UV は道具か表示で使うときだけ求める(結果は組み合わせが変わるまで使い回される)
            _uv = null;
            _uvError = null;
            if (session != null && (tool == Tool.UvIsland || uvDisplay == UvDisplay.Always))
            {
                _uv = UvIslands.Get(CachedUvSources(source), out _uvError);
            }

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                DrawToolStrip();
                var canvas = GUILayoutUtility.GetRect(100f, 100f, GUILayout.ExpandWidth(true),
                    GUILayout.ExpandHeight(true));
                DrawCanvas(canvas, source, session);
                DrawRightPanel(preset, binding, session, source);
            }

            DrawStatusBar(session);
            UpdateSceneFromWindow(session, source);
        }

        /// <summary>
        /// 選んでいるレイヤーがプリセットになければ(名前の変更の取り消しや削除)、同じ位置(なければ近い位置)の
        /// レイヤーにする。
        /// </summary>
        private void EnsureLayer(SpoitableColorPreset preset)
        {
            for (var i = 0; i < preset.layers.Count; i++)
            {
                if (preset.layers[i] == null || preset.layers[i].name != layerName) continue;
                _layerIndex = i;
                return;
            }

            var layer = preset.layers[Mathf.Clamp(_layerIndex, 0, preset.layers.Count - 1)] ??
                        preset.layers.FirstOrDefault(l => l != null);
            layerName = layer != null ? layer.name : "";
        }

        // ------------------------------------------------------------------ 上のツールバー

        private void DrawToolbar(List<(Material material, string property, Texture texture)> textures)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                var picked = (SpoitableColorChanger)EditorGUILayout.ObjectField(target, typeof(SpoitableColorChanger),
                    true, GUILayout.Width(190f));
                if (EditorGUI.EndChangeCheck())
                {
                    SetTarget(picked);
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button(new GUIContent("", "シーン内の Spoitable Color Changer から選ぶ"),
                        EditorStyles.toolbarDropDown, GUILayout.Width(18f)))
                {
                    ShowSwitchMenu();
                }

                followSelection = GUILayout.Toggle(followSelection,
                    new GUIContent("選択に追従", "ヒエラルキーで選んだ Spoitable Color Changer に切り替える"),
                    EditorStyles.toolbarButton, GUILayout.Width(72f));

                if (textures != null && textures.Count > 0)
                {
                    SccGui.VerticalSeparator();
                    GUILayout.Label("テクスチャ", SccGui.ToolbarLabel, GUILayout.ExpandWidth(false));
                    var index = Mathf.Clamp(textureIndex, 0, textures.Count - 1);
                    if (textures.Count > 1)
                    {
                        var labels = textures.Select(t => $"{t.texture.name}({t.material.name}.{t.property})").ToArray();
                        EditorGUI.BeginChangeCheck();
                        index = EditorGUILayout.Popup(index, labels, EditorStyles.toolbarPopup, GUILayout.Width(200f));
                        if (EditorGUI.EndChangeCheck())
                        {
                            textureIndex = index;
                            zoom = -1f;
                            ForgetGradientPoint();
                            ForgetPathPoint();
                            GUIUtility.ExitGUI();
                        }
                    }
                    else
                    {
                        var t = textures[0];
                        GUILayout.Label(new GUIContent(t.texture.name, $"{t.material.name}.{t.property}"),
                            SccGui.ToolbarBoldLabel, GUILayout.ExpandWidth(false));
                    }
                }

                GUILayout.FlexibleSpace();
                DrawSceneToolbar();

                if (IconButton(SccIcon.Undo, "元に戻す (Ctrl+Z)\n色の変更なども含めて、操作した順に戻す")) Undo.PerformUndo();
                if (IconButton(SccIcon.Redo, "やり直す (Ctrl+Y)")) Undo.PerformRedo();
                SccGui.VerticalSeparator();

                DrawPreviewDropdown();

                var helpRect = GUILayoutUtility.GetRect(new GUIContent("?"), EditorStyles.toolbarButton,
                    GUILayout.Width(24f));
                if (GUI.Button(helpRect, new GUIContent("?", "操作とショートカットの一覧"), EditorStyles.toolbarButton))
                {
                    PopupWindow.Show(helpRect, new HelpPopup());
                }
            }
        }

        private static readonly string[] ViewModeLabels = { "適用後", "元の色" };

        private static readonly string[] ViewModeTooltips =
        {
            "色を適用したあとの色で表示する",
            "元のテクスチャの色で表示する",
        };

        /// <summary>キャンバスに何を表示するか(適用後・元の色)を選ぶドロップダウン。</summary>
        private void DrawPreviewDropdown()
        {
            var index = Mathf.Clamp((int)viewMode, 0, ViewModeLabels.Length - 1);
            var content = new GUIContent($"プレビュー: {ViewModeLabels[index]}",
                "キャンバスの表示\n・適用後: " + ViewModeTooltips[0] + "\n・元の色: " + ViewModeTooltips[1]);
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown, GUILayout.Width(118f));
            if (!EditorGUI.DropdownButton(rect, content, FocusType.Passive, EditorStyles.toolbarDropDown)) return;
            var menu = new GenericMenu();
            for (var i = 0; i < ViewModeLabels.Length; i++)
            {
                var mode = (ViewMode)i;
                menu.AddItem(new GUIContent(ViewModeLabels[i]), viewMode == mode, () =>
                {
                    if (this == null) return; // メニューを開いたままウィンドウを閉じた
                    viewMode = mode;
                    Repaint();
                });
            }

            menu.DropDown(rect);
        }

        private static bool IconButton(SccIcon icon, string tooltip)
        {
            var rect = GUILayoutUtility.GetRect(26f, 18f, EditorStyles.toolbarButton, GUILayout.Width(26f));
            var clicked = GUI.Button(rect, new GUIContent("", tooltip), EditorStyles.toolbarButton);
            SccIcons.Draw(icon, new Rect(rect.center.x - 7f, rect.center.y - 7f, 14f, 14f), SccGui.IconColor);
            return clicked;
        }

        private void ShowSwitchMenu()
        {
            var menu = new GenericMenu();
            var all = FindAllComponents();
            foreach (var c in all)
            {
                var captured = c;
                menu.AddItem(new GUIContent(PathOf(c).Replace("/", " ∕ ")), c == target, () =>
                {
                    SetTarget(captured);
                    Repaint();
                });
            }

            if (all.Length == 0) menu.AddDisabledItem(new GUIContent("シーンに Spoitable Color Changer がありません"));
            menu.ShowAsContext();
        }

        private static SpoitableColorChanger[] FindAllComponents()
        {
            return FindObjectsByType<SpoitableColorChanger>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(c => (c.hideFlags & HideFlags.HideInHierarchy) == 0 && !EditorUtility.IsPersistent(c))
                .OrderBy(PathOf)
                .ToArray();
        }

        private static string PathOf(Component c)
        {
            var t = c.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return path;
        }

        // ------------------------------------------------------------------ 何も編集できないとき

        private void DrawNoTarget()
        {
            var all = FindAllComponents();
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(420f)))
                {
                    GUILayout.Label("編集する Spoitable Color Changer を選んでください", SccGui.EmptyTitle);
                    GUILayout.Space(4f);
                    GUILayout.Label("ヒエラルキーで選ぶか、上の欄に入れてください。", SccGui.EmptyBody);
                    if (all.Length > 0)
                    {
                        GUILayout.Space(10f);
                        foreach (var c in all)
                        {
                            if (GUILayout.Button(PathOf(c), GUILayout.Height(22f)))
                            {
                                SetTarget(c);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }

                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
        }

        private void DrawNoPreset()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(420f)))
                {
                    GUILayout.Label("プリセットを指定してください", SccGui.EmptyTitle);
                    GUILayout.Space(4f);
                    GUILayout.Label("プリセットは色の設定(レイヤーとグラデーション)です。新しく作るか、既存のものを指定してください。",
                        SccGui.EmptyBody);
                    GUILayout.Space(10f);
                    var component = ComponentObject();
                    var labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 72f;
                    var changed = MaskBindingGui.DrawPresetField(component);
                    EditorGUIUtility.labelWidth = labelWidth;
                    component.ApplyModifiedProperties();
                    if (changed) GUIUtility.ExitGUI();
                }

                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
        }

        private static void DrawEmpty(string title, string body, string buttonLabel, Action onClick)
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(420f)))
                {
                    GUILayout.Label(title, SccGui.EmptyTitle);
                    GUILayout.Space(4f);
                    GUILayout.Label(body, SccGui.EmptyBody);
                    if (buttonLabel != null)
                    {
                        GUILayout.Space(10f);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(buttonLabel, GUILayout.Width(160f), GUILayout.Height(24f))) onClick();
                            GUILayout.FlexibleSpace();
                        }
                    }
                }

                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
        }

        // ------------------------------------------------------------------ 下のステータスバー

        private void DrawStatusBar(PaintSession session)
        {
            using (new EditorGUILayout.HorizontalScope(SccGui.StatusBar))
            {
                var recent = !string.IsNullOrEmpty(_message) &&
                             EditorApplication.timeSinceStartup - _messageTime < 5.0;
                GUILayout.Label(recent ? _message : _hover, EditorStyles.miniLabel, GUILayout.MinWidth(0f));
                GUILayout.FlexibleSpace();

                GUILayout.Label(zoom > 0f ? $"{Mathf.RoundToInt(zoom * 100f)}%" : "", ZoomLabel, GUILayout.Width(44f));
                var fit = EditorGUIUtility.IconContent("Refresh");
                if (GUILayout.Button(new GUIContent(fit.image, "全体を表示(画像全体が収まる拡大率に戻す)"), EditorStyles.toolbarButton,
                        GUILayout.Width(24f)))
                {
                    zoom = -1f;
                    Repaint();
                }

                SccGui.VerticalSeparator();

                var dirty = PaintSession.AnyDirty();
                GUILayout.Label(dirty ? "● 未保存の変更あり" : "保存済み", dirty ? DirtyLabel : EditorStyles.miniLabel,
                    GUILayout.ExpandWidth(false));
                using (new EditorGUI.DisabledScope(!dirty))
                {
                    if (GUILayout.Button(new GUIContent("保存", "塗りマスクを PNG に保存する (Ctrl+S)。閉じるときにも自動で保存する"),
                            EditorStyles.toolbarButton, GUILayout.Width(44f)))
                    {
                        PaintSession.SaveAll();
                    }
                }
            }
        }

        private static GUIStyle s_zoomLabel;

        private static GUIStyle ZoomLabel => s_zoomLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };

        private static GUIStyle s_dirtyLabel;

        private static GUIStyle DirtyLabel => s_dirtyLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = SccGui.WarningColor },
        };

        // ------------------------------------------------------------------ 操作の一覧

        private sealed class HelpPopup : PopupWindowContent
        {
            private Vector2 _scroll;

            public override Vector2 GetWindowSize()
            {
                return new Vector2(380f, 360f);
            }

            public override void OnGUI(Rect rect)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                Head("表示");
                Row("ホイール", "拡大・縮小");
                Row("右・中ドラッグ / Alt+ドラッグ", "表示の移動");
                Head("右のパネル");
                Row("色・経路・マスク・その他", "選んでいるレイヤーの設定(レイヤーごとに保存)。道具に合わせて切り替わる");
                Row("いちばん下の「道具」", "今の道具の設定(太さなど。全レイヤー共通)と操作のヒント");
                Head("範囲選択ツール");
                Row("Ctrl+クリック・ドラッグ", "押した場所が選択済みなら削る、それ以外は足す");
                Row("選択範囲があるとき", "描画ツールはその中だけに効く");
                Head("描画ツール");
                Row("Ctrl", "塗ると消すを入れ替える");
                Row("[  ]", "ブラシを細く・太く");
                Head("履歴と保存");
                Row("Ctrl+Z / Ctrl+Y", "元に戻す・やり直す(色の変更なども含めて 1 本)");
                Row("Ctrl+S", "塗りマスクを保存");
                Row("閉じるとき・再読み込みの前", "自動で保存");
                Head("道具");
                foreach (var info in SelectionToolInfos.Concat(DrawToolInfos).Concat(MaskToolInfos))
                {
                    Row(KeyOf(info), info.Name);
                }
                Head("グラデーション");
                Row("バーをクリック", "その位置にストップを足す");
                Row("つまみをドラッグ", "位置を動かす(上下に引き抜くと削除)");
                Row("Delete / ← →", "選んだストップを削除 / 少しずつ動かす");
                Head("グラデーションマスク(グラデーションの道具)");
                Row("直線・円形", "ドラッグで引き直す。つまみで始点・終点を動かす");
                Row("点", "クリックで置く、ドラッグで動かす");
                Row("Ctrl+クリック / Delete", "選んだ点を消す");
                Head("色の経路(色の経路の道具)");
                Row("クリック", "点を足す(線の近くなら間に入れる)");
                Row("ドラッグ", "点を動かす(モデル上はシーンビューのハンドルでも)");
                Row("Ctrl+クリック / Delete", "選んだ点を消す");
                Row("経路タブの一覧", "行のクリックで経路を選ぶ。+ で新しい経路を足す(A¹B¹…、A²B²…。塗りマスクの塊ごとに、点が乗っている経路が効く)");
                Head("3D(上のツールバー)");
                Row("3D 空間操作の有効化", "シーンビューでモデルをクリック・ドラッグして選択や塗りができる");
                Row("3D 空間に選択範囲を描画", "範囲選択の道具の間、モデルに選択範囲を色で表示する");
                GUILayout.Space(4f);
                GUILayout.Label("キーは Edit > Shortcuts の「Spoitable Color Changer」で変えられます。", SccGui.Hint);
                EditorGUILayout.EndScrollView();
            }

            private static void Head(string text)
            {
                GUILayout.Space(4f);
                GUILayout.Label(text, EditorStyles.boldLabel);
            }

            private static void Row(string key, string action)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(key, EditorStyles.miniBoldLabel, GUILayout.Width(170f));
                    GUILayout.Label(action, EditorStyles.miniLabel);
                }
            }
        }

        // ------------------------------------------------------------------ ショートカット

        // Unity 全体のショートカット(W/E/Q の移動・回転・視点ツール、Ctrl+S のシーンの保存など)は、
        // ウィンドウの KeyDown より先に処理されてしまう。このウィンドウを文脈にして登録すると、
        // ウィンドウにフォーカスがある間はこちらが優先される。割り当ては Shortcuts の設定で変えられる。
        // Ctrl+Z / Ctrl+Y は、マスクの履歴を Unity の Undo に載せているので Unity 標準のものをそのまま使う。
        private const string ShortcutPrefix = "Spoitable Color Changer/";

        private static Dictionary<string, string> s_keys;

        private void OnShortcutBindingChanged(ShortcutBindingChangedEventArgs args)
        {
            s_keys = null;
            Repaint();
        }

        /// <summary>道具に今割り当てられているキー(表示用)。</summary>
        private static string KeyOf(ToolInfo info)
        {
            s_keys ??= new Dictionary<string, string>();
            if (s_keys.TryGetValue(info.ShortcutId, out var key)) return key;
            try
            {
                key = ShortcutManager.instance.GetShortcutBinding(ShortcutPrefix + info.ShortcutId).ToString();
            }
            catch (ArgumentException)
            {
                key = "";
            }

            s_keys[info.ShortcutId] = key;
            return key;
        }

        /// <summary>ドラッグ中は、道具の切り替えなどをすると途中の状態と食い違うので受け付けない。</summary>
        private bool Busy => _toolDragging || _panning;

        private static void WithWindow(ShortcutArguments args, Action<MaskEditorWindow> action)
        {
            if (!(args.context is MaskEditorWindow window) || window.Busy) return;
            // 入力欄に文字を打っている間は、1 文字のキーを道具の切り替えにしない
            if (EditorGUIUtility.editingTextField) return;
            action(window);
            window.Repaint();
        }

        [Shortcut(ShortcutPrefix + "Save", typeof(MaskEditorWindow), KeyCode.S, ShortcutModifiers.Action)]
        private static void SaveShortcut(ShortcutArguments args)
        {
            WithWindow(args, w =>
            {
                PaintSession.SaveAll();
                w.SetMessage("塗りマスクを保存しました");
            });
        }

        [Shortcut(ShortcutPrefix + "Pen", typeof(MaskEditorWindow), KeyCode.P)]
        private static void PenShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Pen);
        }

        [Shortcut(ShortcutPrefix + "Brush", typeof(MaskEditorWindow), KeyCode.B)]
        private static void BrushShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Brush);
        }

        [Shortcut(ShortcutPrefix + "Eraser", typeof(MaskEditorWindow), KeyCode.E)]
        private static void EraserShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Eraser);
        }

        [Shortcut(ShortcutPrefix + "Bucket", typeof(MaskEditorWindow), KeyCode.G)]
        private static void BucketShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Bucket);
        }

        [Shortcut(ShortcutPrefix + "Rect Select", typeof(MaskEditorWindow), KeyCode.M)]
        private static void RectShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Rect);
        }

        [Shortcut(ShortcutPrefix + "Lasso Select", typeof(MaskEditorWindow), KeyCode.L)]
        private static void LassoShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.Lasso);
        }

        [Shortcut(ShortcutPrefix + "Pen Select", typeof(MaskEditorWindow), KeyCode.Q)]
        private static void SelectBrushShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.SelectBrush);
        }

        [Shortcut(ShortcutPrefix + "Color Select", typeof(MaskEditorWindow), KeyCode.W)]
        private static void ColorSelectShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.ColorSelect);
        }

        [Shortcut(ShortcutPrefix + "UV Island Select", typeof(MaskEditorWindow), KeyCode.U)]
        private static void UvIslandShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.tool = Tool.UvIsland);
        }

        [Shortcut(ShortcutPrefix + "Gradient", typeof(MaskEditorWindow), KeyCode.D)]
        private static void GradientShortcut(ShortcutArguments args)
        {
            WithWindow(args, w =>
            {
                w.tool = Tool.Gradient;
                SceneView.RepaintAll();
            });
        }

        [Shortcut(ShortcutPrefix + "Color Path", typeof(MaskEditorWindow), KeyCode.C)]
        private static void ColorPathShortcut(ShortcutArguments args)
        {
            WithWindow(args, w =>
            {
                w.tool = Tool.ColorPath;
                SceneView.RepaintAll();
            });
        }

        [Shortcut(ShortcutPrefix + "Brush Smaller", typeof(MaskEditorWindow), KeyCode.LeftBracket)]
        private static void BrushSmallerShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.brushRadius = Mathf.Max(1f, w.brushRadius / 1.2f));
        }

        [Shortcut(ShortcutPrefix + "Brush Larger", typeof(MaskEditorWindow), KeyCode.RightBracket)]
        private static void BrushLargerShortcut(ShortcutArguments args)
        {
            WithWindow(args, w => w.brushRadius = Mathf.Min(512f, w.brushRadius * 1.2f));
        }
    }
}
