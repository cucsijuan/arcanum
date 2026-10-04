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
