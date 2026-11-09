// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Morph (rule 702.37) and face-down spells and permanents (rule 708): cast face down as a 2/2 creature for {3}, turned face up
/// for its morph cost as a special action any time its controller has priority.
/// </summary>
public sealed partial class Game
{
    /// <summary>What casting a card face down costs (702.37c).</summary>
    private static readonly ManaCost FaceDownCastCost = ManaCost.Parse("{3}");

    /// <summary>Works something out as if the card were face down (its costs, the way a face-down spell is paid for).</summary>
    private T AsFaceDown<T>(Card card, Func<T> work)
    {
        bool before = card.TurnedFaceDown;
        card.TurnedFaceDown = true;
        try { return work(); }
        finally { card.TurnedFaceDown = before; }
    }

    /// <summary>Face-down permanents the player controls that they can turn face up now, paying their morph costs (702.37e, 116.2b).</summary>
    private IEnumerable<PlayerAction> TurnFaceUpActions(PlayerId playerId)
    {
        bool Affordable(ManaCost cost) => AffordableForSpecialAction(playerId, cost, unlock: false);
        foreach (var c in State.PermanentsControlledBy(playerId).Where(c => c.TurnedFaceDown && !CantBeTurnedFaceUp(c)).ToList())
        {
            if (c.PrintedDefinition.Morph is { } morph && Affordable(morph)) yield return new TurnFaceUp(c.Id);
            if (ManifestTurnUpCost(c) is { } cost && Affordable(cost)) yield return new TurnFaceUp(c.Id, ForManaCost: true);
        }
    }

    /// <summary>An Aura on it says "as long as enchanted creature is face down, it can't be turned face up".</summary>
    private bool CantBeTurnedFaceUp(Card card) =>
        State.Battlefield.Select(State.GetCard).Any(a => a.AttachedTo == card.Id && a.HasTextAbilities && (a.Definition.ExtraRules & Cards.ExtraRules.EnchantedCantBeTurnedFaceUp) != 0);

    /// <summary>A manifested creature card's mana cost, which turns it face up (701.40b; X is 0); null for any other card.</summary>
    private static ManaCost? ManifestTurnUpCost(Card card) =>
        card.Manifested && card.PrintedDefinition.IsCreature() ? card.PrintedDefinition.ManaCost.WithX(0) : null;

    /// <summary>
    /// Manifest (rule 701.40a): the player puts the top card of their library onto the battlefield face down as a 2/2 creature.
    /// </summary>
    private async Task<CardId?> ManifestTopAsync(PlayerId player)
    {
        var library = State.GetPlayer(player).Library;
        return library.Count == 0 ? null : await ManifestAsync(player, library[0]);
    }

    /// <summary>
    /// Manifest dread (rule 701.62a): the player looks at the top two cards of their library, manifests one of them, then puts the other
    /// into their graveyard. With one card left, that one is manifested.
    /// </summary>
    private async Task<CardId?> ManifestDreadAsync(PlayerId player, CardId source)
    {
        var looked = State.GetPlayer(player).Library.Take(2).ToList();
        foreach (var id in looked) State.GetCard(id).KnownTo.Add(player);
        CardId? made = null;
        var rest = new List<CardId>();
        if (looked.Count > 0)
        {
            var pick = looked.Count == 1 ? looked : await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                "Manifest dread: choose the card to manifest (the other goes into your graveyard)", source,
                looked.Select(id => ViewBuilder.Card(State, id, player)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && looked.Contains(pick[0]), "Choose one of the two cards.");
            made = await ManifestAsync(player, pick[0]);
            rest = looked.Where(id => id != pick[0] && State.GetCard(id).Zone == Zone.Library).ToList();
            BeginSimultaneous();
            foreach (var id in rest) await MoveCardAsync(id, Zone.Graveyard);
            EndSimultaneous();
        }
        Emit(new ManifestedDread(player, made, rest));
        return made;
    }

    /// <summary>Manifest (rule 701.40a): the card goes onto the battlefield face down as a 2/2 creature under the player's control.</summary>
    private async Task<CardId?> ManifestAsync(PlayerId player, CardId top)
    {
        _enteringFaceDown = top;
        try { await MoveCardAsync(top, Zone.Battlefield, controller: player); }
        finally { _enteringFaceDown = null; }
        var card = State.GetCard(top);
        if (card.Zone != Zone.Battlefield) return null;
        card.Manifested = true;
        Emit(new Manifested(player, top));
        return top;
    }

    /// <summary>
    /// Turning a face-down permanent face up: pay its morph cost, then it has its own characteristics (702.37e). This doesn't use the
    /// stack; the permanent stays the same object, with its counters, damage and status.
    /// </summary>
    private async Task<bool> TurnFaceUpAsync(Player player, TurnFaceUp action)
    {
        var card = State.GetCard(action.Card);
        var cost = action.ForManaCost ? ManifestTurnUpCost(card) : card.PrintedDefinition.Morph;
        Require(card.Zone == Zone.Battlefield && card.TurnedFaceDown && card.Controller == player.Id && cost is not null && !CantBeTurnedFaceUp(card),
            action.ForManaCost ? "Only a manifested creature card you control can be turned face up for its mana cost."
                : "Only a face-down permanent you control with morph can be turned face up.");
        if (!await PayForSpecialActionAsync(player, card.Id, cost!, unlock: false)) return false;
        card.TurnedFaceDown = false;
        // Megamorph: as it's turned face up, if its megamorph cost was paid, it gets a +1/+1 counter (702.37b).
        if (!action.ForManaCost && card.PrintedDefinition.Megamorph) PutCounters(card, Abilities.CounterKind.PlusOnePlusOne, 1, player.Id);
        RecomputeContinuousEffects();
        Emit(new TurnedFaceUp(card.Id, player.Id));
        player.TurnedFaceUpTurn = State.TurnNumber;
        return true;
    }

    /// <summary>"Reveal target face-down permanent. If it's a creature card, you may turn it face up" (without paying a cost).</summary>
    private async Task RevealFaceDownAsync(RevealFaceDown effect, EffectContext ctx)
    {
        foreach (var card in CardsFor(effect.What, ctx).Where(c => c.Zone == Zone.Battlefield && c.TurnedFaceDown).ToList())
        {
            foreach (var p in State.Players) card.KnownTo.Add(p.Id);
            Emit(new CardsRevealed(card.Controller, new[] { card.Id }));
            if (!card.PrintedDefinition.IsCreature() || CantBeTurnedFaceUp(card)) continue;
            if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Turn {card.PrintedDefinition.Name} face up?", card.Id))) continue;
            card.TurnedFaceDown = false;
            RecomputeContinuousEffects();
            Emit(new TurnedFaceUp(card.Id, ctx.Controller));
            State.GetPlayer(ctx.Controller).TurnedFaceUpTurn = State.TurnNumber;
        }
    }
}
