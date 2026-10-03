// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

public class StaticAbilityTests
{
    private static CardDefinition WithAbilities(CardDefinition card, params AbilityDefinition[] abilities) => card with { Abilities = abilities };

    private static TestController CastEverything(TestController c)
    {
        c.Act = (view, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                 ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                 ?? PassPriority.Instance;
        c.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return c;
    }

    [Fact]
    public async Task LordBuffsOtherCreaturesOfItsType()
    {
        var s = new Scenario();
        var king = s.Add(P0, WithAbilities(Creature("King", 2, 2) with { Subtypes = new[] { "Goblin" } },
            new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures, Other: true, Subtype: "Goblin"), 1, 1)));
        var goblin = s.Add(P0, Creature("Goblin", 1, 1) with { Subtypes = new[] { "Goblin" } });
        var elf = s.Add(P0, Creature("Elf", 1, 1) with { Subtypes = new[] { "Elf" } });
        var enemyGoblin = s.Add(P1, Creature("Goblin", 1, 1) with { Subtypes = new[] { "Goblin" } });
        await s.RunUntilTurn(1);

        Assert.Equal(2, s.Card(goblin).Power);
        Assert.Equal(2, s.Card(king).Power); // "other": not itself
        Assert.Equal(1, s.Card(elf).Power);
        Assert.Equal(1, s.Card(enemyGoblin).Power);
    }

    [Fact]
    public async Task AnthemGrantsKeywordsAndEndsWhenSourceLeaves()
    {
        var s = new Scenario();
        var banner = s.Add(P0, new CardDefinition
        {
            Name = "Banner", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures), 1, 0, new[] { Keyword.Flying }) },
        });
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var shatter = new CardDefinition
        {
            Name = "Shatter", Types = CardType.Instant, ManaCost = ManaCost.Parse("{R}"),
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Enchantment) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        };
        bool flyingWhileBannerOut = false;
        s.Game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 1 }) flyingWhileBannerOut = s.Card(bear).Has(Keyword.Flying); };
        s.Add(P1, Arcanum.Cards.GenericCards.Mountain);
        s.InHand(P1, shatter);
        CastEverything(s.Defender);
        await s.RunUntilTurn(3);

        Assert.True(flyingWhileBannerOut);
        Assert.Equal(Zone.Graveyard, s.Card(banner).Zone);
        Assert.False(s.Card(bear).Has(Keyword.Flying));
        Assert.Equal(2, s.Card(bear).Power);
    }

    private static CardDefinition Aura(string name, int power, int toughness, params Keyword[] keywords) => new()
    {
        Name = name, Types = CardType.Enchantment, Subtypes = new[] { "Aura" }, ManaCost = ManaCost.Parse("{G}"),
        EnchantTarget = new TargetSpec(TargetKind.Creature),
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Enchanted), power, toughness, keywords) },
    };

    [Fact]
    public async Task AuraAttachesToItsTargetAndBuffsIt()
    {
        var s = new Scenario();
        CastEverything(s.Attacker);
        s.Add(P0, Arcanum.Cards.GenericCards.Forest);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var aura = s.InHand(P0, Aura("Might", 2, 0, Keyword.Trample));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(bear) };
        await s.RunUntilTurn();

        Assert.Equal(Zone.Battlefield, s.Card(aura).Zone);
        Assert.Equal(bear, s.Card(aura).AttachedTo);
        Assert.Equal(4, s.Card(bear).Power);
        Assert.True(s.Card(bear).Has(Keyword.Trample));
    }

    [Fact]
    public async Task AuraGoesToGraveyardWhenItsCreatureDies()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var aura = s.InHand(P0, Aura("Might", 1, 1));
        s.Add(P0, Arcanum.Cards.GenericCards.Forest);
        CastEverything(s.Attacker);
        s.Attacker.Targets = (_, r) => new[] { Target.Of(bear) };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        var giant = s.Add(P1, Creature("Giant", 5, 5));
        s.Defender.Block = (_, _, attackers) => new[] { new BlockDeclaration(giant, attackers[0]) };
        await s.RunUntilTurn();

        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(aura).Zone); // 704.5m
    }

    [Fact]
    public async Task AuraSpellFizzlesIfTargetIsGone()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Arcanum.Cards.GenericCards.Forest);
        var aura = s.InHand(P0, Aura("Might", 1, 1));
        CastEverything(s.Attacker);
        s.Attacker.Targets = (_, r) => new[] { Target.Of(bear) };
        // Opponent responds by bouncing the bear.
        s.Add(P1, Arcanum.Cards.GenericCards.Island);
        s.InHand(P1, new CardDefinition
        {
            Name = "Recall", Types = CardType.Instant, ManaCost = ManaCost.Parse("{U}"),
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new ReturnToHand(Subject.TargetAt(0)) } },
        });
        s.Defender.Act = (view, legal) => view.Stack.Count > 0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance
            : PassPriority.Instance;
        await s.RunUntilTurn();

        Assert.Contains(s.Game.Log, e => e is CardMoved { From: Zone.Battlefield, To: Zone.Hand } m && m.Card == bear);
        Assert.Equal(Zone.Graveyard, s.Card(aura).Zone);
        Assert.Contains(s.Game.Log, e => e is FizzledOnResolution f && f.Source == aura);
    }

    private static CardDefinition Sword() => new()
    {
        Name = "Sword", Types = CardType.Artifact, Subtypes = new[] { "Equipment" },
        Abilities = new AbilityDefinition[]
        {
            new StaticAbility(new AffectedFilter(AffectedScope.Equipped), 2, 1),
            new ActivatedAbility
            {
                Cost = new AbilityCost(ManaCost.Parse("{1}")), SorcerySpeed = true, Text = "Equip {1}",
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
                Effects = new Effect[] { new AttachSelf(Subject.TargetAt(0)) },
            },
        },
    };

    [Fact]
    public async Task EquipmentAttachesAndStaysWhenTheCreatureLeaves()
    {
        var s = new Scenario();
        var sword = s.Add(P0, Sword());
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Arcanum.Cards.GenericCards.Mountain);
        CastEverything(s.Attacker);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int powerWhenAttacking = 0;
        s.Game.EventRaised += e => { if (e is AttackerDeclared a && a.Attacker == bear) powerWhenAttacking = s.Card(bear).Power; };
        var giant = s.Add(P1, Creature("Giant", 5, 5));
        s.Defender.Block = (_, _, attackers) => new[] { new BlockDeclaration(giant, attackers[0]) };
        await s.RunUntilTurn();

        Assert.Equal(4, powerWhenAttacking);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(sword).Zone);
        Assert.Null(s.Card(sword).AttachedTo); // 704.5n
    }

    [Fact]
    public async Task EquipIsSorcerySpeedOnly()
    {
        var s = new Scenario();
        s.Add(P1, Sword());
        s.Add(P1, Creature("Bear", 2, 2));
        s.Add(P1, Arcanum.Cards.GenericCards.Mountain);
        CastEverything(s.Defender);
        await s.RunUntilTurn(2); // only P0's turn: P1 never gets sorcery timing
        Assert.DoesNotContain(s.Game.Log, e => e is AbilityActivated);
    }

    [Fact]
    public async Task EntersTappedAndEntersWithCountersReplacements()
    {
        var s = new Scenario();
        CastEverything(s.Attacker);
        s.Lands(P0, 2);
        var tapland = s.InHand(P0, Arcanum.Cards.GenericCards.Forest with { Name = "Slow Grove", EntersTapped = true });
        var grower = s.InHand(P0, Creature("Grower", 0, 0) with { ManaCost = ManaCost.Parse("{2}"), EntersWithCounters = 2 });
        s.Attacker.Act = (view, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault()
                                          ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                          ?? PassPriority.Instance;
        bool tappedOnEntry = false;
        s.Game.EventRaised += e => { if (e is CardMoved { To: Zone.Battlefield } m && m.Card == tapland) tappedOnEntry = s.Card(tapland).Tapped; };
        await s.RunUntilTurn();

        Assert.True(tappedOnEntry);
        Assert.Equal(Zone.Battlefield, s.Card(grower).Zone); // 0/0 survives thanks to the counters
        Assert.Equal(2, s.Card(grower).Power);
    }
}
