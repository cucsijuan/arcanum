// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI;

/// <summary>
/// A pointer moved with a controller's left stick, for playing without a mouse: a gold dot that does what the mouse
/// does (hovering shows a card), with the confirm button as the left click and the back button as the right click while
/// it shows. It appears when the stick moves and goes away after a few seconds unused; meanwhile the stick doesn't move
/// the focus, and the buttons do their usual things again once it's gone.
/// </summary>
public partial class VirtualPointer : CanvasLayer
{
    private const float StickDeadzone = 0.22f;

    /// <summary>Pixels a second with the stick pushed all the way (it speeds up while held).</summary>
    private const float Speed = 700f, FastSpeed = 1500f;

    /// <summary>Seconds unused before it goes away, and how long it takes to fade.</summary>
    private const float Idle = 4f, Fade = 0.4f;

    private readonly Control _dot = new();
    private Vector2 _at;
    private float _unused = float.MaxValue;
    private float _held;
    private MouseButtonMask _pressed;

    public bool Showing => _unused < Idle + Fade;

    public override void _Ready()
    {
        Layer = 128;
        ProcessMode = ProcessModeEnum.Always;
        _dot.MouseFilter = Control.MouseFilterEnum.Ignore;
        _dot.Size = new Vector2(28, 28);
        _dot.Draw += () =>
        {
            var center = _dot.Size / 2;
            _dot.DrawCircle(center + new Vector2(1.5f, 2f), 9f, new Color(0, 0, 0, 0.45f));
            _dot.DrawCircle(center, 9f, new Color(0.96f, 0.78f, 0.25f));
            _dot.DrawArc(center, 9f, 0, Mathf.Tau, 24, new Color(0.35f, 0.24f, 0.05f), 2f, true);
            _dot.DrawCircle(center - new Vector2(3, 3), 2.5f, new Color(1, 0.96f, 0.8f, 0.9f));
        };
        AddChild(_dot);
        _dot.Visible = false;
        _at = GetViewport().GetVisibleRect().GetCenter();
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            // The left stick is the pointer's while it shows (otherwise it would also move the focus).
            case InputEventJoypadMotion { Axis: JoyAxis.LeftX or JoyAxis.LeftY } motion:
                if (Showing || Mathf.Abs(motion.AxisValue) >= StickDeadzone) GetViewport().SetInputAsHandled();
                break;
            // The d-pad moves the focus ring: the pointer goes away, so confirm presses the focused answer, not what lies
            // under the dot.
            case InputEventJoypadButton { Pressed: true } pad when Showing && _pressed == 0
                && (pad.IsAction("ui_left") || pad.IsAction("ui_right") || pad.IsAction("ui_up") || pad.IsAction("ui_down")):
                _unused = float.MaxValue;
                break;
            case InputEventJoypadButton button when Showing && Click(button) is { } mouse:
                if (button.Pressed) _pressed |= Mask(mouse);
                else _pressed &= ~Mask(mouse);
                Send(new InputEventMouseButton { ButtonIndex = mouse, Pressed = button.Pressed, ButtonMask = _pressed });
                _unused = 0;
                GetViewport().SetInputAsHandled();
                break;
            case InputEventScreenTouch or InputEventMouseMotion { Device: not 0 } when Showing:
                _unused = Idle; // a finger or a real mouse took over: the dot fades
                break;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var stick = Vector2.Zero;
        foreach (int pad in Input.GetConnectedJoypads())
            stick += new Vector2(Input.GetJoyAxis(pad, JoyAxis.LeftX), Input.GetJoyAxis(pad, JoyAxis.LeftY));
        float push = Mathf.Min(1f, stick.Length());
        if (push >= StickDeadzone)
        {
            if (!Showing) _at = Clamp(_at);
            _held += dt;
            float strength = (push - StickDeadzone) / (1 - StickDeadzone);
            float speed = Mathf.Lerp(Speed, FastSpeed, Mathf.Clamp(_held - 0.4f, 0, 1));
            var from = _at;
            _at = Clamp(_at + stick.Normalized() * strength * strength * speed * dt);
            _unused = 0;
            if (_at != from) Send(new InputEventMouseMotion { Relative = _at - from, ButtonMask = _pressed });
        }
        else
        {
            _held = 0;
            if (_unused < float.MaxValue) _unused += dt;
        }
        _dot.Visible = Showing;
        if (!_dot.Visible) return;
        _dot.Position = _at - _dot.Size / 2;
        _dot.Modulate = new Color(1, 1, 1, Mathf.Clamp(1 - (_unused - Idle) / Fade, 0, 1));
        if (!Showing && _pressed != 0) ReleaseAll();
    }

    /// <summary>The mouse button a controller button stands for: the confirm button clicks, the back button right-clicks.</summary>
    private static MouseButton? Click(InputEventJoypadButton button) =>
        button.IsAction("ui_accept") ? MouseButton.Left : button.IsAction("ui_cancel") ? MouseButton.Right : null;

    private static MouseButtonMask Mask(MouseButton button) => button == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Right;

    private void ReleaseAll()
    {
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right })
            if ((_pressed & Mask(button)) != 0)
                Send(new InputEventMouseButton { ButtonIndex = button, Pressed = false });
        _pressed = 0;
    }

    private Vector2 Clamp(Vector2 at)
    {
        var rect = GetViewport().GetVisibleRect();
        return at.Clamp(rect.Position, rect.End - Vector2.One);
    }

    /// <summary>Sends a mouse event at the dot (in window coordinates, as a real mouse's would be).</summary>
    private void Send(InputEventMouse mouse)
    {
        var window = GetViewport().GetScreenTransform() * _at;
        mouse.Position = window;
        mouse.GlobalPosition = window;
        mouse.Device = 0;
        Input.ParseInputEvent(mouse);
    }
}
