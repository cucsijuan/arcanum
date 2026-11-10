// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine.Abilities;

namespace Arcanum.Data.Scripts;

public static partial class CardScriptParser
{
    /// <summary>
    /// The academy set's quantities: "cardsDrawnThisTurn", "triggeredSpellX", "manaSpentOnThis", "manaSpentOnTarget",
    /// "affectedHalfLifeRoundedDown", "affectedHalfHandRoundedDown", "affectedHalfPermanentsRoundedDown", "triggeredSpellColorsSpent", "targetHandSize", { "twoToThe": quantity } and { "differentlyNamed": filter }. Null when the quantity isn't one of these.
    /// </summary>
    private static Quantity? ParseAcademyQuantity(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() switch
            {
                "cardsDrawnThisTurn" => new Quantity(0, QuantityKind.CardsYouDrewThisTurn),
                "triggeredSpellX" => new Quantity(0, QuantityKind.TriggeredSpellX),
                "manaSpentOnThis" => new Quantity(0, QuantityKind.ManaSpentOnSource),
                "manaSpentOnTarget" => new Quantity(0, QuantityKind.ManaSpentOnTarget),
                "affectedHalfLifeRoundedDown" => new Quantity(0, QuantityKind.AffectedHalfLifeRoundedDown),
                "affectedHalfHandRoundedDown" => new Quantity(0, QuantityKind.AffectedHalfHandRoundedDown),
                "affectedHalfPermanentsRoundedDown" => new Quantity(0, QuantityKind.AffectedHalfPermanentsRoundedDown),
                "triggeredSpellColorsSpent" => new Quantity(0, QuantityKind.TriggeredSpellColorsSpent),
                "targetHandSize" => new Quantity(0, QuantityKind.TargetHandSize),
                "yourNontokenCreaturesDestroyedThisWay" => new Quantity(0, QuantityKind.YourNontokenCreaturesDestroyedThisWay),
                _ => null,
            };
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("differentlyNamed", out var named))
            return new Quantity(0, QuantityKind.DifferentlyNamedYouControl, ParseFilter(named, ControllerFilter.You));
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("twoToThe", out var power))
            return new Quantity(0, QuantityKind.TwoToThe) { Parts = new[] { ParseQuantity(power) } };
        return null;
    }

    /// <summary>
    /// The academy set's effects: { "atYourNextMainPhase": { "effects": [...], "text": "..." }, "amount": quantity } and
    /// { "moveCountersOfKind": { "from": subject, "to": subject }, "count": quantity, "kind": "+1/+1" },
    /// { "flipCoinsSkipTurns": 5, "who": "target" }, { "chooseLandCardName": true }. Null when the effect isn't one of these.
    /// </summary>
    private static Effect? ParseAcademyEffect(JsonElement e)
    {
        if (e.TryGetProperty("atYourNextMainPhase", out var next))
            return new AtYourNextMainPhase(new TriggeredAbility
                {
                    Trigger = TriggerEvent.NextUpkeep,
                    Effects = EffectList(next.GetProperty("effects")),
                    Text = next.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                },
                e.TryGetProperty("amount", out var amount) ? ParseQuantity(amount) : null);
        // { "whenDealsCombatDamageThisCombat": subject, "effects": [...], "text": "..." }: "whenever that creature deals combat damage to a player this combat".
        if (e.TryGetProperty("whenDealsCombatDamageThisCombat", out var wdc))
            return new WhenDealsCombatDamageThisCombat(ParseSubject(wdc), new TriggeredAbility
            {
                Trigger = TriggerEvent.DealsCombatDamageToPlayer,
                Effects = EffectList(e.GetProperty("effects")),
                Text = e.TryGetProperty("text", out var wdt) ? wdt.GetString() ?? "" : "",
            });
        if (e.TryGetProperty("doubleTriggeredSpellX", out _)) return new DoubleTriggeredSpellX();
        if (e.TryGetProperty("exileTopAsYouChooseCastIfTotalAtMost", out var etac)) return new ExileTopAsYouChooseCastIfTotalAtMost(etac.GetInt32());
        if (e.TryGetProperty("eachExilesUntilNonlandOpponentPicks", out var eeun)) return new EachExilesUntilNonlandOpponentPicks(eeun.GetInt32());
        if (e.TryGetProperty("exiledPilesOpponentChooses", out var epoc)) return new ExiledPilesOpponentChooses(epoc.GetInt32());
        if (e.TryGetProperty("copyForEachOtherCreatureItCouldTarget", out _)) return new CopyForEachOtherCreatureItCouldTarget();
        if (e.TryGetProperty("castExiledFreeThisTurnBottomInstead", out var cefb)) return new CastExiledFreeThisTurnBottomInstead(ParseSubject(cefb));
        if (e.TryGetProperty("returnCopiedCardToGraveyard", out _)) return new ReturnCopiedCardToGraveyard();
        if (e.TryGetProperty("whenYouNextCastThisTurn", out var wnc))
            return new WhenYouNextCastThisTurn(wnc.TryGetProperty("spells", out var wns) ? ParseFilter(wns, ControllerFilter.Any) : ObjectFilter.Anything, new TriggeredAbility
            {
                Trigger = TriggerEvent.YouCastSpell, Effects = EffectList(wnc.GetProperty("effects")),
                Text = wnc.TryGetProperty("text", out var wnt) ? wnt.GetString() ?? "" : "",
            });
        if (e.TryGetProperty("drawTwoPayOrReturnDrawn", out _)) return new DrawTwoPayOrReturnDrawn();
        if (e.TryGetProperty("exileFromHandUntilLeaves", out var efh))
            return new ExileFromHandUntilLeaves(e.TryGetProperty("of", out var efo) ? ParseSubject(efo) : Subject.TargetAt(0), ParseFilter(efh, ControllerFilter.Any));
        if (e.TryGetProperty("solve", out _)) return new SolveCase();
        if (e.TryGetProperty("castFreeNow", out var cfn)) return new CastFreeNow(ParseSubject(cfn), e.TryGetProperty("most", out var cfm) ? cfm.GetInt32() : 1);
        if (e.TryGetProperty("revealToHandLoseLifeRepeat", out _)) return new RevealToHandLoseLifeRepeat();
        if (e.TryGetProperty("cantLoseGameThisTurn", out _)) return new CantLoseGameThisTurn();
        if (e.TryGetProperty("damageCantReduceLifeBelowOneThisTurn", out _)) return new DamageCantReduceLifeBelowOneThisTurn();
        if (e.TryGetProperty("yourSpellsCantBeCounteredThisTurn", out _)) return new YourSpellsCantBeCounteredThisTurn();
        if (e.TryGetProperty("hexproofFromColorsThisTurn", out var hfc)) return new HexproofFromColorsThisTurn(hfc.EnumerateArray().Select(x => x.GetString()!).ToList());
        if (e.TryGetProperty("controllersSearchBasicPerExiled", out _)) return new ControllersSearchBasicPerExiled();
        if (e.TryGetProperty("revealTopDuel", out var rtd)) return new RevealTopDuel(ParseSubject(rtd));
        if (e.TryGetProperty("animateEnchantedCard", out _)) return new AnimateEnchantedCard();
        if (e.TryGetProperty("sacrificeAnimated", out _)) return new AnimateEnchantedCard(Sacrifice: true);
        // { "whenYourCreatureDealsCombatDamageThisTurn": subject, "effects": [...], "text": "..." }: "triggered" in the effects is that object.
        if (e.TryGetProperty("whenYourCreatureDealsCombatDamageThisTurn", out var wyc))
            return new WhenYourCreatureDealsCombatDamageThisTurn(ParseSubject(wyc), new TriggeredAbility
            {
                Trigger = TriggerEvent.DealsCombatDamageToPlayer,
                Effects = EffectList(e.GetProperty("effects")),
                Text = e.TryGetProperty("text", out var wyt) ? wyt.GetString() ?? "" : "",
            });
        if (e.TryGetProperty("chooseLandCardName", out _)) return new ChooseLandCardName();
        if (e.TryGetProperty("flipCoinsSkipTurns", out var fcs))
            return new FlipCoinsSkipTurns(fcs.GetInt32(), e.TryGetProperty("who", out var fcw) ? ParseSubject(fcw) : Subject.TargetAt(0));
        if (e.TryGetProperty("moveCountersOfKind", out var mck))
            return new MoveCountersOfKind(ParseSubject(mck.GetProperty("from")), ParseSubject(mck.GetProperty("to")),
                e.TryGetProperty("count", out var mcc) ? ParseQuantity(mcc) : 1,
                e.TryGetProperty("kind", out var mckk) ? ParseCounterKind(mckk.GetString()) : CounterKind.PlusOnePlusOne);
        return null;
    }
}
