using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GunsawLevelEditorTools;

internal sealed class LevelEditingTools
{
    private const float HitRadiusPixels = 18f;
    private readonly List<GameObject> _selection = new List<GameObject>();
    private readonly List<LineRenderer> _lines = new List<LineRenderer>();
    private readonly List<GameObject> _lineObjects = new List<GameObject>();
    private readonly List<TransformState> _start = new List<TransformState>();
    private Material _material;
    private bool _enabled;
    private DragOperation _drag;
    private Vector3 _center;
    private float _startDistance;
    private float _startAngle;
    private Vector3 _scaleAnchor;
    private Vector3 _scaleStartVector;
    private Vector3 _scaleAxisX;
    private Vector3 _scaleAxisY;
    private bool _scaleXEnabled;
    private bool _scaleYEnabled;
    private GameObject _leader;
    private Vector3 _leaderPosition;
    private GameObject _rotationButton;
    private RectTransform _rotationButtonRect;
    private ScaleHandleInput[] _scaleInputs;
    private Sprite _circleSprite;
    private LevelEditor _editor;
    private bool _restoreNativeSelection;
    private int _overlaySortingLayer;
    private bool _overlaySortingLayerResolved;
    private readonly float[] _nextArrowRepeat = new float[4];

    private static readonly KeyCode[] ArrowKeys =
        { KeyCode.RightArrow, KeyCode.LeftArrow, KeyCode.UpArrow, KeyCode.DownArrow };

    private static readonly Vector3[] ArrowMoves = { Vector3.right, Vector3.left, Vector3.up, Vector3.down };

    internal void Update(LevelEditor editor, bool editingText)
    {
        _editor = editor;
        var wallMode = IsWallEdit(editor);
        RepeatArrowMove(editor, editingText);
        _selection.RemoveAll(item => item == null || !MatchesWallMode(item, wallMode));
        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (!editingText && ctrl && Input.GetKeyDown(KeyCode.M))
        {
            _enabled = !_enabled;
            _drag = DragOperation.None;
            editor.SetInfoText(_enabled ? "Prop editor: ON" : "Prop editor: OFF");
        }

        if (!editingText && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()) &&
            Input.GetKey(KeyCode.LeftShift) && Input.GetMouseButtonDown(0))
        {
            var prop = FindProp(wallMode);
            if (prop != null)
            {
                if (_selection.Contains(prop))
                    _selection.Remove(prop);
                else
                    _selection.Add(prop);
                SetNativeSelection(editor, prop);
                _leader = prop;
                _leaderPosition = prop.transform.position;
            }
        }

        if (!editingText && !Input.GetKey(KeyCode.LeftShift) && Input.GetMouseButtonDown(0) &&
            (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()) &&
            !PointerHitsSelection() && !PointerHitsHandle())
            _selection.Clear();

        var current = GetNativeSelection(editor);
        if (!Input.GetKey(KeyCode.LeftShift) && IsProp(current) && MatchesWallMode(current, wallMode) &&
            !_selection.Contains(current))
        {
            _selection.Clear();
            _selection.Add(current);
            _leader = current;
            _leaderPosition = current.transform.position;
        }

        if (_selection.Count == 0)
        {
            SetLineCount(0);
            HideRotationButton();
            HideScaleInputs();
            return;
        }

        UpdateGroupMove(current);
        if (!_enabled)
        {
            HideRotationButton();
            DrawSelection();
            return;
        }

        var bounds = SelectionBounds();
        var frame = _selection.Count == 1 ? GetFrame(_selection[0]) : new PropFrame();
        var canScale = CanScaleSelection();
        UpdateRotationButton(editor, bounds, frame);
        UpdateScaleInputs(editor, bounds, frame, canScale);
        Draw(bounds, frame, canScale);
        Drag(editor, bounds, frame, canScale, editingText);
    }

    internal bool TryDuplicate(LevelEditor editor)
    {
        var control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (!control || !Input.GetKeyDown(KeyCode.D) || _selection.Count < 2)
            return false;
        var copies = new List<GameObject>();
        foreach (var item in _selection)
        {
            if (!item)
                continue;
            var copy = UnityEngine.Object.Instantiate(item, editor.transform);
            copy.transform.position += Vector3.up * .25f;
            MoveTarget(copy, Vector2.up * .25f);
            copies.Add(copy);
        }

        _selection.Clear();
        _selection.AddRange(copies);
        _leader = copies.Count > 0 ? copies[0] : null;
        if (_leader)
        {
            _leaderPosition = _leader.transform.position;
            SetNativeSelection(editor, null);
            _restoreNativeSelection = true;
        }

        editor.UpdateLighting();
        return copies.Count > 0;
    }

    internal void Prepare(LevelEditor editor)
    {
        if (!_restoreNativeSelection || !_leader)
            return;
        SetNativeSelection(editor, _leader);
        _restoreNativeSelection = false;
    }

    private void Drag(LevelEditor editor, Bounds bounds, PropFrame frame, bool canScale, bool editingText)
    {
        if (Camera.main == null)
            return;
        var rotate = frame.Valid ? frame.RotatePoint : RotatePoint(bounds);
        if (_drag == DragOperation.None && !editingText && !Input.GetKey(KeyCode.LeftShift) &&
            Input.GetMouseButtonDown(0))
        {
            if (canScale && !PointerHitsSelection() && frame.Valid &&
                frame.TryHandle(out var handle, out var scaleXEnabled, out var scaleYEnabled))
                Begin(editor, DragOperation.Scale, bounds, handle, frame, scaleXEnabled, scaleYEnabled);
            else if (canScale && !PointerHitsSelection() && !frame.Valid && TryScaleHandle(bounds, out handle))
                Begin(editor, DragOperation.Scale, bounds, handle, frame, true, true);
            else if (Near(rotate))
                Begin(editor, DragOperation.Rotate, bounds, Vector3.zero, frame, true, true);
        }

        if (_drag == DragOperation.None)
            return;
        if (Input.GetMouseButtonUp(0) || Input.GetKeyDown(KeyCode.Escape))
        {
            _drag = DragOperation.None;
            editor.UpdateLighting();
            return;
        }

        var pointer = CursorWorld();
        if (_drag == DragOperation.Scale)
        {
            ApplyScale(pointer);
        }

        if (_leader)
            _leaderPosition = _leader.transform.position;
        else
        {
            var degrees = RotationDegrees(pointer);
            var rotation = Quaternion.Euler(0f, 0f, degrees);
            foreach (var state in _start)
            {
                if (!state.Transform)
                    continue;
                state.Transform.position = _center + rotation * (state.Position - _center);
                state.Transform.rotation = rotation * state.Rotation;
            }

            if (_leader)
            {
                SetNativeSelection(editor, _leader);
                editor.rotField.text = _leader.transform.eulerAngles.z.ToString();
                editor.UpdatePart(2);
            }
        }
    }

    private void Begin(LevelEditor editor, DragOperation operation, Bounds bounds, Vector3 handle, PropFrame frame,
        bool scaleXEnabled, bool scaleYEnabled)
    {
        _drag = operation;
        _center = bounds.center;
        if (frame.Valid)
            _center = frame.Center;
        SetNativeGrab(editor, false);
        SetNativeSelection(editor, null);
        var pointer = CursorWorld();
        _startDistance = Mathf.Max(.01f, Vector2.Distance(pointer, _center));
        _startAngle = Angle(pointer - _center);
        if (operation == DragOperation.Scale)
        {
            _scaleXEnabled = scaleXEnabled;
            _scaleYEnabled = scaleYEnabled;
            _scaleAnchor = frame.Valid
                ? frame.Opposite(handle)
                : new Vector3(handle.x < bounds.center.x ? bounds.max.x : bounds.min.x,
                    handle.y < bounds.center.y ? bounds.max.y : bounds.min.y, 0f);
            _scaleAxisX = frame.Valid ? frame.AxisX : Vector3.right;
            _scaleAxisY = frame.Valid ? frame.AxisY : Vector3.up;
            var vector = handle - _scaleAnchor;
            _scaleStartVector = new Vector3(Vector3.Dot(vector, _scaleAxisX), Vector3.Dot(vector, _scaleAxisY), 0f);
        }

        _start.Clear();
        foreach (var item in _selection)
            if (item)
                _start.Add(new TransformState(item.transform));
    }

    private void BeginRotation(Vector2 screenPosition)
    {
        if (_editor == null || _selection.Count == 0)
            return;
        var frame = _selection.Count == 1 ? GetFrame(_selection[0]) : new PropFrame();
        var bounds = SelectionBounds();
        Begin(_editor, DragOperation.Rotate, bounds, Vector3.zero, frame, true, true);
        _startAngle = Angle(WorldFromScreen(screenPosition) - _center);
    }

    private void BeginScale(int index)
    {
        if (_editor == null || _selection.Count == 0)
            return;
        var bounds = SelectionBounds();
        var frame = _selection.Count == 1 ? GetFrame(_selection[0]) : new PropFrame();
        if (frame.Valid)
        {
            var handle = index < 4 ? frame.Corners[index] : frame.Edges[index - 4];
            var scaleX = index < 4 || index == 5 || index == 7;
            var scaleY = index < 4 || index == 4 || index == 6;
            Begin(_editor, DragOperation.Scale, bounds, handle, frame, scaleX, scaleY);
            return;
        }

        if (index >= 4)
            return;
        var padding = Mathf.Max(.18f, Mathf.Max(bounds.size.x, bounds.size.y) * .06f);
        var min = bounds.min - Vector3.one * padding;
        var max = bounds.max + Vector3.one * padding;
        var handles = new[]
        {
            new Vector3(min.x, min.y, 0f), new Vector3(min.x, max.y, 0f), new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f)
        };
        Begin(_editor, DragOperation.Scale, bounds, handles[index], frame, true, true);
    }

    private void DragScale(Vector2 screenPosition)
    {
        if (_drag == DragOperation.Scale)
            ApplyScale(WorldFromScreen(screenPosition));
    }

    private void EndScale()
    {
        if (_drag != DragOperation.Scale)
            return;
        _drag = DragOperation.None;
        if (_editor)
            _editor.UpdateLighting();
    }

    private void ApplyScale(Vector3 pointer)
    {
        var current = pointer - _scaleAnchor;
        var x = Vector3.Dot(current, _scaleAxisX);
        var y = Vector3.Dot(current, _scaleAxisY);
        var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (!alt)
        {
            x = Mathf.Round(x * 4f) * .25f;
            y = Mathf.Round(y * 4f) * .25f;
        }

        var scaleX = _scaleXEnabled ? x / _scaleStartVector.x : 1f;
        var scaleY = _scaleYEnabled ? y / _scaleStartVector.y : 1f;
        foreach (var state in _start)
        {
            if (!state.Transform)
                continue;
            var offset = state.Position - _scaleAnchor;
            state.Transform.position = _scaleAnchor + _scaleAxisX * Vector3.Dot(offset, _scaleAxisX) * scaleX +
                                       _scaleAxisY * Vector3.Dot(offset, _scaleAxisY) * scaleY;
            if (state.Renderer != null && state.Renderer.drawMode == SpriteDrawMode.Tiled)
            {
                state.Renderer.size = new Vector2(Mathf.Max(.01f, state.Size.x * Mathf.Abs(scaleX)),
                    Mathf.Max(.01f, state.Size.y * Mathf.Abs(scaleY)));
                state.Transform.localScale = state.Scale;
            }
            else
                state.Transform.localScale = new Vector3(state.Scale.x * scaleX, state.Scale.y * scaleY, state.Scale.z);
        }
    }

    private void DragRotation(Vector2 screenPosition)
    {
        if (_drag != DragOperation.Rotate)
            return;
        var degrees = RotationDegrees(WorldFromScreen(screenPosition));
        var rotation = Quaternion.Euler(0f, 0f, degrees);
        foreach (var state in _start)
        {
            if (!state.Transform)
                continue;
            state.Transform.position = _center + rotation * (state.Position - _center);
            state.Transform.rotation = rotation * state.Rotation;
        }

        if (_leader && _editor)
        {
            SetNativeSelection(_editor, _leader);
            _editor.rotField.text = _leader.transform.eulerAngles.z.ToString();
            _editor.UpdatePart(2);
        }
    }

    private void EndRotation()
    {
        if (_drag != DragOperation.Rotate)
            return;
        _drag = DragOperation.None;
        if (_editor)
            _editor.UpdateLighting();
    }

    private GameObject FindProp(bool wallMode)
    {
        var point = CursorWorld();
        GameObject best = null;
        var score = float.MaxValue;
        foreach (var collider in Physics2D.OverlapCircleAll(point, .04f))
        {
            var part = collider == null ? null : collider.GetComponent<LevelPartGame>();
            if (part == null || !IsProp(part.gameObject) || !MatchesWallMode(part.gameObject, wallMode))
                continue;
            var distance = (part.transform.position - point).sqrMagnitude;
            if (distance < score)
            {
                best = part.gameObject;
                score = distance;
            }
        }

        return best;
    }

    private Bounds SelectionBounds()
    {
        var initialized = false;
        var result = new Bounds();
        foreach (var item in _selection)
        {
            if (!item)
                continue;
            var bounds = ItemBounds(item);
            if (initialized)
                result.Encapsulate(bounds);
            else
            {
                result = bounds;
                initialized = true;
            }
        }

        return result;
    }

    private bool PointerHitsSelection()
    {
        var point = CursorWorld();
        foreach (var item in _selection)
            if (item && ItemBounds(item).Contains(point))
                return true;
        return false;
    }

    private bool PointerHitsHandle()
    {
        if (!_enabled || _selection.Count == 0)
            return false;
        if (_selection.Count == 1)
        {
            var frame = GetFrame(_selection[0]);
            bool scaleX;
            bool scaleY;
            Vector3 handle;
            return Near(frame.RotatePoint) ||
                   (CanScaleSelection() && frame.Valid && frame.TryHandle(out handle, out scaleX, out scaleY));
        }

        var bounds = SelectionBounds();
        return Near(RotatePoint(bounds)) || (CanScaleSelection() && TryScaleHandle(bounds, out var point));
    }

    private void Draw(Bounds bounds, PropFrame frame, bool canScale)
    {
        if (frame.Valid)
        {
            SetLineCount(2);
            Polygon(_lines[0], frame.Corners, new Color(.2f, .85f, 1f, .9f));
            Segment(_lines[1], frame.TopCenter, frame.RotatePoint, new Color(1f, .7f, .15f, 1f));
            return;
        }

        var padding = Mathf.Max(.18f, Mathf.Max(bounds.size.x, bounds.size.y) * .06f);
        var min = bounds.min - Vector3.one * padding;
        var max = bounds.max + Vector3.one * padding;
        var rotate = new Vector3(bounds.center.x, max.y + Mathf.Max(.55f, bounds.size.y * .24f), 0f);
        var index = DrawSelection();
        SetLineCount(index + 7);
        Rectangle(_lines[index++], min, max, new Color(.2f, .85f, 1f, .9f));
        Circle(_lines[index++], new Vector3(min.x, min.y, 0f), new Color(.2f, 1f, .4f, 1f));
        Circle(_lines[index++], new Vector3(min.x, max.y, 0f), new Color(.2f, 1f, .4f, 1f));
        Circle(_lines[index++], new Vector3(max.x, min.y, 0f), new Color(.2f, 1f, .4f, 1f));
        Circle(_lines[index++], new Vector3(max.x, max.y, 0f), new Color(.2f, 1f, .4f, 1f));
        Segment(_lines[index++], new Vector3(bounds.center.x, max.y, 0f), rotate, new Color(1f, .7f, .15f, 1f));
        Circle(_lines[index], rotate, new Color(1f, .7f, .15f, 1f));
    }

    private int DrawSelection()
    {
        if (_selection.Count == 1)
        {
            var frame = GetFrame(_selection[0]);
            if (frame.Valid)
            {
                SetLineCount(1);
                Polygon(_lines[0], frame.Corners, new Color(.2f, .85f, 1f, .9f));
                return 1;
            }
        }

        SetLineCount(_selection.Count);
        var index = 0;
        foreach (var item in _selection)
        {
            if (!item)
                continue;
            var bounds = ItemBounds(item);
            var padding = Mathf.Max(.08f, Mathf.Max(bounds.size.x, bounds.size.y) * .025f);
            Rectangle(_lines[index++], bounds.min - Vector3.one * padding, bounds.max + Vector3.one * padding,
                new Color(.2f, .85f, 1f, .9f));
        }

        return index;
    }

    private void UpdateGroupMove(GameObject current)
    {
        if (_drag != DragOperation.None || _selection.Count < 2)
            return;
        if (current != null && _selection.Contains(current) && current != _leader)
        {
            _leader = current;
            _leaderPosition = current.transform.position;
        }

        if (!_leader)
            return;
        var delta = _leader.transform.position - _leaderPosition;
        if (delta.sqrMagnitude > .0000001f)
        {
            foreach (var item in _selection)
                if (item && item != _leader)
                    item.transform.position += delta;
        }

        _leaderPosition = _leader.transform.position;
    }

    private static bool TryScaleHandle(Bounds bounds, out Vector3 handle)
    {
        var padding = Mathf.Max(.18f, Mathf.Max(bounds.size.x, bounds.size.y) * .06f);
        var min = bounds.min - Vector3.one * padding;
        var max = bounds.max + Vector3.one * padding;
        var handles = new[]
        {
            new Vector3(min.x, min.y, 0f), new Vector3(min.x, max.y, 0f), new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f)
        };
        foreach (var point in handles)
            if (Near(point))
            {
                handle = point;
                return true;
            }

        handle = Vector3.zero;
        return false;
    }

    private static Vector3 RotatePoint(Bounds bounds)
    {
        var padding = Mathf.Max(.18f, Mathf.Max(bounds.size.x, bounds.size.y) * .06f);
        return new Vector3(bounds.center.x, bounds.max.y + padding + Mathf.Max(.55f, bounds.size.y * .24f), 0f);
    }

    private static Bounds ItemBounds(GameObject item)
    {
        var sprites = item.GetComponentsInChildren<SpriteRenderer>(true);
        var initialized = false;
        var result = new Bounds();
        foreach (var sprite in sprites)
        {
            if (sprite == null || sprite.sprite == null)
                continue;
            if (initialized)
                result.Encapsulate(sprite.bounds);
            else
            {
                result = sprite.bounds;
                initialized = true;
            }
        }

        if (initialized)
            return result;
        var renderers = item.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            if (renderer == null)
                continue;
            if (initialized)
                result.Encapsulate(renderer.bounds);
            else
            {
                result = renderer.bounds;
                initialized = true;
            }
        }

        return initialized ? result : new Bounds(item.transform.position, Vector3.one * .45f);
    }

    private static PropFrame GetFrame(GameObject item)
    {
        if (!item)
            return new PropFrame();
        var sprite = item.GetComponent<SpriteRenderer>();
        if (sprite == null || sprite.sprite == null)
            return new PropFrame();
        var local = sprite.sprite.bounds;
        if (sprite.drawMode != SpriteDrawMode.Simple)
            local = new Bounds(local.center, sprite.size);
        var corners = new[]
        {
            item.transform.TransformPoint(new Vector3(local.min.x, local.min.y, 0f)),
            item.transform.TransformPoint(new Vector3(local.min.x, local.max.y, 0f)),
            item.transform.TransformPoint(new Vector3(local.max.x, local.max.y, 0f)),
            item.transform.TransformPoint(new Vector3(local.max.x, local.min.y, 0f))
        };
        var center = (corners[0] + corners[2]) * .5f;
        var axisX = (corners[3] - corners[0]).normalized;
        var axisY = (corners[1] - corners[0]).normalized;
        var top = (corners[1] + corners[2]) * .5f;
        var pixelDistance = Camera.main == null ? .55f : Camera.main.orthographicSize * 80f / Screen.height;
        var distance = Mathf.Max(pixelDistance, Mathf.Max(.55f, Vector3.Distance(center, top) * .48f));
        return new PropFrame
        {
            Valid = axisX.sqrMagnitude > .001f && axisY.sqrMagnitude > .001f,
            Corners = corners,
            Edges = new[]
            {
                (corners[0] + corners[3]) * .5f, (corners[0] + corners[1]) * .5f, (corners[1] + corners[2]) * .5f,
                (corners[2] + corners[3]) * .5f
            },
            Center = center,
            AxisX = axisX,
            AxisY = axisY,
            TopCenter = top,
            RotatePoint = top + (top - center).normalized * distance
        };
    }

    private void SetLineCount(int count)
    {
        while (_lines.Count < count)
        {
            var go = new GameObject("Gunsaw Prop Editor Handle");
            var line = go.AddComponent<LineRenderer>();
            line.material = Material;
            line.useWorldSpace = true;
            line.widthMultiplier = .045f;
            line.numCapVertices = 3;
            line.sortingLayerID = OverlaySortingLayer;
            line.sortingOrder = short.MaxValue;
            _lines.Add(line);
            _lineObjects.Add(go);
        }

        for (var i = 0; i < _lines.Count; i++)
            _lines[i].gameObject.SetActive(i < count);
    }

    private static void Segment(LineRenderer line, Vector3 a, Vector3 b, Color color)
    {
        line.positionCount = 2;
        line.loop = false;
        line.startColor = color;
        line.endColor = color;
        line.SetPosition(0, a);
        line.SetPosition(1, b);
    }

    private static void Rectangle(LineRenderer line, Vector3 min, Vector3 max, Color color)
    {
        line.positionCount = 4;
        line.loop = true;
        line.startColor = color;
        line.endColor = color;
        line.SetPosition(0, new Vector3(min.x, min.y, 0));
        line.SetPosition(1, new Vector3(max.x, min.y, 0));
        line.SetPosition(2, new Vector3(max.x, max.y, 0));
        line.SetPosition(3, new Vector3(min.x, max.y, 0));
    }

    private static void Circle(LineRenderer line, Vector3 center, Color color)
    {
        const int count = 18;
        line.positionCount = count;
        line.loop = true;
        line.startColor = color;
        line.endColor = color;
        for (var i = 0; i < count; i++)
        {
            var a = i * Mathf.PI * 2f / count;
            line.SetPosition(i, center + new Vector3(Mathf.Cos(a) * .18f, Mathf.Sin(a) * .18f, 0));
        }
    }

    private static void Polygon(LineRenderer line, Vector3[] points, Color color)
    {
        line.positionCount = 4;
        line.loop = true;
        line.startColor = color;
        line.endColor = color;
        for (var i = 0; i < 4; i++)
            line.SetPosition(i, points[i]);
    }

    private static bool IsProp(GameObject item)
    {
        return item != null &&
               (item.GetComponent<LevelPartGame>() != null || item.GetComponent("CustomPropMarker") != null);
    }

    private void RepeatArrowMove(LevelEditor editor, bool editingText)
    {
        if (editingText)
            return;
        var selected = editor.currentlySelected;
        if (!IsProp(selected))
            return;
        for (var i = 0; i < ArrowKeys.Length; i++)
        {
            if (Input.GetKeyDown(ArrowKeys[i]))
            {
                _nextArrowRepeat[i] = Time.unscaledTime + .35f;
                continue;
            }

            if (!Input.GetKey(ArrowKeys[i]))
            {
                _nextArrowRepeat[i] = 0f;
                continue;
            }

            if (_nextArrowRepeat[i] <= 0f || Time.unscaledTime < _nextArrowRepeat[i])
                continue;
            selected.transform.position += ArrowMoves[i] * .125f;
            if (i < 2)
                editor.posXField.SetTextWithoutNotify(selected.transform.position.x.ToString());
            else
                editor.posYField.SetTextWithoutNotify(selected.transform.position.y.ToString());
            _nextArrowRepeat[i] = Time.unscaledTime + .08f;
        }
    }

    private static bool MatchesWallMode(GameObject item, bool wallMode)
    {
        return item != null && item.CompareTag("Backwall") == wallMode;
    }

    private static bool IsWallEdit(LevelEditor editor)
    {
        return editor.wallMode;
    }

    private static void MoveTarget(GameObject item, Vector2 delta)
    {
        var part = item == null ? null : item.GetComponent<LevelPartGame>();
        if (part != null && IsMovingPart(part))
            part.part.force += delta;
    }

    private static bool IsMovingPart(LevelPartGame part)
    {
        var name = (part.fullName ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        return name == "door" || name == "stripeddoor" || name == "secretdoor" || name == "movingbackground";
    }

    private bool CanScaleSelection()
    {
        foreach (var item in _selection)
            if (!CanScale(item))
                return false;
        return _selection.Count > 0;
    }

    private static bool CanScale(GameObject item)
    {
        var renderer = item == null ? null : item.GetComponent<SpriteRenderer>();
        return renderer != null && renderer.drawMode == SpriteDrawMode.Tiled;
    }

    private static GameObject GetNativeSelection(LevelEditor editor)
    {
        return editor.currentlySelected;
    }

    private static void SetNativeSelection(LevelEditor editor, GameObject item)
    {
        editor.currentlySelected = item;
    }

    private static void SetNativeGrab(LevelEditor editor, bool value)
    {
        editor.hasGrabPos = value;
    }

    private static Vector3 CursorWorld()
    {
        if (Camera.main == null)
            return Vector3.zero;
        var point = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        point.z = 0f;
        return point;
    }

    private static Vector3 WorldFromScreen(Vector2 screenPosition)
    {
        if (Camera.main == null)
            return Vector3.zero;
        var point = Camera.main.ScreenToWorldPoint(screenPosition);
        point.z = 0f;
        return point;
    }

    private static bool Near(Vector3 point)
    {
        var screen = Camera.main.WorldToScreenPoint(point);
        var delta = new Vector2(screen.x, screen.y) - new Vector2(Input.mousePosition.x, Input.mousePosition.y);
        return delta.sqrMagnitude <= HitRadiusPixels * HitRadiusPixels;
    }

    private static float Angle(Vector3 vector)
    {
        return Mathf.Atan2(vector.y, vector.x) * Mathf.Rad2Deg;
    }

    private float RotationDegrees(Vector3 pointer)
    {
        var degrees = Angle(pointer - _center) - _startAngle;
        var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        return alt ? degrees : Mathf.Round(degrees / 45f) * 45f;
    }

    private Material Material
    {
        get
        {
            if (_material == null)
                _material = new Material(Shader.Find("Sprites/Default"));
            return _material;
        }
    }

    private int OverlaySortingLayer
    {
        get
        {
            if (_overlaySortingLayerResolved)
                return _overlaySortingLayer;
            var layers = SortingLayer.layers;
            _overlaySortingLayer = layers.Length == 0 ? 0 : layers[layers.Length - 1].id;
            _overlaySortingLayerResolved = true;
            return _overlaySortingLayer;
        }
    }

    private void UpdateRotationButton(LevelEditor editor, Bounds bounds, PropFrame frame)
    {
        if (_selection.Count == 0 || editor.infoText == null)
        {
            HideRotationButton();
            return;
        }

        if (_rotationButton == null)
        {
            _rotationButton = new GameObject("Gunsaw Rotation Handle Input", typeof(RectTransform), typeof(Image),
                typeof(RotationHandleInput));
            _rotationButtonRect = _rotationButton.GetComponent<RectTransform>();
            var image = _rotationButton.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, .02f);
            image.raycastTarget = true;
            var dot = new GameObject("Rotation Dot", typeof(RectTransform), typeof(Image));
            dot.transform.SetParent(_rotationButton.transform, false);
            var dotImage = dot.GetComponent<Image>();
            dotImage.sprite = CircleSprite;
            dotImage.color = new Color(1f, .7f, .15f, 1f);
            dotImage.raycastTarget = false;
            var dotRect = dot.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(.5f, .5f);
            dotRect.pivot = new Vector2(.5f, .5f);
            dotRect.sizeDelta = new Vector2(18f, 18f);
            _rotationButton.GetComponent<RotationHandleInput>().Owner = this;
        }

        var canvas = editor.infoText.canvas;
        _rotationButton.transform.SetParent(canvas.transform, false);
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform,
            Camera.main.WorldToScreenPoint(frame.Valid ? frame.RotatePoint : RotatePoint(bounds)),
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var point);
        _rotationButtonRect.anchorMin = _rotationButtonRect.anchorMax = new Vector2(.5f, .5f);
        _rotationButtonRect.anchoredPosition = point;
        _rotationButtonRect.sizeDelta = new Vector2(36f, 36f);
        _rotationButton.SetActive(true);
        _rotationButton.transform.SetAsLastSibling();
    }

    private void UpdateScaleInputs(LevelEditor editor, Bounds bounds, PropFrame frame, bool canScale)
    {
        if (!canScale || editor.infoText == null)
        {
            HideScaleInputs();
            return;
        }

        if (_scaleInputs == null)
        {
            _scaleInputs = new ScaleHandleInput[8];
            for (var i = 0; i < 8; i++)
            {
                var go = new GameObject("Gunsaw Scale Handle Input", typeof(RectTransform), typeof(Image),
                    typeof(ScaleHandleInput));
                var image = go.GetComponent<Image>();
                image.color = new Color(0f, 0f, 0f, .02f);
                image.raycastTarget = true;
                var dot = new GameObject("Scale Dot", typeof(RectTransform), typeof(Image));
                dot.transform.SetParent(go.transform, false);
                var dotImage = dot.GetComponent<Image>();
                dotImage.sprite = CircleSprite;
                dotImage.color = new Color(.2f, 1f, .4f, 1f);
                dotImage.raycastTarget = false;
                var dotRect = dot.GetComponent<RectTransform>();
                dotRect.anchorMin = dotRect.anchorMax = new Vector2(.5f, .5f);
                dotRect.pivot = new Vector2(.5f, .5f);
                dotRect.sizeDelta = new Vector2(18f, 18f);
                var input = go.GetComponent<ScaleHandleInput>();
                input.Owner = this;
                input.Index = i;
                _scaleInputs[i] = input;
            }
        }

        var canvas = editor.infoText.canvas;
        var count = frame.Valid ? 8 : 4;
        var padding = Mathf.Max(.18f, Mathf.Max(bounds.size.x, bounds.size.y) * .06f);
        var min = bounds.min - Vector3.one * padding;
        var max = bounds.max + Vector3.one * padding;
        var groupCorners = new[]
        {
            new Vector3(min.x, min.y, 0f), new Vector3(min.x, max.y, 0f), new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f)
        };
        for (var i = 0; i < _scaleInputs.Length; i++)
        {
            var input = _scaleInputs[i];
            if (i >= count)
            {
                input.gameObject.SetActive(false);
                continue;
            }

            var point = frame.Valid ? (i < 4 ? frame.Corners[i] : frame.Edges[i - 4]) : groupCorners[i];
            input.transform.SetParent(canvas.transform, false);
            var rect = input.GetComponent<RectTransform>();
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform,
                Camera.main.WorldToScreenPoint(point),
                canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var position);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(32f, 32f);
            input.gameObject.SetActive(true);
            input.transform.SetAsLastSibling();
        }
    }

    private void HideScaleInputs()
    {
        if (_scaleInputs == null)
            return;

        foreach (var input in _scaleInputs)
            if (input)
                input.gameObject.SetActive(false);
    }

    private Sprite CircleSprite
    {
        get
        {
            if (_circleSprite)
                return _circleSprite;
            var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var dx = x - 15.5f;
                var dy = y - 15.5f;
                var distance = dx * dx + dy * dy;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, distance >= 121f && distance <= 225f ? 1f : 0f));
            }

            texture.Apply();
            _circleSprite = Sprite.Create(texture, new Rect(0f, 0f, 32f, 32f), new Vector2(.5f, .5f));
            return _circleSprite;
        }
    }

    private void HideRotationButton()
    {
        if (_rotationButton)
            _rotationButton.SetActive(false);
    }

    internal void Dispose()
    {
        foreach (var item in _lineObjects)
            if (item)
                UnityEngine.Object.Destroy(item);
        _lineObjects.Clear();
        _lines.Clear();
        _selection.Clear();
        if (_rotationButton)
            UnityEngine.Object.Destroy(_rotationButton);
        if (_scaleInputs != null)
            foreach (var input in _scaleInputs)
                if (input)
                    UnityEngine.Object.Destroy(input.gameObject);
        _scaleInputs = null;
        _rotationButton = null;
        _rotationButtonRect = null;
        if (_circleSprite)
        {
            UnityEngine.Object.Destroy(_circleSprite.texture);
            UnityEngine.Object.Destroy(_circleSprite);
        }

        _circleSprite = null;
        if (_material)
            UnityEngine.Object.Destroy(_material);
        _material = null;
        _drag = DragOperation.None;
    }

    private enum DragOperation
    {
        None,
        Scale,
        Rotate
    }

    private struct PropFrame
    {
        internal bool Valid;
        internal Vector3[] Corners;
        internal Vector3[] Edges;
        internal Vector3 Center;
        internal Vector3 AxisX;
        internal Vector3 AxisY;
        internal Vector3 TopCenter;
        internal Vector3 RotatePoint;

        internal bool TryHandle(out Vector3 handle, out bool scaleX, out bool scaleY)
        {
            foreach (var point in Corners)
                if (Near(point))
                {
                    handle = point;
                    scaleX = true;
                    scaleY = true;
                    return true;
                }

            for (var i = 0; i < 4; i++)
                if (Near(Edges[i]))
                {
                    handle = Edges[i];
                    scaleX = i == 1 || i == 3;
                    scaleY = i == 0 || i == 2;
                    return true;
                }

            handle = Vector3.zero;
            scaleX = false;
            scaleY = false;
            return false;
        }

        internal Vector3 Opposite(Vector3 handle)
        {
            for (var i = 0; i < 4; i++)
                if ((Corners[i] - handle).sqrMagnitude < .0001f)
                    return Corners[(i + 2) % 4];
            for (var i = 0; i < 4; i++)
                if ((Edges[i] - handle).sqrMagnitude < .0001f)
                    return Edges[(i + 2) % 4];
            return Center;
        }
    }

    private struct TransformState
    {
        internal readonly Transform Transform;
        internal readonly Vector3 Position;
        internal readonly Vector3 Scale;
        internal readonly Quaternion Rotation;
        internal readonly SpriteRenderer Renderer;
        internal readonly Vector2 Size;

        internal TransformState(Transform transform)
        {
            Transform = transform;
            Position = transform.position;
            Scale = transform.localScale;
            Rotation = transform.rotation;
            Renderer = transform.GetComponent<SpriteRenderer>();
            Size = Renderer == null ? Vector2.one : Renderer.size;
        }
    }

    private sealed class RotationHandleInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        internal LevelEditingTools Owner;

        public void OnPointerDown(PointerEventData eventData)
        {
            Owner.BeginRotation(eventData.position);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Owner.DragRotation(eventData.position);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Owner.EndRotation();
        }
    }

    private sealed class ScaleHandleInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        internal LevelEditingTools Owner;
        internal int Index;

        public void OnPointerDown(PointerEventData eventData)
        {
            Owner.BeginScale(Index);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Owner.DragScale(eventData.position);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Owner.EndScale();
        }
    }
}