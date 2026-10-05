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

/// <summary>Conditional statics, characteristic-defining P/T, linked exile, delayed actions, copies and replacement effects.</summary>
public class ContinuousAndReplacementTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell, CardType type = CardType.Sorcery) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = type, Spell = spell,
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardDefinition Enchantment(string name, params AbilityDefinition[] abilities) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment, Abilities = abilities,
    };

    [Fact]
    public async Task ConditionalStaticAppliesOnlyWhileItHolds()
    {
        var s = new Scenario();
        var knight = s.Add(P0, Creature("Knight", 2, 2) with
        {
            Abilities = new AbilityDefinition[]
            {
                new StaticAbility(new AffectedFilter(AffectedScope.Self), 1, 0)
                {
                    While = new YouControl(new ObjectFilter(Colors: new[] { "B" }, Controller: ControllerFilter.Any)),
                },
            },
        });
        Assert.Equal(2, s.Card(knight).Power);
        s.Game.SetupPermanent(P1, Creature("Shade", 1, 1) with { Colors = new[] { "B" } });
        await s.RunUntilTurn(1);
        Assert.Equal(3, s.Card(knight).Power);
    }

    [Fact]
    public async Task CharacteristicDefiningPowerCountsCreatures()
    {
        var s = new Scenario();
        var crusader = s.Add(P0, Creature("Crusader", 0, 0) with
        {
            PowerFrom = new Quantity(0, QuantityKind.PermanentCount, ObjectFilter.YourCreatures),
            ToughnessFrom = new Quantity(0, QuantityKind.PermanentCount, ObjectFilter.YourCreatures),
        });
        s.Add(P0, Creature("A", 1, 1));
        s.Add(P0, Creature("B", 1, 1));
        await s.RunUntilTurn(1);
        Assert.Equal(3, s.Card(crusader).Power);
        Assert.Equal(3, s.Card(crusader).Toughness);
    }

    [Fact]
    public async Task ExiledUntilTheSourceLeavesComesBack()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var light = s.InHand(P0, Enchantment("Banishing Glow", new TriggeredAbility
        {
            Trigger = TriggerEvent.EntersBattlefield,
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
            Effects = new Effect[] { new ExileUntilSourceLeaves(Subject.TargetAt(0)) },
        }));
        s.InHand(P0, Spell("Shatter", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Enchantment) },
            Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) },
        }));
        Zone? bearZoneWhileExiled = null;
        s.Game.EventRaised += e => { if (e is SpellCast c && s.Card(c.Card).Name == "Shatter") bearZoneWhileExiled = s.Card(bear).Zone; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, bearZoneWhileExiled);
        Assert.Equal(Zone.Graveyard, s.Card(light).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.Equal(P1, s.Card(bear).Controller);
    }

    [Fact]
    public async Task FlickeredCreatureReturnsAtTheNextEndStep()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Blink", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
            Effects = new Effect[] { new ExileAndReturnAtEndStep(Subject.TargetAt(0)) },
        }));
        Zone? afterSpell = null;
        s.Game.EventRaised += e => { if (e is SpellResolved) afterSpell = s.Card(bear).Zone; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, afterSpell);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
    }

    [Fact]
    public async Task TokenCopiesWithHasteAreSacrificedAtEndStep()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Duplicate", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
            Effects = new Effect[] { new CreateTokenCopy(Subject.TargetAt(0), 1, Haste: true, SacrificeAtEndStep: true) },
        }));
        CardId? token = null;
        s.Game.EventRaised += e => { if (e is TokenCreated t) token = t.Card; };
        bool hadHaste = false;
        s.Game.EventRaised += e => { if (e is SpellResolved && token is { } id) hadHaste = s.Card(id).Has(Keyword.Haste); };
        await s.RunUntilTurn();
        Assert.True(hadHaste);
        Assert.Contains(s.Game.Log, e => e is PermanentSacrificed p && p.Card == token);
    }

    [Fact]
    public async Task MayPayOnlyHappensWhenPaid()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.InHand(P0, Spell("Offer", "{1}", new SpellAbility
        {
            Effects = new Effect[] { new MayPay("Pay {1}?", ManaCost.Parse("{1}"), null, new Effect[] { new GainLife(5, Subject.You) }) },
        }));
        await s.RunUntilTurn();
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ExileInsteadOfDying()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Smite", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new ExileIfDiesThisTurn(Subject.TargetAt(0)), new DealDamage(3, Subject.TargetAt(0)) },
        }));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(bear).Zone);
    }

    [Fact]
    public async Task DamageDoublingAndPrevention()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, Enchantment("Fury") with { Replaces = Replacements.DoubleDamageToOpponents });
        var wall = s.Add(P1, Creature("Wall", 0, 10) with { Replaces = Replacements.PreventCombatDamageToAndBySelf });
        s.InHand(P0, Spell("Bolt", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
            Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) },
        }, CardType.Instant));
        s.InHand(P0, Spell("Zap", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
            Effects = new Effect[] { new DealDamage(2, Subject.TargetAt(0)) },
        }, CardType.Instant));
        var wallDamage = new List<int>();
        s.Game.EventRaised += e => { if (e is DamageDealt d && d.TargetCard == wall) wallDamage.Add(d.Amount); };
        await s.RunUntilTurn();
        Assert.Equal(14, s.Game.State.GetPlayer(P1).Life); // 3 doubled
        Assert.Equal(new[] { 4 }, wallDamage); // doubled too; the wall only prevents combat damage
    }

    [Fact]
    public async Task DoubledTokensAndCounters()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Enchantment("Season") with { Replaces = Replacements.DoubleTokens | Replacements.DoubleCounters });
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Spell("Growth", "{1}", new SpellAbility
        {
            Effects = new Effect[]
            {
                new CreateTokens(Creature("Soldier", 1, 1), 1, Subject.You),
                new AddCounters(1, Subject.Each(new ObjectFilter(CardType.Creature, Name: "Bear"))),
            },
        }));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.Count(e => e is TokenCreated));
        Assert.Equal(2, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ShuffledIntoTheLibraryInsteadOfTheGraveyard()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var colossus = s.Add(P1, Creature("Colossus", 2, 2) with { Replaces = Replacements.ShuffleIntoLibraryInsteadOfGraveyard });
        s.InHand(P0, Spell("Kill", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature) },
            Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) },
        }));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(colossus).Zone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSpellsControllerChoosesBetweenItsOwnShuffleAndAnExileReplacement(bool chooseShuffle)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Ash Warden", 1, 1) with { Replaces = Replacements.ExileInstantsAndSorceries });
        var echo = s.InHand(P0, Spell("Circling Thought", "{1}", new SpellAbility { Effects = new Effect[] { new GainLife(1, Subject.You) } }, CardType.Instant)
            with { Replaces = Replacements.ShuffleIntoLibraryInsteadOfGraveyard });
        List<string>? offered = null;
        s.Attacker.Option = (_, r) =>
        {
            offered = r.Options.ToList();
            return offered.FindIndex(o => o.Contains("library") == chooseShuffle);
        };
        await s.RunUntilTurn();
        Assert.Equal(2, offered!.Count);
        Assert.Equal(chooseShuffle ? Zone.Library : Zone.Exile, s.Card(echo).Zone);
        int moved = s.Game.Log.ToList().FindIndex(e => e is CardMoved { From: Zone.Stack } m && m.Card == echo);
        Assert.Equal(chooseShuffle, s.Game.Log.Skip(moved).Any(e => e is LibraryShuffled { Player.Value: 0 }));
        Assert.DoesNotContain(s.Game.Log, e => e is CardMoved m && m.Card == echo && m.To == Zone.Graveyard);
    }

    [Fact]
    public async Task OnlyOneApplicableReplacementAppliesWithoutAsking()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Ash Warden", 1, 1) with { Replaces = Replacements.ExileInstantsAndSorceries });
        var insight = s.InHand(P0, Spell("Plain Insight", "{1}", new SpellAbility { Effects = new Effect[] { new GainLife(1, Subject.You) } }, CardType.Instant));
        bool asked = false;
        s.Attacker.Option = (_, _) => { asked = true; return 0; };
        await s.RunUntilTurn();
        Assert.False(asked);
        Assert.Equal(Zone.Exile, s.Card(insight).Zone);
    }

    [Fact]
    public async Task OpponentChoosesWhatYouDiscardFromARevealedHand()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Spell("Pick", "{1}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
            Effects = new Effect[] { new DiscardChosenByYou(Subject.TargetAt(0), new ObjectFilter(CardType.Land)) },
        }));
        s.Attacker.Choose = (_, r) => { Assert.All(r.Options, o => Assert.False(o.IsHidden)); return new[] { r.Options[0].Id }; };
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is HandRevealed);
        Assert.Single(s.Game.Log.OfType<CardDiscarded>().Where(d => d.Player == P1));
    }

    [Fact]
    public async Task StunCounterReplacesUntapping()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var card = s.Card(bear);
        card.Tapped = true;
        card.Counters[CounterKind.Stun] = 1;
        await s.RunUntilTurn(2); // player 0's untap step has happened
        Assert.True(card.Tapped);
        Assert.Equal(0, card.CounterCount(CounterKind.Stun));
    }

    /// <summary>A creature that goes onto the battlefield when an opponent makes its owner discard it, and is shuffled into its owner's library instead of a graveyard.</summary>
    private static readonly CardDefinition Stubborn = Creature("Stubborn", 4, 4) with
    {
        ManaCost = ManaCost.Parse("{9}"), OntoBattlefieldIfOpponentMakesYouDiscard = true,
        Replaces = Replacements.ShuffleIntoLibraryInsteadOfGraveyard,
    };

    private static CardDefinition MindTwist(Subject who, TargetSpec[]? targets) => new()
    {
        Name = "Thought Squeeze", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = targets ?? Array.Empty<TargetSpec>(), Effects = new Effect[] { new Discard(1, who) } },
    };

    [Theory]
    [InlineData(true, Zone.Battlefield)]
    [InlineData(false, Zone.Library)]
    public async Task AnOpponentsDiscardLetsTheOwnerChooseBetweenTheBattlefieldAndAnotherReplacement(bool battlefield, Zone expected)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, MindTwist(Subject.TargetAt(0), new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) }));
        var stubborn = s.InHand(P1, Stubborn);
        s.Defender.Discard = (_, _) => new[] { stubborn };
        var asked = new List<IReadOnlyList<string>>();
        s.Defender.Option = (_, r) =>
        {
            asked.Add(r.Options);
            return r.Options.ToList().FindIndex(o => o.Contains(battlefield ? "battlefield" : "Shuffle"));
        };
        await s.RunUntilTurn();
        Assert.Single(asked);
        Assert.Equal(2, asked[0].Count);
        Assert.Equal(expected, s.Card(stubborn).Zone);
        // Either way it was discarded: the replacements change only where it goes.
        Assert.Contains(s.Game.Log, e => e is CardDiscarded d && d.Card == stubborn);
    }

    [Fact]
    public async Task ADiscardByTheOwnersOwnEffectDoesNotPutItOntoTheBattlefield()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, MindTwist(Subject.You, null));
        var stubborn = s.InHand(P0, Stubborn);
        s.Attacker.Discard = (_, _) => new[] { stubborn };
        var asked = 0;
        s.Attacker.Option = (_, _) => { asked++; return 0; };
        await s.RunUntilTurn();
        Assert.Equal(0, asked); // only the card's own shuffle replacement applies: nothing to choose
        Assert.Equal(Zone.Library, s.Card(stubborn).Zone);
        Assert.Contains(s.Game.Log, e => e is CardDiscarded d && d.Card == stubborn);
    }

    [Fact]
    public async Task AnOpponentsDiscardWithNoOtherReplacementPutsItOntoTheBattlefieldWithoutAsking()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, MindTwist(Subject.TargetAt(0), new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) }));
        var stubborn = s.InHand(P1, Stubborn with { Replaces = Replacements.None });
        s.Defender.Discard = (_, _) => new[] { stubborn };
        s.Defender.Option = (_, _) => throw new InvalidOperationException("Nothing to choose.");
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(stubborn).Zone);
        Assert.Equal(P1, s.Card(stubborn).Controller);
        Assert.Contains(s.Game.Log, e => e is CardDiscarded d && d.Card == stubborn);
    }
}
