using System;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    // 左の道具・右のパネル(上にレイヤーの設定のタブ、いちばん下に今の道具の設定)
    internal sealed partial class MaskEditorWindow
    {
        private readonly struct ToolInfo
        {
            public readonly Tool Tool;
            public readonly SccIcon Icon;
            public readonly string Name;
            public readonly string ShortcutId;
            public readonly string Description;

            public ToolInfo(Tool tool, SccIcon icon, string name, string shortcutId, string description)
            {
                Tool = tool;
                Icon = icon;
                Name = name;
                ShortcutId = shortcutId;
                Description = description;
            }
        }

        private static readonly ToolInfo[] SelectionToolInfos =
        {
            new ToolInfo(Tool.Rect, SccIcon.RectSelect, "矩形選択", "Rect Select", "四角く囲んで選択範囲を作る"),
            new ToolInfo(Tool.Lasso, SccIcon.Lasso, "投げ縄選択", "Lasso Select", "自由な形で囲んで選択範囲を作る"),
            new ToolInfo(Tool.SelectBrush, SccIcon.PenSelect, "ペン選択", "Pen Select", "なぞったところを選択範囲に足す。Ctrl を押して選択済みの場所から始めると削る"),
            new ToolInfo(Tool.ColorSelect, SccIcon.ColorSelect, "色選択", "Color Select", "クリックした色に近い部分を選択範囲にする"),
            new ToolInfo(Tool.UvIsland, SccIcon.UvIsland, "UV 島選択", "UV Island Select", "クリックした UV アイランドを選択範囲にする"),
        };

        private static readonly ToolInfo[] DrawToolInfos =
        {
            new ToolInfo(Tool.Pen, SccIcon.Pen, "ペン", "Pen", "縁のくっきりした線でマスクを塗る"),
            new ToolInfo(Tool.Brush, SccIcon.Brush, "ブラシ", "Brush", "縁のやわらかい線でマスクを塗る"),
            new ToolInfo(Tool.Eraser, SccIcon.Eraser, "消しゴム", "Eraser", "塗ったマスクを消す"),
            new ToolInfo(Tool.Bucket, SccIcon.Bucket, "バケツ", "Bucket", "クリックした色に近い範囲をまとめて塗る"),
        };

        private static readonly ToolInfo[] MaskToolInfos =
        {
            new ToolInfo(Tool.Gradient, SccIcon.Gradient, "グラデーション", "Gradient",
                "位置で濃さが変わるマスク(グラデーションマスク)をキャンバスで決める"),
            new ToolInfo(Tool.ColorPath, SccIcon.ColorPath, "色の経路", "Color Path",
                "点を結んだ線に沿ってレイヤーの色を並べる(Normal / Relative Grade)"),
        };

        private static readonly GUIContent[] OpContents =
        {
            new GUIContent("新規", "今の選択範囲を置き換える"),
            new GUIContent("追加", "今の選択範囲に足す(Ctrl を押して、選ばれていない場所を押しても同じ)"),
            new GUIContent("削除", "今の選択範囲から削る(Ctrl を押して、選択済みの場所を押しても同じ)"),
        };

        private static readonly GUIContent[] BrushOpContents =
        {
            new GUIContent("追加", "なぞったところを選択範囲に足す"),
            new GUIContent("削除", "なぞったところを選択範囲から削る(Ctrl を押して、選択済みの場所から始めても同じ)"),
        };

        private static readonly string[] UvDisplayLabels = { "UV 島の道具のとき", "常に表示", "表示しない" };

        private static ToolInfo InfoOf(Tool t)
        {
            foreach (var info in SelectionToolInfos)
            {
                if (info.Tool == t) return info;
            }

            foreach (var info in DrawToolInfos)
            {
                if (info.Tool == t) return info;
            }

            foreach (var info in MaskToolInfos)
            {
                if (info.Tool == t) return info;
            }

            return DrawToolInfos[0];
        }

        private static bool IsSelectionTool(Tool t)
        {
            return t == Tool.Rect || t == Tool.Lasso || t == Tool.SelectBrush || t == Tool.ColorSelect ||
                   t == Tool.UvIsland;
        }

        // ------------------------------------------------------------------ 左: 道具

        private void DrawToolStrip()
        {
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(ToolStripWidth), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, SccGui.HeaderColor);
                EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), SccGui.SeparatorColor);
            }

            GUILayout.Space(5f);
            foreach (var info in SelectionToolInfos) ToolButton(info);
            var line = GUILayoutUtility.GetRect(ToolStripWidth, 9f, GUILayout.Width(ToolStripWidth));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(line.x + 7f, line.center.y, line.width - 14f, 1f), SccGui.SeparatorColor);
            }

            foreach (var info in DrawToolInfos) ToolButton(info);
            var line2 = GUILayoutUtility.GetRect(ToolStripWidth, 9f, GUILayout.Width(ToolStripWidth));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(line2.x + 7f, line2.center.y, line2.width - 14f, 1f), SccGui.SeparatorColor);
            }

            foreach (var info in MaskToolInfos) ToolButton(info);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void ToolButton(ToolInfo info)
        {
            var row = GUILayoutUtility.GetRect(ToolStripWidth, 32f, GUILayout.Width(ToolStripWidth));
            var r = new Rect(row.x + 5f, row.y + 2f, row.width - 10f, 28f);
            var selected = tool == info.Tool;
            var key = KeyOf(info);
            var tooltip = string.IsNullOrEmpty(key) ? $"{info.Name}\n{info.Description}" : $"{info.Name} ({key})\n{info.Description}";
            var next = GUI.Toggle(r, selected, new GUIContent("", tooltip), SccGui.ToolButton);
            if (next && !selected)
            {
                tool = info.Tool;
                Repaint();
                SceneView.RepaintAll(); // グラデーションの道具のときだけシーンビューにハンドルを出すので
            }

            if (Event.current.type == EventType.Repaint && selected)
            {
                EditorGUI.DrawRect(new Rect(row.x + 1f, r.y + 4f, 2f, r.height - 8f), SccGui.AccentColor);
            }

            SccIcons.Draw(info.Icon, new Rect(r.center.x - 10f, r.center.y - 10f, 20f, 20f),
                selected ? SccGui.IconSelectedColor : SccGui.IconColor);
        }

        // ------------------------------------------------------------------ 右下: 道具の設定

        /// <summary>
        /// 右パネルのいちばん下の、今の道具の設定(全レイヤー共通)と操作のヒント。レイヤーに保存する設定はタブに置く。
        /// t は Layout のときの道具(同じフレームの途中で道具が変わっても、欄の数がずれないように)。
        /// </summary>
        private void DrawToolSection(Tool t, PaintSession session)
        {
            var rect = EditorGUILayout.BeginVertical(ToolSectionStyle);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, SccGui.HeaderColor);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2f), SccGui.SeparatorColor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var info = InfoOf(t);
                var iconRect = GUILayoutUtility.GetRect(18f, 18f, GUILayout.Width(18f), GUILayout.Height(18f));
                SccIcons.Draw(info.Icon, iconRect, SccGui.IconColor);
                GUILayout.Label("道具: " + info.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                GUILayout.Label(new GUIContent("全レイヤー共通", "ここの設定はレイヤーを切り替えても変わらない"),
                    EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            }

            var labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 72f;
            switch (t)
            {
                case Tool.Pen:
                    brushRadius = ToolSlider("太さ", "ブラシの半径(画素)", brushRadius, 1f, 512f);
                    brushStrength = ToolSlider("強さ", "1 回のストロークで塗る濃さ", brushStrength, 0.01f, 1f);
                    break;
                case Tool.Brush:
                case Tool.Eraser:
                    brushRadius = ToolSlider("太さ", "ブラシの半径(画素)", brushRadius, 1f, 512f);
                    brushHardness = ToolSlider("硬さ", "小さいほど縁がぼける", brushHardness, 0f, 1f);
                    brushStrength = ToolSlider("強さ", "1 回のストロークで塗る濃さ", brushStrength, 0.01f, 1f);
                    break;
                case Tool.Bucket:
                    tolerance = ToolSlider("許容範囲", "OKLab での色の距離。大きいほど広く塗る", tolerance, 0.005f, 0.3f);
                    contiguous = EditorGUILayout.ToggleLeft(
                        new GUIContent("つながりのみ", "クリックしたところからつながっている範囲だけ"), contiguous);
                    break;
                case Tool.Rect:
                case Tool.Lasso:
                    selectionOp = GUILayout.Toolbar(selectionOp, OpContents, EditorStyles.miniButton);
                    break;
                case Tool.ColorSelect:
                    selectionOp = GUILayout.Toolbar(selectionOp, OpContents, EditorStyles.miniButton);
                    tolerance = ToolSlider("許容範囲", "OKLab での色の距離。大きいほど広く選ぶ", tolerance, 0.005f, 0.3f);
                    contiguous = EditorGUILayout.ToggleLeft(
                        new GUIContent("つながりのみ", "クリックしたところからつながっている範囲だけ"), contiguous);
                    break;
                case Tool.SelectBrush:
                    // なぞって塗り足していく道具なので「新規」はなく、追加が既定
                    selectBrushSubtract =
                        GUILayout.Toolbar(selectBrushSubtract ? 1 : 0, BrushOpContents, EditorStyles.miniButton) == 1;
                    brushRadius = ToolSlider("太さ", "ブラシの半径(画素)", brushRadius, 1f, 512f);
                    brushHardness = ToolSlider("硬さ", "小さいほど選択範囲の縁がぼける", brushHardness, 0f, 1f);
                    break;
                case Tool.UvIsland:
                    selectionOp = GUILayout.Toolbar(selectionOp, OpContents, EditorStyles.miniButton);
                    islandGrow = EditorGUILayout.IntSlider(
                        new GUIContent("はみ出し", "アイランドの縁から外へ広げる幅(画素)。縁に元の色が筋になって残るのを防ぐ"),
                        islandGrow, 0, 16);
                    if (session == null) GUILayout.Label(" ", EditorStyles.miniLabel);
                    else if (_uvError != null) GUILayout.Label(new GUIContent("UV を読めません", _uvError), WarningLabel);
                    else if (_uv == null || _uv.TriangleCount == 0)
                        GUILayout.Label("このテクスチャを使うメッシュがありません", WarningLabel);
                    else
                        GUILayout.Label($"メッシュ {_uv.SourceCount} / 島 {_uv.IslandCount}", EditorStyles.miniLabel);
                    break;
                case Tool.Gradient:
                    if (_panelGradientPoints)
                    {
                        newPointValue = ToolSlider("新しい点の値", "クリックで置く点の値(0 = マスクなし、1 = 全部)", newPointValue, 0f, 1f);
                    }

                    break;
            }

            EditorGUIUtility.labelWidth = labelWidth;
            GUILayout.Space(2f);
            GUILayout.Label(ToolHint(t), SccGui.Hint);
            EditorGUILayout.EndVertical();
        }

        private static float ToolSlider(string label, string tooltip, float value, float min, float max)
        {
            return EditorGUILayout.Slider(new GUIContent(label, tooltip), value, min, max);
        }

        private static GUIStyle s_toolSection;

        private static GUIStyle ToolSectionStyle => s_toolSection ??= new GUIStyle
        {
            padding = new RectOffset(8, 8, 6, 8),
        };

        /// <summary>道具の欄の下に出す操作のヒント(1 行に 1 つ)。</summary>
        private string ToolHint(Tool t)
        {
            switch (t)
            {
                case Tool.Rect:
                case Tool.Lasso:
                case Tool.ColorSelect:
                case Tool.UvIsland:
                    return "Ctrl: 選択済みの場所なら削る・それ以外は足す";
                case Tool.SelectBrush:
                    return "Ctrl: 選択済みから始めると削る・それ以外は足す\n[ ]: 太さ";
                case Tool.Bucket:
                    return MaskSelection.Exists ? "選択範囲の中だけ塗る\nCtrl: 消す" : "Ctrl: 消す";
                case Tool.Gradient:
                    return GradientHint();
                case Tool.ColorPath:
                    return ColorPathHint();
                default:
                    return MaskSelection.Exists ? "選択範囲の中だけ塗る\nCtrl: 塗る⇔消す\n[ ]: 太さ" : "Ctrl: 塗る⇔消す\n[ ]: 太さ";
            }
        }

        private static GUIStyle s_warningLabel;

        private static GUIStyle WarningLabel => s_warningLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = SccGui.WarningColor },
        };

        // ------------------------------------------------------------------ 右: レイヤーと操作

        private static readonly GUIContent[] PanelTabContents =
        {
            new GUIContent("色", "このレイヤーの色(グラデーションのバー・不透明度など)"),
            new GUIContent("経路", "色の経路(点を結んだ線に沿ってこのレイヤーの色を並べる)"),
            new GUIContent("マスク", "このレイヤーの色が乗る範囲(塗りマスク・画像マスク・グラデーションマスク)と選択範囲"),
            new GUIContent("その他", "プリセットと明るさ、表示"),
        };

        /// <summary>
        /// 右パネル。上から、レイヤー一覧・選んでいるレイヤーの設定のタブ(レイヤーごとに保存)・今の道具の設定(全レイヤー共通、
        /// いちばん下に固定)。
        /// </summary>
        private void DrawRightPanel(SpoitableColorPreset preset, MaskBinding binding, PaintSession session, Texture source)
        {
            // タブと道具の欄の組み方は Layout のときに決める(同じフレームの途中で道具が変わると、欄の数がずれるので)
            if (Event.current.type == EventType.Layout)
            {
                if (panelTool != tool)
                {
                    if (tool == Tool.ColorPath) panelTab = PanelTab.Path;
                    else if (tool == Tool.Gradient) panelTab = PanelTab.Mask;
                    panelTool = tool;
                }

                _panelGradientPoints = IsPointsMode(CurrentGradient().Mode);
            }

            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(RightPanelWidth), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), SccGui.SeparatorColor);
            }

            var component = ComponentObject();
            _presetGui.Begin(preset);
            try
            {
                SccGui.Section("レイヤー", note: "上から順に重ねる");
                using (new EditorGUILayout.VerticalScope(ListPadding))
                {
                    _presetGui.DrawLayerList(layerName);
                    MaskBindingGui.DrawOrphans(component, preset);
                }

                DrawPanelTabs();
                _panelScroll = EditorGUILayout.BeginScrollView(_panelScroll, false, false, GUIStyle.none,
                    GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.ExpandHeight(true));
                switch (panelTab)
                {
                    case PanelTab.Color:
                        SccGui.Section("色", note: layerName);
                        using (SccGui.Body()) _presetGui.DrawLayerProperties(layerName, source);
                        break;
                    case PanelTab.Path:
                        SccGui.Section("色の経路", note: layerName);
                        using (SccGui.Body()) DrawColorPathPanel(source);
                        break;
                    case PanelTab.Mask:
                        DrawMaskSection(component, session, source);
                        DrawSelectionSection(session);
                        break;
                    default:
                        DrawPresetSection(component, source);
                        DrawDisplaySection();
                        break;
                }

                EditorGUILayout.EndScrollView();
                DrawToolSection(panelTool, session);
            }
            finally
            {
                _presetGui.End();
                component.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPanelTabs()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                var next = (PanelTab)GUILayout.Toolbar((int)panelTab, PanelTabContents, EditorStyles.toolbarButton);
                if (!EditorGUI.EndChangeCheck() || next == panelTab) return;
                panelTab = next;
                _panelScroll = Vector2.zero;
                GUIUtility.ExitGUI(); // 中身が入れ替わるので、このフレームはここまで
            }
        }

        private static GUIStyle s_listPadding;

        private static GUIStyle ListPadding => s_listPadding ??= new GUIStyle { padding = new RectOffset(6, 6, 6, 2) };

        /// <summary>
        /// コンポーネントの SerializedObject(対象が変わったら作り直す)。読み直し(Update)は OnGUI の最初に 1 回だけ行う。
        /// </summary>
        private SerializedObject ComponentObject()
        {
            if (_componentObject == null || _componentObject.targetObject != target)
            {
                _componentObject?.Dispose();
                _componentObject = new SerializedObject(target);
                _componentObjectUses = 0;
            }

            return _componentObject;
        }

        /// <summary>
        /// OnGUI の最初に 1 回だけ呼ぶ。長く使った SerializedObject は、ドラッグの途中でなければ作り直す
        /// (Unity が中で呼ぶ GetArrayElementAtIndex の分だけ、少しずつ遅くなるため)。
        /// </summary>
        private SerializedObject ComponentObjectForEvent()
        {
            if (_componentObject != null && ++_componentObjectUses > SccGui.SerializedObjectRenewInterval &&
                SccGui.CanRenewSerializedObject && !Busy && !_pathDragging && _gradientDrag == 0 && !_sceneStroke)
            {
                // 古いものは Dispose しない(開いているメニューのコールバックが握っていることがある)
                _componentObject = null;
            }

            return ComponentObject();
        }

        private void SetupPresetGui()
        {
            _presetGui = new PresetEditorGui
            {
                Selected = name =>
                {
                    // 一覧の下のタブの中身は、このイベントでは前のレイヤーで組んである(Layout で数えた欄の数が
                    // レイヤーで変わる)ので、切り替えは次の Layout で行う
                    _pendingLayerName = name;
                    Repaint();
                },
                Renamed = (oldName, newName) =>
                {
                    if (target == null) return;
                    var component = ComponentObject();
                    MaskBindingGui.Rename(component, oldName, newName);
                    component.ApplyModifiedProperties();
                    if (layerName == oldName) layerName = newName;
                },
                Badge = name => target != null ? MaskBadge(target.FindMask(name)) : null,
                Layout = PathStopLayout,
                CanRename = (oldName, newName) =>
                {
                    if (target == null) return null;
                    var component = ComponentObject();
                    // 削除したレイヤーのマスクの割り当てが同じ名前で残っていると、改名したレイヤーがそれを引き継いでしまう
                    if (MaskBindingGui.FindIndex(component, newName) >= 0 && MaskBindingGui.FindIndex(component, oldName) >= 0)
                    {
                        return $"「{newName}」には、前に使っていたマスクの割り当てが残っています。レイヤー一覧の下の警告から削除か付け替えをしてから変えてください。";
                    }

                    return null;
                },
            };
        }

        private static string MaskBadge(MaskBinding binding)
        {
            return MaskBindingGui.Badge(binding);
        }

        private void DrawMaskSection(SerializedObject component, PaintSession session, Texture source)
        {
            SccGui.Section("マスク", note: layerName);
            using (SccGui.Body())
            {
                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 72f;

                var element = MaskBindingGui.Find(component, layerName);
                var paint = element?.FindPropertyRelative(nameof(MaskBinding.paintMask)).objectReferenceValue as Texture2D;
                EditorGUI.BeginChangeCheck();
                var newPaint = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("塗りマスク", "このウィンドウで塗る範囲(PNG)。白い部分にだけこのレイヤーの色が乗る。未指定なら全体"),
                    paint, typeof(Texture2D), false, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (EditorGUI.EndChangeCheck())
                {
                    element = MaskBindingGui.Ensure(component, layerName);
                    element.FindPropertyRelative(nameof(MaskBinding.paintMask)).objectReferenceValue = newPaint;
                    component.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }

                if (session == null)
                {
                    GUILayout.Label("塗りマスクを作ると、塗った部分(白)にだけこのレイヤーの色が乗ります。", SccGui.Hint);
                    if (GUILayout.Button("塗りマスクを作成", GUILayout.Height(24f))) CreateMask(source);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent("全体を塗る", "マスク全体を白にする"), EditorStyles.miniButtonLeft))
                        {
                            Edit(session, d => MaskOps.Fill(d, 255), "マスクを全体に塗る");
                        }

                        if (GUILayout.Button(new GUIContent("全部消す", "マスク全体を黒にする"), EditorStyles.miniButtonMid))
                        {
                            Edit(session, d => MaskOps.Fill(d, 0), "マスクを全部消す");
                        }

                        if (GUILayout.Button(new GUIContent("反転", "塗ったところと塗っていないところを入れ替える"),
                                EditorStyles.miniButtonRight))
                        {
                            Edit(session, MaskOps.Invert, "マスクを反転");
                        }
                    }
                }

                GUILayout.Space(4f);
                MaskBindingGui.DrawImageMask(component, layerName);
                GUILayout.Space(4f);
                DrawGradientPanel(source);
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        private void DrawPresetSection(SerializedObject component, Texture source)
        {
            var open = SccGui.Section("プリセットと明るさ", showPresetSection, true);
            if (open != showPresetSection)
            {
                showPresetSection = open;
                GUIUtility.ExitGUI(); // 中身の有無でコントロールの数が変わるので、このフレームはここまで
            }

            if (!showPresetSection) return;
            using (SccGui.Body())
            {
                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 72f;
                if (MaskBindingGui.DrawPresetField(component)) GUIUtility.ExitGUI();
                EditorGUIUtility.labelWidth = labelWidth;
                GUILayout.Space(4f);
                _presetGui.DrawRange(source);
            }
        }

        private void DrawSelectionSection(PaintSession session)
        {
            var exists = MaskSelection.Exists;
            SccGui.Section("選択範囲", note: exists ? "あり" : "なし");
            using (SccGui.Body())
            {
                GUILayout.Label(exists
                        ? "描画ツールは選択範囲の中だけに効きます(外側は暗く表示)。レイヤーを切り替えても残ります。"
                        : "選択範囲がないときは、描画ツールは全体に効きます。",
                    SccGui.Hint);
                GUILayout.Space(2f);
                using (new EditorGUI.DisabledScope(session == null))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("全選択", EditorStyles.miniButtonLeft))
                        {
                            var all = new byte[session.Data.Length];
                            MaskOps.Fill(all, 255);
                            EditSelection(session, all, "すべてを選択");
                        }

                        using (new EditorGUI.DisabledScope(!exists))
                        {
                            if (GUILayout.Button("解除", EditorStyles.miniButtonMid)) EditSelection(session, null, "選択を解除");
                            if (GUILayout.Button("反転", EditorStyles.miniButtonMid))
                            {
                                var inverted = (byte[])MaskSelection.For(session.Width, session.Height).Clone();
                                MaskOps.Invert(inverted);
                                EditSelection(session, inverted, "選択範囲を反転");
                            }
                        }

                        if (GUILayout.Button(new GUIContent("マスクから", "今のレイヤーのマスクの形を選択範囲にする"),
                                EditorStyles.miniButtonRight))
                        {
                            EditSelection(session, (byte[])session.Data.Clone(), "マスクから選択");
                        }
                    }

                    using (new EditorGUI.DisabledScope(!exists))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent("選択範囲を塗る", "選択範囲をこのレイヤーのマスクに塗る"),
                                EditorStyles.miniButtonLeft))
                        {
                            var selection = MaskSelection.For(session.Width, session.Height);
                            Edit(session, d => MaskOps.FillSelection(d, selection, 255), "選択範囲を塗る");
                        }

                        if (GUILayout.Button(new GUIContent("選択範囲を消す", "選択範囲のマスクを消す"),
                                EditorStyles.miniButtonRight))
                        {
                            var selection = MaskSelection.For(session.Width, session.Height);
                            Edit(session, d => MaskOps.FillSelection(d, selection, 0), "選択範囲を消す");
                        }
                    }
                }
            }
        }

        private void DrawDisplaySection()
        {
            var open = SccGui.Section("表示", showDisplaySection, true);
            if (open != showDisplaySection)
            {
                showDisplaySection = open;
                GUIUtility.ExitGUI(); // 中身の有無でコントロールの数が変わるので、このフレームはここまで
            }

            if (!showDisplaySection) return;
            using (SccGui.Body())
            {
                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 96f;
                overlayOpacity = EditorGUILayout.Slider("マスクの濃さ", overlayOpacity, 0f, 1f);
                overlayColor = EditorGUILayout.ColorField("マスクの色", overlayColor);
                uvDisplay = (UvDisplay)EditorGUILayout.Popup("UV の線", (int)uvDisplay, UvDisplayLabels);
                using (new EditorGUI.DisabledScope(uvDisplay == UvDisplay.Off))
                {
                    uvAllEdges = EditorGUILayout.Toggle(new GUIContent("内側の辺も", "輪郭だけでなく、すべての辺を表示する"),
                        uvAllEdges);
                }

                DrawSceneDisplayOptions();
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        // ------------------------------------------------------------------ 編集の共通処理

        /// <summary>マスクを書き換える。何も変わらなければ履歴に残さない。</summary>
        private void Edit(PaintSession session, Action<byte[]> operation, string undoName)
        {
            MaskHistory.Begin(session);
            operation(session.Data);
            if (!MaskOps.SameBytes(session.Snapshot, session.Data)) session.Changed();
            MaskHistory.End(undoName);
            Repaint();
        }

        /// <summary>
        /// 選択範囲を置き換える。空になったら選択解除として扱う(Photoshop と同じ)。何も変わらなければ履歴に残さない。
        /// </summary>
        private void EditSelection(PaintSession session, byte[] selection, string undoName)
        {
            if (selection != null && MaskOps.IsEmpty(selection)) selection = null;
            if (selection == null && !MaskSelection.Exists) return;
            MaskHistory.Begin(null);
            MaskSelection.Set(selection, session.Width, session.Height);
            MaskHistory.End(undoName);
            Repaint();
        }
    }
}
