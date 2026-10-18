// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine.Abilities;

namespace Arcanum.Data.Scripts;

public static partial class CardScriptParser
{
    /// <summary>
    /// "drawnThisWay", "domain", "distinctPowers", { "loyalty": "target" }, { "greatestManaValueInGraveyard": filter },
    /// { "milledThisTurn": "target" }. Null when the quantity isn't one of these.
    /// </summary>
    private static Quantity? ParseFractureQuantity(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() switch
            {
                "drawnThisWay" => new Quantity(0, QuantityKind.DrawnThisWay),
                "domain" => new Quantity(0, QuantityKind.Domain),
                "discardedThisTurn" => new Quantity(0, QuantityKind.DiscardedThisTurn),
                "planeswalkerTypes" => new Quantity(0, QuantityKind.PlaneswalkerTypesAmongYours),
                "distinctPowers" => new Quantity(0, QuantityKind.DistinctPowersAmongYourCreatures),
                _ => null,
            };
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("loyalty", out var l) && l.GetString() == "target") return new Quantity(0, QuantityKind.TargetLoyalty);
        if (e.TryGetProperty("greatestManaValueInGraveyard", out var g))
            return new Quantity(0, QuantityKind.GreatestManaValueInYourGraveyard, ParseFilter(g, ControllerFilter.Any));
        if (e.TryGetProperty("opponentsWhoDiscardedFewer", out var odf))
            return new Quantity(odf.GetInt32(), QuantityKind.OpponentsWhoDiscardedFewer, e.TryGetProperty("filter", out var odff) ? ParseFilter(odff, ControllerFilter.Any) : null);
        if (e.TryGetProperty("milledThisTurn", out var m) && m.GetString() == "target") return new Quantity(0, QuantityKind.TargetPlayerMilledThisTurn);
        return null;
    }
}
