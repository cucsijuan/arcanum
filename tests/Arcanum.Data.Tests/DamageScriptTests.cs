// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.Tests;

/// <summary>Script vocabulary for damage, prevention, random discards and the costs of Magic 2010 cards.</summary>
public class DamageScriptTests
{
    [Fact]
    public void ParsesRandomDiscardRedirectionAndEvenDivision()
    {
        var spell = CardScriptParser.Parse("""{ "spell": { "targets": ["player"], "effects": [{ "discard": "X", "who": "target", "random": true }, { "redirectDamage": 2, "to": "target" }, { "divideEvenly": "X" }, { "destroyRandom": "eachTarget" }] } }""").Spell!;
        Assert.True(Assert.IsType<Discard>(spell.Effects[0]).AtRandom);
        Assert.Equal(2, Assert.IsType<RedirectNextDamage>(spell.Effects[1]).Amount);
        Assert.Equal(QuantityKind.X, Assert.IsType<DealDamageDividedEvenly>(spell.Effects[2]).Total.Kind);
        Assert.Equal(SubjectKind.EachTarget, Assert.IsType<DestroyOneAtRandom>(spell.Effects[3]).Among.Kind);
    }

    [Fact]
    public void ParsesPreventionAndStatics()
    {
        var script = CardScriptParser.Parse("""
            {
              "replaces": ["PreventOneDamageFromOpponentsSources", "PreventDamageRemoveCounters"],
              "spell": { "effects": [{ "preventDamage": true, "toYou": true, "toYourCreatures": true }] },
              "abilities": [
                { "static": { "affects": "equipped", "pump": [2, 4], "loseKeywords": ["Flying"], "preventDamage": "noncombat" } },
                { "static": { "affects": "enchanted", "keywords": ["Can't attack", "Can't block"], "cantActivate": true } }
              ]
            }
            """);
        var prevent = Assert.IsType<PreventDamageThisTurn>(script.Spell!.Effects[0]);
        Assert.True(prevent.ToYou && prevent.ToYourCreatures && !prevent.CombatOnly);
        Assert.Equal(Replacements.PreventOneDamageFromOpponentsSources | Replacements.PreventDamageRemoveCounters, script.Replaces);
        var armor = Assert.IsType<StaticAbility>(script.Abilities[0]);
        Assert.Equal(new[] { Keyword.Flying }, armor.LosesKeywords);
        Assert.Equal(StaticDamagePrevention.Noncombat, armor.PreventsDamage);
        Assert.True(Assert.IsType<StaticAbility>(script.Abilities[1]).CantActivateAbilities);
    }

    [Fact]
    public void ParsesTriggersCostsAndCardWideRules()
    {
        var script = CardScriptParser.Parse("""
            {
              "xManaType": "{B}", "extraTargetCost": 1,
              "abilities": [
                { "trigger": "dealsDamageToOpponent", "effects": [{ "discard": 1, "who": "triggeredPlayer", "random": true }] },
                { "trigger": "becomesTargetOfAny", "effects": [{ "sacrificeIt": "self" }] },
                { "trigger": "attachedBecomesTarget", "effects": [{ "destroy": "self" }] },
                { "trigger": "counterRemoved", "effects": [{ "atNextEndStepAbout": "self", "effects": [{ "counters": 2, "what": "triggered" }] }] },
                { "trigger": "playerTapsLandForMana", "effects": [{ "damage": 1, "to": "triggeredPlayer" }] },
                { "cost": "-X", "targets": ["creature"], "effects": [{ "damage": "X", "to": "target" }] },
                { "cost": "{R}", "effects": [{ "if": { "activatedThisTurn": 4 }, "then": [{ "atNextEndStepAbout": "self", "effects": [{ "sacrificeIt": "triggered" }] }] }] }
              ]
            }
            """);
        Assert.Equal(new[] { TriggerEvent.DealsDamageToOpponent, TriggerEvent.BecomesTarget, TriggerEvent.AttachedBecomesTarget, TriggerEvent.CounterRemoved, TriggerEvent.PlayerTapsLandForMana },
            script.Abilities.OfType<TriggeredAbility>().Select(t => t.Trigger));
        var minusX = Assert.IsType<ActivatedAbility>(script.Abilities[5]);
        Assert.True(minusX.Cost.LoyaltyX);
        Assert.Null(minusX.Cost.Loyalty);
        var whelp = Assert.IsType<ActivatedAbility>(script.Abilities[6]);
        Assert.Equal(4, Assert.IsType<ActivatedThisTurn>(Assert.IsType<IfThen>(whelp.Effects[0]).Condition).Times);
        var definition = script.ApplyTo(new CardDefinition { Name = "Test" });
        Assert.Equal(ManaType.Black, definition.XManaType);
        Assert.Equal(1, definition.ExtraTargetCost);
    }
}
