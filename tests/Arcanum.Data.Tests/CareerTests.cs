// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.Career;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

/// <summary>Career rules: starting, daily matches, the store, boosters, the clock and saves.</summary>
public class CareerTests
{
    private static string Card(int i, string type = "Creature — Bear") =>
        $$"""{"oracle_id":"o-{{i}}","name":"Card {{i}}","layout":"normal","mana_cost":"{1}","type_line":"{{type}}","oracle_text":"","power":"1","toughness":"1"}""";

    private static string Print(int i, string rarity) =>
        $$"""{"oracle_id":"o-{{i}}","id":"p-abc-{{i}}","set":"abc","set_name":"Alphabet","collector_number":"{{i}}","rarity":"{{rarity}}","released_at":"2024-01-01","booster":true,"lang":"en"}""";

    /// <summary>Commons 1–10, uncommons 11–15, rares 16–17, a mythic 18, a basic land 19.</summary>
    private static CardDatabase Database()
    {
        var cards = Enumerable.Range(1, 18).Select(i => Card(i)).Append(Card(19, "Basic Land — Forest"));
        var prints = Enumerable.Range(1, 10).Select(i => Print(i, "common"))
            .Concat(Enumerable.Range(11, 5).Select(i => Print(i, "uncommon")))
            .Concat(new[] { Print(16, "rare"), Print(17, "rare"), Print(18, "mythic"), Print(19, "common") });
        static Stream Text(IEnumerable<string> lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return new CardDatabase(OracleJsonl.WithPrintings(OracleJsonl.Import(Text(cards)), OracleJsonl.ImportPrintings(Text(prints))));
    }

    private const string SetJson = """
        {
          "code": "abc", "name": "Alphabet",
          "booster": {
            "name": "Booster",
            "sheets": {
              "common": { "rarity": "common", "basic": false },
              "uncommon": { "rarity": "uncommon" },
              "rare": { "rarity": "rare" },
              "mythic": { "rarity": "mythic" }
            },
            "slots": [
              { "count": 5, "sheet": "common" },
              { "count": 2, "sheet": "uncommon" },
              { "count": 1, "sheets": { "rare": 7, "mythic": 1 } }
            ]
          }
        }
        """;

    private const string ContentJson = """
        {
          "storeName": "Test Store",
          "boosterPrice": 400,
          "preconPrice": 1500,
          "sellRate": 0.5,
          "starter": { "cash": 1000, "boosters": 3, "decks": ["starter", "sixty"] },
          "dailies": { "perDay": 2, "win": 400, "loss": 100, "winBoosters": 1 },
          "sets": ["abc"],
          "featured": ["abc"],
          "precons": [{ "deck": "other" }],
          "practice": { "paidPerDay": 2, "win": 50, "loss": 20 },
          "ante": { "perDay": 2 },
          "tiers": [{ "name": "Newcomer", "reputation": 0 }, { "name": "Regular", "reputation": 10 }],
          "challenges": [
            { "id": "w2", "text": "Win 2 matches", "kind": "winMatch", "count": 2, "cash": 300 },
            { "id": "open", "text": "Open a booster", "kind": "openBoosters", "count": 1, "boosters": 1 }
          ],
          "challengesPerWeek": 2,
          "events": [
            { "id": "draft", "name": "Draft night", "when": "Wednesday", "kind": "draft", "players": 8, "rounds": 3, "entry": 1500, "entryBoosters": 3,
              "prizes": [{ "wins": 3, "boosters": 6, "reputation": 10 }, { "wins": 2, "boosters": 3 }, { "wins": 0, "reputation": 1 }] },
            { "id": "sat", "name": "Saturday tournament", "when": "Saturday", "kind": "constructed", "players": 8, "rounds": 3, "entry": 2000,
              "prizes": [{ "place": 1, "cash": 10000, "tokens": 1 }, { "place": 4, "cash": 2000 }, { "reputation": 2 }] },
            { "id": "cmd", "name": "Commander night", "when": "Friday", "kind": "commander", "players": 4, "rounds": 2, "entry": 500,
              "prizes": [{ "wins": 1, "boosters": 2 }, { "wins": 0, "reputation": 3 }] },
            { "id": "champ", "name": "Championship", "when": "lastSunday", "kind": "constructed", "players": 16, "rounds": 4, "entry": 5000, "entryTokens": 1, "minTier": 1 }
          ],
          "opponents": [
            { "id": "ana", "name": "Ana", "decks": ["other"] },
            { "id": "bo", "name": "Bo", "decks": ["starter"] },
            { "id": "cy", "name": "Cy", "decks": ["other"], "tier": 1 },
            { "id": "dee", "name": "Dee", "decks": ["sixty"], "tier": 5, "rival": true }
          ]
        }
        """;

    private static readonly Dictionary<string, string> Decks = new()
    {
        ["starter"] = "4 Card 1 (ABC) 1\n2 Card 16 (ABC) 16\n10 Card 19 (ABC) 19",
        ["sixty"] = "4 Card 1 (ABC) 1\n56 Card 19 (ABC) 19",
        ["other"] = "4 Card 2 (ABC) 2\n1 Card 18 (ABC) 18",
    };

    /// <summary>The test world; the week's challenges only when asked (they pay, which other tests don't count on).</summary>
    private static CareerWorld World(bool challenges = false, string? season = null) =>
        new(CareerContent.Parse(challenges ? ContentJson : ContentJson.Replace("\"challengesPerWeek\": 2", "\"challengesPerWeek\": 0")), Database(), new[] { SetDefinition.Parse(SetJson) },
            name => Decks.GetValueOrDefault(name), season: season is null ? null : CareerSeason.Parse(season));

    private const string SeasonJson = """
        {
          "schemaVersion": 1,
          "changes": [
            { "from": "2026-10-19", "until": "2026-10-25", "cancel": ["draft"], "featured": "xyz", "boosterPrice": 300, "boosterPrices": { "collector": 2000 },
              "news": "Sale week!" },
            { "from": "2026-10-24", "until": "2026-10-24", "events": [
                { "id": "special", "name": "Special sealed", "when": "2026-10-24", "kind": "draft", "set": "abc", "entry": 500 },
                { "id": "sat", "name": "Saturday tournament", "when": "Saturday", "kind": "constructed", "entry": 100 } ] }
          ]
        }
        """;

    private static readonly DateTime T0 = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private static (CareerState State, List<CareerAction> Journal, CareerWorld World) Started()
    {
        var world = World();
        var state = new CareerState();
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "starter") };
        CareerRules.Apply(state, journal[0], world);
        return (state, journal, world);
    }

    private static CareerResult Do(CareerState state, List<CareerAction> journal, CareerWorld world, CareerAction action)
    {
        var result = CareerRules.Apply(state, action, world);
        journal.Add(action);
        return result;
    }

    [Fact]
    public void ACareerStartsWithItsDeckCashAndBoostersOfTheFeaturedSet()
    {
        var (state, _, _) = Started();
        Assert.Equal(1000, state.Cash);
        Assert.Equal(4, state.Owned(new CardKey("Card 1", "ABC", "1")));
        Assert.Equal(16, state.CollectionSize);
        Assert.Equal(3, state.Sealed["abc"]);
        Assert.Equal(CareerRules.DayOf(T0), state.Day);
    }

    [Fact]
    public void OnlyStartingDecksCanStartACareer()
    {
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(new CareerState(), new StartCareer(T0, 1, "other"), World()));
    }

    [Fact]
    public void BoostersOpenFromTheSeedSoReplayingGivesTheSameCards()
    {
        var (state, journal, world) = Started();
        var first = Do(state, journal, world, new OpenBooster(T0.AddMinutes(1), "abc")).Cards;
        var second = Do(state, journal, world, new OpenBooster(T0.AddMinutes(2), "abc")).Cards;
        Assert.Equal(8, first.Count);
        Assert.NotEqual(first, second);
        Assert.Equal(1, state.Sealed["abc"]);

        var replayed = CareerRules.Replay(journal, World());
        Assert.Equal(state.Collection, replayed.Collection);
        Assert.Equal(state.BoostersOpened, replayed.BoostersOpened);
    }

    [Fact]
    public void TwoDailyMatchesADayThatAreNotBanked()
    {
        var (state, journal, world) = Started();
        for (int i = 0; i < 2; i++)
        {
            Do(state, journal, world, new StartDaily(T0.AddMinutes(10 * i + 1), CareerRules.CashReward));
            Do(state, journal, world, new FinishMatch(T0.AddMinutes(10 * i + 5), Won: true));
        }
        Assert.Equal(1800, state.Cash);
        Assert.Equal(0, CareerRules.DailiesLeft(state, world, T0.AddHours(1)));
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartDaily(T0.AddHours(1), CareerRules.CashReward), world));
        Assert.Contains("No daily", refused.Message);

        // Three days later there are two again, not six.
        var later = T0.AddDays(3);
        Assert.Equal(2, CareerRules.DailiesLeft(state, world, later));
        Do(state, journal, world, new StartDaily(later, CareerRules.BoosterReward));
        Do(state, journal, world, new FinishMatch(later.AddMinutes(5), Won: true));
        Assert.Equal(4, state.Sealed["abc"]);
        Assert.Equal(1, CareerRules.DailiesLeft(state, world, later.AddMinutes(6)));
    }

    [Fact]
    public void ALostDailyStillPaysAndTheOpponentIsFixedForThatMatch()
    {
        var (state, journal, world) = Started();
        var expected = CareerRules.NextDailyOpponent(state, world, T0.AddMinutes(1));
        Assert.Equal(expected, CareerRules.NextDailyOpponent(state, world, T0.AddMinutes(30)));
        Do(state, journal, world, new StartDaily(T0.AddMinutes(1), CareerRules.BoosterReward));
        Assert.Equal(expected.Opponent.Id, state.Pending!.OpponentId);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartDaily(T0.AddMinutes(2), CareerRules.CashReward), world));
        Do(state, journal, world, new FinishMatch(T0.AddMinutes(9), Won: false));
        Assert.Equal(1100, state.Cash);
        Assert.Equal(3, state.Sealed["abc"]);
        Assert.Equal(1, state.Losses);
        Assert.Null(state.Pending);
    }

    [Fact]
    public void TheStoreSellsBoostersAndDecksForCash()
    {
        var (state, journal, world) = Started();
        Do(state, journal, world, new BuyBooster(T0.AddMinutes(1), "abc"));
        Assert.Equal(600, state.Cash);
        Assert.Equal(4, state.Sealed["abc"]);
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyPrecon(T0.AddMinutes(2), "other"), world));
        Assert.Contains("Not enough cash", refused.Message);
        Assert.Equal(600, state.Cash);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyBooster(T0.AddMinutes(2), "zzz"), world));
    }

    [Fact]
    public void PlaymatsAreBoughtOnceAndPutOnTheTable()
    {
        var (state, journal, world) = Started();
        Assert.Equal("", state.Playmat);
        Assert.Contains("Not enough cash", Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyPlaymat(T0.AddMinutes(1), "felt"), world)).Message);
        state.Cash = 5000;
        Do(state, journal, world, new BuyPlaymat(T0.AddMinutes(1), "felt"));
        Assert.Equal(3500, state.Cash);
        Assert.Equal("felt", state.Playmat);
        Do(state, journal, world, new BuyPlaymat(T0.AddMinutes(2), "nebula"));
        Assert.Equal(1700, state.Cash);
        Assert.Equal("nebula", state.Playmat);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyPlaymat(T0.AddMinutes(3), "felt"), world));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyPlaymat(T0.AddMinutes(3), "lace"), world));
        Do(state, journal, world, new UsePlaymat(T0.AddMinutes(3), "felt"));
        Assert.Equal("felt", state.Playmat);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new UsePlaymat(T0.AddMinutes(4), "ocean"), world));
        Do(state, journal, world, new UsePlaymat(T0.AddMinutes(4), ""));
        Assert.Equal("", state.Playmat);
        Assert.Equal(CareerRules.OpponentPlaymat(world, "Ana"), CareerRules.OpponentPlaymat(world, "Ana"));
    }

    [Fact]
    public void BetterOpponentsPlayOnBetterPlaymats()
    {
        var content = World().Content with
        {
            Playmats = new Dictionary<string, int> { ["a"] = 100, ["b"] = 200, ["c"] = 300, ["d"] = 400 },
            Tiers = new[] { new TierSpec("Low", 0), new TierSpec("Mid", 10), new TierSpec("Top", 20) },
        };
        var names = Enumerable.Range(0, 200).Select(i => $"Player {i}").ToList();
        content = content with { Opponents = names.Select(n => new OpponentSpec(n, n, Tier: 2)).ToList() };
        var top = new CareerWorld(content, Database(), new[] { SetDefinition.Parse(SetJson) }, _ => null);
        var low = new CareerWorld(content with { Opponents = Array.Empty<OpponentSpec>() }, Database(), new[] { SetDefinition.Parse(SetJson) }, _ => null);

        // Visitors (and the lowest tier): often none, otherwise the cheaper half.
        var visitors = names.Select(n => CareerRules.OpponentPlaymat(low, n)).ToList();
        Assert.InRange(visitors.Count(m => m is null), 80, 160);
        Assert.All(visitors.OfType<string>(), m => Assert.Contains(m, new[] { "a", "b" }));
        // The top tier: always one, from the dearer half.
        Assert.All(names.Select(n => CareerRules.OpponentPlaymat(top, n)), m => Assert.Contains(m, new[] { "c", "d" }));
    }

    [Fact]
    public void SellingPaysHalfTheValueAndNeedsTheCards()
    {
        var (state, journal, world) = Started();
        var rare = new CardKey("Card 16", "ABC", "16").ToString();
        int each = CareerMarket.SellPrice(state, world, CardKey.Parse(rare), state.Day);
        Assert.Equal((int)Math.Floor(CareerMarket.Price(state, world, CardKey.Parse(rare), state.Day) * 0.5), each);
        Do(state, journal, world, new SellCard(T0.AddMinutes(1), rare, 2));
        Assert.Equal(1000 + 2 * each, state.Cash);
        Assert.Equal(0, state.Owned(CardKey.Parse(rare)));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new SellCard(T0.AddMinutes(2), rare, 1), world));
        Assert.Equal(0, world.Value(new CardKey("Card 19", "ABC", "19")));
    }

    [Fact]
    public void TheClockNeverGoesBackAndARefusedActionChangesNothing()
    {
        var (state, journal, world) = Started();
        Do(state, journal, world, new BuyBooster(T0.AddHours(2), "abc"));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new OpenBooster(T0.AddHours(1), "abc"), world));
        var lastUtc = state.LastUtc;
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuyBooster(T0.AddDays(2), "zzz"), world));
        Assert.Equal(lastUtc, state.LastUtc);
        Assert.Equal(CareerRules.DayOf(T0), state.Day);
    }

    [Fact]
    public void DecksNeedTheCardsButBasicLandsAreFree()
    {
        var (state, _, world) = Started();
        var deck = DeckList.Parse("4 Card 1\n3 Card 16\n20 Card 19\n1 Card 5");
        var missing = CareerRules.Missing(deck, state, world);
        Assert.Equal(new[] { new MissingCard("Card 16", 3, 2), new MissingCard("Card 5", 1, 0) }, missing);
    }

    [Fact]
    public void SavesKeepTheJournalAndStateAndNoticeChanges()
    {
        var (state, journal, world) = Started();
        Do(state, journal, world, new OpenBooster(T0.AddMinutes(1), "abc"));
        Do(state, journal, world, new StartDaily(T0.AddMinutes(2), CareerRules.CashReward));
        var save = new CareerSave { Content = "test 1.0", Journal = journal, State = state };
        var key = CareerSave.DefaultKey;
        var bytes = save.Write(key);

        var loaded = CareerSave.Read(bytes, key);
        Assert.Equal(3, loaded.Journal.Count);
        Assert.IsType<StartDaily>(loaded.Journal[2]);
        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(loaded.State));
        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(CareerRules.Replay(loaded.Journal, world)));

        bytes[^5] ^= 1;
        Assert.Throws<InvalidDataException>(() => CareerSave.Read(bytes, key));
    }

    [Fact]
    public void CareerRandomnessDependsOnlyOnItsInputs()
    {
        var a = CareerRandom.For(7, "booster", 3);
        var b = CareerRandom.For(7, "booster", 3);
        var c = CareerRandom.For(7, "booster", 4);
        var sa = Enumerable.Range(0, 20).Select(_ => a.Next(1000)).ToList();
        Assert.Equal(sa, Enumerable.Range(0, 20).Select(_ => b.Next(1000)));
        Assert.NotEqual(sa, Enumerable.Range(0, 20).Select(_ => c.Next(1000)));
    }

    private static string Sixty => Decks["sixty"];

    private static (CareerState State, List<CareerAction> Journal, CareerWorld World) StartedWithSixty(bool challenges = false)
    {
        var world = World(challenges);
        var state = new CareerState();
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "sixty") };
        CareerRules.Apply(state, journal[0], world);
        return (state, journal, world);
    }

    [Fact]
    public void MatchesNeedADeckOfOwnedCards()
    {
        var (state, journal, world) = StartedWithSixty();
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartPractice(T0.AddMinutes(1), "ana", "8 Card 1\n52 Card 19"), world));
        Assert.Contains("You have 4 of the 8 Card 1", refused.Message);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartDaily(T0.AddMinutes(1), CareerRules.CashReward, "4 Card 1\n10 Card 19"), world));
        Do(state, journal, world, new StartDaily(T0.AddMinutes(1), CareerRules.CashReward, Sixty));
    }

    [Fact]
    public void PracticePaysOnlyTheFirstMatchesOfTheDay()
    {
        var (state, journal, world) = StartedWithSixty();
        for (int i = 0; i < 3; i++)
        {
            Do(state, journal, world, new StartPractice(T0.AddMinutes(10 * i + 1), "bo", Sixty));
            Do(state, journal, world, new FinishMatch(T0.AddMinutes(10 * i + 5), Won: true));
        }
        Assert.Equal(1000 + 2 * 50, state.Cash);
        Assert.Equal(0, CareerRules.PaidPracticeLeft(state, world, T0.AddHours(1)));
        Assert.Equal(2, CareerRules.PaidPracticeLeft(state, world, T0.AddDays(1)));
    }

    [Fact]
    public void AnteTakesACardOutUntilTheMatchEndsAndTheWinnerKeepsBoth()
    {
        var (state, journal, world) = StartedWithSixty();
        int before = state.CollectionSize;
        Do(state, journal, world, new StartAnte(T0.AddMinutes(1), "ana", Sixty));
        var pending = state.Pending!;
        Assert.Equal(CareerRules.Ante, pending.Kind);
        Assert.Equal(before - 1, state.CollectionSize);
        Assert.Contains(CardKey.Parse(pending.OpponentAnte).Name, new[] { "Card 2", "Card 18" });
        Do(state, journal, world, new FinishMatch(T0.AddMinutes(9), Won: true));
        Assert.Equal(before + 1, state.CollectionSize);
        Assert.Equal(1, state.Owned(CardKey.Parse(pending.OpponentAnte)));

        Do(state, journal, world, new StartAnte(T0.AddMinutes(10), "ana", Sixty));
        var lost = CardKey.Parse(state.Pending!.PlayerAnte);
        int owned = state.OwnedByName(lost.Name);
        Do(state, journal, world, new FinishMatch(T0.AddMinutes(19), Won: false));
        Assert.Equal(owned, state.OwnedByName(lost.Name)); // it stays gone
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartAnte(T0.AddMinutes(20), "ana", Sixty), world));

        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(CareerRules.Replay(journal, World())));
    }

    [Fact]
    public void ReputationOpensTheNextTiersRegulars()
    {
        var (state, journal, world) = StartedWithSixty();
        Assert.DoesNotContain(CareerRules.Opponents(state, world), o => o.Id == "cy");
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartPractice(T0.AddMinutes(1), "cy", Sixty), world));
        for (int i = 0; i < 4; i++)
        {
            Do(state, journal, world, new StartAnte(T0.AddDays(i).AddMinutes(1), "ana", Sixty));
            Do(state, journal, world, new FinishMatch(T0.AddDays(i).AddMinutes(5), Won: true));
            Do(state, journal, world, new StartDaily(T0.AddDays(i).AddMinutes(6), CareerRules.CashReward, Sixty));
            Do(state, journal, world, new FinishMatch(T0.AddDays(i).AddMinutes(9), Won: true));
        }
        Assert.True(state.Reputation >= 10);
        Assert.Equal(1, CareerRules.Tier(state, world));
        Do(state, journal, world, new StartPractice(T0.AddDays(5), "cy", Sixty));
    }

    [Fact]
    public void WeeklyChallengesCountAndPayOnce()
    {
        var (state, journal, world) = StartedWithSixty(challenges: true);
        var week = CareerRules.WeekChallenges(state, world, state.Day);
        Assert.Equal(2, week.Count);
        int cash = state.Cash;
        for (int i = 0; i < 3; i++)
        {
            Do(state, journal, world, new StartPractice(T0.AddMinutes(10 * i + 1), "bo", Sixty));
            Do(state, journal, world, new FinishMatch(T0.AddMinutes(10 * i + 5), Won: true));
        }
        var winTwo = week.First(c => c.Id == "w2");
        Assert.Equal(2, CareerRules.Progress(state, winTwo, state.Day));
        Assert.Equal(cash + 2 * 50 + 300, state.Cash); // the third win is unpaid practice and the challenge pays once
        int sealedBefore = state.Sealed["abc"];
        Do(state, journal, world, new OpenBooster(T0.AddHours(1), "abc"));
        Assert.Equal(sealedBefore, state.Sealed["abc"]); // opened one, got one
        Assert.Contains(state.Notes, n => n.Text.StartsWith("Challenge done: Open a booster"));
    }

    [Fact]
    public void ADailyEventHappensEveryDayAndCountsForItsChallenge()
    {
        var json = ContentJson
            .Replace("\"challengesPerWeek\": 2", "\"challengesPerWeek\": 3")
            .Replace("{ \"id\": \"open\", \"text\": \"Open a booster\"",
                "{ \"id\": \"drafts\", \"text\": \"Play 2 daily drafts\", \"kind\": \"playEvent\", \"event\": \"daily\", \"count\": 2, \"cash\": 700 },\n            { \"id\": \"open\", \"text\": \"Open a booster\"")
            .Replace("\"events\": [", "\"events\": [\n            { \"id\": \"daily\", \"name\": \"Daily draft\", \"when\": \"daily\", \"kind\": \"draft\", \"players\": 8, \"rounds\": 3, \"entry\": 100, \"prizes\": [{ \"wins\": 2, \"boosters\": 1, \"cash\": 450 }] },");
        var world = new CareerWorld(CareerContent.Parse(json), Database(), new[] { SetDefinition.Parse(SetJson) }, name => Decks.GetValueOrDefault(name));
        for (int d = 0; d < 7; d++) Assert.Contains(CareerRules.EventsOn(world, new DateOnly(2026, 10, 12).AddDays(d)), e => e.Id == "daily");
        var state = new CareerState();
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "sixty") };
        CareerRules.Apply(state, journal[0], world);
        var drafts = CareerRules.WeekChallenges(state, world, state.Day).First(c => c.Id == "drafts");
        int cash = state.Cash;
        var pool = Enumerable.Range(1, 24).Select(i => new CardKey($"Card {1 + i % 15}", "ABC", $"{1 + i % 15}").ToString()).ToList();
        for (int day = 0; day < 2; day++)
        {
            var at = T0.AddDays(day).AddMinutes(1);
            Do(state, journal, world, new EnterEvent(at, "daily", CareerRules.PayCash));
            Do(state, journal, world, new FinishEvent(at.AddHours(1), day == 0 ? 2 : 0, day == 0 ? 1 : 3, 0, 4, pool));
            Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(at.AddHours(2), "daily", CareerRules.PayCash), world)); // once a day
        }
        Assert.Equal(2, CareerRules.Progress(state, drafts, state.Day));
        Assert.Equal(cash - 2 * 100 + 450 + 700 + 300, state.Cash); // two entries, one prize, the drafts challenge and "win 2 matches"
    }

    [Fact]
    public void TheStoresOffersStayAllDayAndCanBeTakenOnce()
    {
        var (state, journal, world) = StartedWithSixty();
        var offers = CareerRules.Offers(state, world, state.Day);
        Assert.Equal(offers, CareerRules.Offers(state, world, state.Day));
        var bargain = offers.First(o => o.Kind == CareerRules.Bargain);
        var bought = CardKey.Parse(bargain.Get);
        Assert.Contains(bought.Name, new[] { "Card 16", "Card 17", "Card 18" });
        Assert.Equal((int)(CareerMarket.Price(state, world, bought, state.Day) * 0.6), bargain.Price);
        int cash = state.Cash;
        Do(state, journal, world, new TakeOffer(T0.AddMinutes(1), bargain.Index));
        Assert.Equal(cash - bargain.Price, state.Cash);
        Assert.True(state.Owned(bought) >= 1);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new TakeOffer(T0.AddMinutes(2), bargain.Index), world));
        Assert.True(CareerRules.OfferTaken(state, state.Day, bargain.Index));
    }

    [Fact]
    public void TradesAskForACardOfTheCollectionOfAboutTheSameValue()
    {
        var (state, journal, world) = StartedWithSixty();
        // The player owns only Card 1 (a common): trades for commons never come (only better cards are offered), so a day
        // with a trade needs a card worth more. Give them rares by opening boosters, then look for a trade.
        for (int i = 0; i < 3; i++) Do(state, journal, world, new OpenBooster(T0.AddMinutes(1 + i), "abc"));
        for (int d = 1; d < 120; d++)
        {
            var utc = T0.AddDays(d).AddMinutes(1);
            Do(state, journal, world, new BuyBooster(utc, "abc")); // the day begins; its offers are made
            var trade = state.Offers.FirstOrDefault(o => o.Kind == CareerRules.Trade);
            if (trade is null) { state.Cash += 400; continue; }
            var give = CardKey.Parse(trade.Give);
            Assert.True(state.OwnedByName(give.Name) >= 1);
            var day = CareerRules.DayOf(utc);
            Assert.InRange(CareerMarket.Price(state, world, give, day), CareerMarket.Price(state, world, CardKey.Parse(trade.Get), day) * 0.9,
                CareerMarket.Price(state, world, CardKey.Parse(trade.Get), day) * 1.1);
            int before = state.OwnedByName(give.Name);
            Do(state, journal, world, new TakeOffer(utc.AddMinutes(1), trade.Index));
            Assert.Equal(before - 1, state.OwnedByName(give.Name));
            return;
        }
        Assert.Fail("No trade in 120 days.");
    }

    [Fact]
    public void RivalsImproveTheirDecksEveryWeek()
    {
        var (state, journal, world) = StartedWithSixty();
        var first = CareerRules.OpponentDeck(state, world, "dee", "sixty");
        Assert.Equal(4, first.Main.Where(e => e.Name == "Card 1").Sum(e => e.Count));
        Do(state, journal, world, new BuyBooster(T0.AddDays(21), "abc"));
        var later = CareerRules.OpponentDeck(state, world, "dee", "sixty");
        Assert.Equal(60, later.Main.Sum(e => e.Count));
        Assert.True(later.Main.Where(e => e.Name == "Card 1").Sum(e => e.Count) < 4);
        Assert.True(CareerRules.DeckValue(later, world) > CareerRules.DeckValue(first, world));
        Assert.Equal(later.Export(), CareerRules.OpponentDeck(state, world, "dee", "sixty").Export());
        // A regular who isn't a rival keeps their deck.
        Assert.Equal(world.Deck("other").Export(), CareerRules.OpponentDeck(state, world, "ana", "other").Export());
    }

    [Fact]
    public void EventsHappenOnTheirDays()
    {
        var world = World();
        var wednesday = new DateOnly(2026, 10, 14);
        Assert.Equal(new[] { "draft" }, CareerRules.EventsOn(world, wednesday).Select(e => e.Id));
        Assert.Empty(CareerRules.EventsOn(world, wednesday.AddDays(1)));
        Assert.Equal(new[] { "champ" }, CareerRules.EventsOn(world, new DateOnly(2026, 10, 25)).Select(e => e.Id)); // the last Sunday of October
        Assert.Empty(CareerRules.EventsOn(world, new DateOnly(2026, 10, 18)));
        Assert.Equal(("draft", wednesday), CareerRules.Upcoming(world, new DateOnly(2026, 10, 12)).Select(x => (x.Event.Id, x.Day)).First());
    }

    [Fact]
    public void ADraftNightIsEnteredOnceAndItsCardsAndPrizesAreKept()
    {
        var (state, journal, world) = StartedWithSixty();
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(T0.AddMinutes(1), "draft", CareerRules.PayCash), world)); // Monday
        var wednesday = T0.AddDays(2);
        Do(state, journal, world, new EnterEvent(wednesday, "draft", CareerRules.PayBoosters));
        Assert.False(state.Sealed.ContainsKey("abc")); // the three starting boosters paid for it
        Assert.Equal("abc", state.Event!.Set);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(wednesday.AddMinutes(1), "draft", CareerRules.PayCash), world));
        int before = state.CollectionSize;
        var pool = Enumerable.Range(1, 24).Select(i => new CardKey($"Card {1 + i % 15}", "ABC", $"{1 + i % 15}").ToString()).ToList();
        Do(state, journal, world, new FinishEvent(wednesday.AddHours(2), 2, 1, 0, 3, pool));
        Assert.Equal(before + 24, state.CollectionSize);
        Assert.Equal(3, state.Sealed["abc"]);
        Assert.Null(state.Event);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(wednesday.AddHours(3), "draft", CareerRules.PayCash), world));
    }

    [Fact]
    public void ConstructedEventsTakeAnOwnedDeckAndPayByPlace()
    {
        var (state, journal, world) = StartedWithSixty();
        state.Cash = 5000;
        var saturday = T0.AddDays(5);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(saturday, "sat", CareerRules.PayCash), world));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(saturday, "sat", CareerRules.PayCash, "8 Card 1\n52 Card 19"), world));
        Do(state, journal, world, new EnterEvent(saturday, "sat", CareerRules.PayCash, Sixty));
        Assert.Equal(3000, state.Cash);
        Do(state, journal, world, new FinishEvent(saturday.AddHours(2), 3, 0, 0, 1));
        Assert.Equal(13000, state.Cash);
        Assert.Equal(1, state.Tokens);

        // The championship takes a token, but only from a Regular.
        var sunday = T0.AddDays(13);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(sunday, "champ", CareerRules.PayTokens, Sixty), world));
        state.Reputation = 10;
        Do(state, journal, world, new EnterEvent(sunday, "champ", CareerRules.PayTokens, Sixty));
        Assert.Equal(0, state.Tokens);
    }

    [Fact]
    public void AConstructedEventInAFormatTakesOnlyDecksLegalInIt()
    {
        var (state, journal, _) = StartedWithSixty();
        const string season = """
            { "schemaVersion": 1, "changes": [ { "from": "2026-10-12", "events": [
                { "id": "sat", "name": "Saturday tournament", "when": "Saturday", "kind": "constructed", "format": "standard", "entry": 100,
                  "prizes": [{ "reputation": 1 }] } ] } ] }
            """;
        var standard = Arcanum.Data.Formats.FormatRules.Parse("""{ "id": "standard", "name": "Standard", "legality": "standard" }""");
        var world = new CareerWorld(CareerContent.Parse(ContentJson.Replace("\"challengesPerWeek\": 2", "\"challengesPerWeek\": 0")), Database(),
            new[] { SetDefinition.Parse(SetJson) }, name => Decks.GetValueOrDefault(name), new[] { standard }, CareerSeason.Parse(season));
        var saturday = T0.AddDays(5);
        // The starter cards have no legality in that format: the deck can't enter.
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(saturday, "sat", CareerRules.PayCash, Sixty), world));
        Assert.Equal("standard", world.FormatOf(world.Event("sat", DateOnly.FromDateTime(saturday))!).Id);

        // The season check knows the module's formats.
        var unknown = CareerSeason.Parse(season.Replace("\"standard\"", "\"nonesuch\""));
        Assert.Contains(unknown.Problems(world.Content, world.Sets, new HashSet<string>(), new HashSet<string> { "standard" }), p => p.Contains("no format 'nonesuch'"));
        Assert.DoesNotContain(CareerSeason.Parse(season).Problems(world.Content, world.Sets, new HashSet<string>(), new HashSet<string> { "standard" }), p => p.Contains("format"));
    }

    [Fact]
    public void FoilsAreKeptApartAndWorthMore()
    {
        var (state, journal, world) = StartedWithSixty();
        var foil = new CardKey("Card 16", "ABC", "16", Foil: true);
        Assert.Equal(foil, CardKey.Parse(foil.ToString()));
        Assert.Equal(new CardKey("Card 16", "ABC", "16"), CardKey.Parse("Card 16|ABC|16"));
        Assert.Equal(2 * world.Value(foil with { Foil = false }), world.Value(foil));
        Assert.True(CardKey.Parse(foil.ToString()).ToEntry().Foil);
    }

    [Fact]
    public void CommanderNightTakesACommanderDeck()
    {
        var (state, journal, world) = StartedWithSixty();
        var friday = T0.AddDays(4);
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(friday, "cmd", CareerRules.PayCash, Sixty), world));
        Assert.Contains("Commander", refused.Message + string.Join(" ", CareerRules.DeckProblems(DeckList.Parse(Sixty), state, world, commander: true)));
        Assert.Empty(CareerRules.DeckProblems(DeckList.Parse(Sixty), state, world));
    }

    [Fact]
    public void PricesFollowTheWeeksMetaAndDriftDeterministically()
    {
        var (state, _, world) = StartedWithSixty();
        var day = state.Day;
        var meta = CareerMarket.Meta(state, world, CareerRules.WeekOf(day));
        Assert.Equal(2, meta.Count);
        Assert.Equal(meta.Select(m => m.Opponent.Id), CareerMarket.Meta(state, world, CareerRules.WeekOf(day)).Select(m => m.Opponent.Id));
        // A card of a winning deck sells higher than its drift alone would put it.
        var hot = meta[0].Deck.Main.First(e => !world.IsBasicLand(e.Name)).Name;
        Assert.True(CareerMarket.Factor(state, world, hot, day) > 0.85 * 1.6 * 0.9 - 0.001);
        for (int d = 0; d < 60; d++)
            Assert.InRange(CareerMarket.Factor(state, world, "Card 5", day.AddDays(d)), 0.5, 3);
        Assert.Equal(0, CareerMarket.Price(state, world, new CardKey("Card 19", "ABC", "19"), day));
    }

    [Fact]
    public void AnAnteStakeIsChosenAndTheRegularPlaysOnlyForAFairOne()
    {
        var (state, journal, world) = StartedWithSixty();
        // A regular never puts up a basic land, and the offer stays the same while the player looks.
        for (int d = 0; d < 30; d++)
            Assert.All(CareerRules.AnteOfferOf(state, world, "bo", state.Day.AddDays(d)).Cards, c => Assert.False(world.IsBasicLand(c.Card.Name)));
        var offer = CareerRules.AnteOfferOf(state, world, "ana", state.Day);
        Assert.Equal(offer.Cards, CareerRules.AnteOfferOf(state, world, "ana", state.Day).Cards);
        Assert.Equal(2, offer.Cards.Count); // ana's deck has two different cards besides lands
        var stake = new CardKey("Card 1", "ABC", "1");
        var cheap = offer.Cards[0];
        var dear = offer.Cards[^1];
        Assert.False(CareerRules.AnteAccepted(state, world, dear, stake, state.Day)); // a common for a mythic: no
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartAnte(T0.AddMinutes(1), "ana", Sixty, stake.ToString(), dear.Card.ToString()), world));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartAnte(T0.AddMinutes(1), "ana", Sixty, stake.ToString(), "Card 7|ABC|7"), world));
        if (!CareerRules.AnteAccepted(state, world, cheap, stake, state.Day)) return;
        Do(state, journal, world, new StartAnte(T0.AddMinutes(1), "ana", Sixty, stake.ToString(), cheap.Card.ToString()));
        Assert.Equal(stake.ToString(), state.Pending!.PlayerAnte);
        Assert.Equal(cheap.Card.ToString(), state.Pending.OpponentAnte);
        Assert.Equal(3, state.Owned(stake));
    }

    [Fact]
    public void AStakeMustBeAnOwnedCard()
    {
        var (state, _, world) = StartedWithSixty();
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartAnte(T0.AddMinutes(1), "bo", Sixty, new CardKey("Card 16", "ABC", "16").ToString()), world));
    }

    [Fact]
    public void AnyCardOfTheCollectionCanBeTheStake()
    {
        var (state, journal, world) = StartedWithSixty();
        Do(state, journal, world, new OpenBooster(T0.AddMinutes(1), "abc"));
        // The dearest card opened, not in the deck, against the regular's cheapest card.
        var stake = state.Cards.Select(c => c.Card).Where(c => c.Name != "Card 1" && !world.IsBasicLand(c.Name))
            .OrderByDescending(c => CareerMarket.Price(state, world, c, state.Day)).First();
        var theirs = CareerRules.AnteOfferOf(state, world, "ana", state.Day).Cards[0];
        Assert.True(CareerRules.AnteAccepted(state, world, theirs, stake, state.Day));
        Do(state, journal, world, new StartAnte(T0.AddMinutes(2), "ana", Sixty, stake.ToString(), theirs.Card.ToString()));
        Assert.Equal(stake.ToString(), state.Pending!.PlayerAnte);
    }

    [Fact]
    public void TheSeasonChangesTheCalendarAndPricesOnlyOnItsDays()
    {
        var world = World(season: SeasonJson);
        var before = new DateOnly(2026, 10, 14);
        Assert.Equal(new[] { "draft" }, CareerRules.EventsOn(world, before).Select(e => e.Id));
        Assert.Equal("abc", world.FeaturedSet(before));
        Assert.Equal(400, world.BoosterPrice(null, before));
        Assert.Empty(world.News(before));

        // The sale week: no draft night, another featured set, cheaper boosters, a line of news.
        Assert.Empty(CareerRules.EventsOn(world, new DateOnly(2026, 10, 21)));
        Assert.Equal("xyz", world.FeaturedSet(new DateOnly(2026, 10, 21)));
        Assert.Equal(300, world.BoosterPrice(null, new DateOnly(2026, 10, 21)));
        Assert.Equal(2000, world.BoosterPrice("collector", new DateOnly(2026, 10, 21)));
        Assert.Equal(new[] { "Sale week!" }, world.News(new DateOnly(2026, 10, 21)));

        // One Saturday: a one-off event, and the Saturday tournament costs less.
        var saturday = CareerRules.EventsOn(world, new DateOnly(2026, 10, 24));
        Assert.Equal(new[] { "sat", "special" }, saturday.Select(e => e.Id));
        Assert.Equal(100, saturday[0].Entry);
        Assert.Equal(2000, world.Event("sat", new DateOnly(2026, 10, 31))!.Entry);
        Assert.Equal(new[] { "draft" }, CareerRules.EventsOn(world, new DateOnly(2026, 10, 28)).Select(e => e.Id));
    }

    [Fact]
    public void ASeasonFileMayOnlyChangeTheFuture()
    {
        var published = CareerSeason.Parse(SeasonJson);
        var today = new DateOnly(2026, 10, 20);
        // Adding a change from tomorrow on is fine.
        var added = published with { Changes = published.Changes.Append(new SeasonChange(today.AddDays(1)) { Featured = "abc" }).ToList() };
        Assert.Null(published.ChangedPast(added, today));
        // Changing a week that has started isn't: those days have been played.
        var edited = published with { Changes = new[] { published.Changes[0] with { BoosterPrice = 250 }, published.Changes[1] } };
        Assert.NotNull(published.ChangedPast(edited, today));
        Assert.Null(published.ChangedPast(edited, new DateOnly(2026, 10, 18)));
        Assert.Throws<FormatException>(() => CareerSeason.Parse("{ \"schemaVersion\": 99 }"));
    }

    [Fact]
    public void OnlineEventsPayTheirShareAgainstTheComputerAndPvpOnlyOnesCantBePlayedOffline()
    {
        const string season = """
            { "changes": [ { "from": "2026-10-12", "events": [
                { "id": "draft", "name": "Draft night", "when": "Wednesday", "kind": "draft", "players": 8, "rounds": 3, "entry": 1500,
                  "prizes": [{ "wins": 3, "boosters": 6, "tokens": 1, "reputation": 10 }, { "wins": 0, "reputation": 1 }],
                  "online": { "starts": ["18:00", "21:00"], "offlineScale": 0.5 } },
                { "id": "pvp", "name": "Online cup", "when": "Wednesday", "kind": "draft", "entry": 1000, "prizes": [{ "place": 1, "boosters": 8 }],
                  "online": { "starts": ["20:00"], "pvpOnly": true } } ] } ] }
            """;
        var world = World(season: season);
        var state = new CareerState();
        CareerRules.Apply(state, new StartCareer(T0, 42, "starter"), world);
        var wednesday = new DateTime(2026, 10, 14, 19, 0, 0, DateTimeKind.Utc);
        var draft = world.Event("draft", DateOnly.FromDateTime(wednesday))!;
        Assert.Equal(new PrizeSpec(Wins: 3, Boosters: 3, Tokens: 0, Reputation: 5), CareerRules.PrizeFor(draft, 3, 1));
        Assert.Equal(6, CareerRules.PrizeFor(draft, 3, 1, online: true)!.Boosters);
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(wednesday, "pvp", CareerRules.PayCash), world));
        Assert.Contains("online only", refused.Message);

        var broken = CareerSeason.Parse(season.Replace("\"18:00\", \"21:00\"", "\"6pm\"").Replace("\"pvpOnly\": true", "\"pvpOnly\": true, \"checkIn\": 1"));
        var problems = broken.Problems(world.Content, world.Sets, new HashSet<string>());
        Assert.Contains(problems, p => p.Contains("'6pm'"));
        Assert.Contains(problems, p => p.Contains("check-in"));
    }

    // ---------------------------------------------------------------- singles: the store's case, the forum, sale posts

    private static (CareerState State, List<CareerAction> Journal, CareerWorld World) Trading()
    {
        var world = new CareerWorld(CareerContent.Parse(ContentJson.Replace("\"challengesPerWeek\": 2", "\"challengesPerWeek\": 0")
                .Replace("\"sets\": [\"abc\"],", "\"sets\": [\"abc\"], \"visitors\": [\"Zed\"], \"market\": { \"since\": \"2026-10-12\" },")),
            Database(), new[] { SetDefinition.Parse(SetJson) }, name => Decks.GetValueOrDefault(name));
        var state = new CareerState();
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "starter") };
        CareerRules.Apply(state, journal[0], world);
        return (state, journal, world);
    }

    [Fact]
    public void SinglesAreOnlyTradedFromTheMarketsFirstDay()
    {
        var (state, _, world) = Started();
        Assert.Empty(CareerRules.StoreSingles(state, world, state.Day));
        Assert.Empty(CareerRules.Listings(state, world, state.Day));
    }

    [Fact]
    public void NoSingleOrListingCostsLessThanTheStorePaysForIt()
    {
        var (state, _, world) = Trading();
        for (int d = 0; d < 120; d++)
        {
            var day = state.Day.AddDays(d);
            var singles = CareerRules.StoreSingles(state, world, day);
            Assert.Equal(20, singles.Count);
            foreach (var single in singles) Assert.True(single.Price > CareerMarket.SellPrice(state, world, single.Card, day), $"{single.Card} on {day}");
            var listings = CareerRules.Listings(state, world, day);
            Assert.InRange(listings.Count, 10, 15);
            foreach (var listing in listings)
            {
                int storePays = CareerMarket.SellPrice(state, world, listing.Card, day);
                Assert.True(listing.Floor > storePays && listing.Floor <= listing.Price, $"{listing.Card} on {day}");
                // Bought at its lowest and posted back at the cap, it still sells for no more than the cap.
                Assert.True(CareerRules.SaleCap(state, world, listing.Card, day) >= CareerMarket.Price(state, world, listing.Card, day));
            }
        }
    }

    [Fact]
    public void BuyingASingleAndAListingTakesTheirPrices()
    {
        var (state, journal, world) = Trading();
        state.Cash = 100000;
        var single = CareerRules.StoreSingles(state, world, state.Day)[0];
        int cash = state.Cash, owned = state.Owned(single.Card);
        Do(state, journal, world, new BuySingle(T0.AddMinutes(1), 0));
        Assert.Equal(cash - single.Price, state.Cash);
        Assert.Equal(owned + 1, state.Owned(single.Card));
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new BuySingle(T0.AddMinutes(2), 0), world));

        var listing = CareerRules.Listings(state, world, state.Day)[0];
        cash = state.Cash;
        Do(state, journal, world, new BuyListing(T0.AddMinutes(3), 0));
        Assert.Equal(cash - listing.Price, state.Cash);
        Assert.True(CareerRules.ListingBought(state, state.Day, 0));
    }

    [Fact]
    public void ASellerTakesAnOfferAtTheirFloorCountersOneJustBelowAndTurnsDownLowOnes()
    {
        var (state, journal, world) = Trading();
        state.Cash = 100000;
        var listing = CareerRules.Listings(state, world, state.Day).First(l => l.Floor >= 20);
        int i = listing.Index;
        Do(state, journal, world, new OfferOnListing(T0.AddMinutes(1), i, 1));
        Assert.False(CareerRules.ListingBought(state, state.Day, i));
        Assert.Equal(listing.Price, CareerRules.ListingPrice(state, listing, state.Day)); // too low to counter
        Do(state, journal, world, new OfferOnListing(T0.AddMinutes(2), i, (int)Math.Ceiling(listing.Floor * 0.9)));
        Assert.True(CareerRules.ListingPrice(state, listing, state.Day) < listing.Price); // a counter-offer
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new OfferOnListing(T0.AddMinutes(3), i, listing.Floor), world)); // no more offers today

        var other = CareerRules.Listings(state, world, state.Day).First(l => l.Index != i);
        int cash = state.Cash;
        Do(state, journal, world, new OfferOnListing(T0.AddMinutes(4), other.Index, other.Floor));
        Assert.True(CareerRules.ListingBought(state, state.Day, other.Index));
        Assert.Equal(cash - other.Floor, state.Cash);
    }

    [Fact]
    public void APostIsSoldOfferedOrExpiresOverTheFollowingDaysAndNeverAboveTheCap()
    {
        var (state, journal, world) = Trading();
        var card = state.Cards.First(c => c.Card.Name == "Card 16").Card;
        int cap = CareerRules.SaleCap(state, world, card, state.Day);
        Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new PostSale(T0.AddMinutes(1), card.ToString(), cap + 1), world));
        int owned = state.Owned(card);
        int ask = CareerMarket.Price(state, world, card, state.Day);
        Do(state, journal, world, new PostSale(T0.AddMinutes(2), card.ToString(), ask));
        Assert.Equal(owned - 1, state.Owned(card)); // out of the collection while it's up
        var post = state.Posts.Single();
        for (int d = 1; d <= 9 && post.Status == SaleStatus.Open; d++) Do(state, journal, world, new CheckSales(T0.AddDays(d)));
        Assert.NotEqual(SaleStatus.Open, post.Status);
        int cash = state.Cash;
        if (post.Status == SaleStatus.Offered)
        {
            Assert.True(post.Offer < ask);
            Do(state, journal, world, new AnswerSaleOffer(T0.AddDays(10), post.Id, true));
            Assert.Equal(SaleStatus.Sold, post.Status);
            Assert.Equal(cash + post.SoldFor, state.Cash);
        }
        else if (post.Status == SaleStatus.Expired) Assert.Equal(owned, state.Owned(card));

        // The journal replayed gives the same career (what the career service checks).
        var replayed = CareerRules.Replay(journal, world);
        Assert.Equal(state.Cash, replayed.Cash);
        Assert.Equal(state.Collection, replayed.Collection);
        Assert.Equal(post.Status, replayed.Posts.Single().Status);
    }

    [Fact]
    public void ATakenDownPostGivesTheCardBack()
    {
        var (state, journal, world) = Trading();
        var card = state.Cards.First(c => c.Card.Name == "Card 16").Card;
        int owned = state.Owned(card);
        Do(state, journal, world, new PostSale(T0.AddMinutes(1), card.ToString(), 100));
        Do(state, journal, world, new TakeDownSale(T0.AddMinutes(2), state.Posts.Single().Id));
        Assert.Equal(owned, state.Owned(card));
        Assert.Equal(SaleStatus.TakenDown, state.Posts.Single().Status);
    }
}
