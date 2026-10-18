// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Quantities for loyalty, domain, powers, graveyards and draws.</summary>
public sealed partial class Game
{
    private static readonly string[] BasicLandTypeNames = { "Plains", "Island", "Swamp", "Mountain", "Forest" };

    private int EvalFracture(Quantity q, EffectContext ctx) => q.Kind switch
    {
        QuantityKind.TargetLoyalty => ctx.TargetAt(0)?.Card is { } t ? State.GetCard(t).CounterCount(CounterKind.Loyalty) : 0,
        QuantityKind.DistinctPowersAmongYourCreatures => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature).Select(c => c.Power).Distinct().Count(),
        QuantityKind.GreatestManaValueInYourGraveyard => State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard)
            .Where(c => q.Filter is null || Matches(q.Filter, c, c.Owner, ctx.Source, ctx.Controller)).Select(ManaValueOf).DefaultIfEmpty(0).Max(),
        QuantityKind.TargetPlayerMilledThisTurn => ctx.TargetAt(0)?.Player is { } p ? State.GetPlayer(p).MilledThisTurn : 0,
        QuantityKind.DrawnThisWay => ctx.Results.Drawn,
        QuantityKind.Domain => BasicLandTypeNames.Count(type => State.PermanentsControlledBy(ctx.Controller).Any(c => c.Is(CardType.Land) && c.HasSubtype(type))),
        QuantityKind.DiscardedThisTurn => State.GetPlayer(ctx.Controller).CardsDiscardedThisTurn,
        QuantityKind.PlaneswalkerTypesAmongYours => State.PermanentsControlledBy(ctx.Controller).Where(c => c.Is(CardType.Planeswalker))
            .SelectMany(c => c.CurrentSubtypes).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        QuantityKind.OpponentsWhoDiscardedFewer => State.OpponentsOf(ctx.Controller).Count(o => ctx.Results.Discarded.Select(State.GetCard)
            .Count(c => c.Owner == o && (q.Filter is null || Matches(q.Filter, c, o, ctx.Source, ctx.Controller))) < q.Value),
        _ => 0,
    };
}
