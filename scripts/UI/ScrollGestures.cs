// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI;

/// <summary>
/// Scrolling without a mouse wheel, everywhere in the game: sliding a finger over anything a scroll bar moves (a list,
/// a page, the buttons on it) scrolls it with the finger and keeps gliding a little after letting go, and a controller's
/// right stick scrolls what has the focus (or, without one, what the pointer is over, else the front-most list on
/// screen). A touch that slides far enough to scroll is no longer a press on what it started on.
/// </summary>
public partial class ScrollGestures : Node
{
    /// <summary>How far a finger slides before it scrolls instead of pressing (pixels).</summary>
    private const float Deadzone = 14f;

    /// <summary>The right stick's resting range.</summary>
    private const float StickDeadzone = 0.2f;

    /// <summary>Scroll speed with the stick pushed all the way (pixels a second).</summary>
    private const float StickSpeed = 1600f;

    /// <summary>Mouse events made from touches carry this device id.</summary>
    private const int EmulatedMouse = -1;

    private int _finger = -1;
    private Vector2 _start, _last;
    private bool _scrolling;
    private ScrollBar? _bar;
    private bool _vertical;
    private float _glide;
    private ScrollBar? _glideBar;

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventScreenTouch { Pressed: true } touch when _finger < 0:
                _finger = touch.Index;
                _start = _last = touch.Position;
                _scrolling = false;
                _bar = null;
                _glideBar = null;
                break;
            case InputEventScreenDrag drag when drag.Index == _finger:
                if (!_scrolling && !StartScrolling(drag.Position)) break;
                if (_bar is { } bar)
                {
                    float moved = _vertical ? drag.Position.Y - _last.Y : drag.Position.X - _last.X;
                    bar.Value -= moved;
                    _glide = -(_vertical ? drag.Velocity.Y : drag.Velocity.X);
                }
                _last = drag.Position;
                GetViewport().SetInputAsHandled();
                break;
            case InputEventScreenTouch { Pressed: false } touch when touch.Index == _finger:
                _finger = -1;
                if (_scrolling)
                {
                    _glideBar = _bar;
                    GetViewport().SetInputAsHandled();
                }
                _scrolling = false;
                break;
            // The press the touch made already went to what it started on; the rest of it is the scroll's.
            case InputEventMouse { Device: EmulatedMouse } when _scrolling:
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    /// <summary>Starts scrolling once the finger has slid far enough along a way something under it can scroll.</summary>
    private bool StartScrolling(Vector2 at)
    {
        var moved = at - _start;
        if (moved.Length() < Deadzone) return false;
        _vertical = Mathf.Abs(moved.Y) >= Mathf.Abs(moved.X);
        _bar = ScrollableAt(_start, _vertical);
        if (_bar is null) return false;
        _scrolling = true;
        _last = _start;
        // Lets go of the press where nothing is, so the button the finger started on isn't pressed when it lifts.
        var far = new Vector2(-100000, -100000);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = far, GlobalPosition = far });
        return true;
    }

    public override void _Process(double delta)
    {
        if (_finger < 0 && _glideBar is { } gliding)
        {
            if (!IsInstanceValid(gliding) || Mathf.Abs(_glide) < 20f) _glideBar = null;
            else
            {
                gliding.Value += _glide * (float)delta;
                _glide *= Mathf.Pow(0.04f, (float)delta); // most of the speed is gone after a second
            }
        }
        StickScroll((float)delta);
    }

    private void StickScroll(float delta)
    {
        float x = 0, y = 0;
        foreach (int pad in Input.GetConnectedJoypads())
        {
            x += Input.GetJoyAxis(pad, JoyAxis.RightX);
            y += Input.GetJoyAxis(pad, JoyAxis.RightY);
        }
        bool vertical = Mathf.Abs(y) >= Mathf.Abs(x);
        float push = vertical ? y : x;
        if (Mathf.Abs(push) < StickDeadzone) return;
        var viewport = GetViewport();
        var bar = (viewport.GuiGetFocusOwner() is { } focus ? ScrollableAround(focus, vertical) : null)
            ?? (viewport.GuiGetHoveredControl() is { } hovered ? ScrollableAround(hovered, vertical) : null)
            ?? FrontMost(GetTree().Root, vertical);
        if (bar is null) return;
        float strength = (Mathf.Abs(push) - StickDeadzone) / (1 - StickDeadzone);
        bar.Value += Mathf.Sign(push) * strength * strength * StickSpeed * delta;
        _glideBar = null;
    }

    /// <summary>What the finger touched (the control the press went to), else the front-most scrollable under it.</summary>
    private ScrollBar? ScrollableAt(Vector2 at, bool vertical)
    {
        if (GetViewport().GuiGetHoveredControl() is { } hovered && hovered.GetGlobalRect().HasPoint(at)
            && ScrollableAround(hovered, vertical) is { } bar)
            return bar;
        return FrontMost(GetTree().Root, vertical, at);
    }

    /// <summary>The nearest scroll bar of this control or one it is in that can move this way.</summary>
    private static ScrollBar? ScrollableAround(Node node, bool vertical)
    {
        for (Node? n = node; n is not null; n = n.GetParent())
            if (n is Control control && Bar(control, vertical) is { } bar)
                return bar;
        return null;
    }

    /// <summary>The last scrollable drawn (the front-most), under the point when there is one.</summary>
    private static ScrollBar? FrontMost(Node node, bool vertical, Vector2? at = null)
    {
        for (int i = node.GetChildCount() - 1; i >= 0; i--)
        {
            var child = node.GetChild(i);
            if (child is CanvasItem { Visible: false }) continue;
            if (FrontMost(child, vertical, at) is { } inner) return inner;
            if (child is Control control && control.IsVisibleInTree() && (at is null || control.GetGlobalRect().HasPoint(at.Value))
                && Bar(control, vertical) is { } bar)
                return bar;
        }
        return null;
    }

    /// <summary>This control's own scroll bar for that way, when it has something to scroll.</summary>
    private static ScrollBar? Bar(Control control, bool vertical)
    {
        ScrollBar? bar = control switch
        {
            ScrollContainer scroll => vertical ? scroll.GetVScrollBar() : scroll.GetHScrollBar(),
            ItemList list when vertical => list.GetVScrollBar(),
            RichTextLabel { ScrollActive: true } text when vertical => text.GetVScrollBar(),
            TextEdit edit => vertical ? edit.GetVScrollBar() : edit.GetHScrollBar(),
            _ => null,
        };
        return bar is not null && bar.MaxValue - bar.Page > bar.MinValue + 0.5 ? bar : null;
    }
}
