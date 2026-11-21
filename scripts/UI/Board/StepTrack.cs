// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.State;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The turn's steps as a narrow column, top to bottom, for the compact board: the main phases, combat and the end step
/// by name, the other steps as dots between them, the current one lit (gold in this screen's turn, the stop color in
/// another player's). Whose turn it is shows above. Touching it opens the stops panel.
/// </summary>
public partial class StepTrack : Control
{
    /// <summary>Each step in order, and its name on the column (null: a dot).</summary>
    private static readonly (Step Step, string? Label)[] Marks =
    {
        (Step.Upkeep, null),
        (Step.Draw, null),
        (Step.PrecombatMain, "Main"),
        (Step.BeginCombat, null),
        (Step.DeclareAttackers, "Combat"),
        (Step.DeclareBlockers, null),
        (Step.CombatDamage, null),
        (Step.EndCombat, null),
        (Step.PostcombatMain, "Main"),
        (Step.End, "End"),
    };

    private const float Width = 64, NameHeight = 26, ChipHeight = 24, DotRoom = 18, ChipGap = 6;
    private static readonly Color Line = new(1, 1, 1, 0.22f), Dim = new("2a2b30"), Panel = new(0.06f, 0.06f, 0.07f, 0.82f);

    private Step? _current;
    private bool _ownTurn = true;
    private string _owner = "";
    private Color _ownerColor = Colors.White;

    /// <summary>The column was touched or clicked.</summary>
    public event Action? PanelRequested;

    public StepTrack()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Steps of the turn. Touch to choose where the game stops for you.";
        CustomMinimumSize = new Vector2(Width, Height());
        Size = CustomMinimumSize;
    }

    private static float Height() =>
        NameHeight + Marks.Sum(m => m.Label is null ? DotRoom : ChipHeight + 2 * ChipGap) + 8;

    public void SetCurrentStep(Step? step)
    {
        _current = step;
        QueueRedraw();
    }

    public void SetActivePlayer(string? name, bool ownTurn, Color color)
    {
        _owner = name ?? "";
        _ownTurn = ownTurn;
        _ownerColor = color.Lerp(Colors.White, 0.25f);
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
        PanelRequested?.Invoke();
        AcceptEvent();
    }

    public override void _Draw()
    {
        var font = BoardStyle.TitleFont ?? GetThemeDefaultFont();
        DrawStyleBox(BoardStyle.Box(Panel, 10, BoardStyle.PanelBorder, 1), new Rect2(Vector2.Zero, Size));
        float x = Size.X / 2;
        var lit = _ownTurn ? BoardStyle.ActiveBorder : PhaseBar.OpponentStop;

        // Whose turn, shortened to the column's width.
        var owner = _owner.Length > 7 ? _owner[..6] + "…" : _owner;
        DrawString(font, new Vector2(0, NameHeight - 8), owner, HorizontalAlignment.Center, Size.X, 13, _ownerColor);

        // The places of the marks, then the line through them, then the marks over it.
        var places = new List<float>();
        float y = NameHeight + 4;
        foreach (var (_, label) in Marks)
        {
            float room = label is null ? DotRoom : ChipHeight + 2 * ChipGap;
            places.Add(y + room / 2);
            y += room;
        }
        DrawLine(new Vector2(x, places[0]), new Vector2(x, places[^1]), Line, 2);
        for (int i = 0; i < Marks.Length; i++)
        {
            var (step, label) = Marks[i];
            bool current = step == _current;
            float at = places[i];
            if (label is null)
            {
                DrawCircle(new Vector2(x, at), current ? 7 : 4, current ? lit : Dim);
                if (!current) DrawArc(new Vector2(x, at), 4, 0, Mathf.Tau, 16, BoardStyle.PanelBorder, 1);
                continue;
            }
            var chip = new Rect2(4, at - ChipHeight / 2, Size.X - 8, ChipHeight);
            DrawStyleBox(BoardStyle.Box(current ? lit : new Color("1c1d21"), 5, current ? null : BoardStyle.PanelBorder, current ? 0 : 1), chip);
            var color = current ? new Color("16171a") : BoardStyle.TextDim;
            DrawString(font, new Vector2(chip.Position.X, at + 5), label, HorizontalAlignment.Center, chip.Size.X, 13, color);
        }
    }
}
