// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>Script vocabulary for costs, filters, triggers and effects about graveyards, blocking and targeting.</summary>
public class GraveyardAndBlockScriptTests
{
    [Fact]
    public void ParsesSacrificeAnyAndExileGraveyardCostsWithAKind()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "cost": "{1}, sacrificeAny:creature", "effects": [{ "draw": 1 }] },
              { "cost": "sacrifice:creature", "effects": [{ "draw": 1 }] },
              { "cost": "{2}{B}, exileGraveyardCards:1:creature", "effects": [{ "draw": 1 }] },
              { "cost": "exileGraveyardCards:7", "effects": [{ "draw": 1 }] }
            ] }
            """);
        Assert.False(Assert.IsType<ActivatedAbility>(script.Abilities[0]).Cost.Extra!.Sacrifice!.Other);
        Assert.True(Assert.IsType<ActivatedAbility>(script.Abilities[1]).Cost.Extra!.Sacrifice!.Other);
        var exile = Assert.IsType<ActivatedAbility>(script.Abilities[2]).Cost.Extra!;
        Assert.Equal(1, exile.ExileFromGraveyard);
        Assert.Equal(CardType.Creature, exile.ExileFromGraveyardFilter!.Types);
        var any = Assert.IsType<ActivatedAbility>(script.Abilities[3]).Cost.Extra!;
        Assert.Equal(7, any.ExileFromGraveyard);
        Assert.Null(any.ExileFromGraveyardFilter);
    }

    [Fact]
    public void ParsesTheNewTriggersConditionsAndQuantities()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "trigger": "youBecomeTargetOfOpponent", "effects": [{ "counterTriggeringUnlessPays": "{1}" }] },
              { "trigger": "discardedByOpponent", "if": { "control": { "subtype": "Plains" } }, "effects": [{ "gainLife": 1 }] },
              { "trigger": "dealtDamage", "effects": [{ "counters": "triggerAmount", "what": "self" }] },
              { "trigger": "attachedBlocks", "effects": [{ "destroy": "triggered" }] },
              { "trigger": "permanentDies", "filter": { "types": ["planeswalker"] }, "effects": [{ "draw": 1 }] },
              { "trigger": "eachEndStep", "if": "attackedOrBlockedThisTurn", "effects": [{ "loseLife": { "halfUp": "life" } }] }
            ] }
            """);
        Assert.Equal(new[] { TriggerEvent.YouBecomeTargetOfOpponent, TriggerEvent.DiscardedByOpponent, TriggerEvent.DealtDamage, TriggerEvent.AttachedBlocks,
            TriggerEvent.PermanentDies, TriggerEvent.EachEndStep }, script.Abilities.OfType<TriggeredAbility>().Select(t => t.Trigger));
        var counter = Assert.IsType<CounterTriggeringUnlessPays>(Assert.IsType<TriggeredAbility>(script.Abilities[0]).Effects[0]);
        Assert.Equal(1, counter.Mana.ManaValue);
        Assert.IsType<SourceAttackedOrBlockedThisTurn>(Assert.IsType<TriggeredAbility>(script.Abilities[5]).Condition);
        var half = Assert.IsType<LoseLife>(Assert.IsType<TriggeredAbility>(script.Abilities[5]).Effects[0]).Amount;
        Assert.Equal(QuantityKind.HalfRoundedUp, half.Kind);
        Assert.Equal(QuantityKind.YourLife, half.Parts![0].Kind);
    }

    [Fact]
    public void ParsesEffectsFiltersAndCardWideRules()
    {
        var script = CardScriptParser.Parse("""
            {
              "uncounterableIfXAtLeast": 5, "replaces": ["LandsFromGraveyard"], "chooseOnEnter": "cardName",
              "spell": { "effects": [
                { "damage": "X", "to": "target", "cantBePrevented": true },
                { "pump": [0, 0], "what": "target", "cantBeBlockedBy": { "notSubtype": "Spirit" } },
                { "ignoreHexproof": true },
                { "mayCastFromGraveyard": { "subtype": "Zombie" } },
                { "exileTopPlayable": 7, "chooseOne": false, "castOnly": true },
                { "reanimate": "target", "counters": 1, "counterKind": "corpse", "exileIfLeaves": true },
                { "lookAtTop": 3, "take": 1, "required": true }
              ] },
              "abilities": [{ "static": { "affects": "permanents", "filter": { "types": ["land"], "chosenName": true, "self": false }, "setSubtypes": [], "losesAbilities": true } }]
            }
            """);
        Assert.Equal(5, script.CantBeCounteredIfXAtLeast);
        Assert.Equal(Replacements.LandsFromGraveyard, script.Replaces);
        var effects = script.Spell!.Effects;
        Assert.True(Assert.IsType<DealDamage>(effects[0]).Unpreventable);
        Assert.Equal("Spirit", Assert.IsType<PumpUntilEndOfTurn>(effects[1]).CantBeBlockedBy!.ExcludedSubtype);
        Assert.IsType<IgnoreHexproofThisTurn>(effects[2]);
        Assert.Equal("Zombie", Assert.IsType<MayCastFromGraveyardThisTurn>(effects[3]).Filter.Subtype);
        Assert.True(Assert.IsType<ExileTopPlayable>(effects[4]).CastOnly);
        var back = Assert.IsType<PutOntoBattlefield>(effects[5]);
        Assert.True(back.ExileIfLeaves);
        Assert.Equal(CounterKind.Corpse, back.CounterKind);
        Assert.True(Assert.IsType<LookAtTopTake>(effects[6]).Required);
        var land = Assert.IsType<StaticAbility>(script.Abilities[0]).Filter!;
        Assert.True(land.ChosenName);
        Assert.False(land.IsSource);
    }

    [Fact]
    public void TheNewKeywordsAreKnown()
    {
        Assert.True(Keywords.TryParse("Can attack as though it didn't have defender", out var attack));
        Assert.Equal(Keyword.CanAttackWithDefender, attack);
        Assert.True(Keywords.TryParse("Can block an additional creature each combat", out var block));
        Assert.Equal(Keyword.CanBlockAdditional, block);
    }
}
