// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>"Whenever you expend N": it triggers as you spend your Nth total mana to cast spells during a turn, once.</summary>
public class ExpendTests
{
    private static CardDefinition Banner() => new()
    {
        Name = "Thrift Banner", Types = CardType.Enchantment,
        Abilities = CardScriptParser.Parse("""
            { "abilities": [ { "trigger": "youExpend", "expend": 4, "effects": [ { "gainLife": 1 } ], "text": "Whenever you expend 4, you gain 1 life." } ] }
            """).Abilities,
    };

    private static CardDefinition Cub(int cost) => Creature($"Cub {cost}", 1, 1) with { ManaCost = ManaCost.Parse($"{{{cost}}}") };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task TheFourthManaSpentOnSpellsTriggersOnce()
    {
        var s = Casting();
        s.Add(P0, Banner());
        s.Lands(P0, 6);
        s.InHand(P0, Cub(3));
        s.InHand(P0, Cub(1));
        s.InHand(P0, Cub(2));
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ThreeManaDoesNotExpendFour()
    {
        var s = Casting();
        s.Add(P0, Banner());
        s.Lands(P0, 3);
        s.InHand(P0, Cub(3));
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ManaForAbilitiesDoesNotCount()
    {
        var s = Casting();
        s.Add(P0, Banner());
        var pump = new CardDefinition
        {
            Name = "Pump Totem", Types = CardType.Artifact,
            Abilities = CardScriptParser.Parse("""
                { "abilities": [ { "cost": "{4}", "effects": [ { "draw": 1 } ], "text": "{4}: Draw a card." } ] }
                """).Abilities,
        };
        var totem = s.Add(P0, pump);
        s.Lands(P0, 4);
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == totem) is { } a) { used = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(used);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }
}
