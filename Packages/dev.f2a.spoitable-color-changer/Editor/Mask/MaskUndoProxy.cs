using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 塗りマスクと選択範囲の編集を Unity の Undo に載せるための印。番号だけを持つ。
    /// 中身(マスクの画素)は MaskHistory が持ち、Undo で番号が戻ったらその番号の状態に戻す。
    /// </summary>
    internal sealed class MaskUndoProxy : ScriptableObject
    {
        public int version;
    }
}
