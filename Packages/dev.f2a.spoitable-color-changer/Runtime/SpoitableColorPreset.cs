using System;
using System.Collections.Generic;
using UnityEngine;

namespace faketuna.spoitable_color_changer
{
    /// <summary>元テクスチャの明るさを 0〜1 に正規化するときの範囲の決め方。</summary>
    public enum LuminanceRangeMode
    {
        /// <summary>元テクスチャの明るさの分布から自動で決める。</summary>
        Auto = 0,

        /// <summary>黒点と白点を手で指定する。</summary>
        Manual = 1,
    }

    /// <summary>グラデーションのストップ。色は sRGB(カラーピッカーや HEX と同じ値)で持つ。</summary>
    [Serializable]
    public struct ColorStop
    {
        [Range(0f, 1f)] public float position;
        public Color color;

        public ColorStop(float position, Color color)
        {
            this.position = position;
            this.color = color;
        }
    }

    /// <summary>1 つのグラデーションマップ。マスクはアバターごとに違うので、コンポーネント側でレイヤー名に割り当てる。</summary>
    [Serializable]
    public class ColorLayer
    {
        public string name = "Layer";
        public bool visible = true;
        [Range(0f, 1f)] public float opacity = 1f;
        public List<ColorStop> stops = new List<ColorStop>();
    }

    /// <summary>使い回す配色(明るさのレンジとレイヤー)。</summary>
    [CreateAssetMenu(menuName = "faketuna/Spoitable Color Preset", fileName = "SpoitableColorPreset")]
    public class SpoitableColorPreset : ScriptableObject
    {
        public const int MinStops = 2;
        public const int MaxStops = 8;

        public LuminanceRangeMode rangeMode = LuminanceRangeMode.Auto;
        [Range(0f, 1f)] public float blackPoint = 0f;
        [Range(0f, 1f)] public float whitePoint = 1f;
        public List<ColorLayer> layers = new List<ColorLayer>();

        public static ColorLayer CreateDefaultLayer(string name)
        {
            return new ColorLayer
            {
                name = name,
                stops = new List<ColorStop>
                {
                    new ColorStop(0f, Color.black),
                    new ColorStop(1f, Color.white),
                },
            };
        }
    }
}
