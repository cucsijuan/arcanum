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
    private static CareerWorld World(bool challenges = false) =>
        new(CareerContent.Parse(challenges ? ContentJson : ContentJson.Replace("\"challengesPerWeek\": 2", "\"challengesPerWeek\": 0")), Database(), new[] { SetDefinition.Parse(SetJson) }, name => Decks.GetValueOrDefault(name));

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
    public void TheStoresOffersStayAllDayAndCanBeTakenOnce()
    {
        var (state, journal, world) = StartedWithSixty();
        var offers = CareerRules.Offers(state, world, state.Day);
        Assert.Equal(offers, CareerRules.Offers(state, world, state.Day));
        var bargain = offers.First(o => o.Kind == CareerRules.Bargain);
        var bought = CardKey.Parse(bargain.Get);
        Assert.Contains(bought.Name, new[] { "Card 16", "Card 17", "Card 18" });
        Assert.Equal((int)(world.Value(bought) * 0.6), bargain.Price);
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
            Assert.InRange(world.Value(give), world.Value(CardKey.Parse(trade.Get)) * 0.9, world.Value(CardKey.Parse(trade.Get)) * 1.1);
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
        Assert.Equal(wednesday, CareerRules.NextDay(world.Content.Events[0], new DateOnly(2026, 10, 12)));
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
}
