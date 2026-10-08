// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Bots.Limited;
using Arcanum.Cards;
using Arcanum.Data.Decks;
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

    private static EventRules Rules(int boosterSize) => new()
    {
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
}
