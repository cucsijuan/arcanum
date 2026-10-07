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
/// Damage events, prevention and redirection, divided damage, random discards and destruction, becomes-target triggers,
/// loyalty −X, X paid with one color and abilities counted per turn (Magic 2010 cards).
/// </summary>
public class DamageAndPreventionTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell, CardType type = CardType.Sorcery) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = type, Spell = spell,
    };

    private static Scenario Casting(int seed = 1)
    {
        var s = new Scenario(seed);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardDefinition Burn(string name, int amount, TargetKind kind = TargetKind.Any, string cost = "{1}") =>
        Spell(name, cost, new SpellAbility { Targets = new[] { new TargetSpec(kind) }, Effects = new Effect[] { new DealDamage(amount, Subject.TargetAt(0)) } }, CardType.Instant);

    private static CardDefinition Bounce(string cost = "{1}") =>
        Spell("Unsummon", cost, new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new ReturnToHand(Subject.TargetAt(0)) } }, CardType.Instant);

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    /// <summary>Damage dealt to a permanent this game (damage itself wears off in the cleanup step).</summary>
    private static int Dealt(Scenario s, CardId id) => s.Game.Log.OfType<DamageDealt>().Where(d => d.TargetCard == id).Sum(d => d.Amount);

    // ---------------------------------------------------------------- prevention

    [Fact]
    public async Task FogPreventsAllCombatDamage()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Brute", 3, 3));
        s.InHand(P0, Spell("Fog", "{1}", new SpellAbility { Effects = new Effect[] { new PreventDamageThisTurn(CombatOnly: true) } }, CardType.Instant));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(20, Life(s, P1));
        Assert.Empty(s.Game.Log.OfType<DamageDealt>());
    }

    [Fact]
    public async Task FogDoesNotPreventNoncombatDamage()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.InHand(P0, Spell("Fog", "{1}", new SpellAbility { Effects = new Effect[] { new PreventDamageThisTurn(CombatOnly: true) } }, CardType.Instant));
        s.InHand(P0, Burn("Bolt", 3, TargetKind.Player));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        await s.RunUntilTurn();
        Assert.Equal(17, Life(s, P1));
    }

    [Fact]
    public async Task GuardianSeraphPreventsOneFromEachOpponentSource()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Seraph", 3, 4) with { Replaces = Replacements.PreventOneDamageFromOpponentsSources });
        s.Add(P0, Creature("Brute", 3, 3));
        s.Add(P0, Creature("Ogre", 2, 2));
        s.InHand(P0, Burn("Bolt", 3, TargetKind.Player));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        // Bolt 3 → 2; Brute 3 → 2 and Ogre 2 → 1 (each source's damage is reduced by 1).
        Assert.Equal(20 - 2 - 2 - 1, Life(s, P1));
    }

    [Fact]
    public async Task SafePassageCoversCreaturesThatArriveLater()
    {
        var s = Casting();
        s.Lands(P0, 4);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var foe = s.Add(P1, Creature("Foe", 2, 2));
        s.InHand(P0, Spell("Safe Passage", "{1}", new SpellAbility
        {
            Effects = new Effect[] { new PreventDamageThisTurn(false, ToYou: true) { ToYourCreatures = true } },
        }, CardType.Instant));
        var late = s.InHand(P0, Creature("Late", 2, 2) with { ManaCost = ManaCost.Parse("{1}") });
        s.InHand(P0, Spell("Pyroclasm", "{2}", new SpellAbility
        {
            Effects = new Effect[] { new DealDamage(3, new Subject(SubjectKind.Each, Filter: new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any))), new DealDamage(3, Subject.EachOpponent), new DealDamage(3, Subject.You) },
        }));
        // Safe Passage first, then the creature, then the sweeper.
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            var casts = legal.OfType<CastSpell>().ToList();
            var order = new[] { "Safe Passage", "Late", "Pyroclasm" };
            if (step < 3 && casts.FirstOrDefault(c => s.Card(c.Card).Name == order[step]) is { } next) { step++; return next; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(late).Zone);
        Assert.Equal(0, Dealt(s, late));
        Assert.Equal(Zone.Graveyard, s.Card(foe).Zone);
        Assert.Equal(20, Life(s, P0));
        Assert.Equal(17, Life(s, P1));
    }

    [Fact]
    public async Task MagebaneArmorRemovesFlyingAndPreventsNoncombatDamage()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bird = s.Add(P0, Creature("Bird", 1, 1, Keyword.Flying));
        var armor = s.Add(P0, new CardDefinition
        {
            Name = "Magebane Armor", Types = CardType.Artifact, Subtypes = new[] { "Equipment" },
            Abilities = new AbilityDefinition[]
            {
                new StaticAbility(new AffectedFilter(AffectedScope.Equipped), 2, 4) { LosesKeywords = new[] { Keyword.Flying } },
                new StaticAbility(new AffectedFilter(AffectedScope.Equipped)) { PreventsDamage = StaticDamagePrevention.Noncombat },
            },
        });
        s.Card(armor).AttachedTo = bird;
        s.InHand(P0, Burn("Bolt", 3, TargetKind.Creature));
        await s.RunUntilTurn();
        Assert.False(s.Card(bird).Has(Keyword.Flying));
        Assert.Equal(3, s.Card(bird).Power);
        Assert.Equal(5, s.Card(bird).Toughness);
        Assert.Equal(0, Dealt(s, bird));
        Assert.Contains(s.Game.Log, e => e is SpellResolved);
    }

    [Fact]
    public async Task ALaterGrantGivesBackALostKeyword()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bird = s.Add(P0, Creature("Bird", 1, 1, Keyword.Flying));
        var armor = s.Add(P0, new CardDefinition
        {
            Name = "Armor", Types = CardType.Artifact, Subtypes = new[] { "Equipment" },
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Equipped)) { LosesKeywords = new[] { Keyword.Flying } } },
        });
        s.Card(armor).AttachedTo = bird;
        s.InHand(P0, Spell("Wings", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new PumpUntilEndOfTurn(0, 0, Subject.TargetAt(0), new[] { Keyword.Flying }) },
        }, CardType.Instant));
        bool? flyingAfter = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) flyingAfter = s.Card(bird).Has(Keyword.Flying); };
        await s.RunUntilTurn();
        Assert.True(flyingAfter); // the later effect's timestamp wins (rule 613.7)
    }

    // ---------------------------------------------------------------- Protean Hydra

    private static CardDefinition Hydra() => Creature("Hydra", 0, 0) with
    {
        Replaces = Replacements.PreventDamageRemoveCounters,
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility
            {
                Trigger = TriggerEvent.CounterRemoved,
                Effects = new Effect[] { new AtNextEndStepAbout(Subject.Self, new Effect[] { new AddCounters(2, Subject.Triggered) }) },
                Text = "Whenever a +1/+1 counter is removed from this creature, put two +1/+1 counters on it at the beginning of the next end step.",
            },
        },
    };

    [Fact]
    public async Task ProteanHydraTakingThreeDamageLosesThreeCountersAndGetsSixBack()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var hydra = s.Add(P0, Hydra());
        s.Card(hydra).Counters[CounterKind.PlusOnePlusOne] = 4;
        s.InHand(P0, Burn("Bolt", 3, TargetKind.Creature));
        int? countersAfterBolt = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) countersAfterBolt = s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne); };
        await s.RunUntilTurn();
        Assert.Equal(1, countersAfterBolt);
        Assert.Equal(0, Dealt(s, hydra));
        Assert.Equal(3, s.Game.Log.OfType<AbilityTriggered>().Count(t => t.Source == hydra && t.Text.StartsWith("Whenever")));
        Assert.Equal(7, s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne)); // three delayed triggers at the end step
    }

    [Fact]
    public async Task HydraOwnerOrdersPreventionEffects()
    {
        // Prevent-all (a Fog for all damage) and the Hydra's own prevention both apply: its controller chooses which first.
        foreach (bool hydraFirst in new[] { true, false })
        {
            var s = Casting();
            s.Lands(P0, 2);
            var hydra = s.Add(P0, Hydra());
            s.Card(hydra).Counters[CounterKind.PlusOnePlusOne] = 4;
            s.InHand(P0, Spell("Holy Day", "{1}", new SpellAbility { Effects = new Effect[] { new PreventDamageThisTurn(false) } }, CardType.Instant));
            s.InHand(P0, Burn("Bolt", 2, TargetKind.Creature));
            int step = 0;
            s.Attacker.Act = (view, legal) =>
            {
                var casts = view.Stack.Count == 0 ? legal.OfType<CastSpell>().ToList() : new List<CastSpell>();
                var order = new[] { "Holy Day", "Bolt" };
                if (step < 2 && casts.FirstOrDefault(c => s.Card(c.Card).Name == order[step]) is { } next) { step++; return next; }
                return PassPriority.Instance;
            };
            s.Attacker.Option = (_, r) => r.Options.ToList().FindIndex(o => o.StartsWith(hydraFirst ? "Prevent it and remove" : "Prevent all"));
            int? counters = null;
            s.Game.EventRaised += e => { if (e is SpellResolved r && s.Card(r.Card).Name == "Bolt") counters = s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne); };
            await s.RunUntilTurn();
            Assert.Equal(hydraFirst ? 2 : 4, counters);
        }
    }

    // ---------------------------------------------------------------- redirection (Harm's Way)

    [Fact]
    public async Task HarmsWayRedirectsTheNextTwoDamageAcrossTwoEvents()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var wall = s.Add(P0, Creature("Wall", 0, 5));
        var pinger = s.Add(P0, Creature("Pinger", 1, 1) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Zero), Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
                    Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) }, Text = "{0}: 1 damage to target creature you control.",
                },
            },
        });
        s.InHand(P0, Spell("Harm's Way", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Any) },
            Effects = new Effect[] { new RedirectNextDamage(2, Subject.TargetAt(0)) },
        }, CardType.Instant));
        int pings = 0;
        bool shielded = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (!shielded && view.Stack.Count > 0 && legal.OfType<CastSpell>().FirstOrDefault() is { } harm) { shielded = true; return harm; }
            if (view.Stack.Count == 0 && pings < 3 && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == pinger) is { } ping) { pings++; return ping; }
            return PassPriority.Instance;
        };
        s.Attacker.Targets = (_, r) => r.Specs[0].Kind == TargetKind.Any ? new[] { Target.Of(P1) } : new[] { Target.Of(wall) };
        s.Attacker.Choose = (_, r) => r.Purpose == CardChoicePurpose.DamageSource ? new[] { pinger } : r.Options.Take(r.Min).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(3, pings);
        Assert.Equal(18, Life(s, P1)); // the first two pings went to the opponent
        Assert.Equal(1, Dealt(s, wall)); // the third reached the wall
    }

    [Fact]
    public async Task HarmsWayChoosesWhichSimultaneousDamageIsRedirected()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var a = s.Add(P0, Creature("A", 0, 5));
        var b = s.Add(P0, Creature("B", 0, 5));
        var quake = s.InHand(P0, Spell("Quake", "{1}", new SpellAbility
        {
            Effects = new Effect[] { new DealDamage(2, new Subject(SubjectKind.Each, Filter: new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any))) },
        }));
        s.InHand(P0, Spell("Harm's Way", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Any) },
            Effects = new Effect[] { new RedirectNextDamage(2, Subject.TargetAt(0)) },
        }, CardType.Instant));
        s.Attacker.Act = (view, legal) =>
        {
            var casts = legal.OfType<CastSpell>().ToList();
            if (view.Stack.Count == 0 && casts.FirstOrDefault(c => c.Card == quake) is { } q) return q;
            if (view.Stack.Count == 1 && casts.FirstOrDefault(c => c.Card != quake) is { } h) return h;
            return PassPriority.Instance;
        };
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        s.Attacker.Choose = (_, r) => r.Purpose == CardChoicePurpose.DamageSource ? new[] { quake } : r.Options.Take(r.Min).Select(o => o.Id).ToList();
        var asked = new List<NumberRequest>();
        s.Attacker.Number = (_, r) => { asked.Add(r); return asked.Count == 1 ? 1 : r.Max; }; // 1 from A, then 1 from B
        await s.RunUntilTurn();
        Assert.Single(asked);
        Assert.Equal(18, Life(s, P1));
        Assert.Equal(1, Dealt(s, a));
        Assert.Equal(1, Dealt(s, b));
    }

    // ---------------------------------------------------------------- divided damage

    private static CardDefinition Hellfire() => Spell("Hellfire", "{1}", new SpellAbility
    {
        Targets = new[] { new TargetSpec(TargetKind.Any) { AnyNumber = true } },
        Effects = new Effect[] { new DealDamageDivided(5) },
    }, CardType.Instant);

    [Fact]
    public async Task DividedDamageIsAnnouncedAsItIsCastAndNotRedistributed()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Lands(P1, 1);
        var a = s.Add(P1, Creature("A", 1, 10));
        var b = s.Add(P1, Creature("B", 1, 10));
        s.InHand(P0, Hellfire());
        s.InHand(P1, Bounce());
        s.Attacker.Targets = (_, r) => new[] { Target.Of(a), Target.Of(b) };
        bool cast = false;
        s.Attacker.Number = (view, r) => { Assert.False(cast); return 2; }; // 2 to A, 3 to B, before the spell is on the stack
        s.Game.EventRaised += e => { if (e is SpellCast c && s.Card(c.Card).Name == "Hellfire") cast = true; };
        s.Defender.Act = (view, legal) => view.Stack.Count == 1 && view.Stack[0].Controller == P0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        s.Defender.Targets = (_, r) => new[] { Target.Of(a) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(a).Zone);
        Assert.Equal(3, Dealt(s, b)); // A's 2 aren't dealt to B (rule 608.2b)
    }

    [Fact]
    public async Task DividedDamageCantHaveMoreTargetsThanDamage()
    {
        var s = Casting();
        s.Lands(P0, 1);
        for (int i = 0; i < 7; i++) s.Add(P1, Creature($"C{i}", 1, 1));
        s.InHand(P0, Hellfire());
        TargetRequest? request = null;
        s.Attacker.Targets = (v, r) => { request = r; return TestController.FirstAllowed(v, r); };
        await s.RunUntilTurn();
        Assert.Equal(5, request!.MaxCount);
    }

    private static CardDefinition Fireball() => Spell("Fireball", "{X}{R}", new SpellAbility
    {
        TargetRule = TargetRule.AllDifferent,
        Targets = new[] { new TargetSpec(TargetKind.Any), new TargetSpec(TargetKind.Any) { AnyNumber = true } },
        Effects = new Effect[] { new DealDamageDividedEvenly(new Quantity(0, QuantityKind.X)) },
    }) with { ExtraTargetCost = 1 };

    [Fact]
    public async Task FireballCostsMorePerExtraTargetAndDividesEvenly()
    {
        var s = Casting();
        s.Lands(P0, 7);
        var a = s.Add(P1, Creature("A", 1, 10));
        var b = s.Add(P1, Creature("B", 1, 10));
        s.InHand(P0, Fireball());
        TargetRequest? request = null;
        s.Attacker.Number = (_, r) => 4; // X = 4
        s.Attacker.Targets = (_, r) => { request = r; return new[] { Target.Of(a), Target.Of(b), Target.Of(P1) }; };
        await s.RunUntilTurn();
        Assert.Equal(3, request!.MaxCount); // {4}{R} plus {1} for each of two more targets: 7 lands
        Assert.Equal(1, Dealt(s, a)); // 4 / 3 rounded down
        Assert.Equal(1, Dealt(s, b));
        Assert.Equal(19, Life(s, P1));
        Assert.Equal(0, s.Game.State.Battlefield.Select(s.Card).Count(c => c.Controller == P0 && !c.Tapped && c.Is(CardType.Land)));
    }

    [Fact]
    public async Task FireballDividesAmongTheTargetsStillLegal()
    {
        var s = Casting();
        s.Lands(P0, 6);
        s.Lands(P1, 1);
        var a = s.Add(P1, Creature("A", 1, 10));
        var b = s.Add(P1, Creature("B", 1, 10));
        s.InHand(P0, Fireball());
        s.InHand(P1, Bounce());
        s.Attacker.Number = (_, r) => 4;
        s.Attacker.Targets = (_, r) => new[] { Target.Of(a), Target.Of(b) };
        s.Defender.Act = (view, legal) => view.Stack.Count == 1 && view.Stack[0].Controller == P0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        s.Defender.Targets = (_, r) => new[] { Target.Of(a) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(a).Zone);
        Assert.Equal(4, Dealt(s, b));
    }

    // ---------------------------------------------------------------- random discard

    private static CardDefinition MindShatter(int count) => Spell("Shatter", "{1}", new SpellAbility
    {
        Targets = new[] { new TargetSpec(TargetKind.Player) },
        Effects = new Effect[] { new Discard(count, Subject.TargetAt(0)) { AtRandom = true } },
    });

    [Fact]
    public async Task RandomDiscardIsNotChosenAndIsReplayable()
    {
        async Task<List<CardId>> Run()
        {
            var s = Casting(seed: 7);
            s.Lands(P0, 1);
            s.InHand(P0, MindShatter(2));
            s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
            s.Defender.Discard = (_, _) => throw new InvalidOperationException("A random discard isn't chosen.");
            await s.RunUntilTurn();
            Assert.Equal(5, s.Game.State.GetPlayer(P1).Hand.Count);
            return s.Game.Log.OfType<CardDiscarded>().Where(d => d.Player == P1).Select(d => d.Card).ToList();
        }
        var first = await Run();
        Assert.Equal(2, first.Count);
        Assert.Equal(first, await Run()); // same seed, same cards
    }

    [Fact]
    public async Task RandomDiscardWithFewerCardsDiscardsTheWholeHand()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, MindShatter(10));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        await s.RunUntilTurn();
        Assert.Empty(s.Game.State.GetPlayer(P1).Hand);
        Assert.Equal(7, s.Game.Log.OfType<CardDiscarded>().Count(d => d.Player == P1));
    }

    [Fact]
    public async Task HypnoticSpecterTriggersOnCombatAndNoncombatDamageToAnOpponent()
    {
        var s = Casting();
        var specter = s.Add(P0, Creature("Specter", 2, 2, Keyword.Flying) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.DealsDamageToOpponent,
                    Effects = new Effect[] { new Discard(1, Subject.TriggeredPlayer) { AtRandom = true } },
                    Text = "Whenever this creature deals damage to an opponent, that player discards a card at random.",
                },
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Zero), OncePerTurn = true, Targets = new[] { new TargetSpec(TargetKind.Player) },
                    Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) }, Text = "{0}: 1 damage to target player.",
                },
            },
        });
        s.Attacker.Act = (view, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(17, Life(s, P1));
        Assert.Equal(2, s.Game.Log.OfType<CardDiscarded>().Count(d => d.Player == P1));
        Assert.Equal(5, s.Game.State.GetPlayer(P1).Hand.Count);
    }

    // ---------------------------------------------------------------- random destruction

    private static CardDefinition Efreet() => Creature("Efreet", 6, 4) with
    {
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility
            {
                Trigger = TriggerEvent.YourUpkeep, TargetRule = TargetRule.AllDifferent,
                Targets = new[]
                {
                    new TargetSpec(TargetKind.Permanent, ControllerFilter.You, new ObjectFilter(ExcludedTypes: CardType.Land)),
                    new TargetSpec(TargetKind.Permanent, ControllerFilter.Opponent, new ObjectFilter(ExcludedTypes: CardType.Land), Optional: true),
                    new TargetSpec(TargetKind.Permanent, ControllerFilter.Opponent, new ObjectFilter(ExcludedTypes: CardType.Land), Optional: true),
                },
                Effects = new Effect[] { new DestroyOneAtRandom(new Subject(SubjectKind.EachTarget)) },
                Text = "Destroy one of them at random.",
            },
        },
    };

    [Fact]
    public async Task CapriciousEfreetDestroysExactlyOneAtRandom()
    {
        var outcomes = new HashSet<string>();
        for (int seed = 1; seed <= 12; seed++)
        {
            var s = Casting(seed);
            var efreet = s.Add(P0, Efreet());
            var a = s.Add(P1, Creature("A", 1, 1));
            var b = s.Add(P1, Creature("B", 1, 1));
            s.Attacker.Targets = (_, r) => new[] { Target.Of(efreet), Target.Of(a), Target.Of(b) };
            await s.RunUntilTurn();
            var gone = new[] { efreet, a, b }.Where(id => s.Card(id).Zone == Zone.Graveyard).ToList();
            Assert.Single(gone);
            outcomes.Add(s.Card(gone[0]).Name);
        }
        Assert.True(outcomes.Count >= 2); // it's random, not always the same one
    }

    [Fact]
    public async Task CapriciousEfreetPicksOnlyAmongLegalTargets()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            var s = Casting(seed);
            var efreet = s.Add(P0, Efreet());
            var a = s.Add(P1, Creature("A", 1, 1));
            // Only its own target chosen: it's always the one destroyed.
            s.Attacker.Targets = (_, r) => new[] { Target.Of(efreet), Target.None, Target.None };
            await s.RunUntilTurn();
            Assert.Equal(Zone.Graveyard, s.Card(efreet).Zone);
            Assert.Equal(Zone.Battlefield, s.Card(a).Zone);
        }
    }

    // ---------------------------------------------------------------- Manabarbs

    [Fact]
    public async Task ManabarbsTriggersForLandsTappedWhilePayingASpell()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, new CardDefinition
        {
            Name = "Manabarbs", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.PlayerTapsLandForMana, Effects = new Effect[] { new DealDamage(1, Subject.TriggeredPlayer) },
                    Text = "Whenever a player taps a land for mana, this enchantment deals 1 damage to that player.",
                },
            },
        });
        s.InHand(P0, Spell("Divination", "{2}", new SpellAbility { Effects = new Effect[] { new GainLife(1, Subject.You) } }));
        var order = new List<string>();
        s.Game.EventRaised += e =>
        {
            if (e is DamageDealt) order.Add("damage");
            if (e is SpellResolved) order.Add("spell");
        };
        await s.RunUntilTurn();
        Assert.Equal(new[] { "damage", "damage", "spell" }, order); // the triggers go on the stack above the spell
        Assert.Equal(19, Life(s, P0));
    }

    // ---------------------------------------------------------------- becomes-target triggers, Ice Cage

    [Fact]
    public async Task IllusionaryServantIsSacrificedWhenItsControllerTargetsIt()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var servant = s.Add(P0, Creature("Servant", 3, 4, Keyword.Flying) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.BecomesTarget, Effects = new Effect[] { new SacrificeIt(Subject.Self) }, Text = "When this creature becomes the target of a spell or ability, sacrifice it." },
            },
        });
        s.InHand(P0, Spell("Giant Growth", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0)) },
        }, CardType.Instant));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(servant).Zone);
        Assert.Contains(s.Game.Log, e => e is FizzledOnResolution);
    }

    [Fact]
    public async Task IceCageStopsActivatedAbilitiesAndIsDestroyedWhenTheCreatureIsTargeted()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var elf = s.Add(P0, Creature("Elf", 1, 1) with
        {
            TapForMana = new[] { ManaType.Green },
            Abilities = new AbilityDefinition[] { new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero), Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "{0}: gain 1 life." } },
        });
        var cage = s.Add(P0, new CardDefinition
        {
            Name = "Ice Cage", Types = CardType.Enchantment, Subtypes = new[] { "Aura" }, EnchantTarget = new TargetSpec(TargetKind.Creature),
            Abilities = new AbilityDefinition[]
            {
                new StaticAbility(new AffectedFilter(AffectedScope.Enchanted), Keywords: new[] { Keyword.CantAttack, Keyword.CantBlock }) { CantActivateAbilities = true },
                new TriggeredAbility { Trigger = TriggerEvent.AttachedBecomesTarget, Effects = new Effect[] { new Destroy(Subject.Self) }, Text = "When enchanted creature becomes the target of a spell or ability, destroy this Aura." },
            },
        });
        s.Card(cage).AttachedTo = elf;
        IReadOnlyList<PlayerAction>? firstLegal = null;
        s.Attacker.Act = (_, legal) =>
        {
            firstLegal ??= legal;
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.InHand(P0, Spell("Giant Growth", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0)) },
        }, CardType.Instant));
        await s.RunUntilTurn();
        Assert.DoesNotContain(firstLegal!, a => a is ActivateAbility x && x.Source == elf || a is ActivateManaAbility m && m.Source == elf);
        Assert.Equal(Zone.Graveyard, s.Card(cage).Zone);
        Assert.Contains(s.Game.Log, e => e is SpellResolved r && s.Card(r.Card).Name == "Giant Growth"); // the spell still resolved
    }

    // ---------------------------------------------------------------- costs

    private static CardDefinition ConsumeSpirit() => Spell("Consume Spirit", "{X}{1}{B}", new SpellAbility
    {
        Targets = new[] { new TargetSpec(TargetKind.Any) },
        Effects = new Effect[] { new DealDamage(new Quantity(0, QuantityKind.X), Subject.TargetAt(0)), new GainLife(new Quantity(0, QuantityKind.X), Subject.You) },
    }) with { XManaType = ManaType.Black };

    [Theory]
    [InlineData(2, 3, 1)] // one Swamp for {B}, one for X
    [InlineData(4, 1, 3)]
    public async Task ConsumeSpiritSpendsOnlyBlackManaOnX(int swamps, int mountains, int maxX)
    {
        var s = Casting();
        for (int i = 0; i < swamps; i++) s.Add(P0, GenericCards.Swamp);
        s.Lands(P0, mountains);
        s.InHand(P0, ConsumeSpirit());
        NumberRequest? asked = null;
        s.Attacker.Number = (_, r) => { asked = r; return r.Max; };
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        await s.RunUntilTurn();
        Assert.Equal(maxX, asked!.Max);
        Assert.Equal(20 - maxX, Life(s, P1));
        Assert.Equal(20 + maxX, Life(s, P0));
    }

    [Fact]
    public async Task DragonWhelpIsSacrificedAfterFourActivations()
    {
        var s = Casting();
        s.Lands(P0, 7);
        CardDefinition Whelp(string name) => Creature(name, 2, 3, Keyword.Flying) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Parse("{R}")),
                    Effects = new Effect[]
                    {
                        new PumpUntilEndOfTurn(1, 0, Subject.Self),
                        new IfThen(new ActivatedThisTurn(4), new Effect[] { new AtNextEndStepAbout(Subject.Self, new Effect[] { new SacrificeIt(Subject.Triggered) }) }),
                    },
                    Text = "{R}: +1/+0. If this ability has been activated four or more times this turn, sacrifice this creature at the beginning of the next end step.",
                },
            },
        };
        var four = s.Add(P0, Whelp("Four"));
        var three = s.Add(P0, Whelp("Three"));
        var activations = new Dictionary<CardId, int> { [four] = 0, [three] = 0 };
        int? fourPower = null;
        s.Attacker.Act = (view, legal) =>
        {
            foreach (var (id, wanted) in new[] { (four, 4), (three, 3) })
                if (activations[id] < wanted && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == id) is { } act) { activations[id]++; return act; }
            if (view.Stack.Count == 0) fourPower ??= s.Card(four).Power;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(6, fourPower);
        Assert.Equal(Zone.Graveyard, s.Card(four).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(three).Zone);
    }

    // ---------------------------------------------------------------- Chandra Nalaar

    [Fact]
    public async Task LoyaltyMinusXChoosesXUpToLoyalty()
    {
        var s = Casting();
        var walker = s.Add(P0, new CardDefinition
        {
            Name = "Chandra", Types = CardType.Planeswalker, Loyalty = 6,
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Zero) { LoyaltyX = true }, Targets = new[] { new TargetSpec(TargetKind.Creature) },
                    Effects = new Effect[] { new DealDamage(new Quantity(0, QuantityKind.X), Subject.TargetAt(0)) }, Text = "−X: X damage to target creature.",
                },
            },
        });
        s.Card(walker).Counters[CounterKind.Loyalty] = 6;
        var foe = s.Add(P1, Creature("Foe", 3, 3));
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        NumberRequest? asked = null;
        s.Attacker.Number = (_, r) => { asked = r; return 3; };
        await s.RunUntilTurn();
        Assert.Equal(6, asked!.Max);
        Assert.Equal(3, s.Card(walker).CounterCount(CounterKind.Loyalty));
        Assert.Equal(Zone.Graveyard, s.Card(foe).Zone);
        Assert.Single(s.Game.Log.OfType<AbilityActivated>()); // one loyalty ability per turn
    }

    [Fact]
    public async Task ChandraUltimateIsOneDamageEvent()
    {
        var s = Casting();
        var walker = s.Add(P0, new CardDefinition
        {
            Name = "Chandra", Types = CardType.Planeswalker, Loyalty = 8,
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Zero) { Loyalty = -8 }, Targets = new[] { new TargetSpec(TargetKind.PlayerOrPlaneswalker) },
                    Effects = new Effect[]
                    {
                        new Simultaneously(new Effect[]
                        {
                            new DealDamage(10, Subject.TargetAt(0)),
                            new DealDamage(10, new Subject(SubjectKind.Each, Filter: new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any)) { ControlledByTarget = true }),
                        }),
                    },
                    Text = "−8",
                },
            },
        });
        s.Card(walker).Counters[CounterKind.Loyalty] = 8;
        var foe = s.Add(P1, Creature("Foe", 3, 3));
        var mine = s.Add(P0, Creature("Mine", 3, 3));
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        await s.RunUntilTurn();
        Assert.Equal(10, Life(s, P1));
        Assert.Equal(Zone.Graveyard, s.Card(foe).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(mine).Zone);
    }

    [Fact]
    public async Task DamageThisWayAndExcessDamageCountOnceASimultaneousEventIsDealt()
    {
        // Inside "simultaneously" the damage is dealt when the whole event is put together: "excess damage" and
        // "each creature dealt damage this way" still see it (rule 120.4a).
        var s = Casting();
        s.InHand(P0, Spell("Overload", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[]
            {
                new Simultaneously(new Effect[] { new DealDamage(5, Subject.TargetAt(0)) }),
                new GainLife(new Quantity(0, QuantityKind.ExcessDamage), Subject.You),
                new ExileIt(new Subject(SubjectKind.DamagedThisWay)),
            },
        }));
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        await s.RunUntilTurn();
        Assert.Equal(23, Life(s, P0));
        Assert.Equal(Zone.Exile, s.Card(bear).Zone);
    }

    // ---------------------------------------------------------------- last known information, turn order

    private static CardDefinition Mine(bool tapCost) => new()
    {
        Name = "Mine", Types = CardType.Artifact,
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility { Trigger = TriggerEvent.YourUpkeep, Condition = new SourceUntapped(), Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "If this is untapped, gain 1 life." },
            new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero, Tap: tapCost, SacrificeSelf: true), Effects = Array.Empty<Effect>(), Text = "Sacrifice this." },
        },
    };

    [Theory]
    [InlineData(false, 21)]
    [InlineData(true, 20)]
    public async Task IfUntappedUsesLastKnownInformation(bool tapCost, int life)
    {
        var s = Casting();
        s.Add(P0, Mine(tapCost));
        s.Attacker.Act = (view, legal) => view.Stack.Count > 0 && legal.OfType<ActivateAbility>().FirstOrDefault() is { } sac ? sac : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(life, Life(s, P0)); // sacrificed untapped: it was untapped as it last existed (rule 608.2h)
    }

    [Fact]
    public async Task EachPlayerDrawsInTurnOrderFromTheActivePlayer()
    {
        var p0 = new TestController();
        var p1 = new TestController();
        var lands = Decks.Of((GenericCards.Mountain, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P1 }, new[] { new PlayerSetup("A", p0, lands), new PlayerSetup("B", p1, lands) });
        game.SetupPermanent(P1, GenericCards.Mountain);
        game.SetupInHand(P1, Spell("Words", "{1}", new SpellAbility { Effects = new Effect[] { new DrawCards(1, new Subject(SubjectKind.EachPlayer)) } }));
        p1.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        p1.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var draws = new List<PlayerId>();
        bool resolving = false;
        game.EventRaised += e =>
        {
            if (e is SpellCast) resolving = true;
            if (resolving && e is CardDrawn d) draws.Add(d.Player);
            if (e is SpellResolved) resolving = false;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == 2) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(new[] { P1, P0 }, draws);
    }
}
