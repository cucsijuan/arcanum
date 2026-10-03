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

public class AbilityTests
{
    private static CardDefinition Instant(string name, string cost, AbilityDefinition spell) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Instant, Spell = (SpellAbility)spell,
    };

    private static readonly CardDefinition Bolt = Instant("Spark", "{R}", new SpellAbility
    {
        Targets = new[] { new TargetSpec(TargetKind.Any) },
        Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) },
    });

    /// <summary>Casts the first castable spell, otherwise plays greedily; never attacks.</summary>
    private static TestController Caster(Func<TargetRequest, Target>? pick = null)
    {
        var c = new TestController
        {
            Act = (view, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                   ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                   ?? PassPriority.Instance,
            Attack = (_, _, _) => Array.Empty<AttackDeclaration>(),
        };
        if (pick is not null) c.Targets = (_, r) => new[] { pick(r) };
        return c;
    }

    private static Scenario WithCaster(Func<TargetRequest, Target>? pick = null)
    {
        var s = new Scenario();
        var caster = Caster(pick);
        s.Attacker.Act = caster.Act;
        s.Attacker.Attack = caster.Attack;
        s.Attacker.Targets = caster.Targets;
        return s;
    }

    [Fact]
    public async Task DamageSpellKillsTargetCreature()
    {
        var s = WithCaster(r => r.Legal[0].First(t => t.Card is not null));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Contains(s.Game.Log, e => e is SpellResolved);
    }

    [Fact]
    public async Task DamageSpellCanTargetPlayers()
    {
        var s = WithCaster(r => Target.Of(P1));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task SpellWithoutLegalTargetsCannotBeCast()
    {
        var shatter = Instant("Shatter", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Artifact) },
            Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) },
        });
        var s = WithCaster();
        s.Lands(P0, 1);
        var card = s.InHand(P0, shatter);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(card).Zone);
        Assert.DoesNotContain(s.Game.Log, e => e is SpellCast);
    }

    [Fact]
    public async Task HexproofCantBeTargetedByOpponents()
    {
        var s = WithCaster(r => r.Legal[0].First(t => t.Card is not null));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        var ward = s.Add(P1, Creature("Warded", 2, 2, Keyword.Hexproof));
        TargetRequest? seen = null;
        var pick = s.Attacker.Targets;
        s.Attacker.Targets = (v, r) => { seen = r; return pick(v, r); };
        s.Add(P1, Creature("Bear", 2, 2));
        await s.RunUntilTurn();
        Assert.NotNull(seen);
        Assert.DoesNotContain(Target.Of(ward), seen!.Legal[0]);
    }

    [Fact]
    public async Task SpellFizzlesWhenItsOnlyTargetBecomesIllegal()
    {
        // P0 targets the bear; P1 responds by returning it to hand.
        var bounce = Instant("Recall", "{U}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
            Effects = new Effect[] { new ReturnToHand(Subject.TargetAt(0)) },
        });
        var s = WithCaster(r => r.Legal[0].First(t => t.Card is not null));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Add(P1, GenericCardsIsland());
        s.InHand(P1, bounce);
        s.Defender.Act = (view, legal) => view.Stack.Count > 0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance
            : PassPriority.Instance;
        await s.RunUntilTurn();

        Assert.Equal(Zone.Hand, s.Card(bear).Zone);
        Assert.Contains(s.Game.Log, e => e is FizzledOnResolution);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    private static CardDefinition GenericCardsIsland() => Arcanum.Cards.GenericCards.Island;

    [Fact]
    public async Task CounterspellCountersASpell()
    {
        var counter = Instant("Deny", "{U}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Spell) },
            Effects = new Effect[] { new CounterSpell(Subject.TargetAt(0)) },
        });
        var s = WithCaster(r => Target.Of(P1));
        s.Lands(P0, 1);
        var bolt = s.InHand(P0, Bolt);
        s.Add(P1, GenericCardsIsland());
        s.InHand(P1, counter);
        s.Defender.Act = (view, legal) => view.Stack.Count > 0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance
            : PassPriority.Instance;
        await s.RunUntilTurn();

        Assert.Contains(s.Game.Log, e => e is SpellCountered c && c.Card == bolt);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Graveyard, s.Card(bolt).Zone);
    }

    [Fact]
    public async Task PumpLastsUntilEndOfTurn()
    {
        var growth = Instant("Growth", "{G}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
            Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0), new[] { Keyword.Trample }) },
        });
        var s = new Scenario();
        s.Add(P0, Arcanum.Cards.GenericCards.Forest);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, growth);
        int powerDuringCombat = 0;
        bool trampleDuringCombat = false;
        s.Attacker.Act = (view, legal) => view.Step == Step.PrecombatMain
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance
            : PassPriority.Instance;
        s.Game.EventRaised += e =>
        {
            if (e is AttackerDeclared) { powerDuringCombat = s.Card(bear).Power; trampleDuringCombat = s.Card(bear).Has(Keyword.Trample); }
        };
        await s.RunUntilTurn();

        Assert.Equal(5, powerDuringCombat);
        Assert.True(trampleDuringCombat);
        Assert.Equal(15, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(2, s.Card(bear).Power); // back to normal after cleanup
        Assert.False(s.Card(bear).Has(Keyword.Trample));
    }

    [Fact]
    public async Task EntersTheBattlefieldTriggerUsesTheStack()
    {
        var sage = new CardDefinition
        {
            Name = "Sage", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Creature, Power = 1, Toughness = 1,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.EntersBattlefield, Text = "When this enters, it deals 1 damage to any target.",
                    Targets = new[] { new TargetSpec(TargetKind.Any) },
                    Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) },
                },
            },
        };
        var s = WithCaster(r => Target.Of(P1));
        s.Lands(P0, 1);
        s.InHand(P0, sage);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is AbilityTriggered);
        Assert.Contains(s.Game.Log, e => e is AbilityResolved);
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task DiesTriggerFiresFromTheGraveyard()
    {
        var martyr = Creature("Martyr", 1, 1) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.Dies, Effects = new Effect[] { new DrawCards(2, Subject.You) } },
            },
        };
        var s = WithCaster(r => r.Legal[0].First(t => t.Card is not null));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        var victim = s.Add(P1, martyr);
        int handBefore = -1;
        s.Game.EventRaised += e => { if (e is CreatureDied d && d.Card == victim) handBefore = s.Game.State.GetPlayer(P1).Hand.Count; };
        await s.RunUntilTurn();
        Assert.Equal(handBefore + 2, s.Game.State.GetPlayer(P1).Hand.Count);
    }

    [Fact]
    public async Task ActivatedTapAbilityPingsAndUsesTheStack()
    {
        var pinger = Creature("Pinger", 1, 1) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: deal 1 damage to any target.",
                    Targets = new[] { new TargetSpec(TargetKind.Any) },
                    Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) },
                },
            },
        };
        var s = WithCaster(r => Target.Of(P1));
        var source = s.Add(P0, pinger);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is AbilityActivated a && a.Source == source);
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
        Assert.True(s.Card(source).Tapped);
    }

    [Fact]
    public async Task ManaCostedAbilityExcludesItsOwnSourceFromPayment()
    {
        var shaman = Creature("Shaman", 1, 1) with
        {
            TapForMana = new[] { ManaType.Red },
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Parse("{R}"), Tap: true), Text = "{R}, {T}: draw a card.",
                    Effects = new Effect[] { new DrawCards(1, Subject.You) },
                },
            },
        };
        var s = WithCaster();
        var source = s.Add(P0, shaman);
        await s.RunUntilTurn();
        // The shaman can't tap for {R} and also tap for the cost, and there are no other sources.
        Assert.DoesNotContain(s.Game.Log, e => e is AbilityActivated a && a.Source == source);
    }

    [Fact]
    public async Task TokensAndCounters()
    {
        var token = Creature("Soldier", 1, 1);
        var muster = Instant("Muster", "{W}", new SpellAbility { Effects = new Effect[] { new CreateTokens(token, 2, Subject.You) } });
        var s = WithCaster();
        s.Add(P0, Arcanum.Cards.GenericCards.Plains);
        s.InHand(P0, muster);
        await s.RunUntilTurn();
        var tokens = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Definition.IsToken).ToList();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(P0, t.Controller));
    }

    [Fact]
    public async Task TokenLeavingTheBattlefieldCeasesToExist()
    {
        var token = Creature("Soldier", 1, 1) with { IsToken = true };
        var s = WithCaster(r => r.Legal[0].First(t => t.Card is not null));
        s.Lands(P0, 1);
        s.InHand(P0, Bolt);
        var t = s.Add(P1, token);
        await s.RunUntilTurn();
        Assert.DoesNotContain(t, s.Game.State.GetPlayer(P1).Graveyard);
    }
}
