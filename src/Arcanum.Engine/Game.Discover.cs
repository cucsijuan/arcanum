// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Discover (rule 701.57) and "exile until a nonland card; cast it free if its mana value is N or less, or put it into your hand".</summary>
public sealed partial class Game
{
    private async Task DiscoverAsync(DiscoverCards discover, EffectContext ctx)
    {
        int n = Eval(discover.ManaValue, ctx);
        var player = State.GetPlayer(ctx.Controller);
        var exiled = new List<CardId>();
        CardId? hit = null;
        while (player.Library.Count > 0)
        {
            var top = player.Library[0];
            await MoveCardAsync(top, Zone.Exile);
            exiled.Add(top);
            var card = State.GetCard(top);
            if (!card.Is(CardType.Land) && (discover.AnyNonland || card.ManaValue <= n)) { hit = top; break; }
        }
        ctx.Results.DiscoverTarget = n;
        if (hit is { } found)
        {
            var card = State.GetCard(found);
            ctx.Results.Discovered = (found, card.ManaValue);
            bool castable = card.ManaValue <= n && HasLegalTargets(CastingTargets(card.Definition), ctx.Controller, found);
            if (castable && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                    new YesNoRequest($"Cast {card.Name} without paying its mana cost? (No: put it into your hand)", found)))
                await CastNowWithoutPayingAsync(player, found);
            else if (card.Zone == Zone.Exile)
                await MoveCardAsync(found, Zone.Hand);
        }
        if (discover.RestStaysExiled) return;
        var rest = exiled.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
        Rng.Shuffle(rest);
        foreach (var id in rest) await MoveCardAsync(id, Zone.Library, toBottom: true);
        PlacedInUnseenOrder(rest, null);
    }
}
