// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Moves a card between zones. Cards always go to their owner's per-player zones (rule 400.3).</summary>
    /// <param name="kicked">A spell cast with kicker becoming a permanent: it remembers it was kicked (for "if it was kicked").</param>
    private void MoveCard(CardId id, Zone to, bool toBottom = false, PlayerId? controller = null, CardId? attachTo = null, bool kicked = false)
    {
        bool shuffleAfter = false;
        var card = State.GetCard(id);
        var from = card.Zone;
        var owner = State.GetPlayer(card.Owner);
        var lastController = card.Controller;

        // Replacement effects on where the card goes (rule 614).
        if (to == Zone.Graveyard)
        {
            if (from == Zone.Battlefield && State.ExileIfDies.Contains((id, card.Version))) to = Zone.Exile;
            else if ((card.Definition.Replaces & Cards.Replacements.ShuffleIntoLibraryInsteadOfGraveyard) != 0) { to = Zone.Library; shuffleAfter = true; }
            else if ((card.Is(Cards.CardType.Instant) || card.Is(Cards.CardType.Sorcery))
                     && State.Battlefield.Any(b => (State.GetCard(b).Definition.Replaces & Cards.Replacements.ExileInstantsAndSorceries) != 0))
                to = Zone.Exile;
        }

        switch (from)
        {
            case Zone.Battlefield:
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
        card.Kicked = kicked;
        card.Zone = to;
        NoteCommanderMove(card, to);
        switch (to)
        {
            case Zone.Battlefield:
                card.Controller = controller ?? card.Owner;
                card.AttachedTo = attachTo;
                // Replacement effects that modify how the permanent enters (rule 614.1c).
                if (card.Definition.EntersTapped) card.Tapped = true;
                if (card.IsCreature && OpponentsCreaturesEnterTapped(card.Controller)) card.Tapped = true;
                if (card.Definition.EntersWithCounters > 0
                    && (card.Definition.EntersWithCountersIf is not { } cond || Holds(cond, card.Controller, card)))
                    card.Counters[Abilities.CounterKind.PlusOnePlusOne] = card.Definition.EntersWithCounters;
                if (card.Definition.EntersWithCountersFrom is { } countFrom)
                {
                    int n = Eval(countFrom, new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()));
                    if (n > 0) card.Counters[Abilities.CounterKind.PlusOnePlusOne] = card.CounterCount(Abilities.CounterKind.PlusOnePlusOne) + n;
                }
                State.Battlefield.Add(id);
                if (card.Definition.ChooseOnEnter != Cards.EnterChoice.None) State.PendingEnterChoices.Add((id, card.Version));
                if (card.Definition.Loyalty is { } loyalty) card.Counters[Abilities.CounterKind.Loyalty] = loyalty; // 306.5b
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
        RecomputeContinuousEffects();
        int leavingVersion = card.Version - 1;
        Emit(new CardMoved(id, card.Owner, from, to, lastController));
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

    private void Draw(PlayerId playerId, int count = 1)
    {
        var player = State.GetPlayer(playerId);
        for (int i = 0; i < count; i++)
        {
            if (player.Library.Count == 0)
            {
                player.AttemptedDrawFromEmptyLibrary = true; // loses at next SBA check (rule 704.5b)
                return;
            }
            var top = player.Library[0];
            MoveCard(top, Zone.Hand);
            Emit(new CardDrawn(playerId, top));
        }
    }

    private void Shuffle(Player player)
    {
        Rng.Shuffle(player.Library);
        Emit(new LibraryShuffled(player.Id));
    }
}
