// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Cards;
using Arcanum.Engine;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Net.Client;
using Arcanum.Net.Host;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Tests;

/// <summary>Clock the tests move by hand (grace periods, heartbeats).</summary>
public sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public Func<DateTime> Func => () => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Records what a connection receives (to check what a player was sent).</summary>
public sealed class RecordingConnection(IConnection inner) : IConnection
{
    public List<string> Received { get; } = new();
    public bool IsOpen => inner.IsOpen;
    public void Send(string message) => inner.Send(message);

    public bool TryReceive(out string? message)
    {
        if (!inner.TryReceive(out message)) return false;
        Received.Add(message);
        return true;
    }

    public void Close() => inner.Close();
    public void Dispose() => inner.Dispose();
}

/// <summary>A hosted game with players connected in memory, each played by a computer player on the client side.</summary>
public sealed class NetTable
{
    public static readonly CardDefinition Spark = new()
    {
        Name = "Spark", Types = CardType.Instant, ManaCost = ManaCost.Parse("{R}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) } },
    };
    public static readonly CardDefinition Surge = new()
    {
        Name = "Surge", Types = CardType.Instant, ManaCost = ManaCost.Parse("{G}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0)) } },
    };
    public static readonly CardDefinition Pinger = new()
    {
        Name = "Pinger", Types = CardType.Creature, ManaCost = ManaCost.Parse("{2}{R}"), Power = 1, Toughness = 1,
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility { Cost = AbilityCost.TapOnly, Text = "{T}: 1 damage", Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) } },
        },
    };
    public static readonly CardDefinition Brute = GenericCards.HillBrute with { Name = "Trampler", Keywords = new[] { Engine.Cards.Keywords.DisplayName(Keyword.Trample) } };

    public static IReadOnlyList<CardDefinition> Deck(params (CardDefinition Card, int Count)[] entries) =>
        entries.SelectMany(e => Enumerable.Repeat(e.Card, e.Count)).ToList();

    public static IReadOnlyList<CardDefinition> RedGreen() => Deck((GenericCards.Mountain, 9), (GenericCards.Forest, 8), (Spark, 4), (Surge, 3),
        (Pinger, 3), (GenericCards.GladeCub, 6), (Brute, 4), (GenericCards.OgreBrute, 3));

    private readonly Dictionary<string, CardDefinition> _byName;

    public FakeClock Clock { get; } = new();
    public InMemoryListener Listener { get; } = new();
    public GameHost Host { get; }
    public List<GameClient?> Clients { get; } = new();
    public List<RecordingConnection?> Wires { get; } = new();

    public NetTable(int players = 2, ulong seed = 7, HostOptions? options = null, IReadOnlyList<HostSeat>? seats = null)
    {
        seats ??= Enumerable.Range(0, players).Select(i => new HostSeat($"Player {i + 1}", RedGreen())).ToList();
        _byName = seats.SelectMany(s => s.Deck).DistinctBy(d => d.Name).ToDictionary(d => d.Name);
        Host = new GameHost(new GameConfig { Seed = seed }, seats, (options ?? new HostOptions()) with { Clock = Clock.Func });
        Host.AddListener(Listener);
        for (int i = 0; i < seats.Count; i++)
        {
            Clients.Add(null);
            Wires.Add(null);
            if (!seats[i].IsComputer) Connect(new PlayerId(i));
        }
    }

    /// <summary>Connects (or reconnects) a player to their seat, played by a computer player on their side.</summary>
    public GameClient Connect(PlayerId seat, Func<GameClient, IPlayerController>? controller = null)
    {
        var wire = new RecordingConnection(Listener.Connect());
        var client = new GameClient(wire, new ClientIdentity($"Player {seat.Value + 1}", Host.TokenOf(seat), "", ""), Clock.Func);
        client.Controller = controller?.Invoke(client) ?? ClientBot(client, seat);
        client.Policy = new AutoPassPolicy();
        Clients[seat.Value] = client;
        Wires[seat.Value] = wire;
        return client;
    }

    /// <summary>A computer player deciding from the client's view only, with rules read from card names it can see.</summary>
    public BotController ClientBot(GameClient client, PlayerId seat)
    {
        var bot = new BotController(seat) { AlwaysKeep = true };
        bot.UseCardRules(id => client.View?.FindCard(id) is { IsHidden: false, Name: { } name } ? _byName.GetValueOrDefault(name) : null);
        return bot;
    }

    public void Disconnect(PlayerId seat)
    {
        Clients[seat.Value]?.Close();
        Clients[seat.Value] = null;
    }

    public void Step()
    {
        Clock.Advance(TimeSpan.FromMilliseconds(50));
        Host.Poll();
        foreach (var client in Clients) client?.Poll();
        if (Host.Running is { IsFaulted: true } failed) throw failed.Exception!;
    }

    /// <summary>Runs until <paramref name="done"/>; false if it took more than <paramref name="maxSteps"/>.</summary>
    public bool RunUntil(Func<bool> done, int maxSteps = 200_000)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (done()) return true;
            Step();
        }
        return done();
    }

    public bool GameOver => Host.Game.State.IsGameOver;
}
