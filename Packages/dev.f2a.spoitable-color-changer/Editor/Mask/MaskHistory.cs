using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 塗りマスクと選択範囲の取り消し履歴(全レイヤー共通の 1 本)。
    /// 画素はここで持ち、Unity の Undo には番号だけの印(MaskUndoProxy)を積む。Ctrl+Z などで Unity が
    /// 印の番号を戻したら、その番号の状態にマスクと選択範囲を戻す。こうすると、色の変更などほかの操作と
    /// 同じ 1 本の履歴で、操作した順に取り消せる。
    /// </summary>
    internal static class MaskHistory
    {
        /// <summary>履歴に持つ画素の合計の上限。超えたら古いものから捨てる(その先は取り消せない)。</summary>
        private const long MaxBytes = 256L * 1024 * 1024;

        private sealed class Record
        {
            /// <summary>この編集のあとの番号と、前の番号。</summary>
            public int Version;
            public int Previous;

            /// <summary>マスクを変えたセッション。選択範囲だけの編集なら null。</summary>
            public PaintSession Session;
            public byte[] DataBefore;
            public byte[] DataAfter;
            public SelectionState SelectionBefore;
            public SelectionState SelectionAfter;
            public long Bytes;
        }

        private static readonly List<Record> Records = new List<Record>();
        private static int s_position; // 適用済みの記録の数(Records[0..s_position) が今の状態)
        private static int s_counter;
        private static int s_applied; // 今の状態の番号
        private static MaskUndoProxy s_proxy;
        private static bool s_warnedTrimmed;

        private static bool s_editing;
        private static PaintSession s_editSession;
        private static SelectionState s_editSelection;

        /// <summary>
        /// Undo / Redo でマスクか選択範囲を戻したとき。引数は最後に戻したマスクのセッション
        /// (選択範囲だけなら null)。エディタはそのレイヤーに表示を切り替える。
        /// </summary>
        public static event Action<PaintSession> Restored;

        /// <summary>ドラッグの途中で Undo / Redo されて、途中の編集を取り下げたとき。</summary>
        public static event Action EditAborted;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public static bool IsEditing(PaintSession session)
        {
            return s_editing && s_editSession == session;
        }

        /// <summary>
        /// 編集の直前に呼ぶ。マスクを書き換えるなら session を、選択範囲だけなら session は null でよい。
        /// 終わったら必ず End か Cancel を呼ぶ。
        /// </summary>
        public static void Begin(PaintSession session)
        {
            if (s_editing) Cancel();
            s_editing = true;
            s_editSession = session;
            s_editSelection = MaskSelection.Current;
        }

        /// <summary>Begin からの変更を取り下げて、Begin の前の状態に戻す。</summary>
        public static void Cancel()
        {
            if (!s_editing) return;
            s_editing = false;
            if (s_editSession != null && !s_editSession.IsClosed) s_editSession.Discard();
            if (!ReferenceEquals(MaskSelection.Current.Data, s_editSelection.Data)) MaskSelection.Set(s_editSelection);
            s_editSession = null;
            s_editSelection = default;
        }

        /// <summary>
        /// 編集の終わりに呼ぶ。マスクも選択範囲も変わっていなければ何も積まずに false を返す。
        /// 変わっていれば履歴に積み、Unity の Undo に name で印を積む。
        /// </summary>
        public static bool End(string name)
        {
            if (!s_editing) return false;
            s_editing = false;
            var session = s_editSession;
            var selectionBefore = s_editSelection;
            s_editSession = null;
            s_editSelection = default;

            byte[] before = null, after = null;
            if (session != null && !session.IsClosed)
            {
                if (!MaskOps.SameBytes(session.Snapshot, session.Data))
                {
                    before = session.Snapshot;
                    after = (byte[])session.Data.Clone();
                }

                session.Commit(after);
            }

            var selectionAfter = MaskSelection.Current;
            var selectionChanged = !SelectionState.Same(selectionBefore, selectionAfter);
            if (after == null && !selectionChanged) return false;
            // 中身が同じなら前の配列を使う(履歴の配列を増やさない)
            if (!selectionChanged) selectionAfter = selectionBefore;

            // 印は番号を進める前に用意する(初めて作るとき、編集前の番号で作るため)
            var proxy = Proxy();

            // Unity は新しい操作で redo を捨てるので、こちらも今より先の記録を捨てる
            if (s_position < Records.Count) Records.RemoveRange(s_position, Records.Count - s_position);
            var record = new Record
            {
                Previous = s_applied,
                Version = ++s_counter,
                Session = after != null ? session : null,
                DataBefore = before,
                DataAfter = after,
                SelectionBefore = selectionBefore,
                SelectionAfter = selectionAfter,
                Bytes = (after?.Length ?? 0) + (selectionChanged ? selectionAfter.Data?.Length ?? 0 : 0),
            };
            Records.Add(record);
            s_position = Records.Count;
            s_applied = record.Version;

            Undo.IncrementCurrentGroup();
            // RecordObject の記録は ClearUndo で消せないので、消せる RegisterCompleteObjectUndo を使う(2022.3 で実測)
            Undo.RegisterCompleteObjectUndo(proxy, name);
            proxy.version = record.Version;
            Undo.IncrementCurrentGroup();

            Trim();
            return true;
        }

        private static MaskUndoProxy Proxy()
        {
            if (s_proxy == null)
            {
                s_proxy = ScriptableObject.CreateInstance<MaskUndoProxy>();
                s_proxy.hideFlags = HideFlags.HideAndDontSave;
                s_proxy.version = s_applied;
            }

            return s_proxy;
        }

        private static void Trim()
        {
            long total = 0;
            foreach (var r in Records) total += r.Bytes;
            // 最新の 1 件は必ず残す
            while (total > MaxBytes && Records.Count > 1 && s_position > 1)
            {
                total -= Records[0].Bytes;
                Records.RemoveAt(0);
                s_position--;
            }
        }

        private static void OnUndoRedo()
        {
            if (s_proxy == null) return;
            var target = s_proxy.version;
            if (target == s_applied) return;

            if (s_editing)
            {
                Cancel();
                EditAborted?.Invoke();
            }

            var position = FindPosition(target);
            if (position < 0)
            {
                // 番号が見つからないのは、上限を超えて捨てた古い履歴まで戻ろうとしたとき。戻せるところまで戻す
                if (!s_warnedTrimmed)
                {
                    Debug.Log("[SpoitableColorChanger] 塗りマスクの古い履歴は上限を超えたため、ここより前には戻せません。");
                    s_warnedTrimmed = true;
                }

                position = 0;
            }

            PaintSession last = null;
            while (s_position > position)
            {
                var r = Records[--s_position];
                Apply(r, r.DataBefore, r.SelectionBefore);
                if (r.Session != null) last = r.Session;
            }

            while (s_position < position)
            {
                var r = Records[s_position++];
                Apply(r, r.DataAfter, r.SelectionAfter);
                if (r.Session != null) last = r.Session;
            }

            s_applied = target;
            Restored?.Invoke(last);
        }

        /// <summary>番号 version の状態になる、適用済みの記録の数。見つからなければ -1。</summary>
        private static int FindPosition(int version)
        {
            for (var i = Records.Count; i >= 1; i--)
            {
                if (Records[i - 1].Version == version) return i;
            }

            if (Records.Count > 0 && Records[0].Previous == version) return 0;
            return -1;
        }

        private static void Apply(Record record, byte[] data, SelectionState selection)
        {
            if (data != null && record.Session != null && !record.Session.IsClosed) record.Session.Restore(data);
            if (!ReferenceEquals(MaskSelection.Current.Data, selection.Data)) MaskSelection.Set(selection);
        }

        /// <summary>履歴を捨て、Unity の Undo に積んだ印も消す。エディタを閉じたときとスクリプトの再読み込みの前に呼ぶ。</summary>
        public static void Reset()
        {
            s_editing = false;
            s_editSession = null;
            s_editSelection = default;
            Records.Clear();
            s_position = 0;
            s_applied = 0;
            s_warnedTrimmed = false;
            if (s_proxy != null)
            {
                Undo.ClearUndo(s_proxy);
                Object.DestroyImmediate(s_proxy);
                s_proxy = null;
            }

            MaskSelection.Clear();
        }
    }
}
