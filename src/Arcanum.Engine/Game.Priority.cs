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
    private static readonly string[] BasicLandTypes = { "Plains", "Island", "Swamp", "Mountain", "Forest" };

    /// <summary>"As this enters, choose a color / creature type" (rule 614.12), made before anything else happens.</summary>
    private async Task ResolveEnterChoicesAsync()
    {
        await EnterDeferredAsync();
        await ResolvePendingCountersAsync();
        while (State.PendingEnterChoices.Count > 0)
        {
            var (id, version) = State.PendingEnterChoices[0];
            State.PendingEnterChoices.RemoveAt(0);
            var card = State.GetCard(id);
            if (card.Version != version || card.Zone != Zone.Battlefield) continue;
            var who = card.Controller;
            if (card.Definition.Devour > 0)
            {
                // Devour: "As this enters, you may sacrifice any number of [creatures]. It enters with N +1/+1 counters for each."
                var filter = (card.Definition.DevourFilter ?? new ObjectFilter(CardType.Creature)) with { Controller = ControllerFilter.Any };
                var food = State.PermanentsControlledBy(who).Where(c => c.Id != id && Matches(filter, c, who, card, who)).ToList();
                IReadOnlyList<CardId> eaten = Array.Empty<CardId>();
                if (food.Count > 0)
                {
                    eaten = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Devour: sacrifice any number for {card.Name}", id,
                        food.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, food.Count, CardChoicePurpose.Sacrifice));
                    Require(eaten.Distinct().Count() == eaten.Count && eaten.All(e => food.Any(c => c.Id == e)), "Sacrifice among the listed permanents.");
                }
                BeginSimultaneous();
                foreach (var e in eaten) await SacrificePermanentAsync(e);
                EndSimultaneous();
                if (eaten.Count > 0) PutCounters(card, CounterKind.PlusOnePlusOne, eaten.Count * card.Definition.Devour, who);
                await ResolvePendingCountersAsync();
                if (card.Definition.ChooseOnEnter == EnterChoice.None) continue;
            }
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
                else EnterTapped(card);
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.RevealOrTapped)
            {
                // "As this land enters, you may reveal a [kind] card from your hand. If you don't, this land enters tapped."
                var filter = (card.Definition.EnterRevealFilter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any };
                var eligible = State.GetPlayer(who).Hand.Where(h => Matches(filter, State.GetCard(h), who, card, who)).ToList();
                IReadOnlyList<CardId> shown = Array.Empty<CardId>();
                if (eligible.Count > 0)
                {
                    shown = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{card.Name}: reveal a card from your hand so it enters untapped (or none)", id,
                        eligible.Select(h => ViewBuilder.Card(State, h, who)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    Require(shown.Count <= 1 && shown.All(eligible.Contains), "Reveal one of the listed cards.");
                }
                if (shown.Count == 1) Emit(new CardsRevealed(who, shown.ToList()));
                else EnterTapped(card);
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.BasicLandType)
            {
                // "As this enters, choose a basic land type."
                int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose a basic land type", id, BasicLandTypes, OptionKind.Other));
                Require(i >= 0 && i < BasicLandTypes.Length, "Choose one of the basic land types.");
                card.ChosenType = BasicLandTypes[i];
                Emit(new ChoiceMade(id, BasicLandTypes[i]));
                RecomputeContinuousEffects();
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.Creature)
            {
                // "As this Aura enters, choose a creature": any creature on the battlefield; the choice is remembered, not a target.
                var creatures = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature).ToList();
                if (creatures.Count == 0) continue;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{card.Name}: choose a creature", id,
                    creatures.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(pick.Count == 1 && creatures.Any(c => c.Id == pick[0]), "Choose one of the creatures.");
                card.ChosenCreature = (pick[0], State.GetCard(pick[0]).Version);
                Emit(new ChoiceMade(id, State.GetCard(pick[0]).Name));
                RecomputeContinuousEffects();
                continue;
            }
            else if (card.Definition.ChooseOnEnter == EnterChoice.CounterOnPermanent)
            {
                // "As this enters, put a [kind] counter on a [permanent] you control": chosen (not targeted) as it enters.
                var filter = (card.Definition.EnterCounterOn ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any };
                var options = State.PermanentsControlledBy(who).Where(c => c.Id != id && Matches(filter, c, who, card, who)).ToList();
                if (options.Count == 0) continue;
                var pick = options.Count == 1 ? new[] { options[0].Id } : (await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                    $"{card.Name}: choose the permanent to put a counter on", id, options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 1, 1, CardChoicePurpose.Keep))).ToArray();
                Require(pick.Length == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the permanents.");
                Emit(new ChoiceMade(id, State.GetCard(pick[0]).Name));
                PutCounters(State.GetCard(pick[0]), card.Definition.EnterCounterKind, 1, who);
                await ResolvePendingCountersAsync();
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
            else if (card.Definition.ChooseOnEnter is EnterChoice.CardName or EnterChoice.LookAtOpponentsHandThenCardName or EnterChoice.NonbasicLandCardName)
            {
                // "Look at an opponent's hand, then choose any card name" / "choose a card name".
                var seen = new List<string>();
                if (card.Definition.ChooseOnEnter == EnterChoice.LookAtOpponentsHandThenCardName
                    && await ChooseOpponentAsync(who, card, "Choose the opponent whose hand you look at") is { } opponent)
                {
                    var hand = State.GetPlayer(opponent).Hand;
                    Emit(new HandLookedAt(who, opponent, hand.ToList()));
                    seen.AddRange(hand.Select(c => State.GetCard(c).PrintedDefinition.Name));
                }
                bool nonbasicLand = card.Definition.ChooseOnEnter == EnterChoice.NonbasicLandCardName;
                if (await ChooseCardNameAsync(who, card.Id, $"{card.Name}: choose a {(nonbasicLand ? "nonbasic land " : "")}card name", seen, nonbasicLand) is not { } name) continue;
                card.ChosenName = name;
                Emit(new ChoiceMade(id, name));
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

    /// <summary>
    /// "Choose a card name" (rule 201.3): any card's name. The names offered are those of the game's card database
    /// (<see cref="GameConfig.CardNames"/>) or, without one, every card name the chooser knows of — their own cards and
    /// the cards they can see or have seen — so the list never gives away a hidden card. <paramref name="first"/> come first.
    /// </summary>
    /// <param name="nonbasicLand">"Choose a nonbasic land card name": only names of nonbasic land cards.</param>
    private async Task<string?> ChooseCardNameAsync(PlayerId who, CardId? source, string prompt, IReadOnlyList<string>? first = null, bool nonbasicLand = false)
    {
        var names = CardNameOptions(who, first, nonbasicLand);
        if (names.Count == 0) return null;
        int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest(prompt, source, names, OptionKind.CardName));
        Require(i >= 0 && i < names.Count, "Choose one of the names.");
        return names[i];
    }

    /// <summary>The card names offered to <paramref name="who"/> for "choose a card name".</summary>
    private List<string> CardNameOptions(PlayerId who, IReadOnlyList<string>? first, bool nonbasicLand = false)
    {
        static bool IsNonbasicLand(Cards.CardDefinition face) => face.Is(CardType.Land) && (face.Supertypes & Supertype.Basic) == 0;
        var known = State.Cards.Values.Where(c => !c.PrintedDefinition.IsToken && !c.PrintedDefinition.IsEmblem
                                                  && (c.Owner == who || c.IsVisibleTo(who) || Views.ViewBuilder.RevealedByEffect(State, c, who)))
            // Either face of a double-faced card, but not both together (rule 712.19).
            .SelectMany(c => c.PrintedDefinition.BackFace is { } back ? new[] { c.PrintedDefinition, back } : new[] { c.PrintedDefinition });
        IEnumerable<string> all = nonbasicLand
            ? Config.NonbasicLandNames ?? known.Where(IsNonbasicLand).Select(f => f.Name)
            : Config.CardNames ?? known.Select(f => f.Name);
        if (nonbasicLand && first is not null)
            first = first.Where(n => known.Any(f => f.Name == n && IsNonbasicLand(f))).ToList();
        return (first ?? Array.Empty<string>()).Concat(all.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
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
        // Split second (702.61): only mana abilities while such a spell is on the stack.
        bool splitSecond = State.Stack.OfType<SpellOnStack>().Any(s => State.GetCard(s.Card).Has(Keyword.SplitSecond));
        bool sorcerySpeed = playerId == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0;

        // Cards in hand, commanders in the command zone (rule 903.8), cards with flashback in the graveyard and
        // exiled cards the player may play this turn.
        var castable = player.Hand.Concat(player.Command)
            .Concat(player.Graveyard.Where(id => State.GetCard(id).Definition.Flashback is not null))
            .Concat(PlayableExile(playerId))
            .Concat(State.PlayableFromGraveyard.Concat(State.FlashbackGranted)
                .Where(p => p.Player == playerId && State.GetCard(p.Card) is { Zone: Zone.Graveyard } c && c.Version == p.Version).Select(p => p.Card))
            .Concat(OtherCastableCards(playerId))
            .Concat(State.GraveyardCastRights.Any(r => r.Player == playerId && r.Turn == State.TurnNumber)
                ? player.Graveyard.Where(id => HasGraveyardCastRight(State.GetCard(id))) : Array.Empty<CardId>())
            .Distinct();
        foreach (var card in castable.Select(State.GetCard))
        {
            if (card.Is(CardType.Land))
            {
                if (card.Zone is Zone.Hand or Zone.Exile or Zone.Graveyard or Zone.Library && sorcerySpeed && player.LandsPlayedThisTurn < LandsAllowed(playerId)
                    && !(card.Zone == Zone.Exile && State.PlayableFromExile.Where(p => p.Card == card.Id && p.Version == card.Version && p.Player == playerId).All(p => p.CastOnly)
                         && !card.OnAdventure))
                    actions.Add(new PlayLand(card.Id));
            }
            else if (SpellsForbidden(playerId) || splitSecond) continue;
            else if (card.PrintedDefinition.SplitHalves is { } halves)
            {
                // A split card: each half on its own (an aftermath half only from a graveyard, the other half not from there).
                for (int h = 0; h < halves.Count; h++)
                {
                    if (halves[h].Aftermath != (card.Zone == Zone.Graveyard)) continue;
                    card.CastHalf = h;
                    bool halfCastable = CanCast(card, playerId, sorcerySpeed);
                    card.CastHalf = null;
                    if (halfCastable) actions.Add(new CastSpell(card.Id, Half: h));
                }
            }
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
                    && !splitSecond && CanActivate(permanent, ability, i, playerId, sorcerySpeed))
                    actions.Add(new ActivateAbility(permanent.Id, i));
        }

        // Mana abilities can be activated any time the player has priority (rule 605.3a).
        foreach (var source in ManaPayment.AvailableSources(State, playerId, usable: (_, _) => true))
            foreach (var option in ManaPayment.UsableOptions(source, ManaPayment.Affordable(State, (_, _) => true)))
                // "One mana of each color" is a single activation, not a choice of type.
                // "One mana of each color" and "in any combination" are single activations (the combination is chosen as it's activated).
                foreach (var type in source.ManaOptions[option].OneOfEach || source.ManaOptions[option].Combination ? source.ManaOptions[option].Types.Take(1) : source.ManaOptions[option].Types.Distinct())
                    actions.Add(new ActivateManaAbility(source.Id, type, option));
        return actions;
    }

    /// <summary>
    /// Whether the card (as it is now: the card, or its Adventure) can be cast: timing (or paying extra to cast it as though
    /// it had flash), targets and some way to pay for it.
    /// </summary>
    private bool CanCast(Card card, PlayerId playerId, bool sorcerySpeed)
    {
        bool timing = TimingAllows(card, playerId, sorcerySpeed);
        if (!timing && card.Definition.FlashExtraCost is null) return false;
        return CanBeCast(card, playerId, flashExtra: !timing);
    }

    /// <summary>The player can't cast spells now ("players can't cast spells this turn", "your opponents can't cast spells this turn").</summary>
    private bool SpellsForbidden(PlayerId player) =>
        State.SpellsForbiddenTurn == State.TurnNumber || State.SpellsForbiddenFor.Contains((player, State.TurnNumber));

    /// <summary>Lands the player may play this turn.</summary>
    private int LandsAllowed(PlayerId playerId) =>
        Config.LandsPerTurn + State.GetPlayer(playerId).ExtraLandsThisTurn
        + State.PermanentsControlledBy(playerId).Count(c => (c.Definition.Replaces & Replacements.AdditionalLandPlay) != 0
                                                            && (c.Definition.AdditionalLandPlayIf is not { } cond || Holds(cond, playerId, c)));

    /// <summary>"Can be cast as though it had flash" from a permanent the caster controls ("the first creature spell you cast each turn").</summary>
    private bool GrantedFlash(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Concat(State.Emblems.Select(State.GetCard).Where(e => e.Owner == caster))
            .Any(p => p.Abilities.OfType<SpellCostReduction>().Any(r => r.GrantsFlash && ReductionApplies(r, p, card, caster)));

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
        // "As long as an opponent controls more lands than you, you may play lands from the top of your library."
        if (Has(playerId, Replacements.PlayLandsFromLibraryTopWhileBehind) && player.Library.Count > 0 && State.GetCard(player.Library[0]).Is(CardType.Land)
            && State.OpponentsOf(playerId).Any(o => State.PermanentsControlledBy(o).Count(c => c.Is(CardType.Land)) > State.PermanentsControlledBy(playerId).Count(c => c.Is(CardType.Land))))
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
        // "You may play lands from your graveyard."
        if (Has(playerId, Replacements.LandsFromGraveyard))
            foreach (var id in player.Graveyard.Where(id => State.GetCard(id).Is(CardType.Land))) yield return id;
        foreach (var id in player.Graveyard)
            if (State.GetCard(id).Definition.GraveyardCastCost is not null || State.GetCard(id).PrintedDefinition.SplitHalves?.Any(h => h.Aftermath) == true) yield return id;
        // Cards that went on an adventure (rule 715.4).
        foreach (var id in player.Exile)
            if (State.GetCard(id).OnAdventure) yield return id;
    }

    /// <summary>The extra cost of casting from the graveyard, if the card has one.</summary>
    private ExtraCost? GraveyardCost(Card card) =>
        State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version) || HasGraveyardCastRight(card) || card.Definition.Flashback is not null
        || State.FlashbackGranted.Any(p => p.Card == card.Id && p.Version == card.Version)
            ? null : card.Definition.GraveyardCastCost;

    /// <summary>"This turn, you may cast [filter] spells from your graveyard": whether the card in its owner's graveyard is one of them.</summary>
    private bool HasGraveyardCastRight(Card card) =>
        card.Zone == Zone.Graveyard && !card.Is(CardType.Land)
        && State.GraveyardCastRights.Any(r => r.Player == card.Owner && r.Turn == State.TurnNumber
                                              && Matches(r.Filter with { Controller = ControllerFilter.Any }, card, card.Owner, null, r.Player));

    /// <summary>Exiled cards <paramref name="player"/> may currently play.</summary>
    private IEnumerable<CardId> PlayableExile(PlayerId player) =>
        State.PlayableFromExile.Where(p => p.Player == player && p.UntilTurn >= State.TurnNumber
                                           && State.GetCard(p.Card) is { Zone: Zone.Exile } c && c.Version == p.Version
                                           && (p.While is not { } condition || Holds(condition, player, null)))
            .Select(p => p.Card).Distinct().ToList();

    private bool CanActivate(Card source, ActivatedAbility ability, int index, PlayerId player, bool sorcerySpeed)
    {
        // "Its activated abilities can't be activated" (mana abilities too).
        if (source.Zone == Zone.Battlefield && source.AbilitiesCantBeActivated) return false;
        if (ability.Cost.LoyaltyX && (!sorcerySpeed || source.LoyaltyActivatedThisTurn)) return false; // −X: X can be 0
        if (ability.Cost.Loyalty is { } loyalty)
        {
            // Loyalty abilities: sorcery timing, one per planeswalker per turn, and enough loyalty to pay (606.3).
            if (!sorcerySpeed || source.LoyaltyActivatedThisTurn) return false;
            if (loyalty < 0 && source.CounterCount(CounterKind.Loyalty) < -loyalty) return false;
        }
        // "During your turn, you may activate equip abilities any time you could cast an instant."
        bool instantEquip = ability.IsEquip && State.ActivePlayer == player && Has(player, Replacements.EquipAtInstantSpeedOnYourTurn);
        if (ability.SorcerySpeed && !sorcerySpeed && !instantEquip) return false;
        if (ability.OncePerTurn && source.ActivatedThisTurn.Contains(index)) return false;
        // "Activated abilities of lands your opponents control can't be activated unless they're mana abilities."
        if (source.Is(CardType.Land) && !IsManaAbility(ability) && State.OpponentsOf(source.Controller).Any(o => State.PermanentsControlledBy(o).Any(c => (c.Definition.Replaces & Replacements.AnyManaForItsAbilities) != 0)))
            return false;
        if (ability.OnlyOnce && source.ActivatedEver.Contains(index)) return false;
        // "Activated abilities of sources with the chosen name can't be activated" (mana abilities aside).
        if (!IsManaAbility(ability) && State.Battlefield.Select(State.GetCard)
                .Any(c => (c.Definition.Replaces & Replacements.StopsChosenNameAbilities) != 0 && c.ChosenName == source.Name)) return false;
        if (ability.Cost.ReturnSelfToHand && source.Zone != Zone.Battlefield) return false;
        if (ability.Cost.SacrificeSelf && source.Zone == Zone.Battlefield && !CanBeSacrificedBy(source, player)) return false;
        if (ability.Cost.TapGranter && (ability.GrantedBy is not { } granter || State.GetCard(granter) is not { Zone: Zone.Battlefield, Tapped: false })) return false;
        if (ability.ActivationCondition is { } condition && !Holds(condition, player, source)) return false;
        if (ability.Cost.Tap && (source.Tapped || source.IsSummoningSick)) return false;
        if (ability.Cost.RemoveCounters > 0 && source.CounterCount(ability.Cost.RemoveCounterKind) < ability.Cost.RemoveCounters) return false;
        if (!CanPayExtra(player, ability.Cost.Extra, source.Id)) return false;
        if (ability.Cost.SnowMana > 0 && SnowSourcesFor(player, ability.Cost.Tap ? source.Id : null) < ability.Cost.SnowMana) return false;
        if (!HasLegalTargets(ability, player, source.Id)) return false;
        return ActivationCost(source, ability, player, null).PlusGeneric(ability.Cost.SnowMana).WithX(0).Variants().Any(v => ManaPayment.FindPlan(State, player, v, exclude: ability.Cost.Tap ? source.Id : null,
            usable: AbilityManaUsable(source, ability, player), unitUsable: UnitUsableFor(source, isAbility: true)) is not null);
    }

    /// <summary>"You may pay {0} rather than pay the equip cost of the first equip ability you activate each turn."</summary>
    private bool FreeEquipAvailable(PlayerId player) =>
        State.GetPlayer(player).EquipsThisTurn == 0
        && State.PermanentsControlledBy(player).Any(c => c.Definition.FreeFirstEquipIf is { } free && Holds(free, player, c));

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
    private ManaCost ActivationCost(Card source, ActivatedAbility ability, PlayerId player, IReadOnlyList<ChosenTarget>? targets, bool useFreeEquip = true)
    {
        var cost = ability.Cost.Mana;
        // "Mana of any type can be spent to activate [this]'s abilities": colored symbols can be paid with any mana.
        if ((source.Definition.Replaces & Replacements.AnyManaForItsAbilities) != 0 && cost.Pips.Count > 0)
            cost = new ManaCost(cost.Generic + cost.Pips.Count + cost.Hybrid.Count, Array.Empty<ManaType>(), null, cost.XCount);
        // "Activated abilities of Foods you control cost {1} less", "Equip abilities you activate cost {1} less".
        foreach (var reducer in State.PermanentsControlledBy(player))
            foreach (var r in reducer.Abilities.OfType<AbilityCostReduction>())
                if ((!r.EquipOnly || ability.IsEquip) && Matches(r.Sources with { Controller = ControllerFilter.Any }, source, source.Controller, reducer, player))
                    cost = cost.MinusGeneric(r.Amount);
        if (ability.CostReductionPer is { } per)
            cost = cost.MinusGeneric(State.Battlefield.Select(State.GetCard).Count(c => Matches(per, c, c.Controller, source, player)));
        if (ability.CostReductionIf is { } reduceIf && Holds(reduceIf, player, source)) cost = cost.MinusGeneric(ability.CostReductionAmount);
        if (!ability.IsEquip) return cost;
        if (useFreeEquip && FreeEquipAvailable(player)) return ManaCost.Zero;
        var hosts = targets is not null
            ? targets.Select(t => t.Target.Card).OfType<CardId>()
            : ability.Targets.SelectMany(spec => LegalTargets(spec, player, source.Id)).Select(t => t.Card).OfType<CardId>();
        return cost.MinusGeneric(hosts.Select(id => State.GetCard(id).Definition.EquipDiscount).DefaultIfEmpty(0).Max());
    }

    /// <summary>Times a player backed out of something they were doing (a target, mode or mana payment request answered with nothing).</summary>
    private int _playerCancels;

    private readonly List<string> _failedActions = new();

    /// <summary>
    /// Legal actions that were chosen and then came to nothing without the player backing out: the list of legal actions
    /// offered something that couldn't be done, which is an engine error.
    /// </summary>
    public IReadOnlyList<string> FailedActions => _failedActions;

    private string Describe(PlayerAction action) => action switch
    {
        CastSpell c => $"cast {State.GetCard(c.Card).Name}{(c.Adventure ? " (adventure)" : "")}{(c.Half is { } half ? $" (half {half})" : "")}",
        ActivateAbility a => $"activate {State.GetCard(a.Source).Name} #{a.Index}",
        _ => action.ToString() ?? "",
    };

    /// <returns>False if the action was cancelled and nothing happened.</returns>
    private async Task<bool> PerformAsync(PlayerId playerId, PlayerAction action)
    {
        var player = State.GetPlayer(playerId);
        switch (action)
        {
            case PlayLand play:
                NoteExilePlay(State.GetCard(play.Card), playerId);
                player.LandsPlayedThisTurn++;
                // A land played from the graveyard uses up that type's permission, unless a permission without that limit covers it.
                if (State.GetCard(play.Card).Zone == Zone.Graveyard && !Has(playerId, Replacements.LandsFromGraveyard)) player.GraveyardTypesUsedThisTurn |= CardType.Land;
                await MoveCardAsync(play.Card, Zone.Battlefield);
                Emit(new LandPlayed(playerId, play.Card));
                return true;

            case ActivateManaAbility mana:
            {
                var source = State.GetCard(mana.Source);
                if (mana.Option < source.ManaOptions.Count && source.ManaOptions[mana.Option].Combination)
                {
                    // "Add two mana in any combination of …": the player chooses the mana.
                    var combinations = source.ManaOptions[mana.Option].Combinations().ToList();
                    int pick = await ControllerOf(playerId).ChooseOptionAsync(ViewFor(playerId), new OptionRequest($"{source.Name}: choose the mana to add", source.Id,
                        combinations.Select(c => string.Concat(c.Select(t => $"{{{t.ToSymbol()}}}"))).ToList(), OptionKind.Other));
                    Require(pick >= 0 && pick < combinations.Count, "Choose one of the combinations.");
                    await TapForManaAsync(player, new ManaTap(mana.Source, combinations[pick][0], mana.Option, combinations[pick]));
                    return true;
                }
                await TapForManaAsync(player, new ManaTap(mana.Source, mana.Type, mana.Option));
                return true;
            }

            case CastSpell or ActivateAbility:
            {
                int cancels = _playerCancels;
                bool done = action is CastSpell cast
                    ? await CastSpellAsync(player, cast.Card, cast.Adventure, half: cast.Half, withPriority: true)
                    : await ActivateAbilityAsync(player, (ActivateAbility)action);
                // A legal action can only come to nothing because the player backed out of it; anything else is an engine error.
                if (!done && _playerCancels == cancels) _failedActions.Add($"turn {State.TurnNumber}, {playerId}: {Describe(action)}");
                return done;
            }

            default:
                throw new InvalidDecisionException($"Unsupported action {action}.");
        }
    }

    /// <summary>
    /// Casting (rule 601.2): announce how it's paid for, additional costs, modes and X (601.2b), choose targets (601.2c),
    /// work out the total cost (601.2f), then pay it (601.2g-h). Every step up to paying mana can be cancelled with nothing changed.
    /// </summary>
    /// <param name="exileAfter">Exiled instead of going anywhere else when it leaves the stack (as with flashback).</param>
    /// <param name="withPriority">Cast as an action with priority, rather than by an effect that's resolving (which ignores timing).</param>
    private async Task<bool> CastSpellAsync(Player player, CardId cardId, bool adventure = false, bool exileAfter = false, int? half = null, bool withPriority = false)
    {
        var card = State.GetCard(cardId);
        card.AsAdventure = adventure;
        card.CastHalf = half;
        // An aftermath half is exiled whenever it would leave the stack (702.127a).
        if (half is { } h && card.PrintedDefinition.SplitHalves?[h].Aftermath == true) exileAfter = true;
        if (!await CastAsItIsAsync(player, card, exileAfter, withPriority))
        {
            card.AsAdventure = false;
            card.CastHalf = null;
            return false;
        }
        return true;
    }

    private async Task<bool> CastAsItIsAsync(Player player, Card card, bool exileAfter, bool withPriority)
    {
        var cardId = card.Id;
        // Only a spell cast with priority needs flash or the extra cost for it: an effect that casts it ignores timing.
        bool sorceryTiming = player.Id == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0;
        bool flashExtra = withPriority && !TimingAllows(card, player.Id, sorceryTiming);
        // A spell none of whose ways to pay can be paid can't be cast (rule 601.2h), however it's being cast.
        if (!LegendarySpellAllowed(card, player.Id)) return false;
        // "Can't cast spells" also stops casting as part of an effect (rule 101.2).
        if (SpellsForbidden(player.Id)) return false;
        var payable = PayableCastingWays(card, player.Id, flashExtra);
        if (payable.Count == 0) return false;
        bool flashback = exileAfter || IsFlashbackCast(card);
        bool fromGraveyard = card.Zone == Zone.Graveyard;
        if (card.Zone == Zone.Hand) card.CastFromHand = true;

        // 601.2b: announcements, each one only if what's been announced so far can still be paid with it.
        // A gift is promised (or not) as the spell is cast, to an opponent (rule 702.174a).
        PlayerId? giftTo = null;
        if (card.Definition.Gift is not null && State.OpponentsOf(player.Id).Any()
            && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Promise an opponent a gift ({card.Definition.Gift.Name}) for {card.Name}?", cardId)))
            giftTo = await ChooseOpponentAsync(player.Id, card, "Choose the opponent who gets the gift");
        // The mana cost or an alternative cost (118.9), such as dash or "without paying its mana cost".
        var way = await ChooseCastingWayAsync(player.Id, card, payable.Select(c => c.Way).Distinct().ToList());
        // Additional cost with alternatives: the caster picks one that can be paid.
        var withWay = payable.Where(c => c.Way == way).ToList();
        int pick = 0;
        if (withWay.Count > 1)
        {
            var labels = withWay.Select(c => c.Option!.Extra is { } e ? DescribeCost(e) : $"Pay {c.Option.Mana}").ToList();
            pick = await ControllerOf(player.Id).ChooseOptionAsync(ViewFor(player.Id), new OptionRequest($"{card.Name}: choose the additional cost", cardId, labels, OptionKind.Other));
            Require(pick >= 0 && pick < withWay.Count, "Choose one of the costs.");
        }
        var choices = withWay[pick];
        // Kicker, and costs that may be paid any number of times.
        if (card.Definition.Kicker is { } kicker && CanPayToCast(card, player.Id, choices with { Kicked = true })
            && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Pay kicker {kicker} for {card.Name}?", cardId)))
            choices = choices with { Kicked = true };
        if (card.Definition.Multikicker is { } multi)
            choices = choices with { KickCount = await ChooseTimesAsync(player.Id, card, n => choices with { KickCount = n }, multi, "multikicker") };
        if (card.Definition.Replicate is { } replicate)
            choices = choices with { ReplicateCount = await ChooseTimesAsync(player.Id, card, n => choices with { ReplicateCount = n }, replicate, "replicate") };
        if (card.Definition.Squad is { } squad)
            choices = choices with { SquadCount = await ChooseTimesAsync(player.Id, card, n => choices with { SquadCount = n }, squad, "squad") };
        // Splice onto instant or sorcery (702.47): cards revealed from the hand add their effects to this spell.
        if (card.Is(CardType.Instant) || card.Is(CardType.Sorcery))
            foreach (var other in player.Hand.Where(h => h != cardId).Select(State.GetCard).Where(c => c.Definition.Splice is not null).ToList())
            {
                var spliced = choices with { Spliced = choices.Spliced.Append(other).ToList() };
                if (CanPayToCast(card, player.Id, spliced)
                    && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Splice {other.Name} onto {card.Name} (pay {other.Definition.Splice})?", other.Id)))
                    choices = spliced;
            }
        if (choices.Spliced.Count > 0) Emit(new CardsRevealed(player.Id, choices.Spliced.Select(c => c.Id).ToList()));
        // Conspire (702.78): you may tap two untapped creatures you control that share a color with it.
        if (card.Definition.Conspire && CanPayToCast(card, player.Id, choices with { Conspire = true })
            && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Conspire: tap two untapped creatures that share a color with {card.Name} to copy it?", cardId)))
            choices = choices with { Conspire = true };
        bool kicked = choices.Kicked || choices.KickCount > 0;
        // Modes.
        var ability = CastingTargets(card.Definition);
        if (kicked && ability?.WhenKicked is { } kickedVersion) ability = kickedVersion;
        if (ability is not null)
        {
            ability = await ChooseModesAsync(player.Id, ability, cardId, canCancel: true);
            if (ability is null) return false;
        }
        if (choices.Spliced.Count > 0)
            ability = (ability ?? new SpellAbility()) with { Effects = (ability?.Effects ?? Array.Empty<Effect>()).Concat(choices.Spliced.SelectMany(c => c.Definition.Spell?.Effects ?? Array.Empty<Effect>())).ToList() };
        // X: in the cost it's cast for, or in an additional cost ("pay X life", "exile X cards from your graveyard").
        bool exilesX = flashback && card.Zone == Zone.Graveyard && card.Definition.FlashbackExilesX;
        if (choices.Way.Mana.XCount > 0 || card.Definition.PayXLife || exilesX)
        {
            int max = 0;
            while (max < 99 && CanPayToCast(card, player.Id, choices with { X = max + 1 })) max++;
            var prompt = choices.Way.Mana.XCount > 0 ? $"{card.Name}: choose X"
                : card.Definition.PayXLife ? $"{card.Name}: choose X (pay X life)" : $"{card.Name}: choose X (exile X cards from your graveyard)";
            int chosenX = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest(prompt, cardId, 0, max));
            Require(chosenX >= 0 && chosenX <= max, $"X must be between 0 and {max}.");
            choices = choices with { X = chosenX };
        }
        int x = choices.X;

        // 601.2c: targets. "Costs {N} less if it targets …": when the announcements need that reduction, only targets that
        // give it can be chosen.
        Func<Abilities.Target, bool>? affordable = ability is { Targets.Count: 1 } && card.Definition.SelfCostReduction?.IfTargets is not null
            ? t => CanPayToCast(card, player.Id, choices, new[] { new ChosenTarget(t, VersionOf(t)) })
            : null;
        IReadOnlyList<ChosenTarget>? targets;
        // "Costs {1} more for each target beyond the first": no more targets than can be paid for.
        int? maxTargets = null;
        if (card.Definition.ExtraTargetCost > 0)
        {
            static IReadOnlyList<ChosenTarget> Some(int n) => Enumerable.Repeat(new ChosenTarget(Abilities.Target.Of(new PlayerId(0)), 0), n).ToList();
            int most = 1;
            while (most < 100 && CanPayToCast(card, player.Id, choices, Some(most + 1))) most++;
            maxTargets = most;
        }
        _announcedX = x;
        try { targets = await ChooseTargetsAsync(player.Id, ability, cardId, card.Name, canCancel: true, affordable, maxTargets); }
        finally { _announcedX = -1; }
        if (targets is null) return false;
        // 601.2d: how damage or counters are divided among the targets.
        var division = await ChooseDivisionAsync(player.Id, ability, cardId, targets);

        // 601.2f: the total cost, locked in.
        var cost = TotalCastingCost(card, player.Id, choices, targets);
        if (!CanPayCastingCost(card, player.Id, choices, cost)) return false; // the targets chosen don't allow what was announced
        var nonMana = NonManaCastingCosts(card, choices);
        // 601.2g-h: paying. Delve (702.66): each card exiled from the graveyard while casting pays for {1}.
        List<CardId> delved = new();
        if (card.Definition.Delve && cost.Generic > 0)
        {
            var graveyard = player.Graveyard.Where(id => id != cardId).ToList();
            int most = Math.Min(cost.Generic, graveyard.Count - nonMana.Sum(e => e.ExileFromGraveyard));
            int least = 0;
            while (least < most && !CanPayCastingMana(card, player.Id, choices, cost.MinusGeneric(least))) least++;
            if (most > 0)
            {
                var exiled = await ControllerOf(player.Id).ChooseCardsAsync(ViewFor(player.Id), new CardChoiceRequest($"Delve: exile up to {most} cards from your graveyard (each pays for {{1}})", cardId,
                    graveyard.Select(id => ViewBuilder.Card(State, id, player.Id)).ToList(), least, most, CardChoicePurpose.Discard));
                Require(exiled.Count >= least && exiled.Count <= most && exiled.Distinct().Count() == exiled.Count && exiled.All(graveyard.Contains), "Choose cards from your graveyard.");
                delved = exiled.ToList();
                cost = cost.MinusGeneric(delved.Count);
            }
        }
        var paidMana = await PayManaTapsAsync(player, cardId, cost, exclude: null, SpellManaUsable(card, player.Id, choices), UnitUsableFor(card, isAbility: false));
        if (paidMana is null) return false;
        if (choices.Way.Life > 0) ChangeLife(player.Id, -choices.Way.Life);
        foreach (var id in delved) await MoveCardAsync(id, Zone.Exile);
        List<Card>? conspirators = null;
        if (choices.Conspire)
        {
            var candidates = ConspireCandidates(card, player.Id);
            var tapped = await ControllerOf(player.Id).ChooseCardsAsync(ViewFor(player.Id), new CardChoiceRequest("Conspire: tap two creatures", cardId,
                candidates.Select(c => ViewBuilder.Card(State, c.Id, player.Id)).ToList(), 2, 2, CardChoicePurpose.Sacrifice));
            Require(tapped.Count == 2 && tapped.Distinct().Count() == 2 && tapped.All(id => candidates.Any(c => c.Id == id)), "Choose two of the listed creatures.");
            conspirators = tapped.Select(State.GetCard).ToList();
            foreach (var c in conspirators) Tap(c);
        }
        bool treasure = paidMana.Taps.Any(t => State.GetCard(t.Source).HasSubtype("Treasure")) || paidMana.SpecialSpent.Any(u => State.GetCard(u.Source).HasSubtype("Treasure"));
        // What the additional costs sacrificed or discarded stays known to the spell ("the sacrificed creature's power").
        var costSacrificed = new List<CardId>();
        var costDiscarded = new List<CardId>();
        foreach (var extra in nonMana)
        {
            costSacrificed.AddRange(await PayExtraAsync(player.Id, extra, cardId));
            if (extra.Discard > 0) costDiscarded.AddRange(_lastDiscardedForCost);
        }
        if (card.Definition.PayXLife && x > 0) ChangeLife(player.Id, -x);
        if (card.Zone == Zone.Graveyard && Has(player.Id, Replacements.PermanentsFromGraveyard) && GraveyardCost(card) is null
            && card.Definition.Flashback is null && !State.PlayableFromGraveyard.Any(p => p.Card == cardId && p.Version == card.Version) && !HasGraveyardCastRight(card))
            player.GraveyardTypesUsedThisTurn |= await ChoosePermanentTypeAsync(player, card);
        // Mana riders: haste for Dragon creature spells, copies of red instants and sorceries.
        var riders = paidMana.SpecialSpent.Select(u => u.Rider).ToList();
        if (riders.Contains(ManaRider.HasteForDragonCreatureSpells) && card.Is(CardType.Creature) && card.HasSubtype("Dragon")) card.HasteOnEnter = true;
        bool uncounterable = (riders.Contains(ManaRider.LegendaryUncounterable) && (card.Supertypes & Supertype.Legendary) != 0)
                             || riders.Contains(ManaRider.Uncounterable)
                             || (riders.Contains(ManaRider.InstantOrSorceryUncounterable) && (card.Is(CardType.Instant) || card.Is(CardType.Sorcery)));
        // "When that mana is spent to cast a creature spell that shares a creature type with your commander, scry 1."
        var scrySources = card.IsCreature && SharesCreatureTypeWithCommander(card, player.Id)
            ? paidMana.SpecialSpent.Where(u => u.Rider == ManaRider.ScryIfSharesTypeWithCommander).Select(u => u.Source).ToList()
            : new List<CardId>();
        // "When that mana is spent to cast a red instant or sorcery spell, copy that spell": one trigger of the mana's
        // source for each such mana spent, put on the stack above the spell.
        var copySources = (card.Is(CardType.Instant) || card.Is(CardType.Sorcery)) && card.Colors.Contains("R")
            ? paidMana.SpecialSpent.Where(u => u.Rider == ManaRider.CopyRedInstantOrSorcery).Select(u => u.Source).ToList()
            : new List<CardId>();

        if (card.Zone == Zone.Command) player.CommanderCasts[cardId] = player.CommanderCasts.GetValueOrDefault(cardId) + 1;
        bool fromHand = card.CastFromHand, haste = card.HasteOnEnter, asAdventure = card.AsAdventure;
        var castHalf = card.CastHalf;
        NoteExilePlay(card, player.Id);
        // "When you next cast a creature spell of that type this turn": the spell will enter with an additional +1/+1 counter.
        var bonus = card.IsCreature ? State.NextCreatureSpellBonus.Where(b => b.Player == player.Id && b.Turn == State.TurnNumber && card.HasSubtype(b.Type)).ToList() : new();
        await MoveCardAsync(cardId, Zone.Stack, controller: player.Id);
        foreach (var b in bonus)
        {
            State.NextCreatureSpellBonus.Remove(b);
            State.ExtraCountersOnEnter[(card.Id, card.Version)] = State.ExtraCountersOnEnter.GetValueOrDefault((card.Id, card.Version)) + 1;
        }
        card.AsAdventure = asAdventure;
        card.CastHalf = castHalf;
        card.CastFromGraveyard = fromGraveyard;
        card.ManaSpent = cost.ManaValue;
        card.PaidWithTreasure = treasure;
        card.GiftPromised = giftTo is not null;
        card.Uncounterable = uncounterable;
        card.Kicked = kicked;
        card.CastFromHand = fromHand;
        card.WasCast = true;
        card.CastDuringMainPhase = player.Id == State.ActivePlayer && State.Step.IsMain();
        card.HasteOnEnter = haste;
        player.SpellsCastThisTurn.Add(cardId);
        int castBefore = State.SpellsCastThisTurnCount++;
        PushStack(new SpellOnStack(cardId, player.Id, targets)
        {
            Ability = ability != CastingTargets(card.Definition) ? ability : null,
            X = x, Kicked = kicked, Flashback = flashback, GiftTo = giftTo, SacrificedForCost = costSacrificed, DiscardedForCost = costDiscarded, Division = division, KickCount = choices.KickCount, SquadCount = choices.SquadCount, Dashed = choices.Way.Kind == CastingWayKind.Dash,
        });
        Emit(new SpellCast(player.Id, cardId));
        foreach (var castThis in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == TriggerEvent.CastThis))
            AddPending(cardId, castThis, player.Id, new TriggerInfo(cardId, card.Version, player.Id, ManaValueOf(card)));
        // Storm, replicate and conspire trigger as the spell is cast (702.40a, 702.56a, 702.78a).
        if (card.Definition.Storm && castBefore > 0)
            _pendingTriggers.Add(new PendingTrigger(cardId, StormTrigger, player.Id, new TriggerInfo(cardId, card.Version, player.Id, castBefore)));
        if (choices.ReplicateCount > 0)
            _pendingTriggers.Add(new PendingTrigger(cardId, ReplicateTrigger, player.Id, new TriggerInfo(cardId, card.Version, player.Id, choices.ReplicateCount)));
        if (conspirators is not null)
            _pendingTriggers.Add(new PendingTrigger(cardId, ConspireTrigger, player.Id, new TriggerInfo(cardId, card.Version, player.Id, 1)));
        for (int i = 0; i < card.Definition.Cascade; i++) // cascade triggers as the spell is cast (rule 702.85a)
            _pendingTriggers.Add(new PendingTrigger(cardId, CascadeTrigger, player.Id, new TriggerInfo(cardId, card.Version, player.Id, card.ManaValue)));
        foreach (var source in copySources) QueueCopyThatSpell(source, player.Id, card);
        foreach (var source in scrySources) _pendingTriggers.Add(new PendingTrigger(source, ScryOneForSharedType, player.Id));
        return true;
    }

    private static readonly TriggeredAbility StormTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CopySpell(Subject.Triggered, new Quantity(0, QuantityKind.TriggerAmount)) },
        Text = "Storm: copy this spell for each spell cast before it this turn. You may choose new targets for the copies.",
    };

    private static readonly TriggeredAbility ReplicateTrigger = StormTrigger with
    {
        Text = "Replicate: copy this spell for each time its replicate cost was paid. You may choose new targets for the copies.",
    };

    private static readonly TriggeredAbility ConspireTrigger = StormTrigger with
    {
        Text = "Conspire: copy this spell. You may choose new targets for the copy.",
    };

    private static readonly TriggeredAbility ScryOneForSharedType = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new Scry(1) },
        Text = "That mana was spent on a creature spell that shares a creature type with your commander: scry 1.",
    };

    /// <summary>Whether a creature spell shares a creature type with a commander the player owns (in any zone).</summary>
    private bool SharesCreatureTypeWithCommander(Card spell, PlayerId player) =>
        State.Cards.Values.Where(c => c.IsCommander && c.Owner == player)
            .Any(commander => spell.CurrentSubtypes.Where(Card.IsCreatureType).Any(t => commander.HasSubtype(t)) || (spell.Has(Keyword.Changeling) && commander.CurrentSubtypes.Any(Card.IsCreatureType))
                              || (commander.Has(Keyword.Changeling) && spell.CurrentSubtypes.Any(Card.IsCreatureType)));

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
        // X is announced before targets are chosen (rule 601.2b): "target creature with power X" needs it.
        int? announcedX = null;
        // A loyalty cost of −X: X is announced, at most the loyalty it has (rule 606.4, 107.3k: no more counters than it has can be removed).
        if (ability.Cost.LoyaltyX)
        {
            int loyaltyNow = source.CounterCount(CounterKind.Loyalty);
            announcedX = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{source.Name}: choose X (remove X loyalty counters)", source.Id, 0, loyaltyNow));
            Require(announcedX >= 0 && announcedX <= loyaltyNow, $"X must be between 0 and {loyaltyNow}.");
            _announcedX = announcedX.Value;
        }
        if (announcedX is null && ability.Targets.Any(t => t.Filter?.PowerIsX == true || t.Filter?.ManaValueIsX == true) && ActivationCost(source, ability, player.Id, null).XCount > 0)
        {
            int maxX = MaxAffordableX(player.Id, ActivationCost(source, ability, player.Id, null), exclude);
            announcedX = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{source.Name}: choose X", source.Id, 0, maxX));
            Require(announcedX >= 0 && announcedX <= maxX, $"X must be between 0 and {maxX}.");
            _announcedX = announcedX.Value;
        }
        // Equip discounts depend on the creature targeted: only creatures it can be paid for can be chosen.
        Func<Abilities.Target, bool>? affordable = ability is { IsEquip: true, Targets.Count: 1 }
            ? t => Payable(player.Id, ActivationCost(source, ability, player.Id, new[] { new ChosenTarget(t, VersionOf(t)) }).WithX(0), exclude,
                UsableFor(source, isAbility: true))
            : null;
        var targets = await ChooseTargetsAsync(player.Id, ability, source.Id, ability.Text, canCancel: true, affordable);
        _announcedX = -1;
        if (targets is null) return false;
        var division = await ChooseDivisionAsync(player.Id, ability, source.Id, targets); // rule 602.2b → 601.2d
        // The free first equip is an alternative cost the player may choose (or not).
        bool free = ability.IsEquip && FreeEquipAvailable(player.Id)
                    && (!Payable(player.Id, ActivationCost(source, ability, player.Id, targets, useFreeEquip: false).WithX(0), exclude, AbilityManaUsable(source, ability, player.Id))
                        || await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"Pay {{0}} rather than the equip cost of {source.Name}?", source.Id)));

        var cost = ActivationCost(source, ability, player.Id, targets, useFreeEquip: free);
        int x = announcedX ?? 0;
        if (cost.XCount > 0 && announcedX is null)
        {
            int max = MaxAffordableX(player.Id, cost, exclude);
            x = await ControllerOf(player.Id).ChooseNumberAsync(ViewFor(player.Id), new NumberRequest($"{source.Name}: choose X", source.Id, 0, max));
            Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
        }
        cost = cost.WithX(x);

        // {S}: one mana from a snow source each, paid first (from snow mana in the pool, or by tapping a snow source now).
        for (int s = 0; s < ability.Cost.SnowMana; s++)
            if (!await PaySnowManaAsync(player, source, exclude)) return false;
        if (await PayManaTapsAsync(player, source.Id, cost, exclude, AbilityManaUsable(source, ability, player.Id), UnitUsableFor(source, isAbility: true)) is null) return false;
        if (ability.Cost.Tap)
        {
            Tap(source);
        }
        if (ability.Cost.RemoveCounters > 0)
            RemoveCountersFrom(source, ability.Cost.RemoveCounterKind, ability.Cost.RemoveCounters);
        if (ability.Cost.ExileSelf) await MoveCardAsync(source.Id, Zone.Exile);
        if (ability.Cost.FromHand)
        {
            await DiscardCardAsync(player.Id, source.Id, null); // cycling: discard this card
            // "When you cycle this card" (the discarded card's own ability, from wherever it went).
            foreach (var cycled in source.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == TriggerEvent.Cycled))
                AddPending(source.Id, cycled, player.Id, new TriggerInfo(source.Id, source.Version, player.Id, x));
        }
        if (ability.IsEquip) player.EquipsThisTurn++;
        _lastDiscardedForCost.Clear();
        var sacrificed = await PayExtraAsync(player.Id, ability.Cost.Extra, source.Id);
        if (ability.OncePerTurn) source.ActivatedThisTurn.Add(action.Index);
        if (ability.OnlyOnce) source.ActivatedEver.Add(action.Index);
        if (ability.Cost.AddCounters > 0)
        {
            PutCounters(source, ability.Cost.AddCounterKind, ability.Cost.AddCounters, player.Id);
            await ResolvePendingCountersAsync();
        }
        if (ability.Cost.ReturnSelfToHand) await MoveCardAsync(source.Id, Zone.Hand);
        if (ability.Cost.TapGranter && ability.GrantedBy is { } granter)
        {
            Tap(State.GetCard(granter));
        }
        if (ability.Cost.Loyalty is { } loyalty)
        {
            source.LoyaltyActivatedThisTurn = true;
            if (loyalty > 0) PutCounters(source, CounterKind.Loyalty, loyalty, player.Id);
            else if (loyalty < 0) RemoveCountersFrom(source, CounterKind.Loyalty, -loyalty);
        }
        else if (ability.Cost.LoyaltyX)
        {
            source.LoyaltyActivatedThisTurn = true;
            RemoveCountersFrom(source, CounterKind.Loyalty, x);
        }
        // "If this ability has been activated four or more times this turn": every activation counts, resolved or not.
        source.ActivationsThisTurn[action.Index] = source.ActivationsThisTurn.GetValueOrDefault(action.Index) + 1;
        if (ability.Cost.SacrificeSelf)
        {
            if (source.Zone == Zone.Graveyard) await MoveCardAsync(source.Id, Zone.Exile); // "Exile this card from your graveyard"
            else await SacrificePermanentAsync(source.Id);
        }

        Emit(new AbilityActivated(player.Id, source.Id, ability.Text));
        var item = new AbilityOnStack(source.Id, ability, player.Id, targets)
        {
            X = x, SacrificedForCost = sacrificed, SourceVersion = source.Version, DiscardedForCost = _lastDiscardedForCost.ToList(), Division = division,
            AbilityIndex = action.Index, SourceTransforms = source.TransformCount,
        };
        if (IsManaAbility(ability))
        {
            if (ability.Cost.Tap) Emit(new TappedForMana(player.Id, source.Id));
            await ApplyResolutionAsync(item, ability, source); // mana abilities don't use the stack (rule 605.3b)
            return true;
        }
        PushStack(item);
        // "Whenever an opponent activates an ability of [a permanent] that isn't a mana ability" / "whenever you activate an ability".
        if (source.Zone == Zone.Battlefield)
            foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller != player.Id))
                foreach (var watching in abilities.Where(a => a.Trigger == TriggerEvent.OpponentActivatesAbility))
                    if (Matches((watching.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, source, source.Controller, observer, observer.Controller))
                        AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(source.Id, source.Version, player.Id));
        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == player.Id))
            foreach (var watching in abilities.Where(a => a.Trigger == TriggerEvent.YouActivateNonManaAbility))
                AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(source.Id, source.Version, player.Id, State.Stack[^1].Id));
        return true;
    }

    /// <summary>An activated ability that only adds mana and has no targets (rule 605.1a).</summary>
    private static bool IsManaAbility(ActivatedAbility ability) => IsManaAbilityOf(ability);

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
    /// <summary>"When you play a card this way": a card played from exile with such an ability triggers it.</summary>
    private void NoteExilePlay(Card card, PlayerId player)
    {
        if (card.Zone != Zone.Exile) return;
        foreach (var used in State.PlayableFromExile.Where(p => p.Card == card.Id && p.Version == card.Version && p.Player == player && p.Group != 0).Select(p => p.Group).ToList())
            State.PlayableFromExile.RemoveAll(p => p.Group == used && p.Card != card.Id);
        foreach (var entry in State.PlayableFromExile.Where(p => p.Card == card.Id && p.Version == card.Version && p.Player == player && p.WhenPlayed is not null).ToList())
            _pendingTriggers.Add(new PendingTrigger(entry.WhenPlayed!.Value.Source, entry.WhenPlayed.Value.Ability, player));
    }

    private bool CanPayExtra(PlayerId playerId, ExtraCost? extra, CardId source)
    {
        if (extra is null) return true;
        var player = State.GetPlayer(playerId);
        if (player.Hand.Count(id => id != source && DiscardableFor(extra, id, playerId, source)) < extra.Discard) return false;
        if (extra.PayLife > player.Life || (extra.PayLife > 0 && player.CantLoseLifeTurn == State.TurnNumber)) return false; // rule 119.8
        if (ExilableFromGraveyard(player, extra, source).Count < extra.ExileFromGraveyard) return false;
        if (extra.ReturnExiledWithSource is { } exiledFilter && !ExiledWith(State.GetCard(source), exiledFilter).Any()) return false;
        if (extra.Sacrifice is { } filter && SacrificeCandidates(playerId, filter, source).Count < extra.SacrificeCount) return false;
        if (extra.TapCreatures is { } tapFilter && TapCandidates(playerId, tapFilter, source).Count < extra.TapCount) return false;
        if (extra.CrewPower > 0 && TapCandidates(playerId, new ObjectFilter(CardType.Creature, Other: true), source).Sum(c => c.Power) < extra.CrewPower) return false;
        if (extra.RemoveCountersFromYourCreatures > 0
            && State.PermanentsControlledBy(playerId).Where(c => c.IsCreature).Sum(c => c.Counters.Values.Sum()) < extra.RemoveCountersFromYourCreatures) return false;
        return true;
    }

    /// <summary>The cards in a graveyard that can be exiled to pay a cost (other than the source, which may be in it); only matching ones when it names a kind.</summary>
    private List<CardId> ExilableFromGraveyard(Player player, ExtraCost extra, CardId source) =>
        player.Graveyard.Where(id => id != source && (extra.ExileFromGraveyardFilter is not { } filter
            || Matches(filter with { Controller = ControllerFilter.Any }, State.GetCard(id), player.Id, State.GetCard(source), player.Id))).ToList();

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
        return State.PermanentsControlledBy(player).Where(c => Matches(any, c, player, sourceCard, player) && CanBeSacrificedBy(c, player)).ToList();
    }

    private async Task<IReadOnlyList<CardId>> PayExtraAsync(PlayerId playerId, ExtraCost? extra, CardId source, PlayerId? causedBy = null)
    {
        if (extra is null) return Array.Empty<CardId>();
        var player = State.GetPlayer(playerId);
        if (extra.PayLife > 0) ChangeLife(playerId, -extra.PayLife);
        if (extra.ExileFromGraveyard > 0)
        {
            var cards = ExilableFromGraveyard(player, extra, source);
            var exiled = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId), new CardChoiceRequest($"Exile {extra.ExileFromGraveyard} cards from your graveyard", source,
                cards.Select(id => ViewBuilder.Card(State, id, playerId)).ToList(), extra.ExileFromGraveyard, extra.ExileFromGraveyard, CardChoicePurpose.Sacrifice));
            Require(exiled.Count == extra.ExileFromGraveyard && exiled.Distinct().Count() == exiled.Count && exiled.All(cards.Contains), "Exile cards from your graveyard.");
            foreach (var id in exiled) await MoveCardAsync(id, Zone.Exile);
        }
        if (extra.ReturnExiledWithSource is { } returnFilter)
        {
            var exiled = ExiledWith(State.GetCard(source), returnFilter).ToList();
            var pick = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId), new CardChoiceRequest("Choose a card exiled with it to put into its owner's graveyard", source,
                exiled.Select(c => ViewBuilder.Card(State, c.Id, playerId, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Discard));
            Require(pick.Count == 1 && exiled.Any(c => c.Id == pick[0]), "Choose one of the exiled cards.");
            await MoveCardAsync(pick[0], Zone.Graveyard);
        }
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
                Tap(State.GetCard(id));
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
                RemoveCountersFrom(creature, kind, 1);
            }
        }
        _lastDiscardedForCost.Clear();
        if (extra.Discard > 0)
        {
            var hand = player.Hand.Where(id => id != source && DiscardableFor(extra, id, playerId, source)).ToList();
            var options = hand.Select(id => ViewBuilder.Card(State, id, playerId)).ToList();
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest($"Discard {extra.Discard} to pay the cost", source, options, extra.Discard, extra.Discard, CardChoicePurpose.Discard));
            Require(chosen.Count == extra.Discard && chosen.Distinct().Count() == chosen.Count && chosen.All(hand.Contains), "Discard from your hand.");
            foreach (var id in chosen) await DiscardCardAsync(playerId, id, causedBy);
            _lastDiscardedForCost.AddRange(chosen);
        }
        if (extra.Sacrifice is { } filter)
        {
            var candidates = SacrificeCandidates(playerId, filter, source);
            var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, playerId)).ToList();
            var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId),
                new CardChoiceRequest($"Sacrifice {extra.SacrificeCount} to pay the cost", source, options, extra.SacrificeCount, extra.SacrificeCount, CardChoicePurpose.Sacrifice));
            Require(chosen.Count == extra.SacrificeCount && chosen.Distinct().Count() == chosen.Count && chosen.All(id => candidates.Any(c => c.Id == id)),
                "Sacrifice one of the listed permanents.");
            foreach (var id in chosen) await SacrificePermanentAsync(id);
            return chosen;
        }
        return Array.Empty<CardId>();
    }

    /// <summary>Cards in exile exiled with this object (as it is now) that match the filter.</summary>
    private IEnumerable<Card> ExiledWith(Card source, ObjectFilter filter) =>
        State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == source.Id && w.Version == source.Version
                                      && Matches(filter with { Controller = ControllerFilter.Any }, c, c.Owner, source, source.Controller));

    /// <summary>Snow mana a player could spend now: snow mana in their pool and untapped snow permanents with a mana ability.</summary>
    private int SnowSourcesFor(PlayerId player, CardId? exclude) =>
        State.GetPlayer(player).ManaPool.Special.Count(u => u.Snow && u.OnlyFor is null)
        + ManaPayment.AvailableSources(State, player, exclude).Count(c => (c.Supertypes & Supertype.Snow) != 0);

    private async Task<bool> PaySnowManaAsync(Player player, Card source, CardId? exclude)
    {
        var pool = player.ManaPool;
        if (pool.Special.FirstOrDefault(u => u.Snow && u.OnlyFor is null) is not { } unit)
        {
            var snowy = ManaPayment.AvailableSources(State, player.Id, exclude).Where(c => (c.Supertypes & Supertype.Snow) != 0).ToList();
            if (snowy.Count == 0) return false;
            var pick = snowy.Count == 1 ? new[] { snowy[0].Id } : await ControllerOf(player.Id).ChooseCardsAsync(ViewFor(player.Id), new CardChoiceRequest(
                "Pay {S}: tap a snow source for mana", source.Id, snowy.Select(c => ViewBuilder.Card(State, c.Id, player.Id)).ToList(), 1, 1, CardChoicePurpose.Keep));
            if (pick.Count != 1 || snowy.All(c => c.Id != pick[0])) return false;
            var snowSource = State.GetCard(pick[0]);
            int option = ManaPayment.UsableOptions(snowSource, ManaPayment.Affordable(State, null))[0];
            await TapForManaAsync(player, new ManaTap(snowSource.Id, snowSource.ManaOptions[option].Types[0], option));
            unit = pool.Special.LastOrDefault(u => u.Snow && u.Source == snowSource.Id);
            if (unit is null) return false;
        }
        pool.RemoveSpecial(unit);
        return true;
    }

    /// <summary>Groups of exiled cards of which only one may be played.</summary>
    private int _playGroups;

    /// <summary>Cards discarded by the last cost paid.</summary>
    private readonly List<CardId> _lastDiscardedForCost = new();

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
            .SelectMany(c => ManaPayment.UsableOptions(c, ManaPayment.Affordable(State, usable)).Select(i => new ManaSourceOption(c.Id, c.ManaOptions[i].Types, c.ManaOptions[i].Amount, i) { Combination = c.ManaOptions[i].Combination })
                .OrderByDescending(o => o.Amount))
            .ToList();
        var request = new ManaPaymentRequest(source, cost, fromPool, remaining, plan.Taps, sources);

        var taps = await ControllerOf(player.Id).ChooseManaPaymentAsync(ViewFor(player.Id), request);
        if (taps is null)
        {
            _playerCancels++;
            return null;
        }

        Require(taps.Select(t => t.Source).Distinct().Count() == taps.Count, "Each source can be tapped only once.");
        Require(taps.All(t => sources.Any(s => s.Source == t.Source && s.Option == t.Option && s.Types.Contains(t.Type)) && ManaPayment.IsValid(State.GetCard(t.Source), t)), "Illegal mana source.");
        var produced = ManaPayment.Produced(State, taps).ToList();
        var (owed, excess) = ManaPayment.Apply(remaining, produced);
        Require(owed.ManaValue == 0, $"Payment is short by {owed}.");
        // No pointless taps: surplus is only allowed when a source adds more mana than is still needed.
        Require(excess == 0 || taps.Any(t => ManaPayment.AmountOf(State.GetCard(t.Source), t.Option) > 1), "Payment taps more mana than the cost.");

        foreach (var tap in taps) await TapForManaAsync(player, tap);
        // Pay the whole cost from the pool; any surplus keeps floating (rule 106.4).
        var plainUsed = new List<ManaType>();
        var specialUsed = new List<ManaUnit>();
        var (_, left) = ManaPayment.ApplyPool(cost, player.ManaPool, unitUsable, plainUsed, specialUsed);
        Require(left.ManaValue == 0, "Payment is short.");
        foreach (var type in plainUsed) player.ManaPool.Remove(type);
        foreach (var unit in specialUsed) player.ManaPool.RemoveSpecial(unit);
        return new ManaPaid(taps, specialUsed);
    }

    private async Task TapForManaAsync(Player player, ManaTap tap)
    {
        var source = State.GetCard(tap.Source);
        Tap(source);
        Emit(new TappedForMana(player.Id, tap.Source));
        var option = tap.Option < source.ManaOptions.Count ? source.ManaOptions[tap.Option] : null;
        // Restricted mana remembers what it may pay for; the source's "chosen" type or color is fixed now.
        var onlyFor = option?.OnlyFor is { } only
            ? only with
            {
                Subtype = only.ChosenType ? source.ChosenType : only.Subtype, ChosenType = false,
                Colors = only.ChosenColor && source.ChosenColor is { } chosen ? new[] { chosen } : only.Colors, ChosenColor = false,
            }
            : null;
        if (option is { LifeCost: > 0 } paysLife) ChangeLife(player.Id, -paysLife.LifeCost); // "{T}, Pay 1 life: Add …"
        var rider = option is { Rider: not ManaRider.None } ? option.Rider : source.Definition.ManaRider;
        bool snow = (source.Supertypes & Supertype.Snow) != 0;
        foreach (var type in ManaPayment.Produced(source, tap).ToList())
        {
            if (onlyFor is not null || rider != ManaRider.None || snow)
                player.ManaPool.AddSpecial(new ManaUnit(type, source.Id, onlyFor, option?.AbilitiesToo ?? false, rider) { Snow = snow });
            else player.ManaPool.Add(type);
            Emit(new ManaAdded(player.Id, type, tap.Source));
        }
        // "This land deals 1 damage to you" / "You gain 1 life" (part of the mana ability, rule 605.3b).
        if (option is { DamageToController: > 0 } hurts) await DealDamageAsync(source, null, player.Id, hurts.DamageToController);
        if (option is { GainLife: > 0 } heals) GainLifeFor(player.Id, heals.GainLife);
        if (source.Definition.SacrificeForMana) await SacrificePermanentAsync(source.Id);
    }

    /// <summary>Cards being cast for their miracle cost right now.</summary>
    private readonly Dictionary<CardId, ManaCost> _miracleCost = new();

    private static readonly TriggeredAbility MiracleTrigger = new()
    {
        Trigger = TriggerEvent.YouDrawCard,
        Effects = new Effect[] { new CastForMiracle() },
        Text = "Miracle: you may cast it by paying its miracle cost.",
    };

    /// <summary>The resolving spell exiles itself ("then you exile [this]") / goes to the bottom of its owner's library.</summary>
    private CardId? _exileResolvingSpell;
    private CardId? _spellToLibraryBottom;

    /// <summary>Cards being cast "without paying their mana cost" from the hand right now.</summary>
    private readonly HashSet<CardId> _castFree = new();

    /// <summary>
    /// Casts a card without paying its mana cost as part of an effect that's resolving (rule 608.2g): the permission
    /// lasts only for this cast, so a cast that's backed out of or can't be completed leaves nothing behind.
    /// <paramref name="hasteOnEnter"/> is a rider that applies only if the spell is actually cast.
    /// </summary>
    private async Task<bool> CastNowWithoutPayingAsync(Player player, CardId cardId, bool hasteOnEnter = false)
    {
        var card = State.GetCard(cardId);
        bool hadHaste = card.HasteOnEnter;
        if (hasteOnEnter) card.HasteOnEnter = true;
        _castFree.Add(cardId);
        bool cast = false;
        try { cast = await CastSpellAsync(player, cardId); }
        finally
        {
            _castFree.Remove(cardId);
            if (!cast) card.HasteOnEnter = hadHaste;
        }
        return cast;
    }

    /// <summary>How much generic mana the reductions that apply take off casting the card (601.2f).</summary>
    private int CostReductionFor(Card card, PlayerId caster, IReadOnlyList<ChosenTarget>? targets)
    {
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
        if (card.Definition.Undaunted) total += State.OpponentsOf(caster).Count(); // "costs {1} less to cast for each opponent"
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
                _exileResolvingSpell = null;
                if ((spell.Ability ?? CastingTargets(card.Definition)) is { } effect && !await ApplyResolutionAsync(item, effect, card))
                {
                    await MoveCardAsync(spell.Card, discard);
                    Emit(new FizzledOnResolution(spell.Card));
                    break;
                }
                if (card.Zone != Zone.Stack) break; // the spell moved itself (shuffled away, exiled...)
                if (_exileResolvingSpell == card.Id) discard = Zone.Exile;
                // "Exile that card with three time counters on it instead of putting it into your graveyard as it resolves."
                if (!card.Types.IsPermanent() && discard == Zone.Graveyard && State.SuspendOnResolution.Remove((card.Id, card.Version), out int timeCounters))
                {
                    await MoveCardAsync(spell.Card, Zone.Exile);
                    if (card.Zone == Zone.Exile)
                    {
                        card.Counters[CounterKind.Time] = timeCounters;
                        card.Suspended = true; // "if the exiled card doesn't have suspend, it gains suspend"
                    }
                    Emit(new SpellResolved(spell.Card));
                    break;
                }
                if (_spellToLibraryBottom == card.Id && !spell.Flashback)
                {
                    _spellToLibraryBottom = null;
                    await MoveCardAsync(spell.Card, Zone.Library, toBottom: true);
                    Emit(new SpellResolved(spell.Card));
                    break;
                }
                if (card.AsAdventure && !card.Definition.IsToken)
                {
                    // A resolved Adventure goes on an adventure: exiled, castable from there later (rule 715.4).
                    await MoveCardAsync(spell.Card, Zone.Exile);
                    if (card.Zone == Zone.Exile) card.OnAdventure = true;
                    Emit(new SpellResolved(spell.Card));
                    break;
                }
                bool hasteOnEnter = card.HasteOnEnter;
                int castVersion = card.Version;
                // An Aura spell enters attached to the object it targeted (rule 303.4f).
                var attachTo = card.Definition.EnchantTarget is not null ? item.Targets[0].Target.Card : null;
                await MoveCardAsync(spell.Card, card.Types.IsPermanent() ? Zone.Battlefield : discard, controller: spell.Controller, attachTo: attachTo,
                    kicked: spell.Kicked && card.Types.IsPermanent(), castFromHand: card.CastFromHand && card.Types.IsPermanent(),
                    wasCast: card.Types.IsPermanent() && !card.Definition.IsToken, timesKicked: spell.KickCount, squadPaid: spell.SquadCount);
                if (card.Zone == Zone.Battlefield && spell.Dashed)
                {
                    // Dash: it gains haste, and it returns to its owner's hand at the beginning of the next end step.
                    State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                    State.AtNextEndStepEffects.Add((card.Id, spell.Controller, card.Id, card.Version, new Effect[] { new ReturnToHand(Subject.Triggered) }, null, null));
                }
                if (card.Zone == Zone.Battlefield && hasteOnEnter)
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                if (card.Zone == Zone.Battlefield) card.CastX = spell.X;
                if (card.Zone == Zone.Battlefield && State.ExtraCountersOnEnter.Remove((spell.Card, castVersion), out var extraCounters))
                    PutCounters(card, CounterKind.PlusOnePlusOne, extraCounters, spell.Controller);
                if (card.Zone == Zone.Battlefield && card.Definition.EntersWithXCounters && spell.X > 0)
                    PutCounters(card, CounterKind.PlusOnePlusOne, spell.X * card.Definition.XCountersMultiplier, spell.Controller);
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
