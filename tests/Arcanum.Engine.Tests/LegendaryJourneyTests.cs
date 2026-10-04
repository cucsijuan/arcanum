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
/// Ring-bearers chosen each time, keyword counters, lasting control, revealing the top card, excess damage, goad,
/// replacement effects on tokens, life and counters, delayed abilities and other rules of the third full set.
/// </summary>
public class LegendaryJourneyTests
{
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardDefinition Sorcery(string name, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    private static CardDefinition TargetedSorcery(string name, TargetSpec target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Targets = new[] { target }, Effects = effects },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RingTriggersKnowTheCreatureChosenAsRingBearerThisTime(bool chooseAnother)
    {
        var s = Casting();
        s.Lands(P0, 1);
        var watcher = s.Add(P0, Creature("Watcher", 2, 2) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.RingTemptsYou, Condition = new TriggeredMatches(new ObjectFilter(Other: true)),
                    Effects = new Effect[] { new GainLife(3, Subject.You) }, Text = "if you chose a creature other than this, gain 3",
                },
            },
        });
        var other = s.Add(P0, Creature("Companion", 1, 1));
        s.InHand(P0, Sorcery("Tempting", new RingTemptsYou()));
        s.Attacker.Choose = (_, r) => r.Prompt.Contains("Ring-bearer") ? new[] { chooseAnother ? other : watcher } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(chooseAnother ? life + 3 : life, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AChosenKeywordCounterGivesItsKeyword()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, TargetedSorcery("Blessing", new TargetSpec(TargetKind.Creature, ControllerFilter.You),
            new AddChosenCounter(new[] { CounterKind.FirstStrike, CounterKind.Vigilance }, Subject.TargetAt(0))));
        s.Attacker.Option = (_, r) => r.Options.ToList().IndexOf("vigilance");
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(bear).CounterCount(CounterKind.Vigilance));
        Assert.True(s.Card(bear).Has(Keyword.Vigilance));
    }

    [Fact]
    public async Task ControlForAsLongAsYouControlTheSourceEndsWhenItLeaves()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var keeper = s.Add(P0, new CardDefinition
        {
            Name = "Keeper", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Zero), Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
                    Effects = new Effect[] { new GainControl(Subject.TargetAt(0)) { WhileYouControlSource = true } }, Text = "steal",
                },
                new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero, SacrificeSelf: true), Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "let go" },
            },
        });
        PlayerId? whileHeld = null;
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            var abilities = legal.OfType<ActivateAbility>().Where(a => a.Source == keeper).ToList();
            if (step == 0 && abilities.FirstOrDefault(a => a.Index == 0) is { } steal) { step = 1; return steal; }
            if (step == 1 && s.Game.State.Stack.Count == 0 && abilities.FirstOrDefault(a => a.Index == 1) is { } letGo)
            {
                whileHeld = s.Card(bear).Controller;
                step = 2;
                return letGo;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(P0, whileHeld);
        Assert.Equal(P1, s.Card(bear).Controller);
    }

    [Fact]
    public async Task RevealingTheTopCardPutsAMatchingLandOntoTheBattlefield()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var forest = s.Game.SetupInHand(P0, GenericCards.Forest);
        s.InHand(P0, Sorcery("Seek the Land",
            new RevealTop(new ObjectFilter(CardType.Land), false, new Effect[] { new PutOntoBattlefield(new Subject(SubjectKind.Found), Tapped: true) })));
        bool stacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, forest); }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(forest).Zone);
        Assert.True(s.Card(forest).Tapped);
    }

    [Fact]
    public async Task ExcessDamageCanBeDealtToTheCreaturesController()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, TargetedSorcery("Overkill", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent),
            new DealDamage(5, Subject.TargetAt(0)) { ExcessToController = true }));
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(life - 3, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public void AnAttackerThatCantBeBlockedByMoreThanOneCreatureRejectsDoubleBlocks()
    {
        var attacker = new CardId(1);
        var first = new CardId(2);
        var second = new CardId(3);
        var request = new BlockRequest(new[] { attacker }, new[] { first, second },
            new Dictionary<CardId, IReadOnlyList<CardId>> { [first] = new[] { attacker }, [second] = new[] { attacker } }, new Dictionary<CardId, int>())
        {
            MaximumBlockers = new Dictionary<CardId, int> { [attacker] = 1 },
        };
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(first, attacker) }, out _));
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(first, attacker), new BlockDeclaration(second, attacker) }, out _));
    }

    [Fact]
    public async Task TokensComeWithAnAdditionalFood()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Host of the Feast", Types = CardType.Enchantment, Replaces = Replacements.ExtraFoodWithTokens });
        s.InHand(P0, Sorcery("Muster", new CreateTokens(Creature("Soldier", 1, 1), 1, Subject.You)));
        await s.RunUntilTurn();
        var tokens = s.Game.State.PermanentsControlledBy(P0).Where(c => c.Definition.IsToken).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Food", "Soldier" }, tokens);
    }

    [Fact]
    public async Task LifeGainIsDoubledAtFiveOrLessLife()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Phial", Types = CardType.Artifact, Replaces = Replacements.DoubleLifeGainAtFiveOrLess });
        s.InHand(P0, Sorcery("Mend", new GainLife(3, Subject.You)));
        s.Game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 1 }) s.Game.State.GetPlayer(P0).Life = 5; };
        await s.RunUntilTurn();
        Assert.Equal(11, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALegendarySorceryNeedsALegendaryCreatureOrPlaneswalker(bool withLegend)
    {
        var s = Casting();
        s.Lands(P0, 1);
        if (withLegend) s.Add(P0, Creature("Hero", 2, 2) with { Supertypes = Supertype.Legendary });
        s.InHand(P0, Sorcery("Saga of Old", new GainLife(5, Subject.You)) with { Supertypes = Supertype.Legendary });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(withLegend ? life + 5 : life, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ADelayedAbilityWaitsForTheChosenOpponentsNextEndStep()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var watch = new TriggeredAbility
        {
            Trigger = TriggerEvent.NextUpkeep, Text = "draw if they didn't attack you",
            Effects = new Effect[] { new IfThen(new TriggeredPlayerAttackedYou(), new Effect[] { new GainLife(5, Subject.You) }, new Effect[] { new GainLife(1, Subject.You) }) },
        };
        s.Add(P0, new CardDefinition
        {
            Name = "Watchful Prince", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.YourEndStep, Effects = new Effect[] { new AtPlayersNextEndStep(Subject.EachOpponent, watch) }, Text = "watch an opponent" },
            },
        });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn(3);
        // Chosen at player 0's end step; it waited for player 1's end step, who didn't attack.
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life);
        Assert.Empty(s.Game.State.AtPlayersNextEndStep.Where(w => w.MadeOnTurn == 1));
    }

    [Fact]
    public async Task UpToXTargetsUseTheAmountTheTriggerWasAbout()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bears = new[] { s.Add(P0, Creature("Bear A", 2, 2)), s.Add(P0, Creature("Bear B", 2, 2)), s.Add(P0, Creature("Bear C", 2, 2)) };
        s.Add(P0, new CardDefinition
        {
            Name = "Healer", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.YouScry, Text = "counters on up to X",
                    Targets = new[] { new TargetSpec(TargetKind.Creature, Optional: true) { RepeatFrom = new Quantity(0, QuantityKind.TriggerAmount) } },
                    Effects = new Effect[] { new AddCounters(1, new Subject(SubjectKind.EachTarget), CounterKind.PlusOnePlusOne) },
                },
            },
        });
        s.InHand(P0, Sorcery("Glimpse", new Scry(2)));
        s.Attacker.Targets = (_, r) => Enumerable.Range(0, r.Specs.Count).Select(i => Target.Of(bears[i])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(new[] { 1, 1, 0 }, bears.Select(b => s.Card(b).CounterCount(CounterKind.PlusOnePlusOne)).ToArray());
    }

    [Fact]
    public async Task AbilitiesOfFoodsCanCostLess()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Cook", Types = CardType.Enchantment, Abilities = new AbilityDefinition[] { new AbilityCostReduction(new ObjectFilter(Subtype: "Food"), 1, false) } });
        var food = s.Add(P0, PredefinedTokens.Food);
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == food) is { } eat ? eat : PassPriority.Instance;
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 3, s.Game.State.GetPlayer(P0).Life); // {2} paid with a single land
    }

    [Fact]
    public async Task ALegendaryCreatureEnteringMakesWatchingAbilitiesTriggerAgain()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "White Wizard", Types = CardType.Enchantment, Replaces = Replacements.ExtraTriggersFromLegendariesAndArtifactsMoving });
        s.Add(P0, new CardDefinition
        {
            Name = "Herald", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[] { new TriggeredAbility { Trigger = TriggerEvent.CreatureEnters, Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "gain 1" } },
        });
        s.InHand(P0, Creature("Hero", 2, 2) with { Supertypes = Supertype.Legendary, ManaCost = ManaCost.Parse("{R}") });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AGoadedCreatureAttacksOnItsControllersTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, TargetedSorcery("Taunt", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), new Goad(Subject.TargetAt(0))));
        s.Defender.Attack = (_, _, _) => Array.Empty<AttackDeclaration>(); // it attacks anyway
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn(3);
        Assert.Equal(life - 2, s.Game.State.GetPlayer(P0).Life);
    }
}
