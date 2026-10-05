// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private enum DeathFate { ExiledThisTurn, Shuffled, ExiledBy }

    /// <summary>One replacement effect that applies to a permanent going from the battlefield to a graveyard.</summary>
    private readonly record struct DeathReplacement(DeathFate Fate, Card? By);

    /// <summary>The replacement each controller chose for its permanents about to die, by card and version.</summary>
    private readonly Dictionary<(CardId Card, int Version), DeathReplacement> _deathReplacement = new();

    /// <summary>Every replacement effect that would apply if the permanent went from the battlefield to a graveyard.</summary>
    private List<DeathReplacement> DeathReplacements(Card card)
    {
        var list = new List<DeathReplacement>();
        if (State.ExileIfDies.Contains((card.Id, card.Version))) list.Add(new(DeathFate.ExiledThisTurn, null));
        if ((card.Definition.Replaces & Cards.Replacements.ShuffleIntoLibraryInsteadOfGraveyard) != 0) list.Add(new(DeathFate.Shuffled, null));
        if (card.IsCreature)
            list.AddRange(State.Battlefield.Select(State.GetCard)
                .Where(c => c.Controller != card.Controller && (c.Definition.Replaces & Cards.Replacements.OpponentsCreaturesExiledInsteadOfDying) != 0)
                .Select(c => new DeathReplacement(DeathFate.ExiledBy, c)));
        return list;
    }

    /// <summary>
    /// For each permanent about to go to a graveyard from the battlefield with more than one replacement effect that
    /// would apply, its controller chooses which one does (rule 616.1).
    /// </summary>
    private async Task ChooseDeathReplacementsAsync(IEnumerable<CardId> ids)
    {
        foreach (var id in ids.ToList())
        {
            var card = State.GetCard(id);
            if (card.Zone != Zone.Battlefield) continue;
            _deathReplacement.Remove((id, card.Version));
            var options = DeathReplacements(card);
            if (options.Count < 2) continue;
            var labels = options.Select(o => o.Fate switch
            {
                DeathFate.ExiledThisTurn => "Exile it (it would die this turn)",
                DeathFate.Shuffled => $"Shuffle it into its owner's library ({card.Name})",
                _ => $"Exile it ({o.By!.Name}, {State.GetPlayer(o.By.Controller).Name})",
            }).ToList();
            int pick = await ControllerOf(card.Controller).ChooseOptionAsync(ViewFor(card.Controller),
                new Players.OptionRequest($"{card.Name} would be put into a graveyard: choose which replacement applies", id, labels, Players.OptionKind.Other));
            Require(pick >= 0 && pick < options.Count, "Choose one of the listed replacements.");
            _deathReplacement[(id, card.Version)] = options[pick];
        }
    }

    /// <summary>Moves a card between zones. Cards always go to their owner's per-player zones (rule 400.3).</summary>
    /// <param name="tapped">A permanent entering the battlefield tapped (rule 614.1c): it is tapped from the start, with no event.</param>
    /// <param name="kicked">A spell cast with kicker becoming a permanent: it remembers it was kicked (for "if it was kicked").</param>
    private void MoveCard(CardId id, Zone to, bool toBottom = false, PlayerId? controller = null, CardId? attachTo = null, bool kicked = false,
        bool castFromHand = false, bool wasCast = false, int timesKicked = 0, int squadPaid = 0, bool tapped = false)
    {
        bool shuffleAfter = false;
        var enterCounters = new List<(Abilities.CounterKind Kind, int Count)>();
        var card = State.GetCard(id);
        var from = card.Zone;
        var owner = State.GetPlayer(card.Owner);
        var lastController = card.Controller;

        // Replacement effects on where the card goes (rule 614).
        if (to == Zone.Graveyard && from == Zone.Battlefield && DeathReplacements(card) is { Count: > 0 } replacements)
        {
            // Only one applies: the controller chose it before the creature died, or there was no choice (rule 616.1).
            var applied = _deathReplacement.Remove((id, card.Version), out var chosen) && replacements.Contains(chosen) ? chosen : replacements[0];
            if (applied.Fate == DeathFate.Shuffled) { to = Zone.Library; shuffleAfter = true; }
            else to = Zone.Exile;
            // "If a creature an opponent controls would die, exile it instead. When you do, …"
            if (applied.By is { } replacer) Queue(replacer.Id, Abilities.TriggerEvent.CreatureExiledInstead, replacer.Controller);
        }
        else if (to == Zone.Graveyard && State.ExileInsteadOfGraveyard.Remove((id, card.Version)))
        {
            to = Zone.Exile;
        }
        else if (to == Zone.Graveyard)
        {
            if ((card.Definition.Replaces & Cards.Replacements.ShuffleIntoLibraryInsteadOfGraveyard) != 0) { to = Zone.Library; shuffleAfter = true; }
            else if ((card.Is(Cards.CardType.Instant) || card.Is(Cards.CardType.Sorcery))
                     && State.Battlefield.Any(b => (State.GetCard(b).Definition.Replaces & Cards.Replacements.ExileInstantsAndSorceries) != 0))
                to = Zone.Exile;
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
        card.ZoneChangedTurn = State.TurnNumber;
        card.EnteredFrom = from;
        card.Kicked = kicked;
        card.TimesKicked = to == Zone.Battlefield ? timesKicked : 0;
        card.SquadPaid = to == Zone.Battlefield ? squadPaid : 0;
        card.CastFromHand = castFromHand;
        card.WasCast = wasCast;
        card.Zone = to;
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
        // The permanent is on the battlefield with its counters before anything reacts to it entering (rule 614.1c).
        var placedCounters = to == Zone.Battlefield ? PlaceEnterCounters(card, enterCounters) : new List<(Abilities.CounterKind Kind, int Count)>();
        RecomputeContinuousEffects();
        int leavingVersion = card.Version - 1;
        Emit(new CardMoved(id, card.Owner, from, to, lastController));
        AnnounceEnterCounters(card, placedCounters);
        if (shuffleAfter) Shuffle(owner);

        // Cards exiled "until this leaves the battlefield" come back (rule 610.3).
        if (from == Zone.Battlefield)
            foreach (var link in State.LinkedExiles.Where(l => l.Source == id && l.SourceVersion == leavingVersion).ToList())
            {
                State.LinkedExiles.Remove(link);
                var exiled = State.GetCard(link.Exiled);
                if (exiled.Zone == Zone.Exile && exiled.Version == link.ExiledVersion) MoveCard(link.Exiled, Zone.Battlefield, controller: exiled.Owner);
            }

        // A token that leaves the battlefield ceases to exist (rule 111.7, 704.5d).
        if (card.Definition.IsToken && to != Zone.Battlefield && to != Zone.Stack) owner.GetZone(to).Remove(id);
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
            : State.Battlefield.Select(State.GetCard).FirstOrDefault(c => c.Controller != playerId && (c.Definition.Replaces & Cards.Replacements.StealsOpponentsExtraDraws) != 0 && !c.LosesAbilities);
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
            MoveCard(top, Zone.Hand);
            Emit(new CardDrawn(playerId, top));
            // Miracle: the first card a player draws in a turn may be revealed as it's drawn (702.94a).
            if (State.GetCard(top).Definition.Miracle is not null && player.CardsDrawnThisTurn == 1 && State.GetCard(top).Zone == Zone.Hand)
                _pendingTriggers.Add(new PendingTrigger(top, MiracleTrigger, playerId, new TriggerInfo(top, State.GetCard(top).Version, playerId)));
        }
    }

    private void Shuffle(Player player)
    {
        Rng.Shuffle(player.Library);
        Emit(new LibraryShuffled(player.Id));
    }
}
