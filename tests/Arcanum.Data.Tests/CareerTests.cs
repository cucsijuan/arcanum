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
          "starter": { "cash": 1000, "boosters": 3, "decks": ["starter"] },
          "dailies": { "perDay": 2, "win": 400, "loss": 100, "winBoosters": 1 },
          "sets": ["abc"],
          "featured": ["abc"],
          "precons": [{ "deck": "other" }],
          "opponents": [
            { "id": "ana", "name": "Ana", "decks": ["other"] },
            { "id": "bo", "name": "Bo", "decks": ["starter"] }
          ]
        }
        """;

    private static readonly Dictionary<string, string> Decks = new()
    {
        ["starter"] = "4 Card 1 (ABC) 1\n2 Card 16 (ABC) 16\n10 Card 19 (ABC) 19",
        ["other"] = "4 Card 2 (ABC) 2\n1 Card 18 (ABC) 18",
    };

    private static CareerWorld World() =>
        new(CareerContent.Parse(ContentJson), Database(), new[] { SetDefinition.Parse(SetJson) }, name => Decks.GetValueOrDefault(name));

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
            Do(state, journal, world, new FinishDaily(T0.AddMinutes(10 * i + 5), Won: true));
        }
        Assert.Equal(1800, state.Cash);
        Assert.Equal(0, CareerRules.DailiesLeft(state, world, T0.AddHours(1)));
        var refused = Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new StartDaily(T0.AddHours(1), CareerRules.CashReward), world));
        Assert.Contains("No daily", refused.Message);

        // Three days later there are two again, not six.
        var later = T0.AddDays(3);
        Assert.Equal(2, CareerRules.DailiesLeft(state, world, later));
        Do(state, journal, world, new StartDaily(later, CareerRules.BoosterReward));
        Do(state, journal, world, new FinishDaily(later.AddMinutes(5), Won: true));
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
        Do(state, journal, world, new FinishDaily(T0.AddMinutes(9), Won: false));
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
    public void SellingPaysHalfTheValueAndNeedsTheCards()
    {
        var (state, journal, world) = Started();
        var rare = new CardKey("Card 16", "ABC", "16").ToString();
        Do(state, journal, world, new SellCard(T0.AddMinutes(1), rare, 2));
        Assert.Equal(1000 + 2 * 75, state.Cash);
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
}
