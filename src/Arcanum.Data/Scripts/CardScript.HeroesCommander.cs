// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine.Abilities;

namespace Arcanum.Data.Scripts;

/// <summary>Vocabulary for the effects in <see cref="HeroesCommanderEffect"/> (see "Heroes commander decks" in docs/card-scripts.md).</summary>
public static partial class CardScriptParser
{
    /// <summary>
    /// { "adapt": 3 }, { "monstrosity": "X" }, { "explore": "triggered" }, { "connive": subject } (adapt and monstrosity take "what",
    /// default the source), { "sacrificeAllBut": 2, "filter": f, "who": subject }. Null when the effect isn't one of these.
    /// </summary>
    private static Effect? ParseHeroesCommanderEffect(JsonElement e)
    {
        Subject What() => e.TryGetProperty("what", out var w) ? ParseSubject(w) : Subject.Self;
        if (e.TryGetProperty("adapt", out var adapt)) return new Adapt(ParseQuantity(adapt), What());
        if (e.TryGetProperty("monstrosity", out var monstrosity)) return new Monstrosity(ParseQuantity(monstrosity), What());
        if (e.TryGetProperty("explore", out var explore)) return new Explore(ParseSubject(explore));
        if (e.TryGetProperty("connive", out var connive)) return new Connive(ParseSubject(connive));
        if (e.TryGetProperty("opponentSplitsExiled", out _)) return new OpponentSplitsExiled();
        if (e.TryGetProperty("opponentsChooseFromYourGraveyard", out var ocg)) return new OpponentsChooseFromYourGraveyard(ParseFilter(ocg, ControllerFilter.Any));
        if (e.TryGetProperty("exileRandomFromOpponentsGraveyards", out _)) return new ExileRandomFromOpponentsGraveyards();
        if (e.TryGetProperty("castExiledFreeAnyNumber", out _))
            return new CastExiledFreeAnyNumber(e.TryGetProperty("ownersLoseLife", out var oll) && oll.GetBoolean());
        if (e.TryGetProperty("chooseOneOf", out var coo)) return new ChooseOneOf(ParseSubject(coo));
        if (e.TryGetProperty("exileWithSameNameTokens", out var ewsn)) return new ExileWithSameNameTokens(ParseSubject(ewsn));
        if (e.TryGetProperty("putExiledWithThis", out var pew))
            return new PutExiledWithThis(ParseFilter(pew, ControllerFilter.Any),
                e.TryGetProperty("counterKinds", out var pek) ? pek.EnumerateArray().Select(k => ParseCounterKind(k.GetString())).ToList() : Array.Empty<CounterKind>());
        if (e.TryGetProperty("whenDiesThisTurn", out var wdt))
            return new WhenDiesThisTurn(ParseSubject(wdt), EffectList(e.GetProperty("effects")), e.TryGetProperty("text", out var wdx) ? wdx.GetString()! : "When that creature dies this turn");
        if (e.TryGetProperty("attackTaxUntilYourNextTurn", out var atx)) return new AttackTaxUntilYourNextTurn(Arcanum.Engine.Mana.ManaCost.Parse(atx.GetString()!));
        if (e.TryGetProperty("distributeCountersFromSelf", out var dcs)) return new DistributeSourceCounters(ParseCounterKind(dcs.GetString()));
        if (e.TryGetProperty("sacrificeAllBut", out var sab))
            return new SacrificeAllBut(e.TryGetProperty("filter", out var sabf) ? ParseFilter(sabf, ControllerFilter.Any) : ObjectFilter.Anything, sab.GetInt32(),
                e.TryGetProperty("who", out var sabw) ? ParseSubject(sabw) : Subject.You);
        return null;
    }

    /// <summary>
    /// "colorsAmongYourPermanents", "counterKindsOnSelf", { "manaValue": "exiled" }, { "diff": [a, b] }. Null when the quantity isn't one of these.
    /// </summary>
    private static Quantity? ParseHeroesCommanderQuantity(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() switch
            {
                "colorsAmongYourPermanents" => new Quantity(0, QuantityKind.ColorsAmongYourPermanents),
                "counterKindsOnSelf" => new Quantity(0, QuantityKind.CounterKindsOnSource),
                "playersWhoLostLifeThisTurn" => new Quantity(0, QuantityKind.PlayersWhoLostLifeThisTurn),
                "greatestPowerAsCast" => new Quantity(0, QuantityKind.GreatestPowerAsCast),
                "triggeredCounterCount" => new Quantity(0, QuantityKind.TriggeredCounterCount),
                "sacrificedManaValue" => new Quantity(0, QuantityKind.SacrificedManaValue),
                "opponentsCombatDamagedBySelfOrDragon" => new Quantity(0, QuantityKind.OpponentsCombatDamagedBySourceOrDragon),
                _ => null,
            };
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("manaValue", out var mv) && mv.ValueKind == JsonValueKind.String && mv.GetString() == "exiled")
            return new Quantity(0, QuantityKind.ExiledManaValue);
        if (e.TryGetProperty("enteredThisTurn", out var ett))
            return new Quantity(0, QuantityKind.EnteredThisTurnCount, ParseFilter(ett, ControllerFilter.Any));
        if (e.TryGetProperty("opponentsControlling", out var opc))
            return new Quantity(0, QuantityKind.OpponentsControlling, ParseFilter(opc, ControllerFilter.Any));
        if (e.TryGetProperty("diff", out var diff))
            return new Quantity(0, QuantityKind.Difference) { Parts = diff.EnumerateArray().Select(ParseQuantity).ToList() };
        return null;
    }
}
