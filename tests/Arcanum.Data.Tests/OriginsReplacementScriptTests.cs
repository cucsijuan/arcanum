// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>Vocabulary for replacement effects, damage prevention and life (Magic Origins).</summary>
public class OriginsReplacementScriptTests
{
    [Fact]
    public void DamageBonusAndDamageToYouReductionAreCardWide()
    {
        var hellion = CardScriptParser.Parse("""{ "damageBonus": { "sources": { "colors": ["R"], "other": true }, "amount": 1 } }""");
        var bonus = Assert.Single(hellion.DamageBonuses!);
        Assert.Equal(1, bonus.Amount);
        Assert.True(bonus.Sources.Other);
        Assert.Equal(ControllerFilter.You, bonus.Sources.Controller);
        Assert.Equal(new[] { "R" }, bonus.Sources.Colors);

        var orbs = CardScriptParser.Parse("""{ "givesHexproof": true, "preventDamageToYou": { "sources": { "types": ["creature"] }, "amount": 1 } }""");
        var reduction = Assert.Single(orbs.DamageToYouReductions!);
        Assert.Equal(CardType.Creature, reduction.Sources.Types);
        Assert.Equal(ControllerFilter.Any, reduction.Sources.Controller);
        var applied = orbs.ApplyTo(new CardDefinition { Name = "Orbs" });
        Assert.Same(orbs.DamageToYouReductions, applied.DamageToYouReductions);
        Assert.True(applied.GivesControllerHexproof);
    }

    [Fact]
    public void LifeAndDyingReplacementsAreNamedInReplaces()
    {
        var script = CardScriptParser.Parse("""{ "replaces": ["DoubleLifeGain", "OpponentsLifeGainBecomesLoss", "ExileInsteadOfDying"] }""");
        Assert.Equal(Replacements.DoubleLifeGain | Replacements.OpponentsLifeGainBecomesLoss | Replacements.ExileInsteadOfDying, script.Replaces);
    }

    [Fact]
    public void PreventionShieldOnAnObjectAttackRequirementEndOfCombatAndAttackedCondition()
    {
        var mist = CardScriptParser.Parse("""{ "spell": { "targets": ["creature"], "effects": [{ "preventDamage": true, "to": "target" }] } }""");
        var prevent = Assert.IsType<PreventDamageThisTurn>(Assert.Single(mist.Spell!.Effects));
        Assert.Equal(SubjectKind.Target, prevent.To!.Kind);
        Assert.False(prevent.CombatOnly);

        var kytheon = CardScriptParser.Parse("""
            { "abilities": [{ "trigger": "endOfCombat", "if": { "attackedThisCombatWithOthers": 2 }, "effects": [{ "blink": "self", "transformed": true }] }],
              "back": { "abilities": [{ "cost": "+2", "targets": [{ "kind": "creature", "controller": "opponent", "optional": true }],
                                        "effects": [{ "attacksSourceNextTurn": "target" }] }] } }
            """);
        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(kytheon.Abilities));
        Assert.Equal(TriggerEvent.EndOfCombat, trigger.Trigger);
        Assert.Equal(new SourceAndOthersAttackedThisCombat(2), trigger.Condition);
        var plusTwo = Assert.Single(kytheon.Back!.Abilities);
        Assert.Equal(SubjectKind.Target, Assert.IsType<AttacksSourceNextTurn>(Assert.Single(plusTwo.Effects)).What.Kind);
    }
}
