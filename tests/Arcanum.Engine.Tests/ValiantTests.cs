// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Valiant: "Whenever this creature becomes the target of a spell or ability you control for the first time each turn".</summary>
public class ValiantTests
{
    private static CardDefinition Squire() => new()
    {
        Name = "Brave Squire", ManaCost = ManaCost.Parse("{W}"), Types = CardType.Creature, Power = 1, Toughness = 1,
        Abilities = CardScriptParser.Parse("""
            { "abilities": [ { "trigger": "becomesTargetOfYours", "onSelf": true, "nth": 1, "effects": [ { "gainLife": 1 } ],
              "text": "Valiant — Whenever this creature becomes the target of a spell or ability you control for the first time each turn, you gain 1 life." } ] }
            """).Abilities,
    };

    private static CardDefinition Boost() => new()
    {
        Name = "Quick Boost", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Instant,
        Spell = CardScriptParser.Parse("""
            { "spell": { "targets": ["creature"], "effects": [ { "pump": [1, 0], "what": "target" } ], "text": "Target creature gets +1/+0 until end of turn." } }
            """).Spell,
    };

    [Fact]
    public async Task OnlyTheFirstTargetingEachTurnTriggers()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Add(P0, Squire());
        s.Lands(P0, 2);
        s.InHand(P0, Boost());
        s.InHand(P0, Boost());
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AnOpponentsSpellDoesNotTrigger()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Add(P0, Squire());
        s.Lands(P1, 1);
        s.InHand(P1, Boost());
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }
}
