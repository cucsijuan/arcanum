// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Net.Client;
using Arcanum.Net.Host;
using Arcanum.Net.Protocol;

namespace Arcanum.Net.Tests;

public class HostedGameTests
{
    private static readonly PlayerId P0 = new(0);
    private static readonly PlayerId P1 = new(1);

    private static IEnumerable<NetMessage> Messages(RecordingConnection wire)
    {
        var format = new WireFormat();
        return wire.Received.Select(format.Read);
    }

    [Fact]
    public void TwoPlayersFinishAGameOverTheNetwork()
    {
        var table = new NetTable();
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.GameOver), "game did not finish");
        table.Step();
        Assert.All(table.Clients, c => Assert.True(c!.View!.IsGameOver));
        Assert.Equal(table.Host.Game.State.Winner, table.Clients[0]!.View!.Winner);
        Assert.Contains(table.Host.Game.Log, e => e is SpellCast); // spells, targets and payments went over the wire
    }

    [Fact]
    public void PlayersNeverReceiveHiddenInformation()
    {
        var table = new NetTable();
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.GameOver));
        table.Step();

        for (int seat = 0; seat < 2; seat++)
        {
            var me = new PlayerId(seat);
            foreach (var message in Messages(table.Wires[seat]!))
            {
                switch (message)
                {
                    case ViewUpdate { View: var view }:
                        foreach (var player in view.Players.Where(p => p.Id != me))
                        {
                            Assert.All(player.Hand, c => Assert.True(c.IsHidden));
                            Assert.Null(player.LibraryTop);
                        }
                        break;
                    case Happened { Event: { Event: GameStarted started } }:
                        Assert.Equal(0UL, started.Seed);
                        break;
                    case Happened { Event: { Event: CardDrawn drawn } ev } when drawn.Player != me:
                        Assert.False(ev.IsVisible(drawn.Card));
                        break;
                }
            }
        }
    }

    [Fact]
    public void EachPlayerSeesTheirOwnCardIds()
    {
        var table = new NetTable();
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Clients.All(c => c?.View is { Battlefield.Count: > 2 })));
        var real = table.Host.Game.State.Battlefield.Select(id => id.Value).ToHashSet();
        var first = table.Clients[0]!.View!.Battlefield.Select(c => c.Id.Value).ToHashSet();
        var second = table.Clients[1]!.View!.Battlefield.Select(c => c.Id.Value).ToHashSet();
        Assert.Empty(first.Intersect(second));
        Assert.Empty(first.Intersect(real));
    }

    [Fact]
    public void TheComputerTakesOverAfterTheGracePeriodAndThePlayerCanComeBack()
    {
        var table = new NetTable();
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= 3));

        table.Disconnect(P1);
        Assert.True(table.RunUntil(() => table.Host.StateOf(P1) == SeatState.Disconnected, 100));
        Assert.Contains(table.Clients[0]!.Seats.Values, s => s is { Seat.Value: 1, State: SeatState.Disconnected, SecondsLeft: > 100 });

        // Within the grace period nothing is decided for them; after it, the computer plays on.
        void Wait(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                table.Clock.Advance(TimeSpan.FromSeconds(1));
                table.Step();
            }
        }
        Wait(60);
        Assert.Equal(SeatState.Disconnected, table.Host.StateOf(P1));
        Wait(61);
        Assert.Equal(SeatState.Computer, table.Host.StateOf(P1));
        int turn = table.Host.Game.State.TurnNumber;
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= turn + 2));
        Assert.False(table.GameOver);

        var back = table.Connect(P1);
        Assert.True(table.RunUntil(() => table.Host.StateOf(P1) == SeatState.Connected, 100));
        Assert.Equal(P1, back.Seat);
        Assert.NotNull(back.View);
        int updates = 0;
        back.ViewChanged += _ => updates++;
        Assert.True(table.RunUntil(() => table.GameOver));
        Assert.True(updates > 0);
    }

    [Fact]
    public void TheHostCanHandADisconnectedSeatToTheComputerAtOnce()
    {
        var table = new NetTable(options: new HostOptions { Grace = null });
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= 2));
        Assert.False(table.Host.ReplaceWithComputer(P1)); // still connected

        table.Disconnect(P1);
        Assert.True(table.RunUntil(() => table.Host.StateOf(P1) == SeatState.Disconnected, 100));
        for (int i = 0; i < 600; i++)
        {
            table.Clock.Advance(TimeSpan.FromSeconds(1));
            table.Step();
        }
        Assert.Equal(SeatState.Disconnected, table.Host.StateOf(P1)); // no grace: waits for the host
        Assert.Contains(table.Clients[0]!.Seats.Values, s => s is { Seat.Value: 1, SecondsLeft: -1 });

        Assert.True(table.Host.ReplaceWithComputer(P1));
        Assert.True(table.RunUntil(() => table.GameOver));
    }

    /// <summary>Gives an impossible answer once per kind of question, then plays normally.</summary>
    private sealed class Mischief(IPlayerController inner) : IPlayerController
    {
        public int Refused;
        private bool _badAction, _badBottom;

        public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => inner.KeepHandAsync(view, mulligansTaken);

        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
            !_badBottom && (_badBottom = true) ? Task.FromResult<IReadOnlyList<CardId>>(Array.Empty<CardId>()) : inner.ChooseCardsToBottomAsync(view, count);

        public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions)
        {
            if (_badAction) return inner.ChooseActionAsync(view, legalActions);
            _badAction = true;
            // A card id never given to this player, then (on the next try) the opponent's land.
            return Task.FromResult<PlayerAction>(new CastSpell(new CardId(-5)));
        }

        public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => inner.ChooseManaPaymentAsync(view, request);
        public Task<IReadOnlyList<Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => inner.ChooseTargetsAsync(view, request);
        public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
            inner.DeclareAttackersAsync(view, possibleAttackers, defenders);
        public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) => inner.DeclareBlockersAsync(view, request);
        public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => inner.AssignCombatDamageAsync(view, request);
        public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => inner.ChooseYesNoAsync(view, request);
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => inner.ChooseDiscardAsync(view, count);
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => inner.ChooseCardsAsync(view, request);
        public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => inner.ChooseModesAsync(view, request);
        public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => inner.ChooseNumberAsync(view, request);
        public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => inner.ChooseOptionAsync(view, request);
    }

    [Fact]
    public void ImpossibleAnswersAreRefusedAndAskedAgain()
    {
        var table = new NetTable();
        Mischief? mischief = null;
        var refusals = new List<string>();
        var client = table.Connect(P0, c => mischief = new Mischief(table.ClientBot(c, P0)));
        client.AnswerRefused += refusals.Add;
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= 4));
        Assert.NotEmpty(refusals);
        Assert.Equal(SeatState.Connected, table.Host.StateOf(P0));
    }

    [Fact]
    public void ASavedGameResumesExactlyWhereItStopped()
    {
        var table = new NetTable(seed: 11);
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= 5));
        var answers = table.Host.Answers.ToList();
        var format = new WireFormat();
        string Snapshot(Engine.Game g) => format.Write(new ViewUpdate(g.ViewFor(P0, revealAll: true)));

        var seats = new[] { new HostSeat("Player 1", NetTable.RedGreen()), new HostSeat("Player 2", NetTable.RedGreen()) };
        var resumed = new GameHost(new Engine.GameConfig { Seed = 11 }, seats, new HostOptions { Replay = answers });
        resumed.Start();
        Assert.Equal(Snapshot(table.Host.Game), Snapshot(resumed.Game));
        Assert.Equal(answers.Count, resumed.Answers.Count);
    }

    [Fact]
    public void PlayersWithADifferentVersionOrContentAreRefused()
    {
        var table = new NetTable(options: new HostOptions { Version = "1.0", Content = "abc" });
        var wire = table.Listener.Connect();
        var client = new GameClient(wire, new ClientIdentity("Late", table.Host.TokenOf(P0), "1.0", "xyz"), table.Clock.Func);
        table.Clients.Add(client);
        table.Step();
        table.Step();
        Assert.Contains("card content", client.RejectedReason);
        Assert.False(client.IsConnected);

        var stranger = new GameClient(table.Listener.Connect(), new ClientIdentity("Stranger", "not-a-token", "1.0", "abc"), table.Clock.Func);
        table.Clients.Add(stranger);
        table.Step();
        table.Step();
        Assert.NotNull(stranger.RejectedReason);
    }

    [Fact]
    public void PlayersSeeTheirOpponentsPlaymats()
    {
        var table = new NetTable(playmats: new[] { "nebula", "../not a playmat" });
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Clients.All(c => c!.Playmats.Count == 2 && c.Playmats[0] == "nebula"), 200));
        Assert.Null(table.Clients[0]!.Playmats[1]); // only plain playmat ids are passed on
    }

    [Fact]
    public void ComputerSeatsPlayOnTheHost()
    {
        var seats = new[] { new HostSeat("Person", NetTable.RedGreen()), new HostSeat("Computer", NetTable.RedGreen(), IsComputer: true) };
        var table = new NetTable(seats: seats);
        table.Host.Start();
        Assert.Equal(SeatState.Computer, table.Host.StateOf(P1));
        Assert.True(table.RunUntil(() => table.GameOver));
    }

    [Fact]
    public void SilentConnectionsAreDropped()
    {
        var table = new NetTable();
        table.Host.Start();
        table.RunUntil(() => table.Host.StateOf(P1) == SeatState.Connected, 100);
        table.Clients[1] = null; // stops answering, without closing
        Assert.True(table.RunUntil(() => table.Host.StateOf(P1) == SeatState.Disconnected, 1000));
    }

    [Fact]
    public void PlayersConnectOverTcp()
    {
        var seats = new[] { new HostSeat("Host", NetTable.RedGreen()), new HostSeat("Guest", NetTable.RedGreen()) };
        var host = new GameHost(new Engine.GameConfig { Seed = 3 }, seats, new HostOptions { Version = "v", Content = "c" });
        using var listener = new Transport.TcpConnectionListener(0);
        host.AddListener(listener);
        var clients = new List<GameClient>();
        foreach (var seat in new[] { P0, P1 })
        {
            var connection = Transport.TcpConnection.ConnectAsync("localhost", listener.Port, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var client = new GameClient(connection, new ClientIdentity(seat.ToString(), host.TokenOf(seat), "v", "c"));
            var bot = new Bots.BotController(seat) { AlwaysKeep = true };
            client.Controller = bot;
            clients.Add(client);
        }
        host.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!host.Game.State.IsGameOver && host.Game.State.TurnNumber < 4 && DateTime.UtcNow < deadline)
        {
            host.Poll();
            foreach (var c in clients) c.Poll();
            Thread.Sleep(1);
        }
        Assert.True(host.Game.State.TurnNumber >= 4 || host.Game.State.IsGameOver);
        Assert.All(clients, c => Assert.True(c.IsWelcomed));
        Assert.All(clients, c => c.Close());
    }

    /// <summary>A player who never answers anything (until <see cref="WakeUp"/>, which drops every question).</summary>
    private sealed class Sleeper : IPlayerController
    {
        private readonly List<Action> _cancels = new();

        private Task<T> Never<T>()
        {
            var tcs = new TaskCompletionSource<T>();
            _cancels.Add(() => tcs.TrySetCanceled());
            return tcs.Task;
        }

        /// <summary>Ends the questions left waiting (the test framework waits for them).</summary>
        public void WakeUp() => _cancels.ForEach(c => c());

        public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => Never<bool>();
        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => Never<IReadOnlyList<CardId>>();
        public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) => Never<PlayerAction>();
        public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => Never<IReadOnlyList<ManaTap>?>();
        public Task<IReadOnlyList<Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => Never<IReadOnlyList<Engine.Abilities.Target>?>();
        public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
            Never<IReadOnlyList<AttackDeclaration>>();
        public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) => Never<IReadOnlyList<BlockDeclaration>>();
        public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => Never<DamageAssignment>();
        public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Never<bool>();
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => Never<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => Never<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => Never<IReadOnlyList<int>?>();
        public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => Never<int>();
        public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => Never<int>();
    }

    [Fact]
    public void ThePlayerWhoTakesTooLongHasTheDecisionMadeForThem()
    {
        var table = new NetTable(options: new HostOptions { DecisionTime = TimeSpan.FromSeconds(30) });
        var sleeper = new Sleeper();
        var sleepy = table.Connect(P1, _ => sleeper);
        int expired = 0;
        sleepy.QuestionExpired += () => expired++;
        bool sawDeadline = false;
        table.Host.Start();
        Assert.True(table.RunUntil(() =>
        {
            sawDeadline |= sleepy.QuestionDeadline is not null;
            return table.Host.Game.State.TurnNumber >= 4;
        }, 30_000));
        Assert.True(expired > 0);
        Assert.Equal(SeatState.Connected, table.Host.StateOf(P1)); // they keep their seat
        Assert.True(sawDeadline); // the player sees how long they have
        sleeper.WakeUp();
    }
}
