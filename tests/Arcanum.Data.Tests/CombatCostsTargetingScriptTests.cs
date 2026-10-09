// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>Script vocabulary for block taxes, blocking restrictions, tap and graveyard costs, filters and targeting rules.</summary>
public class CombatCostsTargetingScriptTests
{
    private static CardDefinition Apply(string json) => CardScriptParser.Parse(json).ApplyTo(new CardDefinition { Name = "Test" });

    [Fact]
    public void ParsesCardWideBlockTaxTargetingRestrictionAndConditionalUncounterability()
    {
        var card = Apply("""
            { "blockTax": "{1}", "blockTaxIf": "attacking", "cantBeTargetedBy": { "notColors": ["G"] },
              "uncounterableIf": { "graveyard": 2, "filter": { "types": ["instant", "sorcery"] } } }
            """);
        Assert.Equal("{1}", card.BlockTax!.ToString());
        Assert.IsType<SourceAttacking>(card.BlockTaxIf);
        Assert.Equal(new[] { "G" }, card.CantBeTargetedBy!.NotColors);
        Assert.IsType<CardsInGraveyard>(card.CantBeCounteredIf);
        Assert.False(card.CantBeCountered);
    }

    [Fact]
    public void ParsesTheCanBlockOnlyFlyersKeyword()
    {
        Assert.True(Keywords.TryParse("Can block only creatures with flying", out var keyword));
        Assert.Equal(Keyword.CanBlockOnlyFlyers, keyword);
        Assert.Equal("Can block only creatures with flying", Keywords.DisplayName(keyword));
    }

    [Fact]
    public void ParsesTapPermanentsAndMultiTypeGraveyardCosts()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "cost": "tapPermanents:2:artifact", "effects": [{ "draw": 1 }] },
              { "cost": "tapPermanents:3:artifact|creature", "effects": [{ "draw": 1 }] },
              { "cost": "{1}, exileGraveyardCards:1:instant|sorcery", "effects": [{ "draw": 1 }] }
            ] }
            """);
        var tap = Assert.IsType<ActivatedAbility>(script.Abilities[0]).Cost.Extra!;
        Assert.Equal(2, tap.TapCount);
        Assert.Equal(CardType.Artifact, tap.TapCreatures!.Types);
        Assert.False(tap.TapCreatures.Other); // the source may tap itself
        var either = Assert.IsType<ActivatedAbility>(script.Abilities[1]).Cost.Extra!;
        Assert.Equal(CardType.Artifact | CardType.Creature, either.TapCreatures!.Types);
        var exile = Assert.IsType<ActivatedAbility>(script.Abilities[2]).Cost.Extra!;
        Assert.Equal(CardType.Instant | CardType.Sorcery, exile.ExileFromGraveyardFilter!.Types);
    }

    [Fact]
    public void ParsesAGraveyardExileAdditionalCost()
    {
        var card = Apply("""{ "additionalCost": { "exileGraveyard": 2, "exileGraveyardFilter": { "types": ["creature"] } } }""");
        Assert.Equal(2, card.AdditionalCost!.ExileFromGraveyard);
        Assert.Equal(CardType.Creature, card.AdditionalCost.ExileFromGraveyardFilter!.Types);
    }

    [Fact]
    public void ParsesTheNewFilters()
    {
        var a = CardScriptParser.ParseFilter(System.Text.Json.JsonDocument.Parse("""
            { "without": ["Flying", "Reach"], "powerNotEqualToughness": true, "notOwnedByYou": true, "notTriggered": true, "maxManaValueX": true, "targetsYou": true }
            """).RootElement);
        Assert.Equal(new[] { Keyword.Flying, Keyword.Reach }, a.WithoutKeywords);
        Assert.Null(a.WithoutKeyword);
        Assert.True(a.PowerNotEqualToughness && a.NotOwnedByYou && a.NotTriggered && a.MaxManaValueX && a.TargetsYou);
        var b = CardScriptParser.ParseFilter(System.Text.Json.JsonDocument.Parse("""{ "without": "Flying" }""").RootElement);
        Assert.Equal(Keyword.Flying, b.WithoutKeyword);
        Assert.Null(b.WithoutKeywords);
    }

    [Fact]
    public void ParsesChangeTargetToSelfAndCopyingTheCounteredSpell()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "targets": ["spell"], "effects": [
              { "counter": "target" },
              { "copySpell": "target", "counteredThisWay": true },
              { "changeTarget": "target", "to": "self" },
              { "changeTarget": "target" } ] } }
            """);
        var effects = script.Spell!.Effects;
        Assert.True(Assert.IsType<CopySpell>(effects[1]).CounteredThisWay);
        Assert.True(Assert.IsType<ChangeTarget>(effects[2]).ToSource);
        Assert.False(Assert.IsType<ChangeTarget>(effects[3]).ToSource);
    }
}
