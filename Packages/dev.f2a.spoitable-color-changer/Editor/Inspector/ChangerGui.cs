using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// SpoitableColorChanger のインスペクタ。対象の設定、プリセット、レイヤーの一覧と、エディタを開くボタンだけを出す。
    /// 色とマスクの編集はエディタ(MaskEditorWindow)で行う。
    /// 変更はすべて SerializedObject を通す(NDMF のプレビューは Undo に記録された変更にだけ反応するため)。
    /// </summary>
    internal sealed class ChangerGui : IDisposable
    {
        private static readonly string[] ModeOptions = { "Renderer を指定", "マテリアルを指定(アバター内の全 Renderer)" };

        private readonly GradientPreviews _previews = new GradientPreviews();
        private SerializedObject _so;
        private SerializedObject _presetSo;

        /// <summary>
        /// 描くコントロールの数が変わる変更のあとに呼ぶ。IMGUI は Layout のときと同じ数のコントロールを
        /// 要求するので、変更を確定させてこのフレームの描画を打ち切る。
        /// </summary>
        private void ApplyAndExit()
        {
            _so.ApplyModifiedProperties();
            GUIUtility.ExitGUI();
        }

        public void Draw(SpoitableColorChanger component)
        {
            if (component == null) return;
            if (_so == null || _so.targetObject != component)
            {
                _so?.Dispose();
                _so = new SerializedObject(component);
            }

            _so.Update();

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                EditorGUILayout.HelpBox("プロジェクトの色空間が Linear ではありません。VRChat のプロジェクトは Linear が前提です。",
                    MessageType.Warning);
            }

            var preset = (SpoitableColorPreset)_so.FindProperty(nameof(SpoitableColorChanger.preset)).objectReferenceValue;
            var hasTexture = TargetResolver.TargetTextures(component).Count > 0;
            using (new EditorGUI.DisabledScope(!hasTexture))
            {
                if (GUILayout.Button(new GUIContent("エディタを開く", "テクスチャを見ながら、レイヤーの色とマスクを編集する"),
                        GUILayout.Height(30f)))
                {
                    _so.ApplyModifiedProperties();
                    MaskEditorWindow.Open(component, null);
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space();
            SccGui.Section("対象");
            using (SccGui.Body())
            {
                DrawTarget(component);
            }

            SccGui.Section("配色");
            using (SccGui.Body())
            {
                if (MaskBindingGui.DrawPresetField(_so)) GUIUtility.ExitGUI();
                if (preset != null)
                {
                    GUILayout.Space(4f);
                    DrawLayerSummary(component, preset);
                    MaskBindingGui.DrawOrphans(_so, preset);
                }
            }

            SccGui.Section("確認");
            using (SccGui.Body())
            {
                EditorGUILayout.PropertyField(_so.FindProperty(nameof(SpoitableColorChanger.previewEnabled)),
                    new GUIContent("プレビューに反映", "OFF にすると、このコンポーネントだけプレビューで元の色に戻る(見比べ用)。ビルド結果は変わらない"));

                using (new EditorGUI.DisabledScope(preset == null || !hasTexture))
                {
                    if (GUILayout.Button("PNG で書き出し(確認用)"))
                    {
                        _so.ApplyModifiedProperties();
                        ExportPng(component);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            _so.ApplyModifiedProperties();
        }

        /// <summary>レイヤーの一覧(表示の切り替えと、クリックでそのレイヤーをエディタで開く)。</summary>
        private void DrawLayerSummary(SpoitableColorChanger component, SpoitableColorPreset preset)
        {
            if (_presetSo == null || _presetSo.targetObject != preset)
            {
                _presetSo?.Dispose();
                _presetSo = new SerializedObject(preset);
                _previews.Clear();
            }

            _presetSo.Update();
            var layers = _presetSo.FindProperty(nameof(SpoitableColorPreset.layers));
            if (layers.arraySize == 0)
            {
                GUILayout.Label("レイヤーがありません。エディタで追加できます。", SccGui.Hint);
                return;
            }

            GUILayout.Label("レイヤー(上から順に重ねる。クリックでエディタを開く)", SccGui.Hint);
            var e = Event.current;
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.ArrayElement(i);
                var name = layer.FindPropertyRelative(nameof(ColorLayer.name)).stringValue;
                var visible = layer.FindPropertyRelative(nameof(ColorLayer.visible));
                var stops = layer.FindPropertyRelative(nameof(ColorLayer.stops));

                var rect = GUILayoutUtility.GetRect(10f, 24f, GUILayout.ExpandWidth(true));
                if (e.type == EventType.Repaint && rect.Contains(e.mousePosition)) EditorGUI.DrawRect(rect, SccGui.HoverRowColor);

                var eyeRect = new Rect(rect.x + 4f, rect.y + 4f, 16f, 16f);
                var eye = EditorGUIUtility.IconContent(visible.boolValue ? "scenevis_visible" : "scenevis_hidden");
                if (GUI.Button(eyeRect,
                        new GUIContent(eye.image, visible.boolValue ? "表示中(クリックで隠す)" : "非表示(クリックで表示)"),
                        GUIStyle.none))
                {
                    visible.boolValue = !visible.boolValue;
                }

                var thumbRect = new Rect(eyeRect.xMax + 6f, rect.y + 6f, 48f, 12f);
                if (e.type == EventType.Repaint && stops.arraySize > 0)
                {
                    var texture = _previews.Get(layer.propertyPath, GradientEditorGui.ReadStops(stops));
                    EditorGUI.DrawRect(new Rect(thumbRect.x - 1f, thumbRect.y - 1f, thumbRect.width + 2f, thumbRect.height + 2f),
                        SccGui.SeparatorColor);
                    if (texture != null) GUI.DrawTexture(thumbRect, texture, ScaleMode.StretchToFill);
                }

                var badge = MaskBindingGui.Badge(component.FindMask(name));
                var badgeWidth = string.IsNullOrEmpty(badge) ? 0f : SccGui.Badge.CalcSize(new GUIContent(badge)).x + 6f;
                var nameRect = new Rect(thumbRect.xMax + 8f, rect.y, rect.xMax - thumbRect.xMax - 10f - badgeWidth, rect.height);
                var color = GUI.color;
                if (!visible.boolValue) GUI.color = new Color(color.r, color.g, color.b, 0.5f);
                GUI.Label(nameRect, name, SccGui.RowLabel);
                GUI.color = color;
                if (badgeWidth > 0f)
                {
                    GUI.Label(new Rect(rect.xMax - badgeWidth, rect.y, badgeWidth - 2f, rect.height), badge, SccGui.Badge);
                }

                EditorGUIUtility.AddCursorRect(new Rect(thumbRect.x, rect.y, rect.xMax - thumbRect.x, rect.height),
                    MouseCursor.Link);
                if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition) &&
                    !eyeRect.Contains(e.mousePosition) && TargetResolver.TargetTextures(component).Count > 0)
                {
                    _presetSo.ApplyModifiedProperties();
                    _so.ApplyModifiedProperties();
                    MaskEditorWindow.Open(component, name);
                    e.Use();
                    GUIUtility.ExitGUI();
                }
            }

            _presetSo.ApplyModifiedProperties();
            Repaint();
        }

        /// <summary>行のホバー表示のため、マウスが動いたら描き直してもらう。</summary>
        private static void Repaint()
        {
            if (Event.current.type == EventType.MouseMove) HandleUtility.Repaint();
        }

        /// <returns>対象のマテリアル(テクスチャプロパティの候補に使う)</returns>
        private List<Material> DrawTarget(SpoitableColorChanger component)
        {
            var mode = _so.FindProperty(nameof(SpoitableColorChanger.targetMode));
            EditorGUI.BeginChangeCheck();
            var modeIndex = EditorGUILayout.Popup("指定方法", mode.enumValueIndex, ModeOptions);
            if (EditorGUI.EndChangeCheck())
            {
                mode.enumValueIndex = modeIndex;
                ApplyAndExit();
            }

            return mode.enumValueIndex == (int)TargetMode.Material
                ? DrawMaterialTarget(component)
                : DrawRendererTarget(component);
        }

        private List<Material> DrawMaterialTarget(SpoitableColorChanger component)
        {
            var targetMaterial = _so.FindProperty(nameof(SpoitableColorChanger.targetMaterial));
            var props = _so.FindProperty(nameof(SpoitableColorChanger.textureProperties));

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(targetMaterial,
                new GUIContent("マテリアル", "アバター内でこのマテリアルを使うスロットすべてに適用する"));
            if (EditorGUI.EndChangeCheck()) ApplyAndExit();

            var material = targetMaterial.objectReferenceValue as Material;
            if (material == null)
            {
                EditorGUILayout.HelpBox("マテリアルを指定してください。アバター内でそのマテリアルを使う Renderer すべてに適用します。",
                    MessageType.Info);
                return new List<Material>();
            }

            var root = TargetResolver.AvatarRootOf(component);
            var users = TargetResolver.RenderersUsing(root, material);
            if (users.Count == 0)
            {
                EditorGUILayout.HelpBox($"{root.name} の中に、このマテリアルを使う Renderer がありません。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(" ",
                    $"適用先 {users.Count} 個: " + string.Join(", ", users.Select(r => r.name)),
                    EditorStyles.wordWrappedMiniLabel);
            }

            var materials = new List<Material> { material };
            DrawTextureProperties(props, materials);
            return materials;
        }

        private List<Material> DrawRendererTarget(SpoitableColorChanger component)
        {
            var targetRenderer = _so.FindProperty(nameof(SpoitableColorChanger.targetRenderer));
            var slot = _so.FindProperty(nameof(SpoitableColorChanger.materialSlot));
            var props = _so.FindProperty(nameof(SpoitableColorChanger.textureProperties));

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(targetRenderer,
                new GUIContent("Renderer", "未指定なら、このコンポーネントと同じ GameObject の Renderer を使う"));
            if (EditorGUI.EndChangeCheck()) ApplyAndExit();

            var renderer = targetRenderer.objectReferenceValue as Renderer;
            if (renderer == null) renderer = component.GetComponent<Renderer>();
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("Renderer が見つかりません。Renderer を指定するか、Renderer のある GameObject に付けてください。",
                    MessageType.Warning);
                return new List<Material>();
            }

            if (targetRenderer.objectReferenceValue == null)
            {
                EditorGUILayout.LabelField(" ", $"→ {renderer.name}(同じ GameObject)", EditorStyles.miniLabel);
            }

            var materials = renderer.sharedMaterials;
            var outOfRange = slot.intValue >= materials.Length;
            var options = new string[materials.Length + (outOfRange ? 2 : 1)];
            options[0] = "すべてのスロット";
            for (var i = 0; i < materials.Length; i++)
            {
                options[i + 1] = $"{i}: {(materials[i] != null ? materials[i].name : "(なし)")}";
            }

            if (outOfRange) options[options.Length - 1] = $"{slot.intValue}: (この Renderer にはない)";

            var selected = outOfRange ? options.Length - 1 : Mathf.Max(0, slot.intValue + 1);
            EditorGUI.BeginChangeCheck();
            selected = EditorGUILayout.Popup("マテリアルスロット", selected, options);
            if (EditorGUI.EndChangeCheck())
            {
                if (!(outOfRange && selected == options.Length - 1)) slot.intValue = selected - 1;
                ApplyAndExit();
            }

            if (outOfRange)
            {
                EditorGUILayout.HelpBox($"スロット {slot.intValue} はこの Renderer にありません。", MessageType.Warning);
            }

            var slotMaterials = SlotMaterials(materials, slot.intValue);
            DrawTextureProperties(props, slotMaterials);
            return slotMaterials;
        }

        /// <summary>保存済みの設定から、対象のマテリアルを求める(PNG 書き出し用)。</summary>
        private static List<Material> TargetMaterials(SpoitableColorChanger component)
        {
            return TargetResolver.TargetMaterials(component);
        }

        private void DrawTextureProperties(SerializedProperty props, List<Material> materials)
        {
            EditorGUILayout.LabelField("テクスチャプロパティ");
            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < props.arraySize; i++)
                {
                    var prop = props.ArrayElement(i);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var value = EditorGUILayout.DelayedTextField(prop.stringValue);
                        if (EditorGUI.EndChangeCheck())
                        {
                            prop.stringValue = value.Trim();
                            ApplyAndExit();
                        }

                        EditorGUILayout.LabelField(DescribeTexture(materials, prop.stringValue), EditorStyles.miniLabel,
                            GUILayout.Width(150f));
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f)))
                        {
                            props.DeleteArrayElementAtIndex(i);
                            ApplyAndExit();
                        }
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUI.indentLevel * 15f);
                    if (GUILayout.Button("+ プロパティを追加", EditorStyles.miniButton))
                    {
                        ShowPropertyMenu(props, materials);
                    }
                }
            }
        }

        private static List<Material> SlotMaterials(Material[] materials, int slot)
        {
            var list = new List<Material>();
            foreach (var i in RecolorPass.TargetSlots(slot, materials.Length))
            {
                if (materials[i] != null && !list.Contains(materials[i])) list.Add(materials[i]);
            }

            return list;
        }

        private static string DescribeTexture(List<Material> materials, string prop)
        {
            if (string.IsNullOrEmpty(prop)) return "";
            foreach (var m in materials)
            {
                if (!m.HasTexture(prop)) continue;
                var tex = m.GetTexture(prop);
                return tex != null ? tex.name : "(テクスチャなし)";
            }

            return "(このマテリアルにはない)";
        }

        private static void ShowPropertyMenu(SerializedProperty props, List<Material> materials)
        {
            var existing = new HashSet<string>();
            for (var i = 0; i < props.arraySize; i++) existing.Add(props.ArrayElement(i).stringValue);

            var menu = new GenericMenu();
            var seen = new HashSet<string>();
            var owner = props.serializedObject.targetObject;
            var path = props.propertyPath;
            foreach (var m in materials)
            {
                foreach (var name in m.GetTexturePropertyNames())
                {
                    if (!seen.Add(name)) continue;
                    var tex = m.GetTexture(name);
                    var label = tex != null ? $"{name}({tex.name})" : $"{name}(なし)";
                    if (existing.Contains(name))
                    {
                        menu.AddDisabledItem(new GUIContent(label), true);
                        continue;
                    }

                    var captured = name;
                    menu.AddItem(new GUIContent(label), false, () =>
                    {
                        if (owner == null) return;
                        using (var so = new SerializedObject(owner))
                        {
                            var list = so.FindProperty(path);
                            list.arraySize++;
                            list.ArrayElement(list.arraySize - 1).stringValue = captured;
                            so.ApplyModifiedProperties();
                        }
                    });
                }
            }

            if (seen.Count == 0) menu.AddDisabledItem(new GUIContent("テクスチャプロパティが見つかりません"));
            menu.ShowAsContext();
        }

        private static void ExportPng(SpoitableColorChanger component)
        {
            if (component.preset == null) return;

            var config = ComponentConfig.Capture(component);
            var settings = RecolorSettings.Create(PresetConfig.Capture(component.preset), config);

            var sources = new List<Texture>();
            foreach (var m in TargetMaterials(component))
            {
                foreach (var prop in config.TextureProperties)
                {
                    if (!m.HasTexture(prop)) continue;
                    var tex = m.GetTexture(prop);
                    if (tex != null && !sources.Contains(tex)) sources.Add(tex);
                }
            }

            if (sources.Count == 0)
            {
                EditorUtility.DisplayDialog("Spoitable Color Changer", "書き出すテクスチャが見つかりません。", "OK");
                return;
            }

            string directory;
            string singlePath = null;
            if (sources.Count == 1)
            {
                singlePath = EditorUtility.SaveFilePanel("PNG で書き出し", "", sources[0].name + "_recolored", "png");
                if (string.IsNullOrEmpty(singlePath)) return;
                directory = Path.GetDirectoryName(singlePath);
            }
            else
            {
                directory = EditorUtility.SaveFolderPanel("PNG の書き出し先", "", "");
                if (string.IsNullOrEmpty(directory)) return;
            }

            var usedNames = new HashSet<string>();
            foreach (var source in sources)
            {
                // 同じ名前のテクスチャが複数あっても上書きしないよう、名前を分ける
                var fileName = source.name + "_recolored";
                for (var n = 2; !usedNames.Add(fileName); n++) fileName = $"{source.name}_recolored_{n}";

                Texture2D baked = null;
                try
                {
                    var positions = settings.NeedsPositions ? PositionMap.Get(component, source) : null;
                    baked = RecolorCore.Bake(source, settings, false, positions);
                    var path = singlePath ?? Path.Combine(directory, fileName + ".png");
                    File.WriteAllBytes(path, baked.EncodeToPNG());
                    Debug.Log($"[SpoitableColorChanger] 書き出しました: {path}");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Spoitable Color Changer", $"{source.name} の書き出しに失敗しました: {e.Message}", "OK");
                }
                finally
                {
                    if (baked != null) Object.DestroyImmediate(baked);
                }
            }

            if (directory.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/')))
            {
                AssetDatabase.Refresh();
            }
        }

        public void Dispose()
        {
            _previews.Dispose();
            _presetSo?.Dispose();
            _presetSo = null;
            _so?.Dispose();
            _so = null;
        }
    }
}
