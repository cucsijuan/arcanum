// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>Parsing of the vocabulary added for Magic Origins: sweeps into libraries, casting revealed cards, naming, storage counters.</summary>
public class OriginsVocabularyTests
{
    [Fact]
    public void ParsesSweepsCastingNamingAndChoices()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "targets": ["opponent"], "effects": [
                { "shuffleIntoLibraries": "everyone", "hand": true, "permanents": true, "drawThatMany": true },
                { "eachPutsFromHand": { "types": ["land"] }, "who": "everyone" },
                { "revealTopCastFree": 7, "of": "target", "filter": { "types": ["instant", "sorcery"] }, "moreCastsIf": { "graveyard": 2 }, "moreCasts": 2, "rest": "graveyard" },
                { "nameAndExile": "target", "nameFilter": { "types": ["creature"] } },
                { "discardChosen": "target", "optional": true, "else": [{ "discard": 2, "who": "target" }] },
                { "keepOneOfEachType": "everyone", "chooser": "you", "kinds": ["artifact", "planeswalker"], "nonlandOnly": true },
                { "copy": "triggered", "gainsHaste": true },
                { "become": "target", "power": 1, "toughness": 1, "setSubtypes": ["Frog"], "loseAbilities": true },
                { "castFromGraveyardThisTurn": "target", "exileInstead": true },
                { "draw": { "power": "found" } }, { "gainLife": { "toughness": "found" } } ] } }
            """);
        var e = script.Spell!.Effects;
        var sweep = Assert.IsType<ShuffleIntoLibraries>(e[0]);
        Assert.True(sweep.Hand && sweep.Permanents && sweep.DrawThatMany && !sweep.Graveyard);
        Assert.IsType<EachPutsFromHand>(e[1]);
        var talent = Assert.IsType<RevealTopCastFree>(e[2]);
        Assert.Equal((7, 1, 2, true), (talent.Count, talent.Casts, talent.MoreCasts, talent.RestToGraveyard));
        Assert.Equal(CardType.Creature, Assert.IsType<NameThenExileFromAllZones>(e[3]).NameFilter.Types);
        var snare = Assert.IsType<DiscardChosenByYou>(e[4]);
        Assert.True(snare.Optional);
        Assert.Single(snare.Else!);
        var keep = Assert.IsType<KeepOneOfEachType>(e[5]);
        Assert.True(keep.NonlandOnly);
        Assert.Equal(new[] { CardType.Artifact, CardType.Planeswalker }, keep.Kinds);
        Assert.Equal(SubjectKind.You, keep.Chooser!.Kind);
        Assert.True(Assert.IsType<CreateTokenCopy>(e[6]).GainsHaste);
        Assert.True(Assert.IsType<Become>(e[7]).LosesAbilities);
        Assert.True(Assert.IsType<PlayableFromGraveyardThisTurn>(e[8]).ExileInstead);
        Assert.Equal(QuantityKind.FoundPower, Assert.IsType<DrawCards>(e[9]).Count.Kind);
        Assert.Equal(QuantityKind.FoundToughness, Assert.IsType<GainLife>(e[10]).Amount.Kind);
    }

    [Fact]
    public void ParsesPerObjectBasePowerNamesAndStorageCounters()
    {
        var script = CardScriptParser.Parse("""
            { "chooseOnEnter": "opponentsRevealHandsThenNonlandName", "opponentsCantCastChosenName": true,
              "extraMana": [{ "types": "{C}", "removeCounters": "storage" }],
              "abilities": [{ "static": { "affects": "permanents:you", "setPower": { "manaValue": "affected" }, "setToughness": 2 } },
                            { "cost": "{1}, {T}", "effects": [{ "counters": 1, "what": "self", "kind": "storage" }] }] }
            """);
        Assert.Equal(EnterChoice.OpponentsRevealHandsThenNonlandName, script.ChooseOnEnter);
        Assert.True(script.OpponentsCantCastChosenName);
        Assert.Equal(CounterKind.Storage, script.ExtraMana!.Single().RemovesCounters);
        var stat = Assert.IsType<StaticAbility>(script.Abilities[0]);
        Assert.Equal(QuantityKind.AffectedManaValue, stat.SetPowerFrom!.Kind);
        Assert.Null(stat.SetPower);
        Assert.Equal(2, stat.SetToughness);
        Assert.Equal(CounterKind.Storage, Assert.IsType<AddCounters>(Assert.IsType<ActivatedAbility>(script.Abilities[1]).Effects[0]).Kind);
    }
}
