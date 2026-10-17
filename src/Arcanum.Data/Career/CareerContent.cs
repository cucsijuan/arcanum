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

    /// <summary>
    /// The playmats the store sells, by id (the game's built-in playmats: grid, slate, felt, ocean, crimson, nebula and the
    /// picture playmats), and their prices. A career starts without one; the regulars and visitors each play on one of them.
    /// </summary>
    public IReadOnlyDictionary<string, int> Playmats { get; init; } = new Dictionary<string, int>
    {
        ["grid"] = 1500, ["slate"] = 1500, ["felt"] = 1500, ["ocean"] = 1500, ["crimson"] = 1500, ["nebula"] = 1800,
        ["white-dragon"] = 2000, ["blue-dragon"] = 2000, ["black-dragon"] = 2000, ["red-dragon"] = 2000, ["green-dragon"] = 2000,
        ["dark-lord"] = 2000, ["dwarves"] = 2000, ["elves"] = 2000, ["humans"] = 2000, ["riders"] = 2000,
    };

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

    /// <summary>The store's forum on the website: what the regulars write when they look for a game.</summary>
    public ForumSpec Forum { get; init; } = new();

    /// <summary>Single cards: the store's case, the forum's listings and the player's own sale posts.</summary>
    public MarketSpec Market { get; init; } = new();

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

/// <summary>
/// The website's forum: the regulars' posts offering ante and practice games (a title and a post, one of each at random),
/// and what they say when the player answers a practice post.
/// </summary>
public sealed record ForumSpec
{
    public string Name { get; init; } = "TCG Forum";
    public IReadOnlyList<string> AnteTitles { get; init; } = new[] { "Anyone up for an ante game?" };
    public IReadOnlyList<string> AnteBodies { get; init; } = new[] { "Looking for a game for keeps today. I've got a few cards I'm willing to put up. Send me a message!" };
    public IReadOnlyList<string> PracticeTitles { get; init; } = new[] { "Practice partner wanted" };
    public IReadOnlyList<string> PracticeBodies { get; init; } = new[] { "Testing a deck and need someone to play against. Just for fun, nothing on the line." };
    public IReadOnlyList<string> PracticeGreetings { get; init; } = new[] { "Hey, thanks for answering! Meet you at the store?" };
}

/// <summary>
/// Single cards (prices are fractions of a card's price of the day, see <c>CareerMarket.Price</c>). The store sells a case of
/// singles a day at a steady markup; other players list cards on the forum at prices that swing either way and haggle;
/// the player can post cards for sale there too, sold (or answered with a lower offer) over the following days. A
/// "special" printing is a card's other art in a set (borderless, showcase, extended art…): found in play boosters or
/// only in collector boosters. Nothing here applies before <see cref="Since"/>, so older careers replay as they were.
/// </summary>
public sealed record MarketSpec
{
    /// <summary>The first day singles are traded (the special-art prices start then too); null: never.</summary>
    public DateOnly? Since { get; init; }

    /// <summary>Cards in the store's case each day.</summary>
    public int SinglesPerDay { get; init; } = 20;

    /// <summary>The store's markup on a single by rarity (0.4: 40% over its price).</summary>
    public IReadOnlyDictionary<string, double> Markup { get; init; } = new Dictionary<string, double>
    {
        ["common"] = 0.4, ["uncommon"] = 0.35, ["rare"] = 0.3, ["mythic"] = 0.2, ["special"] = 0.3, ["bonus"] = 0.3,
    };

    /// <summary>How often each rarity is picked for a single (relative weights).</summary>
    public IReadOnlyDictionary<string, int> RarityWeights { get; init; } = new Dictionary<string, int>
    {
        ["common"] = 45, ["uncommon"] = 30, ["rare"] = 18, ["mythic"] = 7,
    };

    /// <summary>Chances that a single is a foil, a special art from play boosters, a special art from collector boosters.</summary>
    public double FoilChance { get; init; } = 0.15;
    public double SpecialPlayChance { get; init; } = 0.02;
    public double SpecialCollectorChance { get; init; } = 0.04;

    /// <summary>The forum has special arts this many times more often than the store.</summary>
    public double ForumSpecialFactor { get; init; } = 1.5;

    /// <summary>A special art's price compared with the card's usual price (also what the store pays back).</summary>
    public double SpecialPrice { get; init; } = 2;

    /// <summary>The least a special art costs, compared with the card's usual price: at the store, on the forum.</summary>
    public double StoreSpecialMin { get; init; } = 3;
    public double ForumSpecialMin { get; init; } = 2.5;

    /// <summary>Forum listings a day (between both).</summary>
    public int ListingsMin { get; init; } = 10;
    public int ListingsMax { get; init; } = 15;

    /// <summary>How far a forum price strays from the card's price by rarity: [least, most] (either way for listings; the most is the player's cap).</summary>
    public IReadOnlyDictionary<string, double[]> Spread { get; init; } = new Dictionary<string, double[]>
    {
        ["common"] = new[] { 0.3, 0.5 }, ["uncommon"] = new[] { 0.2, 0.4 }, ["rare"] = new[] { 0.15, 0.3 }, ["mythic"] = new[] { 0.1, 0.2 },
        ["special"] = new[] { 0.15, 0.3 }, ["bonus"] = new[] { 0.15, 0.3 },
    };

    /// <summary>A seller's lowest price, hidden, as a fraction of what they ask: between both.</summary>
    public double FloorMin { get; init; } = 0.85;
    public double FloorMax { get; init; } = 0.95;

    /// <summary>Offers the player can make on one listing.</summary>
    public int OfferAttempts { get; init; } = 2;

    /// <summary>Days a player's post stays up; how many posts at once.</summary>
    public int PostDays { get; init; } = 7;
    public int PostsMax { get; init; } = 10;

    /// <summary>Chance a day that a post sells: asking the card's price or less, and asking the most allowed (in between, in proportion).</summary>
    public double SellChanceFair { get; init; } = 0.5;
    public double SellChanceCap { get; init; } = 0.1;

    /// <summary>Chance a day of a lower offer instead, and how low it is (fractions of the asking price).</summary>
    public double OfferChance { get; init; } = 0.25;
    public double OfferMin { get; init; } = 0.7;
    public double OfferMax { get; init; } = 0.9;

    /// <summary>The forum's sale posts and the sellers' answers (one at random each time).</summary>
    public IReadOnlyList<string> ListingTitles { get; init; } = new[] { "WTS: {card}" };
    public IReadOnlyList<string> ListingBodies { get; init; } = new[] { "Selling my {card}. Asking {price}. Make me an offer if you like." };
    public IReadOnlyList<string> Accepts { get; init; } = new[] { "Deal! It's yours." };
    public IReadOnlyList<string> Counters { get; init; } = new[] { "Hmm, too low. Meet me at {price}?" };
    public IReadOnlyList<string> Declines { get; init; } = new[] { "No way, that's way too low." };
    public IReadOnlyList<string> BuyerOffers { get; init; } = new[] { "Would you take {price} for it?" };
}

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
/// <param name="When">"daily", a day of the week ("Wednesday"), a weekday of the month ("firstFriday", "thirdSaturday",
/// "lastSunday"…) or a date ("2026-10-24"); UTC days. The day the event starts (see <see cref="EventSpec.Days"/>).</param>
/// <param name="Kind">draft, constructed (a career deck, casual rules) or commander (a career Commander deck; pods of
/// <see cref="Players"/> play <see cref="Rounds"/> games, prizes by games won).</param>
/// <param name="Set">Draft: the set code, or "featured" for the week's featured set.</param>
/// <param name="Players">Seats, filled with computer players (the fewest, with <see cref="EventSpec.PlayersMax"/>).</param>
/// <param name="Rounds">Swiss rounds; 0: as many as the attendance calls for (see <see cref="CareerRules.SwissRounds"/>).</param>
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

    /// <summary>
    /// Constructed events: the id of the module format the decks must be legal in ("standard", "modern"…); empty: any cards (casual).
    /// Commander events always use the Commander format.
    /// </summary>
    public string Format { get; init; } = "";

    /// <summary>The months it is held in (1–12); empty: every month.</summary>
    public IReadOnlyList<int> Months { get; init; } = Array.Empty<int>();

    /// <summary>How many days it runs from the day it starts: a player enters on any of them and plays its rounds over them; the rounds not played by then are lost (the player is dropped).</summary>
    public int Days { get; init; } = 1;

    /// <summary>The attendance is drawn between <see cref="Players"/> and this (0: always <see cref="Players"/>).</summary>
    public int PlayersMax { get; init; }

    /// <summary>The top of the Swiss standings that plays a single-elimination playoff (8: a top 8); 0: none.</summary>
    public int TopCut { get; init; }

    /// <summary>A cut by record after this many Swiss rounds (day 1): only the players with at least <see cref="CutPoints"/> match points play on. 0: none.</summary>
    public int CutAfter { get; init; }
    public int CutPoints { get; init; }

    /// <summary>Only the players qualified for this run of it may enter (prizes with <see cref="PrizeSpec.Qualifies"/> its id qualify them).</summary>
    public bool Invitational { get; init; }

    /// <summary>Played at the big tournaments' table, on one mat for both players with the event's name on its crest (no playmats of their own).</summary>
    public bool TournamentMat { get; init; }

    /// <summary>The Swiss rounds' time limit in minutes (0: untimed); the playoff is untimed. See <see cref="Limited.LimitedEvent.RoundMinutes"/>.</summary>
    public int RoundMinutes { get; init; }
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
public sealed record PrizeSpec(int Place = 0, int Wins = -1, int Cash = 0, int Boosters = 0, int Tokens = 0, int Reputation = 0)
{
    /// <summary>The id of an event this prize qualifies the player for: its next run after this one.</summary>
    public string Qualifies { get; init; } = "";

    /// <summary>Only when the event had at least this many players (the next prize that fits applies otherwise).</summary>
    public int MinPlayers { get; init; }
}

/// <param name="Deck">Module deck name.</param>
/// <param name="Price">Price in cents; 0 uses the store's default.</param>
public sealed record PreconSpec(string Deck, int Price = 0)
{
    /// <summary>More decks sold together with <see cref="Deck"/> as one product (a starter kit of two decks built to play each other).</summary>
    public IReadOnlyList<string> With { get; init; } = Array.Empty<string>();

    /// <summary>The product's name when it is more than one deck.</summary>
    public string? Name { get; init; }

    /// <summary>Every deck the product contains, <see cref="Deck"/> first.</summary>
    public IEnumerable<string> AllDecks => With.Prepend(Deck);
}

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
