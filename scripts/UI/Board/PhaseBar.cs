// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Strip of turn steps with the current one highlighted. Each step has two stop markers: the upper one stops
/// during opponents' turns, the lower one during your own turn. Includes a "full control" toggle.
/// </summary>
public partial class PhaseBar : PanelContainer
{
    private static readonly (Step Step, string Short, string Name)[] Steps =
    {
        (Step.Upkeep, "UPK", "Upkeep"),
        (Step.Draw, "DRW", "Draw"),
        (Step.PrecombatMain, "M1", "Main 1"),
        (Step.BeginCombat, "BC", "Beginning of combat"),
        (Step.DeclareAttackers, "ATK", "Declare attackers"),
        (Step.DeclareBlockers, "BLK", "Declare blockers"),
        (Step.CombatDamage, "DMG", "Combat damage"),
        (Step.EndCombat, "EC", "End of combat"),
        (Step.PostcombatMain, "M2", "Main 2"),
        (Step.End, "END", "End step"),
    };

    private static readonly Color OwnStop = BoardStyle.Playable;
    private static readonly Color OpponentStop = BoardStyle.Blocking;

    private readonly Dictionary<Step, (Button Opponent, Panel Chip, Label Label, Button Own)> _chips = new();
    private readonly Button _fullControl = BoardStyle.MakeButton("FULL", 11);
    private AutoPassPolicy? _policy;
    private Step? _current;

    public PhaseBar()
    {
        AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.06f, 0.06f, 0.07f, 0.9f), 8, BoardStyle.PanelBorder, 1));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 3);

        var legend = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        legend.AddThemeConstantOverride("separation", 4);
        legend.AddChild(BoardStyle.MakeLabel("Their turn", 9, OpponentStop));
        legend.AddChild(BoardStyle.MakeLabel("", 9));
        legend.AddChild(BoardStyle.MakeLabel("Your turn", 9, OwnStop));
        row.AddChild(legend);

        foreach (var (step, shortName, name) in Steps)
        {
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 2);
            var opponent = Marker($"Stop at {name} during opponents' turns", () => _policy?.ToggleStop(ownTurn: false, step));
            var own = Marker($"Stop at {name} during your turn", () => _policy?.ToggleStop(ownTurn: true, step));
            var chip = new Panel { CustomMinimumSize = new Vector2(38, 22), MouseFilter = MouseFilterEnum.Ignore };
            var label = BoardStyle.MakeLabel(shortName, 11);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.SetAnchorsPreset(LayoutPreset.FullRect);
            chip.AddChild(label);
            column.AddChild(opponent);
            column.AddChild(chip);
            column.AddChild(own);
            row.AddChild(column);
            _chips[step] = (opponent, chip, label, own);
        }

        _fullControl.ToggleMode = true;
        _fullControl.CustomMinimumSize = new Vector2(44, 0);
        _fullControl.TooltipText = "Full control: stop at every priority, even with nothing to do";
        _fullControl.Toggled += on => _policy?.SetFullControl(on);
        row.AddChild(_fullControl);
        AddChild(row);
    }

    public void Bind(AutoPassPolicy policy)
    {
        if (_policy is not null) _policy.Changed -= UpdateVisuals;
        _policy = policy;
        _policy.Changed += UpdateVisuals;
        UpdateVisuals();
    }

    public void SetCurrentStep(Step? step)
    {
        _current = step;
        UpdateVisuals();
    }

    private static Button Marker(string tooltip, Action onPressed)
    {
        var marker = new Button { CustomMinimumSize = new Vector2(38, 9), FocusMode = FocusModeEnum.None, TooltipText = tooltip };
        marker.Pressed += onPressed;
        return marker;
    }

    private void UpdateVisuals()
    {
        foreach (var (step, (opponent, chip, label, own)) in _chips)
        {
            StyleMarker(opponent, _policy?.HasStop(ownTurn: false, step) ?? false, OpponentStop);
            StyleMarker(own, _policy?.HasStop(ownTurn: true, step) ?? false, OwnStop);
            bool current = step == _current;
            chip.AddThemeStyleboxOverride("panel", BoardStyle.Box(current ? BoardStyle.ActiveBorder : new Color("1c1d21"), 4,
                current ? null : BoardStyle.PanelBorder, current ? 0 : 1));
            label.AddThemeColorOverride("font_color", current ? new Color("16171a") : BoardStyle.TextDim);
        }
        _fullControl.SetPressedNoSignal(_policy?.FullControl ?? false);
        var fullStyle = BoardStyle.Box(_fullControl.ButtonPressed ? BoardStyle.Attacking : BoardStyle.Panel, 4, BoardStyle.PanelBorder, 1);
        _fullControl.AddThemeStyleboxOverride("normal", fullStyle);
        _fullControl.AddThemeStyleboxOverride("pressed", fullStyle);
    }

    private static void StyleMarker(Button marker, bool active, Color color)
    {
        var box = BoardStyle.Box(active ? color : new Color("2a2b30"), 3);
        box.SetContentMarginAll(0);
        var hover = BoardStyle.Box(active ? color.Lightened(0.2f) : new Color("3a3b42"), 3);
        hover.SetContentMarginAll(0);
        marker.AddThemeStyleboxOverride("normal", box);
        marker.AddThemeStyleboxOverride("pressed", box);
        marker.AddThemeStyleboxOverride("hover", hover);
    }
}
