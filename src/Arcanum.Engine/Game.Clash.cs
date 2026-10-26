// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Clash (rule 701.30).</summary>
public sealed partial class Game
{
    private static string OrdinalPosition(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => $"{n}th" };

    /// <summary>
    /// The controller clashes with an opponent they choose: both reveal the top card of their library; each, in APNAP order,
    /// chooses top or bottom for theirs, then both are put there (rule 101.4). The controller wins if their card's mana value is
    /// greater than the opponent's; a player with an empty library reveals nothing, which any card beats.
    /// </summary>
    private async Task<bool> ClashAsync(EffectContext ctx)
    {
        if (!State.OpponentsOf(ctx.Controller).Any()) return false;
        var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent to clash with") ?? State.OpponentsOf(ctx.Controller).First();
        var clashers = new[] { ctx.Controller, opponent };
        var revealed = clashers.ToDictionary(p => p, p => State.GetPlayer(p).Library.Count > 0 ? State.GetPlayer(p).Library[0] : (CardId?)null);
        foreach (var (player, card) in revealed)
            if (card is { } shown) Emit(new CardsRevealed(player, new[] { shown }));
        int ValueOf(PlayerId p) => revealed[p] is { } c ? State.GetCard(c).ManaValue : -1;
        PlayerId? winner = ValueOf(ctx.Controller) > ValueOf(opponent) ? ctx.Controller : ValueOf(opponent) > ValueOf(ctx.Controller) ? opponent : null;
        Emit(new Clashed(ctx.Controller, opponent, revealed[ctx.Controller], revealed[opponent], winner));
        var toBottom = new List<CardId>();
        foreach (var player in State.ApnapOrder().Where(clashers.Contains))
        {
            if (revealed[player] is not { } card) continue;
            var pick = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                "Clash: choose it to put it on the bottom of your library (otherwise it stays on top)", ctx.Source.Id,
                new[] { ViewBuilder.Card(State, card, player, reveal: true) }, 0, 1, CardChoicePurpose.ScryToBottom));
            Require(pick.All(id => id == card), "Choose the revealed card or nothing.");
            if (pick.Count == 1) toBottom.Add(card);
        }
        foreach (var card in toBottom)
        {
            var library = State.GetPlayer(State.GetCard(card).Owner).Library;
            library.Remove(card);
            library.Add(card);
        }
        return winner == ctx.Controller;
    }
}
