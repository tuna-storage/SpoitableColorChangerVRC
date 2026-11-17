using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace faketuna.spoitable_color_changer.editor
{
    // シーンビュー: 選択範囲と、範囲選択の道具でカーソルの下(ドラッグ中はその形)の範囲をモデルの上に色で出す。
    // ツールバーの「3D」で「3D 空間操作の有効化」を ON にすると、シーンビューでモデルをクリック・ドラッグして選択や塗りができる。
    // グラデーションなど、ハンドルで操作する道具のときはマウスを受けない(それぞれのハンドルに任せる)
    internal sealed partial class MaskEditorWindow
    {
        private enum HoverOrigin
        {
            None = 0,
            Canvas = 1,
            Scene = 2,
        }

        private static readonly int SceneInputHint = "SpoitableColorChanger.SceneInput".GetHashCode();

        /// <summary>シーンビューでの 1 回のドラッグのイベントで、レイを飛ばす最大の回数(重くなりすぎないように)。</summary>
        private const int SceneMaxSamples = 24;

        /// <summary>ホバー中の範囲を求め直す最短の間隔(秒)。色選択は塗りつぶしの計算が重いので長め。</summary>
        private const double HoverInterval = 0.05;

        private const double HoverIntervalColor = 0.15;

        [SerializeField] private bool sceneInput;
        [SerializeField] private bool sceneShowSelection = true;
        [SerializeField] private bool sceneShowHover = true;
        [SerializeField] private Color sceneSelectionColor = new Color(0.2f, 0.65f, 1f, 0.45f);
        [SerializeField] private Color sceneHoverColor = new Color(1f, 0.12f, 0.1f, 0.55f);

        // ホバー(最後にマウスが乗っていたのがキャンバスかシーンビューか)
        private HoverOrigin _hoverOrigin;

        /// <summary>ホバー中のテクスチャ上の位置(シーンビューでは Tiling/Offset を掛けたあと、折り返す前)。</summary>
        private Vector2 _hoverUv;

        private bool _sceneHasHit;
        private SceneHit _sceneHit;
        private SceneView _sceneHitView;

        // ホバー中の範囲(マスクと同じ並び)と、その表示用テクスチャ
        private byte[] _hoverMask;
        private Texture2D _hoverTexture;
        private bool _hoverShown;
        private string _hoverKey;
        private PaintSession _hoverSession;
        private double _hoverTime;
        private bool _hoverPending;
        private Texture _hoverPixelsTried; // 色選択のホバーで読み込みを試したテクスチャ(何度も読み込まない)
        private Hash128 _hoverPixelsTriedHash; // そのときのテクスチャの中身(読み込み直されたら、もう一度読む)

        // シーンビューでのストローク(ペン・ブラシ・消しゴム・ペン選択)
        private bool _sceneStroke;
        private Tool _sceneStrokeTool;
        private byte _sceneStrokeTarget;
        private PaintSession _sceneStrokeSession;
        private Vector2 _sceneLastMouse;
        private bool _sceneLastHasPixel;
        private Vector2 _sceneLastPixel;
        private Vector3 _sceneLastPoint;
        private float _sceneLastWorldPerPixel;
        private int _sceneControl;
        private bool _sceneStampMaskChanged; // このイベントで塗りマスクを書き換えた(まとめて 1 回だけ送る)
        private bool _sceneStampSelectionChanged;
        private Tool _sceneOverlayTool; // 最後にシーンビューへ描いたときの道具(選択範囲を出すかが道具で変わる)

        // 対象のテクスチャの一覧(Material 指定ではアバター全体を走査するので、シーンビューのイベントごとには求めない)
        private List<(Material material, string property, Texture texture)> _sceneTextures;
        private SpoitableColorChanger _sceneTexturesTarget;
        private double _sceneTexturesTime;

        private static GUIStyle s_sceneNotice;

        private static GUIStyle SceneNotice => s_sceneNotice ??= new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = Color.white },
        };

        /// <summary>シーンビューで受け付ける道具。矩形・投げ縄とハンドルで操作する道具は除く。</summary>
        private static bool IsSceneTool(Tool t)
        {
            return t == Tool.UvIsland || t == Tool.ColorSelect || t == Tool.Bucket || t == Tool.Pen ||
                   t == Tool.Brush || t == Tool.Eraser || t == Tool.SelectBrush;
        }

        // ------------------------------------------------------------------ 登録と後始末

        private void EnableScene()
        {
            wantsMouseEnterLeaveWindow = true; // キャンバスからマウスが出たら、ホバーの表示を消すため
            SceneView.duringSceneGui += OnSceneGUIScene;
            MaskHistory.EditAborted += OnSceneEditAborted;
            MaskSelection.Updated += OnSelectionUpdated;
        }

        private void DisableScene()
        {
            SceneView.duringSceneGui -= OnSceneGUIScene;
            MaskHistory.EditAborted -= OnSceneEditAborted;
            MaskSelection.Updated -= OnSelectionUpdated;
            EditorApplication.update -= OnHoverUpdate;
            _hoverPending = false;
            FinishSceneStroke();
            ReleaseHover();
            SceneMesh.Clear();
            _sceneTextures = null;
            _sceneHasHit = false;
            _hoverOrigin = HoverOrigin.None;
        }

        private void ReleaseHover()
        {
            if (_hoverTexture != null) DestroyImmediate(_hoverTexture);
            _hoverTexture = null;
            _hoverMask = null;
            _hoverKey = null;
            _hoverShown = false;
            _hoverSession = null;
        }

        private void OnSelectionUpdated()
        {
            if (sceneShowSelection && IsSelectionTool(tool)) SceneView.RepaintAll();
        }

        /// <summary>取り消しなどで編集が打ち切られた(MaskHistory は取り下げ済み)。</summary>
        private void OnSceneEditAborted()
        {
            if (!_sceneStroke) return;
            ResetSceneStroke();
            SceneView.RepaintAll();
        }

        private void ResetSceneStroke()
        {
            _sceneStroke = false;
            _sceneStrokeSession = null;
            _sceneLastHasPixel = false;
            if (_sceneControl != 0 && GUIUtility.hotControl == _sceneControl) GUIUtility.hotControl = 0;
        }

        /// <summary>シーンビューのイベントで使う、今のテクスチャと塗りマスク。テクスチャがなければ false。</summary>
        private bool SceneContext(out Texture source, out PaintSession session)
        {
            source = null;
            session = null;
            if (target == null || target.preset == null) return false;
            var now = EditorApplication.timeSinceStartup;
            if (_sceneTextures == null || _sceneTexturesTarget != target || now - _sceneTexturesTime > 1.0)
            {
                _sceneTextures = TargetResolver.TargetTextures(target);
                _sceneTexturesTarget = target;
                _sceneTexturesTime = now;
            }

            if (_sceneTextures.Count == 0) return false;
            source = _sceneTextures[Mathf.Clamp(textureIndex, 0, _sceneTextures.Count - 1)].texture;
            if (source == null) return false;
            var binding = target.FindMask(layerName);
            session = binding != null && binding.paintMask != null ? PaintSession.Open(binding.paintMask) : null;
            return true;
        }

        // ------------------------------------------------------------------ ウィンドウ側(上のツールバーと表示パネル)

        /// <summary>
        /// 上のツールバーの「3D」。シーンビューの Gizmos と同じく、押すとチェック付きのメニューを出す。
        /// 3D 空間操作が ON の間は、色を付ける(シーンビューのクリックを受け取っていることが分かるように)。
        /// </summary>
        private void DrawSceneToolbar()
        {
            var content = new GUIContent("3D",
                "シーンビュー(3D 空間)との連携\n" +
                "・3D 空間操作の有効化: シーンビューでモデルをクリック・ドラッグして選択や塗りができる" +
                "(UV 島・色選択・バケツ・ペン・ブラシ・消しゴム・ペン選択)\n" +
                "・3D 空間に選択範囲を描画: 範囲選択の道具を使っている間、モデルに選択範囲を色で表示する");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown, GUILayout.Width(40f));
            var clicked = EditorGUI.DropdownButton(rect, content, FocusType.Passive, EditorStyles.toolbarDropDown);
            if (sceneInput && Event.current.type == EventType.Repaint)
            {
                // ドロップダウンには押し込んだ見た目がないので、色を重ねる
                var accent = SccGui.AccentColor;
                EditorGUI.DrawRect(rect, new Color(accent.r, accent.g, accent.b, 0.3f));
            }

            if (clicked)
            {
                var menu = new GenericMenu();
                // メニューを開いたままウィンドウを閉じることもあるので、閉じていたら何もしない
                menu.AddItem(new GUIContent("3D 空間操作の有効化"), sceneInput, () =>
                {
                    if (this != null) SetSceneInput(!sceneInput);
                });
                menu.AddItem(new GUIContent("3D 空間に選択範囲を描画"), sceneShowSelection, () =>
                {
                    if (this == null) return;
                    sceneShowSelection = !sceneShowSelection;
                    SceneView.RepaintAll();
                    Repaint();
                });
                menu.DropDown(rect);
            }

            SccGui.VerticalSeparator();
        }

        private void SetSceneInput(bool on)
        {
            if (sceneInput == on) return;
            sceneInput = on;
            if (!sceneInput)
            {
                FinishSceneStroke();
                _sceneHasHit = false;
                if (_hoverOrigin == HoverOrigin.Scene) _hoverOrigin = HoverOrigin.None;
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private void DrawSceneDisplayOptions()
        {
            EditorGUI.BeginChangeCheck();
            sceneShowSelection = EditorGUILayout.Toggle(
                new GUIContent("3D の選択範囲", "範囲選択の道具を使っている間、シーンビューのモデルに選択範囲を色で表示する(塗る道具のときは出さない)"), sceneShowSelection);
            using (new EditorGUI.DisabledScope(!sceneShowSelection))
            {
                sceneSelectionColor = EditorGUILayout.ColorField("選択範囲の色", sceneSelectionColor);
            }

            sceneShowHover = EditorGUILayout.Toggle(
                new GUIContent("3D のホバー", "範囲選択の道具で、カーソルの下(ドラッグ中はその形)の範囲をシーンビューのモデルに表示する"),
                sceneShowHover);
            using (new EditorGUI.DisabledScope(!sceneShowHover))
            {
                sceneHoverColor = EditorGUILayout.ColorField("ホバーの色", sceneHoverColor);
            }

            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
        }

        /// <summary>
        /// OnGUI の最後に毎イベント呼ぶ。キャンバスの上のマウスをホバーとして覚え、ホバー中の範囲を更新する。
        /// キャンバスの入力で使われたイベントも見るため、元の種類(rawType)で判断する。
        /// </summary>
        private void UpdateSceneFromWindow(PaintSession session, Texture source)
        {
            var e = Event.current;
            var type = e.rawType;
            if (tool != _sceneOverlayTool)
            {
                // 道具を変えたら、選択範囲の表示の有無が変わるのでシーンビューを描き直す
                _sceneOverlayTool = tool;
                SceneView.RepaintAll();
            }

            if (type == EventType.Layout) return;

            // 塗っていたマスクの割り当てが外れた(Undo やレイヤーの切り替え)ら、シーンビューのストロークを打ち切る
            if (_sceneStroke && _sceneStrokeSession != session)
            {
                MaskHistory.Cancel();
                ResetSceneStroke();
            }

            if (type == EventType.MouseMove || type == EventType.MouseDrag || type == EventType.MouseEnterWindow)
            {
                // マウスがこのウィンドウにあるなら、シーンビューでの当たりはもう古い
                if (_sceneHasHit || _hoverOrigin == HoverOrigin.Scene)
                {
                    _sceneHasHit = false;
                    if (_hoverOrigin == HoverOrigin.Scene) _hoverOrigin = HoverOrigin.None;
                    SceneView.RepaintAll();
                }
            }

            if (type == EventType.MouseMove || type == EventType.MouseDrag)
            {
                if (session != null && _imageRect.width > 0f && _canvas.Contains(e.mousePosition))
                {
                    _hoverOrigin = HoverOrigin.Canvas;
                    _hoverUv = ToUv(e.mousePosition);
                }
                else if (_hoverOrigin == HoverOrigin.Canvas && !_toolDragging)
                {
                    _hoverOrigin = HoverOrigin.None;
                }
            }
            else if (type == EventType.MouseLeaveWindow && _hoverOrigin == HoverOrigin.Canvas && !_toolDragging)
            {
                _hoverOrigin = HoverOrigin.None;
            }

            RefreshHoverRange(session, source);
        }

        /// <summary>シーンビューでカーソルが乗っている位置(キャンバスに UV 島の輪郭を出すため)。</summary>
        private bool SceneHoverUv(out Vector2 uv)
        {
            uv = _hoverUv;
            return _hoverOrigin == HoverOrigin.Scene && _sceneHasHit;
        }

        // ------------------------------------------------------------------ ホバー中の範囲

        /// <summary>今の状態のホバー中の範囲を表す文字列。出すものがなければ null。</summary>
        private string HoverKey(PaintSession session, Texture source, out double interval)
        {
            interval = HoverInterval;
            if (!sceneShowHover || session == null || source == null || _hoverOrigin == HoverOrigin.None ||
                _sceneStroke)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.Append((int)tool).Append(':').Append(session.GetHashCode()).Append(':').Append(session.Width).Append('x')
                .Append(session.Height).Append(':').Append(source.GetInstanceID()).Append(':')
                .Append((int)_hoverOrigin).Append(':');
            switch (tool)
            {
                case Tool.UvIsland:
                {
                    if (_toolDragging) return null;
                    var uv = UvIslands.Get(CachedUvSources(source), out _);
                    if (uv == null || uv.TriangleCount == 0) return null;
                    var islands = uv.IslandsAt(_hoverUv);
                    if (islands.Count == 0) return null;
                    islands.Sort();
                    foreach (var i in islands) sb.Append(i).Append(',');
                    sb.Append(':').Append(islandGrow).Append(':').Append(uv.GetHashCode());
                    return sb.ToString();
                }

                case Tool.ColorSelect:
                {
                    interval = HoverIntervalColor;
                    if (_toolDragging || !HoverPixel(session, source, out var pixel)) return null;
                    sb.Append(Mathf.FloorToInt(pixel.x)).Append(',').Append(Mathf.FloorToInt(pixel.y)).Append(':')
                        .Append(tolerance.ToString("R")).Append(contiguous ? 'c' : 'g');
                    return sb.ToString();
                }

                case Tool.SelectBrush:
                {
                    // ドラッグ中は選択範囲そのものが変わっていくので、ブラシの範囲は出さない
                    if (_toolDragging || !HoverPixel(session, source, out var pixel)) return null;
                    sb.Append(Mathf.RoundToInt(pixel.x * 2f)).Append(',').Append(Mathf.RoundToInt(pixel.y * 2f))
                        .Append(':').Append(brushRadius.ToString("R")).Append(':').Append(brushHardness.ToString("R"));
                    return sb.ToString();
                }

                case Tool.Rect:
                {
                    if (!_toolDragging || _hoverOrigin != HoverOrigin.Canvas) return null;
                    var a = ToPixel(_rectStart, session);
                    var b = ToPixel(_rectEnd, session);
                    sb.Append(Mathf.FloorToInt(a.x)).Append(',').Append(Mathf.FloorToInt(a.y)).Append(',')
                        .Append(Mathf.FloorToInt(b.x)).Append(',').Append(Mathf.FloorToInt(b.y));
                    return sb.ToString();
                }

                case Tool.Lasso:
                {
                    if (!_toolDragging || _hoverOrigin != HoverOrigin.Canvas || _lasso.Count < 3) return null;
                    var last = ToPixel(_lasso[_lasso.Count - 1], session);
                    sb.Append(_lasso.Count).Append(':').Append(Mathf.FloorToInt(last.x)).Append(',')
                        .Append(Mathf.FloorToInt(last.y));
                    return sb.ToString();
                }

                default:
                    return null;
            }
        }

        /// <summary>ホバー中の範囲を、変わったときだけ求め直す(重いものは間引く)。</summary>
        private void RefreshHoverRange(PaintSession session, Texture source)
        {
            var key = HoverKey(session, source, out var interval);
            if (key == null)
            {
                HideHover();
                return;
            }

            if (key == _hoverKey) return;

            var now = EditorApplication.timeSinceStartup;
            if (_hoverShown && now - _hoverTime < interval)
            {
                // 少し待ってから、そのときの状態で求め直す
                if (!_hoverPending)
                {
                    _hoverPending = true;
                    EditorApplication.update += OnHoverUpdate;
                }

                return;
            }

            var wasShown = _hoverShown;
            _hoverTime = now;
            _hoverKey = key;
            _hoverSession = session;
            try
            {
                _hoverShown = ComputeHover(session, source);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _hoverShown = false;
            }

            if (_hoverShown || wasShown) SceneView.RepaintAll();
            // シーンビューで乗っている UV 島を、キャンバスにも輪郭で出す
            if (_hoverOrigin == HoverOrigin.Scene && tool == Tool.UvIsland) Repaint();
        }

        private void HideHover()
        {
            _hoverKey = null;
            if (!_hoverShown) return;
            _hoverShown = false;
            SceneView.RepaintAll();
        }

        private void OnHoverUpdate()
        {
            var interval = tool == Tool.ColorSelect ? HoverIntervalColor : HoverInterval;
            if (EditorApplication.timeSinceStartup - _hoverTime < interval) return;
            EditorApplication.update -= OnHoverUpdate;
            _hoverPending = false;
            if (SceneContext(out var source, out var session)) RefreshHoverRange(session, source);
            else HideHover();
        }

        private bool ComputeHover(PaintSession session, Texture source)
        {
            var w = session.Width;
            var h = session.Height;
            var length = w * h;
            if (_hoverMask == null || _hoverMask.Length != length) _hoverMask = new byte[length];
            else Array.Clear(_hoverMask, 0, length);

            switch (tool)
            {
                case Tool.UvIsland:
                {
                    var uv = UvIslands.Get(CachedUvSources(source), out _);
                    if (uv == null) return false;
                    var islands = uv.IslandsAt(_hoverUv);
                    if (islands.Count == 0) return false;
                    var mask = uv.Rasterize(islands, w, h, source.wrapModeU == TextureWrapMode.Repeat,
                        source.wrapModeV == TextureWrapMode.Repeat);
                    UvIslands.Dilate(mask, w, h, islandGrow);
                    _hoverMask = mask;
                    break;
                }

                case Tool.ColorSelect:
                {
                    // ホバーでは読み込まない(進捗バーが点滅する)。まだ読んでいなければ、そのテクスチャで 1 回だけ読む
                    var pixels = SourcePixels.Peek(source);
                    if (pixels == null)
                    {
                        if (_hoverPixelsTried == source && _hoverPixelsTriedHash == source.imageContentsHash) return false;
                        _hoverPixelsTried = source;
                        _hoverPixelsTriedHash = source.imageContentsHash;
                        pixels = LoadSourcePixels(source);
                        if (pixels == null) return false;
                    }

                    if (!HoverPixel(session, source, out var pixel)) return false;
                    if (!MaskOps.SelectSimilar(_hoverMask, w, h, pixels, pixel, tolerance, contiguous, 255)) return false;
                    break;
                }

                case Tool.SelectBrush:
                {
                    if (!HoverPixel(session, source, out var pixel)) return false;
                    FillDisc(_hoverMask, w, h, pixel, brushRadius, CurrentHardness());
                    break;
                }

                case Tool.Rect:
                {
                    var a = ToPixel(_rectStart, session);
                    var b = ToPixel(_rectEnd, session);
                    var points = new List<Vector2>
                    {
                        new Vector2(a.x, a.y), new Vector2(b.x, a.y), new Vector2(b.x, b.y), new Vector2(a.x, b.y),
                    };
                    MaskOps.FillPolygon(_hoverMask, w, h, points, 255);
                    break;
                }

                case Tool.Lasso:
                    MaskOps.FillPolygon(_hoverMask, w, h, _lasso.Select(p => ToPixel(p, session)).ToList(), 255);
                    break;

                default:
                    return false;
            }

            if (_hoverTexture != null && (_hoverTexture.width != w || _hoverTexture.height != h))
            {
                DestroyImmediate(_hoverTexture);
                _hoverTexture = null;
            }

            if (_hoverTexture == null)
            {
                _hoverTexture = new Texture2D(w, h, TextureFormat.R8, false, true)
                {
                    name = "Spoitable hover range",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            _hoverTexture.LoadRawTextureData(_hoverMask);
            _hoverTexture.Apply(false, false);
            return true;
        }

        /// <summary>ホバー中の位置のマスクの画素。画像の外なら false。</summary>
        private bool HoverPixel(PaintSession session, Texture source, out Vector2 pixel)
        {
            if (_hoverOrigin == HoverOrigin.Scene) return ScenePixel(_hoverUv, source, session, out pixel);
            pixel = new Vector2(_hoverUv.x * session.Width, _hoverUv.y * session.Height);
            return pixel.x >= 0f && pixel.y >= 0f && pixel.x < session.Width && pixel.y < session.Height;
        }

        /// <summary>
        /// シーンビューで当たった UV を、マスクの画素に直す。Repeat と Mirror の向きは折り返し、
        /// それ以外(Clamp)で 0〜1 の外なら false(テクスチャの端の色を引き伸ばしているだけなので塗らない)。
        /// </summary>
        private static bool ScenePixel(Vector2 uv, Texture source, PaintSession session, out Vector2 pixel)
        {
            pixel = default;
            if (!WrapCoordinate(ref uv.x, source.wrapModeU) || !WrapCoordinate(ref uv.y, source.wrapModeV)) return false;
            pixel = new Vector2(Mathf.Min(uv.x * session.Width, session.Width - 0.001f),
                Mathf.Min(uv.y * session.Height, session.Height - 0.001f));
            return true;
        }

        private static bool WrapCoordinate(ref float v, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat:
                    v -= Mathf.Floor(v);
                    return true;
                case TextureWrapMode.Mirror:
                {
                    var f = Mathf.Repeat(v, 2f);
                    v = f > 1f ? 2f - f : f;
                    return true;
                }
                default:
                    return v >= 0f && v <= 1f;
            }
        }

        /// <summary>円の範囲を濃さで書く(ペン選択のホバー表示用。MaskOps.Stamp と同じぼかし方)。</summary>
        private static void FillDisc(byte[] mask, int width, int height, Vector2 center, float radius, float hardness)
        {
            radius = Mathf.Max(0.5f, radius);
            var inner = radius * Mathf.Clamp01(hardness);
            var x0 = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            var x1 = Mathf.Min(width - 1, Mathf.CeilToInt(center.x + radius));
            var y0 = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            var y1 = Mathf.Min(height - 1, Mathf.CeilToInt(center.y + radius));
            for (var y = y0; y <= y1; y++)
            {
                var dy = y + 0.5f - center.y;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5f - center.x;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > radius) continue;
                    float falloff;
                    if (d <= inner) falloff = 1f;
                    else
                    {
                        var t = (d - inner) / Mathf.Max(1e-4f, radius - inner);
                        falloff = 1f - t * t * (3f - 2f * t);
                    }

                    mask[y * width + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(falloff) * 255f);
                }
            }
        }

        // ------------------------------------------------------------------ シーンビュー

        private void OnSceneGUIScene(SceneView view)
        {
            if (target == null) return;
            var e = Event.current;
            var hasContext = SceneContext(out var source, out var session);
            var interactive = hasContext && sceneInput && IsSceneTool(tool);

            if (interactive)
            {
                HandleSceneInput(view, source, session);
            }
            else
            {
                if (_sceneStroke) FinishSceneStroke();
                ClearSceneHit(session, source);
            }

            if (e.type != EventType.Repaint || !hasContext) return;
            DrawSceneOverlay(source, session);
            if (interactive && _sceneHasHit && _sceneHitView == view) DrawSceneBrush(session, source);
            if (sceneInput) DrawSceneNotice(view, session);
        }

        private void HandleSceneInput(SceneView view, Texture source, PaintSession session)
        {
            var e = Event.current;
            var id = GUIUtility.GetControlID(SceneInputHint, FocusType.Passive);
            _sceneControl = id;
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    // マウスがモデルの上にあるときだけ、クリックを Unity の選択より先に受け取る(外のクリックは Unity に渡す)。
                    // Alt を押しているときは視点の操作なので受けない
                    if (_sceneHasHit && _sceneHitView == view && session != null && !e.alt)
                    {
                        HandleUtility.AddDefaultControl(id);
                    }

                    // マウスがほかのウィンドウへ移ったら、当たりを忘れる
                    if (_sceneHasHit && _sceneHitView == view && !_sceneStroke &&
                        EditorWindow.mouseOverWindow != view)
                    {
                        ClearSceneHit(session, source);
                    }

                    break;

                case EventType.MouseMove:
                    // ボタンを押していない移動が来たら、離したのを取りこぼしている(視点の操作などを止めないよう放す)
                    if (_sceneStroke) FinishSceneStroke();
                    if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                    UpdateSceneHit(view, e.mousePosition, source, session);
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    if (_sceneStroke) FinishSceneStroke();
                    UpdateSceneHit(view, e.mousePosition, source, session);
                    if (!_sceneHasHit || session == null || HandleUtility.nearestControl != id) break;
                    // クリックで終わる道具も、MouseUp まで受け取って Unity の選択に渡さない
                    GUIUtility.hotControl = id;
                    e.Use();
                    BeginSceneTool(e, source, session);
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    if (_sceneStroke) DragSceneTool(view, e.mousePosition, source, session);
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    FinishSceneStroke();
                    e.Use();
                    break;

                case EventType.MouseLeaveWindow:
                    if (!_sceneStroke) ClearSceneHit(session, source);
                    break;
            }
        }

        private void UpdateSceneHit(SceneView view, Vector2 mouse, Texture source, PaintSession session)
        {
            var mesh = SceneMesh.Get(target, source);
            var had = _sceneHasHit;
            _sceneHasHit = mesh != null && mesh.Raycast(HandleUtility.GUIPointToWorldRay(mouse), out _sceneHit);
            _sceneHitView = view;
            if (_sceneHasHit)
            {
                _hoverOrigin = HoverOrigin.Scene;
                _hoverUv = _sceneHit.Uv;
            }
            else if (_hoverOrigin == HoverOrigin.Scene)
            {
                _hoverOrigin = HoverOrigin.None;
            }

            if (session != null) RefreshHoverRange(session, source);
            else HideHover();
            if (had || _sceneHasHit) view.Repaint(); // ブラシの円を動かす
        }

        private void ClearSceneHit(PaintSession session, Texture source)
        {
            if (!_sceneHasHit && _hoverOrigin != HoverOrigin.Scene) return;
            _sceneHasHit = false;
            if (_hoverOrigin == HoverOrigin.Scene)
            {
                _hoverOrigin = HoverOrigin.None;
                if (session != null && source != null) RefreshHoverRange(session, source);
                else HideHover();
                Repaint();
            }

            SceneView.RepaintAll();
        }

        private void BeginSceneTool(Event e, Texture source, PaintSession session)
        {
            _invertOp = e.control || e.command;
            var uv = _sceneHit.Uv;
            var w = session.Width;
            var h = session.Height;
            _startSelected = ScenePixel(uv, source, session, out var startPixel) && SelectedAt(session, startPixel);

            switch (tool)
            {
                case Tool.UvIsland:
                    // UV 島は 0〜1 の外の UV もそのまま探す(島の UV も Tiling/Offset を掛けただけで折り返していない)
                    _uv = UvIslands.Get(CachedUvSources(source), out _uvError);
                    SelectIsland(session, source, new Vector2(uv.x * w, uv.y * h));
                    break;

                case Tool.ColorSelect:
                {
                    if (!ScenePixel(uv, source, session, out var pixel))
                    {
                        SetMessage("テクスチャの範囲の外です");
                        break;
                    }

                    var pixels = LoadSourcePixels(source);
                    if (pixels == null) break;
                    var op = CurrentOp();
                    var working = PrepareSelection(session, op);
                    if (working == null) break;
                    if (MaskOps.SelectSimilar(working, w, h, pixels, pixel, tolerance, contiguous,
                            op == OpSubtract ? (byte)0 : (byte)255))
                    {
                        EditSelection(session, working, "色で選択");
                    }
                    else
                    {
                        SetMessage("透明な部分は選べません");
                    }

                    break;
                }

                case Tool.Bucket:
                {
                    if (!ScenePixel(uv, source, session, out var pixel))
                    {
                        SetMessage("テクスチャの範囲の外です");
                        break;
                    }

                    var pixels = LoadSourcePixels(source);
                    if (pixels == null) break;
                    MaskHistory.Begin(session);
                    var paint = BrushTarget();
                    if (MaskOps.SelectSimilar(session.Data, w, h, pixels, pixel, tolerance, contiguous, paint,
                            MaskSelection.For(w, h)))
                    {
                        if (!MaskOps.SameBytes(session.Snapshot, session.Data)) session.Changed();
                        MaskHistory.End(paint == 0 ? "バケツで消す" : "バケツで塗る");
                    }
                    else
                    {
                        MaskHistory.Cancel();
                        SetMessage("透明な部分は塗れません");
                    }

                    break;
                }

                case Tool.Pen:
                case Tool.Brush:
                case Tool.Eraser:
                {
                    MaskHistory.Begin(session);
                    var length = session.Data.Length;
                    EnsureStrokeBuffers(length);
                    Buffer.BlockCopy(session.Data, 0, _strokeBase, 0, length);
                    Array.Clear(_strokeAlpha, 0, length);
                    StartSceneStroke(session, BrushTarget());
                    SceneStamp(session, source, _sceneHit);
                    FlushSceneStamp(session);
                    break;
                }

                case Tool.SelectBrush:
                {
                    // キャンバスと同じく、作業用の複製を Set して、その配列だけを End まで書き換える
                    var subtract = SelectBrushSubtracts();
                    var working = PrepareSelection(session, subtract ? OpSubtract : OpAdd);
                    if (working == null) break;
                    MaskHistory.Begin(null);
                    MaskSelection.Set(working, w, h);
                    EnsureStrokeBuffers(working.Length);
                    Buffer.BlockCopy(working, 0, _strokeBase, 0, working.Length);
                    Array.Clear(_strokeAlpha, 0, working.Length);
                    StartSceneStroke(session, subtract ? (byte)0 : (byte)255);
                    SceneStamp(session, source, _sceneHit);
                    FlushSceneStamp(session);
                    break;
                }
            }

            Repaint();
        }

        private void StartSceneStroke(PaintSession session, byte strokeTarget)
        {
            _sceneStroke = true;
            _sceneStrokeTool = tool;
            _sceneStrokeTarget = strokeTarget;
            _sceneStrokeSession = session;
            _sceneLastMouse = Event.current.mousePosition;
            _sceneLastHasPixel = false;
            HideHover();
        }

        /// <summary>マウスの動きを画面上で刻んでレイを飛ばし、当たったところに塗る。</summary>
        private void DragSceneTool(SceneView view, Vector2 mouse, Texture source, PaintSession session)
        {
            if (session == null || session != _sceneStrokeSession) return;
            var mesh = SceneMesh.Get(target, source);
            if (mesh == null) return;
            var from = _sceneLastMouse;
            var steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(from, mouse) / 3f), 1, SceneMaxSamples);
            for (var s = 1; s <= steps; s++)
            {
                var p = Vector2.Lerp(from, mouse, (float)s / steps);
                if (mesh.Raycast(HandleUtility.GUIPointToWorldRay(p), out var hit))
                {
                    SceneStamp(session, source, hit);
                    _sceneHit = hit;
                    _sceneHasHit = true;
                    _sceneHitView = view;
                }
                else
                {
                    // モデルの外に出たら線を切る(戻ってきたところから描き直す)
                    _sceneLastHasPixel = false;
                    if (s == steps) _sceneHasHit = false;
                }
            }

            FlushSceneStamp(session);
            _sceneLastMouse = mouse;
            view.Repaint();
            Repaint();
        }

        /// <summary>
        /// 当たった位置に 1 回塗る。前の位置から、モデルの上で動いた分に見合う距離だけ UV でも動いていれば
        /// 同じ面の上とみなして線でつなぐ。UV の継ぎ目や繰り返しの境目をまたいだときは UV 上で大きく飛ぶので、
        /// つながずに点だけを置く(つなぐとテクスチャを横切る線になる)。
        /// </summary>
        private void SceneStamp(PaintSession session, Texture source, SceneHit hit)
        {
            if (!ScenePixel(hit.Uv, source, session, out var pixel))
            {
                _sceneLastHasPixel = false;
                return;
            }

            var w = session.Width;
            var h = session.Height;
            var worldPerPixel = hit.WorldPerUv / Mathf.Sqrt((float)w * h);
            var connect = false;
            if (_sceneLastHasPixel)
            {
                var minWorldPerPixel = Mathf.Min(worldPerPixel, _sceneLastWorldPerPixel);
                if (minWorldPerPixel > 0f)
                {
                    var uvDistance = Vector2.Distance(_sceneLastPixel, pixel);
                    var expected = Vector3.Distance(_sceneLastPoint, hit.Point) / minWorldPerPixel;
                    connect = uvDistance <= expected * 2f + 2f && uvDistance < 0.25f * Mathf.Max(w, h);
                }
            }

            var hardness = CurrentHardness();
            if (_sceneStrokeTool == Tool.SelectBrush)
            {
                var selection = MaskSelection.Current.Data;
                if (selection == null || selection.Length != w * h)
                {
                    _sceneLastHasPixel = false;
                    return;
                }

                var changed = connect
                    ? MaskOps.StampSegment(selection, _strokeBase, _strokeAlpha, w, h, _sceneLastPixel, pixel,
                        brushRadius, hardness, 1f, _sceneStrokeTarget)
                    : MaskOps.Stamp(selection, _strokeBase, _strokeAlpha, w, h, pixel, brushRadius, hardness, 1f,
                        _sceneStrokeTarget);
                if (changed) _sceneStampSelectionChanged = true;
            }
            else
            {
                var clip = MaskSelection.For(w, h);
                var changed = connect
                    ? MaskOps.StampSegment(session.Data, _strokeBase, _strokeAlpha, w, h, _sceneLastPixel, pixel,
                        brushRadius, hardness, brushStrength, _sceneStrokeTarget, clip)
                    : MaskOps.Stamp(session.Data, _strokeBase, _strokeAlpha, w, h, pixel, brushRadius, hardness,
                        brushStrength, _sceneStrokeTarget, clip);
                if (changed) _sceneStampMaskChanged = true;
            }

            _sceneLastPixel = pixel;
            _sceneLastPoint = hit.Point;
            _sceneLastWorldPerPixel = worldPerPixel;
            _sceneLastHasPixel = true;
        }

        /// <summary>
        /// SceneStamp で書き換えた分を、イベントごとに 1 回だけ作業用テクスチャへ送る
        /// (1 回のドラッグで何十回も送ると、テクスチャの転送とプレビューの描き直しが重なって重くなる)。
        /// </summary>
        private void FlushSceneStamp(PaintSession session)
        {
            if (_sceneStampMaskChanged && session != null) session.Changed();
            if (_sceneStampSelectionChanged) MaskSelection.Changed();
            _sceneStampMaskChanged = false;
            _sceneStampSelectionChanged = false;
        }

        /// <summary>シーンビューのストロークを終えて履歴に積む(何も変わっていなければ積まない)。</summary>
        private void FinishSceneStroke()
        {
            if (!_sceneStroke) return;
            var strokeTool = _sceneStrokeTool;
            var strokeTarget = _sceneStrokeTarget;
            ResetSceneStroke();
            if (strokeTool == Tool.SelectBrush)
            {
                var selection = MaskSelection.Current.Data;
                if (selection != null && MaskOps.IsEmpty(selection)) MaskSelection.Clear();
                MaskHistory.End(strokeTarget == 0 ? "選択範囲をなぞって削る" : "選択範囲をなぞって足す");
            }
            else
            {
                MaskHistory.End(strokeTarget == 0 ? "マスクを消す" : "マスクを塗る");
            }

            Repaint();
        }

        // ------------------------------------------------------------------ シーンビューの表示

        private void DrawSceneOverlay(Texture source, PaintSession session)
        {
            // 選択範囲は範囲選択の道具のときだけ出す(塗っているときは塗りに集中できるように)
            var showSelection = sceneShowSelection && IsSelectionTool(tool) && MaskSelection.Texture != null;
            var showHover = _hoverShown && _hoverTexture != null && session != null && _hoverSession == session;
            if (!showSelection && !showHover) return;
            var mesh = SceneMesh.Get(target, source);
            if (mesh == null || mesh.Parts.Count == 0) return;
            if (showSelection) SceneOverlay.Draw(mesh, MaskSelection.Texture, sceneSelectionColor, source);
            if (showHover) SceneOverlay.Draw(mesh, _hoverTexture, sceneHoverColor, source);
        }

        /// <summary>塗る道具のブラシの円。半径は、当たった三角形の UV の密度から画素をワールドの長さに直したもの。</summary>
        private void DrawSceneBrush(PaintSession session, Texture source)
        {
            if (session == null || source == null || !IsStrokeTool(tool)) return;
            var worldPerPixel = _sceneHit.WorldPerUv / Mathf.Sqrt((float)session.Width * session.Height);
            if (worldPerPixel <= 0f) return;
            var radius = brushRadius * worldPerPixel;
            var point = _sceneHit.Point;
            var normal = _sceneHit.Normal;

            var color = Handles.color;
            var zTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            var ctrl = _sceneStroke ? _invertOp : Event.current.control || Event.current.command;
            bool erase;
            if (tool == Tool.SelectBrush)
            {
                var selected = _sceneStroke
                    ? _startSelected
                    : ScenePixel(_sceneHit.Uv, source, session, out var pixel) && SelectedAt(session, pixel);
                erase = ctrl ? selected : selectBrushSubtract;
            }
            else
            {
                erase = (tool == Tool.Eraser) != ctrl;
            }
            Handles.color = Color.black;
            Handles.DrawWireDisc(point, normal, radius * 1.04f);
            Handles.color = erase ? new Color(0.6f, 0.8f, 1f) : Color.white;
            Handles.DrawWireDisc(point, normal, radius);
            var hardness = CurrentHardness();
            if (hardness < 1f)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.35f);
                Handles.DrawWireDisc(point, normal, radius * hardness);
            }

            Handles.zTest = zTest;
            Handles.color = color;
        }

        /// <summary>「3D 空間操作の有効化」が ON のとき、シーンビューの左下に今の操作を一言出す。</summary>
        private void DrawSceneNotice(SceneView view, PaintSession session)
        {
            string text;
            if (IsSceneTool(tool))
            {
                text = session == null ? "このレイヤーには塗りマスクがありません(エディタの「塗りマスクを作成」で作れます)" : SceneToolText();
            }
            else if (tool == Tool.Rect || tool == Tool.Lasso)
            {
                text = "矩形・投げ縄はキャンバスで使ってください";
            }
            else
            {
                return;
            }

            var content = new GUIContent("Spoitable Color Changer: " + text);
            Handles.BeginGUI();
            var size = SceneNotice.CalcSize(content);
            var height = view.camera != null ? view.camera.pixelRect.height / EditorGUIUtility.pixelsPerPoint : 300f;
            var rect = new Rect(8f, height - size.y - 16f, size.x + 12f, size.y + 4f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.65f));
            GUI.Label(new Rect(rect.x + 6f, rect.y + 2f, size.x, size.y), content, SceneNotice);
            Handles.EndGUI();
        }

        private string SceneToolText()
        {
            switch (tool)
            {
                case Tool.UvIsland:
                    return "クリックで UV 島を選択(Ctrl: 選択済みなら削る・それ以外は足す)";
                case Tool.ColorSelect:
                    return "クリックした色で選択(Ctrl: 選択済みなら削る・それ以外は足す)";
                case Tool.Bucket:
                    return "クリックでバケツ塗り(Ctrl 消す)";
                case Tool.Eraser:
                    return "ドラッグでモデルから消す(Ctrl 塗る)";
                case Tool.SelectBrush:
                    return (selectBrushSubtract ? "なぞって選択範囲を削る" : "なぞって選択範囲に足す") +
                           "(Ctrl: 選択済みから始めると削る・それ以外は足す)";
                default:
                    return "ドラッグでモデルに塗る(Ctrl 消す)";
            }
        }
    }
}
