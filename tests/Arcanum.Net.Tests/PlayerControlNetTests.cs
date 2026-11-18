// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Arcanum.Net.Protocol;

namespace Arcanum.Net.Tests;

/// <summary>
/// Over the hosted protocol, a player who controls another during that player's turn (rule 722) is asked the controlled
/// player's decisions, marked with <see cref="Question.For"/>, and answers from the controlled player's view. The control ends
/// with that turn.
/// </summary>
public class PlayerControlNetTests
{
    private static readonly PlayerId P0 = new(0);
    private static readonly PlayerId P1 = new(1);

    /// <summary>A decision the controller of player 0's seat made, with the view it was given and the view its client held then.</summary>
    private sealed record Decided(int Turn, PlayerId Viewer, bool ControlledHandVisible, PlayerId? ClientControlledViewer);

    /// <summary>Passes every decision to the bot unchanged, and notes the view each one was made from.</summary>
    private sealed class Watching(IPlayerController inner, Func<GameView?> clientView, List<Decided> decided) : IPlayerController
    {
        private void Note(GameView view)
        {
            var hand = view.Players.Single(p => p.Id == P1).Hand;
            decided.Add(new Decided(view.TurnNumber, view.Viewer, hand.Count > 0 && hand.All(c => !c.IsHidden), clientView()?.ControlledView?.Viewer));
        }

        public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) { Note(view); return inner.KeepHandAsync(view, mulligansTaken); }
        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) { Note(view); return inner.ChooseCardsToBottomAsync(view, count); }
        public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) { Note(view); return inner.ChooseActionAsync(view, legalActions); }
        public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) { Note(view); return inner.ChooseManaPaymentAsync(view, request); }
        public Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) { Note(view); return inner.ChooseTargetsAsync(view, request); }
        public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) { Note(view); return inner.DeclareAttackersAsync(view, possibleAttackers, defenders); }
        public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) { Note(view); return inner.DeclareBlockersAsync(view, request); }
        public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) { Note(view); return inner.AssignCombatDamageAsync(view, request); }
        public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) { Note(view); return inner.ChooseYesNoAsync(view, request); }
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) { Note(view); return inner.ChooseDiscardAsync(view, count); }
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) { Note(view); return inner.ChooseCardsAsync(view, request); }
        public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) { Note(view); return inner.ChooseModesAsync(view, request); }
        public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) { Note(view); return inner.ChooseNumberAsync(view, request); }
        public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) { Note(view); return inner.ChooseOptionAsync(view, request); }
    }

    /// <summary>The questions a client was sent, each with the turn it was asked on (the last view the client had been sent).</summary>
    private static IEnumerable<(int Turn, Ask Ask)> Asks(RecordingConnection wire)
    {
        var format = new WireFormat();
        int turn = 0;
        foreach (var message in wire.Received.Select(format.Read))
        {
            switch (message)
            {
                case ViewUpdate { View: var view }: turn = view.TurnNumber; break;
                case Ask ask: yield return (turn, ask); break;
            }
        }
    }

    [Fact]
    public void TheControllerIsAskedTheControlledPlayersDecisionsAndAnswersFromTheirView()
    {
        // Seed 1: player 0 takes turn 1, so turn 2 is player 1's turn.
        var table = new NetTable(seed: 1);
        // Player 1's next turn (turn 2) is controlled by player 0; the engine takes this at the start of that turn.
        table.Host.Game.State.PendingPlayerControl.Add((P1, P0, 0L));
        var client = table.Clients[0]!;
        var decided = new List<Decided>();
        client.Controller = new Watching(table.ClientBot(client, P0), () => client.View, decided);
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.Host.Game.State.TurnNumber >= 3), "game did not reach turn 3");
        for (int i = 0; i < 50; i++) table.Step(); // let the clients take in the last messages

        Assert.Equal(new PlayerControlled(P1, P0), Assert.Single(table.Host.Game.Log.OfType<PlayerControlled>()));

        // Player 1's client is asked nothing on turn 2: that player's decisions are made by the controller.
        Assert.DoesNotContain(Asks(table.Wires[1]!), a => a.Turn == 2);

        // Player 0's client is asked player 1's decisions on turn 2.
        var controllerAsks = Asks(table.Wires[0]!).ToList();
        Assert.Contains(controllerAsks, a => a.Turn == 2 && a.Ask.Question.For == P1);

        // Every decision made for player 1 used their view, which the client held as its controlled view, with their hand visible.
        var forControlled = decided.Where(d => d.Viewer == P1).ToList();
        Assert.NotEmpty(forControlled);
        Assert.All(forControlled, d => Assert.Equal(2, d.Turn));
        Assert.All(forControlled, d => Assert.Equal(P1, d.ClientControlledViewer));
        Assert.All(forControlled, d => Assert.True(d.ControlledHandVisible));

        // After that turn, player 0's questions are their own again.
        Assert.Contains(controllerAsks, a => a.Turn >= 3);
        Assert.All(controllerAsks.Where(a => a.Turn >= 3), a => Assert.Null(a.Ask.Question.For));
    }
}
