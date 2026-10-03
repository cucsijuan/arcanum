// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

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
                    await ResolveTopOfStackAsync();
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
            await ResolveEnterChoicesAsync();
            CheckStateBasedActions();
            if (State.IsGameOver) return;
            await OfferCommanderReturnsAsync();
        }
        while (await PutPendingTriggersOnStackAsync());
    }

    private static readonly string[] ColorNames = { "White", "Blue", "Black", "Red", "Green" };
    private static readonly string[] ColorLetters = { "W", "U", "B", "R", "G" };

    /// <summary>"As this enters, choose a color / creature type" (rule 614.12), made before anything else happens.</summary>
    private async Task ResolveEnterChoicesAsync()
    {
        while (State.PendingEnterChoices.Count > 0)
        {
            var (id, version) = State.PendingEnterChoices[0];
            State.PendingEnterChoices.RemoveAt(0);
            var card = State.GetCard(id);
            if (card.Version != version || card.Zone != Zone.Battlefield) continue;
            var who = card.Controller;
            if (card.Definition.ChooseOnEnter == EnterChoice.Color)
            {
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose a color", id, ColorNames, OptionKind.Color));
                Require(i >= 0 && i < ColorNames.Length, "Choose one of the colors.");
                card.ChosenColor = ColorLetters[i];
            }
            else
            {
                var types = CreatureTypeOptions(who);
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose a creature type", id, types, OptionKind.CreatureType));
                Require(i >= 0 && i < types.Count, "Choose one of the creature types.");
                card.ChosenType = types[i];
                if (card.Definition.CountersPerChosenType is { } kind)
                    PutCounters(card, kind, State.PermanentsControlledBy(who).Count(c => c.IsCreature && c.HasSubtype(types[i])));
            }
            Emit(new ChoiceMade(id, card.ChosenColor is { } c ? ColorNames[Array.IndexOf(ColorLetters, c)] : card.ChosenType!));
            RecomputeContinuousEffects();
        }
    }

    /// <summary>Creature types worth offering: those among the player's cards first, then common ones.</summary>
    private List<string> CreatureTypeOptions(PlayerId player)
    {
        var own = State.Cards.Values.Where(c => c.Owner == player && c.Definition.Is(CardType.Creature))
            .SelectMany(c => c.Definition.Subtypes).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key);
        var common = new[] { "Human", "Elf", "Goblin", "Zombie", "Vampire", "Cat", "Dragon", "Angel", "Wizard", "Soldier", "Knight", "Merfolk", "Beast", "Spirit", "Warrior" };
        return own.Concat(common).Distinct().Take(40).ToList();
    }

    public IReadOnlyList<PlayerAction> GetLegalActions(PlayerId playerId)
    {
        var player = State.GetPlayer(playerId);
        var actions = new List<PlayerAction> { PassPriority.Instance };
        bool sorcerySpeed = playerId == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0;

        // Cards in hand, commanders in the command zone (rule 903.8), cards with flashback in the graveyard and
        // exiled cards the player may play this turn.
        var castable = player.Hand.Concat(player.Command)
            .Concat(player.Graveyard.Where(id => State.GetCard(id).Definition.Flashback is not null))
            .Concat(PlayableExile(playerId));
        foreach (var card in castable.Select(State.GetCard))
        {
            if (card.Is(CardType.Land))
            {
                if (card.Zone is Zone.Hand or Zone.Exile && sorcerySpeed && player.LandsPlayedThisTurn < Config.LandsPerTurn) actions.Add(new PlayLand(card.Id));
                continue;
            }
            bool timingOk = card.Is(CardType.Instant) || card.Definition.KeywordAbilities.Contains(Keyword.Flash) || sorcerySpeed
                            || Has(playerId, Replacements.YourSpellsHaveFlash);
            if (timingOk && HasLegalTargets(CastingTargets(card.Definition), playerId, card.Id)
                && CanPayExtra(playerId, card.Definition.AdditionalCost, card.Id)
                && CastingCost(card).WithX(0).Variants().Any(v => ManaPayment.FindPlan(State, playerId, v) is not null))
                actions.Add(new CastSpell(card.Id));
        }

        foreach (var permanent in State.PermanentsControlledBy(playerId).Concat(player.Graveyard.Select(State.GetCard)))
        {
            var abilities = permanent.Definition.Abilities;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i] is ActivatedAbility ability && ability.Cost.FromGraveyard == (permanent.Zone == Zone.Graveyard)
                    && CanActivate(permanent, ability, i, playerId, sorcerySpeed))
                    actions.Add(new ActivateAbility(permanent.Id, i));
        }

        // Mana abilities can be activated any time the player has priority (rule 605.3a).
        foreach (var source in ManaPayment.AvailableSources(State, playerId))
            foreach (var type in source.ManaTypes.Distinct())
                actions.Add(new ActivateManaAbility(source.Id, type));
        return actions;
    }

    /// <summary>Exiled cards <paramref name="player"/> may currently play.</summary>
    private IEnumerable<CardId> PlayableExile(PlayerId player) =>
        State.PlayableFromExile.Where(p => p.Player == player && p.UntilTurn >= State.TurnNumber
                                           && State.GetCard(p.Card) is { Zone: Zone.Exile } c && c.Version == p.Version)
            .Select(p => p.Card).Distinct().ToList();

    private bool CanActivate(Card source, ActivatedAbility ability, int index, PlayerId player, bool sorcerySpeed)
    {
        if (ability.Cost.Loyalty is { } loyalty)
        {
            // Loyalty abilities: sorcery timing, one per planeswalker per turn, and enough loyalty to pay (606.3).
            if (!sorcerySpeed || source.LoyaltyActivatedThisTurn) return false;
            if (loyalty < 0 && source.CounterCount(CounterKind.Loyalty) < -loyalty) return false;
        }
        if (ability.SorcerySpeed && !sorcerySpeed) return false;
        if (ability.OncePerTurn && source.ActivatedThisTurn.Contains(index)) return false;
        if (ability.ActivationCondition is { } condition && !Holds(condition, player, source)) return false;
        if (ability.Cost.Tap && (source.Tapped || source.IsSummoningSick)) return false;
        if (ability.Cost.RemoveCounters > 0 && source.CounterCount(ability.Cost.RemoveCounterKind) < ability.Cost.RemoveCounters) return false;
        if (!CanPayExtra(player, ability.Cost.Extra, source.Id)) return false;
        if (!HasLegalTargets(ability, player, source.Id)) return false;
        return ability.Cost.Mana.WithX(0).Variants().Any(v => ManaPayment.FindPlan(State, player, v, exclude: ability.Cost.Tap ? source.Id : null) is not null);
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

    /// <summary>
    /// Casting (rule 601.2): choose modes and targets, the value of X and whether to kick, then pay every cost.
    /// Every step up to paying mana can be cancelled with nothing changed.
    /// </summary>
    private async Task<bool> CastSpellAsync(Player player, CardId cardId)
    {
        var card = State.GetCard(cardId);
        bool flashback = card.Zone == Zone.Graveyard;
        var ability = CastingTargets(card.Definition);
        if (ability is not null)
        {
            ability = await ChooseModesAsync(player.Id, ability, cardId, canCancel: true);
            if (ability is null) return false;
        }
        var targets = await ChooseTargetsAsync(player.Id, ability, cardId, card.Name, canCancel: true);
        if (targets is null) return false;

        var cost = CastingCost(card);
        int x = 0;
        if (cost.XCount > 0)
        {
            int max = MaxAffordableX(player.Id, cost, null);
            x = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{card.Name}: choose X", cardId, 0, max));
            Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
        }
        cost = cost.WithX(x);

        bool kicked = false;
        if (card.Definition.Kicker is { } kicker && Payable(player.Id, cost.Plus(kicker), null))
        {
            kicked = await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Pay kicker {kicker} for {card.Name}?", cardId));
            if (kicked) cost = cost.Plus(kicker);
        }

        // Ward: targeting an opponent's warded permanent costs extra; unpaid, the spell is countered (702.21).
        var (wardMana, wardLife) = WardCost(player.Id, targets);
        bool wardPaid = wardMana.ManaValue == 0 && wardLife == 0
                        || (Payable(player.Id, cost.Plus(wardMana), null) && player.Life >= wardLife);
        if (wardPaid) cost = cost.Plus(wardMana);

        if (!await PayManaAsync(player, cardId, cost, exclude: null)) return false;
        if (wardPaid && wardLife > 0) ChangeLife(player.Id, -wardLife);
        await PayExtraAsync(player.Id, card.Definition.AdditionalCost, cardId);

        if (card.Zone == Zone.Command) player.CommanderCasts[cardId] = player.CommanderCasts.GetValueOrDefault(cardId) + 1;
        MoveCard(cardId, Zone.Stack);
        card.Kicked = kicked;
        State.Stack.Add(new SpellOnStack(cardId, player.Id, targets)
        {
            Ability = ability != CastingTargets(card.Definition) ? ability : null,
            X = x, Kicked = kicked, Flashback = flashback,
        });
        Emit(new SpellCast(player.Id, cardId));
        if (!wardPaid) CounterSpellOnStack(cardId);
        return true;
    }

    /// <summary>Activating (rule 602.2): choose modes and targets, pay every cost, put the ability on the stack.</summary>
    private async Task<bool> ActivateAbilityAsync(Player player, ActivateAbility action)
    {
        var source = State.GetCard(action.Source);
        var ability = await ChooseModesAsync(player.Id, (ActivatedAbility)source.Definition.Abilities[action.Index], source.Id, canCancel: true);
        if (ability is null) return false;
        var targets = await ChooseTargetsAsync(player.Id, ability, source.Id, ability.Text, canCancel: true);
        if (targets is null) return false;

        var exclude = ability.Cost.Tap ? source.Id : (CardId?)null;
        var cost = ability.Cost.Mana;
        int x = 0;
        if (cost.XCount > 0)
        {
            int max = MaxAffordableX(player.Id, cost, exclude);
            x = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{source.Name}: choose X", source.Id, 0, max));
            Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
        }
        cost = cost.WithX(x);
        var (wardMana, wardLife) = WardCost(player.Id, targets);
        bool wardPaid = wardMana.ManaValue == 0 && wardLife == 0 || (Payable(player.Id, cost.Plus(wardMana), exclude) && player.Life >= wardLife);
        if (wardPaid) cost = cost.Plus(wardMana);

        if (!await PayManaAsync(player, source.Id, cost, exclude)) return false;
        if (wardPaid && wardLife > 0) ChangeLife(player.Id, -wardLife);
        if (ability.Cost.Tap)
        {
            source.Tapped = true;
            Emit(new PermanentTapped(source.Id));
        }
        if (ability.Cost.RemoveCounters > 0)
            source.Counters[ability.Cost.RemoveCounterKind] = source.CounterCount(ability.Cost.RemoveCounterKind) - ability.Cost.RemoveCounters;
        if (ability.Cost.ExileSelf) MoveCard(source.Id, Zone.Exile);
        await PayExtraAsync(player.Id, ability.Cost.Extra, source.Id);
        if (ability.OncePerTurn) source.ActivatedThisTurn.Add(action.Index);
        if (ability.Cost.Loyalty is { } loyalty)
        {
            source.LoyaltyActivatedThisTurn = true;
            if (loyalty > 0) PutCounters(source, CounterKind.Loyalty, loyalty);
            else if (loyalty < 0) source.Counters[CounterKind.Loyalty] = source.CounterCount(CounterKind.Loyalty) + loyalty;
        }
        if (ability.Cost.SacrificeSelf)
        {
            if (source.Zone == Zone.Graveyard) MoveCard(source.Id, Zone.Exile); // "Exile this card from your graveyard"
            else SacrificePermanent(source.Id);
        }

        Emit(new AbilityActivated(player.Id, source.Id, ability.Text));
        if (!wardPaid) return true; // countered by ward
        State.Stack.Add(new AbilityOnStack(source.Id, ability, player.Id, targets) { X = x });
        return true;
    }

    private bool Payable(PlayerId player, ManaCost cost, CardId? exclude) =>
        cost.Variants().Any(v => ManaPayment.FindPlan(State, player, v, exclude) is not null);

    private int MaxAffordableX(PlayerId player, ManaCost cost, CardId? exclude)
    {
        int x = 0;
        while (x < 99 && Payable(player, cost.WithX(x + 1), exclude)) x++;
        return x;
    }

    /// <summary>Total ward cost of the opponents' permanents among <paramref name="targets"/>.</summary>
    private (ManaCost Mana, int Life) WardCost(PlayerId player, IReadOnlyList<ChosenTarget> targets)
    {
        var mana = ManaCost.Zero;
        int life = 0;
        foreach (var t in targets)
        {
            if (t.Target.Card is not { } id) continue;
            var card = State.GetCard(id);
            if (card.Zone != Zone.Battlefield || card.Controller == player) continue;
            if (card.Definition.WardMana is { } m) mana = mana.Plus(m);
            life += card.Definition.WardLife;
        }
        return (mana, life);
    }

    /// <summary>Whether discard / sacrifice / life costs can be paid (the source itself can't pay them).</summary>
    private bool CanPayExtra(PlayerId playerId, ExtraCost? extra, CardId source)
    {
        if (extra is null) return true;
        var player = State.GetPlayer(playerId);
        if (player.Hand.Count(id => id != source) < extra.Discard) return false;
        if (extra.PayLife > player.Life) return false;
        if (extra.Sacrifice is { } filter && SacrificeCandidates(playerId, filter, source).Count < extra.SacrificeCount) return false;
        return true;
    }

    private List<Card> SacrificeCandidates(PlayerId player, ObjectFilter filter, CardId source)
    {
        var sourceCard = State.GetCard(source);
        var any = filter with { Controller = ControllerFilter.Any };
        return State.PermanentsControlledBy(player).Where(c => Matches(any, c, player, sourceCard, player)).ToList();
    }

    private async Task PayExtraAsync(PlayerId playerId, ExtraCost? extra, CardId source)
    {
        if (extra is null) return;
        var player = State.GetPlayer(playerId);
        if (extra.PayLife > 0) ChangeLife(playerId, -extra.PayLife);
        if (extra.Discard > 0)
        {
            var hand = player.Hand.Where(id => id != source).ToList();
            var options = hand.Select(id => ViewBuilder.Card(State, id, playerId)).ToList();
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest($"Discard {extra.Discard} to pay the cost", source, options, extra.Discard, extra.Discard, CardChoicePurpose.Discard));
            Require(chosen.Count == extra.Discard && chosen.Distinct().Count() == chosen.Count && chosen.All(hand.Contains), "Discard from your hand.");
            foreach (var id in chosen)
            {
                MoveCard(id, Zone.Graveyard);
                Emit(new CardDiscarded(playerId, id));
            }
        }
        if (extra.Sacrifice is { } filter)
        {
            var candidates = SacrificeCandidates(playerId, filter, source);
            var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, playerId)).ToList();
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest($"Sacrifice {extra.SacrificeCount} to pay the cost", source, options, extra.SacrificeCount, extra.SacrificeCount, CardChoicePurpose.Sacrifice));
            Require(chosen.Count == extra.SacrificeCount && chosen.Distinct().Count() == chosen.Count && chosen.All(id => candidates.Any(c => c.Id == id)),
                "Sacrifice one of the listed permanents.");
            foreach (var id in chosen) SacrificePermanent(id);
        }
    }

    /// <summary>Asks the player how to pay a mana cost (floating mana first). Returns false if they cancel.</summary>
    private async Task<bool> PayManaAsync(Player player, CardId source, ManaCost cost, CardId? exclude)
    {
        if (cost.ManaValue == 0) return true;
        // Hybrid symbols: pay the first way that works (the payment dialog then works on a concrete cost).
        cost = cost.Variants().FirstOrDefault(v => ManaPayment.FindPlan(State, player.Id, v, exclude) is not null)
               ?? throw new InvalidOperationException("Legal action became unpayable.");
        var plan = ManaPayment.FindPlan(State, player.Id, cost, exclude)!;
        var (fromPool, remaining) = ManaPayment.ApplyPool(cost, player.ManaPool);
        var sources = ManaPayment.AvailableSources(State, player.Id, exclude)
            .Select(c => new ManaSourceOption(c.Id, c.ManaTypes, c.ManaAmount))
            .ToList();
        var request = new ManaPaymentRequest(source, cost, fromPool, remaining, plan.Taps, sources);

        var taps = await ControllerOf(player.Id).ChooseManaPaymentAsync(ViewFor(player.Id), request);
        if (taps is null) return false;

        Require(taps.Select(t => t.Source).Distinct().Count() == taps.Count, "Each source can be tapped only once.");
        Require(taps.All(t => sources.Any(s => s.Source == t.Source && s.Types.Contains(t.Type))), "Illegal mana source.");
        var produced = ManaPayment.Produced(State, taps).ToList();
        var (owed, excess) = ManaPayment.Apply(remaining, produced);
        Require(owed.ManaValue == 0, $"Payment is short by {owed}.");
        // No pointless taps: surplus is only allowed when a source adds more mana than is still needed.
        Require(excess == 0 || taps.Any(t => State.GetCard(t.Source).ManaAmount > 1), "Payment taps more mana than the cost.");

        foreach (var tap in taps) TapForMana(player, tap);
        // Pay the whole cost from the pool; any surplus keeps floating (rule 106.4).
        var (paid, left) = ManaPayment.ApplyPool(cost, player.ManaPool);
        Require(left.ManaValue == 0, "Payment is short.");
        foreach (var type in paid) player.ManaPool.Remove(type);
        return true;
    }

    private void TapForMana(Player player, ManaTap tap)
    {
        var source = State.GetCard(tap.Source);
        source.Tapped = true;
        Emit(new PermanentTapped(tap.Source));
        for (int i = 0; i < Math.Max(1, source.ManaAmount); i++)
        {
            player.ManaPool.Add(tap.Type);
            Emit(new ManaAdded(player.Id, tap.Type, tap.Source));
        }
        if (source.Definition.SacrificeForMana) MoveCard(source.Id, Zone.Graveyard);
    }

    /// <summary>
    /// Total cost to cast a card (601.2f): its mana cost (or flashback cost from the graveyard), plus commander tax
    /// when cast from the command zone (903.8), minus cost reductions. X stays unresolved.
    /// </summary>
    public ManaCost CastingCost(Card card)
    {
        if (card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.WithoutPaying))
            return ManaCost.Zero; // "without paying its mana cost"
        var cost = card.Zone == Zone.Graveyard && card.Definition.Flashback is { } flashback ? flashback : card.Definition.ManaCost;
        if (card.Zone == Zone.Command && Config.Commander is { } rules)
            cost = cost.PlusGeneric(rules.TaxPerCast * State.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id));
        return cost.MinusGeneric(CostReductionFor(card));
    }

    private int CostReductionFor(Card card)
    {
        var caster = card.Owner;
        int total = 0;
        if (card.Definition.SelfCostReduction is { } self)
        {
            if (self.PerPermanent is { } perPermanent)
                total += self.Amount * State.Battlefield.Select(State.GetCard).Count(c => Matches(perPermanent, c, c.Controller, card, caster));
            else if (self.PerGraveyardCard is { } perCard)
                total += self.Amount * State.GetPlayer(caster).Graveyard.Select(State.GetCard)
                    .Count(c => Matches(perCard with { Controller = ControllerFilter.Any }, c, caster, card, caster));
            else if (self.ByTotalPower)
                total += self.Amount * State.PermanentsControlledBy(caster).Where(c => c.IsCreature).Sum(c => Math.Max(0, c.Power));
            else if (self.Condition is null || Holds(self.Condition, caster, card))
                total += self.Amount;
        }
        foreach (var permanent in State.PermanentsControlledBy(caster))
            foreach (var reduction in permanent.Definition.Abilities.OfType<SpellCostReduction>())
                if (Matches(reduction.Spells with { Controller = ControllerFilter.Any }, card, caster, permanent, caster)) total += reduction.Amount;
        return total;
    }

    /// <summary>What a spell targets when cast: an instant/sorcery's targets, or an Aura's enchant target.</summary>
    private static AbilityDefinition? CastingTargets(CardDefinition definition) =>
        definition.Spell ?? (definition.EnchantTarget is { } enchant ? new SpellAbility { Targets = new[] { enchant } } : null);

    private async Task ResolveTopOfStackAsync()
    {
        var item = State.Stack[^1];
        State.Stack.RemoveAt(State.Stack.Count - 1);
        switch (item)
        {
            case SpellOnStack spell:
            {
                var card = State.GetCard(spell.Card);
                var discard = spell.Flashback ? Zone.Exile : Zone.Graveyard; // flashback: exiled whenever it leaves the stack
                if ((spell.Ability ?? CastingTargets(card.Definition)) is { } effect && !await ApplyResolutionAsync(item, effect, card))
                {
                    MoveCard(spell.Card, discard);
                    Emit(new FizzledOnResolution(spell.Card));
                    break;
                }
                if (card.Zone != Zone.Stack) break; // the spell moved itself (shuffled away, exiled...)
                // An Aura spell enters attached to the object it targeted (rule 303.4f).
                var attachTo = card.Definition.EnchantTarget is not null ? item.Targets[0].Target.Card : null;
                MoveCard(spell.Card, card.Types.IsPermanent() ? Zone.Battlefield : discard, controller: spell.Controller, attachTo: attachTo,
                    kicked: spell.Kicked && card.Types.IsPermanent());
                if (card.Zone == Zone.Battlefield && card.Definition.EntersWithXCounters && spell.X > 0)
                    card.Counters[CounterKind.PlusOnePlusOne] = card.CounterCount(CounterKind.PlusOnePlusOne) + spell.X;
                Emit(new SpellResolved(spell.Card));
                break;
            }
            case AbilityOnStack ability:
                if (!await ApplyResolutionAsync(item, ability.Ability, State.GetCard(ability.Source)))
                {
                    Emit(new FizzledOnResolution(ability.Source));
                    break;
                }
                Emit(new AbilityResolved(ability.Source, ability.Ability.Text));
                break;
        }
    }
}
