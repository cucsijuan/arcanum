// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Class enchantments (rule 716): a Class enters at level 1; "[Cost]: Level N" makes it level N, only from level N-1 and only as a
/// sorcery; the abilities of a level section exist only from that level on, and "When this Class becomes level N" triggers then.
/// </summary>
public class ClassTests
{
    /// <summary>{R} Class: level 1 nothing; {1}: Level 2 — gain 2 life when it becomes level 2, creatures you control get +1/+0; {1}: Level 3 — +0/+1.</summary>
    private static CardDefinition Talent()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "cost": "{1}", "levelUp": 2, "text": "{1}: Level 2" },
              { "level": 2, "trigger": "becomesLevel", "effects": [ { "gainLife": 2 } ], "text": "When this Class becomes level 2, you gain 2 life." },
              { "level": 2, "static": { "affects": "creatures:you", "pump": [1, 0] }, "text": "Creatures you control get +1/+0." },
              { "cost": "{1}", "levelUp": 3, "text": "{1}: Level 3" },
              { "level": 3, "static": { "affects": "creatures:you", "pump": [0, 1] }, "text": "Creatures you control get +0/+1." }
            ] }
            """);
        return new CardDefinition
        {
            Name = "Ember Talent", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Enchantment, Subtypes = new[] { "Class" },
            Abilities = script.Abilities,
        };
    }

    /// <summary>Player 0 casts what it can, then levels up whenever allowed (the level abilities are the only activations); never attacks.</summary>
    private static Scenario Levelling(int maxLevelUps = int.MaxValue)
    {
        var s = new Scenario();
        int ups = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault() is { } cast) return cast;
            if (ups < maxLevelUps && legal.OfType<ActivateAbility>().FirstOrDefault() is { } up) { ups++; return up; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task AClassEntersAtLevel1WithoutItsHigherSections()
    {
        var s = Levelling(maxLevelUps: 0);
        s.Lands(P0, 1);
        var id = s.InHand(P0, Talent());
        var bear = s.Add(P0, Creature("Ash Bear", 2, 2));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(id).ClassLevel);
        Assert.Equal(2, s.Card(bear).Power);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task LevelTwoAddsItsSectionAndTriggers()
    {
        var s = Levelling(maxLevelUps: 1);
        s.Lands(P0, 3);
        var id = s.InHand(P0, Talent());
        var bear = s.Add(P0, Creature("Ash Bear", 2, 2));
        var levels = new List<int>();
        s.Game.EventRaised += e => { if (e is ClassLevelChanged c) levels.Add(c.Level); };
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(id).ClassLevel);
        Assert.Equal(new[] { 2 }, levels);
        Assert.Equal(3, s.Card(bear).Power);
        Assert.Equal(2, s.Card(bear).Toughness);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task LevelsGoUpOneAtATime()
    {
        var s = Levelling();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Talent());
        var bear = s.Add(P0, Creature("Ash Bear", 2, 2));
        var levels = new List<int>();
        s.Game.EventRaised += e => { if (e is ClassLevelChanged c) levels.Add(c.Level); };
        await s.RunUntilTurn();
        Assert.Equal(new[] { 2, 3 }, levels);
        Assert.Equal(3, s.Card(id).ClassLevel);
        Assert.Equal(3, s.Card(bear).Power);
        Assert.Equal(3, s.Card(bear).Toughness);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // the level 2 trigger once
    }

    [Fact]
    public void LevelThreeCantBeActivatedFromLevelOne()
    {
        var s = new Scenario();
        var id = s.Add(P0, Talent());
        s.Lands(P0, 2);
        var card = s.Card(id);
        Assert.Equal(1, card.ClassLevel);
        var levelThree = card.Definition.Abilities.OfType<ActivatedAbility>().Single(a => a.Effects.OfType<BecomeClassLevel>().Single().Level == 3);
        Assert.Equal(new ClassLevelIs(2), levelThree.ActivationCondition);
        Assert.True(levelThree.SorcerySpeed);
        Assert.DoesNotContain(card.Abilities, a => a.ClassLevel == 3);
    }

    /// <summary>{G} Class whose level 2 section is "If you would put one or more counters on a permanent or player, put twice that many …".</summary>
    private static CardDefinition Doubler()
    {
        var script = CardScriptParser.Parse("""
            { "cardRulesLevel": 2, "doublesCountersYouPut": true,
              "abilities": [ { "cost": "{1}", "levelUp": 2, "text": "{1}: Level 2" } ] }
            """);
        return script.ApplyTo(new CardDefinition
        {
            Name = "Brew Talent", ManaCost = ManaCost.Parse("{G}"), Types = CardType.Enchantment, Subtypes = new[] { "Class" },
            Abilities = script.Abilities,
        });
    }

    /// <summary>{1} sorcery: put a +1/+1 counter on target creature.</summary>
    private static CardDefinition Feed() => new()
    {
        Name = "Feed", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Sorcery,
        Spell = CardScriptParser.Parse("""{ "spell": { "targets": ["creature"], "effects": [ { "counters": 1, "what": "target" } ], "text": "Put a +1/+1 counter on target creature." } }""").Spell,
    };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task CardRulesOfALevelSectionApplyFromThatLevel(int levelUps, int counters)
    {
        var s = new Scenario();
        int ups = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (ups < levelUps && legal.OfType<ActivateAbility>().FirstOrDefault() is { } up) { ups++; return up; }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Add(P0, Doubler());
        s.Lands(P0, 2);
        s.InHand(P0, Feed());
        // The opponent's creature: the doubling is about who puts the counters, not who controls the permanent.
        var bear = s.Add(P1, Creature("Ash Bear", 2, 2));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
    }
}
