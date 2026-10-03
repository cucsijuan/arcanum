// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
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
            await SettleBeforePriorityAsync();
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

            if (await PerformAsync(player, action)) consecutivePasses = 0; // rule 117.3c
        }
        State.PriorityPlayer = null;
    }

    /// <summary>State-based actions and pending triggers, repeated until neither applies (rule 117.5).</summary>
    private async Task SettleBeforePriorityAsync()
    {
        do
        {
            CheckStateBasedActions();
            if (State.IsGameOver) return;
        }
        while (await PutPendingTriggersOnStackAsync());
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
            if (timingOk && HasLegalTargets(CastingTargets(card.Definition), playerId, card.Id)
                && ManaPayment.FindPlan(State, playerId, card.Definition.ManaCost) is not null)
                actions.Add(new CastSpell(card.Id));
        }

        foreach (var permanent in State.PermanentsControlledBy(playerId))
        {
            var abilities = permanent.Definition.Abilities;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i] is ActivatedAbility ability && CanActivate(permanent, ability, playerId, sorcerySpeed))
                    actions.Add(new ActivateAbility(permanent.Id, i));
        }

        // Mana abilities can be activated any time the player has priority (rule 605.3a).
        foreach (var source in ManaPayment.AvailableSources(State, playerId))
            foreach (var type in source.Definition.TapForMana.Distinct())
                actions.Add(new ActivateManaAbility(source.Id, type));
        return actions;
    }

    private bool CanActivate(Card source, ActivatedAbility ability, PlayerId player, bool sorcerySpeed)
    {
        if (ability.SorcerySpeed && !sorcerySpeed) return false;
        if (ability.Cost.Tap && (source.Tapped || source.IsSummoningSick)) return false;
        if (!HasLegalTargets(ability, player, source.Id)) return false;
        return ManaPayment.FindPlan(State, player, ability.Cost.Mana, exclude: ability.Cost.Tap ? source.Id : null) is not null;
    }

    /// <returns>False if the action was cancelled and nothing happened.</returns>
    private async Task<bool> PerformAsync(PlayerId playerId, PlayerAction action)
    {
        var player = State.GetPlayer(playerId);
        switch (action)
        {
            case PlayLand play:
                player.LandsPlayedThisTurn++;
                MoveCard(play.Card, Zone.Battlefield);
                Emit(new LandPlayed(playerId, play.Card));
                return true;

            case ActivateManaAbility mana:
                TapForMana(player, new ManaTap(mana.Source, mana.Type));
                return true;

            case CastSpell cast:
                return await CastSpellAsync(player, cast.Card);

            case ActivateAbility activate:
                return await ActivateAbilityAsync(player, activate);

            default:
                throw new InvalidDecisionException($"Unsupported action {action}.");
        }
    }

    /// <summary>Casting (rule 601.2): choose targets, then pay; either step can be cancelled with nothing changed.</summary>
    private async Task<bool> CastSpellAsync(Player player, CardId cardId)
    {
        var card = State.GetCard(cardId);
        var targets = await ChooseTargetsAsync(player.Id, CastingTargets(card.Definition), cardId, card.Name, canCancel: true);
        if (targets is null) return false;

        if (!await PayManaAsync(player, cardId, card.Definition.ManaCost, exclude: null)) return false;

        MoveCard(cardId, Zone.Stack);
        State.Stack.Add(new SpellOnStack(cardId, player.Id, targets));
        Emit(new SpellCast(player.Id, cardId));
        return true;
    }

    /// <summary>Activating (rule 602.2): choose targets, pay every cost, put the ability on the stack.</summary>
    private async Task<bool> ActivateAbilityAsync(Player player, ActivateAbility action)
    {
        var source = State.GetCard(action.Source);
        var ability = (ActivatedAbility)source.Definition.Abilities[action.Index];
        var targets = await ChooseTargetsAsync(player.Id, ability, source.Id, ability.Text, canCancel: true);
        if (targets is null) return false;

        if (!await PayManaAsync(player, source.Id, ability.Cost.Mana, exclude: ability.Cost.Tap ? source.Id : null)) return false;
        if (ability.Cost.Tap)
        {
            source.Tapped = true;
            Emit(new PermanentTapped(source.Id));
        }
        if (ability.Cost.SacrificeSelf) MoveCard(source.Id, Zone.Graveyard);

        State.Stack.Add(new AbilityOnStack(source.Id, ability, player.Id, targets));
        Emit(new AbilityActivated(player.Id, source.Id, ability.Text));
        return true;
    }

    /// <summary>Asks the player how to pay a mana cost (floating mana first). Returns false if they cancel.</summary>
    private async Task<bool> PayManaAsync(Player player, CardId source, ManaCost cost, CardId? exclude)
    {
        if (cost.ManaValue == 0) return true;
        var plan = ManaPayment.FindPlan(State, player.Id, cost, exclude)
                   ?? throw new InvalidOperationException("Legal action became unpayable.");
        var (fromPool, remaining) = ManaPayment.ApplyPool(cost, player.ManaPool);
        var sources = ManaPayment.AvailableSources(State, player.Id, exclude)
            .Select(c => new ManaSourceOption(c.Id, c.Definition.TapForMana))
            .ToList();
        var request = new ManaPaymentRequest(source, cost, fromPool, remaining, plan.Taps, sources);

        var taps = await ControllerOf(player.Id).ChooseManaPaymentAsync(ViewFor(player.Id), request);
        if (taps is null) return false;

        Require(taps.Select(t => t.Source).Distinct().Count() == taps.Count, "Each source can be tapped only once.");
        Require(taps.All(t => sources.Any(s => s.Source == t.Source && s.Types.Contains(t.Type))), "Illegal mana source.");
        var (owed, excess) = ManaPayment.Apply(remaining, taps.Select(t => t.Type));
        Require(owed.ManaValue == 0, $"Payment is short by {owed}.");
        Require(excess == 0, "Payment taps more mana than the cost.");

        foreach (var tap in taps) TapForMana(player, tap);
        foreach (var type in fromPool.Concat(taps.Select(t => t.Type))) player.ManaPool.Remove(type);
        return true;
    }

    private void TapForMana(Player player, ManaTap tap)
    {
        State.GetCard(tap.Source).Tapped = true;
        Emit(new PermanentTapped(tap.Source));
        player.ManaPool.Add(tap.Type);
        Emit(new ManaAdded(player.Id, tap.Type, tap.Source));
    }

    /// <summary>What a spell targets when cast: an instant/sorcery's targets, or an Aura's enchant target.</summary>
    private static AbilityDefinition? CastingTargets(CardDefinition definition) =>
        definition.Spell ?? (definition.EnchantTarget is { } enchant ? new SpellAbility { Targets = new[] { enchant } } : null);

    private void ResolveTopOfStack()
    {
        var item = State.Stack[^1];
        State.Stack.RemoveAt(State.Stack.Count - 1);
        switch (item)
        {
            case SpellOnStack spell:
            {
                var card = State.GetCard(spell.Card);
                if (CastingTargets(card.Definition) is { } effect && !ApplyResolution(item, effect, card))
                {
                    MoveCard(spell.Card, Zone.Graveyard);
                    Emit(new FizzledOnResolution(spell.Card));
                    break;
                }
                // An Aura spell enters attached to the object it targeted (rule 303.4f).
                var attachTo = card.Definition.EnchantTarget is not null ? item.Targets[0].Target.Card : null;
                MoveCard(spell.Card, card.Types.IsPermanent() ? Zone.Battlefield : Zone.Graveyard, controller: spell.Controller, attachTo: attachTo);
                Emit(new SpellResolved(spell.Card));
                break;
            }
            case AbilityOnStack ability:
                if (!ApplyResolution(item, ability.Ability, State.GetCard(ability.Source)))
                {
                    Emit(new FizzledOnResolution(ability.Source));
                    break;
                }
                Emit(new AbilityResolved(ability.Source, ability.Ability.Text));
                break;
        }
    }
}
