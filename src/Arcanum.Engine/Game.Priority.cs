// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>
    /// Priority round (rule 117): the active player acts first; when all players pass in succession the
    /// top of the stack resolves, or the step ends if the stack is empty.
    /// </summary>
    private async Task RunPriorityAsync()
    {
        var player = State.ActivePlayer;
        int consecutivePasses = 0;

        while (true)
        {
            _ct.ThrowIfCancellationRequested();
            CheckStateBasedActions();
            if (State.IsGameOver) return;
            if (State.GetPlayer(player).HasLost) player = State.NextLivingPlayer(player);

            State.PriorityPlayer = player;
            Emit(new PriorityGiven(player));
            var legal = GetLegalActions(player);
            var action = await ControllerOf(player).ChooseActionAsync(ViewFor(player), legal);
            Require(legal.Contains(action), $"Illegal action {action} for {player}.");

            if (action is PassPriority)
            {
                consecutivePasses++;
                if (consecutivePasses >= State.LivingPlayers.Count())
                {
                    if (State.Stack.Count == 0) break;
                    ResolveTopOfStack();
                    consecutivePasses = 0;
                    player = State.ActivePlayer; // rule 117.3b
                }
                else
                {
                    player = State.NextLivingPlayer(player);
                }
                continue;
            }

            await PerformAsync(player, action);
            consecutivePasses = 0; // rule 117.3c: the acting player receives priority again
        }
        State.PriorityPlayer = null;
    }

    public IReadOnlyList<PlayerAction> GetLegalActions(PlayerId playerId)
    {
        var player = State.GetPlayer(playerId);
        var actions = new List<PlayerAction> { PassPriority.Instance };
        bool sorcerySpeed = playerId == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0;

        foreach (var card in player.Hand.Select(State.GetCard))
        {
            if (card.Is(CardType.Land))
            {
                if (sorcerySpeed && player.LandsPlayedThisTurn < Config.LandsPerTurn) actions.Add(new PlayLand(card.Id));
                continue;
            }
            bool timingOk = card.Is(CardType.Instant) || sorcerySpeed;
            if (timingOk && ManaPayment.FindPlan(State, playerId, card.Definition.ManaCost) is not null)
                actions.Add(new CastSpell(card.Id));
        }

        // Mana abilities can be activated any time the player has priority (rule 605.3a).
        foreach (var source in ManaPayment.AvailableSources(State, playerId))
            foreach (var type in source.Definition.TapForMana.Distinct())
                actions.Add(new ActivateManaAbility(source.Id, type));
        return actions;
    }

    private async Task PerformAsync(PlayerId playerId, PlayerAction action)
    {
        var player = State.GetPlayer(playerId);
        switch (action)
        {
            case PlayLand play:
                player.LandsPlayedThisTurn++;
                MoveCard(play.Card, Zone.Battlefield);
                Emit(new LandPlayed(playerId, play.Card));
                break;

            case ActivateManaAbility mana:
                TapForMana(player, new ManaTap(mana.Source, mana.Type));
                break;

            case CastSpell cast:
                await CastSpellAsync(player, cast.Card);
                break;

            default:
                throw new InvalidDecisionException($"Unsupported action {action}.");
        }
    }

    private async Task CastSpellAsync(Player player, CardId cardId)
    {
        var cost = State.GetCard(cardId).Definition.ManaCost;
        var plan = ManaPayment.FindPlan(State, player.Id, cost)
                   ?? throw new InvalidOperationException("Legal spell became unpayable.");
        var (fromPool, remaining) = ManaPayment.ApplyPool(cost, player.ManaPool);
        var sources = ManaPayment.AvailableSources(State, player.Id)
            .Select(c => new ManaSourceOption(c.Id, c.Definition.TapForMana))
            .ToList();
        var request = new ManaPaymentRequest(cardId, cost, fromPool, remaining, plan.Taps, sources);

        var taps = await ControllerOf(player.Id).ChooseManaPaymentAsync(ViewFor(player.Id), request);
        if (taps is null) return; // cancelled: nothing was paid, the card never left the hand

        Require(taps.Select(t => t.Source).Distinct().Count() == taps.Count, "Each source can be tapped only once.");
        Require(taps.All(t => sources.Any(s => s.Source == t.Source && s.Types.Contains(t.Type))), "Illegal mana source.");
        var (owed, excess) = ManaPayment.Apply(remaining, taps.Select(t => t.Type));
        Require(owed.ManaValue == 0, $"Payment is short by {owed}.");
        Require(excess == 0, "Payment taps more mana than the spell costs.");

        foreach (var tap in taps) TapForMana(player, tap);
        foreach (var type in fromPool.Concat(taps.Select(t => t.Type))) player.ManaPool.Remove(type);
        MoveCard(cardId, Zone.Stack);
        State.Stack.Add(new SpellOnStack(cardId, player.Id));
        Emit(new SpellCast(player.Id, cardId));
    }

    private void TapForMana(Player player, ManaTap tap)
    {
        State.GetCard(tap.Source).Tapped = true;
        Emit(new PermanentTapped(tap.Source));
        player.ManaPool.Add(tap.Type);
        Emit(new ManaAdded(player.Id, tap.Type, tap.Source));
    }

    private void ResolveTopOfStack()
    {
        var item = State.Stack[^1];
        State.Stack.RemoveAt(State.Stack.Count - 1);
        switch (item)
        {
            case SpellOnStack spell:
                var card = State.GetCard(spell.Card);
                // Instant/sorcery effects arrive with the ability system (M4).
                MoveCard(spell.Card, card.Types.IsPermanent() ? Zone.Battlefield : Zone.Graveyard, controller: spell.Controller);
                Emit(new SpellResolved(spell.Card));
                break;
        }
    }
}
