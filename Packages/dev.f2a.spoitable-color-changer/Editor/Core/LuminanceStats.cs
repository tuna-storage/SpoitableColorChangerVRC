using System.Collections.Generic;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// 元テクスチャの明るさ(OKLab の L)の分布。自動レンジとインスペクタのヒストグラムに使う。
    /// 透明な部分(アルファ 0.5 未満)は髪の色ではないので除く。
    /// </summary>
    internal sealed class LuminanceStats
    {
        public const int DisplayBins = 64;
        private const int SampleSize = 256;
        private const int FineBins = 1024;
        private const float LowPercentile = 0.005f;
        private const float HighPercentile = 0.995f;
        private const float MinSpan = 0.02f;

        public float Low { get; private set; }
        public float High { get; private set; }

        /// <summary>0〜1 を DisplayBins 等分した度数。最大値で 1 に正規化している。</summary>
        public float[] Histogram { get; private set; }

        public int SampleCount { get; private set; }

        private struct CacheEntry
        {
            public Hash128 Hash;
            public uint UpdateCount;
            public LuminanceStats Stats;
        }

        private static readonly Dictionary<int, CacheEntry> Cache = new Dictionary<int, CacheEntry>();

        public static LuminanceStats Get(Texture texture)
        {
            if (texture == null) return null;

            // RenderTexture(重ねたコンポーネントの途中結果)は中身が変わっても検知できないので、毎回求める
            if (texture is RenderTexture) return Compute(texture);

            var id = texture.GetInstanceID();
            var hash = texture.imageContentsHash;
            var updateCount = texture.updateCount;
            if (Cache.TryGetValue(id, out var entry) && entry.Hash == hash && entry.UpdateCount == updateCount)
            {
                return entry.Stats;
            }

            var stats = Compute(texture);
            Cache[id] = new CacheEntry { Hash = hash, UpdateCount = updateCount, Stats = stats };
            return stats;
        }

        private static LuminanceStats Compute(Texture texture)
        {
            var scale = Mathf.Min(1f, (float)SampleSize / Mathf.Max(texture.width, texture.height));
            var w = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var h = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Color[] pixels;
            try
            {
                RecolorCore.BlitStats(texture, rt);
                var readback = RecolorCore.ReadBack(rt, TextureFormat.RGBAFloat, false, true);
                pixels = readback.GetPixels();
                Object.DestroyImmediate(readback);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }

            var fine = new int[FineBins];
            var opaque = 0;
            foreach (var p in pixels)
            {
                if (p.g >= 0.5f) opaque++;
            }

            // ほぼ全部が透明なら、アルファを無視して全体から求める
            var useAlpha = opaque >= 64;
            var count = 0;
            foreach (var p in pixels)
            {
                if (useAlpha && p.g < 0.5f) continue;
                var bin = Mathf.Clamp((int)(Mathf.Clamp01(p.r) * FineBins), 0, FineBins - 1);
                fine[bin]++;
                count++;
            }

            var stats = new LuminanceStats { SampleCount = count };
            if (count == 0)
            {
                stats.Low = 0f;
                stats.High = 1f;
                stats.Histogram = new float[DisplayBins];
                return stats;
            }

            stats.Low = Percentile(fine, count, LowPercentile);
            stats.High = Percentile(fine, count, HighPercentile);
            if (stats.High - stats.Low < MinSpan)
            {
                var mid = (stats.High + stats.Low) * 0.5f;
                stats.Low = Mathf.Clamp01(mid - MinSpan * 0.5f);
                stats.High = Mathf.Clamp01(stats.Low + MinSpan);
            }

            var display = new float[DisplayBins];
            for (var i = 0; i < FineBins; i++)
            {
                display[i * DisplayBins / FineBins] += fine[i];
            }

            var max = 0f;
            foreach (var v in display) max = Mathf.Max(max, v);
            if (max > 0f)
            {
                for (var i = 0; i < DisplayBins; i++) display[i] /= max;
            }

            stats.Histogram = display;
            return stats;
        }

        private static float Percentile(int[] bins, int count, float q)
        {
            var target = q * count;
            var acc = 0f;
            for (var i = 0; i < bins.Length; i++)
            {
                acc += bins[i];
                if (acc >= target) return (i + 0.5f) / bins.Length;
            }

            return 1f;
        }
    }
}
