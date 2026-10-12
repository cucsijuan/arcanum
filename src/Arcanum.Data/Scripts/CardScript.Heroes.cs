// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine.Abilities;

namespace Arcanum.Data.Scripts;

/// <summary>Vocabulary for the effects in <see cref="HeroesEffect"/> (see "Heroes, summons and jobs" in docs/card-scripts.md).</summary>
public static partial class CardScriptParser
{
    /// <summary>
    /// { "searchLibraryOrGraveyard": filter }, { "putSourceCountersOn": "target" }, { "attachFormerlyAttached": "target", "filter": f }.
    /// Null when the effect isn't one of these.
    /// </summary>
    private static Effect? ParseHeroesEffect(JsonElement e)
    {
        if (e.TryGetProperty("flipCoin", out _))
            return new FlipCoin(e.TryGetProperty("won", out var won) ? EffectList(won) : Array.Empty<Effect>(), e.TryGetProperty("lost", out var lost) ? EffectList(lost) : Array.Empty<Effect>());
        if (e.TryGetProperty("copyAbility", out var cab))
            return new CopyAbility(ParseSubject(cab), e.TryGetProperty("count", out var cac) ? ParseQuantity(cac) : 1);
        if (e.TryGetProperty("additionalEndStep", out var aes)) return new AdditionalEndStep(aes.ValueKind == JsonValueKind.String && aes.GetString() == "ifFirst");
        if (e.TryGetProperty("unattachWhenControlLost", out var uwc)) return new UnattachWhenControlLost(ParseSubject(uwc));
        if (e.TryGetProperty("revealAndAlternate", out var raa)) return new RevealAndAlternate(raa.GetInt32());
        if (e.TryGetProperty("rememberChosen", out _)) return new RememberChosen();
        if (e.TryGetProperty("exileTop", out var ext))
            return new ExileTop(ParseQuantity(ext), e.TryGetProperty("who", out var exw) ? ParseSubject(exw) : Subject.You)
            {
                PlayableDuringLoreTurns = e.TryGetProperty("playableDuringLoreTurns", out var pdl) && pdl.GetBoolean(),
            };
        if (e.TryGetProperty("nextCreatureSpell", out var ncs))
            return new NextCreatureSpell(ncs.TryGetProperty("haste", out var nh) && nh.GetBoolean(), ncs.TryGetProperty("counters", out var nc) ? nc.GetInt32() : 0);
        if (e.TryGetProperty("eachExilesTopPlayLesser", out _)) return new EachExilesTopPlayLesser();
        if (e.TryGetProperty("removeCounterFromAnyNumber", out var rca))
            return new RemoveCounterFromAnyNumber(ParseFilter(rca, ControllerFilter.You), ParseCounterKind(e.TryGetProperty("kind", out var rck) ? rck.GetString() : null));
        if (e.TryGetProperty("exileRandomPermanentCardCopyTapped", out _)) return new ExileRandomPermanentCardCopyTapped();
        if (e.TryGetProperty("countersUpTo", out var cut))
            return new CountersUpTo(cut.GetInt32(), ParseCounterKind(e.TryGetProperty("kind", out var cuk) ? cuk.GetString() : null), e.TryGetProperty("what", out var cuw) ? ParseSubject(cuw) : Subject.Self);
        if (e.TryGetProperty("attachAnyNumber", out var aan)) return new AttachAnyNumber(ParseFilter(aan, ControllerFilter.You), e.TryGetProperty("to", out var aat) ? ParseSubject(aat) : Subject.TargetAt(0));
        if (e.TryGetProperty("shuffleFromGraveyard", out var sfg)) return new ShuffleFromGraveyard(ParseFilter(sfg, ControllerFilter.Any));
        if (e.TryGetProperty("lookHandLandsGraveyard", out var lhl)) return new LookHandLandsGraveyard(ParseQuantity(lhl));
        if (e.TryGetProperty("doubleDamageUntilYourNextTurn", out var ddy)) return new DoubleDamageToPlayerUntilYourNextTurn(ParseSubject(ddy));
        if (e.TryGetProperty("eachDealsDamageEqualToPower", out var edp)) return new EachDealsDamageEqualToPower(ParseSubject(edp), e.TryGetProperty("to", out var edt) ? ParseSubject(edt) : Subject.TargetAt(0));
        if (e.TryGetProperty("doublePower", out var dpw)) return new MultiplyPower(ParseSubject(dpw), 2, false);
        if (e.TryGetProperty("triplePowerAndToughness", out var tpt)) return new MultiplyPower(ParseSubject(tpt), 3, true);
        if (e.TryGetProperty("entersWithAdditionalCounters", out var ewac)) return new EntersWithAdditionalCounters(ParseSubject(ewac), ParseQuantity(e.GetProperty("count")));
        if (e.TryGetProperty("thisSpellOntoBattlefieldTransformed", out var tsb))
            return new ThisSpellOntoBattlefieldTransformed(tsb.ValueKind == JsonValueKind.String ? ParseCounterKind(tsb.GetString()) : null);
        if (e.TryGetProperty("unattachAtNextEndStep", out _)) return new UnattachAtNextEndStep();
        if (e.TryGetProperty("castExiledNow", out var cen))
            return new CastExiledNow(ParseSubject(cen), e.TryGetProperty("else", out var cel) ? EffectList(cel) : Array.Empty<Effect>());
        if (e.TryGetProperty("playableThisTurn", out var ptt))
            return new PlayableThisTurn(ParseSubject(ptt), e.TryGetProperty("anyMana", out var pam) && pam.GetBoolean());
        if (e.TryGetProperty("meld", out _)) return new Meld();
        if (e.TryGetProperty("handToTop", out var htt)) return new HandToTop(htt.GetInt32());
        if (e.TryGetProperty("commanderToHand", out _)) return new CommanderToHand();
        if (e.TryGetProperty("damageRandomOpponent", out var dro)) return new DamageRandomOpponent(ParseQuantity(dro));
        if (e.TryGetProperty("addManaOneColor", out var amo1)) return new AddManaOfOneColor(ParseQuantity(amo1), e.TryGetProperty("optional", out var amop) && amop.GetBoolean());
        if (e.TryGetProperty("rollDie", out var rd)) return new RollDie(rd.GetInt32());
        if (e.TryGetProperty("exileUntilLesserCastFree", out var eul)) return new ExileUntilLesserCastFree(ParseFilter(eul, ControllerFilter.Any));
        if (e.TryGetProperty("revealOnePerCardType", out var rop)) return new RevealOnePerCardType(rop.GetInt32());
        if (e.TryGetProperty("ninjutsuEnter", out _)) return new NinjutsuEnter();
        if (e.TryGetProperty("searchLibraryOrGraveyard", out var slg)) return new SearchLibraryOrGraveyard(ParseFilter(slg, ControllerFilter.Any));
        if (e.TryGetProperty("putSourceCountersOn", out var psc)) return new PutSourceCountersOn(ParseSubject(psc));
        if (e.TryGetProperty("attachFormerlyAttached", out var afa))
            return new AttachFormerlyAttached(ParseSubject(afa), e.TryGetProperty("filter", out var aff) ? ParseFilter(aff, ControllerFilter.Any) : null);
        return null;
    }

    /// <summary>{ "manaValueSum": filter }, "controlGainedThisWay". Null when the quantity isn't one of these.</summary>
    private static Quantity? ParseHeroesQuantity(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() switch
            {
                "controlGainedThisWay" => new Quantity(0, QuantityKind.ControlGainedThisWay),
                "foundManaValue" => new Quantity(0, QuantityKind.FoundManaValue),
                "discardedManaValue" => new Quantity(0, QuantityKind.DiscardedManaValue),
                "cardTypesAmongDiscarded" => new Quantity(0, QuantityKind.CardTypesAmongDiscarded),
                "creaturesDiedThisTurn" => new Quantity(0, QuantityKind.CreaturesDiedThisTurn),
                "countersRemovedThisWay" => new Quantity(0, QuantityKind.CountersRemovedThisWay),
                "rolled" => new Quantity(0, QuantityKind.Rolled),
                "opponentsDealtCombatDamage" => new Quantity(0, QuantityKind.OpponentsDealtCombatDamage),
                _ => null,
            };
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("manaValueSum", out var mvs)) return new Quantity(0, QuantityKind.ManaValueSum, ParseFilter(mvs));
        if (e.TryGetProperty("found", out var fnd)) return new Quantity(0, QuantityKind.FoundMatching, ParseFilter(fnd, ControllerFilter.Any));
        return null;
    }
}
