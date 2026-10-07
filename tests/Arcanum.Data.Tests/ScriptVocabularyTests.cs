// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>Parsing of script vocabulary for continuous copies, state triggers, counter bans and per-player targets.</summary>
public class ScriptVocabularyTests
{
    [Fact]
    public void ParsesStateTriggersCountersAndCopies()
    {
        var script = CardScriptParser.Parse("""
            {
              "chooseOnEnter": "creature",
              "entersCounterOn": { "filter": { "types": ["artifact"] }, "kind": "phylactery" },
              "abilities": [
                { "static": { "affects": "enchanted", "copyOfChosen": true } },
                { "trigger": "state", "when": { "not": { "control": { "hasCounterKind": "phylactery" } } }, "effects": [{ "sacrificeIt": "self" }] },
                { "trigger": "attacks", "targets": [{ "kind": "permanent", "perPlayer": true }],
                  "effects": [{ "sacrificeIt": "eachTarget" }, { "revealTopPut": { "types": ["land"] }, "who": "sacrificers" }, { "cantHaveCounters": "target" },
                              { "exileLibraryAllBut": 1 }, { "exileUncastEntering": { "types": ["creature"] } }] }
              ]
            }
            """);
        Assert.True(Assert.IsType<StaticAbility>(script.Abilities[0]).CopiesChosenCreature);
        var state = Assert.IsType<TriggeredAbility>(script.Abilities[1]);
        Assert.Equal(TriggerEvent.StateTrigger, state.Trigger);
        Assert.NotNull(state.TriggerCondition);
        var attack = Assert.IsType<TriggeredAbility>(script.Abilities[2]);
        Assert.True(attack.Targets[0].PerPlayer);
        Assert.IsType<RevealTopPutOntoBattlefield>(attack.Effects[1]);
        Assert.IsType<PreventCounters>(attack.Effects[2]);
        Assert.IsType<ExileLibraryAllButBottom>(attack.Effects[3]);
        Assert.IsType<ExileUncastEntering>(attack.Effects[4]);
        Assert.Equal(EnterChoice.Creature, script.ChooseOnEnter);
    }

    [Fact]
    public void ParsesNewQuantitiesConditionsAndKeywords()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "static": { "affects": "self", "while": { "not": "dealtDamage" }, "keywords": ["Can attack despite defender"] } },
              { "cost": "{1}", "effects": [{ "draw": { "greatestManaValue": { "types": ["artifact"] } } }, { "pump": ["-lifeGained", "-lifeGained"], "what": "self" }] }
            ] }
            """);
        var st = Assert.IsType<StaticAbility>(script.Abilities[0]);
        Assert.IsType<Not>(st.While);
        Assert.Contains(Keyword.CanAttackDespiteDefender, st.GrantedKeywords);
        var activated = Assert.IsType<ActivatedAbility>(script.Abilities[1]);
        Assert.NotNull(activated.Effects[0]);
    }
}
