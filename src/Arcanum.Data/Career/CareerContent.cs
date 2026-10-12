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

    /// <summary>The prices of the sets' other boosters by kind (set, collector...); a kind without a price isn't sold.</summary>
    public IReadOnlyDictionary<string, int> BoosterPrices { get; init; } = new Dictionary<string, int> { ["set"] = 600, ["collector"] = 2500 };

    /// <summary>Default price of a preconstructed deck (a <see cref="PreconSpec"/> can set its own).</summary>
    public int PreconPrice { get; init; } = 2000;

    /// <summary>A traditional foil's value as a multiple of the card's.</summary>
    public double FoilMultiplier { get; init; } = 2;

    /// <summary>A surge foil's value as a multiple of the card's.</summary>
    public double SurgeMultiplier { get; init; } = 4;

    /// <summary>What the store pays for a card, as a fraction of its value.</summary>
    public double SellRate { get; init; } = 0.5;

    /// <summary>A card's value by rarity (common, uncommon, rare, mythic, special...); basic lands are worth nothing.</summary>
    public IReadOnlyDictionary<string, int> Values { get; init; } = new Dictionary<string, int>
    {
        ["common"] = 10, ["uncommon"] = 25, ["rare"] = 150, ["mythic"] = 500, ["special"] = 100, ["bonus"] = 300,
    };

    public StarterSpec Starter { get; init; } = new();

    public DailySpec Dailies { get; init; } = new();

    public PracticeSpec Practice { get; init; } = new();

    public AnteSpec Ante { get; init; } = new();

    /// <summary>Reputation tiers, lowest first: each one opens the regulars of its tier (and the ones below).</summary>
    public IReadOnlyList<TierSpec> Tiers { get; init; } = new[] { new TierSpec("Newcomer", 0) };

    /// <summary>The challenges a week's ones are drawn from.</summary>
    public IReadOnlyList<ChallengeSpec> Challenges { get; init; } = Array.Empty<ChallengeSpec>();

    public int ChallengesPerWeek { get; init; } = 3;

    public OfferSpec Offers { get; init; } = new();

    /// <summary>The store's calendar: events on fixed days of the week or of the month.</summary>
    public IReadOnlyList<EventSpec> Events { get; init; } = Array.Empty<EventSpec>();

    /// <summary>Names of visitors who fill an event's seats after the regulars.</summary>
    public IReadOnlyList<string> Visitors { get; init; } = Array.Empty<string>();

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

/// <param name="PaidPerDay">Practice matches a day that pay (the others count for nothing but practice).</param>
/// <param name="Win">Cash for a paid win.</param>
/// <param name="Loss">Cash for a paid loss.</param>
public sealed record PracticeSpec(int PaidPerDay = 3, int Win = 50, int Loss = 20);

/// <param name="PerDay">Ante matches a day.</param>
/// <param name="MinRatio">A regular plays only for a stake worth at least this share of what they put up.</param>
/// <param name="Offers">How many cards a regular offers to choose from.</param>
/// <param name="Declines">What a regular says turning a stake down (one at random).</param>
public sealed record AnteSpec(int PerDay = 3, double MinRatio = 0.8, IReadOnlyList<string>? Declines = null, int Offers = 4)
{
    public IReadOnlyList<string> Declines { get; init; } = Declines ?? new[] { "Nah, that's not worth it." };

    /// <summary>How a regular opens the chat, showing what they'd put up.</summary>
    public IReadOnlyList<string> Greetings { get; init; } = new[] { "Hey! Wanna play for keeps? Here's what I've got:" };

    /// <summary>How a regular asks for the player's stake once a card is chosen.</summary>
    public IReadOnlyList<string> AskStake { get; init; } = new[] { "Good pick. What are you putting up?" };

    /// <summary>How a regular takes a stake.</summary>
    public IReadOnlyList<string> Accepts { get; init; } = new[] { "Deal!" };

    /// <summary>How a regular ends the chat before the game.</summary>
    public IReadOnlyList<string> Farewells { get; init; } = new[] { "See you at the LGS, mate!" };
}

/// <param name="BargainRate">A bargain's price as a fraction of the card's value.</param>
/// <param name="TradeSpread">How far apart in value a trade's two cards may be (0.1: within 10%).</param>
/// <param name="RivalUpgrades">Cards a rival changes in their decks each week.</param>
/// <param name="RivalWeeks">Weeks rivals keep improving.</param>
public sealed record OfferSpec(double BargainRate = 0.6, double TradeSpread = 0.1, int RivalUpgrades = 2, int RivalWeeks = 12);

/// <param name="Name">Shown name ("Regular").</param>
/// <param name="Reputation">Reputation it takes.</param>
public sealed record TierSpec(string Name, int Reputation);

/// <summary>A goal for the week: <see cref="Kind"/> says what counts (see <see cref="CareerRules"/>).</summary>
/// <param name="Id">Stable id (kept in saves).</param>
/// <param name="Text">What to do, as shown.</param>
/// <param name="Kind">winDaily, winMatch, winAnte, winCheap (a deck worth at most Value cents), winMono, openBoosters, playPractice,
/// playEvent (the event <see cref="ChallengeSpec.Event"/> played through).</param>
/// <param name="Count">How many times.</param>
/// <param name="Value">The kind's amount (winCheap: the deck's highest value in cents).</param>
/// <param name="Cash">Reward in cents.</param>
/// <param name="Boosters">Reward in boosters of the featured set.</param>
public sealed record ChallengeSpec(string Id, string Text, string Kind, int Count = 1, int Value = 0, int Cash = 0, int Boosters = 0)
{
    /// <summary>playEvent: the id of the event that counts.</summary>
    public string Event { get; init; } = "";
}

/// <summary>An event on the store's calendar.</summary>
/// <param name="Id">Stable id (kept in saves).</param>
/// <param name="Name">Shown name.</param>
/// <param name="When">"daily", a day of the week ("Wednesday"), or "lastSunday" (the last Sunday of the month); UTC days.</param>
/// <param name="Kind">draft, constructed (a career deck, casual rules) or commander (a career Commander deck; pods of
/// <see cref="Players"/> play <see cref="Rounds"/> games, prizes by games won).</param>
/// <param name="Set">Draft: the set code, or "featured" for the week's featured set.</param>
/// <param name="Players">Seats, filled with computer players.</param>
/// <param name="Rounds">Swiss rounds.</param>
/// <param name="BestOf">Games per match.</param>
/// <param name="Entry">Entry fee in cents.</param>
/// <param name="EntryBoosters">Draft: may enter with this many boosters of the event's set instead (0: no).</param>
/// <param name="EntryTokens">May enter with this many qualification tokens instead (0: no).</param>
/// <param name="MinTier">The reputation tier it takes.</param>
/// <param name="Prizes">The first prize that fits the player's result is theirs.</param>
public sealed record EventSpec(string Id, string Name, string When, string Kind, string Set = "featured", int Players = 8, int Rounds = 3,
    int BestOf = 1, int Entry = 0, int EntryBoosters = 0, int EntryTokens = 0, int MinTier = 0, IReadOnlyList<PrizeSpec>? Prizes = null)
{
    public IReadOnlyList<PrizeSpec> Prizes { get; init; } = Prizes ?? Array.Empty<PrizeSpec>();

    /// <summary>Commander events: the module decks the computer players bring.</summary>
    public IReadOnlyList<string> Decks { get; init; } = Array.Empty<string>();

    /// <summary>Played online at set times too (null: only against the computer, any time of its day).</summary>
    public OnlineSpec? Online { get; init; }
}

/// <summary>
/// An event played online: it starts at set times (players check in before; seats left empty get computer players,
/// and matches against them pay as offline). Unless it's PvP only, it can also be played against the computer any time
/// of its day, for prizes scaled by <see cref="OfflineScale"/>.
/// </summary>
public sealed record OnlineSpec
{
    /// <summary>Start times of its day, UTC ("20:00").</summary>
    public IReadOnlyList<string> Starts { get; init; } = Array.Empty<string>();

    /// <summary>Minutes before each start that check-in opens.</summary>
    public int CheckIn { get; init; } = 30;

    /// <summary>Only online: no version against the computer.</summary>
    public bool PvpOnly { get; init; }

    /// <summary>The share of the prizes the version against the computer pays (0 to 1).</summary>
    public double OfflineScale { get; init; } = 0.5;
}

/// <summary>A prize for a place (1 = first; <see cref="Place"/> covers places up to it) or for a number of match wins.</summary>
public sealed record PrizeSpec(int Place = 0, int Wins = -1, int Cash = 0, int Boosters = 0, int Tokens = 0, int Reputation = 0);

/// <param name="Deck">Module deck name.</param>
/// <param name="Price">Price in cents; 0 uses the store's default.</param>
public sealed record PreconSpec(string Deck, int Price = 0);

/// <param name="Id">Stable id (kept in saves).</param>
/// <param name="Name">Shown name.</param>
/// <param name="Style">One line about how they play.</param>
/// <param name="Decks">Module deck names they play.</param>
/// <param name="Tier">The reputation tier the player needs to meet them (index in <see cref="CareerContent.Tiers"/>).</param>
/// <param name="Rival">A rival improves their decks every week of the career.</param>
public sealed record OpponentSpec(string Id, string Name, string Style = "", IReadOnlyList<string>? Decks = null, int Tier = 0, bool Rival = false)
{
    public IReadOnlyList<string> Decks { get; init; } = Decks ?? Array.Empty<string>();
}
