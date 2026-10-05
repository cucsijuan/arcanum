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

/// <summary>Keyword actions, conditions and triggers on other objects' events.</summary>
public class MechanicsTests
{
    private static CardDefinition Sorcery(string name, string cost, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    private static CardDefinition WithAbilities(CardDefinition card, params AbilityDefinition[] abilities) => card with { Abilities = abilities };

    private static readonly CardDefinition Ping = new()
    {
        Name = "Ping", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant,
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Player) }, Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) } },
    };

    /// <summary>Player 0 casts whatever it can, targets the opponent when possible and never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, r) => r.Legal.Select(choices => choices.FirstOrDefault(t => t.Player == P1) is { Player: not null } p ? p : choices[0]).ToList();
        return s;
    }

    [Fact]
    public async Task ScryPutsChosenCardsOnTheBottom()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Glimpse", "{R}", new Scry(2)));
        var library = s.Game.State.GetPlayer(P0).Library;
        CardId? sent = null, kept = null;
        s.Attacker.Choose = (_, request) =>
        {
            Assert.Equal(CardChoicePurpose.ScryToBottom, request.Purpose);
            Assert.Equal(2, request.Options.Count);
            Assert.All(request.Options, o => Assert.False(o.IsHidden)); // the chooser sees the cards
            sent = request.Options[0].Id;
            kept = request.Options[1].Id;
            return new[] { sent.Value };
        };
        await s.RunUntilTurn();
        Assert.Equal(sent, library[^1]);
        Assert.Equal(kept, library[0]);
    }

    [Fact]
    public async Task SurveilPutsChosenCardsIntoTheGraveyard()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Peek", "{R}", new Surveil(1)));
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Single(s.Game.State.GetPlayer(P0).Graveyard.Where(id => s.Card(id).Name == "Forest"));
        Assert.Contains(s.Game.Log, e => e is LookedAtTop { Scry: false, Moved: 1 });
    }

    [Fact]
    public async Task FightingCreaturesDamageEachOther()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var mine = s.Add(P0, Creature("Brawler", 3, 3));
        var theirs = s.Add(P1, Creature("Victim", 2, 2));
        s.InHand(P0, new CardDefinition
        {
            Name = "Scuffle", ManaCost = ManaCost.Parse("{G}"), Types = CardType.Sorcery,
            Spell = new SpellAbility
            {
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You), new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
                Effects = new Effect[] { new Fight(Subject.TargetAt(0), Subject.TargetAt(1)) },
            },
        });
        int damageOnMine = -1;
        s.Game.EventRaised += e => { if (e is SpellResolved) damageOnMine = s.Card(mine).Damage; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(theirs).Zone);
        Assert.Equal(2, damageOnMine);
    }

    [Fact]
    public void FlashAllowsCastingAtInstantSpeed()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var flash = s.InHand(P0, Creature("Ambusher", 2, 2, Keyword.Flash));
        var plain = s.InHand(P0, Creature("Walker", 2, 2));
        var legal = s.Game.GetLegalActions(P0); // before the game starts: not a main phase
        Assert.Contains(new CastSpell(flash), legal);
        Assert.DoesNotContain(new CastSpell(plain), legal);
    }

    [Fact]
    public async Task ProwessPumpsOnNoncreatureSpells()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var monk = s.Add(P0, Creature("Monk", 1, 1, Keyword.Prowess));
        s.InHand(P0, Ping);
        int power = 0;
        s.Game.EventRaised += e => { if (e is AbilityResolved { Text: "Prowess" }) power = s.Card(monk).Power; };
        await s.RunUntilTurn();
        Assert.Equal(2, power);
        Assert.Equal(1, s.Card(monk).Power); // until end of turn only
    }

    [Fact]
    public async Task TreasureIsSacrificedForAnyColor()
    {
        var s = Casting();
        var treasure = s.Add(P0, PredefinedTokens.Treasure);
        s.InHand(P0, Ping);
        await s.RunUntilTurn();
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
        Assert.DoesNotContain(treasure, s.Game.State.Battlefield);
    }

    [Fact]
    public async Task AnotherCreatureEnteringUnderYourControlTriggers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, WithAbilities(Creature("Greeter", 1, 1), new TriggeredAbility
        {
            Trigger = TriggerEvent.CreatureEnters,
            Filter = ObjectFilter.YourCreatures with { Other = true },
            Effects = new Effect[] { new GainLife(1, Subject.You) },
        }));
        s.InHand(P0, Creature("Newcomer", 1, 1));
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // the Greeter itself was already there: one trigger
    }

    [Fact]
    public async Task OpponentsCreaturesDontTriggerYourEntersAbilities()
    {
        var s = Casting();
        s.Add(P1, WithAbilities(Creature("Greeter", 1, 1), new TriggeredAbility
        {
            Trigger = TriggerEvent.CreatureEnters, Filter = ObjectFilter.YourCreatures, Effects = new Effect[] { new GainLife(1, Subject.You) },
        }));
        s.Lands(P0, 1);
        s.InHand(P0, Creature("Newcomer", 1, 1));
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    private static TriggeredAbility RaidReward => new()
    {
        Trigger = TriggerEvent.YourEndStep, Condition = new AttackedThisTurn(), Effects = new Effect[] { new GainLife(3, Subject.You) },
    };

    [Fact]
    public async Task RaidTriggersAfterAttacking()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Raider", 1, 1));
        s.Add(P0, WithAbilities(new CardDefinition { Name = "Banner", Types = CardType.Enchantment }, RaidReward));
        await s.RunUntilTurn(3);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task RaidDoesNothingWithoutAnAttack()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Add(P0, Creature("Raider", 1, 1));
        s.Add(P0, WithAbilities(new CardDefinition { Name = "Banner", Types = CardType.Enchantment }, RaidReward));
        await s.RunUntilTurn(3);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task GainingLifeTriggersAndConditionalEffectsCheckTheTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var cleric = s.Add(P0, WithAbilities(Creature("Cleric", 1, 1), new TriggeredAbility
        {
            Trigger = TriggerEvent.YouGainLife, Effects = new Effect[] { new AddCounters(1, Subject.Self) },
        }));
        s.InHand(P0, Sorcery("Mend", "{R}", new GainLife(2, Subject.You),
            new IfThen(new GainedLifeThisTurn(2), new Effect[] { new DrawCards(1, Subject.You) })));
        int handBefore = 0;
        s.Game.EventRaised += e => { if (e is SpellCast) handBefore = s.Game.State.GetPlayer(P0).Hand.Count; };
        int handAfter = 0;
        s.Game.EventRaised += e => { if (e is SpellResolved) handAfter = s.Game.State.GetPlayer(P0).Hand.Count; };
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(cleric).Power);
        Assert.Equal(handBefore + 1, handAfter);
    }

    [Fact]
    public async Task OptionalEffectsAskTheController()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Maybe", "{R}", new MayDo("Lose 5 life?", new Effect[] { new LoseLife(5, Subject.You) })));
        s.Attacker.YesNo = (_, request) => { Assert.Equal("Lose 5 life?", request.Prompt); return false; };
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task TargetPlayerDiscards()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, new CardDefinition
        {
            Name = "Thought Squeeze", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) }, Effects = new Effect[] { new Discard(2, Subject.TargetAt(0)) } },
        });
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.Count(e => e is CardDiscarded { Player.Value: 1 }));
        Assert.Equal(5, s.Game.State.GetPlayer(P1).Hand.Count);
    }

    [Fact]
    public async Task LandfallAndSpellCastTriggers()
    {
        var s = Casting();
        s.Add(P0, WithAbilities(new CardDefinition { Name = "Shrine", Types = CardType.Enchantment },
            new TriggeredAbility { Trigger = TriggerEvent.LandEnters, Filter = new ObjectFilter(CardType.Land), Effects = new Effect[] { new GainLife(1, Subject.You) } },
            new TriggeredAbility
            {
                Trigger = TriggerEvent.YouCastSpell, Filter = new ObjectFilter(CardType.Instant | CardType.Sorcery),
                Effects = new Effect[] { new GainLife(10, Subject.You) },
            }));
        s.Lands(P0, 1);
        s.InHand(P0, Ping);
        await s.RunUntilTurn();
        Assert.Equal(31, s.Game.State.GetPlayer(P0).Life); // one land played, one instant cast
    }
}
