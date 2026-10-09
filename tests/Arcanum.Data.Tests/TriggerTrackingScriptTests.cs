// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;

namespace Arcanum.Data.Tests;

/// <summary>Vocabulary for triggers and turn tracking (Magic Origins).</summary>
public class TriggerTrackingScriptTests
{
    [Theory]
    [InlineData("becomesRenowned", TriggerEvent.BecomesRenowned)]
    [InlineData("creatureBecomesRenowned", TriggerEvent.CreatureBecomesRenowned)]
    [InlineData("creatureBecomesBlocked", TriggerEvent.CreatureBecomesBlocked)]
    [InlineData("becomesTargetOfYours", TriggerEvent.BecomesTargetOfYours)]
    public void ParsesTheNewTriggers(string name, TriggerEvent expected)
    {
        var script = CardScriptParser.Parse($$"""{ "abilities": [{ "trigger": "{{name}}", "effects": [{ "draw": 1 }] }] }""");
        Assert.Equal(expected, Assert.IsType<TriggeredAbility>(Assert.Single(script.Abilities)).Trigger);
    }

    [Fact]
    public void BatchedPerPlayerIsBatched()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "trigger": "creatureCombatDamageToPlayer", "filter": { "types": ["artifact"] }, "batchedPerPlayer": true, "effects": [{ "draw": 1 }] }] }
            """);
        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(script.Abilities));
        Assert.True(trigger.Batched);
        Assert.True(trigger.BatchedPerPlayer);
    }

    [Fact]
    public void ParsesConditionsSubjectsFiltersAndEmblemOptions()
    {
        Assert.Equal(new SpellsCastLastTurn(2), CardScriptParser.ParseCondition(Json("""{ "spellsCastLastTurn": 2 }""")));
        Assert.Equal(new SourceDealtDamageThisTurn(3), CardScriptParser.ParseCondition(Json("""{ "dealtDamageThisTurn": 3 }""")));
        Assert.Equal(SubjectKind.PlayersDamagedThisWay, CardScriptParser.ParseSubject("playersDamagedThisWay").Kind);
        Assert.True(CardScriptParser.ParseFilter(Json("""{ "damagedThisTurnByThat": true }""")).DamagedThisTurnByRemembered);

        var script = CardScriptParser.Parse("""
            { "spell": { "targets": ["creature:you"], "effects": [
              { "emblem": "Delayed", "untilEndOfTurn": true, "about": "target", "for": "playersDamagedThisWay",
                "abilities": [{ "trigger": "creatureDies", "effects": [{ "loseLife": 2, "who": "triggeredPlayer" }] }] } ] } }
            """);
        var emblem = Assert.IsType<CreateEmblem>(Assert.Single(script.Spell!.Effects));
        Assert.True(emblem.UntilEndOfTurn);
        Assert.Equal(SubjectKind.Target, emblem.About!.Kind);
        Assert.Equal(SubjectKind.PlayersDamagedThisWay, emblem.For!.Kind);
    }

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement;
}
