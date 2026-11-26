// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Players;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The stops of the phase bar, big enough for a finger and reachable with a controller's directional pad: one row per
/// step, with its stop during opponents' turns and during this player's own turn, and full control.
/// </summary>
public partial class StopsPanel : PanelContainer
{
    private readonly List<(Button Button, Func<bool> IsOn, Color Color)> _toggles = new();
    private readonly Button _close = BoardStyle.MakeButton("Done", 18);
    private AutoPassPolicy? _policy;

    public StopsPanel()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AnchorLeft = AnchorRight = AnchorTop = AnchorBottom = 0.5f;
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Modal, BoardStyle.Panel, 12, BoardStyle.PanelBorder, 1, artPadding: 18));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(BoardStyle.MakeTitle("Stops", 26));
        var hint = BoardStyle.MakeLabel("The game stops at these steps for you to act; at the others it passes on.", 14, BoardStyle.TextDim);
        hint.TooltipText = PhaseBar.StopHint.Trim();
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(560, 0);
        box.AddChild(hint);

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);
        grid.AddChild(new Control());
        grid.AddChild(BoardStyle.MakeLabel("Their turns", 16, PhaseBar.OpponentStop, bold: true));
        grid.AddChild(BoardStyle.MakeLabel("Your turn", 16, PhaseBar.OwnStop, bold: true));
        foreach (var (step, _, name) in PhaseBar.Steps)
        {
            grid.AddChild(BoardStyle.MakeLabel(name, 18));
            grid.AddChild(Toggle(() => _policy?.HasStop(ownTurn: false, step) ?? false, () => _policy?.ToggleStop(ownTurn: false, step), PhaseBar.OpponentStop));
            grid.AddChild(Toggle(() => _policy?.HasStop(ownTurn: true, step) ?? false, () => _policy?.ToggleStop(ownTurn: true, step), PhaseBar.OwnStop));
        }
        box.AddChild(grid);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 10);
        var full = Toggle(() => _policy?.FullControl ?? false, () => _policy?.SetFullControl(!_policy.FullControl), BoardStyle.Attacking, "Full control");
        full.TooltipText = "Stop at every priority, even with nothing to do";
        full.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bottom.AddChild(full);
        _close.CustomMinimumSize = new Vector2(140, 40);
        _close.Pressed += Close;
        bottom.AddChild(_close);
        box.AddChild(bottom);
        AddChild(box);
    }

    public void Bind(AutoPassPolicy policy)
    {
        if (_policy is not null) _policy.Changed -= UpdateToggles;
        _policy = policy;
        _policy.Changed += UpdateToggles;
        UpdateToggles();
    }

    public void Open()
    {
        Visible = true;
        UpdateToggles();
        // Drawn smaller when the board is too short for it.
        ResetSize();
        float room = GetParentAreaSize().Y - 16;
        float fit = Mathf.Min(1, room / Mathf.Max(1, Size.Y));
        Scale = new Vector2(fit, fit);
        PivotOffset = Size / 2;
        OffsetLeft = -Size.X / 2; OffsetRight = Size.X / 2; OffsetTop = -Size.Y / 2; OffsetBottom = Size.Y / 2;
        _toggles[0].Button.GrabFocus(); // a controller starts on the first stop
    }

    public void Close() => Visible = false;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            Close();
            AcceptEvent();
        }
    }

    private Button Toggle(Func<bool> isOn, Action toggle, Color color, string? text = null)
    {
        var button = BoardStyle.MakeButton(text ?? "", 18, compact: true);
        button.CustomMinimumSize = new Vector2(text is null ? 150 : 200, 40);
        button.FocusMode = FocusModeEnum.All;
        button.Pressed += toggle;
        _toggles.Add((button, isOn, color));
        return button;
    }

    private void UpdateToggles()
    {
        if (!Visible) return; // brought up to date when it opens
        foreach (var (button, isOn, color) in _toggles)
        {
            bool on = isOn();
            if (button.Text is "" or "On" or "Off") button.Text = on ? "On" : "Off";
            var normal = BoardStyle.Box(on ? color.Darkened(0.25f) : new Color("2a2b30"), 6, on ? color : BoardStyle.PanelBorder, 2);
            var focus = BoardStyle.Box(on ? color.Darkened(0.1f) : new Color("3a3b42"), 6, Colors.White, 3);
            button.AddThemeStyleboxOverride("normal", normal);
            button.AddThemeStyleboxOverride("pressed", normal);
            button.AddThemeStyleboxOverride("hover", focus);
            button.AddThemeStyleboxOverride("focus", focus);
            button.AddThemeColorOverride("font_color", on ? new Color("16171a") : BoardStyle.Text);
            button.AddThemeColorOverride("font_focus_color", on ? new Color("16171a") : BoardStyle.Text);
            button.AddThemeColorOverride("font_hover_color", on ? new Color("16171a") : BoardStyle.Text);
        }
    }
}
