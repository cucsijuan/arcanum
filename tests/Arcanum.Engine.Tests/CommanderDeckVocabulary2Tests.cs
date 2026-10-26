// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>More vocabulary of Commander decks: abilities from the command zone, revealing and casting from the top of the library.</summary>
public class CommanderDeckVocabulary2Tests
{
    private static CardDefinition Scripted(CardDefinition card, string json)
    {
        var script = CardScriptParser.Parse(json);
        return script.ApplyTo(card with { Abilities = script.Abilities, Spell = script.Spell ?? card.Spell });
    }

    [Fact]
    public async Task AnAbilityCanBeActivatedFromTheCommandZone()
    {
        var leader = Scripted(new CardDefinition
        {
            Name = "Sky Marshal", ManaCost = ManaCost.Parse("{4}{G}"), Types = CardType.Creature, Supertypes = Supertype.Legendary, Power = 2, Toughness = 3,
        }, """{ "abilities": [ { "cost": "{G}", "fromCommandZone": true, "effects": [ { "reanimate": "self" } ], "text": "{G}: Put this onto the battlefield from the command zone." } ] }""");
        var p0 = new TestController
        {
            Attack = (_, _, _) => Array.Empty<AttackDeclaration>(),
            Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance,
        };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0, StartingLife = 40, Commander = new CommanderRules() }, new[]
        {
            new PlayerSetup("A", p0, Decks.Of((GenericCards.Forest, 30)), new[] { leader }),
            new PlayerSetup("B", new TestController(), Decks.Of((GenericCards.Mountain, 30))),
        });
        game.SetupPermanent(P0, GenericCards.Forest);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        var commander = game.State.Cards.Values.Single(c => c.IsCommander);
        Assert.Equal(Zone.Battlefield, commander.Zone);
        Assert.False(game.State.GetPlayer(P0).CommanderCasts.ContainsKey(commander.Id)); // not cast: no commander tax later
    }

    [Fact]
    public async Task ASpellRevealedOnTopCanBeCastFreeAndTheRestGoToTheBottom()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool stacked = false;
        CardId cheap = default, land = default;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, land, cheap); }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList();
        s.Lands(P0, 3);
        s.Add(P0, Scripted(new CardDefinition { Name = "Sun Ritual", Types = CardType.Enchantment },
            """{ "abilities": [ { "trigger": "castSpell", "filter": { "castFromHand": true }, "effects": [ { "revealTopCastOneFree": "triggerAmount", "filter": { "maxManaValueTriggerAmount": true } } ], "text": "Whenever you cast a spell from your hand, reveal the top X cards …" } ] }"""));
        s.InHand(P0, Creature("Big Bear", 3, 3) with { ManaCost = ManaCost.Parse("{2}{R}") });
        cheap = s.Game.SetupInLibrary(P0, Creature("Small Bear", 1, 1) with { ManaCost = ManaCost.Parse("{1}{R}") });
        land = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(cheap).Zone); // cast without paying from among the revealed cards
        var library = s.Game.State.GetPlayer(P0).Library;
        Assert.Contains(land, library.TakeLast(2)); // the other two revealed cards on the bottom, in a random order
    }

    /// <summary>Player 0 casts what it can (choosing the most it may) and never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task AnAuraEnchantingAnOpponentAttachesToThatPlayer()
    {
        var (truce, _) = Arcanum.Data.CardData.CardFactory.Create(new Arcanum.Data.CardData.CardRecord
        {
            OracleId = "o-wary-pact", Name = "Wary Pact", Layout = "normal", TypeLine = "Enchantment — Aura", ManaCost = "{R}", Keywords = new[] { "Enchant" },
            OracleText = "Enchant opponent\nAt the beginning of enchanted opponent's end step, you and that player each draw a card.",
        }, CardScriptParser.Parse("""{ "abilities": [ { "trigger": "eachEndStep", "if": "enchantedPlayersTurn", "effects": [ { "draw": 1 }, { "draw": 1, "who": "enchantedPlayer" } ], "text": "At the beginning of enchanted opponent's end step, you and that player each draw a card." } ] }"""));
        var s = Casting();
        s.Lands(P0, 1);
        var id = s.InHand(P0, truce);
        int p0Draws = 0, p1Draws = 0;
        s.Game.EventRaised += e =>
        {
            if (e is CardDrawn d && s.Game.State.Step == Step.End) { if (d.Player == P0) p0Draws++; else p1Draws++; }
        };
        await s.RunUntilTurn(3);
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
        Assert.Equal(P1, s.Card(id).AttachedPlayer);
        Assert.Equal(1, p0Draws); // only at the enchanted opponent's end step (turn 2)
        Assert.Equal(1, p1Draws);
    }

    [Fact]
    public async Task SacrificingCreaturesAsTheSpellIsCastCopiesItForEach()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear A", 2, 2));
        s.Add(P0, Creature("Bear B", 2, 2));
        var plumb = Scripted(new CardDefinition { Name = "Deep Bargain", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant },
            """{ "additionalSacrificeAny": { "types": ["creature"] }, "sacrificeAnyNoReduction": true, "copyPerSacrificed": true, "spell": { "effects": [ { "draw": 1 }, { "loseLife": 1 } ], "text": "You draw a card and lose 1 life." } }""");
        s.InHand(P0, plumb);
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P0).Life); // the spell and two copies
        Assert.Empty(s.Game.State.PermanentsControlledBy(P0).Where(c => c.IsCreature));
    }

    [Fact]
    public async Task CreatureSpellsCanPayAGainedOffspringCost()
    {
        var s = Casting();
        s.Lands(P0, 2);
        s.Add(P0, Scripted(new CardDefinition { Name = "Nest Singer", Types = CardType.Enchantment }, """{ "creatureSpellsGainOffspring": "{1}" }"""));
        s.InHand(P0, Creature("Ember Hound", 3, 3) with { ManaCost = ManaCost.Parse("{R}") });
        await s.RunUntilTurn();
        var hounds = s.Game.State.PermanentsControlledBy(P0).Where(c => c.Name == "Ember Hound").ToList();
        Assert.Equal(2, hounds.Count);
        Assert.Contains(hounds, h => h.Definition.IsToken && h.Power == 1 && h.Toughness == 1);
    }

    [Fact]
    public async Task SacrificeXIsAnnouncedAndPaid()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool used = false;
        s.Attacker.Act = (_, legal) => !used && legal.OfType<ActivateAbility>().FirstOrDefault() is { } a ? (used = true) ? a : a : PassPriority.Instance;
        s.Lands(P0, 1);
        s.Add(P0, Scripted(new CardDefinition { Name = "Squirrel Chief", Types = CardType.Enchantment },
            """{ "abilities": [ { "cost": "{R}, sacrificeAny:Squirrel*X", "targets": ["creature"], "effects": [ { "pump": ["X", "-X"], "what": "target" } ], "text": "{R}, Sacrifice X Squirrels: Target creature gets +X/-X until end of turn." } ] }"""));
        var squirrel = new CardDefinition { Name = "Squirrel", Types = CardType.Creature, Subtypes = new[] { "Squirrel" }, Power = 1, Toughness = 1 };
        s.Add(P0, squirrel);
        s.Add(P0, squirrel);
        var bear = s.Add(P1, Creature("Target Bear", 2, 2));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone); // 4/0
        Assert.DoesNotContain(s.Game.State.PermanentsControlledBy(P0), c => c.Name == "Squirrel");
    }

    [Theory]
    [InlineData("""{ "clueFoodTreasureEach": true }""", new[] { "Clue", "Food", "Treasure" })]
    [InlineData("""{ "tokensPlusThatManySquirrels": true }""", new[] { "Squirrel", "Treasure" })]
    public async Task TokenCreationReplacements(string rules, string[] made)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted(new CardDefinition { Name = "Token Shaper", Types = CardType.Enchantment }, rules));
        s.InHand(P0, Scripted(new CardDefinition { Name = "Find Coin", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery },
            """{ "spell": { "effects": [ { "tokens": 1, "token": "Treasure" } ], "text": "Create a Treasure token." } }"""));
        await s.RunUntilTurn();
        var tokens = s.Game.State.PermanentsControlledBy(P0).Where(c => c.Definition.IsToken).Select(c => c.Name).OrderBy(n => n).ToArray();
        Assert.Equal(made, tokens);
    }

    [Fact]
    public async Task AnExtraTurnGiftIsTakenAfterThisTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted(new CardDefinition { Name = "Time Present", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery },
            """{ "gift": "extraTurn", "spell": { "effects": [ { "draw": 1 } ], "text": "Draw a card." } }"""));
        var turns = new List<PlayerId>();
        s.Game.EventRaised += e => { if (e is TurnBegan t) turns.Add(s.Game.State.ActivePlayer); };
        await s.RunUntilTurn(4);
        Assert.Equal(new[] { P0, P1, P1, P0 }, turns);
    }

    [Fact]
    public async Task OpponentsCantDrawMoreThanOneCardEachTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Scripted(new CardDefinition { Name = "Veil Keeper", Types = CardType.Enchantment }, """{ "opponentsDrawAtMostOne": true }"""));
        s.InHand(P0, Scripted(new CardDefinition { Name = "Big Study", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery },
            """{ "spell": { "effects": [ { "draw": 3 } ], "text": "Draw three cards." } }"""));
        int drawn = 0;
        bool started = false;
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan) started = true;
            if (started && e is CardDrawn d && d.Player == P0) drawn++;
        };
        await s.RunUntilTurn();
        Assert.Equal(1, drawn); // the first of the three; the other two don't happen
    }

    [Fact]
    public async Task HalvedCopiesOfADestroyedCreatureRoundUp()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var giant = s.Add(P1, Creature("Quarry Hulk", 5, 3));
        s.InHand(P0, Scripted(new CardDefinition { Name = "Split Trick", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant },
            """{ "spell": { "targets": ["creature"], "effects": [ { "destroy": "target" }, { "copiesOfDestroyedHalved": 2 } ], "text": "Destroy target creature. …" } }"""));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(giant).Zone);
        var copies = s.Game.State.PermanentsControlledBy(P1).Where(c => c.Name == "Quarry Hulk").ToList();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, c => { Assert.Equal(3, c.Power); Assert.Equal(2, c.Toughness); });
    }

    [Fact]
    public async Task PermanentsPhasingOutTogetherWithTheirAurasPhaseBackInOnce()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Ash Bear", 2, 2));
        var aura = s.Game.SetupPermanent(P0, new CardDefinition { Name = "Warm Cloak", Types = CardType.Enchantment, Subtypes = new[] { "Aura" },
            EnchantTarget = new TargetSpec(TargetKind.Creature) }, bear);
        s.InHand(P0, Scripted(new CardDefinition { Name = "Vanish All", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery },
            """{ "spell": { "effects": [ { "phaseOut": { "each": { "controller": "you" } } } ], "text": "All permanents you control phase out." } }"""));
        await s.RunUntilTurn(4);
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => id == aura));
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => id == bear));
        Assert.Equal(bear, s.Card(aura).AttachedTo);
    }
}
