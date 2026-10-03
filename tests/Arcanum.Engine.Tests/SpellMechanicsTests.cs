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

/// <summary>Modes, X, hybrid, kicker, flashback, ward, extra costs, cost reduction and other casting rules.</summary>
public class SpellMechanicsTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell, CardType type = CardType.Sorcery) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = type, Spell = spell,
    };

    private static SpellAbility Effects(params Effect[] effects) => new() { Effects = effects };

    private static readonly TargetSpec AnyCreature = new(TargetKind.Creature);

    /// <summary>Player 0 casts whatever it can (no land drop) and never attacks; player 1 does nothing.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, _) => PassPriority.Instance;
        return s;
    }

    private Player P(Scenario s, PlayerId id) => s.Game.State.GetPlayer(id);

    [Fact]
    public async Task ModalSpellUsesTheChosenMode()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Charm", "{R}", new SpellAbility
        {
            Modes = new[]
            {
                new Mode("Gain 5 life.", Array.Empty<TargetSpec>(), new Effect[] { new GainLife(5, Subject.You) }),
                new Mode("Destroy target creature.", new[] { AnyCreature }, new Effect[] { new Destroy(Subject.TargetAt(0)) }),
            },
        }));
        s.Attacker.Modes = (_, r) => { Assert.Equal(new[] { 0, 1 }, r.Possible); return new[] { 1 }; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(20, P(s, P0).Life);
    }

    [Fact]
    public async Task ModesWithoutLegalTargetsCantBeChosen()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Charm", "{R}", new SpellAbility
        {
            Modes = new[]
            {
                new Mode("Gain 5 life.", Array.Empty<TargetSpec>(), new Effect[] { new GainLife(5, Subject.You) }),
                new Mode("Destroy target artifact.", new[] { new TargetSpec(TargetKind.Artifact) }, new Effect[] { new Destroy(Subject.TargetAt(0)) }),
            },
        }));
        await s.RunUntilTurn();
        Assert.Equal(25, P(s, P0).Life); // the only possible mode is picked without asking
    }

    [Fact]
    public async Task EachSubjectHitsEveryMatchingPermanent()
    {
        var s = Casting();
        s.Lands(P0, 4);
        var mine = s.Add(P0, Creature("Mine", 2, 2));
        var theirs = s.Add(P1, Creature("Theirs", 3, 3));
        s.InHand(P0, Spell("Wrath", "{4}", Effects(new Destroy(Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any))))));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(mine).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(theirs).Zone);
        Assert.Equal(4, s.Game.State.Battlefield.Count); // the lands survive
    }

    [Fact]
    public async Task XIsChosenAndPaid()
    {
        var s = Casting();
        s.Lands(P0, 4);
        s.InHand(P0, Spell("Blast", "{X}{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
            Effects = new Effect[] { new DealDamage(Quantity.X, Subject.TargetAt(0)) },
        }));
        int offered = -1;
        s.Attacker.Number = (_, r) => { offered = r.Max; return r.Max; };
        await s.RunUntilTurn();
        Assert.Equal(3, offered);
        Assert.Equal(17, P(s, P1).Life);
    }

    [Fact]
    public async Task HybridManaCanBePaidWithEitherColor()
    {
        var s = Casting();
        s.Add(P0, GenericCards.Forest);
        s.InHand(P0, Spell("Duo", "{R/G}", Effects(new GainLife(3, Subject.You))));
        await s.RunUntilTurn();
        Assert.Equal(23, P(s, P0).Life);
    }

    private static CardDefinition Burst => Spell("Burst", "{R}", new SpellAbility
    {
        Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
        Effects = new Effect[] { new IfThen(new WasKicked(), new Effect[] { new DealDamage(4, Subject.TargetAt(0)) }, new Effect[] { new DealDamage(2, Subject.TargetAt(0)) }) },
    }, CardType.Instant) with { Kicker = ManaCost.Parse("{2}") };

    [Fact]
    public async Task KickerIsOfferedWhenAffordable()
    {
        var s = Casting();
        s.Lands(P0, 3);
        s.InHand(P0, Burst);
        await s.RunUntilTurn();
        Assert.Equal(16, P(s, P1).Life);
    }

    [Fact]
    public async Task UnkickedSpellUsesTheOtherBranch()
    {
        var s = Casting();
        s.Lands(P0, 3);
        s.InHand(P0, Burst);
        s.Attacker.YesNo = (_, _) => false;
        await s.RunUntilTurn();
        Assert.Equal(18, P(s, P1).Life);
    }

    [Fact]
    public async Task FlashbackCastsFromTheGraveyardThenExiles()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var card = s.InHand(P0, Spell("Echo", "{R}", Effects(new GainLife(2, Subject.You))) with { Flashback = ManaCost.Parse("{R}") });
        await s.RunUntilTurn();
        Assert.Equal(24, P(s, P0).Life); // cast from hand, then again from the graveyard
        Assert.Equal(Zone.Exile, s.Card(card).Zone);
    }

    [Fact]
    public async Task WardCountersSpellsThatDontPay()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var warded = s.Add(P1, Creature("Warded", 2, 2) with { WardMana = ManaCost.Parse("{2}") });
        s.InHand(P0, Spell("Kill", "{R}", new SpellAbility { Targets = new[] { AnyCreature }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } }));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(warded).Zone);
        Assert.Contains(s.Game.Log, e => e is SpellCountered);
    }

    [Fact]
    public async Task WardIsPaidWhenPossible()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var warded = s.Add(P1, Creature("Warded", 2, 2) with { WardMana = ManaCost.Parse("{2}") });
        s.InHand(P0, Spell("Kill", "{R}", new SpellAbility { Targets = new[] { AnyCreature }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } }));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(warded).Zone);
    }

    [Fact]
    public async Task GraveyardTargetsAndReanimation()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var bear = s.InHand(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Raise", "{1}{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.GraveyardCard, ControllerFilter.You, new ObjectFilter(CardType.Creature)) },
            Effects = new Effect[] { new PutOntoBattlefield(Subject.TargetAt(0)) },
        }));
        // The bear starts in the graveyard: discard it before the game.
        var p0 = P(s, P0);
        p0.Hand.Remove(bear);
        p0.Graveyard.Add(bear);
        s.Card(bear).Zone = Zone.Graveyard;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.Equal(P0, s.Card(bear).Controller);
    }

    [Fact]
    public async Task OptionalTargetsCanBeLeftEmpty()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Maybe", "{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, Optional: true) },
            Effects = new Effect[] { new Destroy(Subject.TargetAt(0)), new GainLife(1, Subject.You) },
        }));
        s.Attacker.Targets = (_, r) => { Assert.Contains(Target.None, r.Legal[0]); return new[] { Target.None }; };
        await s.RunUntilTurn();
        Assert.Equal(21, P(s, P0).Life);
        Assert.Equal(2, s.Game.State.Battlefield.Count);
    }

    [Fact]
    public async Task SearchPutsABasicLandOntoTheBattlefieldTapped()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Ramp", "{R}", Effects(new SearchLibrary(new ObjectFilter(CardType.Land, Supertype: Supertype.Basic), 1, Zone.Battlefield, Tapped: true))));
        s.Attacker.Choose = (_, r) => r.Options.Take(r.Max).Select(o => o.Id).ToList();
        int before = 0;
        s.Game.EventRaised += e => { if (e is SpellCast) before = P(s, P0).Library.Count; };
        CardId? found = null;
        s.Game.EventRaised += e => { if (e is CardMoved { From: Zone.Library, To: Zone.Battlefield } m) found = m.Card; };
        await s.RunUntilTurn();
        Assert.NotNull(found);
        Assert.Contains(s.Game.Log, e => e is LibraryShuffled);
        Assert.Equal(Zone.Battlefield, s.Card(found!.Value).Zone);
    }

    [Fact]
    public async Task OpponentSacrificesACreatureOfTheirChoice()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var small = s.Add(P1, Creature("Small", 1, 1));
        var big = s.Add(P1, Creature("Big", 5, 5));
        s.InHand(P0, Spell("Edict", "{R}", Effects(new Sacrifice(1, new ObjectFilter(CardType.Creature), Subject.EachOpponent))));
        s.Defender.Choose = (_, r) => { Assert.Equal(CardChoicePurpose.Sacrifice, r.Purpose); return new[] { small }; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(small).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(big).Zone);
    }

    [Fact]
    public async Task AdditionalDiscardCostIsPaid()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Rummage", "{R}", Effects(new DrawCards(2, Subject.You))) with { AdditionalCost = new ExtraCost(Discard: 1) });
        await s.RunUntilTurn();
        Assert.Equal(1, s.Game.Log.Count(e => e is CardDiscarded { Player.Value: 0 }));
    }

    [Fact]
    public async Task CostReductionsLowerGenericMana()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition
        {
            Name = "Mentor", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[] { new SpellCostReduction(new ObjectFilter(CardType.Instant | CardType.Sorcery), 2) },
        });
        s.InHand(P0, Spell("Big Idea", "{2}{R}", Effects(new GainLife(4, Subject.You))));
        await s.RunUntilTurn();
        Assert.Equal(24, P(s, P0).Life);
    }

    [Fact]
    public async Task ControlGainedUntilEndOfTurnReturns()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Borrow", "{R}", new SpellAbility { Targets = new[] { AnyCreature }, Effects = new Effect[] { new GainControl(Subject.TargetAt(0), UntilEndOfTurn: true) } }));
        PlayerId? controllerAfterSpell = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) controllerAfterSpell = s.Card(bear).Controller; };
        await s.RunUntilTurn();
        Assert.Equal(P0, controllerAfterSpell);
        Assert.Equal(P1, s.Card(bear).Controller);
    }

    [Fact]
    public async Task UncounterableSpellsIgnoreCounterspells()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        s.Lands(P0, 1);
        s.Lands(P1, 1);
        s.InHand(P0, Spell("Sure Thing", "{R}", Effects(new GainLife(3, Subject.You))) with { CantBeCountered = true });
        s.InHand(P1, Spell("Nope", "{R}", new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Spell) }, Effects = new Effect[] { new CounterSpell(Subject.TargetAt(0)) } }, CardType.Instant));
        await s.RunUntilTurn();
        Assert.Equal(23, P(s, P0).Life);
    }

    [Fact]
    public async Task QuantitiesCountPermanents()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("A", 1, 1));
        s.Add(P0, Creature("B", 1, 1));
        s.InHand(P0, Spell("Census", "{R}", Effects(new GainLife(new Quantity(0, QuantityKind.PermanentCount, ObjectFilter.YourCreatures, Multiplier: 2), Subject.You))));
        await s.RunUntilTurn();
        Assert.Equal(24, P(s, P0).Life);
    }
}
