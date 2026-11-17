using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 編集中の塗りマスク。PNG アセットごとに、メモリ上の作業用データ(1 画素 1 バイト)とテクスチャを持つ。
    /// プレビューとビルドは、保存前でもこの作業用テクスチャを使うので、見た目と結果がずれない。
    /// 取り消し履歴と選択範囲は MaskHistory と MaskSelection が全レイヤー共通で持つ。
    /// </summary>
    internal sealed class PaintSession
    {
        /// <summary>どこかの塗りマスクが変わるたびに増える。NDMF のプレビューが購読して描き直す。</summary>
        public static readonly PublishedValue<int> Version =
            new PublishedValue<int>(0, "SpoitableColorChanger/PaintVersion");

        private static readonly Dictionary<int, PaintSession> Sessions = new Dictionary<int, PaintSession>();
        private static int s_counter;

        public readonly Texture2D Asset;
        private readonly int _assetId;
        public readonly int Width;
        public readonly int Height;

        /// <summary>マスクの値。行 0 がテクスチャの下端(Texture2D と同じ並び)。</summary>
        public readonly byte[] Data;

        /// <summary>
        /// 編集していないときの Data と同じ中身の配列。履歴と共有するので書き換えない。
        /// 編集の前に複製しなくて済むよう、編集が確定するたびに MaskHistory が差し替える。
        /// </summary>
        public byte[] Snapshot { get; private set; }

        private byte[] _saved; // 最後に保存(または読み込み)したときの Snapshot
        private bool _modified; // Snapshot のあとに Data を書き換えた(ストロークの途中など)

        public Texture2D Working { get; private set; }
        public int ContentVersion { get; private set; }

        /// <summary>Snapshot(確定した中身)が変わるたびに変わる値。ストロークの途中では変わらない。</summary>
        public int CommitVersion { get; private set; }
        public bool IsClosed => Working == null;
        public bool Dirty => _modified || !ReferenceEquals(Snapshot, _saved);

        [InitializeOnLoadMethod]
        private static void Init()
        {
            // 保存し忘れを防ぐ。ドメインリロードで作業用データは消えるので、その前に書き出す
            AssemblyReloadEvents.beforeAssemblyReload += SaveAndCloseAll;
            EditorApplication.quitting += SaveAll;
        }

        private PaintSession(Texture2D asset)
        {
            Asset = asset;
            _assetId = asset.GetInstanceID();
            Width = asset.width;
            Height = asset.height;
            Data = ReadMask(asset);
            Snapshot = (byte[])Data.Clone();
            _saved = Snapshot;
            CommitVersion = ++s_counter;
            Working = new Texture2D(Width, Height, TextureFormat.R8, false, true)
            {
                name = asset.name + " (editing)",
                hideFlags = HideFlags.HideAndDontSave,
                wrapModeU = asset.wrapModeU,
                wrapModeV = asset.wrapModeV,
                filterMode = FilterMode.Bilinear,
            };
            Upload();
        }

        /// <summary>レンダリングに使うテクスチャ。編集中なら作業用、そうでなければアセットそのもの。</summary>
        public static Texture Resolve(Texture2D asset, out int version)
        {
            version = 0;
            if (asset == null) return null;
            if (Sessions.TryGetValue(asset.GetInstanceID(), out var session) && session.Working != null)
            {
                version = session.ContentVersion;
                return session.Working;
            }

            return asset;
        }

        private static PaintSession SessionOf(Texture working)
        {
            if (working == null) return null;
            foreach (var session in Sessions.Values)
            {
                if (session.Working != null && session.Working == working) return session;
            }

            return null;
        }

        /// <summary>
        /// レンダリングに使うテクスチャ(Resolve の結果)の、確定した中身の版。編集中のセッションならストロークや取り消しで
        /// 確定するたびに変わる値、そうでなければ -1(呼ぶ側はテクスチャの中身のハッシュで見る)。
        /// </summary>
        public static int CommittedVersion(Texture texture)
        {
            var session = SessionOf(texture);
            return session != null ? session.CommitVersion : -1;
        }

        /// <summary>
        /// レンダリングに使うテクスチャ(Resolve の結果)の確定した中身。行 0 が下端。編集中なら Snapshot
        /// (ストロークの途中の分は含まない。書き換えないこと)、そうでなければテクスチャから読む。
        /// </summary>
        public static byte[] ReadCommitted(Texture texture, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (texture == null) return null;
            var session = SessionOf(texture);
            if (session != null)
            {
                width = session.Width;
                height = session.Height;
                return session.Snapshot;
            }

            width = texture.width;
            height = texture.height;
            return ReadMask(texture);
        }

        public static PaintSession Get(Texture2D asset)
        {
            if (asset == null) return null;
            if (!Sessions.TryGetValue(asset.GetInstanceID(), out var session)) return null;

            // 外で大きさを変えて再インポートされたら、古い作業用データは使えない(上書きしてしまう)
            if (asset.width != session.Width || asset.height != session.Height)
            {
                Debug.LogWarning(
                    $"[SpoitableColorChanger] {asset.name} の大きさが変わったので、編集中の内容と取り消し履歴を破棄して読み直します。");
                session.Close();
                // 閉じたセッションの履歴は戻せない(取り消しが空振りする)ので、SaveAndCloseAll と同じく履歴ごと捨てる
                MaskHistory.Reset();
                return null;
            }

            return session;
        }

        public static PaintSession Open(Texture2D asset)
        {
            var session = Get(asset);
            if (session != null) return session;
            session = new PaintSession(asset);
            Sessions[asset.GetInstanceID()] = session;
            return session;
        }

        /// <summary>Data を書き換えたあとに呼ぶ。作業用テクスチャに反映し、プレビューに知らせる。</summary>
        public void Changed()
        {
            _modified = true;
            Upload();
            ContentVersion = ++s_counter;
            // 塗っている間は何度も呼ばれる。NDMF のプレビューは 1 回の描き直しに 15ms ほどかかるので間引く
            // (このウィンドウのキャンバスは Working をそのまま描くので、間引かずにすぐ変わる)
            PublishSoon();
        }

        /// <summary>編集の確定(MaskHistory から呼ぶ)。after は確定した中身の複製。変わっていなければ null。</summary>
        public void Commit(byte[] after)
        {
            _modified = false;
            if (after == null) return;
            Snapshot = after;
            CommitVersion = ++s_counter;
            // 確定した中身で作るもの(色の経路の塊の表)を作り直すため、プレビューに 1 回だけ描き直してもらう。
            // ストロークの途中で間引いた分もここで必ず届く
            ContentVersion = ++s_counter;
            Publish();
        }

        /// <summary>確定していない書き換えを捨てて、Snapshot の中身に戻す(MaskHistory から呼ぶ)。</summary>
        public void Discard()
        {
            if (!_modified) return;
            Buffer.BlockCopy(Snapshot, 0, Data, 0, Data.Length);
            _modified = false;
            Notify();
        }

        /// <summary>取り消し・やり直しで、snapshot の中身に戻す(MaskHistory から呼ぶ)。</summary>
        public void Restore(byte[] snapshot)
        {
            Buffer.BlockCopy(snapshot, 0, Data, 0, Data.Length);
            Snapshot = snapshot;
            CommitVersion = ++s_counter;
            _modified = false;
            Notify();
        }

        private void Notify()
        {
            Upload();
            ContentVersion = ++s_counter;
            Publish();
        }

        /// <summary>塗っている間に、プレビューへ知らせる間隔(秒)。</summary>
        private const double PreviewInterval = 0.1;

        private static double s_lastPublished = double.NegativeInfinity;
        private static bool s_publishPending;

        /// <summary>今の版をすぐプレビューに知らせる(確定・取り消し・閉じたとき)。間引いて待っている分もこれで済む。</summary>
        private static void Publish()
        {
            if (s_publishPending)
            {
                s_publishPending = false;
                EditorApplication.update -= PublishPending;
            }

            s_lastPublished = EditorApplication.timeSinceStartup;
            Version.Value = s_counter;
        }

        /// <summary>
        /// 塗っている途中の変更を知らせる。前に知らせてから間隔があいていればすぐ、そうでなければ間隔のあとに 1 回だけ。
        /// </summary>
        private static void PublishSoon()
        {
            if (EditorApplication.timeSinceStartup - s_lastPublished >= PreviewInterval)
            {
                Publish();
                return;
            }

            if (s_publishPending) return;
            s_publishPending = true;
            EditorApplication.update += PublishPending;
        }

        private static void PublishPending()
        {
            if (EditorApplication.timeSinceStartup - s_lastPublished < PreviewInterval) return;
            Publish();
        }

        private void Upload()
        {
            if (Working == null) return;
            Working.LoadRawTextureData(Data);
            Working.Apply(false, false);
        }

        /// <summary>PNG に書き出してアセットを更新する。</summary>
        public void Save()
        {
            if (Asset == null) return;
            var path = AssetDatabase.GetAssetPath(Asset);
            if (string.IsNullOrEmpty(path)) return;

            MaskAsset.WritePng(path, Data, Width, Height);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            // ストロークの途中で保存したときは、保存した中身がどの Snapshot とも違うので、終わったあとも未保存の扱い
            _saved = _modified ? null : Snapshot;
        }

        public void Close()
        {
            if (Working != null) Object.DestroyImmediate(Working);
            Working = null;
            // アセットが削除されていても取り除けるよう、開いたときの ID で消す
            if (Sessions.TryGetValue(_assetId, out var registered) && registered == this) Sessions.Remove(_assetId);
            ContentVersion = ++s_counter;
            Publish();
        }

        public static void SaveAll()
        {
            // 元の PNG が削除されたセッションは、保存先がないので閉じる
            foreach (var orphan in new List<PaintSession>(Sessions.Values))
            {
                if (orphan.Asset == null) orphan.Close();
            }

            foreach (var session in Sessions.Values)
            {
                if (!session.Dirty) continue;
                try
                {
                    session.Save();
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public static void SaveAndCloseAll()
        {
            SaveAll();
            foreach (var session in new List<PaintSession>(Sessions.Values)) session.Close();
            // 閉じたセッションには戻せないので、履歴と Unity の Undo に積んだ印も消す
            MaskHistory.Reset();
        }

        public static bool AnyDirty()
        {
            foreach (var session in Sessions.Values)
            {
                if (session.Dirty) return true;
            }

            return false;
        }

        /// <summary>マスクのテクスチャを GPU で読み出す(読み取り不可のアセットでも読める)。</summary>
        private static byte[] ReadMask(Texture texture)
        {
            var readWrite = RecolorCore.IsSrgb(texture) ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, readWrite);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = previous;
                var readback = RecolorCore.ReadBack(rt, TextureFormat.RGBA32, false, readWrite == RenderTextureReadWrite.Linear);
                var pixels = readback.GetPixels32();
                Object.DestroyImmediate(readback);

                var data = new byte[pixels.Length];
                for (var i = 0; i < pixels.Length; i++) data[i] = pixels[i].r;
                return data;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }

    /// <summary>塗りマスクの PNG アセットを作る・書き出す。</summary>
    internal static class MaskAsset
    {
        public static void WritePng(string assetPath, byte[] data, int width, int height)
        {
            var temp = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color32[data.Length];
                for (var i = 0; i < data.Length; i++)
                {
                    var v = data[i];
                    pixels[i] = new Color32(v, v, v, 255);
                }

                temp.SetPixels32(pixels);
                temp.Apply(false, false);
                File.WriteAllBytes(FullPath(assetPath), temp.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }
        }

        /// <summary>真っ黒(どこも塗らない)のマスクを作り、マスク用の取り込み設定にする。</summary>
        public static Texture2D CreateBlank(string assetPath, int width, int height, TextureWrapMode wrapMode)
        {
            WritePng(assetPath, new byte[width * height], width, height);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.textureType = TextureImporterType.SingleChannel;
                settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
                settings.sRGBTexture = false;
                settings.mipmapEnabled = false;
                settings.wrapMode = wrapMode;
                settings.filterMode = FilterMode.Bilinear;
                importer.SetTextureSettings(settings);
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(Mathf.Max(width, height)));
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private static string FullPath(string assetPath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
            return Path.Combine(projectRoot, assetPath);
        }
    }
}
