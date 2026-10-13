// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Limited;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>The set's draft statistics as the player sees them in career drafts: a grade at each card's foot and the module's credit line.</summary>
public static class DraftGrades
{
    /// <summary>The statistics of the event's set when the player may see them, else null.</summary>
    public static DraftRatings? ForPlayer(LimitedService service) => service.RatingsForPlayer ? service.Ratings : null;

    /// <summary>A dark strip across the foot of a card tile, with a short text.</summary>
    public static void Strip(Control tile, string text, Color color)
    {
        var strip = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1,
            OffsetLeft = 4, OffsetRight = -4, OffsetBottom = -4, GrowVertical = Control.GrowDirection.Begin,
        };
        strip.AddThemeStyleboxOverride("panel", CareerStyle.Box(new Color(0.07f, 0.08f, 0.1f, 0.88f), null, 0, 8, 3));
        var label = CareerStyle.Text(text, 15, color);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        strip.AddChild(label);
        tile.AddChild(strip);
    }

    /// <summary>Marks a card tile with its grade and win rate, and explains them in the card's tooltip.</summary>
    public static void Mark(Control tile, Control card, DraftRatings ratings, string name)
    {
        if (ratings.Stats(name) is not { } stats) return;
        var grade = ratings.Grade(name);
        var text = grade is null ? "–" : stats.InHandWinRate is { } rate ? $"{grade} · {rate:P1}" : grade;
        Strip(tile, text, GradeColor(grade));
        var lines = new List<string>();
        if (stats.InHandWinRate is { } wr)
            lines.Add($"Grade {grade ?? "–"}: in hand it won {wr:P1} of {stats.Games:N0} games (the set's average is {ratings.Mean:P1})."
                      + (grade is null ? " Too few games for a grade." : ""));
        if (stats.TakenAt is { } at) lines.Add($"Usually taken around pick {at:0.#} of a booster.");
        card.TooltipText = (card.TooltipText.Length > 0 ? card.TooltipText + "\n" : "") + string.Join("\n", lines);
    }

    private static Color GradeColor(string? grade) => grade?[0] switch
    {
        'A' => new Color("7be08a"),
        'B' => new Color("c5e87a"),
        'C' => new Color("f2e6c4"),
        'D' => new Color("f0a35a"),
        'F' => new Color("ef6a5a"),
        _ => new Color(1, 1, 1, 0.6f),
    };

    /// <summary>The module's credit line for the statistics, to show wherever the grades are.</summary>
    public static Label Credit(DraftRatings ratings, Color color) => CareerStyle.Text(ratings.Credit, 14, color, wrap: true);
}
