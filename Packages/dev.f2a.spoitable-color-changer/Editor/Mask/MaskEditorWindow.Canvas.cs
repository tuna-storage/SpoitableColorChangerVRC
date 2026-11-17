using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    // 真ん中のキャンバス: 表示と、道具の入力
    internal sealed partial class MaskEditorWindow
    {
        private RenderTexture _recolored;
        private RecolorSettings _recoloredSettings;
        private Texture _recoloredSource;
        private Texture _recoloredPositions;
        private RenderTexture _display;

        private Rect _canvas;
        private Rect _imageRect;
        private Vector2 _mouse;

        private bool _panning;
        private bool _toolDragging;
        private bool _invertOp; // 押したときに Ctrl を押していた(塗る道具は塗る⇔消す、範囲選択は足す・削るを押した場所で決める)
        private bool _startSelected; // 押した場所がもう選ばれていた(Ctrl を押した範囲選択で、削るか足すかを決める)
        private Vector2 _lastPixel;
        private byte[] _strokeBase;
        private byte[] _strokeAlpha;
        private readonly List<Vector2> _lasso = new List<Vector2>();
        private Vector2 _rectStart;
        private Vector2 _rectEnd;
        private byte _strokeTarget;
        private PaintSession _strokeSession;

        private UvIslands _uv;
        private string _uvError;
        private List<UvSource> _uvSources;
        private SpoitableColorChanger _uvSourcesTarget;
        private Texture _uvSourcesTexture;
        private double _uvSourcesTime;

        // UV の線の画面座標。表示位置と大きさが変わらない間は使い回す
        private Vector3[] _uvLines;
        private Vector3[] _uvInnerLines;
        private UvIslands _uvLinesSource;
        private Rect _uvLinesImage;
        private Rect _uvLinesCanvas;
        private bool _uvLinesAll;

        private void OnLostFocus()
        {
            // ドラッグ中にフォーカスが外れると MouseUp が届かないので、操作を打ち切る
            if (!_toolDragging && !_panning) return;
            if (_toolDragging && IsStrokeTool(tool)) FinishStroke(_strokeSession);
            _gradientDrag = 0;
            _gradientNew = false;
            _strokeSession = null;
            _toolDragging = false;
            _panning = false;
            _lasso.Clear();
            GUIUtility.hotControl = 0;
            Repaint();
        }

        private void ReleaseTextures()
        {
            if (_recolored != null)
            {
                _recolored.Release();
                DestroyImmediate(_recolored);
            }

            if (_display != null)
            {
                _display.Release();
                DestroyImmediate(_display);
            }

            _recolored = null;
            _display = null;
            _recoloredSettings = null;
            _recoloredSource = null;
            _recoloredPositions = null;
        }

        private static bool IsStrokeTool(Tool t)
        {
            return t == Tool.Pen || t == Tool.Brush || t == Tool.Eraser || t == Tool.SelectBrush;
        }

        private void EnsureStrokeBuffers(int length)
        {
            if (_strokeBase != null && _strokeBase.Length == length && _strokeAlpha != null) return;
            _strokeBase = new byte[length];
            _strokeAlpha = new byte[length];
        }

        /// <summary>ペンは常に縁がくっきり。</summary>
        private float CurrentHardness()
        {
            return tool == Tool.Pen ? 1f : brushHardness;
        }

        private SourcePixels LoadSourcePixels(Texture source)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Spoitable Color Changer", "テクスチャの色を読み込んでいます", 0.5f);
                return SourcePixels.Get(source);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void CreateMask(Texture source)
        {
            var directory = "Assets";
            if (target.preset != null)
            {
                var presetPath = AssetDatabase.GetAssetPath(target.preset);
                if (!string.IsNullOrEmpty(presetPath))
                {
                    directory = (Path.GetDirectoryName(presetPath) ?? "Assets").Replace('\\', '/');
                }
            }

            var path = EditorUtility.SaveFilePanelInProject("塗りマスクを作成", $"{target.gameObject.name}_{layerName}_mask",
                "png", "塗りマスクの保存先を選んでください", directory);
            if (!string.IsNullOrEmpty(path))
            {
                var asset = MaskAsset.CreateBlank(path, source.width, source.height, source.wrapMode);
                using (var so = new SerializedObject(target))
                {
                    var element = MaskBindingGui.Ensure(so, layerName);
                    element.FindPropertyRelative(nameof(MaskBinding.paintMask)).objectReferenceValue = asset;
                    so.ApplyModifiedProperties();
                }
            }

            GUIUtility.ExitGUI();
        }

        // ------------------------------------------------------------------ 描画

        private void DrawCanvas(Rect canvas, Texture source, PaintSession session)
        {
            _canvas = canvas;
            // Layout イベントでは GetRect が仮の大きさを返すので、全体表示の計算はそれ以外で行う
            if (zoom <= 0f && Event.current.type != EventType.Layout && canvas.width > 2f && canvas.height > 2f)
            {
                zoom = Mathf.Min(canvas.width / source.width, canvas.height / source.height) * 0.95f;
                pan = Vector2.zero;
            }

            var size = new Vector2(source.width, source.height) * zoom;
            _imageRect = new Rect(canvas.center + pan - size * 0.5f, size);

            HandleCanvasInput(source, session);

            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(canvas, SccGui.CanvasColor);
            var display = UpdateDisplay(source, session);
            if (display != null)
            {
                GUI.BeginClip(canvas);
                var local = new Rect(_imageRect.position - canvas.position, _imageRect.size);
                GUI.DrawTexture(local, display, ScaleMode.StretchToFill, false);
                GUI.EndClip();
            }

            DrawUvOverlay();
            DrawToolOverlay(session);
            if (tool == Tool.Gradient) DrawGradientOverlay(source);
            if (tool == Tool.ColorPath) DrawColorPathOverlay(source);

            if (session == null && tool != Tool.Gradient && tool != Tool.ColorPath)
            {
                DrawCanvasNotice("このレイヤーには塗りマスクがありません", "右の「塗りマスクを作成」で作ると、ここで塗れます");
            }
        }

        private void DrawCanvasNotice(string title, string body)
        {
            var box = new Rect(_canvas.center.x - 170f, _canvas.center.y - 30f, 340f, 60f);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.6f));
            var titleStyle = new GUIStyle(SccGui.EmptyTitle) { normal = { textColor = Color.white } };
            var bodyStyle = new GUIStyle(SccGui.EmptyBody) { normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
            GUI.Label(new Rect(box.x, box.y + 6f, box.width, 24f), title, titleStyle);
            GUI.Label(new Rect(box.x, box.y + 30f, box.width, 22f), body, bodyStyle);
        }

        /// <summary>
        /// UV アイランドの元になるメッシュの一覧。Material 指定ではアバター全体を走査するので、イベントごとには求めない。
        /// 対象かテクスチャが変わるか、1 秒たったら求め直す(マテリアルの付け替えなどに追従するため)。
        /// </summary>
        private List<UvSource> CachedUvSources(Texture source)
        {
            var now = EditorApplication.timeSinceStartup;
            if (_uvSources == null || _uvSourcesTarget != target || _uvSourcesTexture != source ||
                now - _uvSourcesTime > 1.0)
            {
                _uvSources = TargetResolver.UvSources(target, source);
                _uvSourcesTarget = target;
                _uvSourcesTexture = source;
                _uvSourcesTime = now;
            }

            return _uvSources;
        }

        private static readonly Color UvLineColor = new Color(0.25f, 0.9f, 1f, 0.75f);
        private static readonly Color UvInnerLineColor = new Color(0.25f, 0.9f, 1f, 0.3f);
        private static readonly Color UvHoverColor = new Color(1f, 0.85f, 0.2f, 1f);

        /// <summary>UV の輪郭(と内側の辺)、UV 島の道具ではマウスの下のアイランドを描く。キャンバスの外は切り落とす。</summary>
        private void DrawUvOverlay()
        {
            if (_uv == null || _uv.TriangleCount == 0) return;
            var color = Handles.color;

            var show = uvDisplay == UvDisplay.Always || (uvDisplay == UvDisplay.IslandTool && tool == Tool.UvIsland);
            if (show)
            {
                if (_uvLines == null || _uvLinesSource != _uv || _uvLinesImage != _imageRect ||
                    _uvLinesCanvas != _canvas || _uvLinesAll != uvAllEdges)
                {
                    _uvLines = ToScreenLines(_uv.Outline, 0, _uv.Outline.Length);
                    _uvInnerLines = uvAllEdges ? ToScreenLines(_uv.Edges, 0, _uv.Edges.Length) : null;
                    _uvLinesSource = _uv;
                    _uvLinesImage = _imageRect;
                    _uvLinesCanvas = _canvas;
                    _uvLinesAll = uvAllEdges;
                }

                // すべての辺を薄く描いてから、輪郭を濃く重ねる
                if (_uvInnerLines != null && _uvInnerLines.Length > 0)
                {
                    Handles.color = UvInnerLineColor;
                    Handles.DrawLines(_uvInnerLines);
                }

                if (_uvLines.Length > 0)
                {
                    Handles.color = UvLineColor;
                    Handles.DrawLines(_uvLines);
                }
            }

            // シーンビューでモデルに乗っているときは、その UV 島を出す
            var sceneHover = SceneHoverUv(out var sceneUv);
            if (tool == Tool.UvIsland && !_toolDragging && (_canvas.Contains(_mouse) || sceneHover) &&
                _imageRect.width > 0f)
            {
                var uv = sceneHover
                    ? sceneUv
                    : new Vector2((_mouse.x - _imageRect.x) / _imageRect.width,
                        1f - (_mouse.y - _imageRect.y) / _imageRect.height);
                foreach (var island in _uv.IslandsAt(uv))
                {
                    _uv.OutlineRange(island, out var start, out var end);
                    var lines = ToScreenLines(_uv.Outline, start, end);
                    if (lines.Length == 0) continue;
                    Handles.color = UvHoverColor;
                    Handles.DrawLines(lines);
                }
            }

            Handles.color = color;
        }

        /// <summary>UV の線分 [start, end) を画面座標にし、キャンバスに収まる部分だけを返す。</summary>
        private Vector3[] ToScreenLines(Vector2[] segments, int start, int end)
        {
            var result = new List<Vector3>();
            for (var i = start; i + 1 < end; i += 2)
            {
                var a = new Vector2(_imageRect.x + segments[i].x * _imageRect.width,
                    _imageRect.y + (1f - segments[i].y) * _imageRect.height);
                var b = new Vector2(_imageRect.x + segments[i + 1].x * _imageRect.width,
                    _imageRect.y + (1f - segments[i + 1].y) * _imageRect.height);
                if (!ClipSegment(ref a, ref b, _canvas)) continue;
                result.Add(a);
                result.Add(b);
            }

            return result.ToArray();
        }

        /// <summary>線分を矩形で切り取る(Liang–Barsky)。矩形と重ならなければ false。</summary>
        private static bool ClipSegment(ref Vector2 a, ref Vector2 b, Rect rect)
        {
            var t0 = 0f;
            var t1 = 1f;
            var d = b - a;
            if (!ClipEdge(-d.x, a.x - rect.xMin, ref t0, ref t1)) return false;
            if (!ClipEdge(d.x, rect.xMax - a.x, ref t0, ref t1)) return false;
            if (!ClipEdge(-d.y, a.y - rect.yMin, ref t0, ref t1)) return false;
            if (!ClipEdge(d.y, rect.yMax - a.y, ref t0, ref t1)) return false;
            var origin = a;
            if (t1 < 1f) b = origin + d * t1;
            if (t0 > 0f) a = origin + d * t0;
            return true;
        }

        private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0f) return q >= 0f;
            var t = q / p;
            if (p < 0f)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }

            return true;
        }

        private Texture UpdateDisplay(Texture source, PaintSession session)
        {
            Texture baseTexture = source;
            if (viewMode == ViewMode.Recolored && target.preset != null)
            {
                var settings = RecolorSettings.Create(PresetConfig.Capture(target.preset), ComponentConfig.Capture(target));
                if (_recolored == null || _recolored.width != source.width || _recolored.height != source.height)
                {
                    if (_recolored != null)
                    {
                        _recolored.Release();
                        DestroyImmediate(_recolored);
                    }

                    _recolored = RecolorCore.CreateTarget(source);
                    _recoloredSettings = null;
                }

                var positions = settings.NeedsPositions ? Positions(source) : null;
                if (_recoloredSource != source || _recoloredSettings == null || !_recoloredSettings.Equals(settings) ||
                    !ReferenceEquals(_recoloredPositions, positions))
                {
                    try
                    {
                        RecolorCore.Render(source, settings, _recolored, positions);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }

                    _recoloredSettings = settings;
                    _recoloredSource = source;
                    _recoloredPositions = positions;
                }

                baseTexture = _recolored;
            }

            if (_display == null || _display.width != source.width || _display.height != source.height)
            {
                if (_display != null)
                {
                    _display.Release();
                    DestroyImmediate(_display);
                }

                _display = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                };
                _display.Create();
            }

            // グラデーションの道具では、塗りマスクの代わりにグラデーションマスクの濃さを重ねる
            // 色の経路の道具では、色の並びが見えるようにマスクを重ねない
            var overlay = tool == Tool.Gradient ? GradientView(source) : tool == Tool.ColorPath ? null : session?.Working;
            RecolorCore.BlitOverlay(baseTexture, overlay, MaskSelection.Texture, overlayColor, overlayOpacity, _display);
            return _display;
        }

        private void DrawToolOverlay(PaintSession session)
        {
            if (session == null) return;
            var color = Handles.color;

            if (IsStrokeTool(tool) && (_canvas.Contains(_mouse) || _toolDragging))
            {
                var radius = brushRadius / session.Width * _imageRect.width;
                // ドラッグ中はストローク開始時の向き、それ以外は今の Ctrl の状態(とマウスの下の選択範囲)で色を変える
                var ctrl = _toolDragging ? _invertOp : Event.current.control || Event.current.command;
                bool erase;
                if (tool == Tool.SelectBrush)
                {
                    var selected = _toolDragging ? _startSelected : SelectedAt(session, ToPixel(_mouse, session));
                    erase = ctrl ? selected : selectBrushSubtract;
                }
                else
                {
                    erase = (tool == Tool.Eraser) != ctrl;
                }
                Handles.color = Color.black;
                Handles.DrawWireDisc(_mouse, Vector3.forward, radius + 1f);
                Handles.color = erase ? new Color(0.6f, 0.8f, 1f) : Color.white;
                Handles.DrawWireDisc(_mouse, Vector3.forward, radius);
                var hardness = CurrentHardness();
                if (hardness < 1f)
                {
                    Handles.color = new Color(1f, 1f, 1f, 0.35f);
                    Handles.DrawWireDisc(_mouse, Vector3.forward, radius * hardness);
                }
            }

            if (_toolDragging && tool == Tool.Rect)
            {
                var r = Rect.MinMaxRect(Mathf.Min(_rectStart.x, _rectEnd.x), Mathf.Min(_rectStart.y, _rectEnd.y),
                    Mathf.Max(_rectStart.x, _rectEnd.x), Mathf.Max(_rectStart.y, _rectEnd.y));
                Handles.color = Color.white;
                Handles.DrawAAPolyLine(2f, new Vector3(r.xMin, r.yMin), new Vector3(r.xMax, r.yMin),
                    new Vector3(r.xMax, r.yMax), new Vector3(r.xMin, r.yMax), new Vector3(r.xMin, r.yMin));
            }

            if (_toolDragging && tool == Tool.Lasso && _lasso.Count > 1)
            {
                Handles.color = Color.white;
                var points = _lasso.Select(p => (Vector3)p).Concat(new[] { (Vector3)_lasso[0] }).ToArray();
                Handles.DrawAAPolyLine(2f, points);
            }

            Handles.color = color;
        }

        // ------------------------------------------------------------------ 入力

        private Vector2 ToPixel(Vector2 mouse, PaintSession session)
        {
            var u = (mouse.x - _imageRect.x) / _imageRect.width;
            var v = 1f - (mouse.y - _imageRect.y) / _imageRect.height;
            return new Vector2(u * session.Width, v * session.Height);
        }

        private void HandleCanvasInput(Texture source, PaintSession session)
        {
            var e = Event.current;
            var id = GUIUtility.GetControlID(FocusType.Passive);
            var inCanvas = _canvas.Contains(e.mousePosition);

            switch (e.GetTypeForControl(id))
            {
                case EventType.ScrollWheel:
                    if (!inCanvas) break;
                    var before = (e.mousePosition - _canvas.center - pan) / zoom;
                    var factor = e.delta.y > 0f ? 1f / 1.15f : 1.15f;
                    zoom = Mathf.Clamp(zoom * factor, 0.02f, 64f);
                    pan = e.mousePosition - _canvas.center - before * zoom;
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseMove:
                    _mouse = e.mousePosition;
                    if (inCanvas || _toolDragging) Repaint();
                    if (inCanvas) UpdateHover(session);
                    else _hover = "";
                    break;

                case EventType.MouseDown:
                    // ドラッグ中に別のボタンを押しても、今の操作を上書きしない(終わらないまま残るので)
                    if (GUIUtility.hotControl == id)
                    {
                        e.Use();
                        break;
                    }

                    if (!inCanvas) break;
                    _mouse = e.mousePosition;
                    if (e.button == 1 || e.button == 2 || (e.button == 0 && e.alt))
                    {
                        GUIUtility.hotControl = id;
                        _panning = true;
                        e.Use();
                        break;
                    }

                    if (e.button == 0 && tool == Tool.Gradient)
                    {
                        GUIUtility.hotControl = id;
                        GUIUtility.keyboardControl = 0;
                        BeginGradient(source);
                        e.Use();
                    }
                    else if (e.button == 0 && tool == Tool.ColorPath)
                    {
                        GUIUtility.hotControl = id;
                        GUIUtility.keyboardControl = 0;
                        BeginColorPath(source);
                        e.Use();
                    }
                    else if (e.button == 0 && session != null)
                    {
                        GUIUtility.hotControl = id;
                        GUIUtility.keyboardControl = 0;
                        BeginTool(e, source, session);
                        e.Use();
                    }

                    break;

                case EventType.KeyDown:
                    // 選んだグラデーションの点を消す(入力欄に文字を打っているときは除く)
                    if (tool == Tool.Gradient && !EditorGUIUtility.editingTextField && GUIUtility.keyboardControl == 0 &&
                        (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && DeleteSelectedPoint())
                    {
                        e.Use();
                        Repaint();
                    }
                    else if (tool == Tool.ColorPath && !EditorGUIUtility.editingTextField &&
                             GUIUtility.keyboardControl == 0 &&
                             (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) &&
                             DeleteSelectedPathPoint(source))
                    {
                        e.Use();
                        Repaint();
                    }

                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    _mouse = e.mousePosition;
                    if (_panning) pan += e.delta;
                    else if (tool == Tool.Gradient) DragGradient(source);
                    else if (tool == Tool.ColorPath) DragColorPath(source);
                    else if (session != null) DragTool(session);
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    _mouse = e.mousePosition;
                    if (_panning) _panning = false;
                    else if (tool == Tool.Gradient) EndGradient();
                    else if (tool == Tool.ColorPath) EndColorPath();
                    else if (session != null) EndTool(session);
                    e.Use();
                    Repaint();
                    break;
            }
        }

        private void UpdateHover(PaintSession session)
        {
            if (session == null || _imageRect.width <= 0f)
            {
                _hover = "";
                return;
            }

            var p = ToPixel(_mouse, session);
            var x = Mathf.FloorToInt(p.x);
            var y = Mathf.FloorToInt(p.y);
            if (x < 0 || y < 0 || x >= session.Width || y >= session.Height)
            {
                _hover = "";
                return;
            }

            var index = y * session.Width + x;
            _hover = $"({x}, {y})   マスク {session.Data[index] / 255f:F2}";
            var selection = MaskSelection.For(session.Width, session.Height);
            if (selection != null) _hover += $"   選択 {selection[index] / 255f:F2}";
            if (tool == Tool.UvIsland) _hover += $"   UV ({p.x / session.Width:F3}, {p.y / session.Height:F3})";
        }

        private void BeginTool(Event e, Texture source, PaintSession session)
        {
            _invertOp = e.control || e.command;
            var pixel = ToPixel(_mouse, session);
            _startSelected = SelectedAt(session, pixel);

            switch (tool)
            {
                case Tool.Pen:
                case Tool.Brush:
                case Tool.Eraser:
                {
                    MaskHistory.Begin(session);
                    var length = session.Data.Length;
                    EnsureStrokeBuffers(length);
                    Buffer.BlockCopy(session.Data, 0, _strokeBase, 0, length);
                    Array.Clear(_strokeAlpha, 0, length);
                    _strokeTarget = BrushTarget();
                    if (MaskOps.Stamp(session.Data, _strokeBase, _strokeAlpha, session.Width, session.Height, pixel,
                            brushRadius, CurrentHardness(), brushStrength, _strokeTarget,
                            MaskSelection.For(session.Width, session.Height)))
                    {
                        session.Changed();
                    }

                    _strokeSession = session;
                    _lastPixel = pixel;
                    _toolDragging = true;
                    break;
                }

                case Tool.Rect:
                    _rectStart = _mouse;
                    _rectEnd = _mouse;
                    _toolDragging = true;
                    break;

                case Tool.Lasso:
                    _lasso.Clear();
                    _lasso.Add(_mouse);
                    _toolDragging = true;
                    break;

                case Tool.Bucket:
                {
                    var pixels = LoadSourcePixels(source);
                    if (pixels == null) break;
                    MaskHistory.Begin(session);
                    var paint = BrushTarget();
                    if (MaskOps.SelectSimilar(session.Data, session.Width, session.Height, pixels, pixel, tolerance,
                            contiguous, paint, MaskSelection.For(session.Width, session.Height)))
                    {
                        if (!MaskOps.SameBytes(session.Snapshot, session.Data)) session.Changed();
                        MaskHistory.End(paint == 0 ? "バケツで消す" : "バケツで塗る");
                    }
                    else
                    {
                        MaskHistory.Cancel();
                        SetMessage("画像の外や透明な部分は塗れません");
                    }

                    break;
                }

                case Tool.ColorSelect:
                {
                    var pixels = LoadSourcePixels(source);
                    if (pixels == null) break;
                    var op = CurrentOp();
                    var working = PrepareSelection(session, op);
                    if (working == null) break;
                    if (MaskOps.SelectSimilar(working, session.Width, session.Height, pixels, pixel, tolerance,
                            contiguous, op == OpSubtract ? (byte)0 : (byte)255))
                    {
                        EditSelection(session, working, "色で選択");
                    }
                    else
                    {
                        SetMessage("画像の外や透明な部分は選べません");
                    }

                    break;
                }

                case Tool.SelectBrush:
                {
                    // 選択範囲だけを書き換える。マスクには触れないので、プレビューも描き直さない
                    var subtract = SelectBrushSubtracts();
                    var working = PrepareSelection(session, subtract ? OpSubtract : OpAdd);
                    if (working == null) break;
                    MaskHistory.Begin(null);
                    MaskSelection.Set(working, session.Width, session.Height);
                    EnsureStrokeBuffers(working.Length);
                    Buffer.BlockCopy(working, 0, _strokeBase, 0, working.Length);
                    Array.Clear(_strokeAlpha, 0, working.Length);
                    _strokeTarget = subtract ? (byte)0 : (byte)255;
                    _strokeSession = session;
                    if (MaskOps.Stamp(working, _strokeBase, _strokeAlpha, session.Width, session.Height, pixel,
                            brushRadius, CurrentHardness(), 1f, _strokeTarget))
                    {
                        MaskSelection.Changed();
                    }

                    _lastPixel = pixel;
                    _toolDragging = true;
                    break;
                }

                case Tool.UvIsland:
                    SelectIsland(session, source, pixel);
                    break;
            }
        }

        private void SelectIsland(PaintSession session, Texture source, Vector2 pixel)
        {
            if (_uv == null || _uv.TriangleCount == 0)
            {
                SetMessage(_uvError ?? "このテクスチャを使うメッシュが見つかりません");
                return;
            }

            var op = CurrentOp();
            var islands = _uv.IslandsAt(new Vector2(pixel.x / session.Width, pixel.y / session.Height));
            if (islands.Count == 0)
            {
                // 矩形と同じく、何もないところを「新規」でクリックしたら選択を解除する
                if (op == OpNew && MaskSelection.Exists) EditSelection(session, null, "選択を解除");
                else SetMessage("ここに UV アイランドはありません");
                return;
            }

            var working = PrepareSelection(session, op);
            if (working == null) return;
            var mask = _uv.Rasterize(islands, session.Width, session.Height,
                source.wrapModeU == TextureWrapMode.Repeat, source.wrapModeV == TextureWrapMode.Repeat);
            UvIslands.Dilate(mask, session.Width, session.Height, islandGrow);
            var value = op == OpSubtract ? (byte)0 : (byte)255;
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0) working[i] = value;
            }

            EditSelection(session, working, "UV アイランドを選択");
            SetMessage(islands.Count == 1
                ? "アイランドを選択しました"
                : $"重なっているアイランド {islands.Count} 個をまとめて選択しました");
        }

        private void DragTool(PaintSession session)
        {
            if (!_toolDragging) return;
            switch (tool)
            {
                case Tool.Pen:
                case Tool.Brush:
                case Tool.Eraser:
                {
                    var pixel = ToPixel(_mouse, session);
                    if (MaskOps.StampSegment(session.Data, _strokeBase, _strokeAlpha, session.Width, session.Height,
                            _lastPixel, pixel, brushRadius, CurrentHardness(), brushStrength, _strokeTarget,
                            MaskSelection.For(session.Width, session.Height)))
                    {
                        session.Changed();
                    }

                    _lastPixel = pixel;
                    break;
                }

                case Tool.SelectBrush:
                {
                    // ストロークの始めに Set した作業用の配列(まだ履歴に積んでいない)だけを書き換える
                    var selection = MaskSelection.Current.Data;
                    if (selection == null || selection.Length != session.Data.Length) break;
                    var pixel = ToPixel(_mouse, session);
                    if (MaskOps.StampSegment(selection, _strokeBase, _strokeAlpha, session.Width, session.Height,
                            _lastPixel, pixel, brushRadius, CurrentHardness(), 1f, _strokeTarget))
                    {
                        MaskSelection.Changed();
                    }

                    _lastPixel = pixel;
                    break;
                }

                case Tool.Rect:
                    _rectEnd = _mouse;
                    break;

                case Tool.Lasso:
                    if (Vector2.Distance(_lasso[_lasso.Count - 1], _mouse) >= 2f) _lasso.Add(_mouse);
                    break;
            }
        }

        private void EndTool(PaintSession session)
        {
            if (!_toolDragging) return;
            _toolDragging = false;

            switch (tool)
            {
                case Tool.Rect:
                {
                    var a = ToPixel(_rectStart, session);
                    var b = ToPixel(_rectEnd, session);
                    var points = new List<Vector2>
                    {
                        new Vector2(a.x, a.y), new Vector2(b.x, a.y), new Vector2(b.x, b.y), new Vector2(a.x, b.y),
                    };
                    FillSelection(session, points, "矩形で選択");
                    break;
                }

                case Tool.Lasso:
                    if (_lasso.Count >= 3)
                    {
                        FillSelection(session, _lasso.Select(p => ToPixel(p, session)).ToList(), "投げ縄で選択");
                    }

                    _lasso.Clear();
                    break;

                case Tool.Pen:
                case Tool.Brush:
                case Tool.Eraser:
                case Tool.SelectBrush:
                    FinishStroke(_strokeSession ?? session);
                    break;
            }
        }

        /// <summary>
        /// ストロークの後始末。ペン選択で選択範囲が空になったら選択解除として扱う。
        /// 何も変わらなかった(画像の外をなぞっただけなど)なら、履歴に残さない。
        /// </summary>
        private void FinishStroke(PaintSession session)
        {
            _strokeSession = null;
            if (session == null) return;
            if (tool == Tool.SelectBrush)
            {
                var selection = MaskSelection.Current.Data;
                if (selection != null && MaskOps.IsEmpty(selection)) MaskSelection.Clear();
                MaskHistory.End(_strokeTarget == 0 ? "選択範囲をなぞって削る" : "選択範囲をなぞって足す");
                return;
            }

            MaskHistory.End(_strokeTarget == 0 ? "マスクを消す" : "マスクを塗る");
        }

        /// <summary>
        /// 囲んだ範囲で選択範囲を作る。面積がない(クリックしただけ)ときは、新規なら選択を解除し、それ以外は何もしない。
        /// </summary>
        private void FillSelection(PaintSession session, List<Vector2> points, string undoName)
        {
            var op = CurrentOp();
            var min = new Vector2(points.Min(p => p.x), points.Min(p => p.y));
            var max = new Vector2(points.Max(p => p.x), points.Max(p => p.y));
            if (max.x - min.x < 1f || max.y - min.y < 1f)
            {
                if (op == OpNew && MaskSelection.Exists) EditSelection(session, null, "選択を解除");
                return;
            }

            var working = PrepareSelection(session, op);
            if (working == null) return;
            MaskOps.FillPolygon(working, session.Width, session.Height, points, op == OpSubtract ? (byte)0 : (byte)255);
            EditSelection(session, working, undoName);
        }

        /// <summary>
        /// 修飾キーを加味した、選択の操作(新規・追加・削除)。Ctrl を押していれば、押した場所が選択済みなら削り、
        /// それ以外は足す(押した瞬間に決める)。
        /// </summary>
        private int CurrentOp()
        {
            if (_invertOp) return _startSelected ? OpSubtract : OpAdd;
            return selectionOp;
        }

        /// <summary>ペン選択で削るか(Ctrl なら押した場所で決め、それ以外は道具の欄の追加・削除のとおり)。</summary>
        private bool SelectBrushSubtracts()
        {
            return _invertOp ? _startSelected : selectBrushSubtract;
        }

        /// <summary>その画素がもう選ばれているか(選択範囲の値が半分以上)。選択範囲がない・画像の外なら false。</summary>
        private static bool SelectedAt(PaintSession session, Vector2 pixel)
        {
            if (session == null) return false;
            var selection = MaskSelection.For(session.Width, session.Height);
            if (selection == null) return false;
            var x = Mathf.FloorToInt(pixel.x);
            var y = Mathf.FloorToInt(pixel.y);
            if (x < 0 || y < 0 || x >= session.Width || y >= session.Height) return false;
            return selection[y * session.Width + x] >= 128;
        }

        /// <summary>選択範囲を書き換える土台(今の選択範囲の複製か、空の配列)。削除しようにも選択がなければ null。</summary>
        private byte[] PrepareSelection(PaintSession session, int op)
        {
            var current = MaskSelection.For(session.Width, session.Height);
            switch (op)
            {
                case OpAdd:
                    return current != null ? (byte[])current.Clone() : new byte[session.Data.Length];
                case OpSubtract:
                    if (current == null)
                    {
                        SetMessage("選択範囲がないので、削るものがありません");
                        return null;
                    }

                    return (byte[])current.Clone();
                default:
                    return new byte[session.Data.Length];
            }
        }

        private byte BrushTarget()
        {
            var erase = tool == Tool.Eraser;
            if (_invertOp) erase = !erase;
            return erase ? (byte)0 : (byte)255;
        }
    }
}
