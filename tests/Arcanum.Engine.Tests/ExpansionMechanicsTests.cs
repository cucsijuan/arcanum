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

/// <summary>Adventures, Sagas, amass, enduring stories and other card-frame mechanics.</summary>
public class ExpansionMechanicsTests
{
    private static readonly CardDefinition Errand = new()
    {
        Name = "Quick Errand", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Subtypes = new[] { "Adventure" },
        Spell = new SpellAbility { Effects = new Effect[] { new DrawCards(1, Subject.You) } },
    };

    private static readonly CardDefinition Knight = Creature("Wandering Knight", 2, 2) with { ManaCost = ManaCost.Parse("{1}{R}"), Adventure = Errand };

    /// <summary>Player 0 casts the Adventure first when it can, otherwise anything castable; never attacks.</summary>
    private static Scenario Adventuring()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Adventure)
                                       ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task AdventureResolvesIntoExileAndTheCardIsCastFromThere()
    {
        var s = Adventuring();
        s.Lands(P0, 3);
        var knight = s.InHand(P0, Knight);
        await s.RunUntilTurn();
        var card = s.Card(knight);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal("Wandering Knight", card.Name);
        Assert.False(card.AsAdventure);
        int cast = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == knight);
        Assert.Contains(s.Game.Log.Skip(cast), e => e is CardDrawn { Player.Value: 0 }); // the Errand drew a card
        Assert.Contains(s.Game.Log, e => e is CardMoved { To: Zone.Exile } m && m.Card == knight);
    }

    [Fact]
    public async Task AdventureCardOnTheStackHasTheAdventuresCharacteristics()
    {
        var s = Adventuring();
        s.Lands(P0, 1);
        var knight = s.InHand(P0, Knight);
        string? seenOnStack = null;
        s.Game.EventRaised += e =>
        {
            if (e is SpellCast c && c.Card == knight) seenOnStack = s.Card(knight).Name + "/" + s.Card(knight).Types;
        };
        await s.RunUntilTurn();
        Assert.Equal("Quick Errand/Sorcery", seenOnStack);
        // Only one land: the Knight itself stays on its adventure.
        Assert.Equal(Zone.Exile, s.Card(knight).Zone);
        Assert.True(s.Card(knight).OnAdventure);
    }

    [Fact]
    public async Task CounteredAdventureGoesToTheGraveyard()
    {
        var s = Adventuring();
        s.Lands(P0, 1);
        s.Lands(P1, 2);
        var knight = s.InHand(P0, Knight);
        s.InHand(P1, new CardDefinition
        {
            Name = "Refusal", ManaCost = ManaCost.Parse("{1}{R}"), Types = CardType.Instant,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Spell) }, Effects = new Effect[] { new CounterSpell(Subject.TargetAt(0)) } },
        });
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(knight).Zone);
        Assert.False(s.Card(knight).OnAdventure);
    }

    [Fact]
    public void AdventureIsOfferedOnlyFromTheHandNotAfterTheAdventure()
    {
        var s = Adventuring();
        s.Lands(P0, 3);
        var knight = s.InHand(P0, Knight);
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        var legal = s.Game.GetLegalActions(P0);
        Assert.Contains(new CastSpell(knight), legal);
        Assert.Contains(new CastSpell(knight, Adventure: true), legal);
    }
}

public class SagaTests
{
    private static TriggeredAbility Chapter(params int[] chapters) => new()
    {
        Trigger = TriggerEvent.Chapter, Chapters = chapters, Effects = new Effect[] { new GainLife(chapters[0], Subject.You) }, Text = "chapter",
    };

    private static readonly CardDefinition Tale = new()
    {
        Name = "Long Tale", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment, Subtypes = new[] { "Saga" },
        Abilities = new AbilityDefinition[] { Chapter(1), Chapter(2, 3) },
    };

    [Fact]
    public void FinalChapterIsTheHighestChapterNumber() => Assert.Equal(3, Tale.FinalChapter);

    [Fact]
    public async Task SagaGainsLoreEachTurnTriggersChaptersAndIsSacrificedAfterTheLast()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var tale = s.Add(P0, Tale);
        int life = s.Game.State.GetPlayer(P0).Life;
        var lifeAtTurn = new Dictionary<int, int>();
        var loreAtTurn = new Dictionary<int, int>();
        s.Game.EventRaised += e =>
        {
            if (e is not TurnBegan t) return;
            lifeAtTurn[t.TurnNumber] = s.Game.State.GetPlayer(P0).Life;
            loreAtTurn[t.TurnNumber] = s.Card(tale).CounterCount(CounterKind.Lore);
        };
        // Set up before the game (no counter for entering): it gets one at each of its controller's precombat mains.
        await s.RunUntilTurn(6);
        Assert.Equal(1, loreAtTurn[2]);
        Assert.Equal(life + 1, lifeAtTurn[2]); // chapter I
        Assert.Equal(life + 3, lifeAtTurn[4]); // chapter II
        Assert.Equal(life + 5, s.Game.State.GetPlayer(P0).Life); // chapter III, then sacrificed
        Assert.Equal(Zone.Graveyard, s.Card(tale).Zone);
        Assert.Contains(s.Game.Log, e => e is PermanentSacrificed p && p.Card == tale);
    }

    [Fact]
    public async Task SagaCastThisTurnStartsAtChapterOne()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var tale = s.InHand(P0, Tale);
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Battlefield, s.Card(tale).Zone);
        Assert.Equal(1, s.Card(tale).CounterCount(CounterKind.Lore));
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life);
    }
}

public class AmassTests
{
    private static readonly CardDefinition GoblinArmy = new()
    {
        Name = "Goblin Army", Types = CardType.Creature, Subtypes = new[] { "Goblin", "Army" }, Power = 0, Toughness = 0, Colors = new[] { "B" }, IsToken = true,
    };

    private static CardDefinition Muster(int n) => new()
    {
        Name = $"Muster {n}", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
        Spell = new SpellAbility { Effects = new Effect[] { new Amass(n, "Goblin", GoblinArmy, Subject.You) } },
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task AmassCreatesAnArmyThenGrowsIt()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.InHand(P0, Muster(2));
        s.InHand(P0, Muster(1));
        await s.RunUntilTurn();
        var armies = s.Game.State.PermanentsControlledBy(P0).Where(c => c.HasSubtype("Army")).ToList();
        var army = Assert.Single(armies);
        Assert.Equal(3, army.Power);
        Assert.True(army.HasSubtype("Goblin"));
    }

    [Fact]
    public async Task AmassMakesAnExistingArmyOfTheType()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var orcs = s.Add(P0, GoblinArmy with { Name = "Orc Army", Subtypes = new[] { "Orc", "Army" }, Power = 1, Toughness = 1, IsToken = false });
        s.InHand(P0, Muster(2));
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(orcs).Power);
        Assert.True(s.Card(orcs).HasSubtype("Goblin"));
        Assert.True(s.Card(orcs).HasSubtype("Orc"));
    }
}

public class EnduringStoryTests
{
    private static readonly CardDefinition Bard = Creature("Story Keeper", 2, 2, Keyword.Storied) with
    {
        Abilities = new AbilityDefinition[]
        {
            new StaticAbility(new AffectedFilter(AffectedScope.Self), Power: 1) { While = new HasEnduringStory() },
        },
    };

    private static readonly CardDefinition Trinket = new() { Name = "Trinket", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Artifact };

    [Fact]
    public async Task ThreeArtifactsLegendariesOrSagasGiveAnEnduringStoryForTheRestOfTheGame()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var bard = s.Add(P0, Bard);
        s.Add(P0, Trinket);
        s.Add(P0, Trinket);
        s.InHand(P0, Trinket with { Name = "Old Crown", Types = CardType.Enchantment, Supertypes = Supertype.Legendary });
        int? powerBefore = null;
        bool? storyBefore = null;
        s.Game.EventRaised += e =>
        {
            if (e is not SpellCast) return;
            powerBefore = s.Card(bard).Power;
            storyBefore = s.Game.State.GetPlayer(P0).HasEnduringStory;
        };
        await s.RunUntilTurn();
        Assert.Equal(2, powerBefore);
        Assert.False(storyBefore);
        Assert.True(s.Game.State.GetPlayer(P0).HasEnduringStory);
        Assert.Equal(3, s.Card(bard).Power);
        Assert.Contains(s.Game.Log, e => e is EnduringStoryGained { Player.Value: 0 });
        Assert.False(s.Game.State.GetPlayer(P1).HasEnduringStory);
    }
}

/// <summary>Keyword actions and effects new with the second set: recruit, gift, behold, cycling, attack taxes and more.</summary>
public class KeywordActionTests
{
    private static readonly CardDefinition Soldier = new()
    {
        Name = "Human Soldier", Types = CardType.Creature, Subtypes = new[] { "Human", "Soldier" }, Power = 1, Toughness = 1, Colors = new[] { "W" }, IsToken = true,
    };

    private static CardDefinition Sorcery(string name, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static int Soldiers(Scenario s) => s.Game.State.PermanentsControlledBy(P0).Count(c => c.Name == "Human Soldier");

    [Fact]
    public async Task RecruitMakesASoldierWhenANonlandCardIsDiscarded()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Muster Call", new Recruit(Soldier, Subject.You)));
        var bear = s.InHand(P0, Creature("Spare Bear", 2, 2) with { ManaCost = ManaCost.Parse("{5}") });
        s.Attacker.Discard = (view, count) => view.Self.Hand.Any(c => c.Id == bear) ? new[] { bear } : view.Self.Hand.TakeLast(count).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(1, Soldiers(s));
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
    }

    [Fact]
    public async Task RecruitMakesNothingWhenALandIsDiscarded()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Muster Call", new Recruit(Soldier, Subject.You)));
        await s.RunUntilTurn(); // draws a Forest and discards the last card in hand: a Forest
        Assert.Equal(0, Soldiers(s));
    }

    [Fact]
    public async Task PromisedGiftIsGivenBeforeTheSpellsEffects()
    {
        var s = Casting();
        s.Lands(P0, 1);
        bool? sawPromise = null;
        s.InHand(P0, Sorcery("Generous Act", new IfThen(new GiftPromised(), new Effect[] { new DrawCards(2, Subject.You) })) with { Gift = PredefinedTokens.Treasure });
        s.Attacker.YesNo = (_, r) => { if (r.Prompt.Contains("gift")) sawPromise = true; return true; };
        await s.RunUntilTurn();
        Assert.True(sawPromise);
        Assert.Single(s.Game.State.PermanentsControlledBy(P1), c => c.Name == "Treasure");
        int cast = s.Game.Log.ToList().FindIndex(e => e is SpellCast);
        Assert.Equal(2, s.Game.Log.Skip(cast).Count(e => e is CardDrawn { Player.Value: 0 }));
    }

    [Fact]
    public async Task TypecyclingFindsACardOfThatTypeFromTheHand()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        var hole = s.InHand(P0, new CardDefinition
        {
            Name = "Burrow", Types = CardType.Land,
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Parse("{2}")) { FromHand = true },
                    Effects = new Effect[] { new SearchLibrary(new ObjectFilter(Subtype: "Halfling", Controller: ControllerFilter.Any), 1, Zone.Hand) { Reveal = true } },
                    Text = "Halflingcycling {2}",
                },
            },
        });
        var halfling = s.Game.SetupInLibrary(P0, Creature("Small Friend", 1, 1) with { Subtypes = new[] { "Halfling" } });
        bool stacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, halfling); }
            return legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == hole) ?? (PlayerAction)PassPriority.Instance;
        };
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == halfling).Select(o => o.Id).ToList();
        s.Attacker.Discard = (view, count) => view.Self.Hand.Take(count).Select(c => c.Id).ToList(); // keep the card just found
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(hole).Zone);
        Assert.Equal(Zone.Hand, s.Card(halfling).Zone);
    }

    [Fact]
    public async Task AttackTaxIsPaidOrTheCreaturesDontAttack()
    {
        foreach (bool canPay in new[] { true, false })
        {
            var s = new Scenario();
            s.Attacker.Act = (_, _) => PassPriority.Instance; // no land drop: only the lands given here can pay
            var warrior = s.Add(P0, Creature("Raider", 3, 3));
            if (canPay) s.Lands(P0, 1);
            s.Add(P1, new CardDefinition { Name = "Gatehouse", Types = CardType.Enchantment, AttackTax = ManaCost.Parse("{1}") });
            int life = s.Game.State.GetPlayer(P1).Life;
            await s.RunUntilTurn();
            Assert.Equal(canPay ? life - 3 : life, s.Game.State.GetPlayer(P1).Life);
            Assert.Equal(canPay, s.Game.Log.Any(e => e is AttackerDeclared a && a.Attacker == warrior));
        }
    }

    [Fact]
    public async Task HoneCountersGiveTheEquippedCreaturePower()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var bearer = s.Add(P0, Creature("Bearer", 1, 1));
        var blade = s.Add(P0, new CardDefinition { Name = "Old Blade", Types = CardType.Artifact, Subtypes = new[] { "Equipment" } });
        s.Card(blade).AttachedTo = bearer;
        s.Card(blade).Counters[CounterKind.Hone] = 2;
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(bearer).Power);
        Assert.Equal(1, s.Card(bearer).Toughness);
    }

    [Fact]
    public async Task TriggeredAbilitiesOfAffectedPermanentsTriggerAnAdditionalTime()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, new CardDefinition
        {
            Name = "Echo Hall", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourPermanents)) { ExtraTriggers = true, Filter = new ObjectFilter(Subtype: "Dwarf") } },
        });
        s.InHand(P0, Creature("Chatty Dwarf", 1, 1) with
        {
            Subtypes = new[] { "Dwarf" },
            Abilities = new AbilityDefinition[] { new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield, Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "gain 1" } },
        });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task DrawsBecomeTwoExceptTheFirstInTheDrawStep()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Add(P1, new CardDefinition { Name = "Archive", Types = CardType.Enchantment, Replaces = Replacements.DrawTwoExceptFirstInDrawStep });
        int atTurnStart = 0, afterDrawStep = 0;
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan { TurnNumber: 2 }) atTurnStart = s.Game.State.GetPlayer(P1).Hand.Count;
            if (e is StepBegan { Step: Step.PrecombatMain } && s.Game.State.TurnNumber == 2) afterDrawStep = s.Game.State.GetPlayer(P1).Hand.Count;
        };
        await s.RunUntilTurn(3);
        Assert.Equal(atTurnStart + 1, afterDrawStep); // its own draw step: one card
    }

    [Fact]
    public async Task OpponentsCreaturesThatWouldDieAreExiledAndTheReplacerTriggers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var victim = s.Add(P1, Creature("Doomed", 1, 1));
        s.Add(P0, new CardDefinition
        {
            Name = "Hunt Leader", Types = CardType.Creature, Power = 2, Toughness = 2, Replaces = Replacements.OpponentsCreaturesExiledInsteadOfDying,
            Abilities = new AbilityDefinition[] { new TriggeredAbility { Trigger = TriggerEvent.CreatureExiledInstead, Effects = new Effect[] { new GainLife(3, Subject.You) }, Text = "gain 3" } },
        });
        s.InHand(P0, new CardDefinition
        {
            Name = "Zap", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) }, Effects = new Effect[] { new DealDamage(2, Subject.TargetAt(0)) } },
        });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(victim).Zone);
        Assert.Equal(life + 3, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ExchangeControlSwapsTwoPermanents()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var mine = s.Add(P0, Creature("Mine", 1, 1));
        var theirs = s.Add(P1, Creature("Theirs", 3, 3));
        s.InHand(P0, new CardDefinition
        {
            Name = "Swap", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility
            {
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You), new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
                TargetRule = TargetRule.ShareCardType, Effects = new Effect[] { new ExchangeControl(Subject.TargetAt(0), Subject.TargetAt(1)) },
            },
        });
        s.Attacker.Targets = (_, r) => new[] { Target.Of(mine), Target.Of(theirs) };
        await s.RunUntilTurn();
        Assert.Equal(P1, s.Card(mine).Controller);
        Assert.Equal(P0, s.Card(theirs).Controller);
    }

    [Fact]
    public async Task LeavingTheGraveyardTriggersForCreatureCards()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var dead = s.Game.SetupInLibrary(P0, Creature("Fallen", 2, 2));
        s.Add(P0, new CardDefinition
        {
            Name = "Mourning Road", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.LeavesGraveyard, Filter = new ObjectFilter(CardType.Creature), Effects = new Effect[] { new GainLife(4, Subject.You) }, Text = "gain 4" },
            },
        });
        s.InHand(P0, Sorcery("Bury and Raise", new MillUntil(Subject.You, new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any)),
            new ReturnFromGraveyard(new ObjectFilter(CardType.Creature), 1, Zone.Hand)));
        s.Attacker.Choose = (_, r) => r.Options.Take(1).Select(o => o.Id).ToList();
        s.Attacker.Discard = (view, count) => view.Self.Hand.Take(count).Select(c => c.Id).ToList(); // keep the card just found
        bool stacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, dead); }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(dead).Zone);
        Assert.Equal(life + 4, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task CastFromGraveyardConditionFollowsFlashback()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var spell = s.InHand(P0, Sorcery("Second Wind", new IfThen(new WasCastFromGraveyard(), new Effect[] { new GainLife(5, Subject.You) }, new Effect[] { new GainLife(1, Subject.You) }))
            with { Flashback = ManaCost.Parse("{R}") });
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 6, s.Game.State.GetPlayer(P0).Life); // cast from hand (1), then flashback (5)
        Assert.Equal(Zone.Exile, s.Card(spell).Zone);
    }

    [Fact]
    public async Task BeholdingRevealsOrChoosesAMatchingCard()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Creature("Elf Friend", 1, 1) with { Subtypes = new[] { "Elf" }, ManaCost = ManaCost.Parse("{9}") });
        s.InHand(P0, Sorcery("Elven Call", new Behold(new ObjectFilter(Subtype: "Elf"), new Effect[] { new GainLife(2, Subject.You) })));
        s.Attacker.Choose = (_, r) => r.Options.Take(1).Select(o => o.Id).ToList();
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life);
        Assert.Contains(s.Game.Log, e => e is CardsRevealed);
    }

    [Fact]
    public async Task ExiledCardsCastByPayingLifeInsteadOfMana()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bomb = s.Game.SetupInLibrary(P1, Creature("Big Thing", 5, 5) with { ManaCost = ManaCost.Parse("{4}{G}") });
        s.InHand(P0, new CardDefinition
        {
            Name = "Pilfer", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility
            {
                Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
                Effects = new Effect[] { new ExileTopPlayable(0, ChooseOne: false) { CountFrom = 99, From = Subject.TargetAt(0), PayLife = true } },
            },
        });
        bool stacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P1, bomb); }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bomb).Zone);
        Assert.Equal(P0, s.Card(bomb).Controller);
        Assert.Equal(life - 5, s.Game.State.GetPlayer(P0).Life);
    }
}
