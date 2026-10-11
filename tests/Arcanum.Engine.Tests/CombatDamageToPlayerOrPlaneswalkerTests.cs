// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>"Whenever this creature deals combat damage to a player or planeswalker, create that many Treasure tokens."</summary>
public partial class ScriptedVocabularyTests
{
    private const string TreasureRaiderScript = """
        { "abilities": [
          { "trigger": "combatDamageToPlayerOrPlaneswalker", "effects": [{ "tokens": "triggerAmount", "token": "Treasure" }],
            "text": "Whenever this creature deals combat damage to a player or planeswalker, create that many Treasure tokens." } ] }
        """;

    private static int Treasures(Scenario s, PlayerId player) =>
        s.Game.State.Battlefield.Select(s.Game.State.GetCard).Count(c => c.Controller == player && c.Definition.Name == "Treasure");

    [Fact]
    public async Task CombatDamageToAPlayerCreatesThatManyTreasures()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Raider", TreasureRaiderScript, 3, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(3, Treasures(s, P0));
    }

    [Fact]
    public async Task CombatDamageToAPlaneswalkerCreatesThatManyTreasures()
    {
        var s = new Scenario();
        var walker = s.Game.SetupPermanent(P1, new CardDefinition { Name = "Sage", ManaCost = ManaCost.Parse("{2}"), Types = CardType.Planeswalker, Loyalty = 5 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        s.Add(P0, Creature("Raider", TreasureRaiderScript, 3, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0], walker)).ToList();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(walker).CounterCount(CounterKind.Loyalty));
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(3, Treasures(s, P0));
    }

    [Fact]
    public async Task CombatDamageToACreatureCreatesNoTreasure()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Raider", TreasureRaiderScript, 3, 3));
        s.Add(P1, Scenario.Creature("Wall", 0, 5));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(0, Treasures(s, P0));
    }
}
