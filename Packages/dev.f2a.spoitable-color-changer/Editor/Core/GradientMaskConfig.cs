using System;
using System.Linq;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>グラデーションマスクの設定のスナップショット。プレビューの変更検知とキャッシュのキーに使う。</summary>
    internal sealed class GradientMaskConfig : IEquatable<GradientMaskConfig>
    {
        public static readonly GradientMaskConfig None = new GradientMaskConfig(null);

        public readonly GradientMaskMode Mode;
        public readonly Vector3 Start;
        public readonly Vector3 End;
        /// <summary>点(xyz: 位置, w: 値)。表示用の UV は含めない(描く結果に関係しないので)。</summary>
        public readonly Vector4[] Points;
        public readonly float Sharpness;
        public readonly bool Smooth;
        public readonly bool Invert;

        private GradientMaskConfig(GradientMask g)
        {
            Mode = g?.mode ?? GradientMaskMode.None;
            Start = g?.start ?? Vector3.zero;
            End = g?.end ?? Vector3.zero;
            Points = g?.points != null
                ? g.points.Take(GradientMask.MaxPoints)
                    .Select(p => new Vector4(p.position.x, p.position.y, p.position.z, Mathf.Clamp01(p.value)))
                    .ToArray()
                : Array.Empty<Vector4>();
            Sharpness = Mathf.Clamp(g?.sharpness ?? 2f, 0.5f, 8f);
            Smooth = g?.smooth ?? false;
            Invert = g?.invert ?? false;
        }

        public static GradientMaskConfig Capture(GradientMask g)
        {
            return g == null || g.mode == GradientMaskMode.None ? None : new GradientMaskConfig(g);
        }

        public bool IsNone => Mode == GradientMaskMode.None ||
                              (IsPoints && Points.Length == 0);

        public bool IsPoints => Mode == GradientMaskMode.TexturePoints || Mode == GradientMaskMode.ModelPoints;

        /// <summary>モデル上の位置を使う種類(位置マップが要る)。</summary>
        public bool UsesModel => !IsNone &&
                                 (Mode == GradientMaskMode.ModelDirection || Mode == GradientMaskMode.ModelPoints);

        public bool Equals(GradientMaskConfig other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (Mode != other.Mode) return false;
            if (Mode == GradientMaskMode.None) return true;
            return Start == other.Start && End == other.End && Points.SequenceEqual(other.Points) &&
                   Sharpness.Equals(other.Sharpness) && Smooth == other.Smooth && Invert == other.Invert;
        }

        public override bool Equals(object obj)
        {
            return obj is GradientMaskConfig other && Equals(other);
        }

        public override int GetHashCode()
        {
            if (Mode == GradientMaskMode.None) return 0;
            var h = HashCode.Combine((int)Mode, Start, End, Sharpness, Smooth, Invert);
            foreach (var p in Points) h = HashCode.Combine(h, p);
            return h;
        }
    }
}
