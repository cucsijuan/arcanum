// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>
/// A content module's career settings (career/career.json): the store, prices, how a career starts, daily matches,
/// the weekly featured sets and the store's regulars. Money is in cents.
/// </summary>
public sealed record CareerContent
{
    /// <summary>The store the career happens in.</summary>
    public string StoreName { get; init; } = "The card shop";

    public int BoosterPrice { get; init; } = 450;

    /// <summary>Default price of a preconstructed deck (a <see cref="PreconSpec"/> can set its own).</summary>
    public int PreconPrice { get; init; } = 2000;

    /// <summary>What the store pays for a card, as a fraction of its value.</summary>
    public double SellRate { get; init; } = 0.5;

    /// <summary>A card's value by rarity (common, uncommon, rare, mythic, special...); basic lands are worth nothing.</summary>
    public IReadOnlyDictionary<string, int> Values { get; init; } = new Dictionary<string, int>
    {
        ["common"] = 10, ["uncommon"] = 25, ["rare"] = 150, ["mythic"] = 500, ["special"] = 100, ["bonus"] = 300,
    };

    public StarterSpec Starter { get; init; } = new();

    public DailySpec Dailies { get; init; } = new();

    /// <summary>Sets whose boosters the store sells (each needs a booster definition).</summary>
    public IReadOnlyList<string> Sets { get; init; } = Array.Empty<string>();

    /// <summary>The featured set rotates every week (Monday, UTC) through this list; it is the daily matches' booster prize.</summary>
    public IReadOnlyList<string> Featured { get; init; } = Array.Empty<string>();

    /// <summary>Preconstructed decks the store sells (module deck names).</summary>
    public IReadOnlyList<PreconSpec> Precons { get; init; } = Array.Empty<PreconSpec>();

    /// <summary>The store's regulars, the daily matches' opponents.</summary>
    public IReadOnlyList<OpponentSpec> Opponents { get; init; } = Array.Empty<OpponentSpec>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CareerContent Parse(string json)
    {
        var content = JsonSerializer.Deserialize<CareerContent>(json, Options) ?? throw new FormatException("Empty career settings.");
        if (content.Featured.Count == 0) throw new FormatException("Career settings need at least one \"featured\" set.");
        if (content.Opponents.Count == 0 || content.Opponents.Any(o => o.Decks.Count == 0))
            throw new FormatException("Career settings need \"opponents\", each with at least one deck.");
        if (content.Starter.Decks.Count == 0) throw new FormatException("Career settings need starter \"decks\" to choose from.");
        return content;
    }
}

/// <param name="Cash">Cash a new career starts with.</param>
/// <param name="Boosters">Boosters of the featured set a new career starts with.</param>
/// <param name="Decks">The preconstructed decks a new career may start with (module deck names).</param>
public sealed record StarterSpec(int Cash = 1000, int Boosters = 3, IReadOnlyList<string>? Decks = null)
{
    public IReadOnlyList<string> Decks { get; init; } = Decks ?? Array.Empty<string>();
}

/// <param name="PerDay">Daily matches each real day (UTC); unplayed ones are lost.</param>
/// <param name="Win">Cash for a win when the player chose cash.</param>
/// <param name="Loss">Cash for a loss, whatever was chosen.</param>
/// <param name="WinBoosters">Boosters of the featured set for a win when the player chose boosters.</param>
public sealed record DailySpec(int PerDay = 2, int Win = 450, int Loss = 100, int WinBoosters = 1);

/// <param name="Deck">Module deck name.</param>
/// <param name="Price">Price in cents; 0 uses the store's default.</param>
public sealed record PreconSpec(string Deck, int Price = 0);

/// <param name="Id">Stable id (kept in saves).</param>
/// <param name="Name">Shown name.</param>
/// <param name="Style">One line about how they play.</param>
/// <param name="Decks">Module deck names they play.</param>
public sealed record OpponentSpec(string Id, string Name, string Style = "", IReadOnlyList<string>? Decks = null)
{
    public IReadOnlyList<string> Decks { get; init; } = Decks ?? Array.Empty<string>();
}
