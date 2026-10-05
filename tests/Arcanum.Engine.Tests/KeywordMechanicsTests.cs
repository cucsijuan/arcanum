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
/// Casting options (multikicker, storm, dash, split cards, suspend), mana with strings attached (damage, snow), replacement
/// and restriction effects, and combat keywords added for the commander set.
/// </summary>
public class KeywordMechanicsTests
{
    /// <summary>Casts spells in the given name order (each once it's castable); passes otherwise; never attacks.</summary>
    private static Scenario Casting(params string[] order)
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) =>
        {
            foreach (var name in order)
                if (legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == name) is { } cast) return cast;
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        return s;
    }

    private static CardDefinition Sorcery(string name, string cost, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    [Fact]
    public async Task APainfulManaAbilityDealsDamageToItsController()
    {
        var s = Casting();
        s.Add(P0, new CardDefinition
        {
            Name = "Painful Ground", Types = CardType.Land, TapForMana = new[] { ManaType.Colorless },
            ExtraManaOptions = new[] { new ManaOption(new[] { ManaType.White }) { DamageToController = 1 } },
        });
        s.InHand(P0, Sorcery("Prayer", "{W}", new GainLife(0, Subject.You)));
        await s.RunUntilTurn();
        Assert.Equal(19, Life(s, P0));
    }

    [Fact]
    public async Task MultikickerCountsEveryPaymentForItsCounters()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var chalice = s.InHand(P0, new CardDefinition
        {
            Name = "Chalice", ManaCost = ManaCost.Parse("{0}"), Types = CardType.Artifact, Multikicker = ManaCost.Parse("{1}"),
            EntersWithCountersFrom = new Quantity(0, QuantityKind.TimesKicked), EntersWithCounterKind = CounterKind.Charge,
        });
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(chalice).CounterCount(CounterKind.Charge));
    }

    [Fact]
    public async Task StormCopiesForEachSpellCastBeforeIt()
    {
        var s = Casting("Opener", "Second", "Storm Bolt");
        s.Lands(P0, 3);
        s.InHand(P0, Sorcery("Opener", "{R}", new GainLife(1, Subject.You)));
        s.InHand(P0, Sorcery("Second", "{R}", new GainLife(1, Subject.You)));
        s.InHand(P0, Sorcery("Storm Bolt", "{R}", new DealDamage(2, Subject.EachOpponent)) with { Storm = true });
        await s.RunUntilTurn();
        Assert.Equal(14, Life(s, P1)); // the spell and two copies
    }

    [Fact]
    public async Task ADashedCreatureHasHasteAndReturnsToItsOwnersHand()
    {
        var s = Casting();
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Lands(P0, 1);
        var raider = s.InHand(P0, Creature("Raider", 3, 3) with { ManaCost = ManaCost.Parse("{4}"), Dash = ManaCost.Parse("{R}") });
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id != raider).Take(count).Select(c => c.Id).ToList(); // cleanup keeps it
        await s.RunUntilTurn();
        Assert.Equal(17, Life(s, P1));
        Assert.Equal(Zone.Hand, s.Card(raider).Zone);
    }

    [Fact]
    public async Task PersistReturnsTheCreatureWithAMinusCounterOnce()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var persist = new TriggeredAbility
        {
            Trigger = TriggerEvent.Dies, Condition = new Not(new SourceHadCounters(CounterKind.MinusOneMinusOne)),
            Effects = new Effect[] { new PutOntoBattlefield(Subject.Self, UnderOwnersControl: true) { Counters = 1, CounterKind = CounterKind.MinusOneMinusOne } },
            Text = "Persist",
        };
        var spirit = s.Add(P0, Creature("Spirit", 3, 3) with { Abilities = new AbilityDefinition[] { persist } });
        var kill = new CardDefinition
        {
            Name = "Kill", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        };
        s.InHand(P0, kill);
        s.InHand(P0, kill);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(spirit).Zone); // the second death had a -1/-1 counter
    }

    [Fact]
    public async Task ARegeneratedCreatureIsTappedInsteadOfDestroyed()
    {
        var s = Casting("Shield", "Kill");
        s.Lands(P0, 2);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, new CardDefinition
        {
            Name = "Shield", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Regenerate(Subject.TargetAt(0)) } },
        });
        s.InHand(P0, new CardDefinition
        {
            Name = "Kill", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        });
        bool tappedWhileAlive = false;
        s.Game.EventRaised += e => { if (e is PermanentTapped t && t.Card == bear) tappedWhileAlive = true; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.True(tappedWhileAlive);
    }

    [Fact]
    public async Task DevourSacrificesAndAddsCountersForEach()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Snack", 1, 1));
        s.Add(P0, Creature("Morsel", 1, 1));
        var beast = s.InHand(P0, Creature("Devourer", 1, 1) with { ManaCost = ManaCost.Parse("{R}"), Devour = 2 });
        s.Attacker.Choose = (_, r) => r.Prompt.StartsWith("Devour") ? r.Options.Select(c => c.Id).ToList() : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(beast).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Single(s.Game.State.PermanentsControlledBy(P0).Where(c => c.IsCreature));
    }

    [Fact]
    public async Task AnAftermathHalfIsCastFromTheGraveyardAndThenExiled()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        var left = Sorcery("Dawnlight", "{R}", new GainLife(2, Subject.You));
        var right = Sorcery("Duskfall", "{R}", new GainLife(5, Subject.You)) with { Aftermath = true };
        var card = s.InHand(P0, new CardDefinition
        {
            Name = "Dawnlight // Duskfall", ManaCost = left.ManaCost.Plus(right.ManaCost), Types = CardType.Sorcery, SplitHalves = new[] { left, right },
        });
        var halvesOffered = new List<int?>();
        s.Attacker.Act = (_, legal) =>
        {
            var cast = legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card);
            if (cast is not null) halvesOffered.Add(cast.Half);
            return (PlayerAction?)cast ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(new int?[] { 0, 1 }, halvesOffered.ToArray());
        Assert.Equal(27, Life(s, P0));
        Assert.Equal(Zone.Exile, s.Card(card).Zone);
    }

    [Fact]
    public async Task TheLastTimeCounterLetsTheSuspendedCardBeCastFree()
    {
        var s = Casting();
        var spell = s.InHand(P0, Sorcery("Delayed Blessing", "{5}", new GainLife(4, Subject.You)));
        bool moved = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!moved)
            {
                // Put it into exile suspended with one time counter.
                moved = true;
                var card = s.Card(spell);
                s.Game.State.GetPlayer(P0).Hand.Remove(spell);
                s.Game.State.GetPlayer(P0).Exile.Add(spell);
                card.Zone = Zone.Exile;
                card.Counters[CounterKind.Time] = 1;
                card.Suspended = true;
            }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        await s.RunUntilTurn(4);
        Assert.Equal(24, Life(s, P0));
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone);
    }

    [Fact]
    public async Task AnOpponentsThiefDrawsInsteadOfExtraDraws()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Thief", 1, 1) with { Replaces = Replacements.StealsOpponentsExtraDraws });
        s.InHand(P0, Sorcery("Study", "{R}", new DrawCards(2, Subject.You)));
        var drawn = new int[2];
        bool cast = false;
        s.Game.EventRaised += e =>
        {
            if (e is SpellCast) cast = true;
            if (cast && e is CardDrawn d && s.Game.State.TurnNumber == 1) drawn[d.Player.Value]++;
        };
        await s.RunUntilTurn();
        Assert.Equal(0, drawn[0]);
        Assert.Equal(2, drawn[1]);
    }

    [Fact]
    public async Task ABridgeKeepsCreaturesWithPowerAboveItsControllersHandSizeHome()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Giant", 9, 9));
        s.Add(P0, Creature("Squire", 2, 2));
        s.Add(P1, new CardDefinition { Name = "Bridge", Types = CardType.Artifact, CantAttackIfPowerAboveHandSize = true });
        IReadOnlyList<CardId>? offered = null;
        s.Attacker.Attack = (_, attackers, _) => { offered = attackers; return Array.Empty<AttackDeclaration>(); };
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(new[] { "Squire" }, offered!.Select(id => s.Card(id).Name).ToArray());
    }

    [Fact]
    public async Task ACostIncreaseAppliesToEveryPlayersSpells()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, new CardDefinition
        {
            Name = "Thorn", Types = CardType.Artifact,
            Abilities = new AbilityDefinition[] { new SpellCostIncrease(new ObjectFilter(ExcludedTypes: CardType.Creature, Controller: ControllerFilter.Any), 1) },
        });
        var spell = s.InHand(P0, Sorcery("Spark", "{R}", new DealDamage(1, Subject.EachOpponent)));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(spell).Zone); // {R} plus {1} with a single land
    }

    [Fact]
    public async Task SwappingGraveyardAndBattlefieldReturnsTheExiledCreatures()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var onField = s.Add(P1, Creature("Living", 2, 2));
        var buried = s.InHand(P1, Creature("Buried", 3, 3));
        s.InHand(P0, Sorcery("Turnabout", "{R}", new SwapGraveyardAndBattlefield(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any))));
        bool moved = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (v, legal) =>
        {
            if (!moved)
            {
                moved = true;
                var card = s.Card(buried);
                s.Game.State.GetPlayer(P1).Hand.Remove(buried);
                s.Game.State.GetPlayer(P1).Graveyard.Add(buried);
                card.Zone = Zone.Graveyard;
            }
            return act(v, legal);
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(buried).Zone);
        Assert.Equal(P1, s.Card(buried).Controller);
        Assert.Equal(Zone.Graveyard, s.Card(onField).Zone);
    }

    [Fact]
    public async Task ALaterControlEffectWinsOverAnEarlierStaticOne()
    {
        var s = Casting();
        s.Lands(P0, 1);
        // P1's creature is controlled by the monarch (P1) through its own static ability; P0 then gains control of it.
        var hound = s.Add(P1, Creature("Hound", 2, 2) with
        {
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Self)) { GivesControlToMonarch = true } },
        });
        s.Game.State.Monarch = P1;
        s.InHand(P0, new CardDefinition
        {
            Name = "Seize", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) }, Effects = new Effect[] { new GainControl(Subject.TargetAt(0)) } },
        });
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Card(hound).Controller);
    }

    [Fact]
    public async Task AnExertedCreatureDoesntUntapDuringItsControllersNextUntapStep()
    {
        var s = new Scenario();
        var runner = s.Add(P0, Creature("Runner", 2, 2) with { Exert = true });
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn(4);
        Assert.True(s.Card(runner).Tapped);
        Assert.Equal(18, Life(s, P1)); // it attacked on the first turn only
    }

    [Fact]
    public async Task ExaltedPumpsACreatureAttackingAlone()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Lone", 2, 2));
        s.Add(P0, new CardDefinition { Name = "Banner", Types = CardType.Enchantment, Keywords = new[] { "Exalted" } });
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(17, Life(s, P1));
    }

    [Fact]
    public async Task SnowManaPaysForSnowSymbols()
    {
        var s = new Scenario();
        var snow = s.Add(P0, new CardDefinition { Name = "Snowy Field", Types = CardType.Land, Supertypes = Supertype.Snow, TapForMana = new[] { ManaType.Colorless } });
        s.Add(P0, new CardDefinition
        {
            Name = "Shrine", Types = CardType.Artifact,
            Abilities = new AbilityDefinition[] { new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero) { SnowMana = 1 }, Effects = new Effect[] { new GainLife(3, Subject.You) }, Text = "{S}: gain 3" } },
        });
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (used || legal.OfType<ActivateAbility>().FirstOrDefault() is not { } activate) return PassPriority.Instance;
            used = true;
            return activate;
        };
        await s.RunUntilTurn();
        Assert.Equal(23, Life(s, P0));
        Assert.True(s.Card(snow).Tapped);
    }

    [Fact]
    public async Task AbilitiesTriggeringInTheCleanupStepResolveBeforeTheTurnEnds()
    {
        var s = Casting();
        // The defender's "whenever an opponent discards a card, you gain 5 life" triggers on the cleanup discard (eight cards
        // in hand) and resolves in that cleanup step, before the next turn begins (rule 514.3a).
        s.Add(P1, Creature("Watcher", 1, 1) with
        {
            Abilities = new AbilityDefinition[] { new TriggeredAbility { Trigger = TriggerEvent.OpponentDiscards, Effects = new Effect[] { new GainLife(5, Subject.You) }, Text = "gain 5" } },
        });
        s.InHand(P0, Creature("Extra", 1, 1) with { ManaCost = ManaCost.Parse("{9}") });
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(25, Life(s, P1));
    }
}
