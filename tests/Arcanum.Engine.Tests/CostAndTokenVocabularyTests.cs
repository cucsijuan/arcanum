// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>"Discard this card" costs, "spells your opponents cast cost more" and land tokens with a basic land type.</summary>
public class CostAndTokenVocabularyTests
{
    [Fact]
    public void DiscardThisCostIsActivatedFromTheHand()
    {
        var cost = CardScriptParser.ParseCost("{3}{G}, discardThis");
        Assert.True(cost.FromHand);
        Assert.Equal(4, cost.Mana.ManaValue);
    }

    [Fact]
    public void LandTokenWithABasicLandTypeTapsForThatColor()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [{ "tokens": 1, "token": { "name": "Grove Arm", "types": "Land Creature — Forest Arm", "power": 3, "toughness": 3, "colors": ["G"] } }] } }
            """);
        var token = Assert.IsType<CreateTokens>(script.Spell!.Effects[0]).Token;
        Assert.Equal(new[] { ManaType.Green }, token.TapForMana);
    }

    [Fact]
    public void TokenCanTapForManaOfItsListedColors()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [{ "tokens": 1, "token": { "name": "Ember Root", "types": "Artifact — Root", "colors": ["G", "R"], "tapForMana": "{R}{G}" } }] } }
            """);
        var token = Assert.IsType<CreateTokens>(script.Spell!.Effects[0]).Token;
        Assert.Equal(new[] { ManaType.Red, ManaType.Green }, token.TapForMana);
    }

    private static readonly CardDefinition Tax = new()
    {
        Name = "Tolling Bell", Types = CardType.Artifact,
        Abilities = new AbilityDefinition[]
        {
            new SpellCostIncrease(new ObjectFilter(ExcludedTypes: CardType.Creature, Controller: ControllerFilter.Opponent), 1) { Text = "Noncreature spells your opponents cast cost {1} more to cast." },
        },
    };

    private static readonly CardDefinition Spark = new()
    {
        Name = "Small Spark", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
        Spell = new SpellAbility { Effects = new Effect[] { new GainLife(1, Subject.You) } },
    };

    [Theory]
    [InlineData(false, false)] // an opponent's tax: {R} becomes {1}{R}, one land isn't enough
    [InlineData(true, true)]   // its controller's own spells don't pay it
    public void OnlyOpponentsPayTheIncrease(bool ownTax, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(ownTax ? P0 : P1, Tax);
        var spark = s.InHand(P0, Spark);
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        Assert.Equal(castable, s.Game.GetLegalActions(P0).Contains(new CastSpell(spark)));
    }
}
