using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    [CustomEditor(typeof(SpoitableColorChanger))]
    internal sealed class SpoitableColorChangerEditor : Editor
    {
        private ChangerGui _gui;

        private void OnEnable()
        {
            _gui = new ChangerGui();
        }

        private void OnDisable()
        {
            _gui?.Dispose();
            _gui = null;
        }

        public override void OnInspectorGUI()
        {
            _gui.Draw((SpoitableColorChanger)target);
        }
    }

    /// <summary>
    /// プリセットのアセットのインスペクタ。レイヤーの色と明るさのレンジを編集できる(マスクはアバターごとなので、
    /// コンポーネント側のエディタで扱う)。
    /// </summary>
    [CustomEditor(typeof(SpoitableColorPreset))]
    internal sealed class SpoitableColorPresetEditor : Editor
    {
        private PresetEditorGui _gui;
        private string _selected;

        private void OnEnable()
        {
            _gui = new PresetEditorGui
            {
                Selected = name => _selected = name,
                Renamed = (oldName, newName) =>
                {
                    if (_selected == oldName) _selected = newName;
                },
            };
        }

        private void OnDisable()
        {
            _gui?.Dispose();
            _gui = null;
        }

        public override void OnInspectorGUI()
        {
            var preset = (SpoitableColorPreset)target;
            GUILayout.Label("マスクはアバターごとに、Spoitable Color Changer コンポーネントのエディタで割り当てます。" +
                            "名前を変えたレイヤーのマスクは、コンポーネント側で付け替えが必要です。", SccGui.Hint);
            EditorGUILayout.Space();

            _gui.Begin(preset);
            try
            {
                if (_selected == null || _gui.IndexOf(_selected) < 0)
                {
                    _selected = preset.layers.Count > 0 && preset.layers[0] != null ? preset.layers[0].name : null;
                }

                SccGui.Section("レイヤー", note: "上から順に重ねる");
                GUILayout.Space(4f);
                _gui.DrawLayerList(_selected);

                if (_selected != null)
                {
                    SccGui.Section("色", note: _selected);
                    using (SccGui.Body())
                    {
                        _gui.DrawLayerProperties(_selected, null);
                    }
                }

                SccGui.Section("明るさ");
                using (SccGui.Body())
                {
                    _gui.DrawRange(null);
                }
            }
            finally
            {
                _gui.End();
            }
        }
    }
}
