using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// プレビュー用の再配色結果を、(元テクスチャ, 設定) ごとに 1 枚だけ描いて共有する。
    /// 同じマテリアルを複数の Renderer が使っていても、描くのは 1 回で済む。参照カウントで解放する。
    /// </summary>
    internal static class SharedRenderCache
    {
        private readonly struct Key : IEquatable<Key>
        {
            private readonly Texture _source;
            private readonly Hash128 _contentHash;
            private readonly RecolorSettings _settings;
            private readonly Texture _positions;

            public Key(Texture source, RecolorSettings settings, Texture positions)
            {
                _source = source;
                // 元テクスチャが再インポートされたら別物として描き直す
                _contentHash = source is RenderTexture ? default : source.imageContentsHash;
                _settings = settings;
                // 位置マップは形や位置が変わると別の RenderTexture になる(PositionMap のキャッシュ)
                _positions = positions;
            }

            public bool Equals(Key other)
            {
                return ReferenceEquals(_source, other._source) && _contentHash == other._contentHash &&
                       _settings.Equals(other._settings) && ReferenceEquals(_positions, other._positions);
            }

            public override bool Equals(object obj)
            {
                return obj is Key other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_source != null ? _source.GetInstanceID() : 0, _contentHash, _settings);
            }
        }

        private sealed class Entry
        {
            public Key Key;
            public RenderTexture Target;
            public int RefCount;
        }

        private static readonly Dictionary<Key, Entry> ByKey = new Dictionary<Key, Entry>();
        private static readonly Dictionary<int, Entry> ByTarget = new Dictionary<int, Entry>();

        /// <param name="positions">モデル上のグラデーションマスクに使う位置マップ(要らなければ null)</param>
        public static RenderTexture Acquire(Texture source, RecolorSettings settings, Texture positions)
        {
            var key = new Key(source, settings, positions);
            if (ByKey.TryGetValue(key, out var entry) && entry.Target != null)
            {
                entry.RefCount++;
                return entry.Target;
            }

            var rt = RecolorCore.CreateTarget(source);
            try
            {
                RecolorCore.Render(source, settings, rt, positions);
            }
            catch
            {
                rt.Release();
                Object.DestroyImmediate(rt);
                throw;
            }

            entry = new Entry { Key = key, Target = rt, RefCount = 1 };
            ByKey[key] = entry;
            ByTarget[rt.GetInstanceID()] = entry;
            return rt;
        }

        public static void Release(RenderTexture target)
        {
            if (ReferenceEquals(target, null)) return;
            if (!ByTarget.TryGetValue(target.GetInstanceID(), out var entry)) return;
            if (--entry.RefCount > 0) return;

            ByTarget.Remove(target.GetInstanceID());
            if (ByKey.TryGetValue(entry.Key, out var current) && current == entry) ByKey.Remove(entry.Key);
            if (target != null)
            {
                target.Release();
                Object.DestroyImmediate(target);
            }
        }
    }
}
