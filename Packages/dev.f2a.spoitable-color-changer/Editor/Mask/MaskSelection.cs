using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>選択範囲の状態。Data が null なら選択なし。</summary>
    internal readonly struct SelectionState
    {
        public readonly byte[] Data;
        public readonly int Width;
        public readonly int Height;

        public SelectionState(byte[] data, int width, int height)
        {
            Data = data;
            Width = data != null ? width : 0;
            Height = data != null ? height : 0;
        }

        public static bool Same(SelectionState a, SelectionState b)
        {
            if (a.Data == null || b.Data == null) return a.Data == b.Data;
            return a.Width == b.Width && a.Height == b.Height && MaskOps.SameBytes(a.Data, b.Data);
        }
    }

    /// <summary>
    /// 選択範囲(マスクと同じ並び。行 0 がテクスチャの下端)。レイヤーをまたいで共通で、保存はしない。
    /// 配列は履歴と共有するので、一度 Set したら書き換えない。ペン選択のように少しずつ書き換える道具は、
    /// MaskHistory.Begin のあとに複製を Set し、その配列だけを End まで書き換える(書き換えたら Changed を呼ぶ)。
    /// </summary>
    internal static class MaskSelection
    {
        public static SelectionState Current { get; private set; }

        /// <summary>表示用テクスチャ(選択範囲の解像度のまま)。選択なしなら null。</summary>
        public static Texture2D Texture { get; private set; }

        private static byte[] s_resampled;
        private static int s_resampledWidth;
        private static int s_resampledHeight;

        public static bool Exists => Current.Data != null;

        /// <summary>選択範囲(表示用テクスチャ)が変わったとき。シーンビューの表示を描き直すのに使う。</summary>
        public static event System.Action Updated;

        public static void Set(byte[] data, int width, int height)
        {
            Set(new SelectionState(data, width, height));
        }

        public static void Set(SelectionState state)
        {
            Current = state;
            s_resampled = null;
            Upload();
        }

        public static void Clear()
        {
            Set(default);
        }

        /// <summary>Current.Data の中身を書き換えたあとに呼ぶ。</summary>
        public static void Changed()
        {
            s_resampled = null;
            Upload();
        }

        /// <summary>
        /// width × height の並びにした選択範囲(読み取り専用)。解像度の違うマスクに切り替えたときは
        /// 引き伸ばしたものを返す。選択なしなら null。
        /// </summary>
        public static byte[] For(int width, int height)
        {
            var current = Current;
            if (current.Data == null) return null;
            if (current.Width == width && current.Height == height) return current.Data;
            if (s_resampled == null || s_resampledWidth != width || s_resampledHeight != height)
            {
                s_resampled = Resample(current, width, height);
                s_resampledWidth = width;
                s_resampledHeight = height;
            }

            return s_resampled;
        }

        private static byte[] Resample(SelectionState source, int width, int height)
        {
            var result = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var sy = Mathf.Min(source.Height - 1, (int)((y + 0.5f) * source.Height / height));
                for (var x = 0; x < width; x++)
                {
                    var sx = Mathf.Min(source.Width - 1, (int)((x + 0.5f) * source.Width / width));
                    result[y * width + x] = source.Data[sy * source.Width + sx];
                }
            }

            return result;
        }

        private static void Upload()
        {
            UploadTexture();
            Updated?.Invoke();
        }

        private static void UploadTexture()
        {
            var current = Current;
            if (current.Data == null)
            {
                if (Texture != null) Object.DestroyImmediate(Texture);
                Texture = null;
                return;
            }

            if (Texture != null && (Texture.width != current.Width || Texture.height != current.Height))
            {
                Object.DestroyImmediate(Texture);
                Texture = null;
            }

            if (Texture == null)
            {
                Texture = new Texture2D(current.Width, current.Height, TextureFormat.R8, false, true)
                {
                    name = "Spoitable selection",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            Texture.LoadRawTextureData(current.Data);
            Texture.Apply(false, false);
        }
    }
}
