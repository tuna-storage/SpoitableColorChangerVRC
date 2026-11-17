using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>エディタとインスペクタで共通に使う見た目(色・スタイル・節の見出し)。</summary>
    internal static class SccGui
    {
        private static GUIStyle s_sectionLabel;
        private static GUIStyle s_toolButton;
        private static GUIStyle s_statusBar;
        private static GUIStyle s_hint;
        private static GUIStyle s_emptyTitle;
        private static GUIStyle s_emptyBody;
        private static GUIStyle s_rowLabel;
        private static GUIStyle s_badge;

        public static bool Pro => EditorGUIUtility.isProSkin;

        /// <summary>パネルの地の色より少し沈めた色(見出しの帯・キャンバスの外)。</summary>
        public static Color HeaderColor => Pro ? new Color(0.17f, 0.17f, 0.17f) : new Color(0.72f, 0.72f, 0.72f);
        public static Color SeparatorColor => Pro ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);
        public static Color CanvasColor => Pro ? new Color(0.13f, 0.13f, 0.13f) : new Color(0.45f, 0.45f, 0.45f);
        public static Color SelectedRowColor => Pro ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.23f, 0.45f, 0.69f, 0.55f);
        public static Color HoverRowColor => Pro ? new Color(1f, 1f, 1f, 0.05f) : new Color(0f, 0f, 0f, 0.05f);
        public static Color IconColor => Pro ? new Color(0.82f, 0.82f, 0.82f) : new Color(0.2f, 0.2f, 0.2f);
        public static Color IconSelectedColor => Pro ? Color.white : Color.black;
        public static Color AccentColor => Pro ? new Color(0.35f, 0.7f, 1f) : new Color(0.1f, 0.4f, 0.8f);
        public static Color WarningColor => Pro ? new Color(1f, 0.75f, 0.3f) : new Color(0.65f, 0.4f, 0f);

        public static GUIStyle SectionLabel => s_sectionLabel ??= new GUIStyle(EditorStyles.boldLabel)
        {
            padding = new RectOffset(6, 4, 0, 0),
            alignment = TextAnchor.MiddleLeft,
        };

        public static GUIStyle ToolButton => s_toolButton ??= new GUIStyle(GUI.skin.button)
        {
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };

        private static GUIStyle s_toolbarLabel;
        private static GUIStyle s_toolbarBoldLabel;

        /// <summary>ツールバーの中で、ボタンと縦位置のそろうラベル。</summary>
        public static GUIStyle ToolbarLabel => s_toolbarLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = EditorStyles.toolbar.fixedHeight,
            margin = new RectOffset(2, 2, 0, 0),
            padding = new RectOffset(2, 2, 0, 0),
        };

        public static GUIStyle ToolbarBoldLabel => s_toolbarBoldLabel ??= new GUIStyle(ToolbarLabel)
        {
            fontStyle = FontStyle.Bold,
        };

        public static GUIStyle StatusBar => s_statusBar ??= new GUIStyle(EditorStyles.toolbar)
        {
            padding = new RectOffset(6, 4, 0, 0),
        };

        public static GUIStyle Hint => s_hint ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = Pro ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.35f, 0.35f, 0.35f) },
            wordWrap = true,
        };

        public static GUIStyle EmptyTitle => s_emptyTitle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };

        public static GUIStyle EmptyBody => s_emptyBody ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };

        public static GUIStyle RowLabel => s_rowLabel ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        };

        public static GUIStyle Badge => s_badge ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = Pro ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.3f, 0.3f, 0.3f) },
        };

        /// <summary>節の見出し(帯)。open を渡すと折りたためる。</summary>
        public static bool Section(string title, bool open = true, bool foldable = false, string note = null)
        {
            var rect = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, HeaderColor);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), SeparatorColor);
            }

            if (foldable)
            {
                var foldRect = new Rect(rect.x + 4f, rect.y, rect.width - 4f, rect.height);
                open = EditorGUI.Foldout(foldRect, open, title, true, SectionFoldout);
            }
            else
            {
                GUI.Label(rect, title, SectionLabel);
            }

            if (!string.IsNullOrEmpty(note))
            {
                GUI.Label(new Rect(rect.x, rect.y, rect.width - 6f, rect.height), note, Badge);
            }

            return open;
        }

        private static GUIStyle s_sectionFoldout;

        private static GUIStyle SectionFoldout => s_sectionFoldout ??= new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold,
        };

        /// <summary>縦の区切り線(ツールバーの中)。</summary>
        public static void VerticalSeparator()
        {
            var rect = GUILayoutUtility.GetRect(9f, 18f, GUILayout.Width(9f));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.x + 4f, rect.y + 2f, 1f, rect.height - 4f), SeparatorColor);
            }
        }

        /// <summary>中身の左右の余白。</summary>
        public static EditorGUILayout.VerticalScope Body()
        {
            return new EditorGUILayout.VerticalScope(BodyStyle);
        }

        private static GUIStyle s_body;

        private static GUIStyle BodyStyle => s_body ??= new GUIStyle
        {
            padding = new RectOffset(8, 8, 6, 8),
        };

        /// <summary>
        /// 配列の index 番目の要素(範囲外なら null)。GetArrayElementAtIndex の代わりに使う。
        /// Unity 2022.3 では、同じ SerializedObject で GetArrayElementAtIndex を呼ぶほど 1 回が遅くなり
        /// (3 万回で 3µs → 75µs)、Update() でも戻らない。パスで探すほうは遅くならない。
        /// </summary>
        public static SerializedProperty ArrayElement(this SerializedProperty array, int index)
        {
            return array.FindPropertyRelative("Array.data[" + index + "]");
        }

        /// <summary>
        /// 長く使う SerializedObject を作り直すまでの回数(OnGUI の呼び出し)。ReorderableList などが内部で呼ぶ
        /// GetArrayElementAtIndex の分だけ少しずつ遅くなるので、ときどき作り直す。
        /// </summary>
        public const int SerializedObjectRenewInterval = 2000;

        /// <summary>SerializedObject を作り直してよいか(ドラッグや文字の入力の途中でない)。</summary>
        public static bool CanRenewSerializedObject => GUIUtility.hotControl == 0 && !EditorGUIUtility.editingTextField;
    }
}
