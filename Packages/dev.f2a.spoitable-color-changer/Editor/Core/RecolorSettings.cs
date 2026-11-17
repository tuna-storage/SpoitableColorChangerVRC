using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>コンポーネントの設定のスナップショット。NDMF プレビューの変更検知にも使う。</summary>
    internal sealed class ComponentConfig : IEquatable<ComponentConfig>
    {
        public readonly TargetMode TargetMode;
        public readonly Renderer TargetRenderer;
        public readonly int MaterialSlot;
        public readonly Material TargetMaterial;
        public readonly string[] TextureProperties;
        public readonly SpoitableColorPreset Preset;
        public readonly MaskConfig[] Masks;
        public readonly bool PreviewEnabled;

        private ComponentConfig(SpoitableColorChanger c)
        {
            TargetMode = c.targetMode;
            TargetRenderer = c.targetRenderer;
            MaterialSlot = c.materialSlot;
            TargetMaterial = c.targetMaterial;
            TextureProperties = (c.textureProperties ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct()
                .ToArray();
            Preset = c.preset;
            Masks = (c.masks ?? new List<MaskBinding>())
                .Where(m => m != null)
                .Select(m => new MaskConfig(m.layerName ?? "", m.texture, m.channel, m.invert, m.paintMask,
                    GradientMaskConfig.Capture(m.gradient), ColorPathConfig.Capture(m.colorPath)))
                .ToArray();
            PreviewEnabled = c.previewEnabled;
        }

        public static ComponentConfig Capture(SpoitableColorChanger c)
        {
            return c == null ? null : new ComponentConfig(c);
        }

        public MaskConfig FindMask(string layerName)
        {
            foreach (var m in Masks)
            {
                if (m.LayerName == layerName) return m;
            }

            return null;
        }

        public bool Equals(ComponentConfig other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return TargetMode == other.TargetMode
                   && TargetRenderer == other.TargetRenderer
                   && MaterialSlot == other.MaterialSlot
                   && TargetMaterial == other.TargetMaterial
                   && TextureProperties.SequenceEqual(other.TextureProperties)
                   && Preset == other.Preset
                   && Masks.SequenceEqual(other.Masks)
                   && PreviewEnabled == other.PreviewEnabled;
        }

        public override bool Equals(object obj)
        {
            return obj is ComponentConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)TargetMode, TargetRenderer, MaterialSlot, TargetMaterial, Preset, PreviewEnabled);
        }

        public static bool AreEqual(ComponentConfig a, ComponentConfig b)
        {
            return a is null ? b is null : a.Equals(b);
        }
    }

    internal sealed class MaskConfig : IEquatable<MaskConfig>
    {
        public readonly string LayerName;
        public readonly Texture2D Texture;
        public readonly MaskChannel Channel;
        public readonly bool Invert;
        public readonly Texture2D PaintMask;
        public readonly GradientMaskConfig Gradient;
        public readonly ColorPathConfig Path;

        public MaskConfig(string layerName, Texture2D texture, MaskChannel channel, bool invert, Texture2D paintMask,
            GradientMaskConfig gradient, ColorPathConfig path)
        {
            LayerName = layerName;
            Texture = texture;
            Channel = channel;
            Invert = invert;
            PaintMask = paintMask;
            Gradient = gradient ?? GradientMaskConfig.None;
            Path = path ?? ColorPathConfig.None;
        }

        public bool Equals(MaskConfig other)
        {
            if (other is null) return false;
            return LayerName == other.LayerName && Texture == other.Texture && Channel == other.Channel &&
                   Invert == other.Invert && PaintMask == other.PaintMask && Gradient.Equals(other.Gradient) &&
                   Path.Equals(other.Path);
        }

        public override bool Equals(object obj)
        {
            return obj is MaskConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LayerName, Texture, (int)Channel, Invert, PaintMask, Gradient, Path);
        }
    }

    /// <summary>プリセットのスナップショット。</summary>
    internal sealed class PresetConfig : IEquatable<PresetConfig>
    {
        public readonly LuminanceRangeMode RangeMode;
        public readonly float BlackPoint;
        public readonly float WhitePoint;
        public readonly LayerConfig[] Layers;

        private PresetConfig(SpoitableColorPreset p)
        {
            RangeMode = p.rangeMode;
            BlackPoint = p.blackPoint;
            WhitePoint = p.whitePoint;
            Layers = (p.layers ?? new List<ColorLayer>())
                .Where(l => l != null)
                .Select(l => new LayerConfig(l))
                .ToArray();
        }

        public static PresetConfig Capture(SpoitableColorPreset p)
        {
            return p == null ? null : new PresetConfig(p);
        }

        public bool Equals(PresetConfig other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return RangeMode == other.RangeMode
                   && BlackPoint.Equals(other.BlackPoint)
                   && WhitePoint.Equals(other.WhitePoint)
                   && Layers.SequenceEqual(other.Layers);
        }

        public override bool Equals(object obj)
        {
            return obj is PresetConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = HashCode.Combine((int)RangeMode, BlackPoint, WhitePoint);
            foreach (var l in Layers) h = HashCode.Combine(h, l.GetHashCode());
            return h;
        }

        public static bool AreEqual(PresetConfig a, PresetConfig b)
        {
            return a is null ? b is null : a.Equals(b);
        }
    }

    internal sealed class LayerConfig : IEquatable<LayerConfig>
    {
        public readonly string Name;
        public readonly bool Visible;
        public readonly float Opacity;
        public readonly (float position, Color color)[] Stops;

        public LayerConfig(ColorLayer l)
        {
            Name = l.name ?? "";
            Visible = l.visible;
            Opacity = Mathf.Clamp01(l.opacity);
            Stops = (l.stops ?? new List<ColorStop>())
                .Select(s => (Mathf.Clamp01(s.position), s.color))
                .ToArray();
        }

        public bool Equals(LayerConfig other)
        {
            if (other is null) return false;
            return Name == other.Name && Visible == other.Visible && Opacity.Equals(other.Opacity) &&
                   Stops.SequenceEqual(other.Stops);
        }

        public override bool Equals(object obj)
        {
            return obj is LayerConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = HashCode.Combine(Name, Visible, Opacity);
            foreach (var s in Stops) h = HashCode.Combine(h, s.position, s.color);
            return h;
        }
    }

    /// <summary>1 枚のテクスチャを再配色するのに必要な値。プレビューとビルドの両方がこれを使う。</summary>
    internal sealed class RecolorSettings : IEquatable<RecolorSettings>
    {
        public readonly LuminanceRangeMode RangeMode;
        public readonly float BlackPoint;
        public readonly float WhitePoint;
        public readonly LayerSettings[] Layers;

        public bool IsNoop => Layers.Length == 0;

        /// <summary>モデル上の位置を使うグラデーションマスクか色の経路がある(描くのに位置マップが要る)。</summary>
        public bool NeedsPositions => Layers.Any(l => l.Gradient.UsesModel || l.Path.UsesModel);

        private RecolorSettings(PresetConfig preset, ComponentConfig component)
        {
            RangeMode = preset.RangeMode;
            BlackPoint = preset.BlackPoint;
            WhitePoint = preset.WhitePoint;
            Layers = preset.Layers
                .Where(l => l.Visible && l.Opacity > 0f && l.Stops.Length > 0)
                .Select(l =>
                {
                    var mask = component?.FindMask(l.Name);
                    // 編集中の塗りマスクは、保存前でもメモリ上の作業用テクスチャを使う(プレビューとビルドで同じ)
                    Texture paint = null;
                    var paintVersion = 0;
                    if (mask?.PaintMask != null) paint = PaintSession.Resolve(mask.PaintMask, out paintVersion);
                    return new LayerSettings(l.Stops, l.Opacity, mask?.Texture, mask?.Channel ?? MaskChannel.Gray,
                        mask?.Invert ?? false, paint, paintVersion, mask?.Gradient ?? GradientMaskConfig.None,
                        mask?.Path ?? ColorPathConfig.None);
                })
                .ToArray();
        }

        public static RecolorSettings Create(PresetConfig preset, ComponentConfig component)
        {
            return preset == null ? null : new RecolorSettings(preset, component);
        }

        public bool Equals(RecolorSettings other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return RangeMode == other.RangeMode
                   && BlackPoint.Equals(other.BlackPoint)
                   && WhitePoint.Equals(other.WhitePoint)
                   && Layers.SequenceEqual(other.Layers);
        }

        public override bool Equals(object obj)
        {
            return obj is RecolorSettings other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = HashCode.Combine((int)RangeMode, BlackPoint, WhitePoint);
            foreach (var l in Layers) h = HashCode.Combine(h, l.GetHashCode());
            return h;
        }
    }

    internal sealed class LayerSettings : IEquatable<LayerSettings>
    {
        public readonly (float position, Color color)[] Stops;
        public readonly float Opacity;
        public readonly Texture2D Mask;
        public readonly MaskChannel Channel;
        public readonly bool Invert;
        public readonly Texture PaintMask;
        public readonly int PaintVersion;
        public readonly GradientMaskConfig Gradient;

        /// <summary>色の経路。有効なら明るさの代わりに経路上の位置でバーの色を当てる。</summary>
        public readonly ColorPathConfig Path;

        public LayerSettings((float position, Color color)[] stops, float opacity, Texture2D mask,
            MaskChannel channel, bool invert, Texture paintMask, int paintVersion, GradientMaskConfig gradient,
            ColorPathConfig path)
        {
            Gradient = gradient ?? GradientMaskConfig.None;
            Path = path ?? ColorPathConfig.None;
            Stops = stops;
            Opacity = opacity;
            Mask = mask;
            Channel = channel;
            Invert = invert;
            PaintMask = paintMask;
            PaintVersion = paintVersion;
        }

        public bool Equals(LayerSettings other)
        {
            if (other is null) return false;
            return Stops.SequenceEqual(other.Stops) && Opacity.Equals(other.Opacity) && Mask == other.Mask &&
                   Channel == other.Channel && Invert == other.Invert && PaintMask == other.PaintMask &&
                   PaintVersion == other.PaintVersion && Gradient.Equals(other.Gradient) && Path.Equals(other.Path);
        }

        public override bool Equals(object obj)
        {
            return obj is LayerSettings other && Equals(other);
        }

        public override int GetHashCode()
        {
            var h = HashCode.Combine(Opacity, Mask, (int)Channel, Invert, PaintMask, PaintVersion, Gradient, Path);
            foreach (var s in Stops) h = HashCode.Combine(h, s.position, s.color);
            return h;
        }
    }
}
