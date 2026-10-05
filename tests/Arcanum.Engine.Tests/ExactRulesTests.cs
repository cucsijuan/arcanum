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

/// <summary>Target rules, simultaneous deaths, ward, blocking requirements, ordering, mana abilities and kicker.</summary>
public class ExactRulesTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell, CardType type = CardType.Sorcery) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = type, Spell = spell,
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task TargetsMustBeControlledByDifferentPlayers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var mine = s.Add(P0, Creature("Mine", 1, 1));
        var mine2 = s.Add(P0, Creature("Mine Too", 1, 1));
        var theirs = s.Add(P1, Creature("Theirs", 1, 1));
        s.InHand(P0, Spell("Away", "{1}", new SpellAbility
        {
            TargetRule = TargetRule.DifferentControllers,
            Targets = new[] { new TargetSpec(TargetKind.Creature), new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new ReturnToHand(Subject.TargetAt(0)), new ReturnToHand(Subject.TargetAt(1)) },
        }, CardType.Instant));
        s.Attacker.Targets = (_, r) =>
        {
            var first = Target.Of(mine);
            Assert.False(r.IsAllowed(1, Target.Of(mine2), new[] { first })); // same controller
            Assert.True(r.IsAllowed(1, Target.Of(theirs), new[] { first }));
            return new[] { first, Target.Of(theirs) };
        };
        (Zone Mine, Zone Theirs)? after = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) after = (s.Card(mine).Zone, s.Card(theirs).Zone); };
        await s.RunUntilTurn();
        Assert.Equal((Zone.Hand, Zone.Hand), after); // (checked on resolution: cleanup may discard down to hand size)
    }

    [Fact]
    public async Task AnyNumberOfTargets()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var a = s.Add(P1, Creature("A", 1, 1));
        var b = s.Add(P1, Creature("B", 1, 1));
        var c = s.Add(P1, Creature("C", 1, 1));
        s.InHand(P0, Spell("Storm", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) { AnyNumber = true } },
            Effects = new Effect[] { new Destroy(new Subject(SubjectKind.EachTarget)) },
        }));
        s.Attacker.Targets = (_, r) =>
        {
            Assert.True(r.LastIsAnyNumber);
            Assert.False(r.IsAllowed(1, Target.Of(a), new[] { Target.Of(a) })); // no repeats
            return new[] { Target.Of(a), Target.Of(c) };
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(b).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(c).Zone);
    }

    [Fact]
    public async Task CreaturesDyingTogetherSeeEachOther()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var watcher = new TriggeredAbility
        {
            Trigger = TriggerEvent.CreatureDies, Filter = ObjectFilter.YourCreatures with { Other = true },
            Effects = new Effect[] { new GainLife(1, Subject.You) },
        };
        s.Add(P0, Creature("Watcher A", 1, 1) with { Abilities = new AbilityDefinition[] { watcher } });
        s.Add(P0, Creature("Watcher B", 1, 1) with { Abilities = new AbilityDefinition[] { watcher } });
        s.InHand(P0, Spell("Wrath", "{1}", new SpellAbility { Effects = new Effect[] { new Destroy(Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any))) } }));
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // each watcher saw the other die
    }

    [Fact]
    public async Task MustBeBlockedIsARequirement()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Lure", 1, 1, Keyword.MustBeBlocked));
        s.Add(P1, Creature("Wall", 0, 4));
        BlockRequest? request = null;
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        s.Game.EventRaised += _ => { };
        var defender = s.Defender;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            defender.Block = (_, blockers, attackers) => { request = defender.LastBlockRequest; return Array.Empty<BlockDeclaration>(); };
            await s.RunUntilTurn();
        });
        Assert.NotNull(request);
        Assert.Equal(1, request!.MaxRequirements);
        Assert.False(request.IsLegal(Array.Empty<BlockDeclaration>(), out _));
        Assert.True(request.IsLegal(request.WithRequirements(Array.Empty<BlockDeclaration>()), out _));
    }

    [Fact]
    public async Task ScryOrdersTheCardsKept()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Glimpse", "{1}", new SpellAbility { Effects = new Effect[] { new Scry(2) } }));
        var library = s.Game.State.GetPlayer(P0).Library;
        CardId? chosenTop = null;
        s.Attacker.Choose = (_, r) =>
        {
            if (r.Purpose != CardChoicePurpose.Order) return Array.Empty<CardId>(); // keep both on top
            chosenTop = r.Options[^1].Id; // put the second one on top
            return new[] { chosenTop.Value };
        };
        await s.RunUntilTurn();
        Assert.Equal(chosenTop, library[0]);
    }

    private static CardDefinition Filler(string name) => Creature(name, 1, 1);

    /// <summary>Casts a look at the top three taking one, the rest left over per <paramref name="look"/>; returns the cards in order.</summary>
    private static async Task<(Scenario S, CardId[] Cards, CardId Taken, int OrderRequests)> LookAtThree(Func<LookAtTopTake, LookAtTopTake> configure, bool bottomLast = true)
    {
        var s = Casting();
        s.Lands(P0, 1);
        var cards = new[] { s.Game.SetupInLibrary(P0, Filler("Alpha")), s.Game.SetupInLibrary(P0, Filler("Beta")), s.Game.SetupInLibrary(P0, Filler("Gamma")) };
        s.InHand(P0, Spell("Peek", "{1}", new SpellAbility { Effects = new Effect[] { configure(new LookAtTopTake(3, null, 1, Zone.Hand)) } }));
        bool stacked = false;
        var cast = s.Attacker.Act;
        s.Attacker.Act = (v, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, cards); }
            return cast(v, legal);
        };
        int orders = 0;
        s.Attacker.Choose = (_, r) =>
        {
            if (r.Purpose != CardChoicePurpose.Order) return new[] { r.Options[0].Id }; // take the first
            orders++;
            return new[] { bottomLast ? r.Options[^1].Id : r.Options[0].Id };
        };
        await s.RunUntilTurn();
        return (s, cards, cards[0], orders);
    }

    [Fact]
    public async Task RestInAnyOrderLetsThePlayerOrderTheBottom()
    {
        var (s, cards, taken, orders) = await LookAtThree(l => l with { RestOrder = RestOrder.Chosen });
        var library = s.Game.State.GetPlayer(P0).Library;
        Assert.NotEqual(Zone.Library, s.Card(taken).Zone); // taken (it may be discarded down to hand size at the end of the turn)
        Assert.Equal(1, orders);
        // The player puts the last offered card first (deepest): Gamma, then Beta, at the very bottom.
        Assert.Equal(cards[2], library[^2]);
        Assert.Equal(cards[1], library[^1]);
    }

    [Fact]
    public async Task RestInARandomOrderAsksForNoOrder()
    {
        var (s, cards, _, orders) = await LookAtThree(l => l);
        var library = s.Game.State.GetPlayer(P0).Library;
        Assert.Equal(0, orders);
        Assert.Equal(new[] { cards[1], cards[2] }.OrderBy(c => c.Value), library.TakeLast(2).OrderBy(c => c.Value));
    }

    [Fact]
    public async Task RestBackOnTopInAnyOrderLetsThePlayerOrderThem()
    {
        var (s, cards, _, orders) = await LookAtThree(l => l with { RestOnTop = true, RestOrder = RestOrder.Chosen }, bottomLast: true);
        var library = s.Game.State.GetPlayer(P0).Library;
        Assert.Equal(1, orders);
        Assert.Equal(cards[2], library[0]);
        Assert.Equal(cards[1], library[1]);
    }

    [Fact]
    public async Task RestrictedManaOnlyPaysForMatchingSpells()
    {
        var s = new Scenario();
        s.Add(P0, new CardDefinition { Name = "Shrine", Types = CardType.Land, TapForMana = new[] { ManaType.White }, ManaOnlyFor = new ObjectFilter(Subtype: "Angel") });
        var angel = s.InHand(P0, Creature("Angel", 2, 2) with { ManaCost = ManaCost.Parse("{W}"), Subtypes = new[] { "Angel" } });
        var other = s.InHand(P0, Creature("Bear", 2, 2) with { ManaCost = ManaCost.Parse("{W}") });
        var legal = s.Game.GetLegalActions(P0);
        Assert.DoesNotContain(new CastSpell(other), legal);
        // Outside the main phase nothing can be cast at sorcery speed: check payability through the action list on its turn instead.
        s.Attacker.Act = (_, l) => l.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(angel).Zone);
        Assert.Equal(Zone.Hand, s.Card(other).Zone);
    }

    [Fact]
    public async Task KickerIsAnnouncedBeforeTargets()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var a = s.Add(P0, Creature("A", 1, 1));
        var b = s.Add(P0, Creature("B", 1, 1));
        s.InHand(P0, Spell("Resilience", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
            Effects = new Effect[] { new PumpUntilEndOfTurn(5, 0, Subject.TargetAt(0)) },
            WhenKicked = new SpellAbility
            {
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) { AnyNumber = true } },
                Effects = new Effect[] { new PumpUntilEndOfTurn(5, 0, new Subject(SubjectKind.EachTarget)) },
            },
        }, CardType.Instant) with { Kicker = ManaCost.Parse("{2}") });
        s.Attacker.Targets = (_, r) => r.LastIsAnyNumber ? new[] { Target.Of(a), Target.Of(b) } : new[] { Target.Of(a) };
        int powerB = 0;
        s.Game.EventRaised += e => { if (e is SpellResolved) powerB = s.Card(b).Power; };
        await s.RunUntilTurn();
        Assert.Equal(6, powerB);
    }

    [Fact]
    public async Task ChangeTheTargetOfASpell()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Lands(P1, 1);
        var first = s.Add(P1, Creature("First", 1, 1));
        var second = s.Add(P1, Creature("Second", 1, 1));
        s.InHand(P0, Spell("Zap", "{1}", new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } }, CardType.Instant));
        s.InHand(P1, Spell("Bend", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.SpellOrAbility) { SingleTargetOnly = true } },
            Effects = new Effect[] { new ChangeTarget(Subject.TargetAt(0)) },
        }, CardType.Instant));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, r) => new[] { Target.Of(first) };
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 && view.Stack[^1].Controller == P0
            ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        s.Defender.Targets = (_, r) => r.Specs[0].Kind == TargetKind.SpellOrAbility ? new[] { r.Legal[0][0] } : new[] { Target.Of(second) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(first).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(second).Zone);
    }

    [Fact]
    public async Task GrantedAbilityTapsTheGranter()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var pole = s.Add(P0, new CardDefinition
        {
            Name = "Pole", Types = CardType.Artifact, Subtypes = new[] { "Equipment" },
            Abilities = new AbilityDefinition[]
            {
                new StaticAbility(new AffectedFilter(AffectedScope.Equipped))
                {
                    GrantsAbilities = new AbilityDefinition[]
                    {
                        new ActivatedAbility
                        {
                            Cost = new AbilityCost(ManaCost.Parse("{1}"), Tap: true) { TapGranter = true },
                            Effects = new Effect[] { new AddCounters(1, new Subject(SubjectKind.GranterPermanent), CounterKind.Bait) },
                            Text = "bait",
                        },
                    },
                },
            },
        });
        s.Card(pole).AttachedTo = bear;
        s.Card(bear).ControlledSinceTurnStart = true;
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool poleTapped = false;
        s.Game.EventRaised += e => { if (e is CountersPlaced { Kind: CounterKind.Bait }) poleTapped = s.Card(pole).Tapped; };
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(pole).CounterCount(CounterKind.Bait));
        Assert.True(poleTapped);
    }

    [Fact]
    public async Task AnthemsCanFilterByColorCountAndAttachedAuras()
    {
        var s = Casting();
        static CardDefinition Anthem(string name, ObjectFilter filter) => new()
        {
            Name = name, Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures), 1, 1) { Filter = filter } },
        };
        s.Add(P0, Anthem("Many Colors", new ObjectFilter(Multicolored: true)));
        s.Add(P0, Anthem("No Color", new ObjectFilter(Colorless: true)));
        s.Add(P0, Anthem("Enchanted Ones", new ObjectFilter(Enchanted: true)));
        var gold = s.Add(P0, Creature("Gold", 2, 2) with { ManaCost = ManaCost.Parse("{W}{U}") });
        var mono = s.Add(P0, Creature("Mono", 2, 2) with { ManaCost = ManaCost.Parse("{W}") });
        var golem = s.Add(P0, Creature("Golem", 2, 2) with { ManaCost = ManaCost.Parse("{2}"), Types = CardType.Artifact | CardType.Creature });
        var aura = s.Add(P0, new CardDefinition { Name = "Charm", Types = CardType.Enchantment, Subtypes = new[] { "Aura" } });
        s.Card(aura).AttachedTo = mono;
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(gold).Power);
        Assert.Equal(3, s.Card(mono).Power);
        Assert.Equal(3, s.Card(golem).Power);
    }
}
