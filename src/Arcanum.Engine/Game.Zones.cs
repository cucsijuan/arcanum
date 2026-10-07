// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private enum ZoneReplacementKind
    {
        /// <summary>"If it would die this turn, exile it instead."</summary>
        ExileIfDies,
        /// <summary>The card's own "if it would be put into a graveyard from anywhere, shuffle it into its owner's library instead".</summary>
        ShuffleIntoLibrary,
        /// <summary>"If a creature an opponent controls would die, exile it instead" (<see cref="ZoneReplacement.By"/>).</summary>
        ExiledByOpponentsPermanent,
        /// <summary>A spell cast with "if it would be put into a graveyard, exile it instead".</summary>
        ExileInsteadOfGraveyard,
        /// <summary>A permanent that exiles instants and sorceries that would be put into a graveyard.</summary>
        ExileInstantsAndSorceries,
        /// <summary>A commander that would be put into a hand or library may go to the command zone instead (903.9b).</summary>
        CommandZone,
        /// <summary>The card's own "if a spell or ability an opponent controls causes you to discard it, put it onto the battlefield instead".</summary>
        OntoBattlefieldInsteadOfDiscard,
        /// <summary>"If it would leave the battlefield, exile it instead of putting it anywhere else" (a permanent put there by such an effect).</summary>
        ExileIfLeaves,
    }

    /// <summary>One replacement effect that would modify where a card goes (rule 614.1a).</summary>
    private readonly record struct ZoneReplacement(ZoneReplacementKind Kind, Card? By = null)
    {
        /// <summary>The affected player may decline it ("may put it into the command zone instead").</summary>
        public bool Optional => Kind == ZoneReplacementKind.CommandZone;
    }

    /// <summary>Where a card goes once every replacement effect that modifies its zone change has been applied.</summary>
    private sealed record MovePlan(Zone To, bool Shuffle, IReadOnlyList<ZoneReplacement> Applied);

    /// <summary>Moves already decided (as the card moves, or ahead for cards leaving at the same time), by card and version: the requested zone and the plan.</summary>
    private readonly Dictionary<(CardId Card, int Version), (Zone Requested, MovePlan Plan)> _movePlans = new();

    /// <summary>
    /// The replacement effects that would apply to the card going from one zone to another, except those already applied (rule 614.5).
    /// <paramref name="discardedByOpponent"/>: the move is a discard that a spell or ability an opponent of the card's owner controls caused.
    /// </summary>
    private List<ZoneReplacement> ZoneReplacements(Card card, Zone from, Zone to, IReadOnlyList<ZoneReplacement> applied, bool discardedByOpponent = false)
    {
        var list = new List<ZoneReplacement>();
        if (from == Zone.Battlefield && to != Zone.Exile && State.ExileIfLeaves.Contains((card.Id, card.Version))) list.Add(new(ZoneReplacementKind.ExileIfLeaves));
        if (to is Zone.Hand or Zone.Library && from != to && Config.Commander is not null && card.IsCommander)
            list.Add(new(ZoneReplacementKind.CommandZone));
        if (to == Zone.Graveyard)
        {
            if (from == Zone.Battlefield && State.ExileIfDies.Contains((card.Id, card.Version))) list.Add(new(ZoneReplacementKind.ExileIfDies));
            if ((card.Definition.Replaces & Cards.Replacements.ShuffleIntoLibraryInsteadOfGraveyard) != 0) list.Add(new(ZoneReplacementKind.ShuffleIntoLibrary));
            if (discardedByOpponent && from == Zone.Hand && card.Definition.OntoBattlefieldIfOpponentMakesYouDiscard)
                list.Add(new(ZoneReplacementKind.OntoBattlefieldInsteadOfDiscard));
            if (from == Zone.Battlefield && card.IsCreature)
                list.AddRange(State.Battlefield.Select(State.GetCard)
                    .Where(c => c.Controller != card.Controller && (c.Definition.Replaces & Cards.Replacements.OpponentsCreaturesExiledInsteadOfDying) != 0)
                    .Select(c => new ZoneReplacement(ZoneReplacementKind.ExiledByOpponentsPermanent, c)));
            if (State.ExileInsteadOfGraveyard.Contains((card.Id, card.Version))) list.Add(new(ZoneReplacementKind.ExileInsteadOfGraveyard));
            if ((card.Is(Cards.CardType.Instant) || card.Is(Cards.CardType.Sorcery))
                && State.Battlefield.Any(b => (State.GetCard(b).Definition.Replaces & Cards.Replacements.ExileInstantsAndSorceries) != 0))
                list.Add(new(ZoneReplacementKind.ExileInstantsAndSorceries));
        }
        // A replacement effect gets only one opportunity to affect an event (rule 614.5).
        list.RemoveAll(applied.Contains);
        return list;
    }

    private static Zone Destination(ZoneReplacement replacement) => replacement.Kind switch
    {
        ZoneReplacementKind.ShuffleIntoLibrary => Zone.Library,
        ZoneReplacementKind.CommandZone => Zone.Command,
        ZoneReplacementKind.OntoBattlefieldInsteadOfDiscard => Zone.Battlefield,
        _ => Zone.Exile,
    };

    private string Describe(ZoneReplacement replacement, Card card) => replacement.Kind switch
    {
        ZoneReplacementKind.ExileIfDies => "Exile it (it would die this turn)",
        ZoneReplacementKind.ExileIfLeaves => "Exile it instead (it would leave the battlefield)",
        ZoneReplacementKind.ShuffleIntoLibrary => $"Shuffle it into its owner's library ({card.Name})",
        ZoneReplacementKind.ExiledByOpponentsPermanent => $"Exile it ({replacement.By!.Name}, {State.GetPlayer(replacement.By.Controller).Name})",
        ZoneReplacementKind.ExileInsteadOfGraveyard => "Exile it (the effect it was cast with)",
        ZoneReplacementKind.ExileInstantsAndSorceries => "Exile it (instants and sorceries are exiled)",
        ZoneReplacementKind.OntoBattlefieldInsteadOfDiscard => $"Put it onto the battlefield ({card.Name}: an opponent made you discard it)",
        _ => "Put it into the command zone",
    };

    /// <summary>
    /// Applies the replacement effects that modify where a card goes (rule 616.1): while any apply, the affected object's
    /// controller (its owner if it has none) chooses one, applies it, and the rest are checked again against the modified
    /// event, so one about the new destination may then apply (616.1e). An optional one ("may … instead") can be declined.
    /// Without <paramref name="canAsk"/> the move must need no choice; it then completes synchronously.
    /// </summary>
    private async Task<MovePlan> PlanMoveAsync(Card card, Zone to, bool canAsk = true, bool discardedByOpponent = false)
    {
        var from = card.Zone;
        var chooser = from is Zone.Battlefield or Zone.Stack ? card.Controller : card.Owner;
        var applied = new List<ZoneReplacement>();
        var declined = new List<ZoneReplacement>();
        bool shuffle = false;
        while (ZoneReplacements(card, from, to, applied.Concat(declined).ToList(), discardedByOpponent) is { Count: > 0 } options)
        {
            ZoneReplacement? pick = options[0];
            if (options.Count > 1 || options[0].Optional)
            {
                if (!canAsk) throw new InvalidOperationException($"Moving {card.Name} to {to} needs a replacement choice: move it with MoveCardAsync.");
                var zoneName = to.ToString().ToLowerInvariant();
                if (options.Count == 1)
                {
                    var request = new Players.YesNoRequest($"Put {card.Name} into the command zone instead of your {zoneName}?", card.Id);
                    if (!await ControllerOf(chooser).ChooseYesNoAsync(ViewFor(chooser), request)) pick = null;
                }
                else
                {
                    var labels = options.Select(o => Describe(o, card)).ToList();
                    // Not applying any is possible only when every one left is optional.
                    if (options.All(o => o.Optional)) labels.Add($"Put it into the {zoneName}");
                    int index = await ControllerOf(chooser).ChooseOptionAsync(ViewFor(chooser), new Players.OptionRequest(
                        $"{card.Name} would be put into the {zoneName}: choose which replacement applies", card.Id, labels, Players.OptionKind.Other));
                    Require(index >= 0 && index < labels.Count, "Choose one of the listed replacements.");
                    pick = index < options.Count ? options[index] : null;
                }
            }
            if (pick is not { } chosen)
            {
                declined.AddRange(options.Where(o => o.Optional));
                continue;
            }
            applied.Add(chosen);
            to = Destination(chosen);
            if (chosen.Kind == ZoneReplacementKind.ShuffleIntoLibrary) shuffle = true;
        }
        return new MovePlan(to, shuffle, applied);
    }

    /// <summary>
    /// Decides how each of these cards moves before any of them does, for cards that leave at the same time (rule 704.3):
    /// the choices among replacement effects are made as the event happens.
    /// </summary>
    private async Task PlanMovesAsync(IEnumerable<CardId> ids, Zone to)
    {
        foreach (var id in ids.ToList())
        {
            var card = State.GetCard(id);
            _movePlans[(id, card.Version)] = (to, await PlanMoveAsync(card, to));
        }
    }

    /// <summary>Moves a card, asking for the choices its zone-change replacement effects need (rule 616.1).</summary>
    /// <param name="transformed">"Put onto the battlefield transformed": a double-faced card enters with its back face up (rule 712.14a).</param>
    private async Task MoveCardAsync(CardId id, Zone to, bool toBottom = false, PlayerId? controller = null, CardId? attachTo = null, bool kicked = false,
        bool castFromHand = false, bool wasCast = false, int timesKicked = 0, int squadPaid = 0, bool tapped = false, bool faceDown = false, bool transformed = false)
    {
        var card = State.GetCard(id);
        transformed &= to == Zone.Battlefield && card.Zone != Zone.Battlefield && card.IsDoubleFaced;
        if (to == Zone.Battlefield && card.Zone != Zone.Battlefield)
        {
            var newController = controller ?? card.Owner;
            // "You may have this creature enter as a copy of …": chosen as it enters, so it enters with the copy's characteristics.
            if (await ChooseCopyAsync(card, newController, transformed) is { } copied) _pendingCopies[(id, card.Version)] = copied;
            // An Aura put onto the battlefield without being cast: the player putting it there chooses what it enchants; with
            // nothing legal to enchant it stays where it is (rules 303.4f, 303.4g).
            if (attachTo is null && card.Zone != Zone.Stack && (_pendingCopies.GetValueOrDefault((id, card.Version)) ?? EnteringFace(card, transformed)) is { EnchantTarget: { } enchant } aura
                && aura.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase))
            {
                var hosts = AuraHosts(card, aura, enchant, newController);
                if (hosts.Count == 0)
                {
                    _pendingCopies.Remove((id, card.Version));
                    return;
                }
                attachTo = hosts.Count == 1 ? hosts[0] : await ChooseAuraHostAsync(card, enchant, hosts, newController);
            }
        }
        if (!(_movePlans.TryGetValue((id, card.Version), out var planned) && planned.Requested == to))
            _movePlans[(id, card.Version)] = (to, await PlanMoveAsync(card, to));
        var move = BeginMove(id, to, toBottom, controller, attachTo, kicked, castFromHand, wasCast, timesKicked, squadPaid, tapped, faceDown, transformed);
        // A permanent that enters with counters its controller must order replacement effects for (rule 616.1): asked as it
        // enters, so the counters are on it before it is announced, as in every other case.
        var ordered = await OrderEnterCountersAsync(move.Card, move.EnterCounters);
        foreach (var returning in FinishMove(move, ordered)) await MoveCardAsync(returning, Zone.Battlefield, controller: State.GetCard(returning).Owner);
    }

    /// <summary>
    /// Moves a card between zones. Cards always go to their owner's per-player zones (rule 400.3). Directly only for moves
    /// that need no replacement choice (or whose choices were made); everything else goes through <see cref="MoveCardAsync"/>.
    /// Counters a permanent enters with that need their replacement effects ordered are, here, put on right after the current effect.
    /// </summary>
    /// <param name="tapped">A permanent entering the battlefield tapped (rule 614.1c): it is tapped from the start, with no event.</param>
    /// <param name="kicked">A spell cast with kicker becoming a permanent: it remembers it was kicked (for "if it was kicked").</param>
    /// <param name="faceDown">Exiled face down (rule 406.3): only the players who could see it as it moved know it.</param>
    private void MoveCard(CardId id, Zone to, bool toBottom = false, PlayerId? controller = null, CardId? attachTo = null, bool kicked = false,
        bool castFromHand = false, bool wasCast = false, int timesKicked = 0, int squadPaid = 0, bool tapped = false, bool faceDown = false, bool transformed = false)
    {
        var card = State.GetCard(id);
        transformed &= to == Zone.Battlefield && card.Zone != Zone.Battlefield && card.IsDoubleFaced;
        if (to == Zone.Battlefield && card.Zone != Zone.Battlefield && attachTo is null)
        {
            // Entering needs a choice that can't be asked here (a copy, or what an Aura enchants): it enters as soon as the
            // choice can be made, before anything else happens. An Aura with nothing to enchant stays where it is (303.4g).
            var newController = controller ?? card.Owner;
            var entering = EnteringFace(card, transformed);
            bool copyChoice = entering.EntersAsCopyOf is { } copyFilter && CopyCandidates(card, copyFilter, newController).Count > 0;
            if (entering.EnchantTarget is { } enchant && entering.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase))
            {
                var hosts = AuraHosts(card, entering, enchant, newController);
                if (hosts.Count == 0) return;
                if (hosts.Count == 1 && !copyChoice) attachTo = hosts[0];
                else copyChoice = true;
            }
            if (copyChoice)
            {
                _deferredEnters.Add((id, card.Version, newController, transformed));
                return;
            }
        }
        var move = BeginMove(id, to, toBottom, controller, attachTo, kicked, castFromHand, wasCast, timesKicked, squadPaid, tapped, faceDown, transformed);
        foreach (var returning in FinishMove(move, null)) MoveCard(returning, Zone.Battlefield, controller: State.GetCard(returning).Owner);
    }

    /// <summary>The face a card about to enter the battlefield will have up: its back face if it enters transformed (rule 712.14a).</summary>
    private static Cards.CardDefinition EnteringFace(Card card, bool transformed) =>
        transformed && card.PrintedDefinition.BackFace is { } back ? back : card.Definition;

    /// <summary>Copies chosen for cards about to enter (by card and version before the move).</summary>
    private readonly Dictionary<(CardId Card, int Version), Cards.CardDefinition> _pendingCopies = new();

    /// <summary>Cards that were to enter the battlefield where no choice could be asked: they enter (asking) before the next priority.</summary>
    private readonly List<(CardId Card, int Version, PlayerId Controller, bool Transformed)> _deferredEnters = new();

    private async Task EnterDeferredAsync()
    {
        if (_deferredEnters.Count == 0) return;
        var entering = _deferredEnters.ToList();
        _deferredEnters.Clear();
        BeginEnteringTogether();
        foreach (var (id, version, controller, transformed) in entering)
            if (State.GetCard(id) is { } card && card.Version == version && card.Zone != Zone.Battlefield)
                await MoveCardAsync(id, Zone.Battlefield, controller: controller, transformed: transformed);
        EndEnteringTogether();
    }

    /// <summary>Creatures a card entering "as a copy of any [filter] on the battlefield" may copy (not those entering with it).</summary>
    private List<Card> CopyCandidates(Card card, Abilities.ObjectFilter filter, PlayerId controller) =>
        State.Battlefield.Select(State.GetCard)
            .Where(c => c.Id != card.Id && !(_enteringTogether?.Contains(c.Id) ?? false)
                        && Matches(filter with { Controller = Abilities.ControllerFilter.Any }, c, c.Controller, card, controller))
            .ToList();

    /// <summary>"You may have this enter as a copy of …": the player chooses one (or none); the copy's copiable values (rule 707.2).</summary>
    private async Task<Cards.CardDefinition?> ChooseCopyAsync(Card card, PlayerId controller, bool transformed = false)
    {
        if (EnteringFace(card, transformed).EntersAsCopyOf is not { } filter) return null;
        var candidates = CopyCandidates(card, filter, controller);
        if (candidates.Count == 0) return null;
        var chosen = await ControllerOf(controller).ChooseCardsAsync(ViewFor(controller), new Players.CardChoiceRequest(
            $"{card.Name}: choose a creature to enter as a copy of (or none)", card.Id,
            candidates.Select(c => ViewBuilder.Card(State, c.Id, controller)).ToList(), 0, 1, Players.CardChoicePurpose.ToBattlefield));
        Require(chosen.Count <= 1 && chosen.All(c => candidates.Any(x => x.Id == c)), "Choose one of the creatures, or none.");
        if (chosen.Count == 0) return null;
        var original = State.GetCard(chosen[0]);
        Emit(new ChoiceMade(card.Id, $"a copy of {original.Name}"));
        // The copiable values are the original's own (with any copy effect on it), not counters or other effects (rule 707.2);
        // being a card (or a token) stays the copy's own.
        return original.Definition with { IsToken = card.PrintedDefinition.IsToken, Foil = card.PrintedDefinition.Foil };
    }

    /// <summary>
    /// What an Aura entering without being cast may enchant (rule 303.4f): what its enchant ability allows, under the player it
    /// enters for, that it isn't protected from, and not anything entering at the same time.
    /// </summary>
    private List<CardId> AuraHosts(Card aura, Cards.CardDefinition auraDefinition, Abilities.TargetSpec enchant, PlayerId controller)
    {
        var colors = auraDefinition.ColorList;
        return State.Battlefield.Select(State.GetCard)
            .Where(h => h.Id != aura.Id && !(_enteringTogether?.Contains(h.Id) ?? false) && MatchesKind(h, enchant.Kind)
                        && enchant.Controller switch
                        {
                            Abilities.ControllerFilter.You => h.Controller == controller,
                            Abilities.ControllerFilter.Opponent => h.Controller != controller,
                            _ => true,
                        }
                        && (enchant.Filter is not { } f || Matches(f with { Controller = Abilities.ControllerFilter.Any }, h, h.Controller, aura, controller))
                        && !h.Has(Cards.Keyword.ProtectionFromEverything) && !h.ProtectedFromPlayers.Contains(controller)
                        && (h.ProtectionFromTypes & auraDefinition.Types) == 0
                        && !colors.Any(c => Cards.Keywords.ProtectionFrom(c) is { } protection && h.Has(protection)))
            .Select(h => h.Id).ToList();
    }

    private async Task<CardId> ChooseAuraHostAsync(Card aura, Abilities.TargetSpec enchant, List<CardId> hosts, PlayerId controller)
    {
        var request = new Players.TargetRequest(aura.Id, $"Choose what {aura.Name} enchants as it enters", new[] { enchant with { Optional = false, Text = $"what {aura.Name} enchants" } },
            new[] { (IReadOnlyList<Abilities.Target>)hosts.Select(Abilities.Target.Of).ToList() }, CanCancel: false);
        var pick = await ControllerOf(controller).ChooseTargetsAsync(ViewFor(controller), request);
        Require(pick is { Count: 1 } && pick[0].Card is { } c && hosts.Contains(c), "Choose one of the permanents it can enchant.");
        return pick![0].Card!.Value;
    }

    /// <summary>A card that has moved, before anything is announced: the counters it enters with are still to be put on.</summary>
    private sealed record MoveInProgress(Card Card, Zone From, Zone To, PlayerId LastController, MovePlan Plan, List<(Abilities.CounterKind Kind, int Count)> EnterCounters);

    /// <summary>The move itself, with its replacement effects applied: the card is in its new zone but nothing has been announced yet.</summary>
    private MoveInProgress BeginMove(CardId id, Zone to, bool toBottom, PlayerId? controller, CardId? attachTo, bool kicked,
        bool castFromHand, bool wasCast, int timesKicked, int squadPaid, bool tapped, bool faceDown, bool transformed = false)
    {
        var enterCounters = new List<(Abilities.CounterKind Kind, int Count)>();
        var card = State.GetCard(id);
        var from = card.Zone;
        var owner = State.GetPlayer(card.Owner);
        var lastController = card.Controller;
        if (from == Zone.Battlefield) NoteLibraryTopLooks(); // a permission to look at it may be leaving with this card
        // The players who see the card as it moves keep knowing it where it goes, even into a hidden zone.
        var knewIt = State.Players.Where(p => card.IsVisibleTo(p.Id)).Select(p => p.Id).ToList();

        // Replacement effects on where the card goes (rule 614), decided as it moves or ahead for a simultaneous move.
        var plan = _movePlans.Remove((id, card.Version), out var planned) && planned.Requested == to
            ? planned.Plan
            : PlanMoveAsync(card, to, canAsk: false).GetAwaiter().GetResult();
        to = plan.To;
        foreach (var applied in plan.Applied)
        {
            if (applied.Kind == ZoneReplacementKind.ExileInsteadOfGraveyard) State.ExileInsteadOfGraveyard.Remove((id, card.Version));
            // "If a creature an opponent controls would die, exile it instead. When you do, …"
            if (applied is { Kind: ZoneReplacementKind.ExiledByOpponentsPermanent, By: { } replacer })
                Queue(replacer.Id, Abilities.TriggerEvent.CreatureExiledInstead, replacer.Controller);
        }

        if (from == Zone.Stack) card.LastOnStack = (card.Version, ManaValueOf(card));
        switch (from)
        {
            case Zone.Battlefield:
                card.LeftBattlefieldTurn = State.TurnNumber;
                card.WasAttacking = State.Combat?.FindAttack(id) is not null;
                card.WasBlocking = State.Combat?.IsBlocking(id) == true;
                State.Battlefield.Remove(id);
                State.Combat?.Remove(id);
                break;
            case Zone.Stack:
                State.Stack.RemoveAll(s => s is SpellOnStack spell && spell.Card == id);
                break;
            default:
                owner.GetZone(from).Remove(id);
                break;
        }

        card.ResetStatus();
        // A double-faced card enters front face up unless it's put onto the battlefield transformed (rule 712.14).
        card.Transformed = transformed && to == Zone.Battlefield && card.IsDoubleFaced;
        if (to == Zone.Battlefield && _pendingCopies.Remove((id, card.Version - 1), out var copied)) card.CopiedDefinition = copied;
        _pendingCopies.Remove((id, card.Version - 1));
        card.ZoneChangedTurn = State.TurnNumber;
        card.EnteredFrom = from;
        card.Kicked = kicked;
        card.TimesKicked = to == Zone.Battlefield ? timesKicked : 0;
        card.SquadPaid = to == Zone.Battlefield ? squadPaid : 0;
        card.CastFromHand = castFromHand;
        card.WasCast = wasCast;
        card.Zone = to;
        card.FaceDown = faceDown && to == Zone.Exile;
        card.KnownTo.UnionWith(knewIt);
        NoteCommanderMove(card, to);
        switch (to)
        {
            case Zone.Battlefield:
                card.Controller = controller ?? card.Owner;
                card.BaseController = card.Controller;
                card.AttachedTo = attachTo;
                // Replacement effects that modify how the permanent enters (rule 614.1c).
                if (tapped || card.Definition.EntersTapped) card.Tapped = true;
                if (card.Definition.EntersTappedUnless is { } unless && !Holds(unless, card.Controller, card)) card.Tapped = true;
                if (card.IsCreature && OpponentsCreaturesEnterTapped(card.Controller)) card.Tapped = true;
                // Counters it enters with (rule 122.6) are on it before it is announced as entered, so its enters triggers see them.
                if (card.Definition.EntersWithCounters > 0
                    && (card.Definition.EntersWithCountersIf is not { } cond || Holds(cond, card.Controller, card)))
                    enterCounters.Add((card.Definition.EntersWithCounterKind, card.Definition.EntersWithCounters));
                if (card.Definition.EntersWithCountersFrom is { } countFrom)
                    enterCounters.Add((card.Definition.EntersWithCounterKind,
                        Eval(countFrom, new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))));
                if (card.Definition.Loyalty is { } loyalty) enterCounters.Add((Abilities.CounterKind.Loyalty, loyalty)); // 306.5b
                if (card.Definition.FinalChapter > 0) enterCounters.Add((Abilities.CounterKind.Lore, 1)); // a Saga enters with a lore counter (714.3a)
                // "Each other Angel you control enters with an additional +1/+1 counter for each Angel you already control."
                if (card.HasSubtype("Angel"))
                {
                    int angels = State.Battlefield.Select(State.GetCard).Count(c => c.Controller == card.Controller && c.HasSubtype("Angel"));
                    int extraCounterSources = State.Battlefield.Select(State.GetCard)
                        .Count(c => c.Controller == card.Controller && (c.Definition.Replaces & Cards.Replacements.AngelsEnterWithCounters) != 0);
                    enterCounters.Add((Abilities.CounterKind.PlusOnePlusOne, angels * extraCounterSources));
                }
                State.Battlefield.Add(id);
                if (card.IsCreature && ExtraEnterCounters(card) is var extraCounters and > 0) enterCounters.Add((Abilities.CounterKind.PlusOnePlusOne, extraCounters));
                if (card.Definition.ChooseOnEnter != Cards.EnterChoice.None || card.Definition.Devour > 0) State.PendingEnterChoices.Add((id, card.Version));
                break;
            case Zone.Stack:
                card.Controller = controller ?? card.Owner;
                break;
            case Zone.Library:
                if (toBottom) owner.Library.Add(id);
                else owner.Library.Insert(0, id);
                break;
            default:
                owner.GetZone(to).Add(id);
                break;
        }
        return new MoveInProgress(card, from, to, lastController, plan, enterCounters);
    }

    /// <summary>
    /// Completes a move (<see cref="BeginMove"/>): the permanent gets the counters it enters with, then everything is announced.
    /// <paramref name="ordered"/> holds the amounts of counters (by index) whose replacement effects the controller already ordered.
    /// Returns the cards exiled "until this leaves the battlefield" that come back now (rule 610.3).
    /// </summary>
    private List<CardId> FinishMove(MoveInProgress move, IReadOnlyDictionary<int, int>? ordered)
    {
        var (card, from, to, lastController, plan, enterCounters) = move;
        var id = card.Id;
        var owner = State.GetPlayer(card.Owner);
        // The permanent is on the battlefield with its counters before anything reacts to it entering (rule 614.1c).
        var placedCounters = to == Zone.Battlefield ? PlaceEnterCounters(card, enterCounters, ordered) : new List<(Abilities.CounterKind Kind, int Count)>();
        RecomputeContinuousEffects();
        int leavingVersion = card.Version - 1;
        Emit(new CardMoved(id, card.Owner, from, to, lastController));
        if (plan.Applied.Any(r => r.Kind == ZoneReplacementKind.CommandZone)) Emit(new CommanderReturned(id, card.Owner));
        AnnounceEnterCounters(card, placedCounters);
        // "Shuffle it into its owner's library": that library is shuffled even if the card went elsewhere instead.
        if (plan.Shuffle) Shuffle(owner);

        // Cards exiled "until this leaves the battlefield" come back (rule 610.3).
        var returning = new List<CardId>();
        if (from == Zone.Battlefield)
            foreach (var link in State.LinkedExiles.Where(l => l.Source == id && l.SourceVersion == leavingVersion).ToList())
            {
                State.LinkedExiles.Remove(link);
                var exiled = State.GetCard(link.Exiled);
                if (exiled.Zone == Zone.Exile && exiled.Version == link.ExiledVersion) returning.Add(link.Exiled);
            }

        // A token that leaves the battlefield ceases to exist (rule 111.7, 704.5d).
        if (card.Definition.IsToken && to != Zone.Battlefield && to != Zone.Stack) owner.GetZone(to).Remove(id);
        return returning;
    }

    private async Task DrawAsync(PlayerId playerId, int count = 1)
    {
        for (int i = 0; i < count; i++) await DrawOneAsync(playerId, 0);
    }

    /// <summary>
    /// One draw, after replacement effects (rule 614.11): "draw two instead" and "instead that player skips that draw and you draw
    /// a card"; when both apply the drawing player chooses which applies (rule 616.1).
    /// </summary>
    private async Task DrawOneAsync(PlayerId playerId, int depth)
    {
        var player = State.GetPlayer(playerId);
        // "If you would draw a card except the first one you draw in each of your draw steps …"
        bool firstInDrawStep = State.Step == Step.Draw && State.ActivePlayer == playerId && !player.DrewInDrawStep;
        if (State.Step == Step.Draw && State.ActivePlayer == playerId) player.DrewInDrawStep = true;
        bool doubles = (!firstInDrawStep && Has(playerId, Cards.Replacements.DrawTwoExceptFirstInDrawStep))
                       || (player.Hand.Count == 0 && Has(playerId, Cards.Replacements.DrawTwoWithEmptyHand));
        var thief = firstInDrawStep || depth > 8 ? null
            : State.Battlefield.Select(State.GetCard).FirstOrDefault(c => c.Controller != playerId && (c.Definition.Replaces & Cards.Replacements.StealsOpponentsExtraDraws) != 0 && !c.LosesAbilities && !c.LosesTextAbilities);
        if (thief is not null && (!doubles || await ControllerOf(playerId).ChooseOptionAsync(ViewFor(playerId), new Players.OptionRequest(
                "Two replacement effects apply to this draw: choose the one that applies", thief.Id,
                new[] { $"{thief.Name}: skip this draw ({State.GetPlayer(thief.Controller).Name} draws instead)", "Draw two cards instead" }, Players.OptionKind.Other)) == 0))
        {
            Emit(new Events.ChoiceMade(thief.Id, $"{player.Name} skips a draw"));
            await DrawOneAsync(thief.Controller, depth + 1);
            return;
        }
        for (int n = 0; n < (doubles ? 2 : 1); n++)
        {
            if (player.Library.Count == 0)
            {
                player.AttemptedDrawFromEmptyLibrary = true; // loses at next SBA check (rule 704.5b)
                return;
            }
            var top = player.Library[0];
            // A drawn commander its owner puts into the command zone instead (rule 903.9b) is still a draw: that replacement
            // modifies only where the card goes, and the modified event happens instead of the original (rule 614.6), so the
            // player still drew the top card (rule 121.1). The draw isn't replaced by some different event, so it counts for
            // "cards drawn this turn" and "whenever you draw a card" triggers.
            await MoveCardAsync(top, Zone.Hand);
            Emit(new CardDrawn(playerId, top));
            // Miracle: the first card a player draws in a turn may be revealed as it's drawn (702.94a).
            if (State.GetCard(top).Definition.Miracle is not null && player.CardsDrawnThisTurn == 1 && State.GetCard(top).Zone == Zone.Hand)
                _pendingTriggers.Add(new PendingTrigger(top, MiracleTrigger, playerId, new TriggerInfo(top, State.GetCard(top).Version, playerId)));
        }
    }

    private void Shuffle(Player player)
    {
        Rng.Shuffle(player.Library);
        // No one knows where any card of a shuffled library is (rule 701.24a).
        foreach (var id in player.Library) State.GetCard(id).KnownTo.Clear();
        RecomputeContinuousEffects(); // "as long as the top card of your library is …"
        Emit(new LibraryShuffled(player.Id));
    }

    /// <summary><paramref name="who"/> looks at these cards: they know them where they are, and as they move from there.</summary>
    private void Look(PlayerId who, IEnumerable<CardId> cards)
    {
        foreach (var id in cards) State.GetCard(id).KnownTo.Add(who);
    }

    /// <summary>Revealed cards (rule 701.20a): every player knows them.</summary>
    private void RevealToAll(IEnumerable<CardId> cards)
    {
        foreach (var id in cards) State.GetCard(id).KnownTo.UnionWith(State.Players.Select(p => p.Id));
    }

    /// <summary>
    /// Cards just put into a library together in an order only <paramref name="arranger"/> sees (rule 401.4), or in a random
    /// order (null): when more than one of them went there, the other players can no longer tell which card is where.
    /// </summary>
    private void PlacedInUnseenOrder(IEnumerable<CardId> cards, PlayerId? arranger)
    {
        var placed = cards.Where(id => State.GetCard(id).Zone == Zone.Library).ToList();
        if (placed.Count < 2) return;
        foreach (var id in placed) State.GetCard(id).KnownTo.RemoveWhere(p => p != arranger);
        KnowledgeLost?.Invoke();
    }

    /// <summary>"You may look at the top card of your library any time": the player knows that card.</summary>
    private void NoteLibraryTopLooks()
    {
        foreach (var player in State.Players)
            if (player.Library.Count > 0 && ViewBuilder.MayLookAtLibraryTop(State, player.Id))
                State.GetCard(player.Library[0]).KnownTo.Add(player.Id);
        // Cards an effect keeps revealed (an opponent's hand, the top card of a library) are known to those who see them.
        foreach (var viewer in State.Players)
            foreach (var player in State.Players)
            {
                if (player.Library.Count > 0 && ViewBuilder.RevealedByEffect(State, State.GetCard(player.Library[0]), viewer.Id))
                    State.GetCard(player.Library[0]).KnownTo.Add(viewer.Id);
                if (player.Id != viewer.Id && player.Hand.Count > 0 && ViewBuilder.RevealedByEffect(State, State.GetCard(player.Hand[0]), viewer.Id))
                    foreach (var id in player.Hand) State.GetCard(id).KnownTo.Add(viewer.Id);
            }
    }

    /// <summary>What players learn from an event as it happens: revealed cards, and hands looked at.</summary>
    private void NoteKnowledge(GameEvent e)
    {
        switch (e)
        {
            case CardsRevealed r: RevealToAll(r.Cards); break;
            case HandRevealed h: RevealToAll(h.Cards); break;
            case HandLookedAt l: Look(l.Looker, l.Cards); break;
        }
        NoteLibraryTopLooks();
    }
}
