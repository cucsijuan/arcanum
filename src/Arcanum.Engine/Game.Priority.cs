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
            if (State.IsGameOver || _endTurnRequested) break;
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
            await CheckStateBasedActionsAsync();
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
            else if (card.Definition.ChooseOnEnter == EnterChoice.PayLifeOrTapped)
            {
                // "As this enters, you may pay N life. If you don't, it enters tapped."
                int life = Math.Max(card.Definition.EnterLife, 1);
                bool pay = State.GetPlayer(who).Life >= life
                           && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Pay {life} life so {card.Name} enters untapped?", id));
                if (pay) ChangeLife(who, -life);
                else card.Tapped = true;
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.OddOrEven)
            {
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose odd or even", id, new[] { "Odd", "Even" }, OptionKind.Other));
                Require(i is 0 or 1, "Choose odd or even.");
                card.ChosenParity = i == 0 ? "odd" : "even";
                Emit(new ChoiceMade(id, i == 0 ? "Odd" : "Even"));
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.CardName)
            {
                // "Look at an opponent's hand, then choose any card name."
                var names = new List<string>();
                if (await ChooseOpponentAsync(who, card, "Choose the opponent whose hand you look at") is { } opponent)
                {
                    var hand = State.GetPlayer(opponent).Hand;
                    Emit(new HandRevealed(opponent, hand.ToList()));
                    names.AddRange(hand.Select(c => State.GetCard(c).Name));
                }
                // Any card name: every name in the game is offered (seen ones first).
                names.AddRange(State.Cards.Values.Where(c => !c.Definition.IsEmblem).Select(c => c.Definition.Name).OrderBy(n => n));
                names = names.Distinct().ToList();
                if (names.Count == 0) continue;
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose a card name", id, names, OptionKind.Other));
                Require(i >= 0 && i < names.Count, "Choose one of the names.");
                card.ChosenName = names[i];
                Emit(new ChoiceMade(id, names[i]));
                continue;
            }
            else
            {
                var types = CreatureTypeOptions(who);
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose a creature type", id, types, OptionKind.CreatureType));
                Require(i >= 0 && i < types.Count, "Choose one of the creature types.");
                card.ChosenType = types[i];
                if (card.Definition.CountersPerChosenType is { } kind)
                    PutCounters(card, kind, State.PermanentsControlledBy(who).Count(c => c.IsCreature && c.HasSubtype(types[i])), who);
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
        var inGame = State.Cards.Values.Where(c => c.Definition.Is(CardType.Creature)).SelectMany(c => c.Definition.Subtypes).OrderBy(t => t);
        return own.Concat(common).Concat(inGame).Distinct().ToList();
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
            .Concat(PlayableExile(playerId))
            .Concat(State.PlayableFromGraveyard.Concat(State.FlashbackGranted)
                .Where(p => p.Player == playerId && State.GetCard(p.Card) is { Zone: Zone.Graveyard } c && c.Version == p.Version).Select(p => p.Card))
            .Concat(OtherCastableCards(playerId))
            .Distinct();
        foreach (var card in castable.Select(State.GetCard))
        {
            if (card.Is(CardType.Land))
            {
                if (card.Zone is Zone.Hand or Zone.Exile or Zone.Graveyard && sorcerySpeed && player.LandsPlayedThisTurn < LandsAllowed(playerId))
                    actions.Add(new PlayLand(card.Id));
            }
            else if (State.SpellsForbiddenTurn == State.TurnNumber) continue;
            else if (CanCast(card, playerId, sorcerySpeed)) actions.Add(new CastSpell(card.Id));
            // An adventurer card can be cast as its Adventure wherever it could be cast (rule 715.3), except from exile after an adventure.
            if (card.PrintedDefinition.Adventure is not null && !card.OnAdventure && card.Zone != Zone.Command)
            {
                card.AsAdventure = true;
                bool canAdventure = CanCast(card, playerId, sorcerySpeed);
                card.AsAdventure = false;
                if (canAdventure) actions.Add(new CastSpell(card.Id, Adventure: true));
            }
        }

        foreach (var permanent in State.PermanentsControlledBy(playerId).Concat(player.Graveyard.Select(State.GetCard)).Concat(player.Hand.Select(State.GetCard)))
        {
            var abilities = permanent.Abilities;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i] is ActivatedAbility ability && ability.Cost.FromGraveyard == (permanent.Zone == Zone.Graveyard)
                    && ability.Cost.FromHand == (permanent.Zone == Zone.Hand)
                    && CanActivate(permanent, ability, i, playerId, sorcerySpeed))
                    actions.Add(new ActivateAbility(permanent.Id, i));
        }

        // Mana abilities can be activated any time the player has priority (rule 605.3a).
        foreach (var source in ManaPayment.AvailableSources(State, playerId, usable: (_, _) => true))
            foreach (var option in ManaPayment.UsableOptions(source, (_, _) => true))
                // "One mana of each color" is a single activation, not a choice of type.
                foreach (var type in source.ManaOptions[option].OneOfEach ? source.ManaOptions[option].Types.Take(1) : source.ManaOptions[option].Types.Distinct())
                    actions.Add(new ActivateManaAbility(source.Id, type, option));
        return actions;
    }

    /// <summary>Whether the card (as it is now: the card, or its Adventure) can be cast: timing, targets and every cost.</summary>
    private bool CanCast(Card card, PlayerId playerId, bool sorcerySpeed)
    {
        bool timingOk = card.Is(CardType.Instant) || card.Definition.KeywordAbilities.Contains(Keyword.Flash) || sorcerySpeed
                        || Has(playerId, Replacements.YourSpellsHaveFlash)
                        || (card.Definition.FlashIf is { } flashIf && Holds(flashIf, playerId, card))
                        || GrantedFlash(card, playerId);
        if (PaysLife(card) && State.GetPlayer(playerId).Life < card.Definition.ManaCost.ManaValue) return false;
        var cost = PaysLife(card) ? ManaCost.Zero : CastingCost(card).WithX(0);
        if (!timingOk && card.Definition.FlashExtraCost is { } flashExtra)
        {
            timingOk = true; // "as though it had flash if you pay {2} more"
            cost = cost.Plus(flashExtra);
        }
        return timingOk && HasLegalTargets(CastingTargets(card.Definition), playerId, card.Id)
               && CanPayExtra(playerId, card.Definition.AdditionalCost, card.Id)
               && (card.Zone != Zone.Graveyard || CanPayExtra(playerId, GraveyardCost(card), card.Id))
               && (CanPayFromCostOptions(playerId, card, cost) || AlternativeCostPayable(playerId, card));
    }

    /// <summary>Lands the player may play this turn.</summary>
    private int LandsAllowed(PlayerId playerId) =>
        Config.LandsPerTurn + State.GetPlayer(playerId).ExtraLandsThisTurn
        + State.PermanentsControlledBy(playerId).Count(c => (c.Definition.Replaces & Replacements.AdditionalLandPlay) != 0
                                                            && (c.Definition.AdditionalLandPlayIf is not { } cond || Holds(cond, playerId, c)));

    /// <summary>"Can be cast as though it had flash" from a permanent the caster controls ("the first creature spell you cast each turn").</summary>
    private bool GrantedFlash(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Any(p => p.Abilities.OfType<SpellCostReduction>().Any(r => r.GrantsFlash && ReductionApplies(r, p, card, caster)));

    /// <summary>Whether a "spells you cast cost less" ability of <paramref name="permanent"/> applies to <paramref name="card"/>.</summary>
    private bool ReductionApplies(SpellCostReduction reduction, Card permanent, Card card, PlayerId caster) =>
        Matches(reduction.Spells with { Controller = ControllerFilter.Any }, card, caster, permanent, caster)
        && (!reduction.FirstOfTurn || !State.GetPlayer(caster).SpellsCastThisTurn.Select(State.GetCard)
            .Any(c => c.Id != card.Id && Matches(reduction.Spells with { Controller = ControllerFilter.Any, InHand = null }, c, caster, permanent, caster)));

    /// <summary>Cast from exile by paying life equal to its mana value ("rather than pay its mana cost").</summary>
    private bool PaysLife(Card card) =>
        card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.PayLife);

    /// <summary>Cards castable through permissions: the top of the library, stashed cards, permanents from the graveyard.</summary>
    private IEnumerable<CardId> OtherCastableCards(PlayerId playerId)
    {
        var player = State.GetPlayer(playerId);
        if ((Has(playerId, Replacements.CreaturesFromLibraryTop) || Has(playerId, Replacements.CastCreaturesFromLibraryTop))
            && player.Library.Count > 0 && State.GetCard(player.Library[0]).Is(CardType.Creature))
            yield return player.Library[0];
        bool myTurn = State.ActivePlayer == playerId;
        if (myTurn && Has(playerId, Replacements.PlayStashedCards))
            foreach (var card in State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.Owner != playerId && c.CounterCount(CounterKind.Stash) > 0))
                yield return card.Id;
        if (myTurn && Has(playerId, Replacements.PermanentsFromGraveyard))
            foreach (var id in player.Graveyard)
            {
                var card = State.GetCard(id);
                var types = card.Types & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker);
                if (types != 0 && (types & ~player.GraveyardTypesUsedThisTurn) != 0) yield return id;
            }
        foreach (var id in player.Graveyard)
            if (State.GetCard(id).Definition.GraveyardCastCost is not null) yield return id;
        // Cards that went on an adventure (rule 715.4).
        foreach (var id in player.Exile)
            if (State.GetCard(id).OnAdventure) yield return id;
    }

    /// <summary>The extra cost of casting from the graveyard, if the card has one.</summary>
    private ExtraCost? GraveyardCost(Card card) =>
        State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version) || card.Definition.Flashback is not null
        || State.FlashbackGranted.Any(p => p.Card == card.Id && p.Version == card.Version)
            ? null : card.Definition.GraveyardCastCost;

    /// <summary>Whether some way of paying the mana cost plus one of the additional-cost options works.</summary>
    private bool CanPayFromCostOptions(PlayerId player, Card card, ManaCost cost)
    {
        if (card.Definition.AdditionalCostOptions is not { Count: > 0 } options)
            return cost.Variants().Any(v => ManaPayment.FindPlan(State, player, v, usable: SpellManaUsable(card, null), unitUsable: UnitUsableFor(card, isAbility: false)) is not null);
        return options.Any(o => CanPayExtra(player, o.Extra, card.Id)
                                && cost.Plus(o.Mana ?? ManaCost.Zero).Variants().Any(v => ManaPayment.FindPlan(State, player, v, usable: SpellManaUsable(card, o.Extra), unitUsable: UnitUsableFor(card, isAbility: false)) is not null));
    }

    /// <summary>
    /// Mana sources usable for a spell, keeping back sources that sacrifice themselves for mana (Treasures) when they're
    /// needed for a sacrifice the spell also costs (its additional cost, or <paramref name="option"/>).
    /// </summary>
    private ManaPayment.OptionUsable SpellManaUsable(Card card, ExtraCost? option)
    {
        var usable = UsableFor(card, isAbility: false);
        var reserved = new HashSet<CardId>();
        foreach (var extra in new[] { card.Definition.AdditionalCost, option })
        {
            if (extra?.Sacrifice is not { } filter) continue;
            var candidates = SacrificeCandidates(card.Owner, filter, card.Id);
            var selfSacrificing = candidates.Where(c => c.Definition.SacrificeForMana).ToList();
            if (candidates.Count - selfSacrificing.Count < extra.SacrificeCount) reserved.UnionWith(selfSacrificing.Select(c => c.Id));
        }
        return reserved.Count == 0 ? usable : (source, o) => !reserved.Contains(source.Id) && usable(source, o);
    }

    private bool AlternativeCostPayable(PlayerId player, Card card) =>
        card.Definition.AlternativeCost is { } alt && (alt.If is null || Holds(alt.If, player, card))
        && alt.Cost.Variants().Any(v => ManaPayment.FindPlan(State, player, v, usable: UsableFor(card, isAbility: false), unitUsable: UnitUsableFor(card, isAbility: false)) is not null);

    /// <summary>Exiled cards <paramref name="player"/> may currently play.</summary>
    private IEnumerable<CardId> PlayableExile(PlayerId player) =>
        State.PlayableFromExile.Where(p => p.Player == player && p.UntilTurn >= State.TurnNumber
                                           && State.GetCard(p.Card) is { Zone: Zone.Exile } c && c.Version == p.Version
                                           && (p.While is not { } condition || Holds(condition, player, null)))
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
        if (ability.OnlyOnce && source.ActivatedEver.Contains(index)) return false;
        // "Activated abilities of sources with the chosen name can't be activated" (mana abilities aside).
        if (!IsManaAbility(ability) && State.Battlefield.Select(State.GetCard)
                .Any(c => (c.Definition.Replaces & Replacements.StopsChosenNameAbilities) != 0 && c.ChosenName == source.Name)) return false;
        if (ability.Cost.ReturnSelfToHand && source.Zone != Zone.Battlefield) return false;
        if (ability.Cost.TapGranter && (ability.GrantedBy is not { } granter || State.GetCard(granter) is not { Zone: Zone.Battlefield, Tapped: false })) return false;
        if (ability.ActivationCondition is { } condition && !Holds(condition, player, source)) return false;
        if (ability.Cost.Tap && (source.Tapped || source.IsSummoningSick)) return false;
        if (ability.Cost.RemoveCounters > 0 && source.CounterCount(ability.Cost.RemoveCounterKind) < ability.Cost.RemoveCounters) return false;
        if (!CanPayExtra(player, ability.Cost.Extra, source.Id)) return false;
        if (!HasLegalTargets(ability, player, source.Id)) return false;
        return ActivationCost(source, ability, player, null).WithX(0).Variants().Any(v => ManaPayment.FindPlan(State, player, v, exclude: ability.Cost.Tap ? source.Id : null,
            usable: AbilityManaUsable(source, ability, player), unitUsable: UnitUsableFor(source, isAbility: true)) is not null);
    }

    /// <summary>
    /// Mana sources usable for an ability, keeping back creatures that tap for mana when they're needed for a "tap an untapped
    /// creature you control" cost, and permanents that sacrifice themselves for mana when they're needed for a sacrifice.
    /// </summary>
    private ManaPayment.OptionUsable AbilityManaUsable(Card source, ActivatedAbility ability, PlayerId player)
    {
        var usable = UsableFor(source, isAbility: true);
        var reserved = new HashSet<CardId>();
        if (ability.Cost.Extra is { } extra)
        {
            if (extra.TapCreatures is { } tapFilter)
            {
                var candidates = TapCandidates(player, tapFilter, source.Id);
                var manaSources = candidates.Where(c => c.ManaOptions.Count > 0).ToList();
                if (candidates.Count - manaSources.Count < extra.TapCount) reserved.UnionWith(manaSources.Select(c => c.Id));
            }
            if (extra.Sacrifice is { } sacrifice)
            {
                var candidates = SacrificeCandidates(player, sacrifice, source.Id);
                var selfSacrificing = candidates.Where(c => c.Definition.SacrificeForMana).ToList();
                if (candidates.Count - selfSacrificing.Count < extra.SacrificeCount) reserved.UnionWith(selfSacrificing.Select(c => c.Id));
            }
        }
        return reserved.Count == 0 ? usable : (s, o) => !reserved.Contains(s.Id) && usable(s, o);
    }

    /// <summary>
    /// An activated ability's mana cost after reductions: "costs {1} less for each …", equip discounts of the creature it
    /// targets (before targets are chosen, the best legal one), and a free first equip each turn.
    /// </summary>
    private ManaCost ActivationCost(Card source, ActivatedAbility ability, PlayerId player, IReadOnlyList<ChosenTarget>? targets)
    {
        var cost = ability.Cost.Mana;
        if (ability.CostReductionPer is { } per)
            cost = cost.MinusGeneric(State.Battlefield.Select(State.GetCard).Count(c => Matches(per, c, c.Controller, source, player)));
        if (ability.CostReductionIf is { } reduceIf && Holds(reduceIf, player, source)) cost = cost.MinusGeneric(ability.CostReductionAmount);
        if (!ability.IsEquip) return cost;
        if (State.GetPlayer(player).EquipsThisTurn == 0
            && State.PermanentsControlledBy(player).Any(c => c.Definition.FreeFirstEquipIf is { } free && Holds(free, player, c)))
            return ManaCost.Zero;
        var hosts = targets is not null
            ? targets.Select(t => t.Target.Card).OfType<CardId>()
            : ability.Targets.SelectMany(spec => LegalTargets(spec, player, source.Id)).Select(t => t.Card).OfType<CardId>();
        return cost.MinusGeneric(hosts.Select(id => State.GetCard(id).Definition.EquipDiscount).DefaultIfEmpty(0).Max());
    }

    /// <returns>False if the action was cancelled and nothing happened.</returns>
    private async Task<bool> PerformAsync(PlayerId playerId, PlayerAction action)
    {
        var player = State.GetPlayer(playerId);
        switch (action)
        {
            case PlayLand play:
                player.LandsPlayedThisTurn++;
                if (State.GetCard(play.Card).Zone == Zone.Graveyard) player.GraveyardTypesUsedThisTurn |= CardType.Land;
                MoveCard(play.Card, Zone.Battlefield);
                Emit(new LandPlayed(playerId, play.Card));
                return true;

            case ActivateManaAbility mana:
                TapForMana(player, new ManaTap(mana.Source, mana.Type, mana.Option));
                return true;

            case CastSpell cast:
                return await CastSpellAsync(player, cast.Card, cast.Adventure);

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
    /// <param name="exileAfter">Exiled instead of going anywhere else when it leaves the stack (as with flashback).</param>
    private async Task<bool> CastSpellAsync(Player player, CardId cardId, bool adventure = false, bool exileAfter = false)
    {
        var card = State.GetCard(cardId);
        card.AsAdventure = adventure;
        if (!await CastAsItIsAsync(player, card, exileAfter))
        {
            card.AsAdventure = false;
            return false;
        }
        return true;
    }

    private async Task<bool> CastAsItIsAsync(Player player, Card card, bool exileAfter)
    {
        var cardId = card.Id;
        bool flashback = exileAfter || (card.Zone == Zone.Graveyard
                         && (card.Definition.Flashback is not null || State.FlashbackGranted.Any(p => p.Card == cardId && p.Version == card.Version))
                         && !State.PlayableFromGraveyard.Any(p => p.Card == cardId && p.Version == card.Version));
        bool fromGraveyard = card.Zone == Zone.Graveyard;
        bool paysLife = PaysLife(card);
        if (card.Zone == Zone.Hand) card.CastFromHand = true;
        // A gift is promised (or not) as the spell is cast, to an opponent (rule 702.174a).
        PlayerId? giftTo = null;
        if (card.Definition.Gift is not null && State.OpponentsOf(player.Id).Any()
            && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Promise an opponent a gift ({card.Definition.Gift.Name}) for {card.Name}?", cardId)))
            giftTo = await ChooseOpponentAsync(player.Id, card, "Choose the opponent who gets the gift");
        // Kicker is announced before targets (601.2b): a kicked spell may target differently.
        bool kicked = false;
        if (card.Definition.Kicker is { } announcedKicker && Payable(player.Id, CastingCost(card).WithX(0).Plus(announcedKicker), null))
            kicked = await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Pay kicker {announcedKicker} for {card.Name}?", cardId));
        var ability = CastingTargets(card.Definition);
        if (kicked && ability?.WhenKicked is { } kickedVersion) ability = kickedVersion;
        if (ability is not null)
        {
            ability = await ChooseModesAsync(player.Id, ability, cardId, canCancel: true);
            if (ability is null) return false;
        }
        // "Costs {N} less if it targets …": when the full cost can't be paid, only targets that make it affordable can be chosen.
        Func<Abilities.Target, bool>? affordable = ability is { Targets.Count: 1 } && card.Definition.SelfCostReduction?.IfTargets is not null && !paysLife
            ? t => Payable(player.Id, CastingCost(card, new[] { new ChosenTarget(t, VersionOf(t)) }).WithX(0), null, SpellManaUsable(card, null))
            : null;
        var targets = await ChooseTargetsAsync(player.Id, ability, cardId, card.Name, canCancel: true, affordable);
        if (targets is null) return false;

        var cost = CastingCost(card, targets);
        // "Without paying its mana cost" with X: X is 0, so the player may prefer to pay (Omniscience).
        if (card.Zone == Zone.Hand && cost.XCount > 0 && Has(player.Id, Replacements.CastFromHandFree)
            && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Cast {card.Name} without paying its mana cost (X = 0)?", cardId)))
            cost = ManaCost.Zero;
        bool sorceryTiming = player.Id == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0;
        if (!sorceryTiming && !card.Is(CardType.Instant) && !card.Definition.KeywordAbilities.Contains(Keyword.Flash)
            && !Has(player.Id, Replacements.YourSpellsHaveFlash) && card.Definition.FlashExtraCost is { } flashExtra)
            cost = cost.Plus(flashExtra);
        // Alternative cost ("pay {B} rather than this spell's mana cost").
        if (card.Definition.AlternativeCost is { } alt && AlternativeCostPayable(player.Id, card)
            && (!Payable(player.Id, cost.WithX(0), null)
                || await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Pay {alt.Cost} instead of the mana cost?", cardId))))
            cost = alt.Cost;
        // Additional cost with alternatives: the caster picks one that can be paid.
        CostOption? option = null;
        if (card.Definition.AdditionalCostOptions is { Count: > 0 } options)
        {
            var payable = options.Where(o => CanPayExtra(player.Id, o.Extra, cardId) && Payable(player.Id, cost.WithX(0).Plus(o.Mana ?? ManaCost.Zero), null, SpellManaUsable(card, o.Extra))).ToList();
            if (payable.Count == 0) return false;
            int pick = 0;
            if (payable.Count > 1)
            {
                var labels = payable.Select(o => o.Extra is { } e ? DescribeCost(e) : $"Pay {o.Mana}").ToList();
                pick = await ControllerOf(player.Id).ChooseOptionAsync(ViewFor(player.Id), new OptionRequest($"{card.Name}: choose the additional cost", cardId, labels, OptionKind.Other));
                Require(pick >= 0 && pick < payable.Count, "Choose one of the costs.");
            }
            option = payable[pick];
            if (option.Mana is { } extraMana) cost = cost.Plus(extraMana);
        }
        int x = 0;
        if (cost.XCount > 0)
        {
            int max = MaxAffordableX(player.Id, cost, null);
            x = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{card.Name}: choose X", cardId, 0, max));
            Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
        }
        cost = cost.WithX(x);

        if (kicked && card.Definition.Kicker is { } kicker) cost = cost.Plus(kicker);

        // Ward: targeting an opponent's warded permanent costs extra; unpaid, the spell is countered (702.21).

        if (paysLife) cost = ManaCost.Zero;
        var paidMana = await PayManaTapsAsync(player, cardId, cost, exclude: null, SpellManaUsable(card, option?.Extra), UnitUsableFor(card, isAbility: false));
        if (paidMana is null) return false;
        if (paysLife) ChangeLife(player.Id, -card.Definition.ManaCost.ManaValue);
        bool treasure = paidMana.Taps.Any(t => State.GetCard(t.Source).HasSubtype("Treasure")) || paidMana.SpecialSpent.Any(u => State.GetCard(u.Source).HasSubtype("Treasure"));
        await PayExtraAsync(player.Id, card.Definition.AdditionalCost, cardId);
        if (option?.Extra is { } chosenExtra) await PayExtraAsync(player.Id, chosenExtra, cardId);
        if (card.Zone == Zone.Graveyard && GraveyardCost(card) is { } graveyardCost) await PayExtraAsync(player.Id, graveyardCost, cardId);
        if (card.Zone == Zone.Graveyard && Has(player.Id, Replacements.PermanentsFromGraveyard) && GraveyardCost(card) is null
            && card.Definition.Flashback is null && !State.PlayableFromGraveyard.Any(p => p.Card == cardId && p.Version == card.Version))
            player.GraveyardTypesUsedThisTurn |= await ChoosePermanentTypeAsync(player, card);
        // Mana riders: haste for Dragon creature spells, copies of red instants and sorceries.
        var riders = paidMana.SpecialSpent.Select(u => u.Rider).ToList();
        if (riders.Contains(ManaRider.HasteForDragonCreatureSpells) && card.Is(CardType.Creature) && card.HasSubtype("Dragon")) card.HasteOnEnter = true;
        bool uncounterable = riders.Contains(ManaRider.LegendaryUncounterable) && (card.Definition.Supertypes & Supertype.Legendary) != 0;
        // "When that mana is spent to cast a red instant or sorcery spell, copy that spell": one trigger of the mana's
        // source for each such mana spent, put on the stack above the spell.
        var copySources = (card.Is(CardType.Instant) || card.Is(CardType.Sorcery)) && card.Colors.Contains("R")
            ? paidMana.SpecialSpent.Where(u => u.Rider == ManaRider.CopyRedInstantOrSorcery).Select(u => u.Source).ToList()
            : new List<CardId>();

        if (card.Zone == Zone.Command) player.CommanderCasts[cardId] = player.CommanderCasts.GetValueOrDefault(cardId) + 1;
        bool fromHand = card.CastFromHand, haste = card.HasteOnEnter, asAdventure = card.AsAdventure;
        MoveCard(cardId, Zone.Stack);
        card.AsAdventure = asAdventure;
        card.CastFromGraveyard = fromGraveyard;
        card.ManaSpent = cost.ManaValue;
        card.PaidWithTreasure = treasure;
        card.GiftPromised = giftTo is not null;
        card.Uncounterable = uncounterable;
        card.Kicked = kicked;
        card.CastFromHand = fromHand;
        card.WasCast = true;
        card.HasteOnEnter = haste;
        player.SpellsCastThisTurn.Add(cardId);
        PushStack(new SpellOnStack(cardId, player.Id, targets)
        {
            Ability = ability != CastingTargets(card.Definition) ? ability : null,
            X = x, Kicked = kicked, Flashback = flashback, GiftTo = giftTo,
        });
        Emit(new SpellCast(player.Id, cardId));
        for (int i = 0; i < card.Definition.Cascade; i++) // cascade triggers as the spell is cast (rule 702.85a)
            _pendingTriggers.Add(new PendingTrigger(cardId, CascadeTrigger, player.Id, new TriggerInfo(cardId, card.Version, player.Id, card.Definition.ManaCost.ManaValue)));
        foreach (var source in copySources) QueueCopyThatSpell(source, player.Id, card);
        return true;
    }

    /// <summary>Which permanent type a card cast from the graveyard uses up ("a permanent spell of each permanent type").</summary>
    private async Task<CardType> ChoosePermanentTypeAsync(Player player, Card card)
    {
        var unused = new[] { CardType.Creature, CardType.Artifact, CardType.Enchantment, CardType.Planeswalker }
            .Where(t => card.Is(t) && (player.GraveyardTypesUsedThisTurn & t) == 0).ToList();
        if (unused.Count <= 1) return unused.FirstOrDefault();
        int i = await ControllerOf(player.Id).ChooseOptionAsync(ViewFor(player.Id),
            new OptionRequest($"Which permanent type does {card.Name} use?", card.Id, unused.Select(t => t.ToString()).ToList(), OptionKind.Other));
        Require(i >= 0 && i < unused.Count, "Choose one of the types.");
        return unused[i];
    }

    /// <summary>Activating (rule 602.2): choose modes and targets, pay every cost, put the ability on the stack.</summary>
    private async Task<bool> ActivateAbilityAsync(Player player, ActivateAbility action)
    {
        var source = State.GetCard(action.Source);
        var ability = await ChooseModesAsync(player.Id, (ActivatedAbility)source.Abilities[action.Index], source.Id, canCancel: true);
        if (ability is null) return false;
        var exclude = ability.Cost.Tap ? source.Id : (CardId?)null;
        // Equip discounts depend on the creature targeted: only creatures it can be paid for can be chosen.
        Func<Abilities.Target, bool>? affordable = ability is { IsEquip: true, Targets.Count: 1 }
            ? t => Payable(player.Id, ActivationCost(source, ability, player.Id, new[] { new ChosenTarget(t, VersionOf(t)) }).WithX(0), exclude,
                UsableFor(source, isAbility: true))
            : null;
        var targets = await ChooseTargetsAsync(player.Id, ability, source.Id, ability.Text, canCancel: true, affordable);
        if (targets is null) return false;

        var cost = ActivationCost(source, ability, player.Id, targets);
        int x = 0;
        if (cost.XCount > 0)
        {
            int max = MaxAffordableX(player.Id, cost, exclude);
            x = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{source.Name}: choose X", source.Id, 0, max));
            Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
        }
        cost = cost.WithX(x);

        if (await PayManaTapsAsync(player, source.Id, cost, exclude, AbilityManaUsable(source, ability, player.Id), UnitUsableFor(source, isAbility: true)) is null) return false;
        if (ability.Cost.Tap)
        {
            source.Tapped = true;
            Emit(new PermanentTapped(source.Id));
        }
        if (ability.Cost.RemoveCounters > 0)
            source.Counters[ability.Cost.RemoveCounterKind] = source.CounterCount(ability.Cost.RemoveCounterKind) - ability.Cost.RemoveCounters;
        if (ability.Cost.ExileSelf) MoveCard(source.Id, Zone.Exile);
        if (ability.Cost.FromHand) DiscardCard(player.Id, source.Id, null); // cycling: discard this card
        if (ability.IsEquip) player.EquipsThisTurn++;
        var sacrificed = await PayExtraAsync(player.Id, ability.Cost.Extra, source.Id);
        if (ability.OncePerTurn) source.ActivatedThisTurn.Add(action.Index);
        if (ability.OnlyOnce) source.ActivatedEver.Add(action.Index);
        if (ability.Cost.AddCounters > 0) PutCounters(source, ability.Cost.AddCounterKind, ability.Cost.AddCounters, player.Id);
        if (ability.Cost.ReturnSelfToHand) MoveCard(source.Id, Zone.Hand);
        if (ability.Cost.TapGranter && ability.GrantedBy is { } granter)
        {
            State.GetCard(granter).Tapped = true;
            Emit(new PermanentTapped(granter));
        }
        if (ability.Cost.Loyalty is { } loyalty)
        {
            source.LoyaltyActivatedThisTurn = true;
            if (loyalty > 0) PutCounters(source, CounterKind.Loyalty, loyalty, player.Id);
            else if (loyalty < 0) source.Counters[CounterKind.Loyalty] = source.CounterCount(CounterKind.Loyalty) + loyalty;
        }
        if (ability.Cost.SacrificeSelf)
        {
            if (source.Zone == Zone.Graveyard) MoveCard(source.Id, Zone.Exile); // "Exile this card from your graveyard"
            else SacrificePermanent(source.Id);
        }

        Emit(new AbilityActivated(player.Id, source.Id, ability.Text));
        var item = new AbilityOnStack(source.Id, ability, player.Id, targets) { X = x, SacrificedForCost = sacrificed, SourceVersion = source.Version };
        if (IsManaAbility(ability))
        {
            await ApplyResolutionAsync(item, ability, source); // mana abilities don't use the stack (rule 605.3b)
            return true;
        }
        PushStack(item);
        return true;
    }

    /// <summary>An activated ability that only adds mana and has no targets (rule 605.1a).</summary>
    private static bool IsManaAbility(ActivatedAbility ability) =>
        ability.Targets.Count == 0 && ability.Cost.Loyalty is null && ability.Effects.Count > 0 && ability.Effects.All(e => e is AddMana or AddManaOfAnyColor);

    private bool Payable(PlayerId player, ManaCost cost, CardId? exclude, ManaPayment.OptionUsable? usable = null) =>
        cost.Variants().Any(v => ManaPayment.FindPlan(State, player, v, exclude, usable) is not null);

    /// <summary>Which restricted mana in the pool can pay for a spell (or an ability of <paramref name="use"/>).</summary>
    private Func<ManaUnit, bool> UnitUsableFor(Card? use, bool isAbility) => unit =>
        unit.OnlyFor is not { } only
        || (use is not null && (!isAbility || unit.AbilitiesToo) && Matches(only with { Controller = ControllerFilter.Any }, use, use.Controller, null, use.Controller));

    /// <summary>Which mana abilities can pay for a spell (or an ability of <paramref name="use"/>): restricted mana only for what it allows.</summary>
    private ManaPayment.OptionUsable UsableFor(Card? use, bool isAbility) => (source, option) =>
        option.OnlyFor is not { } only
        || (use is not null && (!isAbility || option.AbilitiesToo)
            && Matches(only with { Controller = ControllerFilter.Any }, use, use.Controller, source, source.Controller));

    private int MaxAffordableX(PlayerId player, ManaCost cost, CardId? exclude)
    {
        int x = 0;
        while (x < 99 && Payable(player, cost.WithX(x + 1), exclude)) x++;
        return x;
    }

    /// <summary>Whether discard / sacrifice / life costs can be paid (the source itself can't pay them).</summary>
    private bool CanPayExtra(PlayerId playerId, ExtraCost? extra, CardId source)
    {
        if (extra is null) return true;
        var player = State.GetPlayer(playerId);
        if (player.Hand.Count(id => id != source && DiscardableFor(extra, id, playerId, source)) < extra.Discard) return false;
        if (extra.PayLife > player.Life) return false;
        if (extra.Sacrifice is { } filter && SacrificeCandidates(playerId, filter, source).Count < extra.SacrificeCount) return false;
        if (extra.TapCreatures is { } tapFilter && TapCandidates(playerId, tapFilter, source).Count < extra.TapCount) return false;
        if (extra.CrewPower > 0 && TapCandidates(playerId, new ObjectFilter(CardType.Creature, Other: true), source).Sum(c => c.Power) < extra.CrewPower) return false;
        if (extra.RemoveCountersFromYourCreatures > 0
            && State.PermanentsControlledBy(playerId).Where(c => c.IsCreature).Sum(c => c.Counters.Values.Sum()) < extra.RemoveCountersFromYourCreatures) return false;
        return true;
    }

    /// <summary>Whether a card in hand can be discarded to pay the cost ("discard a legendary card with the same name as …").</summary>
    private bool DiscardableFor(ExtraCost extra, CardId card, PlayerId player, CardId source) =>
        extra.DiscardFilter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, State.GetCard(card), player, State.GetCard(source), player);

    private List<Card> TapCandidates(PlayerId player, ObjectFilter filter, CardId source)
    {
        var sourceCard = State.GetCard(source);
        return State.PermanentsControlledBy(player)
            .Where(c => !c.Tapped && Matches(filter with { Controller = ControllerFilter.Any }, c, player, sourceCard, player)).ToList();
    }

    private List<Card> SacrificeCandidates(PlayerId player, ObjectFilter filter, CardId source)
    {
        var sourceCard = State.GetCard(source);
        var any = filter with { Controller = ControllerFilter.Any };
        return State.PermanentsControlledBy(player).Where(c => Matches(any, c, player, sourceCard, player)).ToList();
    }

    private async Task<IReadOnlyList<CardId>> PayExtraAsync(PlayerId playerId, ExtraCost? extra, CardId source, PlayerId? causedBy = null)
    {
        if (extra is null) return Array.Empty<CardId>();
        var player = State.GetPlayer(playerId);
        if (extra.PayLife > 0) ChangeLife(playerId, -extra.PayLife);
        if (extra.TapCreatures is { } tapFilter || extra.CrewPower > 0)
        {
            var candidates = TapCandidates(playerId, extra.TapCreatures ?? new ObjectFilter(CardType.Creature, Other: true), source);
            int min = extra.CrewPower > 0 ? 1 : extra.TapCount;
            var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, playerId)).ToList();
            var prompt = extra.CrewPower > 0 ? $"Crew {extra.CrewPower}: tap creatures with total power {extra.CrewPower} or more" : $"Tap {extra.TapCount} to pay the cost";
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest(prompt, source, options, min, extra.CrewPower > 0 ? candidates.Count : extra.TapCount, CardChoicePurpose.Sacrifice));
            Require(chosen.All(id => candidates.Any(c => c.Id == id)) && chosen.Distinct().Count() == chosen.Count, "Tap among the listed creatures.");
            Require(extra.CrewPower > 0 ? chosen.Sum(id => State.GetCard(id).Power) >= extra.CrewPower : chosen.Count == extra.TapCount, "Not enough to pay the cost.");
            foreach (var id in chosen)
            {
                State.GetCard(id).Tapped = true;
                Emit(new PermanentTapped(id));
            }
        }
        if (extra.RemoveCountersFromYourCreatures > 0)
        {
            // The player picks each counter to remove (a creature, then the kind when it has several).
            for (int left = extra.RemoveCountersFromYourCreatures; left > 0; left--)
            {
                var holders = State.PermanentsControlledBy(playerId).Where(c => c.IsCreature && c.Counters.Values.Any(v => v > 0)).ToList();
                var options = holders.Select(c => ViewBuilder.Card(State, c.Id, playerId)).ToList();
                var pick = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                    new CardChoiceRequest($"Remove a counter ({left} left)", source, options, 1, 1, CardChoicePurpose.Sacrifice));
                Require(pick.Count == 1 && holders.Any(c => c.Id == pick[0]), "Choose a creature with counters.");
                var creature = State.GetCard(pick[0]);
                var kinds = creature.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                var kind = kinds[0];
                if (kinds.Count > 1)
                {
                    int k = await ControllerOf(playerId).ChooseOptionAsync(ViewFor(playerId),
                        new OptionRequest($"Which counter from {creature.Name}?", creature.Id, kinds.Select(x => x.ToString()).ToList(), OptionKind.Other));
                    Require(k >= 0 && k < kinds.Count, "Choose a counter kind.");
                    kind = kinds[k];
                }
                creature.Counters[kind]--;
            }
        }
        if (extra.Discard > 0)
        {
            var hand = player.Hand.Where(id => id != source && DiscardableFor(extra, id, playerId, source)).ToList();
            var options = hand.Select(id => ViewBuilder.Card(State, id, playerId)).ToList();
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest($"Discard {extra.Discard} to pay the cost", source, options, extra.Discard, extra.Discard, CardChoicePurpose.Discard));
            Require(chosen.Count == extra.Discard && chosen.Distinct().Count() == chosen.Count && chosen.All(hand.Contains), "Discard from your hand.");
            foreach (var id in chosen) DiscardCard(playerId, id, causedBy);
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
            return chosen;
        }
        return Array.Empty<CardId>();
    }

    /// <summary>Asks the player how to pay a mana cost (floating mana first). Returns false if they cancel.</summary>
    private async Task<bool> PayManaAsync(Player player, CardId source, ManaCost cost, CardId? exclude) =>
        await PayManaTapsAsync(player, source, cost, exclude, null, null) is not null;

    /// <summary>What paid a mana cost: sources tapped and restricted / rider mana spent.</summary>
    private sealed record ManaPaid(IReadOnlyList<ManaTap> Taps, IReadOnlyList<ManaUnit> SpecialSpent);

    /// <summary>Like <see cref="PayManaAsync"/>, returning what paid (null if cancelled).</summary>
    private async Task<ManaPaid?> PayManaTapsAsync(Player player, CardId source, ManaCost cost, CardId? exclude, ManaPayment.OptionUsable? usable,
        Func<ManaUnit, bool>? unitUsable)
    {
        if (cost.ManaValue == 0) return new ManaPaid(Array.Empty<ManaTap>(), Array.Empty<ManaUnit>());
        // Hybrid symbols: pay the first way that works (the payment dialog then works on a concrete cost).
        cost = cost.Variants().FirstOrDefault(v => ManaPayment.FindPlan(State, player.Id, v, exclude, usable, unitUsable) is not null)
               ?? throw new InvalidOperationException($"Legal action became unpayable: {State.GetCard(source).Name} costing {cost}.");
        var plan = ManaPayment.FindPlan(State, player.Id, cost, exclude, usable, unitUsable)!;
        var (fromPool, remaining) = ManaPayment.ApplyPool(cost, player.ManaPool, unitUsable);
        // One entry per usable mana ability, bigger ones first (a click picks the first that helps).
        var sources = ManaPayment.AvailableSources(State, player.Id, exclude, usable)
            .SelectMany(c => ManaPayment.UsableOptions(c, usable).Select(i => new ManaSourceOption(c.Id, c.ManaOptions[i].Types, c.ManaOptions[i].Amount, i))
                .OrderByDescending(o => o.Amount))
            .ToList();
        var request = new ManaPaymentRequest(source, cost, fromPool, remaining, plan.Taps, sources);

        var taps = await ControllerOf(player.Id).ChooseManaPaymentAsync(ViewFor(player.Id), request);
        if (taps is null) return null;

        Require(taps.Select(t => t.Source).Distinct().Count() == taps.Count, "Each source can be tapped only once.");
        Require(taps.All(t => sources.Any(s => s.Source == t.Source && s.Option == t.Option && s.Types.Contains(t.Type))), "Illegal mana source.");
        var produced = ManaPayment.Produced(State, taps).ToList();
        var (owed, excess) = ManaPayment.Apply(remaining, produced);
        Require(owed.ManaValue == 0, $"Payment is short by {owed}.");
        // No pointless taps: surplus is only allowed when a source adds more mana than is still needed.
        Require(excess == 0 || taps.Any(t => ManaPayment.AmountOf(State.GetCard(t.Source), t.Option) > 1), "Payment taps more mana than the cost.");

        foreach (var tap in taps) TapForMana(player, tap);
        // Pay the whole cost from the pool; any surplus keeps floating (rule 106.4).
        var plainUsed = new List<ManaType>();
        var specialUsed = new List<ManaUnit>();
        var (_, left) = ManaPayment.ApplyPool(cost, player.ManaPool, unitUsable, plainUsed, specialUsed);
        Require(left.ManaValue == 0, "Payment is short.");
        foreach (var type in plainUsed) player.ManaPool.Remove(type);
        foreach (var unit in specialUsed) player.ManaPool.RemoveSpecial(unit);
        return new ManaPaid(taps, specialUsed);
    }

    private void TapForMana(Player player, ManaTap tap)
    {
        var source = State.GetCard(tap.Source);
        source.Tapped = true;
        Emit(new PermanentTapped(tap.Source));
        var option = tap.Option < source.ManaOptions.Count ? source.ManaOptions[tap.Option] : null;
        // Restricted mana remembers what it may pay for; the source's "chosen" type or color is fixed now.
        var onlyFor = option?.OnlyFor is { } only
            ? only with
            {
                Subtype = only.ChosenType ? source.ChosenType : only.Subtype, ChosenType = false,
                Colors = only.ChosenColor && source.ChosenColor is { } chosen ? new[] { chosen } : only.Colors, ChosenColor = false,
            }
            : null;
        foreach (var type in ManaPayment.Produced(source, tap).ToList())
        {
            if (onlyFor is not null || source.Definition.ManaRider != ManaRider.None)
                player.ManaPool.AddSpecial(new ManaUnit(type, source.Id, onlyFor, option?.AbilitiesToo ?? false, source.Definition.ManaRider));
            else player.ManaPool.Add(type);
            Emit(new ManaAdded(player.Id, type, tap.Source));
        }
        if (source.Definition.SacrificeForMana) SacrificePermanent(source.Id);
    }

    /// <summary>
    /// Total cost to cast a card (601.2f): its mana cost (or flashback cost from the graveyard), plus commander tax
    /// when cast from the command zone (903.8), minus cost reductions. X stays unresolved.
    /// </summary>
    public ManaCost CastingCost(Card card) => CastingCost(card, null);

    private ManaCost CastingCost(Card card, IReadOnlyList<ChosenTarget>? targets)
    {
        if ((card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.WithoutPaying)) || _castFree.Contains(card.Id))
            return ManaCost.Zero; // "without paying its mana cost"
        var cost = card.Zone == Zone.Graveyard && card.Definition.Flashback is { } flashback
                   && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version)
            ? flashback : card.Definition.ManaCost;
        if (card.Zone == Zone.Command && Config.Commander is { } rules)
            cost = cost.PlusGeneric(rules.TaxPerCast * State.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id));
        var caster = card.Zone == Zone.Exile && card.Owner != State.ActivePlayer ? State.ActivePlayer : card.Owner;
        if (card.Zone == Zone.Hand && Has(card.Owner, Replacements.CastFromHandFree) && card.Definition.ManaCost.XCount == 0) return ManaCost.Zero;
        // "Mana of any type can be spent": colored symbols become generic.
        bool anyType = (card.Is(CardType.Creature) && Has(card.Owner, Replacements.CreaturesFromLibraryTop)) // "spend mana of any type to cast creature spells"
                       || (card.Zone == Zone.Exile && card.CounterCount(CounterKind.Stash) > 0 && Has(caster, Replacements.PlayStashedCards))
                       || (card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.AnyManaType));
        if (anyType) cost = new ManaCost(cost.ManaValue, Array.Empty<ManaType>(), null, cost.XCount);
        return cost.MinusGeneric(CostReductionFor(card, targets));
    }

    /// <summary>Cards being cast "without paying their mana cost" from the hand right now.</summary>
    private readonly HashSet<CardId> _castFree = new();

    private int CostReductionFor(Card card, IReadOnlyList<ChosenTarget>? targets = null)
    {
        var caster = card.Owner;
        int total = 0;
        if (card.Definition.SelfCostReduction is { AmountFrom: { } amountFrom })
            total += Math.Max(0, Eval(amountFrom, new EffectContext(caster, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>())));
        if (card.Definition.SelfCostReduction is { IfTargets: { } wanted } byTarget)
        {
            // Before targets are chosen, assume the best case when some legal target qualifies.
            var candidates = targets?.Where(t => t.Target.Card is not null).Select(t => State.GetCard(t.Target.Card!.Value))
                             ?? (CastingTargets(card.Definition)?.Targets.SelectMany(spec => LegalTargets(spec, caster, card.Id))
                                 .Where(t => t.Card is not null).Select(t => State.GetCard(t.Card!.Value)) ?? Enumerable.Empty<Card>());
            if (candidates.Any(c => Matches(wanted with { Controller = ControllerFilter.Any }, c, c.Controller, card, caster))) total += byTarget.Amount;
        }
        else if (card.Definition.SelfCostReduction is { } self)
        {
            if (self.PerPermanent is { } perPermanent)
                total += self.Amount * State.Battlefield.Select(State.GetCard).Count(c => Matches(perPermanent, c, c.Controller, card, caster));
            else if (self.PerGraveyardCard is { } perCard)
                total += self.Amount * State.GetPlayer(caster).Graveyard.Select(State.GetCard)
                    .Count(c => Matches(perCard with { Controller = ControllerFilter.Any }, c, caster, card, caster));
            else if (self.ByTotalPower)
                total += self.Amount * State.PermanentsControlledBy(caster)
                    .Where(c => c.IsCreature && (self.PowerFilter is not { } pf || Matches(pf, c, c.Controller, card, caster))).Sum(c => Math.Max(0, c.Power));
            else if (self.Condition is null || Holds(self.Condition, caster, card))
                total += self.Amount;
        }
        foreach (var permanent in State.PermanentsControlledBy(caster))
            foreach (var reduction in permanent.Abilities.OfType<SpellCostReduction>())
                if (ReductionApplies(reduction, permanent, card, caster))
                    total += reduction.AmountFrom is { } from
                        ? Math.Max(0, Eval(from, new EffectContext(caster, permanent, Array.Empty<ChosenTarget>(), Array.Empty<bool>())))
                        : reduction.Amount;
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
                if ((spell.Ability ?? card.Definition.Spell) is SpellAbility { ExileAfterResolving: true }) discard = Zone.Exile;
                if ((spell.Ability ?? CastingTargets(card.Definition)) is { } effect && !await ApplyResolutionAsync(item, effect, card))
                {
                    MoveCard(spell.Card, discard);
                    Emit(new FizzledOnResolution(spell.Card));
                    break;
                }
                if (card.Zone != Zone.Stack) break; // the spell moved itself (shuffled away, exiled...)
                if (card.AsAdventure && !card.Definition.IsToken)
                {
                    // A resolved Adventure goes on an adventure: exiled, castable from there later (rule 715.4).
                    MoveCard(spell.Card, Zone.Exile);
                    if (card.Zone == Zone.Exile) card.OnAdventure = true;
                    Emit(new SpellResolved(spell.Card));
                    break;
                }
                bool hasteOnEnter = card.HasteOnEnter;
                // An Aura spell enters attached to the object it targeted (rule 303.4f).
                var attachTo = card.Definition.EnchantTarget is not null ? item.Targets[0].Target.Card : null;
                MoveCard(spell.Card, card.Types.IsPermanent() ? Zone.Battlefield : discard, controller: spell.Controller, attachTo: attachTo,
                    kicked: spell.Kicked && card.Types.IsPermanent(), castFromHand: card.CastFromHand && card.Types.IsPermanent(),
                    wasCast: card.Types.IsPermanent() && !card.Definition.IsToken);
                if (card.Zone == Zone.Battlefield && hasteOnEnter)
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                if (card.Zone == Zone.Battlefield && card.Definition.EntersWithXCounters && spell.X > 0)
                    PutCounters(card, CounterKind.PlusOnePlusOne, spell.X, spell.Controller);
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
