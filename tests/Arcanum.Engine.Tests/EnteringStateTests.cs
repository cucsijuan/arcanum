// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>A permanent enters already tapped and with its counters (rules 614.1c, 122.6), and tapping goes through one path.</summary>
public class EnteringStateTests
{
    private static CardDefinition Spell(string name, string cost, SpellAbility spell) => new()
    {
        Name = name, ManaCost = Mana.ManaCost.Parse(cost), Types = CardType.Instant, Spell = spell,
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static TriggeredAbility GainFiveIf(Condition condition) => new()
    {
        Trigger = TriggerEvent.EntersBattlefield, Condition = condition,
        Effects = new Effect[] { new GainLife(5, Subject.You) }, Text = "When this enters, if the condition holds, you gain 5 life.",
    };

    [Fact]
    public async Task PutOntoTheBattlefieldTappedIsTappedWhenItsEntersTriggerChecksItsCondition()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var sleeper = s.InHand(P0, Creature("Sleeper", 2, 2) with { Abilities = new AbilityDefinition[] { GainFiveIf(new Not(new SourceUntapped())) } });
        s.InHand(P0, Spell("Wake", "{1}{R}", new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.GraveyardCard, ControllerFilter.You, new ObjectFilter(CardType.Creature)) },
            Effects = new Effect[] { new PutOntoBattlefield(Subject.TargetAt(0), Tapped: true) },
        }));
        var p0 = s.Game.State.GetPlayer(P0);
        p0.Hand.Remove(sleeper);
        p0.Graveyard.Add(sleeper);
        s.Card(sleeper).Zone = Zone.Graveyard;
        bool tappedWhenMoved = false;
        s.Game.EventRaised += e => { if (e is CardMoved m && m.Card == sleeper && m.To == Zone.Battlefield) tappedWhenMoved = s.Card(sleeper).Tapped; };
        await s.RunUntilTurn();
        Assert.True(tappedWhenMoved); // already tapped when the move was announced (and recomputed), not tapped afterwards
        Assert.True(s.Card(sleeper).Tapped);
        Assert.Equal(25, p0.Life); // "if it's tapped" held as it entered
    }

    [Fact]
    public async Task CountersTheCreatureEntersWithAreThereForItsEntersTrigger()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var bear = s.InHand(P0, Creature("Grower", 1, 1) with
        {
            ManaCost = Mana.ManaCost.Parse("{1}{R}"), EntersWithCounters = 2,
            Abilities = new AbilityDefinition[] { GainFiveIf(new SourceHasCounters(2)) },
        });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.Equal(2, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task CountersFromAnotherPermanentsReplacementAreThereForItsEntersTrigger()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Patron", Types = CardType.Enchantment, OthersEnterWithCounters = new Quantity(1) });
        var bear = s.InHand(P0, Creature("Pup", 1, 1) with
        {
            ManaCost = Mana.ManaCost.Parse("{R}"),
            Abilities = new AbilityDefinition[] { GainFiveIf(new SourceHasCounters(1)) },
        });
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task EnteringCountersStillAnnounceThemselves()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var bear = s.InHand(P0, Creature("Grower", 1, 1) with { ManaCost = Mana.ManaCost.Parse("{1}{R}"), EntersWithCounters = 2 });
        var seen = new List<string>();
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved m && m.Card == bear && m.To == Zone.Battlefield) seen.Add("moved");
            if (e is CountersPlaced c && c.Card == bear) seen.Add($"counters{c.Count}");
        };
        await s.RunUntilTurn();
        Assert.Equal(new[] { "moved", "counters2" }, seen);
    }

    /// <summary>An Orc that enters with one counter, with "that many plus one" and "twice that many" effects out: the order is the controller's (rule 616.1).</summary>
    private static (Scenario Scenario, CardId Orc, List<string> Seen, List<string> Asked) EnteringWithBothReplacements(int pick)
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, new CardDefinition { Name = "Captain", Types = CardType.Enchantment, Replaces = Replacements.ExtraCounterOnArmiesGoblinsOrcs });
        s.Add(P0, new CardDefinition { Name = "Season", Types = CardType.Enchantment, Replaces = Replacements.DoubleCounters });
        var orc = s.InHand(P0, Creature("Orc", 1, 1) with
        {
            ManaCost = Mana.ManaCost.Parse("{1}{R}"), Subtypes = new[] { "Orc" }, EntersWithCounters = 1,
            Abilities = new AbilityDefinition[] { GainFiveIf(new SourceHasCounters(4)) },
        });
        var seen = new List<string>();
        var asked = new List<string>();
        s.Attacker.Option = (_, r) =>
        {
            if (!r.Options.Contains("Twice that many")) return 0;
            asked.Add(r.Prompt);
            seen.Add("asked");
            return pick;
        };
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved m && m.Card == orc && m.To == Zone.Battlefield) seen.Add($"moved:{s.Card(orc).CounterCount(CounterKind.PlusOnePlusOne)}");
            if (e is CountersPlaced c && c.Card == orc) seen.Add($"counters{c.Count}");
        };
        return (s, orc, seen, asked);
    }

    [Theory]
    [InlineData(0, 4)] // that many plus one, then twice that many: (1 + 1) x 2
    [InlineData(1, 3)] // twice that many, then plus one: 1 x 2 + 1
    public async Task TheControllerOrdersCounterReplacementsAsACreatureEntersBeforeItIsAnnounced(int pick, int expected)
    {
        var (s, orc, seen, asked) = EnteringWithBothReplacements(pick);
        await s.RunUntilTurn();
        Assert.Single(asked);
        Assert.Equal(expected, s.Card(orc).CounterCount(CounterKind.PlusOnePlusOne));
        // Asked as it moves, with the counters already on it when it is announced, and announced once.
        Assert.Equal(new[] { "asked", $"moved:{expected}", $"counters{expected}" }, seen);
        // Its intervening-if enters trigger saw the counters it entered with.
        Assert.Equal(pick == 0 ? 25 : 20, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    public async Task TheControllerOrdersCounterReplacementsForATokenThatEntersWithCounters(int pick, int expected)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition { Name = "Patron", Types = CardType.Enchantment, OthersEnterWithCounters = new Quantity(1) });
        s.Add(P0, new CardDefinition { Name = "Captain", Types = CardType.Enchantment, Replaces = Replacements.ExtraCounterOnArmiesGoblinsOrcs });
        s.Add(P0, new CardDefinition { Name = "Season", Types = CardType.Enchantment, Replaces = Replacements.DoubleCounters });
        s.InHand(P0, Spell("Muster", "{R}", new SpellAbility
        {
            Effects = new Effect[] { new CreateTokens(Creature("Orc", 1, 1) with { Subtypes = new[] { "Orc" } }, 1, Subject.You) },
        }));
        s.Attacker.Option = (_, r) => r.Options.Contains("Twice that many") ? pick : 0;
        await s.RunUntilTurn();
        var token = s.Game.State.PermanentsControlledBy(P0).Single(c => c.Name == "Orc");
        Assert.Equal(expected, token.CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task TappingByAnEffectEmitsOnceAndTappingATappedPermanentEmitsNothing()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var foe = s.Add(P1, Creature("Foe", 2, 2));
        var tap = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new TapIt(Subject.TargetAt(0)) } };
        s.InHand(P0, Spell("Chill", "{R}", tap));
        s.InHand(P0, Spell("Chill Again", "{R}", tap));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(foe) };
        int tapped = 0;
        bool tappedAtEvent = false;
        s.Game.EventRaised += e => { if (e is PermanentTapped t && t.Card == foe) { tapped++; tappedAtEvent = s.Card(foe).Tapped; } };
        await s.RunUntilTurn();
        Assert.True(tappedAtEvent);
        Assert.Equal(1, tapped);
    }
}
