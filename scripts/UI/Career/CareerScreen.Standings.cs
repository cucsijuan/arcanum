// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Career;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>The online standings: the rank from matches against people, and this month's points (from the career service).</summary>
public partial class CareerScreen
{
    private async void StandingsPage(VBoxContainer page)
    {
        page.AddChild(CareerStyle.Text("Online events count here: the rank moves with every match against a person, and each month starts the points again (3 a win, 1 a draw).", 19, CareerStyle.InkDim, wrap: true));
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 40);
        page.AddChild(columns);
        foreach (var (board, title) in new[] { ("rank", "Rank"), ("month", $"{Career.Now:MMMM} points") })
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddChild(CareerStyle.Text(title, 24, CareerStyle.TitleBar));
            var loading = CareerStyle.Text("Loading…", 18, CareerStyle.InkDim);
            column.AddChild(loading);
            columns.AddChild(column);
            var rows = await Career.LeaderboardAsync(board);
            if (!IsInstanceValid(column)) return;
            loading.QueueFree();
            if (rows is null) { column.AddChild(CareerStyle.Text("The standings need a Discord sign-in and the career service.", 18, CareerStyle.InkDim, wrap: true)); continue; }
            if (rows.Count == 0) column.AddChild(CareerStyle.Text("Nobody yet: play an online event!", 18, CareerStyle.InkDim));
            foreach (var row in rows)
                column.AddChild(CareerStyle.Text($"{row.Rank}. {row.Name}  {row.Score}", 20, row.Mine ? CareerStyle.Good : CareerStyle.Ink));
        }
    }
}
