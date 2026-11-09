// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Rooms (doors cast, unlocked and locked), manifest dread, and the second main phase.</summary>
public class RoomAndDreadTests
{
    private static CardDefinition Scripted(CardDefinition card, string json)
    {
        var script = CardScriptParser.Parse(json);
        return script.ApplyTo(card with { Abilities = script.Abilities, Spell = script.Spell ?? card.Spell });
    }

    private static void NoAttacks(Scenario s) => s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();

    private static CardDefinition Door(string name, string cost, string json) => Scripted(new CardDefinition
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Enchantment, Subtypes = new[] { "Room" },
    }, json);

    /// <summary>Left door: when unlocked, gain 3 life. Right door: when unlocked, gain 5 life; creatures you control get +1/+1.</summary>
    private static CardDefinition Room() => new()
    {
        Name = "Cellar // Attic", ManaCost = ManaCost.Parse("{G}").Plus(ManaCost.Parse("{1}{G}")), Types = CardType.Enchantment, Subtypes = new[] { "Room" },
        SplitHalves = new[]
        {
            Door("Cellar", "{G}", """{ "abilities": [ { "trigger": "youUnlockThisDoor", "effects": [ { "gainLife": 3 } ], "text": "When you unlock this door, you gain 3 life." } ] }"""),
            Door("Attic", "{1}{G}", """
                { "abilities": [ { "trigger": "youUnlockThisDoor", "effects": [ { "gainLife": 5 } ], "text": "When you unlock this door, you gain 5 life." },
                  { "static": { "affects": "creatures:you", "pump": [1, 1] }, "text": "Creatures you control get +1/+1." } ] }
                """),
        },
    };

    [Fact]
    public async Task ARoomSpellEntersWithItsDoorUnlockedAndHasOnlyThatDoor()
    {
        var s = new Scenario();
        NoAttacks(s);
        var room = s.InHand(P0, Room());
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, GenericCards.Forest);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == room && c.Half == 0) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(room).Zone);
        Assert.Equal("Cellar", s.Card(room).Name);
        Assert.Equal(1, s.Card(room).ManaValue);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // the cast door's unlock trigger
        Assert.Equal(2, s.Card(bear).Power); // the locked door's static ability doesn't apply
    }

    [Fact]
    public async Task UnlockingTheOtherDoorFullyUnlocksTheRoom()
    {
        var s = new Scenario();
        NoAttacks(s);
        var room = s.InHand(P0, Room());
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var watcher = s.Add(P0, Scripted(Creature("Watcher", 1, 1), """
            { "abilities": [ { "trigger": "youFullyUnlockRoom", "effects": [ { "gainLife": 1 } ], "text": "Whenever you fully unlock a Room, you gain 1 life." } ] }
            """));
        for (int i = 0; i < 3; i++) s.Add(P0, GenericCards.Forest);
        s.Attacker.Act = (_, legal) =>
            legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == room && c.Half == 0) is { } cast ? cast
            : legal.OfType<UnlockDoor>().FirstOrDefault(u => u.Card == room) is { } unlock ? unlock : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.True(s.Card(room).IsUnlocked(0) && s.Card(room).IsUnlocked(1));
        Assert.Equal(3, s.Card(room).ManaValue);
        Assert.Equal(20 + 3 + 5 + 1, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(3, s.Card(bear).Power);
        Assert.Equal(Zone.Battlefield, s.Card(watcher).Zone);
    }

    [Fact]
    public async Task ARoomPutOntoTheBattlefieldOtherwiseHasBothDoorsLocked()
    {
        var s = new Scenario();
        NoAttacks(s);
        var room = s.Add(P0, Room());
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(room).UnlockedDoors);
        Assert.Equal("", s.Card(room).Name);
        Assert.Equal(0, s.Card(room).ManaValue);
        Assert.Empty(s.Card(room).Abilities);
    }

    [Fact]
    public async Task AnEffectUnlocksALockedDoorOfYourChoice()
    {
        var s = new Scenario();
        NoAttacks(s);
        var room = s.Add(P0, Room());
        var key = s.InHand(P0, Scripted(new CardDefinition { Name = "Key", ManaCost = ManaCost.Parse("{G}"), Types = CardType.Sorcery },
            """{ "spell": { "effects": [ { "unlockDoor": true } ], "text": "Unlock a locked door of a Room you control." } }"""));
        s.Add(P0, GenericCards.Forest);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == key) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Option = (_, r) => r.Options.ToList().FindIndex(o => o.Contains("Attic"));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(room).UnlockedDoors);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ManifestDreadManifestsOneOfTheTopTwoAndBinsTheOther()
    {
        var s = new Scenario();
        NoAttacks(s);
        var spell = s.InHand(P0, Scripted(new CardDefinition { Name = "Dread", ManaCost = ManaCost.Parse("{G}"), Types = CardType.Sorcery },
            """{ "spell": { "effects": [ { "manifestDread": true }, { "counters": 1, "what": "created", "kind": "+1/+1" } ], "text": "Manifest dread, then put a +1/+1 counter on that creature." } }"""));
        var watcher = s.Add(P0, Scripted(Creature("Watcher", 1, 1), """
            { "abilities": [ { "trigger": "youManifestDread", "effects": [ { "gainLife": 2 } ], "text": "Whenever you manifest dread, you gain 2 life." } ] }
            """));
        s.Add(P0, GenericCards.Forest);
        var first = s.InHand(P0, Creature("Top One", 3, 3));
        var second = s.InHand(P0, Creature("Top Two", 4, 4));
        bool stacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, first, second); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Choose = (_, r) => r.Options.Any(o => o.Id == second) ? new[] { second } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(second).Zone);
        Assert.True(s.Card(second).TurnedFaceDown);
        Assert.Equal(3, s.Card(second).Power); // a 2/2 with a +1/+1 counter
        Assert.Equal(Zone.Graveyard, s.Card(first).Zone);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Battlefield, s.Card(watcher).Zone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SurvivalTriggersAtTheSecondMainPhaseIfTapped(bool attacks)
    {
        var s = new Scenario();
        var survivor = s.Add(P0, Scripted(Creature("Survivor", 1, 1), """
            { "abilities": [ { "trigger": "yourSecondMainPhase", "if": { "not": "untapped" }, "effects": [ { "gainLife": 4 } ],
              "text": "Survival — At the beginning of your second main phase, if this creature is tapped, you gain 4 life." } ] }
            """));
        if (!attacks) NoAttacks(s);
        await s.RunUntilTurn();
        Assert.Equal(attacks ? 24 : 20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AMegamorphCreatureTurnedFaceUpGetsAPlusOneCounter()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Lands(P0, 4);
        var id = s.InHand(P0, new CardDefinition
        {
            Name = "Mist Raptor", ManaCost = ManaCost.Parse("{1}{G}{G}"), Types = CardType.Creature, Power = 3, Toughness = 3,
            Morph = ManaCost.Parse("{4}{G}"), Megamorph = true,
        });
        s.Add(P0, GenericCards.Forest);
        s.Attacker.Act = (_, legal) =>
            legal.Contains(new TurnFaceUp(id)) ? new TurnFaceUp(id) : legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.YesNo = (_, r) => r.Prompt.Contains("face down");
        await s.RunUntilTurn(4);
        Assert.False(s.Card(id).TurnedFaceDown);
        Assert.Equal(4, s.Card(id).Power);
    }
}
