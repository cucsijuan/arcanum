// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Last known information, timestamps and layers, the legend rule, simultaneous events, reflexive triggers and other
/// details where the exact rule matters.
/// </summary>
public class FidelityTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell, CardType type = CardType.Instant) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = type, Spell = spell,
    };

    private static CardDefinition Enchantment(string name, params AbilityDefinition[] abilities) => new()
    {
        Name = name, Types = CardType.Enchantment, Abilities = abilities,
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    [Fact]
    public async Task LandfallWithoutAFilterWatchesLands()
    {
        var s = new Scenario();
        s.Add(P0, Enchantment("Watcher", new TriggeredAbility { Trigger = TriggerEvent.LandEnters, Effects = new Effect[] { new GainLife(1, Subject.You) } }));
        s.InHand(P0, GenericCards.Forest);
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(21, Life(s, P0));
    }

    [Fact]
    public async Task DeathTriggersSeeTheCreatureAsItLastExisted()
    {
        var s = Casting();
        s.Lands(P0, 1);
        // Creatures you control are Angels; a watcher cares only about non-Angels dying.
        s.Add(P0, Enchantment("Choir", new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures)) { AddSubtypes = new[] { "Angel" } }));
        s.Add(P0, Enchantment("Mourner", new TriggeredAbility
        {
            Trigger = TriggerEvent.CreatureDies, Filter = new ObjectFilter(CardType.Creature, ExcludedSubtype: "Angel"),
            Effects = new Effect[] { new GainLife(5, Subject.You) },
        }));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Doom", "{R}", new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } }));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(20, Life(s, P0)); // it was an Angel when it died
    }

    [Fact]
    public async Task DoublingCountersOfOneKindLeavesOthers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Card(bear).Counters[CounterKind.Stun] = 1;
        s.InHand(P0, Spell("Grow", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new DoubleCounters(Subject.TargetAt(0), CounterKind.PlusOnePlusOne) },
        }));
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.True(s.Card(bear).CounterCount(CounterKind.Stun) <= 1);
    }

    [Fact]
    public async Task TokenCopiesAreDoubledAndCarryTheirSacrificeAbility()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Season", Types = CardType.Enchantment, Replaces = Replacements.DoubleTokens });
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Mirror", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new CreateTokenCopy(Subject.TargetAt(0), 1, Haste: true, SacrificeAtEndStep: true) },
        }));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        var copies = new List<CardId>();
        s.Game.EventRaised += e => { if (e is TokenCreated t) copies.Add(t.Card); };
        await s.RunUntilTurn();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, id => Assert.True(s.Card(id).Definition.Abilities.OfType<TriggeredAbility>().Any(a => a.Trigger == TriggerEvent.EachEndStep)));
        Assert.All(copies, id => Assert.NotEqual(Zone.Battlefield, s.Card(id).Zone)); // sacrificed at the end step
    }

    [Fact]
    public async Task ReflexiveTriggerTargetsAfterTheActionAndTheSpellNeedsNoTarget()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var foe = s.Add(P1, Creature("Foe", 2, 2));
        var faerie = Creature("Faerie", 1, 1, Keyword.Flying) with { IsToken = true };
        s.InHand(P0, Spell("Trick", "{R}", new SpellAbility
        {
            Effects = new Effect[]
            {
                new CreateTokens(faerie, 2, Subject.You),
                new ReflexiveTrigger(new TriggeredAbility
                {
                    Trigger = TriggerEvent.Reflexive, Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
                    Effects = new Effect[] { new TapIt(Subject.TargetAt(0)) }, Text = "When you do, tap target creature an opponent controls.",
                }, new CreatedThisWay()),
            },
        }));
        int tokensWhenTargeting = -1;
        s.Attacker.Targets = (view, r) =>
        {
            tokensWhenTargeting = view.Battlefield.Count(c => c.Name == "Faerie");
            return new[] { Target.Of(foe) };
        };
        bool tapped = false;
        s.Game.EventRaised += e => { if (e is PermanentTapped t && t.Card == foe) tapped = true; };
        await s.RunUntilTurn();
        Assert.Equal(2, tokensWhenTargeting); // chosen once the tokens exist
        Assert.True(tapped);
    }

    [Fact]
    public async Task TriggerConditionsAreNotCheckedAgainOnResolution()
    {
        var s = Casting();
        s.Lands(P0, 1);
        // Put on the stack in battlefield order (the test controller picks the first one each time): the drain goes
        // last, so it resolves first and drops life below 20 before the others resolve.
        s.Add(P0, Enchantment("Event condition", new TriggeredAbility
        {
            Trigger = TriggerEvent.YouCastSpell, TriggerCondition = new LifeAtLeast(20), Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "event",
        }));
        s.Add(P0, Enchantment("Intervening if", new TriggeredAbility
        {
            Trigger = TriggerEvent.YouCastSpell, Condition = new LifeAtLeast(20), Effects = new Effect[] { new GainLife(100, Subject.You) }, Text = "if",
        }));
        s.Add(P0, Enchantment("Drain", new TriggeredAbility
        {
            Trigger = TriggerEvent.YouCastSpell, Effects = new Effect[] { new LoseLife(5, Subject.You) }, Text = "drain",
        }));
        s.InHand(P0, Spell("Nothing", "{R}", new SpellAbility { Effects = new Effect[] { new GainLife(0, Subject.You) } }));
        await s.RunUntilTurn();
        Assert.Equal(16, Life(s, P0)); // -5, then +1; the intervening "if" no longer holds
    }

    [Fact]
    public async Task SpellsCastBeforeTheTriggeringOneAreFixedByOrder()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, Enchantment("Storm", new TriggeredAbility
        {
            Trigger = TriggerEvent.YouCastSpell, Filter = new ObjectFilter(CardType.Instant),
            Effects = new Effect[] { new GainLife(new Quantity(0, QuantityKind.SpellsCastBeforeTriggered, new ObjectFilter(CardType.Instant, Controller: ControllerFilter.Any)), Subject.You) },
        }));
        s.InHand(P0, Spell("First", "{R}", new SpellAbility { Effects = new Effect[] { new GainLife(0, Subject.You) } }));
        s.InHand(P0, Spell("Second", "{R}", new SpellAbility { Effects = new Effect[] { new GainLife(0, Subject.You) } }));
        await s.RunUntilTurn();
        Assert.Equal(21, Life(s, P0)); // 0 before the first, 1 before the second (cast in response)
    }

    [Fact]
    public async Task CopiesOfAKickedSpellAreKicked()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, Enchantment("Echo", new TriggeredAbility
        {
            Trigger = TriggerEvent.YouCastSpell, Effects = new Effect[] { new CopySpell(Subject.Triggered, 1) },
        }));
        s.InHand(P0, Spell("Bolt", "{R}", new SpellAbility
        {
            Effects = new Effect[] { new IfThen(new WasKicked(), new Effect[] { new GainLife(4, Subject.You) }, new Effect[] { new GainLife(2, Subject.You) }) },
        }) with { Kicker = ManaCost.Parse("{1}") });
        await s.RunUntilTurn();
        Assert.Equal(28, Life(s, P0));
    }

    [Fact]
    public async Task ItsControllerIsTheLastControllerOfAPermanentThatLeft()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var stolen = s.Add(P1, Creature("Stolen", 2, 2));
        s.Card(stolen).Controller = P0;
        s.Card(stolen).BaseController = P0;
        var human = Creature("Human", 1, 1) with { IsToken = true };
        s.InHand(P0, Spell("Midnight", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new ExileIt(Subject.TargetAt(0)), new CreateTokens(human, 1, new Subject(SubjectKind.TargetController)) },
        }));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(stolen) };
        PlayerId? tokenController = null;
        s.Game.EventRaised += e => { if (e is TokenCreated t) tokenController = t.Controller; };
        await s.RunUntilTurn();
        Assert.Equal(P0, tokenController);
    }

    [Fact]
    public async Task LegendRuleKeepsOne()
    {
        var s = new Scenario();
        var legend = Creature("Unique Hero", 2, 2) with { Supertypes = Supertype.Legendary };
        var a = s.Add(P0, legend);
        var b = s.Add(P0, legend);
        var other = s.Add(P1, legend); // another player's copy is fine
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Single(new[] { a, b }, id => s.Card(id).Zone == Zone.Battlefield);
        Assert.Equal(Zone.Battlefield, s.Card(other).Zone);
        Assert.Equal(CardChoicePurpose.Keep, s.Attacker.LastChoice?.Purpose);
    }

    [Fact]
    public async Task CombatDamageTriggersOncePerCreatureWithTheTotal()
    {
        var s = new Scenario();
        var wurm = s.Add(P0, Creature("Wurm", 6, 6, Keyword.Trample) with
        {
            Abilities = new AbilityDefinition[] { new TriggeredAbility { Trigger = TriggerEvent.DealsCombatDamage, Effects = new Effect[] { new GainLife(new Quantity(0, QuantityKind.TriggerAmount), Subject.You) } } },
        });
        var wall = s.Add(P1, Creature("Wall", 0, 2));
        s.Attacker.Attack = (_, attackers, defenders) => new[] { new AttackDeclaration(wurm, defenders[0]) };
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(wall, wurm) };
        int triggers = 0;
        s.Game.EventRaised += e => { if (e is AbilityTriggered t && t.Source == wurm) triggers++; };
        await s.RunUntilTurn();
        Assert.Equal(1, triggers);
        Assert.Equal(26, Life(s, P0));
        Assert.Equal(16, Life(s, P1));
    }

    [Fact]
    public async Task LosingAllAbilitiesFollowsTimestamps()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Enchantment("Banner", new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures), 0, 0, new[] { Keyword.Vigilance })));
        var bird = s.Add(P0, Creature("Bird", 1, 1, Keyword.Flying));
        s.Game.SetupPermanent(P0, Enchantment("Curse", new StaticAbility(new AffectedFilter(AffectedScope.Enchanted)) { LosesAllAbilities = true }) with
        {
            Subtypes = new[] { "Aura" }, EnchantTarget = new TargetSpec(TargetKind.Creature),
        }, attachTo: bird);
        s.InHand(P0, Spell("Surge", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new PumpUntilEndOfTurn(0, 0, Subject.TargetAt(0), new[] { Keyword.Trample }) },
        }));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bird) };
        (bool Flying, bool Vigilance, bool Trample)? after = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) after = (s.Card(bird).Has(Keyword.Flying), s.Card(bird).Has(Keyword.Vigilance), s.Card(bird).Has(Keyword.Trample)); };
        await s.RunUntilTurn();
        Assert.Equal((false, false, true), after); // printed and older grants lost, the newer grant kept
    }

    [Fact]
    public async Task PlayersLosingAtTheSameTimeDraw()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Doomsday", "{R}", new SpellAbility { Effects = new Effect[] { new LoseLife(20, new Subject(SubjectKind.EachPlayer)) } }));
        GameEnded? ended = null;
        s.Game.EventRaised += e => { if (e is GameEnded g) ended = g; };
        await s.RunUntilTurn();
        Assert.NotNull(ended);
        Assert.Null(ended!.Winner);
    }

    [Fact]
    public async Task PermanentsEnteringTogetherSeeEachOther()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var scout = Creature("Scout", 1, 1) with
        {
            IsToken = true,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.CreatureEnters, Filter = new ObjectFilter(CardType.Creature, Other: true),
                    Effects = new Effect[] { new GainLife(1, Subject.You) },
                },
            },
        };
        s.InHand(P0, Spell("Muster", "{R}", new SpellAbility { Effects = new Effect[] { new CreateTokens(scout, 2, Subject.You) } }));
        await s.RunUntilTurn();
        Assert.Equal(22, Life(s, P0));
    }

    [Fact]
    public async Task AnEffectThatChangesTypesRemovesSubtypesThatNoLongerFit()
    {
        var s = new Scenario();
        var arbor = s.Add(P0, new CardDefinition { Name = "Grove Dryad", Types = CardType.Land | CardType.Creature, Subtypes = new[] { "Forest", "Dryad" }, Power = 1, Toughness = 1 });
        s.Game.SetupPermanent(P0, Enchantment("Moon Prison", new StaticAbility(new AffectedFilter(AffectedScope.Enchanted)) { SetTypes = CardType.Land, LosesAllAbilities = true }) with
        {
            Subtypes = new[] { "Aura" }, EnchantTarget = new TargetSpec(TargetKind.Permanent),
        }, attachTo: arbor);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(s.Card(arbor).HasSubtype("Forest"));
        Assert.False(s.Card(arbor).HasSubtype("Dryad"));
        Assert.False(s.Card(arbor).IsCreature);
    }
}
