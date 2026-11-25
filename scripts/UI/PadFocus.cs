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
/// prefers, else the top left one). Cards to pick on a screen (a collection, a deck being built) are reached the same way
/// once marked <see cref="Pressable"/>: confirm clicks the focused one, <see cref="AltButton"/> right-clicks it. When the
/// focused one goes away (the page was built again) the focus goes to the nearest one left. On the career's screens the
/// ring is their thin gold line.
/// </summary>
public partial class PadFocus : CanvasLayer
{
    private static PadFocus? _instance;

    /// <summary>The last input was a controller's button or a key (not a mouse, the stick's pointer or a finger).</summary>
    public static bool UsingButtons { get; private set; }

    private const string ChoiceMeta = "pad_choice";

    /// <summary>Marks buttons made reachable by looking over the whole screen (looked over again as it changes).</summary>
    private const string ScreenMeta = "pad_screen";

    /// <summary>Marks controls other than buttons the screen scan reaches (cards to pick).</summary>
    private const string PressMeta = "pad_press";

    /// <summary>Marks a screen that moves its own selection with the arrows (the title screen's options): left alone.</summary>
    private const string OwnMeta = "pad_own";

    /// <summary>Marks a screen's back button: the back button of a controller (or Esc) presses it.</summary>
    private const string BackMeta = "pad_back";

    /// <summary>On a control partly covered by the next one (a card in a stack): the height of the part that shows.</summary>
    private const string ShowsMeta = "pad_shows";

    /// <summary>Marks controls other than buttons the arrows reach that handle touches their own way (cards on a game's table).</summary>
    private const string ReachMeta = "pad_reach";
    private const string InsideMeta = "pad_inside";

    /// <summary>On a panel's answer: the panel (the arrows move between its answers first).</summary>
    private const string PanelMeta = "pad_panel";

    /// <summary>The controller button that right-clicks the focused card (the left face button).</summary>
    public const JoyButton AltButton = JoyButton.X;

    /// <summary>The screen's control focused last, and where it was: when it goes away the nearest one is focused.</summary>
    private Control? _lost;
    private Rect2 _lostAt;

    /// <summary>The answer to focus when a button is used and nothing has the focus.</summary>
    private Control? _preferred;

    /// <summary>The text of the answer focused last: a panel built again keeps the focus on the same answer.</summary>
    private string? _lastText;

    private readonly Control _ring = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly StyleBoxFlat _ringBox = Ring(UiArt.Gold, 3, 8);

    /// <summary>The career's screens: a thin gold line, like the edges of their tags and panels, between two brown ones
    /// (so it also stands out on their pale paper).</summary>
    private readonly StyleBoxFlat _careerRing = Ring(Career.CareerStyle.Gold, 2, 6);

    private readonly StyleBoxFlat _careerRingEdge = Ring(Career.CareerStyle.PaperEdge, 5, 7);

    private StyleBoxFlat _ringShown = null!;

    private static StyleBoxFlat Ring(Color color, int width, int radius) => new()
    {
        DrawCenter = false, BorderColor = color, CornerDetail = 6,
        BorderWidthLeft = width, BorderWidthTop = width, BorderWidthRight = width, BorderWidthBottom = width,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
    };

    public override void _Ready()
    {
        _instance = this;
        Layer = 127; // over every screen, under the stick's pointer
        ProcessMode = ProcessModeEnum.Always;
        _ringShown = _ringBox;
        _ring.Draw += () =>
        {
            var rect = new Rect2(Vector2.Zero, _ring.Size);
            if (_ringShown == _careerRing) _ring.DrawStyleBox(_careerRingEdge, rect.Grow(2));
            _ring.DrawStyleBox(_ringShown, rect);
        };
        AddChild(_ring);
        GetViewport().GuiFocusChanged += focused =>
        {
            // Leaving the panel's answers (for the cards on the table) forgets the one passed last: the next panel
            // focuses its own default answer, not one the arrows only went through on their way out.
            if (focused.HasMeta(PanelMeta) && focused is Button button) _lastText = button.Text;
            else if (focused.HasMeta(ReachMeta)) _lastText = null;
            // A list's scroll bar isn't a choice: the arrows would scroll the list under an unmarked focus. It gives the
            // focus up to the nearest choice and never takes it again.
            if (focused is ScrollBar bar)
            {
                var at = Shown(bar);
                bar.FocusMode = Control.FocusModeEnum.None; // (lets go of the focus at once)
                Callable.From(() =>
                {
                    if (GetViewport().GuiGetFocusOwner() is not (null or ScrollBar)) return;
                    if (Nearest(at) is { } choice) choice.GrabFocus();
                    else GetViewport().GuiReleaseFocus();
                }).CallDeferred();
            }
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
            choice.SetMeta(PanelMeta, panel);
        }
        if (preferred is not null && !choices.Contains(preferred)) preferred = null;
        // Under something covering the screen (a question): its answers wait.
        if (IsCovered(panel)) return;
        focus._preferred = preferred ?? choices.FirstOrDefault(c => c is not BaseButton { Disabled: true }) ?? choices.FirstOrDefault();
        if (!UsingButtons || focus._preferred is null) return;
        var owner = focus.GetViewport().GuiGetFocusOwner();
        if (owner is not null && choices.Contains(owner)) return;
        var same = choices.FirstOrDefault(c => c is Button b && b.Text == focus._lastText && !b.Disabled);
        // The panel's buttons may just have been added: focus once they are in place.
        GrabSoon(same ?? focus._preferred);
    }

    /// <summary>
    /// <see cref="Offer"/> for a panel shown over the screen (a question that must be answered): its answer takes the
    /// focus at once, whatever was used last, so one press of confirm answers it (and never presses the screen's
    /// focused button under the panel).
    /// </summary>
    public static void OfferModal(Control panel, Control? preferred = null)
    {
        if (_instance is not { } focus) return;
        if (focus.GetViewport().GuiGetFocusOwner() is { } owner && !panel.IsAncestorOf(owner)) owner.ReleaseFocus();
        Offer(panel, preferred);
        if (IsCovered(panel)) return;
        if (!UsingButtons && focus._preferred is { } answer && IsInstanceValid(answer)) GrabSoon(answer);
    }

    /// <summary>Focuses <paramref name="control"/> once it's in place, unless something covers it by then.</summary>
    private static void GrabSoon(Control control) => Callable.From(() =>
    {
        if (IsInstanceValid(control) && control.IsInsideTree() && !IsCovered(control)) control.GrabFocus();
    }).CallDeferred();

    /// <summary>
    /// The control a screen wants focused first with the buttons (a settings page's first option); it gets the focus
    /// now if buttons are in use and nothing has it.
    /// </summary>
    public static void Prefer(Control? control)
    {
        if (_instance is not { } focus || control is null) return;
        Mark(control, ScreenMeta);
        focus._preferred = control;
        if (UsingButtons && focus.GetViewport().GuiGetFocusOwner() is null) GrabSoon(control);
    }

    /// <summary>
    /// A control other than a button the arrows reach on its screen (a card to pick); <paramref name="shows"/> is the
    /// height of the part that shows when the next one covers the rest (a card in a stack).
    /// </summary>
    public static void Pressable(Control control, float? shows = null)
    {
        control.SetMeta(PressMeta, true);
        if (shows is { } height) control.SetMeta(ShowsMeta, height);
    }

    /// <summary>A screen with its own selection moved by the arrows: no button of it is focused, no ring shows.</summary>
    public static void OwnArrows(Control screen) => screen.SetMeta(OwnMeta, true);

    /// <summary>The button the controller's back button (or Esc) presses while its screen shows and nothing is open over it.</summary>
    public static void BackButton(BaseButton button) => button.SetMeta(BackMeta, true);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("ui_cancel") || GetTree().Root.GetEmbeddedSubwindows().Any(w => w.Visible)) return;
        var back = ScreenChoices(Scope()).OfType<BaseButton>().FirstOrDefault(b => b.HasMeta(BackMeta) && !b.Disabled);
        if (back is null) return;
        GetViewport().SetInputAsHandled();
        back.EmitSignal(BaseButton.SignalName.Pressed);
    }

    /// <summary>
    /// A control other than a button the arrows reach (a card on a game's table), which, unlike <see cref="Pressable"/>,
    /// keeps its own handling of touches.
    /// </summary>
    public static void Reachable(Control control) => control.SetMeta(ReachMeta, true);

    /// <summary>While the focus is inside <paramref name="panel"/> (one covering the screen) the arrows don't leave it.</summary>
    public static void KeepInside(Control panel, bool keep) => panel.SetMeta(InsideMeta, keep);

    /// <summary>Where the arrows may go from <paramref name="from"/>: the panel that keeps them (<see cref="KeepInside"/>), else the screen.</summary>
    private Node ReachOf(Control from)
    {
        for (Node? node = from; node is not null; node = node.GetParent())
            if (node.HasMeta(InsideMeta) && node.GetMeta(InsideMeta).AsBool()) return node;
        return Scope();
    }

    /// <summary>What covers the screen now, the last one on top (see <see cref="Cover"/>).</summary>
    private static readonly List<Control> Covers = new();

    /// <summary>What had the focus when the screen was covered, and where: it goes back there.</summary>
    private Control? _covered;
    private Rect2? _coveredAt;

    /// <summary>
    /// <paramref name="overlay"/>, just added, covers the screen (an opened booster, a question): until it goes away the
    /// buttons reach nothing under it, and the focus goes to <paramref name="first"/> (else to its first choice when a
    /// direction is pressed). Closed, the focus goes back to what had it, or the nearest choice left.
    /// </summary>
    public static void Cover(Control overlay, Control? first = null)
    {
        if (_instance is not { } focus) return;
        var owner = focus.GetViewport().GuiGetFocusOwner();
        if (TopCover() is null)
        {
            // The focused one may have just gone (the page under was built again): then where it was.
            focus._covered = owner;
            focus._coveredAt = owner is not null && !owner.IsQueuedForDeletion() ? Shown(owner) : focus._lost is not null ? focus._lostAt : null;
        }
        Covers.Remove(overlay);
        Covers.Add(overlay);
        focus._lost = null;
        if (owner is not null && !overlay.IsAncestorOf(owner)) owner.ReleaseFocus();
        overlay.TreeExited += () => Callable.From(Uncovered).CallDeferred();
        if (first is not null) Prefer(first);
    }

    private static void Uncovered()
    {
        if (_instance is not { } focus || TopCover() is not null) return;
        var (covered, at) = (focus._covered, focus._coveredAt);
        (focus._covered, focus._coveredAt) = (null, null);
        if (focus.GetViewport().GuiGetFocusOwner() is not null) return;
        if (covered is not null && IsInstanceValid(covered) && !covered.IsQueuedForDeletion() && covered.IsVisibleInTree() && covered.FocusMode != Control.FocusModeEnum.None)
            covered.GrabFocus();
        else if (UsingButtons && at is { } place) focus.Nearest(place)?.GrabFocus();
    }

    private static Control? TopCover()
    {
        Covers.RemoveAll(c => !IsInstanceValid(c) || c.IsQueuedForDeletion());
        return Covers.LastOrDefault(c => c.IsVisibleInTree());
    }

    /// <summary>Whether something covers <paramref name="control"/> (see <see cref="Cover"/>).</summary>
    public static bool IsCovered(Control control) => TopCover() is { } cover && cover != control && !cover.IsAncestorOf(control);

    /// <summary>Where the buttons reach now: what covers the screen, else all of it.</summary>
    private Node Scope() => TopCover() ?? (Node)GetTree().Root;

    /// <summary>
    /// A line of a list that shows something while pointed at (a card's name): it does while focused too, and the arrows
    /// reach it.
    /// </summary>
    public static void Shows(Control row, Action show, Action hide)
    {
        Reachable(row);
        row.MouseEntered += show;
        row.MouseExited += hide;
        row.FocusEntered += () => { if (UsingButtons) show(); };
        row.FocusExited += hide;
    }

    /// <summary>Whether <paramref name="control"/> was marked <see cref="Pressable"/>.</summary>
    public static bool IsPressable(Control control) => control.HasMeta(PressMeta);

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
        foreach (var button in ScreenChoices(Scope()))
        {
            Mark(button, ScreenMeta);
            if (button is BaseButton { Disabled: true }) continue;
            var at = button.GetGlobalRect().Position;
            if (first is null || at.Y < first.GetGlobalRect().Position.Y - 4
                || Math.Abs(at.Y - first.GetGlobalRect().Position.Y) <= 4 && at.X < first.GetGlobalRect().Position.X)
                first = button;
        }
        return first;
    }

    // The walks below gather the controls at once, child by child (no engine array of children kept open while other
    // code runs between steps: on the console that once read a stale array and crashed).
    private List<Control> ScreenChoices(Node node)
    {
        var found = new List<Control>();
        if (!node.HasMeta(OwnMeta)) AddScreenChoices(node, found);
        return found;
    }

    private void AddScreenChoices(Node node, List<Control> found)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            if (child == this || child is VirtualPointer || child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }
                || child is CanvasLayer { Visible: false } || child.HasMeta(OwnMeta)) continue;
            if (child is BaseButton button) found.Add(button);
            else if (child is Control { MouseFilter: not Control.MouseFilterEnum.Ignore } pressable && (pressable.HasMeta(PressMeta) || pressable.HasMeta(ReachMeta)))
                found.Add(pressable);
            else AddScreenChoices(child, found);
        }
    }

    /// <summary>The part of a control that shows: the whole, or the top of a card covered by the next one.</summary>
    private static Rect2 Shown(Control control)
    {
        // Where it is drawn, turned included (a tapped card lies sideways around its middle).
        var rect = control.GetGlobalTransform() * new Rect2(Vector2.Zero, control.Size);
        if (control.HasMeta(ShowsMeta)) rect.Size = rect.Size with { Y = Math.Min(rect.Size.Y, (float)control.GetMeta(ShowsMeta) * control.GetGlobalTransform().Scale.Y) };
        return rect;
    }

    /// <summary>The screen's usable choice nearest to where the one that went away was.</summary>
    private Control? Nearest(Rect2 at)
    {
        Control? nearest = null;
        float best = float.MaxValue;
        foreach (var choice in ScreenChoices(Scope()))
        {
            Mark(choice, ScreenMeta);
            if (choice is BaseButton { Disabled: true }) continue;
            float distance = Shown(choice).GetCenter().DistanceSquaredTo(at.GetCenter());
            if (distance < best) (nearest, best) = (choice, distance);
        }
        return nearest;
    }

    /// <summary>Offers <paramref name="panel"/>'s answers each time it's shown.</summary>
    public static void OfferWhenShown(Control panel, Func<Control?>? preferred = null) =>
        panel.VisibilityChanged += () =>
        {
            if (panel.IsVisibleInTree()) Offer(panel, preferred?.Invoke());
        };

    /// <summary>The panel whose answer <paramref name="control"/> is, while that panel shows; else null.</summary>
    private static Control? PanelOf(Control control) =>
        control.HasMeta(PanelMeta) && control.GetMeta(PanelMeta).AsGodotObject() is Control panel && IsInstanceValid(panel)
        && panel.IsVisibleInTree() && control.IsVisibleInTree() ? panel : null;

    private static List<Control> Choices(Node node)
    {
        var found = new List<Control>();
        AddChoices(node, found);
        return found;
    }

    private static void AddChoices(Node node, List<Control> found)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            if (child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }) continue;
            if (child is BaseButton button) found.Add(button);
            else AddChoices(child, found);
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
                if (OS.GetEnvironment("ARCANUM_PAD_DEBUG") == "1")
                    GD.Print($"PAD {@event.AsText()} owner={owner?.GetType().Name}:{owner?.Name} path={owner?.GetPath()} choice={owner?.HasMeta(ChoiceMeta)} screen={owner?.HasMeta(ScreenMeta)}");
                // (Nothing under what covers the screen keeps it.)
                if (owner is not null && IsCovered(owner))
                {
                    owner.ReleaseFocus();
                    owner = null;
                }
                if (owner is null)
                {
                    bool offered = _preferred is { } preferred && IsInstanceValid(preferred) && preferred.IsVisibleInTree() && !IsCovered(preferred);
                    // No panel offered an answer: the screen's buttons, the preferred one first.
                    var first = MarkScreen();
                    var target = offered ? _preferred : first;
                    if (target is null) break;
                    target.GrabFocus();
                    GetViewport().SetInputAsHandled();
                }
                // Confirm on a panel's answer presses it at once: the panel may be built again before the button is
                // let go, and a button pressed on release would then never answer.
                else if (@event.IsActionPressed("ui_accept") && owner is Button { Disabled: false, ToggleMode: false } answer and not OptionButton && PanelOf(owner) is not null
                    && !GetTree().Root.GetEmbeddedSubwindows().Any(w => w.Visible))
                {
                    GetViewport().SetInputAsHandled();
                    answer.EmitSignal(BaseButton.SignalName.Pressed);
                    break;
                }
                // Buttons shown since the screen was looked over (another page, a list filled) become reachable too.
                // (Also when the focus is on something the scan doesn't reach, so the arrows can leave it.)
                else if (owner.HasMeta(ScreenMeta) || !owner.HasMeta(ChoiceMeta)) MarkScreen();
                // On a panel's answer the arrows go to the panel's nearest answer that way, and leave the panel only
                // when it has none there.
                if (owner is not null && PanelOf(owner) is { } panel && DirectionOf(@event) is { } panelDirection
                    && !GetTree().Root.GetEmbeddedSubwindows().Any(w => w.Visible))
                {
                    MarkScreen();
                    var next = Neighbor(owner, panelDirection, Choices(panel).Where(c => c.IsVisibleInTree()))
                               ?? Neighbor(owner, panelDirection, FocusTargets(ReachOf(owner)));
                    if (next is not null) next.GrabFocus();
                    GetViewport().SetInputAsHandled();
                }
                // Between the screen's controls the arrows go to the nearest one that way (cards overlapping in a
                // stack included), unless a menu opened over the screen has them.
                // With nothing that way the focus stays put (the engine's own navigation could send it to a control
                // that isn't a choice, such as a picture under a page, and leave the arrows stuck there).
                else if (owner is not null && owner.HasMeta(ScreenMeta) && DirectionOf(@event) is { } direction
                    && !GetTree().Root.GetEmbeddedSubwindows().Any(w => w.Visible))
                {
                    Neighbor(owner, direction, FocusTargets(ReachOf(owner)))?.GrabFocus();
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseButton { Pressed: true, Device: -1 }: // made from a touch
                UsingButtons = false;
                Board.CardNode.TouchBegan();
                break;
            case InputEventMouseButton { Pressed: true }:
            case InputEventScreenTouch { Pressed: true }:
                UsingButtons = false;
                break;
        }
    }

    private static Vector2? DirectionOf(InputEvent e) =>
        e.IsActionPressed("ui_left") ? Vector2.Left : e.IsActionPressed("ui_right") ? Vector2.Right
        : e.IsActionPressed("ui_up") ? Vector2.Up : e.IsActionPressed("ui_down") ? Vector2.Down : null;

    /// <summary>
    /// The control the arrows go to from <paramref name="from"/> in <paramref name="direction"/>: among those further
    /// that way, the one nearest along it, a sideways offset counting three times as much (so a card in the next column
    /// beats a filter above it); level ones, the nearest sideways.
    /// </summary>
    private static Control? Neighbor(Control from, Vector2 direction, IEnumerable<Control> targets)
    {
        var a = Shown(from);
        bool across = direction.X != 0;
        Control? best = null;
        float bestScore = float.MaxValue;
        foreach (var target in targets)
        {
            if (target == from || target is BaseButton { Disabled: true }) continue;
            var b = Shown(target);
            // Further that way: its middle beyond this one's.
            if ((b.GetCenter() - a.GetCenter()).Dot(direction) <= 1) continue;
            float along = across
                ? (direction.X > 0 ? b.Position.X - a.End.X : a.Position.X - b.End.X)
                : (direction.Y > 0 ? b.Position.Y - a.End.Y : a.Position.Y - b.End.Y);
            float offset = across
                ? Math.Max(0, Math.Max(b.Position.Y - a.End.Y, a.Position.Y - b.End.Y))
                : Math.Max(0, Math.Max(b.Position.X - a.End.X, a.Position.X - b.End.X));
            float level = across ? Math.Abs(b.GetCenter().Y - a.GetCenter().Y) : Math.Abs(b.GetCenter().X - a.GetCenter().X);
            // Off its line counts too: the next card of the same row comes before one of the row above that only
            // starts a little that way.
            float score = Math.Max(0, along) + offset * 3 + level * 0.5f;
            if (score < bestScore) (best, bestScore) = (target, score);
        }
        return best;
    }

    /// <summary>What the arrows may go to on screen: its buttons and cards, and the fields and sliders that take the focus.</summary>
    private List<Control> FocusTargets(Node node)
    {
        var found = new List<Control>();
        AddFocusTargets(node, found);
        return found;
    }

    private void AddFocusTargets(Node node, List<Control> found)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            if (child == this || child is VirtualPointer || child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }
                || child is CanvasLayer { Visible: false } || child is Window || child.HasMeta(OwnMeta)) continue;
            if (child is BaseButton button) found.Add(button);
            else if (child is Control { MouseFilter: not Control.MouseFilterEnum.Ignore } pressable && (pressable.HasMeta(PressMeta) || pressable.HasMeta(ReachMeta)))
                found.Add(pressable);
            else if (child is (Godot.Range and not ScrollBar) or LineEdit or TextEdit or ItemList or Tree && child is Control { FocusMode: Control.FocusModeEnum.All } field)
                found.Add(field);
            else AddFocusTargets(child, found);
        }
    }

    private static bool InCareer(Control control)
    {
        for (Node? node = control; node is not null; node = node.GetParent())
            if (node is Control { Theme: { } theme } && (theme == Career.CareerStyle.Notes || theme == Career.CareerStyle.Web)) return true;
        return false;
    }

    /// <summary>On the career's website: its theme is the nearest one (notes over the store's picture have their own, inside
    /// a screen with the website's).</summary>
    private static bool InWebsite(Control control)
    {
        for (Node? node = control; node is not null; node = node.GetParent())
            if (node is Control { Theme: { } theme }) return theme == Career.CareerStyle.Web;
        return false;
    }

    private static bool IsNavigation(InputEvent @event) =>
        @event.IsAction("ui_accept") || @event.IsAction("ui_left") || @event.IsAction("ui_right")
        || @event.IsAction("ui_up") || @event.IsAction("ui_down");

    public override void _Process(double delta)
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (!UsingButtons) _lost = null;
        else if (owner is not null && owner.HasMeta(ScreenMeta) && owner.IsVisibleInTree())
        {
            _lost = owner;
            _lostAt = Shown(owner);
        }
        else if (owner is null && _lost is not null && (!IsInstanceValid(_lost) || !_lost.IsVisibleInTree()))
        {
            // The focused card or button went away (the page was built again after it was used): the nearest one
            // left takes the focus.
            _lost = null;
            Nearest(_lostAt)?.GrabFocus();
            return;
        }
        // The career's website draws its own selection (a computer's highlight), so no ring there.
        bool show = UsingButtons && owner is not null && owner.HasMeta(ChoiceMeta) && owner.IsVisibleInTree()
                    && !(owner is BaseButton && InWebsite(owner));
        _ring.Visible = show;
        if (!show) return;
        // Inside a list scrolled by hand (the right stick, a finger) the focused answer may be partly or wholly out of
        // sight: the ring keeps to the part of it that shows, and goes away while none does.
        var style = InCareer(owner!) ? _careerRing : _ringBox;
        if (style != _ringShown)
        {
            _ringShown = style;
            _ring.QueueRedraw();
        }
        var rect = Shown(owner!).Grow(style == _careerRing ? 3 : 4);
        for (var parent = owner.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer { Visible: true } scroll)
                rect = rect.Intersection(scroll.GetGlobalRect());
        if (rect.Size.X < 8 || rect.Size.Y < 8)
        {
            _ring.Visible = false;
            return;
        }
        if (rect.Position == _ring.Position && rect.Size == _ring.Size) return;
        _ring.Position = rect.Position;
        _ring.Size = rect.Size;
        _ring.QueueRedraw();
    }
}
