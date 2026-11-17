using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// コンポーネント側の、レイヤー名に対するマスクの割り当て(MaskBinding)の読み書きと、プリセットの作成。
    /// エディタのウィンドウとインスペクタで共有する。変更は SerializedObject を通す(Undo とプレビューのため)。
    /// </summary>
    internal static class MaskBindingGui
    {
        /// <summary>レイヤーの一覧に出す、マスクの種類の一言(塗り・画像・グラデ)。</summary>
        public static string Badge(MaskBinding binding)
        {
            if (binding == null) return "";
            var parts = new List<string>();
            if (binding.paintMask != null) parts.Add("塗り");
            if (binding.texture != null) parts.Add("画像");
            if (!GradientMaskConfig.Capture(binding.gradient).IsNone) parts.Add("グラデ");
            if (ColorPathConfig.Capture(binding.colorPath).IsActive) parts.Add("経路");
            return string.Join("+", parts);
        }

        public static int FindIndex(SerializedObject component, string layerName)
        {
            var masks = component.FindProperty(nameof(SpoitableColorChanger.masks));
            for (var i = 0; i < masks.arraySize; i++)
            {
                if (masks.ArrayElement(i).FindPropertyRelative(nameof(MaskBinding.layerName)).stringValue ==
                    layerName)
                {
                    return i;
                }
            }

            return -1;
        }

        public static SerializedProperty Find(SerializedObject component, string layerName)
        {
            var index = FindIndex(component, layerName);
            return index >= 0
                ? component.FindProperty(nameof(SpoitableColorChanger.masks)).ArrayElement(index)
                : null;
        }

        public static SerializedProperty Ensure(SerializedObject component, string layerName)
        {
            var element = Find(component, layerName);
            if (element != null) return element;
            var masks = component.FindProperty(nameof(SpoitableColorChanger.masks));
            // 配列を伸ばすと末尾の要素が複製されるので、すべての項目を初期値に戻す
            masks.arraySize++;
            element = masks.ArrayElement(masks.arraySize - 1);
            element.FindPropertyRelative(nameof(MaskBinding.layerName)).stringValue = layerName;
            element.FindPropertyRelative(nameof(MaskBinding.texture)).objectReferenceValue = null;
            element.FindPropertyRelative(nameof(MaskBinding.channel)).enumValueIndex = (int)MaskChannel.Gray;
            element.FindPropertyRelative(nameof(MaskBinding.invert)).boolValue = false;
            element.FindPropertyRelative(nameof(MaskBinding.paintMask)).objectReferenceValue = null;
            ResetGradient(element.FindPropertyRelative(nameof(MaskBinding.gradient)));
            ResetColorPath(element.FindPropertyRelative(nameof(MaskBinding.colorPath)));
            return element;
        }

        /// <summary>色の経路を初期値(明るさで色を決める)に戻す。</summary>
        public static void ResetColorPath(SerializedProperty path)
        {
            var defaults = new ColorPath();
            path.FindPropertyRelative(nameof(ColorPath.mode)).intValue = (int)defaults.mode;
            path.FindPropertyRelative(nameof(ColorPath.space)).intValue = (int)defaults.space;
            path.FindPropertyRelative(nameof(ColorPath.lines)).arraySize = 0;
            // 線が 1 本だったころの点(読み込むと lines に移すが、ここで新しく作った要素に残っていることがある)
            var legacy = path.FindPropertyRelative("points");
            if (legacy != null) legacy.arraySize = 0;
            path.FindPropertyRelative(nameof(ColorPath.uvHintTexture)).objectReferenceValue = null;
            path.FindPropertyRelative(nameof(ColorPath.shading)).floatValue = defaults.shading;
        }

        /// <summary>グラデーションマスクを初期値(なし)に戻す。</summary>
        public static void ResetGradient(SerializedProperty gradient)
        {
            var defaults = new GradientMask();
            gradient.FindPropertyRelative(nameof(GradientMask.mode)).intValue = (int)defaults.mode;
            gradient.FindPropertyRelative(nameof(GradientMask.start)).vector3Value = defaults.start;
            gradient.FindPropertyRelative(nameof(GradientMask.end)).vector3Value = defaults.end;
            gradient.FindPropertyRelative(nameof(GradientMask.startUv)).vector2Value = defaults.startUv;
            gradient.FindPropertyRelative(nameof(GradientMask.endUv)).vector2Value = defaults.endUv;
            gradient.FindPropertyRelative(nameof(GradientMask.uvHintTexture)).objectReferenceValue = null;
            gradient.FindPropertyRelative(nameof(GradientMask.points)).arraySize = 0;
            gradient.FindPropertyRelative(nameof(GradientMask.sharpness)).floatValue = defaults.sharpness;
            gradient.FindPropertyRelative(nameof(GradientMask.smooth)).boolValue = defaults.smooth;
            gradient.FindPropertyRelative(nameof(GradientMask.invert)).boolValue = defaults.invert;
        }

        /// <summary>レイヤー名の変更に合わせて、割り当ての名前も変える。</summary>
        public static void Rename(SerializedObject component, string oldName, string newName)
        {
            if (FindIndex(component, newName) >= 0) return;
            var element = Find(component, oldName);
            if (element == null) return;
            element.FindPropertyRelative(nameof(MaskBinding.layerName)).stringValue = newName;
        }

        /// <summary>画像マスクの欄(テクスチャ・チャンネル・反転)。</summary>
        public static void DrawImageMask(SerializedObject component, string layerName)
        {
            var element = Find(component, layerName);
            var texture = element?.FindPropertyRelative(nameof(MaskBinding.texture)).objectReferenceValue as Texture2D;

            EditorGUI.BeginChangeCheck();
            var newTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("画像マスク", "外部で作った白黒画像や RGBA のチャンネル。塗りマスクと掛け合わせる。未指定なら全体"),
                texture, typeof(Texture2D), false, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (EditorGUI.EndChangeCheck())
            {
                element = Ensure(component, layerName);
                element.FindPropertyRelative(nameof(MaskBinding.texture)).objectReferenceValue = newTexture;
                component.ApplyModifiedProperties();
                GUIUtility.ExitGUI(); // チャンネル欄の表示が変わるので、このフレームはここまで
            }

            if (element == null || texture == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(MaskBinding.channel)), GUIContent.none,
                    GUILayout.MinWidth(60f));
                var invert = element.FindPropertyRelative(nameof(MaskBinding.invert));
                invert.boolValue = EditorGUILayout.ToggleLeft("反転", invert.boolValue, GUILayout.Width(50f));
            }
        }

        /// <summary>
        /// プリセットにないレイヤー名に割り当てたマスク(名前の変更や削除で取り残されたもの)。あれば警告と、
        /// 付け替え・削除のボタンを出す。
        /// </summary>
        public static void DrawOrphans(SerializedObject component, SpoitableColorPreset preset)
        {
            if (preset == null) return;
            var names = new HashSet<string>();
            foreach (var layer in preset.layers)
            {
                if (layer != null) names.Add(layer.name ?? "");
            }

            var masks = component.FindProperty(nameof(SpoitableColorChanger.masks));
            var orphans = new List<int>();
            for (var i = 0; i < masks.arraySize; i++)
            {
                var name = masks.ArrayElement(i).FindPropertyRelative(nameof(MaskBinding.layerName)).stringValue;
                if (!names.Contains(name)) orphans.Add(i);
            }

            if (orphans.Count == 0) return;

            EditorGUILayout.HelpBox("プリセットにないレイヤー名に割り当てたマスクがあります(レイヤー名の変更や削除で残ったもの)。必要なら付け替えてください。",
                MessageType.Warning);
            foreach (var i in orphans)
            {
                var element = masks.ArrayElement(i);
                var name = element.FindPropertyRelative(nameof(MaskBinding.layerName)).stringValue;
                var tex = element.FindPropertyRelative(nameof(MaskBinding.texture)).objectReferenceValue;
                var paint = element.FindPropertyRelative(nameof(MaskBinding.paintMask)).objectReferenceValue;
                var label = string.Join(" / ", new[] { paint, tex }.Where(t => t != null).Select(t => t.name));
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent($"「{name}」: {(label.Length > 0 ? label : "(なし)")}"),
                        EditorStyles.miniLabel, GUILayout.MinWidth(40f));
                    if (GUILayout.Button("付け替え", EditorStyles.miniButtonLeft, GUILayout.Width(56f)))
                    {
                        ShowReassignMenu(component, i, names);
                    }

                    if (GUILayout.Button("削除", EditorStyles.miniButtonRight, GUILayout.Width(38f)))
                    {
                        masks.DeleteArrayElementAtIndex(i);
                        component.ApplyModifiedProperties();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private static void ShowReassignMenu(SerializedObject component, int maskIndex, HashSet<string> layerNames)
        {
            var owner = component.targetObject;
            var menu = new GenericMenu();
            foreach (var name in layerNames)
            {
                if (FindIndex(component, name) >= 0)
                {
                    menu.AddDisabledItem(new GUIContent(name + "(割り当て済み)"));
                    continue;
                }

                var captured = name;
                menu.AddItem(new GUIContent(name), false, () =>
                {
                    if (owner == null) return;
                    using (var so = new SerializedObject(owner))
                    {
                        var masks = so.FindProperty(nameof(SpoitableColorChanger.masks));
                        if (maskIndex >= masks.arraySize) return;
                        masks.ArrayElement(maskIndex).FindPropertyRelative(nameof(MaskBinding.layerName))
                            .stringValue = captured;
                        so.ApplyModifiedProperties();
                    }
                });
            }

            if (layerNames.Count == 0) menu.AddDisabledItem(new GUIContent("レイヤーがありません"));
            menu.ShowAsContext();
        }

        /// <summary>プリセットを新しく作るか、source を複製して保存する。キャンセルなら null。</summary>
        public static SpoitableColorPreset CreatePreset(SpoitableColorChanger component, SpoitableColorPreset source)
        {
            var defaultName = source != null ? source.name + "_copy" : component.gameObject.name + "_ColorPreset";
            var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            var directory = string.IsNullOrEmpty(sourcePath)
                ? "Assets"
                : (Path.GetDirectoryName(sourcePath) ?? "Assets").Replace('\\', '/');
            var path = EditorUtility.SaveFilePanelInProject("プリセットを保存", defaultName, "asset",
                "プリセットの保存先を選んでください", directory);
            if (string.IsNullOrEmpty(path)) return null;

            // 複製元そのものに上書きすると、複製元が消えてしまう
            if (source != null && path == sourcePath) return source;

            SpoitableColorPreset preset;
            if (source != null)
            {
                preset = Object.Instantiate(source);
            }
            else
            {
                preset = ScriptableObject.CreateInstance<SpoitableColorPreset>();
                preset.layers.Add(SpoitableColorPreset.CreateDefaultLayer("ベース"));
            }

            AssetDatabase.CreateAsset(preset, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<SpoitableColorPreset>(path);
        }

        /// <summary>プリセットの欄(指定・新規・複製)。変更したら true(このフレームの描画は打ち切ってよい)。</summary>
        public static bool DrawPresetField(SerializedObject component)
        {
            var presetProp = component.FindProperty(nameof(SpoitableColorChanger.preset));
            var target = (SpoitableColorChanger)component.targetObject;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(presetProp, new GUIContent("プリセット", "色の設定(レイヤーとグラデーション)。ほかのアバターと使い回せる"));
                if (EditorGUI.EndChangeCheck())
                {
                    component.ApplyModifiedProperties();
                    return true;
                }

                if (GUILayout.Button(new GUIContent("新規", "新しいプリセットを作って割り当てる"), EditorStyles.miniButtonLeft,
                        GUILayout.Width(38f)))
                {
                    var created = CreatePreset(target, null);
                    if (created != null) presetProp.objectReferenceValue = created;
                    component.ApplyModifiedProperties();
                    return true; // 保存ダイアログを挟んだので、このフレームのレイアウトは打ち切る
                }

                using (new EditorGUI.DisabledScope(presetProp.objectReferenceValue == null))
                {
                    if (GUILayout.Button(new GUIContent("複製", "今のプリセットを別名で保存して割り当てる"), EditorStyles.miniButtonRight,
                            GUILayout.Width(38f)))
                    {
                        var created = CreatePreset(target, (SpoitableColorPreset)presetProp.objectReferenceValue);
                        if (created != null) presetProp.objectReferenceValue = created;
                        component.ApplyModifiedProperties();
                        return true;
                    }
                }
            }

            var preset = presetProp.objectReferenceValue as SpoitableColorPreset;
            if (preset != null && AssetDatabase.Contains(preset))
            {
                EditorGUILayout.LabelField(" ", "変更は、同じプリセットを使うほかのアバターにも反映されます", SccGui.Hint);
            }

            return false;
        }
    }
}
