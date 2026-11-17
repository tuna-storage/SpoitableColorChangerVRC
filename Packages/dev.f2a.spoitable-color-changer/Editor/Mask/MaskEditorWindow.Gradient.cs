using System;
using UnityEditor;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    // グラデーションマスク: キャンバスとシーンビューでの操作、右パネルのマスクタブの欄
    internal sealed partial class MaskEditorWindow
    {
        private const float GradientHandleRadius = 7f;

        private static readonly GradientMaskMode[] GradientModes =
        {
            GradientMaskMode.None, GradientMaskMode.TextureLinear, GradientMaskMode.TextureRadial,
            GradientMaskMode.TexturePoints, GradientMaskMode.ModelDirection, GradientMaskMode.ModelPoints,
        };

        private static readonly GUIContent[] GradientModeLabels =
        {
            new GUIContent("なし", "グラデーションマスクを使わない"),
            new GUIContent("直線", "テクスチャ上で、始点から終点へ濃くなる"),
            new GUIContent("円形", "テクスチャ上で、中心から外へ濃くなる"),
            new GUIContent("点", "テクスチャ上に置いた点の値を補間する"),
            new GUIContent("方向(モデル)", "モデル上で、始点から終点の向きに濃くなる"),
            new GUIContent("点(モデル)", "モデル上に置いた点の値を補間する"),
        };

        private static readonly Vector2 NoUv = new Vector2(-1f, -1f);

        [SerializeField] private float newPointValue = 1f;

        private int _gradientPoint = -1; // 選んでいる点
        private string _gradientPointLayer;
        private int _gradientDrag; // 0: なし, 1: 始点, 2: 終点, 3: 点
        private RenderTexture _gradientView;

        // 何もないところからの引き直し。少しドラッグするまでは書き込まない(クリックだけで今のグラデを壊さないため)
        private bool _gradientNew;
        private bool _gradientNewWritten;
        private Vector2 _gradientNewUv;
        private Vector3 _gradientNewPosition;

        /// <summary>選んでいる点を忘れる(対象・テクスチャの切り替えや Undo のあとは、番号が別の点を指すことがある)。</summary>
        private void ForgetGradientPoint()
        {
            _gradientPoint = -1;
            _gradientPointLayer = null;
        }

        // ------------------------------------------------------------------ 値の読み書き

        /// <summary>今のレイヤーのグラデーションマスクの設定(なければ None)。</summary>
        private GradientMaskConfig CurrentGradient()
        {
            return GradientMaskConfig.Capture(target != null ? target.FindMask(layerName)?.gradient : null);
        }

        /// <summary>今のレイヤーの gradient の SerializedProperty。create なら割り当てを作る。</summary>
        private SerializedProperty GradientProperty(bool create)
        {
            var component = ComponentObject();
            var element = create ? MaskBindingGui.Ensure(component, layerName) : MaskBindingGui.Find(component, layerName);
            return element?.FindPropertyRelative(nameof(MaskBinding.gradient));
        }

        private static SerializedProperty Prop(SerializedProperty gradient, string name)
        {
            return gradient.FindPropertyRelative(name);
        }

        private static GradientMaskMode ModeOf(SerializedProperty gradient)
        {
            return gradient != null
                ? (GradientMaskMode)Prop(gradient, nameof(GradientMask.mode)).intValue
                : GradientMaskMode.None;
        }

        private static bool IsModelMode(GradientMaskMode mode)
        {
            return mode == GradientMaskMode.ModelDirection || mode == GradientMaskMode.ModelPoints;
        }

        private static bool IsPointsMode(GradientMaskMode mode)
        {
            return mode == GradientMaskMode.TexturePoints || mode == GradientMaskMode.ModelPoints;
        }

        private void SetMode(SerializedProperty gradient, GradientMaskMode mode, Texture source)
        {
            var previous = ModeOf(gradient);
            Prop(gradient, nameof(GradientMask.mode)).intValue = (int)mode;
            if (mode == GradientMaskMode.None) return;
            // テクスチャ上(UV)とモデル上(メートル)では座標の意味が違うので、行き来したら初期位置に戻す
            if (IsModelMode(previous) != IsModelMode(mode) || previous == GradientMaskMode.None)
            {
                Points(gradient).arraySize = 0;
                if (IsModelMode(mode)) SetModelAxis(gradient, source, Vector3.down);
                else SetTextureDefault(gradient);
            }
        }

        private static void SetTextureDefault(SerializedProperty gradient)
        {
            Prop(gradient, nameof(GradientMask.start)).vector3Value = new Vector3(0.5f, 1f, 0f);
            Prop(gradient, nameof(GradientMask.end)).vector3Value = new Vector3(0.5f, 0f, 0f);
        }

        /// <summary>モデル上の方向を、対象のメッシュの範囲の端から端へ、direction の向きに置き直す。</summary>
        private void SetModelAxis(SerializedProperty gradient, Texture source, Vector3 direction)
        {
            var bounds = ModelBounds(source);
            var center = bounds.center;
            var half = Vector3.Scale(bounds.extents, new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y),
                Mathf.Abs(direction.z)));
            var d = new Vector3(Mathf.Sign(direction.x) * half.x, Mathf.Sign(direction.y) * half.y,
                Mathf.Sign(direction.z) * half.z);
            Prop(gradient, nameof(GradientMask.start)).vector3Value = center - d;
            Prop(gradient, nameof(GradientMask.end)).vector3Value = center + d;
            Prop(gradient, nameof(GradientMask.startUv)).vector2Value = NoUv;
            Prop(gradient, nameof(GradientMask.endUv)).vector2Value = NoUv;
        }

        /// <summary>このテクスチャを使うメッシュの範囲(アバターのルートから見た位置)。求められなければ原点まわり。</summary>
        private Bounds ModelBounds(Texture source)
        {
            var root = TargetResolver.AvatarRootOf(target);
            var first = true;
            var bounds = new Bounds(Vector3.up, Vector3.one * 0.2f);
            foreach (var (renderer, _) in TargetResolver.PositionSources(target, source))
            {
                Bounds local;
                var toAvatar = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    var baked = new Mesh();
                    try
                    {
                        skinned.BakeMesh(baked, true);
                        local = baked.bounds;
                    }
                    catch (Exception)
                    {
                        local = skinned.sharedMesh != null ? skinned.sharedMesh.bounds : new Bounds();
                    }
                    finally
                    {
                        DestroyImmediate(baked);
                    }
                }
                else
                {
                    var mesh = TargetResolver.MeshOf(renderer);
                    if (mesh == null) continue;
                    local = mesh.bounds;
                }

                for (var i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var p = toAvatar.MultiplyPoint3x4(corner);
                    if (first)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        first = false;
                    }
                    else
                    {
                        bounds.Encapsulate(p);
                    }
                }
            }

            return bounds;
        }

        private static SerializedProperty Points(SerializedProperty gradient)
        {
            return Prop(gradient, nameof(GradientMask.points));
        }

        private static SerializedProperty PointField(SerializedProperty gradient, int index, string name)
        {
            return Points(gradient).ArrayElement(index).FindPropertyRelative(name);
        }

        // ------------------------------------------------------------------ 表示用のテクスチャ上の位置(モデル上の種類)

        /// <summary>モデル上の種類の表示用の位置が、今のテクスチャで拾ったものか。</summary>
        private static bool HintsMatch(SerializedProperty gradient, Texture source)
        {
            return Prop(gradient, nameof(GradientMask.uvHintTexture)).objectReferenceValue == source;
        }

        /// <summary>
        /// これから source で拾った表示用の位置を書き込む。別のテクスチャで拾った位置が残っていたら、それは
        /// 今のテクスチャでは別の場所を指すので、すべて「分からない」に戻す。
        /// </summary>
        private static void UseHintTexture(SerializedProperty gradient, Texture source)
        {
            var texture = Prop(gradient, nameof(GradientMask.uvHintTexture));
            if (texture.objectReferenceValue == source) return;
            Prop(gradient, nameof(GradientMask.startUv)).vector2Value = NoUv;
            Prop(gradient, nameof(GradientMask.endUv)).vector2Value = NoUv;
            var points = Points(gradient);
            for (var i = 0; i < points.arraySize; i++) PointField(gradient, i, nameof(GradientMaskPoint.uv)).vector2Value = NoUv;
            texture.objectReferenceValue = source;
        }

        /// <summary>キャンバスに出す始点(start = true)か終点の位置。分からなければ false。</summary>
        private static bool EndpointUv(SerializedProperty gradient, GradientMaskMode mode, bool start, Texture source,
            out Vector2 uv)
        {
            if (mode != GradientMaskMode.ModelDirection)
            {
                // テクスチャ上の種類は位置そのもの(画像の外を指していても出す)
                uv = Prop(gradient, start ? nameof(GradientMask.start) : nameof(GradientMask.end)).vector3Value;
                return true;
            }

            uv = Prop(gradient, start ? nameof(GradientMask.startUv) : nameof(GradientMask.endUv)).vector2Value;
            return uv.x >= 0f && HintsMatch(gradient, source);
        }

        /// <summary>キャンバスに出す点の位置(テクスチャ上は位置そのもの、モデル上は置いたときの UV)。分からなければ null。</summary>
        private static Vector2? PointUv(GradientMaskMode mode, SerializedProperty gradient, int index, Texture source)
        {
            if (mode == GradientMaskMode.TexturePoints)
            {
                return PointField(gradient, index, nameof(GradientMaskPoint.position)).vector3Value;
            }

            var uv = PointField(gradient, index, nameof(GradientMaskPoint.uv)).vector2Value;
            return uv.x < 0f || !HintsMatch(gradient, source) ? (Vector2?)null : uv;
        }

        /// <summary>今のテクスチャの位置マップ(モデル上の種類で使う)。求められなければ null。</summary>
        private RenderTexture Positions(Texture source)
        {
            if (target == null || source == null) return null;
            try
            {
                return PositionMap.Get(target, source);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        // ------------------------------------------------------------------ 座標

        private Vector2 ToUv(Vector2 mouse)
        {
            return new Vector2((mouse.x - _imageRect.x) / _imageRect.width, 1f - (mouse.y - _imageRect.y) / _imageRect.height);
        }

        private Vector2 UvToScreen(Vector2 uv)
        {
            return new Vector2(_imageRect.x + uv.x * _imageRect.width, _imageRect.y + (1f - uv.y) * _imageRect.height);
        }

        /// <summary>キャンバスの uv が、モデル上のどこか。モデルの表面でなければ false(理由はステータスバーに出す)。</summary>
        private bool ModelPositionAt(Texture source, Vector2 uv, out Vector3 position)
        {
            position = Vector3.zero;
            var map = Positions(source);
            if (map == null)
            {
                SetMessage("このテクスチャを使うメッシュが見つかりません");
                return false;
            }

            if (!PositionMap.Read(map, uv, out position, out var covered) || !covered)
            {
                SetMessage("ここはモデルの表面ではありません(UV のない場所)");
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ キャンバスでの入力

        private void BeginGradient(Texture source)
        {
            // 割り当てを作るのは、実際に書き込むときだけ(クリックしただけで割り当てを増やさない)
            var gradient = GradientProperty(false);
            var mode = ModeOf(gradient);
            var uv = ToUv(_mouse);
            _gradientNew = false;
            _gradientNewWritten = false;
            _gradientDrag = 0;

            switch (mode)
            {
                case GradientMaskMode.None:
                case GradientMaskMode.TextureLinear:
                case GradientMaskMode.TextureRadial:
                case GradientMaskMode.ModelDirection:
                {
                    if (mode != GradientMaskMode.None)
                    {
                        if (EndpointUv(gradient, mode, false, source, out var endUv) &&
                            Vector2.Distance(UvToScreen(endUv), _mouse) <= GradientHandleRadius + 2f)
                        {
                            _gradientDrag = 2;
                        }
                        else if (EndpointUv(gradient, mode, true, source, out var startUv) &&
                                 Vector2.Distance(UvToScreen(startUv), _mouse) <= GradientHandleRadius + 2f)
                        {
                            _gradientDrag = 1;
                        }
                    }

                    if (_gradientDrag == 0)
                    {
                        // 何もないところからドラッグすると引き直す。始点はここで決めておき、書き込むのは動かしてから
                        if (mode == GradientMaskMode.ModelDirection && !ModelPositionAt(source, uv, out _gradientNewPosition))
                        {
                            break;
                        }

                        _gradientNewUv = uv;
                        _gradientNew = true;
                        _gradientDrag = 2;
                    }

                    break;
                }

                case GradientMaskMode.TexturePoints:
                case GradientMaskMode.ModelPoints:
                {
                    var hit = HitPoint(gradient, mode, source);
                    if (hit >= 0 && (Event.current.control || Event.current.command))
                    {
                        Points(gradient).DeleteArrayElementAtIndex(hit);
                        ForgetGradientPoint();
                        break;
                    }

                    if (hit < 0)
                    {
                        var points = Points(gradient);
                        if (points.arraySize >= GradientMask.MaxPoints)
                        {
                            SetMessage($"点は {GradientMask.MaxPoints} 個までです");
                            break;
                        }

                        Vector3 position = uv;
                        if (mode == GradientMaskMode.ModelPoints)
                        {
                            if (!ModelPositionAt(source, uv, out position)) break;
                            UseHintTexture(gradient, source);
                        }

                        points.arraySize++;
                        hit = points.arraySize - 1;
                        PointField(gradient, hit, nameof(GradientMaskPoint.position)).vector3Value = position;
                        PointField(gradient, hit, nameof(GradientMaskPoint.value)).floatValue = newPointValue;
                        PointField(gradient, hit, nameof(GradientMaskPoint.uv)).vector2Value =
                            mode == GradientMaskMode.ModelPoints ? uv : NoUv;
                    }

                    _gradientPoint = hit;
                    _gradientPointLayer = layerName;
                    _gradientDrag = 3;
                    break;
                }
            }

            ComponentObject().ApplyModifiedProperties();
            if (IsModelMode(mode)) SceneView.RepaintAll();
            _toolDragging = _gradientDrag != 0;
        }

        /// <summary>始点(start = true)か終点を uv の位置にする。モデル上でモデルの表面でなければ false(変えない)。</summary>
        private bool SetEndpoint(SerializedProperty gradient, GradientMaskMode mode, Texture source, Vector2 uv, bool start)
        {
            if (mode == GradientMaskMode.ModelDirection)
            {
                if (!ModelPositionAt(source, uv, out var position)) return false;
                UseHintTexture(gradient, source);
                Prop(gradient, start ? nameof(GradientMask.start) : nameof(GradientMask.end)).vector3Value = position;
                Prop(gradient, start ? nameof(GradientMask.startUv) : nameof(GradientMask.endUv)).vector2Value = uv;
                return true;
            }

            Prop(gradient, start ? nameof(GradientMask.start) : nameof(GradientMask.end)).vector3Value = uv;
            return true;
        }

        private void DragGradient(Texture source)
        {
            if (_gradientDrag == 0) return;
            var uv = ToUv(_mouse);
            var modeCreated = false;

            if (_gradientNew && !_gradientNewWritten)
            {
                // 少し動かすまでは、クリックしただけとして何も書かない
                if (Vector2.Distance(UvToScreen(_gradientNewUv), _mouse) < 3f) return;
                var created = GradientProperty(true);
                var current = ModeOf(created);
                if (current == GradientMaskMode.None)
                {
                    SetMode(created, GradientMaskMode.TextureLinear, source);
                    current = GradientMaskMode.TextureLinear;
                    modeCreated = true;
                }

                if (current == GradientMaskMode.ModelDirection)
                {
                    UseHintTexture(created, source);
                    Prop(created, nameof(GradientMask.start)).vector3Value = _gradientNewPosition;
                    Prop(created, nameof(GradientMask.startUv)).vector2Value = _gradientNewUv;
                }
                else
                {
                    Prop(created, nameof(GradientMask.start)).vector3Value = _gradientNewUv;
                }

                _gradientNewWritten = true;
            }

            var gradient = GradientProperty(false);
            if (gradient == null) return;
            var mode = ModeOf(gradient);
            switch (_gradientDrag)
            {
                case 1:
                case 2:
                    // モデル上でモデルの表面から外れたら、最後の位置のままにする
                    SetEndpoint(gradient, mode, source, uv, _gradientDrag == 1);
                    break;
                case 3:
                    if (_gradientPoint < 0 || _gradientPoint >= Points(gradient).arraySize) break;
                    if (mode == GradientMaskMode.ModelPoints)
                    {
                        if (!ModelPositionAt(source, uv, out var position)) break;
                        UseHintTexture(gradient, source);
                        PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.position)).vector3Value = position;
                        PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.uv)).vector2Value = uv;
                    }
                    else
                    {
                        PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.position)).vector3Value = uv;
                    }

                    break;
            }

            ComponentObject().ApplyModifiedProperties();
            if (IsModelMode(mode)) SceneView.RepaintAll();
            if (modeCreated)
            {
                // なしから直線になると、このあとに描くマスクタブの欄が増える(この Layout では数えていない)ので、
                // このフレームはここまで。つかんだままなので、次のイベントからも引き続き引ける
                Repaint();
                GUIUtility.ExitGUI();
            }
        }

        private void EndGradient()
        {
            _gradientDrag = 0;
            _gradientNew = false;
            _gradientNewWritten = false;
            _toolDragging = false;
        }

        /// <summary>マウスの下の点(選んでいる点を優先)。なければ -1。モデル上の点は、置いた UV が分かるものだけ。</summary>
        private int HitPoint(SerializedProperty gradient, GradientMaskMode mode, Texture source)
        {
            var points = Points(gradient);
            var limit = GradientHandleRadius + 2f;
            if (_gradientPointLayer == layerName && _gradientPoint >= 0 && _gradientPoint < points.arraySize)
            {
                var uv = PointUv(mode, gradient, _gradientPoint, source);
                if (uv.HasValue && Vector2.Distance(UvToScreen(uv.Value), _mouse) <= limit) return _gradientPoint;
            }

            var best = -1;
            var bestDistance = limit;
            for (var i = 0; i < points.arraySize; i++)
            {
                var uv = PointUv(mode, gradient, i, source);
                if (!uv.HasValue) continue;
                var d = Vector2.Distance(UvToScreen(uv.Value), _mouse);
                if (d > bestDistance) continue;
                bestDistance = d;
                best = i;
            }

            return best;
        }

        /// <summary>選んでいる点を消す。消したら true。</summary>
        private bool DeleteSelectedPoint()
        {
            var gradient = GradientProperty(false);
            if (gradient == null || !IsPointsMode(ModeOf(gradient)) || _gradientPointLayer != layerName) return false;
            var points = Points(gradient);
            if (_gradientPoint < 0 || _gradientPoint >= points.arraySize) return false;
            points.DeleteArrayElementAtIndex(_gradientPoint);
            ForgetGradientPoint();
            ComponentObject().ApplyModifiedProperties();
            SceneView.RepaintAll();
            return true;
        }

        // ------------------------------------------------------------------ キャンバスへの表示

        /// <summary>線・円・つまみを描く。キャンバスの外にはみ出す部分は描かない(ツールバーに重ならないように)。</summary>
        private void DrawGradientOverlay(Texture source)
        {
            var gradient = GradientProperty(false);
            var mode = ModeOf(gradient);
            if (_gradientPointLayer != layerName) ForgetGradientPoint();
            if (gradient == null || mode == GradientMaskMode.None) return;
            var invert = Prop(gradient, nameof(GradientMask.invert)).boolValue;
            var color = Handles.color;
            switch (mode)
            {
                case GradientMaskMode.TextureLinear:
                case GradientMaskMode.TextureRadial:
                case GradientMaskMode.ModelDirection:
                {
                    // 引き直しの途中でまだ書いていないときは、始点から今のマウスまでを出す
                    Vector2 startUv, endUv;
                    if (_gradientNew && !_gradientNewWritten)
                    {
                        startUv = _gradientNewUv;
                        endUv = _gradientNewUv;
                    }
                    else if (!EndpointUv(gradient, mode, true, source, out startUv) ||
                             !EndpointUv(gradient, mode, false, source, out endUv))
                    {
                        // モデル上でシーンビューから動かしたあとなどは、テクスチャ上の位置が分からないので出さない
                        break;
                    }

                    var a = UvToScreen(startUv);
                    var b = UvToScreen(endUv);
                    if (mode == GradientMaskMode.TextureRadial) DrawClippedCircle(a, Vector2.Distance(a, b));
                    DrawClippedLine(a, b);
                    DrawHandleDisc(a, invert ? 1f : 0f, _gradientDrag == 1);
                    DrawHandleDisc(b, invert ? 0f : 1f, _gradientDrag == 2);
                    break;
                }

                case GradientMaskMode.TexturePoints:
                case GradientMaskMode.ModelPoints:
                {
                    var points = Points(gradient);
                    for (var i = 0; i < points.arraySize; i++)
                    {
                        var uv = PointUv(mode, gradient, i, source);
                        if (!uv.HasValue) continue;
                        var p = UvToScreen(uv.Value);
                        var value = PointField(gradient, i, nameof(GradientMaskPoint.value)).floatValue;
                        DrawHandleDisc(p, value, i == _gradientPoint);
                        var label = new Rect(p.x + GradientHandleRadius + 2f, p.y - 9f, 40f, 18f);
                        if (_canvas.Contains(label.min) && _canvas.Contains(label.max))
                        {
                            GUI.Label(label, value.ToString("0.##"), PointLabel);
                        }
                    }

                    break;
                }
            }

            Handles.color = color;
        }

        private void DrawClippedLine(Vector2 a, Vector2 b)
        {
            if (!ClipSegment(ref a, ref b, _canvas)) return;
            Handles.color = new Color(0f, 0f, 0f, 0.8f);
            Handles.DrawAAPolyLine(4f, a, b);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(2f, a, b);
        }

        private void DrawClippedCircle(Vector2 center, float radius)
        {
            const int segments = 72;
            var previous = center + new Vector2(radius, 0f);
            for (var i = 1; i <= segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments;
                var next = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                var a = previous;
                var b = next;
                if (ClipSegment(ref a, ref b, _canvas))
                {
                    Handles.color = new Color(0f, 0f, 0f, 0.8f);
                    Handles.DrawAAPolyLine(3f, a, b);
                    Handles.color = Color.white;
                    Handles.DrawAAPolyLine(1.5f, a, b);
                }

                previous = next;
            }
        }

        /// <summary>値の濃さで塗った丸いつまみ。キャンバスに収まらないものは描かない。</summary>
        private void DrawHandleDisc(Vector2 center, float value, bool selected)
        {
            var r = GradientHandleRadius + 2f;
            if (center.x - r < _canvas.xMin || center.x + r > _canvas.xMax || center.y - r < _canvas.yMin ||
                center.y + r > _canvas.yMax)
            {
                return;
            }

            Handles.color = selected ? SccGui.AccentColor : Color.black;
            Handles.DrawSolidDisc(center, Vector3.forward, r);
            Handles.color = Color.white;
            Handles.DrawSolidDisc(center, Vector3.forward, GradientHandleRadius);
            Handles.color = new Color(value, value, value, 1f);
            Handles.DrawSolidDisc(center, Vector3.forward, GradientHandleRadius - 2f);
        }

        private static GUIStyle s_pointLabel;

        private static GUIStyle PointLabel => s_pointLabel ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            normal = { textColor = Color.white },
        };

        /// <summary>表示用に、今のレイヤーのグラデーションマスクの値を描いたテクスチャ。なしなら null。</summary>
        private Texture GradientView(Texture source)
        {
            var g = CurrentGradient();
            if (g.IsNone) return null;
            if (_gradientView == null || _gradientView.width != source.width || _gradientView.height != source.height)
            {
                ReleaseGradientView();
                _gradientView = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.R8,
                    RenderTextureReadWrite.Linear)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                };
                _gradientView.Create();
            }

            RecolorCore.BlitGradient(source, g, g.UsesModel ? Positions(source) : null, _gradientView);
            return _gradientView;
        }

        private void ReleaseGradientView()
        {
            if (_gradientView == null) return;
            _gradientView.Release();
            DestroyImmediate(_gradientView);
            _gradientView = null;
        }

        // ------------------------------------------------------------------ シーンビュー

        /// <summary>モデル上の種類のとき、始点・終点や点をシーンビューに出して動かせるようにする。</summary>
        private void OnSceneGUI(SceneView view)
        {
            if (tool != Tool.Gradient || target == null) return;
            var component = ComponentObject();
            component.Update();
            var gradient = GradientProperty(false);
            var mode = ModeOf(gradient);
            if (gradient == null || !IsModelMode(mode)) return;

            var root = TargetResolver.AvatarRootOf(target);
            var invert = Prop(gradient, nameof(GradientMask.invert)).boolValue;
            var size = HandleUtility.GetHandleSize(root.position) * 0.06f;
            var previous = Handles.color;
            var matrix = Handles.matrix;
            Handles.matrix = root.localToWorldMatrix;
            EditorGUI.BeginChangeCheck();

            if (mode == GradientMaskMode.ModelDirection)
            {
                var start = Prop(gradient, nameof(GradientMask.start));
                var end = Prop(gradient, nameof(GradientMask.end));
                Handles.color = Color.white;
                Handles.DrawAAPolyLine(3f, start.vector3Value, end.vector3Value);
                Handles.color = invert ? Color.white : Color.black;
                Handles.SphereHandleCap(0, start.vector3Value, Quaternion.identity, size, EventType.Repaint);
                Handles.color = invert ? Color.black : Color.white;
                Handles.SphereHandleCap(0, end.vector3Value, Quaternion.identity, size, EventType.Repaint);
                Handles.Label(start.vector3Value, invert ? " 1(始点)" : " 0(始点)", SceneLabel);
                Handles.Label(end.vector3Value, invert ? " 0(終点)" : " 1(終点)", SceneLabel);

                var newStart = Handles.PositionHandle(start.vector3Value, Quaternion.identity);
                var newEnd = Handles.PositionHandle(end.vector3Value, Quaternion.identity);
                if (newStart != start.vector3Value)
                {
                    start.vector3Value = newStart;
                    Prop(gradient, nameof(GradientMask.startUv)).vector2Value = NoUv;
                }

                if (newEnd != end.vector3Value)
                {
                    end.vector3Value = newEnd;
                    Prop(gradient, nameof(GradientMask.endUv)).vector2Value = NoUv;
                }
            }
            else
            {
                var points = Points(gradient);
                if (_gradientPointLayer != layerName) ForgetGradientPoint();
                for (var i = 0; i < points.arraySize; i++)
                {
                    var position = PointField(gradient, i, nameof(GradientMaskPoint.position)).vector3Value;
                    var value = PointField(gradient, i, nameof(GradientMaskPoint.value)).floatValue;
                    Handles.color = i == _gradientPoint ? SccGui.AccentColor : new Color(value, value, value, 1f);
                    if (Handles.Button(position, Quaternion.identity, size, size * 1.2f, Handles.SphereHandleCap))
                    {
                        _gradientPoint = i;
                        _gradientPointLayer = layerName;
                        Repaint();
                    }

                    Handles.Label(position, " " + value.ToString("0.##"), SceneLabel);
                }

                if (_gradientPoint >= 0 && _gradientPoint < points.arraySize)
                {
                    var position = PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.position));
                    var moved = Handles.PositionHandle(position.vector3Value, Quaternion.identity);
                    if (moved != position.vector3Value)
                    {
                        position.vector3Value = moved;
                        // テクスチャ上のどこに当たるかは分からなくなるので、キャンバスには出さない
                        PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.uv)).vector2Value = NoUv;
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                component.ApplyModifiedProperties();
                Repaint();
            }

            Handles.matrix = matrix;
            Handles.color = previous;
        }

        private static GUIStyle s_sceneLabel;

        private static GUIStyle SceneLabel => s_sceneLabel ??= new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = Color.white },
        };

        // ------------------------------------------------------------------ 右パネル(マスクタブ)

        /// <summary>グラデーションマスクの種類ごとの設定(マスクタブ。レイヤーに保存する値)。</summary>
        private void DrawGradientSettings(SerializedProperty gradient, GradientMaskMode mode, Texture source)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                var invert = Prop(gradient, nameof(GradientMask.invert));
                invert.boolValue = EditorGUILayout.ToggleLeft(new GUIContent("反転", "濃いところと薄いところを入れ替える"),
                    invert.boolValue, GUILayout.Width(52f));
                var smooth = Prop(gradient, nameof(GradientMask.smooth));
                smooth.boolValue = EditorGUILayout.ToggleLeft(new GUIContent("なめらか", "0 と 1 の付近をゆるやかにする"),
                    smooth.boolValue, GUILayout.Width(72f));
            }

            if (mode == GradientMaskMode.ModelDirection)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent("向き", "対象のメッシュの端から端へ、始点と終点を置き直す"));
                    if (GUILayout.Button(new GUIContent("上→下", "対象のメッシュの上端から下端へ"), EditorStyles.miniButtonLeft))
                        SetModelAxis(gradient, source, Vector3.down);
                    if (GUILayout.Button(new GUIContent("前→後", "対象のメッシュの前端から後端へ"), EditorStyles.miniButtonMid))
                        SetModelAxis(gradient, source, Vector3.back);
                    if (GUILayout.Button(new GUIContent("左→右", "対象のメッシュの左端から右端へ(アバターから見て)"),
                            EditorStyles.miniButtonRight))
                        SetModelAxis(gradient, source, Vector3.right);
                }
            }

            if (IsPointsMode(mode))
            {
                var points = Points(gradient);
                // 点を選んだかどうかで欄の数が変わらないよう、選んでいないときも灰色で出す
                var selected = _gradientPointLayer == layerName && _gradientPoint >= 0 && _gradientPoint < points.arraySize;
                using (new EditorGUI.DisabledScope(!selected))
                {
                    var content = new GUIContent("選んだ点", "キャンバスで選んだ点の値(0 = マスクなし、1 = 全部)。新しく置く点の値は下の道具の欄で決める");
                    if (selected)
                    {
                        var value = PointField(gradient, _gradientPoint, nameof(GradientMaskPoint.value));
                        value.floatValue = EditorGUILayout.Slider(content, value.floatValue, 0f, 1f);
                    }
                    else
                    {
                        EditorGUILayout.Slider(content, 0f, 0f, 1f);
                    }
                }

                var sharpness = Prop(gradient, nameof(GradientMask.sharpness));
                sharpness.floatValue = EditorGUILayout.Slider(
                    new GUIContent("くっきり", "大きいほど、それぞれの点の近くがその点の値に寄る"), sharpness.floatValue, 0.5f, 8f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(" ");
                    using (new EditorGUI.DisabledScope(!selected))
                    {
                        if (GUILayout.Button(new GUIContent("点を消す", "選んでいる点を消す(Delete キーでも消せる)"),
                                EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                        {
                            DeleteSelectedPoint();
                            GUIUtility.ExitGUI();
                        }
                    }

                    GUILayout.Label($"点 {points.arraySize}/{GradientMask.MaxPoints}", EditorStyles.miniLabel);
                }
            }

            if (GUI.changed && IsModelMode(mode)) SceneView.RepaintAll();
            ComponentObject().ApplyModifiedProperties();
        }

        private void DrawGradientModePopup(SerializedProperty gradient, GradientMaskMode mode, float width, Texture source)
        {
            var index = Array.IndexOf(GradientModes, mode);
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.Popup(Mathf.Max(0, index), GradientModeLabels, GUILayout.Width(width));
            if (!EditorGUI.EndChangeCheck() || next == index) return;
            if (gradient == null) gradient = GradientProperty(true);
            SetMode(gradient, GradientModes[next], source);
            ForgetGradientPoint();
            ComponentObject().ApplyModifiedProperties();
            SceneView.RepaintAll();
            GUIUtility.ExitGUI(); // 種類ごとに欄が変わるので、このフレームはここまで
        }

        /// <summary>マスクタブに入る、グラデーションマスクの欄(種類と、種類ごとの設定)。</summary>
        private void DrawGradientPanel(Texture source)
        {
            var gradient = GradientProperty(false);
            var mode = ModeOf(gradient);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("グラデ", "位置で濃さが変わるマスク。画像マスク・塗りマスクと掛け合わせる"));
                DrawGradientModePopup(gradient, mode, 104f, source);
                if (GUILayout.Button(new GUIContent("編集", "キャンバスでグラデーションの位置を決める道具にする"), EditorStyles.miniButton))
                {
                    tool = Tool.Gradient;
                    SceneView.RepaintAll();
                }
            }

            if (gradient != null && mode != GradientMaskMode.None) DrawGradientSettings(gradient, mode, source);

            switch (mode)
            {
                case GradientMaskMode.None:
                    GUILayout.Label("位置で濃さを変えるマスク。「編集」でキャンバスをドラッグすると、直線のグラデーションを引けます。", SccGui.Hint);
                    break;
                case GradientMaskMode.ModelDirection:
                case GradientMaskMode.ModelPoints:
                    GUILayout.Label("キャンバスのクリックでモデル上の位置を拾い、シーンビューのハンドルで細かく動かせます。" +
                                    "UV を左右で共有しているメッシュでは、左右で違う濃さにはできません。", SccGui.Hint);
                    break;
            }
        }

        private string GradientHint()
        {
            switch (CurrentGradient().Mode)
            {
                case GradientMaskMode.TexturePoints:
                    return "クリック: 点を置く\nドラッグ: 動かす\nCtrl+クリック / Delete: 消す";
                case GradientMaskMode.ModelPoints:
                    return "クリック: モデル上に点を置く\nシーンビュー: 位置を動かす\nDelete: 消す";
                case GradientMaskMode.ModelDirection:
                    return "ドラッグ: モデル上で始点→終点を引く\nシーンビュー: ハンドルで動かす";
                case GradientMaskMode.TextureRadial:
                    return "ドラッグ: 中心から引き直す\nつまみ: 中心と半径";
                default:
                    return "ドラッグ: 引き直す\nつまみ: 始点と終点";
            }
        }
    }
}
