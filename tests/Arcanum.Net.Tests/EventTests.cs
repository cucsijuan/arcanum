// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Bots.Limited;
using Arcanum.Cards;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Net.Client;
using Arcanum.Net.Events;
using Arcanum.Net.Host;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Tests;

public class EventTests
{
    private static readonly Dictionary<string, CardDefinition> Cards =
        NetTable.RedGreen().Append(GenericCards.Mountain).Append(GenericCards.Forest).DistinctBy(c => c.Name).ToDictionary(c => c.Name);

    private static readonly string[] Spells = NetTable.RedGreen().Where(c => !c.Is(CardType.Land)).Select(c => c.Name).Distinct().ToArray();

    /// <summary>Boosters of random spells from the test cards.</summary>
    private sealed class TestBoosters(int size) : IBoosterSource
    {
        public string Name => "Test";
        public List<PoolCard> Open(Random random) =>
            Enumerable.Range(0, size).Select(_ => new PoolCard(Spells[random.Next(Spells.Length)], "", "", "common")).ToList();
    }

    private static readonly Dictionary<ManaType, string> Basics = new() { [ManaType.Red] = "Mountain", [ManaType.Green] = "Forest" };

    private static DeckList AutoBuild(IReadOnlyList<PoolCard> pool)
    {
        var choice = LimitedDeckBuilder.Build(pool.Select(p => new DraftOption(Cards[p.Name], p.Rarity)).ToList());
        var deck = new DeckList();
        foreach (var i in choice.PoolCards) DeckList.Adjust(deck.Main, pool[i].Name, 1, null, null);
        foreach (var (color, count) in choice.BasicLands.Where(kv => kv.Value > 0))
            DeckList.Adjust(deck.Main, Basics.GetValueOrDefault(color, "Forest"), count, null, null);
        return deck;
    }

    private static string? CheckDeck(DeckList deck, IReadOnlyList<PoolCard> pool)
    {
        if (deck.Main.Sum(e => e.Count) < 40) return "Fewer than 40 cards.";
        foreach (var entry in deck.Main.Where(e => !Basics.ContainsValue(e.Name)))
            if (pool.Count(p => p.Name == entry.Name) < entry.Count) return $"{entry.Name} isn't in your pool.";
        return null;
    }

    private static readonly FormatRules Constructed = new() { Id = "test", Name = "Test", MinDeckSize = 40, SideboardMax = 15 };

    private static EventRules Rules(int boosterSize) => new()
    {
        CheckSideboarding = (deck, registered) => DeckValidator.ValidateSideboarding(deck, registered, Constructed).Select(i => i.Message).FirstOrDefault(),
        Boosters = _ => new TestBoosters(boosterSize),
        Option = p => new DraftOption(Cards[p.Name], p.Rarity),
        AutoBuild = AutoBuild,
        CheckDeck = CheckDeck,
        Cards = deck => deck.Main.SelectMany(e => Enumerable.Repeat(Cards[e.Name], e.Count)).ToList(),
    };

    /// <summary>A person at the event, played automatically: picks, builds, readies and plays their games.</summary>
    private sealed class Attendee
    {
        public required EventClient Client { get; init; }
        public required int Seat { get; init; }
        public bool Idle { get; init; } // never picks or builds: the timers decide for them
        public GameClient? Game;
        public string? GameToken;
        public int Games;
        public int Picks;
    }

    [Fact]
    public void ADraftWithPeopleAndComputersRunsToTheEnd()
    {
        var clock = new FakeClock();
        var lobby = new LobbyHost(new LobbySettings("Limited", false, 20, 4, "1", "c", Event: "Draft"), _ => throw new InvalidOperationException(), clock.Func);
        var listener = new InMemoryListener();
        lobby.AddListener(listener);
        var host = new LobbyClient(listener.Connect(), new ClientIdentity("Host", lobby.HostToken, "1", "c"), clock.Func);
        var guest = new LobbyClient(listener.Connect(), new ClientIdentity("Guest", "", "1", "c"), clock.Func);
        for (int i = 0; i < 4; i++) { lobby.Poll(); host.Poll(); guest.Poll(); }
        Assert.Contains("free", lobby.StartProblem); // no decks needed, but seats must be taken
        lobby.SetComputer(2, "Computer 2", "", "");
        lobby.SetComputer(3, "Computer 3", "", "");
        Assert.Null(lobby.StartProblem);

        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var ev = lobby.StartEvent(seats => new EventHost(new LimitedEvent
        {
            Mode = LimitedMode.Draft, Source = "test", BoostersPerPlayer = 3, RoundsTotal = 2, BestOf = 1, Seed = 9,
            Seats = seats.Select(s => new EventSeat { Name = s.Name, IsHuman = !s.IsComputer }).ToList(),
        }, seats.Select(s => s.IsComputer ? null : s.Token).ToList(), Rules(12), options) { Title = "Draft · Test" });
        for (int i = 0; i < 3; i++) { lobby.Poll(); host.Poll(); guest.Poll(); }
        Assert.True(host.IsEvent && guest.IsEvent);

        var people = new[]
        {
            new Attendee { Client = host.JoinEvent(), Seat = 0 },
            new Attendee { Client = guest.JoinEvent(), Seat = 1, Idle = true },
        };
        var seen = new List<EventInfo>();
        for (int step = 0; step < 400_000 && ev.Event.Stage != EventStage.Finished; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            lobby.Poll();
            foreach (var p in people)
            {
                p.Client.Poll();
                if (p.Client.Event is not { } info) continue;
                if (p.Seat == 1) seen.Add(info);
                Act(p, info, listener, clock);
                p.Game?.Poll();
            }
        }

        Assert.Equal(EventStage.Finished, ev.Event.Stage);
        Assert.Equal(2, ev.Event.Rounds.Count);
        Assert.All(ev.Event.Rounds.SelectMany(r => r), m => Assert.True(m.Done));
        Assert.Equal(36, ev.Event.Seats[1].Pool.Count); // the idle guest still got a full pool: picked for them when time ran out
        Assert.NotNull(ev.Event.Seats[1].Deck);
        Assert.True(people[0].Picks >= 36);
        Assert.True(people[0].Games > 0 && people[1].Games > 0);
        // The guest only ever saw their own boosters and pool.
        Assert.All(seen, info => Assert.Equal(1, info.You));
        Assert.Contains(seen, info => info.Draft is { Picked: false } && info.SecondsLeft > 0);
    }

    private static void Act(Attendee p, EventInfo info, InMemoryListener listener, FakeClock clock)
    {
        if (info.Draft is { Picked: false } draft && !p.Idle && draft.Pack.Count > 0 && p.Picks < (draft.Round * 12) + draft.Pick + 1)
        {
            p.Client.Pick(0);
            p.Picks++;
        }
        if (info.Stage == EventStage.Building && !p.Idle && !info.Ready && info.Deck is null)
        {
            p.Client.SubmitDeck("auto", AutoBuild(info.Pool).Export());
            p.Client.Ready();
        }
        if (info.Stage == EventStage.Playing && info.GameToken is { } token && token != p.GameToken)
        {
            p.GameToken = token;
            p.Games++;
            var game = new GameClient(listener.Connect(), new ClientIdentity("x", token, "1", "c"), clock.Func);
            game.Welcomed += () =>
            {
                var seatBot = new BotController(game.Seat) { AlwaysKeep = true };
                seatBot.UseCardRules(id => game.View?.FindCard(id) is { IsHidden: false, Name: { } n } ? Cards.GetValueOrDefault(n) : null);
                game.Controller = seatBot;
            };
            p.Game = game;
        }
        if (info.Stage == EventStage.Playing && info.GameToken is null && !info.Ready && !p.Idle && info.SecondsLeft >= 0) p.Client.Ready();
    }

    [Fact]
    public void SealedBestOfThreeBetweenTwoPeople()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var ev = new EventHost(new LimitedEvent
        {
            Mode = LimitedMode.Sealed, Source = "test", BoostersPerPlayer = 6, RoundsTotal = 1, BestOf = 3, Seed = 2,
            Seats = new List<EventSeat> { new() { Name = "A", IsHuman = true }, new() { Name = "B", IsHuman = true } },
        }, new[] { "a", "b" }, Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        var people = new[] { "a", "b" }.Select((t, i) => new Attendee
        {
            Client = new EventClient(listener.Connect(), new ClientIdentity(t, t, "1", "c"), greet: true, clock.Func), Seat = i,
        }).ToList();
        for (int step = 0; step < 400_000 && ev.Event.Stage != EventStage.Finished; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            ev.Poll();
            foreach (var p in people)
            {
                p.Client.Poll();
                if (p.Client.Event is { } info) Act(p, info, listener, clock);
                p.Game?.Poll();
            }
        }
        var match = ev.Event.Rounds.Single().Single();
        Assert.True(match.Done);
        Assert.Equal(2, Math.Max(match.WinsA, match.WinsB));
        Assert.Equal(match.WinsA + match.WinsB + match.Draws, people[0].Games);
        Assert.Equal(72, ev.Event.Seats[0].Pool.Count);
    }

    [Fact]
    public void TheFirstPickOfARoundHasTimeToOpenTheBooster()
    {
        var clock = new FakeClock();
        var options = new EventOptions { Clock = clock.Func };
        var ev = new EventHost(new LimitedEvent
        {
            Mode = LimitedMode.Draft, Source = "test", BoostersPerPlayer = 2, RoundsTotal = 1, Seed = 4,
            Seats = Enumerable.Range(0, 3).Select(i => new EventSeat { Name = $"P{i}", IsHuman = i == 0 }).ToList(),
        }, new[] { "tok", null, null }, Rules(8), options);
        ev.Start();

        var info = ev.InfoFor(0);
        Assert.Equal(0, info.Draft!.Pick);
        Assert.Equal(options.PickSeconds(8) + (int)options.OpeningTime.TotalSeconds, info.SecondsLeft);
    }

    [Fact]
    public void AnEventResumesFromItsSave()
    {
        var clock = new FakeClock();
        var options = new EventOptions { Clock = clock.Func };
        var ev = new EventHost(new LimitedEvent
        {
            Mode = LimitedMode.Draft, Source = "test", BoostersPerPlayer = 2, RoundsTotal = 1, Seed = 4,
            Seats = Enumerable.Range(0, 3).Select(i => new EventSeat { Name = $"P{i}", IsHuman = i == 0 }).ToList(),
        }, new[] { "tok", null, null }, Rules(8), options);
        ev.Start();
        ev.Poll(); // computers pick; the person hasn't yet
        var saved = ev.Save();

        var resumed = EventHost.Restore(saved, Rules(8), options);
        Assert.Equal(EventStage.Drafting, resumed.Event.Stage);
        var info = resumed.InfoFor(0);
        Assert.Equal(8, info.Draft!.Pack.Count);
        Assert.Equal(ev.InfoFor(0).Draft!.Pack, info.Draft.Pack);
    }

    /// <summary>A constructed deck brought to an event: a pool's deck with two spells beside it.</summary>
    private static DeckList Brought(int seed)
    {
        var random = new Random(seed);
        var deck = AutoBuild(new TestBoosters(45).Open(random));
        deck.Sideboard.Add(new DeckEntry(2, Spells[random.Next(Spells.Length)]));
        return deck;
    }

    private static LimitedEvent ConstructedEvent(int seats, int people, int rounds, int topCut, int bestOf = 1, int roundMinutes = 0) => new()
    {
        Mode = LimitedMode.Sealed, Source = "constructed:test", BoostersPerPlayer = 0, RoundsTotal = rounds, TopCut = topCut, BestOf = bestOf,
        RoundMinutes = roundMinutes, Seed = 5,
        Seats = Enumerable.Range(0, seats).Select(i => Brought(i) is var deck
            ? new EventSeat { Name = $"P{i}", IsHuman = i < people, Deck = deck, Registered = DeckList.Parse(deck.Export()) } : null!).ToList(),
    };

    private static void Run(EventHost ev, IReadOnlyList<Attendee> people, InMemoryListener listener, FakeClock clock, Action? each = null)
    {
        for (int step = 0; step < 400_000 && ev.Event.Stage != EventStage.Finished; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            ev.Poll();
            foreach (var p in people)
            {
                p.Client.Poll();
                if (p.Client.Event is { } info) Act(p, info, listener, clock);
                p.Game?.Poll();
            }
            each?.Invoke();
        }
    }

    private static List<Attendee> Attend(EventHost ev, InMemoryListener listener, FakeClock clock, int people) =>
        Enumerable.Range(0, people).Select(i => new Attendee
        {
            Client = new EventClient(listener.Connect(), new ClientIdentity($"t{i}", $"t{i}", "1", "c"), greet: true, clock.Func), Seat = i,
        }).ToList();

    [Fact]
    public void AConstructedEventPlaysItsSwissRoundsThenThePlayoff()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var spec = ConstructedEvent(seats: 8, people: 1, rounds: 3, topCut: 4);
        var brought = spec.Seats.Select(s => s.Deck!.Export()).ToList();
        var ev = new EventHost(spec, spec.Seats.Select((s, i) => s.IsHuman ? $"t{i}" : null).ToList(), Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        Assert.Equal(EventStage.Playing, ev.Event.Stage); // no deck building: everyone brought a deck

        var people = Attend(ev, listener, clock, 1);
        Run(ev, people, listener, clock);

        Assert.Equal(EventStage.Finished, ev.Event.Stage);
        Assert.Equal(3 + 2, ev.Event.Rounds.Count); // the Swiss rounds, then the semifinals and the final
        Assert.Single(ev.Event.Rounds[^1]);
        Assert.Equal(brought, ev.Event.Seats.Select(s => s.Deck!.Export()).ToList()); // the decks brought, not built from a pool
    }

    [Fact]
    public void ARoundEndsOnTime()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var spec = ConstructedEvent(seats: 2, people: 2, rounds: 1, topCut: 0, bestOf: 3, roundMinutes: 1);
        var ev = new EventHost(spec, new[] { "t0", "t1" }, Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        var people = Attend(ev, listener, clock, 2);
        var match = ev.Event.Rounds.Single().Single();
        bool called = false;
        Run(ev, people, listener, clock, () => called |= people[0].Game?.View is { } view && view.TimeCalledAt is not null);

        Assert.True(match.Done);
        Assert.NotNull(match.Started);
        Assert.True(match.WinsA < 2 && match.WinsB < 2); // a minute isn't enough for a match: time ended it
        Assert.True(called);
    }

    [Fact]
    public void AConstructedDeckChangesBetweenGamesOnlyWithItsSideboard()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var spec = ConstructedEvent(seats: 2, people: 1, rounds: 1, topCut: 0, bestOf: 3);
        var ev = new EventHost(spec, new[] { "t0", null }, Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        var me = ev.Event.Seats[0];
        var registered = me.Registered!;
        var match = ev.Event.Rounds.Single().Single();
        var person = Attend(ev, listener, clock, 1).Single();
        void Step()
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            ev.Poll();
            person.Client.Poll();
            person.Game?.Poll();
        }
        // The first game, then sideboarding before the second.
        for (int step = 0; step < 200_000 && (match.WinsA + match.WinsB + match.Draws == 0 || person.Client.Event?.GameToken is not null); step++)
        {
            Step();
            if (person.Game is null && person.Client.Event is { } info) Act(person, info, listener, clock);
        }
        Assert.False(match.Done);
        void Submit(DeckList deck)
        {
            person.Client.SubmitDeck("side", deck.Export());
            for (int i = 0; i < 4; i++) Step();
        }

        // In: a sideboard card for one of the deck's spells.
        var swapped = DeckList.Parse(registered.Export());
        swapped.Sideboard.Clear();
        var outCard = swapped.Main.First(e => Spells.Contains(e.Name) && e.Name != registered.Sideboard[0].Name);
        DeckList.Adjust(swapped.Main, outCard.Name, -1, null, null);
        DeckList.Adjust(swapped.Main, registered.Sideboard[0].Name, 1, null, null);
        var stranger = DeckList.Parse(swapped.Export());
        DeckList.Adjust(stranger.Main, "Mountain", 30, null, null);

        Submit(stranger);
        Assert.NotNull(ev.InfoFor(0).DeckProblem);
        Assert.Equal(registered.Export(), me.Deck!.Export()); // refused: the deck as it was
        Submit(swapped);
        Assert.Null(ev.InfoFor(0).DeckProblem);
        Assert.Contains(me.Deck!.Sideboard, e => e.Name == outCard.Name);
        Assert.Equal(registered.Main.Concat(registered.Sideboard).Sum(e => e.Count), me.Deck.Main.Concat(me.Deck.Sideboard).Sum(e => e.Count));
    }

    [Fact]
    public void ATwoDayEventStopsAfterTheFirstDayAndGoesOnFromItsSave()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var nextDay = clock.Func().AddDays(1);
        var games = new HostOptions { Version = "1", Content = "c" };
        var spec = ConstructedEvent(seats: 8, people: 1, rounds: 4, topCut: 0);
        var day1 = new EventHost(new LimitedEvent
        {
            Mode = spec.Mode, Source = spec.Source, BoostersPerPlayer = 0, RoundsTotal = 4, CutAfter = 2, CutPoints = 3, Seed = 5, Seats = spec.Seats,
        }, spec.Seats.Select((s, i) => s.IsHuman ? $"t{i}" : null).ToList(), Rules(12),
            new EventOptions { Clock = clock.Func, Games = games, DayEndsAfter = 2, NextDay = nextDay });
        bool ended = false;
        day1.DayEnded += () => ended = true;
        day1.AddListener(listener);
        day1.Start();
        var people = Attend(day1, listener, clock, 1);
        for (int step = 0; step < 400_000 && !day1.DayOver; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            day1.Poll();
            people[0].Client.Poll();
            if (people[0].Client.Event is { } info) Act(people[0], info, listener, clock);
            people[0].Game?.Poll();
        }
        Assert.True(ended);
        for (int i = 0; i < 20; i++) day1.Poll(); // stays put
        Assert.Equal(2, day1.Event.Rounds.Count);
        Assert.Equal(nextDay, day1.InfoFor(0).NextDay);
        // The cut is known at the end of the day: everyone without a win is out.
        Assert.All(Enumerable.Range(0, 8).Where(s => day1.Event.Points(s) < 3), s => Assert.Contains(s, day1.Event.Dropped));

        // The next day, on another host: the player comes back with their seat's token.
        clock.Advance(TimeSpan.FromHours(20));
        var day2 = EventHost.Restore(day1.Save(), Rules(12), new EventOptions { Clock = clock.Func, Games = games });
        Assert.Equal(0, day2.SeatOf("t0"));
        var listener2 = new InMemoryListener();
        day2.AddListener(listener2);
        var back = Attend(day2, listener2, clock, 1);
        Run(day2, back, listener2, clock);
        Assert.Equal(EventStage.Finished, day2.Event.Stage);
        Assert.Equal(4, day2.Event.Rounds.Count);
        Assert.All(day2.Event.Rounds.Skip(2).SelectMany(r => r), m => Assert.DoesNotContain(m.SeatA, day1.Event.Dropped));
    }

    [Fact]
    public void ATwoDayEventWithNoPersonLeftAfterTheCutEndsOnTheFirstDay()
    {
        var clock = new FakeClock();
        var games = new HostOptions { Version = "1", Content = "c" };
        var spec = ConstructedEvent(seats: 8, people: 1, rounds: 4, topCut: 0);
        var host = new EventHost(new LimitedEvent
        {
            Mode = spec.Mode, Source = spec.Source, BoostersPerPlayer = 0, RoundsTotal = 4, CutAfter = 2, CutPoints = 3, Seed = 5, Seats = spec.Seats,
        }, spec.Seats.Select((s, i) => s.IsHuman ? $"t{i}" : null).ToList(), Rules(12),
            new EventOptions { Clock = clock.Func, Games = games, DayEndsAfter = 2, NextDay = clock.Func().AddDays(1) });
        bool ended = false;
        host.DayEnded += () => ended = true;
        host.Start();
        host.Event.Drop(0); // the only person leaves: the computer players' rounds needn't wait for the next day
        for (int step = 0; step < 1000 && host.Event.Stage != EventStage.Finished; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            host.Poll();
        }
        Assert.False(ended);
        Assert.False(host.DayOver);
        Assert.Equal(EventStage.Finished, host.Event.Stage);
        Assert.Equal(4, host.Event.Rounds.Count);
    }

    [Fact]
    public void APlayerWhoDoesntShowUpLosesTheMatchAndLeavesTheEvent()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var spec = ConstructedEvent(seats: 8, people: 1, rounds: 3, topCut: 0, bestOf: 3);
        var ev = new EventHost(spec, spec.Seats.Select((s, i) => s.IsHuman ? $"t{i}" : null).ToList(), Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        var match = ev.Event.Rounds[0].Single(m => m.Involves(0));
        int opponent = match.Opponent(0)!.Value;
        Assert.True(match.Waiting);

        // After the seat call, a game loss; ten minutes after the match was due, the match.
        clock.Advance(LimitedEvent.SeatCall + TimeSpan.FromSeconds(1));
        ev.Poll();
        Assert.Equal((0, 1), (match.WinsOf(0), match.WinsOf(opponent)));
        Assert.Equal(new[] { 0 }, match.Late);
        Assert.False(match.Done);
        clock.Advance(LimitedEvent.NoShowAfter);
        ev.Poll();
        Assert.True(match.Done);
        Assert.Equal(2, match.WinsOf(opponent));
        Assert.Contains(0, ev.Event.Dropped);
        Run(ev, Array.Empty<Attendee>(), listener, clock);
        Assert.Equal(EventStage.Finished, ev.Event.Stage);
        Assert.Equal(0, ev.Event.Points(0));
    }

    [Fact]
    public void APlayerWhoComesLateLosesAGameAndPlaysOn()
    {
        var clock = new FakeClock();
        var listener = new InMemoryListener();
        var options = new EventOptions { Clock = clock.Func, Games = new HostOptions { Version = "1", Content = "c" } };
        var spec = ConstructedEvent(seats: 8, people: 1, rounds: 3, topCut: 0, bestOf: 3);
        var ev = new EventHost(spec, spec.Seats.Select((s, i) => s.IsHuman ? $"t{i}" : null).ToList(), Rules(12), options);
        ev.AddListener(listener);
        ev.Start();
        var match = ev.Event.Rounds[0].Single(m => m.Involves(0));
        int opponent = match.Opponent(0)!.Value;
        clock.Advance(TimeSpan.FromMinutes(3));
        ev.Poll();

        var people = Attend(ev, listener, clock, 1);
        Run(ev, people, listener, clock);
        Assert.Equal(EventStage.Finished, ev.Event.Stage);
        Assert.Equal(new[] { 0 }, match.Late);
        Assert.True(match.WinsOf(opponent) >= 1);
        Assert.True(match.WinsA + match.WinsB + match.Draws >= 2); // the late player played on after the game loss
        Assert.DoesNotContain(0, ev.Event.Dropped);
    }
}
