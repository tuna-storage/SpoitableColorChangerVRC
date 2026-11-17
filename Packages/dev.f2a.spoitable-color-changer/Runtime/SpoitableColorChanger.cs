using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace faketuna.spoitable_color_changer
{
    /// <summary>マスクのどの値を使うか。</summary>
    public enum MaskChannel
    {
        /// <summary>白黒画像として扱う(RGB の明るさ)。</summary>
        Gray = 0,
        Red = 1,
        Green = 2,
        Blue = 3,
        Alpha = 4,
    }

    /// <summary>どこを塗り替えるかの指定方法。</summary>
    public enum TargetMode
    {
        /// <summary>1 つの Renderer のマテリアルスロットを指定する。</summary>
        Renderer = 0,

        /// <summary>マテリアルを指定し、アバター内でそれを使うすべての Renderer に適用する。</summary>
        Material = 1,
    }

    /// <summary>グラデーションマスクの種類。</summary>
    public enum GradientMaskMode
    {
        None = 0,

        /// <summary>テクスチャ上の直線。start で 0、end で 1。</summary>
        TextureLinear = 1,

        /// <summary>テクスチャ上の円形。start(中心)で 0、end までの距離で 1。</summary>
        TextureRadial = 2,

        /// <summary>テクスチャ上の点ごとの値を補間する。</summary>
        TexturePoints = 3,

        /// <summary>モデル上の方向。start で 0、end で 1(アバターのルートから見た位置)。</summary>
        ModelDirection = 4,

        /// <summary>モデル上の点ごとの値を補間する。</summary>
        ModelPoints = 5,
    }

    /// <summary>グラデーションマスクの点。</summary>
    [Serializable]
    public struct GradientMaskPoint
    {
        /// <summary>テクスチャ上なら xy が UV(0〜1)。モデル上ならアバターのルートから見た位置。</summary>
        public Vector3 position;

        [Range(0f, 1f)] public float value;

        /// <summary>モデル上の点を置いたテクスチャ上の位置(表示用)。分からなければ (-1, -1)。</summary>
        public Vector2 uv;

        public GradientMaskPoint(Vector3 position, float value)
        {
            this.position = position;
            this.value = value;
            uv = new Vector2(-1f, -1f);
        }
    }

    /// <summary>
    /// 位置で濃さが変わるマスク。画像マスク・塗りマスクと掛け合わせる。
    /// テクスチャ上の種類は UV(0〜1)、モデル上の種類はアバターのルートから見た位置で指定する。
    /// </summary>
    [Serializable]
    public class GradientMask
    {
        public const int MaxPoints = 16;

        public GradientMaskMode mode = GradientMaskMode.None;
        public Vector3 start = new Vector3(0.5f, 1f, 0f);
        public Vector3 end = new Vector3(0.5f, 0f, 0f);

        /// <summary>モデル上の方向の始点・終点を置いたテクスチャ上の位置(表示用)。分からなければ (-1, -1)。</summary>
        public Vector2 startUv = new Vector2(-1f, -1f);
        public Vector2 endUv = new Vector2(-1f, -1f);

        /// <summary>モデル上の種類で、表示用のテクスチャ上の位置(uv・startUv・endUv)を拾ったテクスチャ。</summary>
        public Texture uvHintTexture;
        public List<GradientMaskPoint> points = new List<GradientMaskPoint>();

        /// <summary>点の補間で、近い点の影響をどれだけ強くするか(逆距離加重の指数)。</summary>
        [Range(0.5f, 8f)] public float sharpness = 2f;

        /// <summary>0 と 1 の付近をなめらかにする(smoothstep)。</summary>
        public bool smooth;
        public bool invert;
    }

    /// <summary>レイヤーの色をどう決めるか。あとで種類を足すときは末尾に足す(シリアライズ済みの値をずらさない)。</summary>
    public enum ColorPathMode
    {
        /// <summary>元テクスチャの明るさでバーの色を当てる(今までどおり)。</summary>
        Luminance = 0,

        /// <summary>点を結んだ折れ線に沿ってバーの色を並べる。折れ線の全長をバーの 0〜1 に割り当てる。</summary>
        Normal = 1,

        /// <summary>点 1 つにストップ 1 つを対応させ、点の間の距離の比でストップの位置を決める。</summary>
        Relative = 2,
    }

    /// <summary>色の経路の点を置く場所。</summary>
    public enum ColorPathSpace
    {
        /// <summary>テクスチャ上(UV 0〜1)。</summary>
        Texture = 0,

        /// <summary>モデル上(アバターのルートから見た位置)。</summary>
        Model = 1,
    }

    /// <summary>色の経路の点。</summary>
    [Serializable]
    public struct ColorPathPoint
    {
        /// <summary>テクスチャ上なら xy が UV(0〜1)。モデル上ならアバターのルートから見た位置。</summary>
        public Vector3 position;

        /// <summary>モデル上の点を置いたテクスチャ上の位置(表示用)。分からなければ (-1, -1)。</summary>
        public Vector2 uv;

        public ColorPathPoint(Vector3 position)
        {
            this.position = position;
            uv = new Vector2(-1f, -1f);
        }
    }

    /// <summary>色の経路の 1 本(点を A→B→C… の順に結んだ折れ線)。</summary>
    [Serializable]
    public class ColorPathLine
    {
        public List<ColorPathPoint> points = new List<ColorPathPoint>();
    }

    /// <summary>
    /// 位置で色を並べる設定。点を A→B→C… の順に結んだ折れ線に沿って、レイヤーのバーの色を並べる。
    /// 陰影は元テクスチャの明るさから残す。点が 2 個以上ある線が 1 本もなければ明るさで色を決める。
    /// 線は何本でも置け(上限あり)、塗りマスクのつながった塊ごとに、その塊に点が乗っている線だけを使う。
    /// 塊の中に線が複数あれば、画素ごとにいちばん近い線に沿って色を決める。
    /// </summary>
    [Serializable]
    public class ColorPath : ISerializationCallbackReceiver
    {
        /// <summary>1 本の線の点の上限。</summary>
        public const int MaxPoints = 16;

        /// <summary>線の本数の上限(塊ごとに使う線を 8 ビットで持つので 8 本まで)。</summary>
        public const int MaxLines = 8;

        /// <summary>すべての線の点の合計の上限(シェーダーに渡せる数)。</summary>
        public const int MaxTotalPoints = 64;

        public ColorPathMode mode = ColorPathMode.Luminance;
        public ColorPathSpace space = ColorPathSpace.Model;
        public List<ColorPathLine> lines = new List<ColorPathLine>();

        /// <summary>線が 1 本だけだったころの点。読み込んだら lines の 1 本目に移す。</summary>
        [SerializeField, HideInInspector] private List<ColorPathPoint> points = new List<ColorPathPoint>();

        /// <summary>モデル上の点の表示用の位置(uv)を拾ったテクスチャ。</summary>
        public Texture uvHintTexture;

        /// <summary>元の明暗をどれだけ残すか。0 で平らな色、1 で元の明暗差をそのまま。</summary>
        [Range(0f, 1f)] public float shading = 1f;

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            if (points == null || points.Count == 0) return;
            if (lines == null) lines = new List<ColorPathLine>();
            if (lines.Count == 0) lines.Add(new ColorPathLine { points = new List<ColorPathPoint>(points) });
            points.Clear();
        }
    }

    /// <summary>プリセットのレイヤー名に対する、このアバター用のマスク。</summary>
    [Serializable]
    public class MaskBinding
    {
        public string layerName = "";

        /// <summary>画像マスク(外部で作った白黒画像や RGBA のチャンネル)。</summary>
        public Texture2D texture;
        public MaskChannel channel = MaskChannel.Gray;
        public bool invert;

        /// <summary>マスクエディタで塗る範囲マスク(PNG)。画像マスクと掛け合わせる。</summary>
        public Texture2D paintMask;

        /// <summary>グラデーションマスク。画像マスク・塗りマスクと掛け合わせる。</summary>
        public GradientMask gradient = new GradientMask();

        /// <summary>位置で色を並べる設定(色の経路)。点はアバターごとに違うので、プリセットではなくこちらに持つ。</summary>
        public ColorPath colorPath = new ColorPath();
    }

    /// <summary>
    /// 対象のテクスチャを、プリセットの配色でグラデーションマップ再配色する。
    /// 適用は NDMF のビルド処理で行い、元のアセットは書き換えない。
    /// </summary>
    [AddComponentMenu("faketuna/Spoitable Color Changer")]
    public class SpoitableColorChanger : MonoBehaviour, INDMFEditorOnly
    {
        public TargetMode targetMode = TargetMode.Renderer;

        /// <summary>Renderer モード: 未指定なら同じ GameObject の Renderer を使う。</summary>
        public Renderer targetRenderer;

        /// <summary>Renderer モード: 対象のマテリアルスロット。-1 ならすべてのスロット。</summary>
        public int materialSlot = 0;

        /// <summary>Material モード: アバター内でこのマテリアルを使うスロットすべてに適用する。</summary>
        public Material targetMaterial;

        /// <summary>差し替えるテクスチャのプロパティ名。</summary>
        public List<string> textureProperties = new List<string> { "_MainTex" };

        public SpoitableColorPreset preset;

        public List<MaskBinding> masks = new List<MaskBinding>();

        /// <summary>プレビューにだけ効く ON/OFF(元の色との見比べ用)。ビルド結果は変わらない。</summary>
        public bool previewEnabled = true;

        public Renderer ResolveRenderer()
        {
            return targetRenderer != null ? targetRenderer : GetComponent<Renderer>();
        }

        public MaskBinding FindMask(string layerName)
        {
            if (masks == null) return null;
            foreach (var m in masks)
            {
                if (m != null && m.layerName == layerName) return m;
            }

            return null;
        }
    }
}
