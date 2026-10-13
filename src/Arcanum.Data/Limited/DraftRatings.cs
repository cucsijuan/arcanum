// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Limited;

/// <summary>A card's draft statistics: the win rate of the games it was in hand, in how many games, and the pick it was taken at on average.</summary>
public sealed record CardDraftStats(double? InHandWinRate, int Games, double? TakenAt, int Picks);

/// <summary>
/// A set's draft statistics from the module (ratings/&lt;set&gt;.json): how each card did in the community's drafts. The
/// computer players draft and build with them; career drafts may show them as grades.
/// </summary>
public sealed class DraftRatings
{
    /// <summary>Fewer games than this and a card's win rate says little: it is not graded and counts less.</summary>
    public const int MinGames = 500;

    /// <summary>The line the module asks to show wherever the ratings are shown.</summary>
    public string Credit { get; init; } = "";
    public string Set { get; init; } = "";
    public int Games { get; init; }

    /// <summary>The average and the spread of the in-hand win rates of the cards with enough games.</summary>
    public double Mean { get; init; } = 0.55;
    public double Spread { get; init; } = 0.03;

    private readonly Dictionary<string, CardDraftStats> _cards = new(StringComparer.OrdinalIgnoreCase);

    public static DraftRatings Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ratings = new DraftRatings
        {
            Credit = root.TryGetProperty("credit", out var c) ? c.GetString() ?? "" : "",
            Set = root.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "",
            Games = root.TryGetProperty("games", out var g) ? g.GetInt32() : 0,
            Mean = root.TryGetProperty("mean", out var m) ? m.GetDouble() : 0.55,
            Spread = root.TryGetProperty("sd", out var sd) && sd.GetDouble() > 0 ? sd.GetDouble() : 0.03,
        };
        foreach (var card in root.GetProperty("cards").EnumerateObject())
        {
            var v = card.Value;
            ratings._cards[card.Name] = new CardDraftStats(
                v.TryGetProperty("gih", out var gih) ? gih.GetDouble() : null,
                v.TryGetProperty("games", out var games) ? games.GetInt32() : 0,
                v.TryGetProperty("ata", out var ata) ? ata.GetDouble() : null,
                v.TryGetProperty("picks", out var picks) ? picks.GetInt32() : 0);
        }
        return ratings;
    }

    /// <summary>A card's statistics, by its name or its front face's name; null when the set has none for it.</summary>
    public CardDraftStats? Stats(string name) =>
        _cards.TryGetValue(name, out var stats) || name.Contains(" // ") && _cards.TryGetValue(name[..name.IndexOf(" // ", StringComparison.Ordinal)], out stats)
            ? stats : null;

    /// <summary>How many spreads above (or below) the set's average the card's in-hand win rate is; null without enough games.</summary>
    public double? Deviation(string name) =>
        Stats(name) is { InHandWinRate: { } rate, Games: >= MinGames } ? (rate - Mean) / Spread : null;

    /// <summary>
    /// The card's value on the computer players' 0–10 scale (5 is an average card of the set, each spread above
    /// adds 1.5), and how much to trust it (0–1, from the games played); null when the set has no win rate for it.
    /// </summary>
    public (double Score, double Confidence)? Score(string name)
    {
        if (Stats(name) is not { InHandWinRate: { } rate, Games: > 0 } stats) return null;
        double score = Math.Clamp(5 + (rate - Mean) / Spread * 1.5, 0, 10);
        return (score, stats.Games / (double)(stats.Games + MinGames));
    }

    private static readonly (double Above, string Grade)[] Grades =
    {
        (2.0, "A+"), (1.5, "A"), (1.2, "A-"), (0.8, "B+"), (0.5, "B"), (0.2, "B-"),
        (-0.2, "C+"), (-0.5, "C"), (-0.8, "C-"), (-1.2, "D+"), (-1.5, "D"), (-2.0, "D-"),
    };

    /// <summary>A letter grade from the card's deviation (A+ two spreads above the average, C around it, F two below); null without enough games.</summary>
    public string? Grade(string name) =>
        Deviation(name) is { } z ? Grades.FirstOrDefault(g => z >= g.Above).Grade ?? "F" : null;
}
