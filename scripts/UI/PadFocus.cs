// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI;

/// <summary>
/// Playing with a controller or the keys: a panel of choices that opens (the board's prompt, a dialog, a menu) takes
/// the focus on one of its answers, usually the default one; the arrows move it between the answers, and the confirm
/// button presses the one focused. A gold ring shows where it is. With a mouse or a finger the ring stays away (the
/// next button press brings it back on the panel's answer). Panels only telling something have no answers and nothing
/// to focus: they go away by themselves. On a screen with no such panel (a menu, the settings), the first direction or
/// confirm press makes the screen's buttons reachable with the arrows and focuses the first one (the one the screen
/// prefers, else the top left one).
/// </summary>
public partial class PadFocus : CanvasLayer
{
    private static PadFocus? _instance;

    /// <summary>The last input was a controller's button or a key (not a mouse, the stick's pointer or a finger).</summary>
    public static bool UsingButtons { get; private set; }

    private const string ChoiceMeta = "pad_choice";

    /// <summary>Marks buttons made reachable by looking over the whole screen (looked over again as it changes).</summary>
    private const string ScreenMeta = "pad_screen";

    /// <summary>The answer to focus when a button is used and nothing has the focus.</summary>
    private Control? _preferred;

    /// <summary>The text of the answer focused last: a panel built again keeps the focus on the same answer.</summary>
    private string? _lastText;

    private readonly Control _ring = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly StyleBoxFlat _ringBox = new()
    {
        DrawCenter = false, BorderColor = UiArt.Gold, CornerDetail = 6,
        BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
        CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
    };

    public override void _Ready()
    {
        _instance = this;
        Layer = 127; // over every screen, under the stick's pointer
        ProcessMode = ProcessModeEnum.Always;
        _ring.Draw += () => _ring.DrawStyleBox(_ringBox, new Rect2(Vector2.Zero, _ring.Size));
        AddChild(_ring);
        GetViewport().GuiFocusChanged += focused =>
        {
            if (focused.HasMeta(ChoiceMeta) && focused is Button button) _lastText = button.Text;
        };
    }

    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
    }

    /// <summary>
    /// The answers of a panel just shown or built again (every button under <paramref name="panel"/> shown and usable
    /// now); with buttons in use the focus goes to the one focused before if it's still there, else to
    /// <paramref name="preferred"/> (else the first).
    /// </summary>
    public static void Offer(Control panel, Control? preferred = null)
    {
        if (_instance is not { } focus) return;
        var choices = Choices(panel).ToList();
        foreach (var choice in choices)
        {
            choice.FocusMode = Control.FocusModeEnum.All;
            choice.SetMeta(ChoiceMeta, true);
        }
        if (preferred is not null && !choices.Contains(preferred)) preferred = null;
        focus._preferred = preferred ?? choices.FirstOrDefault(c => c is not BaseButton { Disabled: true }) ?? choices.FirstOrDefault();
        if (!UsingButtons || focus._preferred is null) return;
        var owner = focus.GetViewport().GuiGetFocusOwner();
        if (owner is not null && choices.Contains(owner)) return;
        var same = choices.FirstOrDefault(c => c is Button b && b.Text == focus._lastText && !b.Disabled);
        // The panel's buttons may just have been added: focus once they are in place.
        (same ?? focus._preferred).CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>
    /// The control a screen wants focused first with the buttons (a settings page's first option); it gets the focus
    /// now if buttons are in use and nothing has it.
    /// </summary>
    public static void Prefer(Control? control)
    {
        if (_instance is not { } focus || control is null) return;
        Mark(control, ScreenMeta);
        focus._preferred = control;
        if (UsingButtons && focus.GetViewport().GuiGetFocusOwner() is null) control.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static void Mark(Control control, string meta)
    {
        control.FocusMode = Control.FocusModeEnum.All;
        control.SetMeta(ChoiceMeta, true);
        if (meta != ChoiceMeta) control.SetMeta(meta, true);
    }

    /// <summary>Makes every button shown on screen reachable with the arrows; returns the top left usable one.</summary>
    private Control? MarkScreen()
    {
        Control? first = null;
        foreach (var button in ScreenButtons(GetTree().Root))
        {
            Mark(button, ScreenMeta);
            if (button.Disabled) continue;
            var at = button.GetGlobalRect().Position;
            if (first is null || at.Y < first.GetGlobalRect().Position.Y - 4
                || Math.Abs(at.Y - first.GetGlobalRect().Position.Y) <= 4 && at.X < first.GetGlobalRect().Position.X)
                first = button;
        }
        return first;
    }

    private IEnumerable<BaseButton> ScreenButtons(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child == this || child is VirtualPointer || child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }
                || child is CanvasLayer { Visible: false }) continue;
            if (child is BaseButton button) yield return button;
            else foreach (var inner in ScreenButtons(child)) yield return inner;
        }
    }

    /// <summary>Offers <paramref name="panel"/>'s answers each time it's shown.</summary>
    public static void OfferWhenShown(Control panel, Func<Control?>? preferred = null) =>
        panel.VisibilityChanged += () =>
        {
            if (panel.IsVisibleInTree()) Offer(panel, preferred?.Invoke());
        };

    private static IEnumerable<Control> Choices(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }) continue;
            if (child is BaseButton button) yield return button;
            else foreach (var inner in Choices(child)) yield return inner;
        }
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventJoypadButton { Pressed: true }:
            case InputEventKey { Pressed: true }:
                UsingButtons = true;
                // Nothing focused (a mouse or a finger was used last, or the panel opened before any button was): the
                // first direction or confirm only puts the focus on the panel's answer.
                if (!IsNavigation(@event)) break;
                var owner = GetViewport().GuiGetFocusOwner();
                if (owner is null)
                {
                    bool offered = _preferred is { } preferred && IsInstanceValid(preferred) && preferred.IsVisibleInTree();
                    // No panel offered an answer: the screen's buttons, the preferred one first.
                    var first = MarkScreen();
                    var target = offered ? _preferred : first;
                    if (target is null) break;
                    target.GrabFocus();
                    GetViewport().SetInputAsHandled();
                }
                // Buttons shown since the screen was looked over (another page, a list filled) become reachable too.
                else if (owner.HasMeta(ScreenMeta)) MarkScreen();
                break;
            case InputEventMouseButton { Pressed: true }:
            case InputEventScreenTouch { Pressed: true }:
                UsingButtons = false;
                break;
        }
    }

    private static bool IsNavigation(InputEvent @event) =>
        @event.IsAction("ui_accept") || @event.IsAction("ui_left") || @event.IsAction("ui_right")
        || @event.IsAction("ui_up") || @event.IsAction("ui_down");

    public override void _Process(double delta)
    {
        var owner = GetViewport().GuiGetFocusOwner();
        bool show = UsingButtons && owner is not null && owner.HasMeta(ChoiceMeta) && owner.IsVisibleInTree();
        _ring.Visible = show;
        if (!show) return;
        var rect = owner!.GetGlobalRect().Grow(4);
        if (rect.Position == _ring.Position && rect.Size == _ring.Size) return;
        _ring.Position = rect.Position;
        _ring.Size = rect.Size;
        _ring.QueueRedraw();
    }
}
