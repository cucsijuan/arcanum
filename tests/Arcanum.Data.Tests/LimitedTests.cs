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

    private static LimitedEvent SealedEvent(int seats, int bestOf = 1, int rounds = 3, int topCut = 0, int cutAfter = 0, int cutPoints = 0) => new()
    {
        Mode = LimitedMode.Sealed, Source = "constructed:x", BestOf = bestOf, RoundsTotal = rounds, Seed = 5,
        TopCut = topCut, CutAfter = cutAfter, CutPoints = cutPoints,
        Seats = Enumerable.Range(0, seats).Select(i => new EventSeat { Name = $"P{i}" }).ToList(),
    };

    /// <summary>Plays every game of the matches: the lower seat wins each game.</summary>
    private static void PlayRound(LimitedEvent ev, IEnumerable<EventMatch> round)
    {
        foreach (var m in round)
            while (!m.Done) ev.RecordGame(m, Math.Min(m.SeatA, m.SeatB ?? m.SeatA));
    }

    private static int Winner(EventMatch m) => m.WinsA > m.WinsB ? m.SeatA : m.SeatB!.Value;

    private static int Loser(EventMatch m) => m.WinsA > m.WinsB ? m.SeatB!.Value : m.SeatA;

    private static EventMatch FinishedMatch(int a, int b, int winsA, int winsB) => new() { SeatA = a, SeatB = b, WinsA = winsA, WinsB = winsB, Done = true };

    [Fact]
    public void ATopCutPlaysSingleEliminationFromTheSwissStandings()
    {
        var ev = SealedEvent(16, bestOf: 3, rounds: 4, topCut: 8);
        var random = new Random(7);
        for (int r = 0; r < 4; r++) PlayRound(ev, ev.PairNextRound(random));
        var swiss = ev.SwissStandings();
        var quarters = ev.PairNextRound(random);
        // Seeds 1 against 8, 4 against 5, 2 against 7 and 3 against 6 of the Swiss standings.
        Assert.Equal(new[] { (swiss[0], swiss[7]), (swiss[3], swiss[4]), (swiss[1], swiss[6]), (swiss[2], swiss[5]) },
            quarters.Select(m => (m.SeatA, m.SeatB!.Value)));
        Assert.False(ev.Complete);
        PlayRound(ev, quarters);
        var semis = ev.PairNextRound(random);
        // The winners of matches 0 and 1 meet, then those of 2 and 3.
        Assert.Equal(new[] { (Winner(quarters[0]), Winner(quarters[1])), (Winner(quarters[2]), Winner(quarters[3])) },
            semis.Select(m => (m.SeatA, m.SeatB!.Value)));
        PlayRound(ev, semis);
        var final = ev.PairNextRound(random).Single();
        Assert.Equal((Winner(semis[0]), Winner(semis[1])), (final.SeatA, final.SeatB!.Value));
        Assert.False(ev.Complete);
        PlayRound(ev, new[] { final });
        Assert.True(ev.Complete);
        var standings = ev.Standings();
        Assert.Equal(Winner(final), standings[0]);
        Assert.Equal<int?>(final.Opponent(standings[0]), standings[1]);
        // The semifinal losers take third and fourth, in Swiss order.
        Assert.Equal(semis.Select(Loser).OrderBy(s => swiss.IndexOf(s)), standings.Skip(2).Take(2));
    }

    [Fact]
    public void APlayoffMatchPlaysOnAfterADrawnGame()
    {
        var ev = SealedEvent(2, bestOf: 3, rounds: 1, topCut: 2);
        var random = new Random(1);
        PlayRound(ev, ev.PairNextRound(random));
        var match = ev.PairNextRound(random).Single();
        ev.RecordGame(match, match.SeatA);
        ev.RecordGame(match, match.SeatB);
        ev.RecordGame(match, null);
        Assert.False(match.Done); // 1-1 with a draw: the players go on
        ev.RecordGame(match, match.SeatA);
        Assert.True(match.Done);
        Assert.Equal(2, match.WinsA);
    }

    [Fact]
    public void ASwissMatchEndsAfterItsGamesEvenWithADraw()
    {
        var ev = SealedEvent(2, bestOf: 3);
        var match = ev.PairNextRound(new Random(1)).Single();
        ev.RecordGame(match, match.SeatA);
        ev.RecordGame(match, match.SeatB);
        ev.RecordGame(match, null);
        Assert.True(match.Done);
        Assert.Equal(1, ev.Points(match.SeatA));
    }

    [Fact]
    public void AnOddNumberOfSeatsGivesEachSeatAtMostOneBye()
    {
        var ev = SealedEvent(5);
        var random = new Random(3);
        var first = ev.PairNextRound(random);
        var bye = Assert.Single(first, m => m.SeatB is null).SeatA;
        Assert.Equal(3, ev.Points(bye)); // a bye is a win
        PlayRound(ev, first);
        for (int r = 1; r < 3; r++) PlayRound(ev, ev.PairNextRound(random));
        var byes = ev.Rounds.SelectMany(r => r).Where(m => m.SeatB is null).Select(m => m.SeatA).ToList();
        Assert.Equal(3, byes.Count);
        Assert.Equal(byes.Count, byes.Distinct().Count());
    }

    [Fact]
    public void ASeatBelowTheCutPointsIsOutAfterTheCut()
    {
        var ev = SealedEvent(8, rounds: 3, cutAfter: 2, cutPoints: 6);
        var random = new Random(5);
        for (int r = 0; r < 2; r++) PlayRound(ev, ev.PairNextRound(random));
        var low = Enumerable.Range(0, 8).Where(s => ev.Points(s) < 6).ToList();
        var round = ev.PairNextRound(random);
        Assert.Equal(low, ev.Dropped);
        Assert.Single(round);
        foreach (var seat in low) Assert.DoesNotContain(round, m => m.Involves(seat));
        Assert.Equal(8, ev.Standings().Count); // they keep their place
    }

    [Fact]
    public void ADroppedSeatConcedesItsMatchAndIsNotPairedAgain()
    {
        var ev = SealedEvent(8, bestOf: 3);
        var random = new Random(2);
        var match = ev.PairNextRound(random).First(m => m.Involves(3));
        ev.Drop(3);
        Assert.True(match.Done);
        Assert.Equal(ev.WinsNeeded, match.WinsOf(match.Opponent(3)!.Value));
        var next = ev.PairNextRound(random);
        Assert.DoesNotContain(next, m => m.Involves(3));
        Assert.Contains(3, ev.Standings());
    }

    [Fact]
    public void EqualPointsAreOrderedByTheOpponentsMatchWins()
    {
        // Seats 3 and 2 each win one match and lose one (3 points each). Seat 3 played seat 2 (a 50% match record) and seat 0
        // (100%): an average of 75%. Seat 2 played seat 3 (50%) and seat 1, which lost both matches (raised to 33%): 42%.
        // Seat 3 ranks above seat 2, though seat 2 has the lower number.
        var ev = SealedEvent(4, rounds: 2);
        ev.Rounds.Add(new List<EventMatch> { FinishedMatch(0, 1, 1, 0), FinishedMatch(3, 2, 1, 0) });
        ev.Rounds.Add(new List<EventMatch> { FinishedMatch(0, 3, 1, 0), FinishedMatch(2, 1, 1, 0) });
        Assert.Equal(new[] { 0, 3, 2, 1 }, ev.SwissStandings());
    }

    [Fact]
    public void TopCutCutAndDropsSaveAndLoad()
    {
        var ev = SealedEvent(8, bestOf: 3, topCut: 8, cutAfter: 2, cutPoints: 6);
        ev.Dropped.Add(3);
        var back = LimitedEvent.FromJson(ev.ToJson());
        Assert.Equal(8, back.TopCut);
        Assert.Equal(2, back.CutAfter);
        Assert.Equal(6, back.CutPoints);
        Assert.Equal(new[] { 3 }, back.Dropped);
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
