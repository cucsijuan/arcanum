// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

/// <summary>Draft sessions, cubes, limited events (pairings, saving) and limited deck rules.</summary>
public class LimitedTests
{
    /// <summary>Boosters numbered so passing can be followed: booster k of a round holds cards "k-0".."k-(size-1)".</summary>
    private sealed class NumberedBoosters(int size) : IBoosterSource
    {
        private int _opened;
        public string Name => "Numbered";

        public List<PoolCard> Open(Random random)
        {
            int k = _opened++;
            return Enumerable.Range(0, size).Select(i => new PoolCard($"{k}-{i}", "", "", "common")).ToList();
        }
    }

    [Fact]
    public void BoostersPassLeftThenRightThenLeft()
    {
        var draft = new DraftSession(new NumberedBoosters(3), seats: 3, rounds: 3, new Random(1));
        Assert.Equal("0-0", draft.PackFor(0)[0].Name);
        for (int s = 0; s < 3; s++) draft.Pick(s, 0);
        // Passing left: seat 1 now holds what seat 0 opened.
        Assert.StartsWith("0-", draft.PackFor(1)[0].Name);
        Assert.Equal(2, draft.PackFor(1).Count);
        while (draft.Round == 0) for (int s = 0; s < 3; s++) draft.Pick(s, 0);
        Assert.Equal(1, draft.Round);
        Assert.False(draft.PassesLeft);
        for (int s = 0; s < 3; s++) draft.Pick(s, 0);
        // Passing right: seat 0 now holds what seat 1 opened this round (booster 4).
        Assert.StartsWith("4-", draft.PackFor(0)[0].Name);
        while (!draft.IsComplete) for (int s = 0; s < 3; s++) draft.Pick(s, 0);
        Assert.All(draft.Picks, p => Assert.Equal(9, p.Count));
        Assert.Equal(27, draft.Picks.SelectMany(p => p).Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void PicksWaitForEverySeat()
    {
        var draft = new DraftSession(new NumberedBoosters(3), seats: 2, rounds: 1, new Random(1));
        draft.Pick(0, 1);
        Assert.True(draft.HasPicked(0));
        Assert.Equal(3, draft.PackFor(0).Count); // nothing taken until the other seat picks
        Assert.Throws<InvalidOperationException>(() => draft.Pick(0, 0));
        draft.Pick(1, 0);
        Assert.Equal(new[] { "0-1" }, draft.Picks[0].Select(c => c.Name));
        Assert.Equal(new[] { "1-0" }, draft.Picks[1].Select(c => c.Name));
    }

    [Fact]
    public void ADraftResumesFromItsSnapshot()
    {
        var source = new NumberedBoosters(3);
        var draft = new DraftSession(source, seats: 2, rounds: 2, new Random(1));
        draft.Pick(0, 0); draft.Pick(1, 0);
        var restored = DraftSession.Restore(draft.Snapshot(), source, new Random(2));
        Assert.Equal(draft.PackFor(0).Select(c => c.Name), restored.PackFor(0).Select(c => c.Name));
        Assert.Equal(draft.Picks[1].Select(c => c.Name), restored.Picks[1].Select(c => c.Name));
        while (!restored.IsComplete) { restored.Pick(0, 0); restored.Pick(1, 0); }
        Assert.Equal(6, restored.Picks[0].Count);
    }

    [Fact]
    public void ACubeDealsEachCardOnce()
    {
        var cards = Enumerable.Range(0, 45).Select(i => new PoolCard($"Card {i}", "", "", "")).ToList();
        var cube = new CubeBoosters("Mini", cards, 15, new Random(3));
        var opened = Enumerable.Range(0, 3).SelectMany(_ => cube.Open(new Random())).ToList();
        Assert.Equal(45, opened.Select(c => c.Name).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => cube.Open(new Random()));
    }

    private static LimitedEvent Event(int seats = 8, int bestOf = 1) => new()
    {
        Mode = LimitedMode.Draft, Source = "set:abc", BestOf = bestOf, Seed = 5,
        Seats = Enumerable.Range(0, seats).Select(i => new EventSeat { Name = i == 0 ? "You" : $"Computer {i}", IsHuman = i == 0 }).ToList(),
    };

    [Fact]
    public void SwissRoundsAvoidRematchesAndCountPoints()
    {
        var ev = Event();
        var random = new Random(4);
        for (int r = 0; r < 3; r++)
        {
            var round = ev.PairNextRound(random);
            Assert.Equal(4, round.Count);
            Assert.Equal(8, round.SelectMany(m => new[] { m.SeatA, m.SeatB!.Value }).Distinct().Count());
            foreach (var m in round) ev.RecordGame(m, m.SeatA);
        }
        var opponents = ev.Rounds.SelectMany(r => r).Where(m => m.Involves(0)).Select(m => m.Opponent(0)).ToList();
        Assert.Equal(3, opponents.Distinct().Count());
        Assert.Equal(new[] { 9, 6, 6, 6, 3, 3, 3, 0 }, ev.Standings().Select(ev.Points)); // 3-0, 2-1 ×3, 1-2 ×3, 0-3
    }

    [Fact]
    public void BestOfThreeEndsAtTwoWins()
    {
        var ev = Event(2, bestOf: 3);
        var match = ev.PairNextRound(new Random(1)).Single();
        ev.RecordGame(match, match.SeatA);
        Assert.False(match.Done);
        ev.RecordGame(match, match.SeatB);
        ev.RecordGame(match, match.SeatA);
        Assert.True(match.Done);
        Assert.Equal(3, ev.Points(match.SeatA));
    }

    [Fact]
    public void AnOddSeatGetsABye()
    {
        var ev = Event(3);
        var round = ev.PairNextRound(new Random(1));
        var bye = Assert.Single(round, m => m.SeatB is null);
        Assert.True(bye.Done);
        Assert.Equal(3, ev.Points(bye.SeatA));
    }

    [Fact]
    public void EventsSaveAndLoad()
    {
        var ev = Event(3, bestOf: 3);
        ev.Seats[0].Pool.Add(new PoolCard("Glade Cub", "abc", "12", "common"));
        ev.Seats[0].Deck = DeckList.Parse("17 Forest\n1 Glade Cub (ABC) 12");
        ev.Stage = EventStage.Playing;
        ev.Draft = new DraftSnapshot(3, 3, 1, 4, new[] { new List<PoolCard> { new("A", "", "", "") } }, new[] { new List<PoolCard>() });
        var m = ev.PairNextRound(new Random(2))[0];
        ev.RecordGame(m, m.SeatB);
        var back = LimitedEvent.FromJson(ev.ToJson());
        Assert.Equal(ev.ToJson(), back.ToJson());
        Assert.Equal("Glade Cub", back.Seats[0].Pool.Single().Name);
        Assert.Equal(EventStage.Playing, back.Stage);
        Assert.Equal(1, back.Rounds[0][0].WinsB);
    }

    private const string Cards = """
        {"oracle_id":"o-1","name":"Glade Cub","layout":"normal","mana_cost":"{1}{G}","type_line":"Creature — Bear","oracle_text":"","power":"2","toughness":"2","colors":["G"]}
        {"oracle_id":"o-2","name":"Forest","layout":"normal","mana_cost":"","type_line":"Basic Land — Forest","oracle_text":"({T}: Add {G}.)"}
        {"oracle_id":"o-3","name":"Sky Lancer","layout":"normal","mana_cost":"{2}{W}","type_line":"Creature — Bird Soldier","oracle_text":"","power":"2","toughness":"2"}
        """;

    [Fact]
    public void LimitedDecksComeFromThePoolPlusBasicLands()
    {
        var db = new CardDatabase(OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(Cards))));
        var format = new FormatRules { Id = "limited", Name = "Limited", MinDeckSize = 40, Limited = true };
        var pool = new List<PoolCard> { new("Glade Cub", "abc", "1", "common"), new("Glade Cub", "abc", "1", "common") };
        Assert.Empty(DeckValidator.ValidateLimited(DeckList.Parse("38 Forest\n2 Glade Cub"), format, pool, db).Where(i => i.Severity == IssueSeverity.Error));
        var tooMany = DeckValidator.ValidateLimited(DeckList.Parse("37 Forest\n3 Glade Cub"), format, pool, db);
        Assert.Contains(tooMany, i => i.Message.Contains("your pool has 2"));
        var notInPool = DeckValidator.ValidateLimited(DeckList.Parse("39 Forest\n1 Sky Lancer"), format, pool, db);
        Assert.Contains(notInPool, i => i.Message.Contains("isn't in your pool"));
        var small = DeckValidator.ValidateLimited(DeckList.Parse("30 Forest"), format, pool, db);
        Assert.Contains(small, i => i.Message.Contains("at least 40"));
    }
}
