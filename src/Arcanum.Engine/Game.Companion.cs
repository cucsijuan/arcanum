// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Companion (rule 702.139): before the game, a player may reveal a card from outside the game whose companion condition their
/// starting deck fulfills (103.2b); once during the game, at sorcery timing, they may pay {3} to put it into their hand (116.2g).
/// </summary>
public sealed partial class Game
{
    private static readonly ManaCost CompanionCost = ManaCost.Parse("{3}");

    /// <summary>The cards a player's starting deck is made of: their library and their commanders.</summary>
    private IEnumerable<CardDefinition> StartingDeck(Player player) =>
        player.Library.Concat(State.Cards.Values.Where(c => c.Owner == player.Id && c.Zone == Zone.Command).Select(c => c.Id))
            .Select(id => State.GetCard(id).PrintedDefinition);

    /// <summary>Each player in APNAP order may reveal one companion their starting deck allows (103.2b).</summary>
    private async Task RevealCompanionsAsync()
    {
        foreach (var playerId in State.ApnapOrder().ToList())
        {
            var player = State.GetPlayer(playerId);
            var deck = StartingDeck(player).ToList();
            var candidates = player.Outside.Where(id => State.GetCard(id).PrintedDefinition.Companion is { } condition && condition.FulfilledBy(deck)).ToList();
            if (candidates.Count == 0) continue;
            var pick = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId), new CardChoiceRequest(
                "Reveal a companion? (Once during the game you may pay {3} to put it into your hand.)", null,
                candidates.Select(id => ViewBuilder.Card(State, id, playerId, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
            Require(pick.All(candidates.Contains), "Choose one of your companions or none.");
            if (pick.Count == 0) continue;
            player.Companion = pick[0];
            State.GetCard(pick[0]).KnownTo.UnionWith(State.Players.Select(p => p.Id)); // revealed: everyone knows it
            Emit(new CardsRevealed(playerId, new[] { pick[0] }));
            Emit(new CompanionRevealed(playerId, pick[0]));
        }
    }

    private IEnumerable<PlayerAction> CompanionActions(PlayerId playerId)
    {
        var player = State.GetPlayer(playerId);
        if (player.Companion is { } companion && !player.CompanionTaken && player.Outside.Contains(companion)
            && ManaPayment.FindPlan(State, playerId, CompanionCost) is not null)
            yield return new PutCompanionIntoHand(companion);
    }

    private async Task<bool> PutCompanionIntoHandAsync(Player player, PutCompanionIntoHand action)
    {
        Require(player.Companion == action.Card && !player.CompanionTaken && player.Outside.Contains(action.Card)
                && player.Id == State.ActivePlayer && State.Step.IsMain() && State.Stack.Count == 0,
            "Your companion can be put into your hand once, any time you could cast a sorcery.");
        if (!await PayManaAsync(player, action.Card, CompanionCost, null)) return false;
        player.CompanionTaken = true;
        await MoveCardAsync(action.Card, Zone.Hand);
        Emit(new CompanionPutIntoHand(player.Id, action.Card));
        return true;
    }
}
