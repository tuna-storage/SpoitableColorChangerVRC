using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>レイヤーのグラデーションを表示するテクスチャ。ストップが変わったときだけ作り直す。</summary>
    internal sealed class GradientPreviews : IDisposable
    {
        private readonly Dictionary<object, (int hash, Texture2D texture)> _cache =
            new Dictionary<object, (int, Texture2D)>();

        /// <param name="key">レイヤーを見分けるもの(ColorLayer の参照など)</param>
        public Texture2D Get(object key, IReadOnlyList<ColorStop> stops)
        {
            var list = new List<(float position, Color color)>(stops.Count);
            foreach (var s in stops) list.Add((s.position, s.color));
            return Get(key, list);
        }

        public Texture2D Get(object key, List<(float position, Color color)> stops)
        {
            if (stops.Count == 0) return null;
            var hash = 17;
            foreach (var (p, c) in stops) hash = HashCode.Combine(hash, p, c);

            if (_cache.TryGetValue(key, out var cached) && cached.texture != null && cached.hash == hash)
            {
                return cached.texture;
            }

            var texture = cached.texture;
            if (texture == null)
            {
                texture = new Texture2D(GradientLut.Width, 1, TextureFormat.RGBA32, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            GradientLut.FillDisplayTexture(texture, stops);
            _cache[key] = (hash, texture);
            return texture;
        }

        public void Clear()
        {
            foreach (var kv in _cache)
            {
                if (kv.Value.texture != null) Object.DestroyImmediate(kv.Value.texture);
            }

            _cache.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
