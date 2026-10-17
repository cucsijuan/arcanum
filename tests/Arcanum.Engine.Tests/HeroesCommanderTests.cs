// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
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

/// <summary>Keywords and rules added for the heroes set's commander decks (intimidate, melee, …).</summary>
public class HeroesCommanderTests
{
    private static readonly PlayerId P2 = new(2);
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"heroes-commander-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine,
            ["oracle_text"] = oracle, ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    /// <summary>Activates the named permanent's first ability <paramref name="times"/> times (before that, <paramref name="setup"/> once); passes otherwise; never attacks.</summary>
    private static void ActivateTimes(Scenario s, string name, int times, Action? setup = null)
    {
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (setup is not null) { setup(); setup = null; }
            if (times > 0 && legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name) is { } activate) { times--; return activate; }
            return PassPriority.Instance;
        };
    }

    private static void PutOnTop(Scenario s, PlayerId player, CardId card)
    {
        var library = s.Game.State.GetPlayer(player).Library;
        library.Remove(card);
        library.Insert(0, card);
    }

    private static (Game Game, TestController[] Players) ThreePlayers()
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        return (game, players);
    }

    private static async Task Run(Game game, int turn = 2)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == turn) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ intimidate (702.13)

    private static Scenario IntimidateBlockedBy(CardDefinition blocker)
    {
        var s = new Scenario();
        s.Add(P0, Creature("Stalker", 2, 2, Keyword.Intimidate) with { Colors = new[] { "B" } });
        s.Add(P1, blocker);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (_, blockers, attackers) => blockers.Select(b => new BlockDeclaration(b, attackers[0])).ToList();
        return s;
    }

    [Fact]
    public async Task IntimidateCantBeBlockedByACreatureOfAnotherColor()
    {
        var s = IntimidateBlockedBy(Creature("Red Wall", 0, 4) with { Colors = new[] { "R" } });
        await s.RunUntilTurn();
        Assert.Null(s.Defender.LastBlockRequest); // nothing could block, so blocks weren't asked for
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task IntimidateCanBeBlockedByACreatureSharingAColor()
    {
        var s = IntimidateBlockedBy(Creature("Dusk Wall", 0, 4) with { Colors = new[] { "W", "B" } });
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task IntimidateCanBeBlockedByAnArtifactCreature()
    {
        var s = IntimidateBlockedBy(Creature("Iron Wall", 0, 4) with { Types = CardType.Artifact | CardType.Creature });
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ melee (702.121)

    [Fact]
    public async Task MeleeCountsEachOpponentAttacked()
    {
        var (game, players) = ThreePlayers();
        var duelist = game.SetupPermanent(P0, Creature("Duelist", 2, 2, Keyword.Melee));
        var squire = game.SetupPermanent(P0, Creature("Squire", 1, 1));
        players[0].Attack = (_, _, _) => new[] { new AttackDeclaration(duelist, P1), new AttackDeclaration(squire, P2) };
        await Run(game);
        Assert.Equal(16, game.State.GetPlayer(P1).Life); // 2 + 2 opponents attacked
        Assert.Equal(19, game.State.GetPlayer(P2).Life);
    }

    [Fact]
    public async Task MeleeCountsOneOpponentAttackedByEveryCreature()
    {
        var (game, players) = ThreePlayers();
        var duelist = game.SetupPermanent(P0, Creature("Duelist", 2, 2, Keyword.Melee));
        var squire = game.SetupPermanent(P0, Creature("Squire", 1, 1));
        players[0].Attack = (_, _, _) => new[] { new AttackDeclaration(duelist, P1), new AttackDeclaration(squire, P1) };
        await Run(game);
        Assert.Equal(16, game.State.GetPlayer(P1).Life); // (2 + 1) + 1
    }

    // ------------------------------------------------------------------ evolve (702.100)

    [Theory]
    [InlineData(3, 1, 1)] // greater power
    [InlineData(1, 3, 1)] // greater toughness
    [InlineData(2, 2, 0)] // neither greater
    public async Task EvolveGrowsWhenAGreaterCreatureEnters(int power, int toughness, int counters)
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var evolver = s.Add(P0, Creature("Mutant", 2, 2, Keyword.Evolve));
        s.InHand(P0, Creature("Newcomer", power, toughness));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.State.Battlefield, id => s.Card(id).Name == "Newcomer");
        Assert.Equal(counters, s.Card(evolver).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task EvolveComparesAgainItsGrownSize()
    {
        // Two 3/3s entering one after the other: the first makes the 2/2 a 3/3, so the second doesn't evolve it.
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var evolver = s.Add(P0, Creature("Mutant", 2, 2, Keyword.Evolve));
        s.Add(P0, GenericCards.Forest);
        s.InHand(P0, Creature("First", 3, 3));
        s.InHand(P0, Creature("Second", 3, 3));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name is "First" or "Second"));
        Assert.Equal(1, s.Card(evolver).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ adapt (701.46), monstrosity (701.37)

    [Fact]
    public async Task AdaptPutsCountersOnlyWithoutAny()
    {
        var s = new Scenario();
        var mutant = s.Add(P0, Make("Adaptable", "Creature — Mutant", """{ "abilities": [{ "cost": "{0}", "effects": [{ "adapt": 2 }], "text": "{0}: Adapt 2." }] }""", pt: "1/1"));
        ActivateTimes(s, "Adaptable", 2);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(mutant).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task MonstrosityHappensOnce()
    {
        var s = new Scenario();
        var beast = s.Add(P0, Make("Swelling Beast", "Creature — Beast", """{ "abilities": [{ "cost": "{0}", "effects": [{ "monstrosity": 3 }], "text": "{0}: Monstrosity 3." }] }""", pt: "2/2"));
        ActivateTimes(s, "Swelling Beast", 2);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(beast).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.True(s.Card(beast).Monstrous);
    }

    // ------------------------------------------------------------------ explore (701.44)

    private static CardDefinition Explorer => Make("Scout", "Creature — Scout", """{ "abilities": [{ "cost": "{0}", "effects": [{ "explore": "self" }], "text": "{0}: This creature explores." }] }""", pt: "1/1");

    [Fact]
    public async Task ExploringALandPutsItIntoTheHand()
    {
        var s = new Scenario();
        var scout = s.Add(P0, Explorer);
        var top = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id != top).Take(count).Select(c => c.Id).ToList();
        ActivateTimes(s, "Scout", 1, () => PutOnTop(s, P0, top));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(top).Zone);
        Assert.Equal(0, s.Card(scout).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExploringANonlandCardGivesACounterAndMayMillIt(bool mill)
    {
        var s = new Scenario();
        var scout = s.Add(P0, Explorer);
        var spell = s.Game.SetupInLibrary(P0, Creature("Hidden Beast", 3, 3));
        s.Attacker.YesNo = (_, _) => mill;
        ActivateTimes(s, "Scout", 1, () => PutOnTop(s, P0, spell));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(scout).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(mill ? Zone.Graveyard : Zone.Library, s.Card(spell).Zone);
        Assert.Contains(s.Game.Log, e => e is CardsRevealed r && r.Cards.Contains(spell));
    }

    // ------------------------------------------------------------------ connive (701.50)

    [Theory]
    [InlineData("Spare Idea", 1)] // a nonland card discarded: a counter
    [InlineData("Spare Land", 0)]
    public async Task ConnivingDrawsThenDiscards(string discard, int counters)
    {
        var s = new Scenario();
        var schemer = s.Add(P0, Make("Schemer", "Creature — Rogue", """{ "abilities": [{ "cost": "{0}", "effects": [{ "connive": "self" }], "text": "{0}: This creature connives." }] }""", pt: "1/1"));
        s.InHand(P0, Creature("Spare Idea", 9, 9) with { ManaCost = ManaCost.Parse("{9}") });
        s.InHand(P0, new CardDefinition { Name = "Spare Land", Types = CardType.Land });
        // The named card first (conniving), then any other (cleanup).
        s.Attacker.Discard = (view, count) => view.Self.Hand.OrderBy(c => c.Name == discard ? 0 : c.Name.StartsWith("Spare") ? 2 : 1).Take(count).Select(c => c.Id).ToList();
        ActivateTimes(s, "Schemer", 1);
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(schemer).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Contains(s.Game.State.GetPlayer(P0).Graveyard, id => s.Card(id).Name == discard);
    }

    // ------------------------------------------------------------------ convoke (702.51)

    [Fact]
    public async Task ConvokeCreaturesPayForTheirColorOrGenericMana()
    {
        // {2}{W}{W}: two white creatures pay {W}{W}, a green one pays {1}, a Mountain the last {1}.
        var s = new Scenario();
        s.Lands(P0, 1);
        var knights = new[] { s.Add(P0, Creature("Knight", 2, 2) with { Colors = new[] { "W" } }), s.Add(P0, Creature("Squire", 1, 1) with { Colors = new[] { "W" } }) };
        var elf = s.Add(P0, Creature("Elf", 1, 1) with { Colors = new[] { "G" } });
        s.InHand(P0, Make("Gathering", "Instant", """{ "spell": { "effects": [{ "gainLife": 5 }] } }""", "{2}{W}{W}", oracle: "Convoke", keywords: "Convoke"));
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Gathering") as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
        Assert.All(knights.Append(elf), id => Assert.True(s.Card(id).Tapped));
    }

    [Fact]
    public async Task ConvokeCantPayAColoredSymbolWithAnotherColor()
    {
        // {W}{W} with one white and one red creature (no lands): not castable.
        var s = new Scenario();
        s.Add(P0, Creature("Knight", 2, 2) with { Colors = new[] { "W" } });
        s.Add(P0, Creature("Raider", 2, 2) with { Colors = new[] { "R" } });
        var spell = s.InHand(P0, Make("Gathering", "Instant", """{ "spell": { "effects": [{ "gainLife": 5 }] } }""", "{W}{W}", oracle: "Convoke", keywords: "Convoke"));
        bool offered = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == spell); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task EachCreatureThatConvokedConnives()
    {
        var s = new Scenario();
        var schemers = new[] { s.Add(P0, Creature("Agent", 1, 1) with { Colors = new[] { "B" } }), s.Add(P0, Creature("Spy", 1, 1) with { Colors = new[] { "B" } }) };
        var target = s.Add(P1, Creature("Mark", 3, 3));
        s.InHand(P0, Make("Dark Plot", "Instant",
            """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }, { "connive": "convokers" }] } }""", "{B}{B}", oracle: "Convoke", keywords: "Convoke"));
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).Take(Math.Max(1, request.Max)).ToList();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(target) };
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Name != "Forest").Concat(view.Self.Hand).Take(count).Select(c => c.Id).ToList();
        s.InHand(P0, Creature("Idea One", 9, 9) with { ManaCost = ManaCost.Parse("{9}") });
        s.InHand(P0, Creature("Idea Two", 9, 9) with { ManaCost = ManaCost.Parse("{9}") });
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Dark Plot") as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(target).Zone);
        Assert.All(schemers, id => Assert.Equal(1, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne)));
    }

    // ------------------------------------------------------------------ unearth (702.84)

    private static void ToGraveyard(Scenario s, CardId id)
    {
        var card = s.Card(id);
        var owner = s.Game.State.GetPlayer(card.Owner);
        owner.Hand.Remove(id);
        card.Zone = Zone.Graveyard;
        owner.Graveyard.Add(id);
    }

    [Fact]
    public async Task UnearthReturnsAHastyCreatureForATurn()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var ghoul = s.InHand(P0, Make("Restless Ghoul", "Creature — Zombie", "{}", "{2}{B}", "3/2", "Unearth {R}", "Unearth"));
        ToGraveyard(s, ghoul);
        bool unearthed = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!unearthed && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ghoul) is { } activate) { unearthed = true; return activate; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.True(unearthed);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // it attacked the turn it came back
        Assert.Equal(Zone.Exile, s.Card(ghoul).Zone); // exiled at the end step
    }

    [Fact]
    public async Task AnUnearthedCreatureIsExiledInsteadOfDying()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var ghoul = s.InHand(P0, Make("Restless Ghoul", "Creature — Zombie", "{}", "{2}{B}", "3/2", "Unearth {R}", "Unearth"));
        s.InHand(P0, Make("Zap", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 3, "to": "target" }] } }""", "{R}"));
        ToGraveyard(s, ghoul);
        var steps = new Queue<Func<IReadOnlyList<PlayerAction>, PlayerAction?>>(new Func<IReadOnlyList<PlayerAction>, PlayerAction?>[]
        {
            legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ghoul),
            legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Zap"),
        });
        s.Attacker.Act = (_, legal) =>
        {
            if (steps.Count == 0 || steps.Peek()(legal) is not { } action) return PassPriority.Instance;
            steps.Dequeue();
            return action;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ghoul) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(ghoul).Zone);
        Assert.DoesNotContain(ghoul, s.Game.State.GetPlayer(P0).Graveyard);
    }

    // ------------------------------------------------------------------ rebound (702.88)

    [Fact]
    public async Task ReboundCastsTheSpellAgainAtTheNextUpkeep()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var spell = s.InHand(P0, Make("Echoing Gift", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 2 }] } }""", "{R}", oracle: "Rebound", keywords: "Rebound"));
        bool cast = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (cast || legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) is not { } play) return PassPriority.Instance;
            cast = true;
            return play;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Exile, s.Card(spell).Zone);
        await s.RunUntilTurn(4);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life); // cast again from exile, free
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone); // not cast from the hand this time
    }

    // ------------------------------------------------------------------ blitz (702.152)

    [Fact]
    public async Task ABlitzedCreatureAttacksIsSacrificedAndDrawsACard()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var raider = s.InHand(P0, Make("Lightning Raider", "Creature — Warrior", """{ "blitz": "{R}" }""", "{3}{R}", "3/3", "Blitz {R}", "Blitz"));
        s.Attacker.YesNo = (_, request) => request.Prompt.Contains("blitz");
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == raider) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int drawn = 0;
        s.Game.EventRaised += e => { if (e is CardDrawn { Player.Value: 0 } && s.Game.State.Step == Step.End) drawn++; };
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // haste
        Assert.Equal(Zone.Graveyard, s.Card(raider).Zone); // sacrificed at the end step
        Assert.Equal(1, drawn); // "when this dies, draw a card"
    }

    [Fact]
    public async Task BlitzFromTheGraveyardPaysItsDiscard()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var monk = s.InHand(P0, Make("Returning Monk", "Creature — Monk",
            """{ "blitz": "{R}", "blitzExtra": { "discard": 1 }, "blitzFromGraveyard": true }""", "{4}{R}", "3/3",
            "Blitz—{R}, Discard a card.\nYou may cast this card from your graveyard using its blitz ability.", "Blitz"));
        ToGraveyard(s, monk);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == monk) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int discarded = 0;
        s.Game.EventRaised += e => { if (e is CardDiscarded) discarded++; };
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.True(discarded >= 1);
        Assert.Equal(Zone.Graveyard, s.Card(monk).Zone);
    }

    // ------------------------------------------------------------------ foretell (702.143)

    [Fact]
    public async Task AForetoldCardIsCastLaterForItsForetellCost()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var vision = s.InHand(P0, Make("Distant Vision", "Instant",
            """{ "spell": { "effects": [{ "gainLife": 2 }, { "if": "castFromExile", "then": [{ "gainLife": 3 }] }] } }""", "{3}{R}{R}",
            oracle: "You gain 2 life. If this spell was cast from exile, you gain 3 more life.\nForetell {R}", keywords: "Foretell"));
        bool foretold = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!foretold && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == vision) is { } foretell) { foretold = true; return foretell; }
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == vision) is { } cast) return cast;
            return PassPriority.Instance;
        };
        bool castOnTurnOne = false;
        s.Game.EventRaised += e => { if (e is SpellCast c && c.Card == vision && s.Game.State.TurnNumber == 1) castOnTurnOne = true; };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Exile, s.Card(vision).Zone);
        Assert.True(s.Card(vision).FaceDown);
        Assert.False(s.Card(vision).IsVisibleTo(P1));
        Assert.True(s.Card(vision).IsVisibleTo(P0));
        Assert.False(castOnTurnOne); // not the turn it was foretold
        await s.RunUntilTurn(4);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Graveyard, s.Card(vision).Zone);
    }

    // ------------------------------------------------------------------ cast or copy triggers

    [Fact]
    public async Task CastOrCopyTriggersOnEachCastAndOnTheCopy()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Echo Mage", "Creature — Wizard", """{ "abilities": [{ "trigger": "castOrCopy", "filter": { "types": ["instant", "sorcery"] }, "effects": [{ "gainLife": 1 }], "text": "Whenever you cast or copy an instant or sorcery spell, you gain 1 life." }] }""", pt: "1/1"));
        var plain = s.InHand(P0, Make("Plain Spell", "Instant", """{ "spell": { "effects": [{ "gainLife": 0 }] } }""", "{R}"));
        var copier = s.InHand(P0, Make("Echo Copy", "Instant", """{ "spell": { "targets": ["spell"], "effects": [{ "copySpell": "target" }] } }""", "{R}"));
        int step = 0;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(plain) };
        s.Attacker.Act = (_, legal) =>
        {
            if (step == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == plain) is { } cast) { step = 1; return cast; }
            if (step == 1 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == copier) is { } copy) { step = 2; return copy; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(2, step); // both spells were cast in response to each other
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // cast Plain Spell, cast Echo Copy, the copy created
    }

    // ------------------------------------------------------------------ attacked and unblocked triggers

    private static async Task<int> DrawsDuringCombat(bool blocked)
    {
        var s = new Scenario();
        s.Add(P1, Make("Tripwire", "Artifact", """{ "abilities": [{ "trigger": "attackedUnblockedByOpponent", "effects": [{ "draw": 1, "who": "triggeredPlayer" }], "text": "Whenever a creature attacks you and isn't blocked, its controller draws a card." }] }"""));
        if (blocked) s.Add(P1, Creature("Guard", 0, 4));
        s.Add(P0, Creature("Raider", 2, 2));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (_, blockers, attackers) => blockers.Select(b => new BlockDeclaration(b, attackers[0])).ToList();
        int drawn = 0;
        s.Game.EventRaised += e => { if (e is CardDrawn { Player.Value: 0 } && s.Game.State.Step is Step.BeginCombat or Step.DeclareAttackers or Step.DeclareBlockers or Step.CombatDamage or Step.EndCombat) drawn++; };
        await s.RunUntilTurn();
        return drawn;
    }

    [Fact]
    public async Task AnUnblockedAttackDrawsACard()
    {
        Assert.Equal(1, await DrawsDuringCombat(blocked: false));
    }

    [Fact]
    public async Task ABlockedAttackDrawsNothing()
    {
        Assert.Equal(0, await DrawsDuringCombat(blocked: true));
    }

    // ------------------------------------------------------------------ "power minus self" counters

    [Theory]
    [InlineData(4, 3)] // an opponent's 4/4: 4 - 1
    [InlineData(1, 0)] // a 1/1: nothing
    public async Task CountersEqualToTheTargetsPowerLessItsOwn(int targetPower, int counters)
    {
        var s = new Scenario();
        var grower = s.Add(P0, Make("Grower", "Creature — Beast", """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "counters": { "diff": [{ "power": "target" }, { "power": "self" }] }, "what": "self" }], "text": "{0}: Put a +1/+1 counter on this creature for each power the target has more than it." }] }""", pt: "1/1"));
        var mark = s.Add(P1, Creature("Mark", targetPower, targetPower));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(mark) };
        ActivateTimes(s, "Grower", 1);
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(grower).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ colors among your permanents

    [Fact]
    public async Task EachCreatureGetsOnePowerForEachColorAmongYourPermanents()
    {
        var s = new Scenario();
        s.Add(P0, Make("Prism", "Enchantment", """{ "abilities": [{ "static": { "affects": "creatures:you", "pump": ["colorsAmongYourPermanents", 0] } }] }"""));
        var knight = s.Add(P0, Creature("Knight", 2, 2) with { Colors = new[] { "W" } });
        var sprite = s.Add(P0, Creature("Sprite", 1, 1) with { Colors = new[] { "U", "G" } });
        await s.RunUntilTurn();
        Assert.Equal(5, s.Card(knight).Power); // white, blue, green among your permanents: +3
        Assert.Equal(4, s.Card(sprite).Power);
    }

    // ------------------------------------------------------------------ counter kinds on the source

    [Fact]
    public async Task GainsLifeForEachKindOfCounterOnItself()
    {
        var s = new Scenario();
        var tally = s.Add(P0, Make("Tally", "Creature — Construct", """{ "abilities": [{ "cost": "{0}", "effects": [{ "gainLife": "counterKindsOnSelf" }], "text": "{0}: You gain 1 life for each kind of counter on this creature." }] }""", pt: "1/1"));
        s.Card(tally).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Card(tally).Counters[CounterKind.Charge] = 1;
        ActivateTimes(s, "Tally", 1);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // two kinds of counter
    }

    // ------------------------------------------------------------------ sacrifice all but

    [Fact]
    public async Task SacrificeAllButKeepsTheCreaturesChosen()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var purge = s.InHand(P0, Make("Purge", "Sorcery", """{ "spell": { "effects": [{ "sacrificeAllBut": 2, "filter": { "types": ["creature"] }, "who": "opponents" }] } }""", "{R}"));
        var ogres = new[] { s.Add(P1, Creature("Ogre A", 1, 1)), s.Add(P1, Creature("Ogre B", 1, 1)), s.Add(P1, Creature("Ogre C", 1, 1)), s.Add(P1, Creature("Ogre D", 1, 1)) };
        var kept = new List<CardId>();
        s.Defender.Choose = (_, request) =>
        {
            var keep = request.Options.Select(o => o.Id).Take(2).ToList();
            kept.AddRange(keep);
            return keep;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == purge) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, id => Assert.Equal(Zone.Battlefield, s.Card(id).Zone));
        Assert.All(ogres.Except(kept), id => Assert.Equal(Zone.Graveyard, s.Card(id).Zone));
    }

    // ------------------------------------------------------------------ opponent splits exiled cards

    [Fact]
    public async Task AnOpponentSplitsTheExiledCardsOneToTheBottomOfTheLibraryAndOneBackTapped()
    {
        var s = new Scenario();
        var splitter = s.Add(P0, Make("Splitter", "Artifact", """{ "abilities": [{ "cost": "{0}, exileGraveyardCards:2:creature, sacrifice", "effects": [{ "opponentSplitsExiled": true }], "text": "{0}, Exile two creature cards from your graveyard, Sacrifice this artifact: An opponent separates the exiled cards into two piles." }] }"""));
        var fallen = new[] { s.InHand(P0, Creature("Fallen Ash", 1, 1)), s.InHand(P0, Creature("Fallen Oak", 2, 2)) };
        foreach (var id in fallen) ToGraveyard(s, id);
        CardId? first = null;
        s.Defender.Choose = (_, request) =>
        {
            first ??= request.Options[0].Id;
            return new[] { first.Value };
        };
        ActivateTimes(s, "Splitter", 1);
        await s.RunUntilTurn();
        Assert.NotNull(first);
        Assert.Equal(first, s.Game.State.GetPlayer(P0).Library.Last()); // bottom of the library
        var other = fallen.Single(id => id != first);
        Assert.Equal(Zone.Battlefield, s.Card(other).Zone);
        Assert.True(s.Card(other).Tapped);
        Assert.Equal(P0, s.Card(other).Controller);
    }

    // ------------------------------------------------------------------ intervening "if" on opponents' lands

    [Fact]
    public async Task AnOpponentsLandTriggersOnlyWhenTheyControlMoreLands()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Watcher", "Enchantment", """{ "abilities": [{ "trigger": "permanentEnters", "filter": { "types": ["land"], "controller": "opponent" }, "if": "triggeredControllerHasMoreLands", "effects": [{ "gainLife": 1 }], "text": "Whenever a land enters under an opponent's control while that player controls more lands than you, you gain 1 life." }] }"""));
        for (int i = 0; i < 3; i++) s.Add(P1, GenericCards.Forest);
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn(3);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // P1 played a fourth land: 4 lands against 1
    }

    // ------------------------------------------------------------------ opponents can't cast on your turn

    [Fact]
    public async Task OpponentsCantCastOnYourTurnWhileTheEquipmentIsAttached()
    {
        var s = new Scenario();
        var bearer = s.Add(P0, Creature("Bearer", 1, 1));
        s.Game.SetupPermanent(P0, Make("Shackle", "Artifact — Equipment", """{ "opponentsCantCastDuringYourTurnWhileAttached": true }"""), attachTo: bearer);
        var rebuke = s.InHand(P1, Make("Rebuke", "Instant", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}"));
        s.Lands(P1, 1);
        var offeredOn = new List<int>();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().Any(c => c.Card == rebuke)) offeredOn.Add(s.Game.State.TurnNumber);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(3);
        Assert.DoesNotContain(1, offeredOn); // during the equipped player's turn
        Assert.Contains(2, offeredOn); // on their own turn
    }

    // ------------------------------------------------------------------ escalate by tapping

    [Fact]
    public async Task EscalateByTappingACreatureAllowsBothModes()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var helper = s.Add(P0, Creature("Helper", 1, 1));
        var pick = s.InHand(P0, Make("Pick Two", "Sorcery", """{ "escalateExtra": { "tap": { "types": ["creature"] }, "tapCount": 1 }, "spell": { "chooseCount": 2, "upTo": true, "modes": [ { "text": "a", "effects": [{ "gainLife": 1 }] }, { "text": "b", "effects": [{ "gainLife": 2 }] } ] } }""", "{R}", oracle: "Escalate—Tap an untapped creature you control.", keywords: "Escalate"));
        s.Attacker.Modes = (_, request) => request.Possible.ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == pick) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
        Assert.True(s.Card(helper).Tapped);
    }

    [Fact]
    public async Task WithoutACreatureToTapOnlyOneModeIsChosen()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var pick = s.InHand(P0, Make("Pick Two", "Sorcery", """{ "escalateExtra": { "tap": { "types": ["creature"] }, "tapCount": 1 }, "spell": { "chooseCount": 2, "upTo": true, "modes": [ { "text": "a", "effects": [{ "gainLife": 1 }] }, { "text": "b", "effects": [{ "gainLife": 2 }] } ] } }""", "{R}", oracle: "Escalate—Tap an untapped creature you control.", keywords: "Escalate"));
        s.Attacker.Modes = (_, request) => request.Possible.Take(request.Max).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == pick) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.True(s.Game.State.GetPlayer(P0).Life is 21 or 22); // one mode only
    }

    // ------------------------------------------------------------------ graveyard casting, once per turn

    [Fact]
    public async Task OneArtifactCreatureCardIsCastFromTheGraveyardEachTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Quarry", "Creature — Golem", """{ "graveyardCastOncePerTurn": { "filter": { "types": ["artifact"] }, "entersTapped": true } }""", pt: "1/1"));
        var clockwork = new[] { s.InHand(P0, Make("Clockwork A", "Artifact Creature — Construct", "{}", "{1}", "1/1")), s.InHand(P0, Make("Clockwork B", "Artifact Creature — Construct", "{}", "{1}", "1/1")) };
        foreach (var id in clockwork) ToGraveyard(s, id);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault() as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        var cast = clockwork.Where(id => s.Card(id).Zone == Zone.Battlefield).ToList();
        Assert.Single(cast);
        Assert.True(s.Card(cast[0]).Tapped); // the Quarry's entersTapped
        Assert.Equal(Zone.Graveyard, s.Card(clockwork.Single(id => id != cast[0])).Zone);
    }

    [Fact]
    public async Task ACreatureCardThatDidNotEnterTheGraveyardThisTurnCantBeCast()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Relic Keeper", "Creature — Spirit", """{ "graveyardCastOncePerTurn": { "filter": { "types": ["creature"] }, "notFromBattlefieldThisTurn": true } }""", pt: "1/1"));
        var old = s.InHand(P0, Creature("Old Hand", 1, 1));
        ToGraveyard(s, old);
        bool offered = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == old); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    // ------------------------------------------------------------------ "the second spell you cast each turn costs {2} less"

    [Fact]
    public async Task OnlyTheSecondSpellEachTurnCostsLess()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P0, Make("Twin Caster", "Creature — Wizard", """{ "abilities": [{ "spellCost": { "spells": {}, "amount": 2, "nth": 2 } }] }""", pt: "1/1"));
        foreach (var name in new[] { "First Gift", "Second Gift", "Third Gift" })
            s.InHand(P0, Make(name, "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) => legal.OfType<CastSpell>().OrderBy(c => view.FindCard(c.Card)?.Name).FirstOrDefault() as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // {2}, then {0}; the third ({2}) can't be paid with the one land left
    }

    // ------------------------------------------------------------------ "cast the copy by paying {3} rather than its mana cost"

    [Fact]
    public async Task ACopyCastForAnAlternativeCostPaysIt()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var lesson = s.InHand(P1, Make("Old Lesson", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 4 }] } }""", "{9}"));
        ToGraveyard(s, lesson);
        s.Add(P0, Make("Mimic Staff", "Artifact", """{ "abilities": [{ "cost": "{0}", "targets": [{ "kind": "graveyardCard", "controller": "any" }], "effects": [{ "castCopy": "target", "cost": "{3}" }] }] }"""));
        ActivateTimes(s, "Mimic Staff", 1);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(lesson) };
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Exile, s.Card(lesson).Zone);
        Assert.All(s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Mountain"), m => Assert.True(m.Tapped)); // {3} paid
    }

    // ------------------------------------------------------------------ "entered from a graveyard or was cast from a graveyard"

    [Fact]
    public async Task APermanentCastFromAGraveyardCountsAsComingFromIt()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Grave Watcher", "Enchantment",
            """{ "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "enteredOrCastFromGraveyard": true }, "effects": [{ "gainLife": 5 }] }] }"""));
        var returner = s.InHand(P0, Creature("Returner", 1, 1));
        s.InHand(P0, Creature("Newcomer", 1, 1) with { ManaCost = ManaCost.Parse("{0}") });
        ToGraveyard(s, returner);
        s.Add(P0, Make("Grave Gate", "Enchantment", """{ "graveyardCastOncePerTurn": { "filter": { "types": ["creature"] } } }"""));
        s.Game.State.GetCard(returner).ZoneChangedTurn = 1;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(returner).Zone);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life); // the one cast from the graveyard only
    }

    // ------------------------------------------------------------------ "at the beginning of each opponent's end step, that player …"

    [Fact]
    public async Task EachEndStepKnowsWhoseEndStepItIs()
    {
        var s = new Scenario();
        s.Add(P0, Make("Night Warden", "Enchantment",
            """{ "abilities": [{ "trigger": "eachEndStep", "when": { "not": "yourTurn" }, "effects": [{ "loseLife": 1, "who": "triggeredPlayer" }] }] }"""));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(3);
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life); // P1's end step only
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ shield counters

    [Theory]
    [InlineData(1, Zone.Battlefield)] // the first damage is prevented and the counter goes
    [InlineData(2, Zone.Graveyard)] // the second isn't
    public async Task AShieldCounterPreventsDamageOnceThenIsRemoved(int casts, Zone zone)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var mark = s.Add(P1, Creature("Mark", 2, 2));
        s.Card(mark).Counters[CounterKind.Shield] = 1;
        for (int i = 0; i < 2; i++)
            s.InHand(P0, Make("Zap", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 3, "to": "target" }] } }""", "{R}"));
        int cast = 0;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(mark) };
        s.Attacker.Act = (_, legal) =>
        {
            if (cast < casts && s.Game.State.Stack.Count == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Zap") is { } zap) { cast++; return zap; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(zone, s.Card(mark).Zone);
        Assert.Equal(0, s.Card(mark).CounterCount(CounterKind.Shield));
        if (zone == Zone.Battlefield) Assert.Equal(0, s.Card(mark).Damage);
    }

    [Fact]
    public async Task AShieldCounterStopsADestroyEffectAndIsRemoved()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var mark = s.Add(P1, Creature("Mark", 2, 2));
        s.Card(mark).Counters[CounterKind.Shield] = 1;
        var unmake = s.InHand(P0, Make("Unmake", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(mark) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == unmake) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(mark).Zone);
        Assert.Equal(0, s.Card(mark).CounterCount(CounterKind.Shield));
    }

    [Fact]
    public async Task LethalDamageThatCantBePreventedIgnoresAShieldCounter()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var mark = s.Add(P1, Creature("Mark", 2, 2));
        s.Card(mark).Counters[CounterKind.Shield] = 1;
        var blast = s.InHand(P0, Make("Blast", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "damageCantBePrevented": true }, { "damage": 3, "to": "target" }] } }""", "{R}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(mark) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == blast) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(mark).Zone); // the shield didn't stop the lethal damage
    }

    // ------------------------------------------------------------------ "+1/+1 counters on creatures you control"

    [Theory]
    [InlineData(true, 3)] // a creature you control: 2 counters, plus one
    [InlineData(false, 2)] // an opponent's creature isn't yours
    public async Task PlusOneCountersOnACreatureYouControlAreOneMoreEach(bool yours, int counters)
    {
        var s = new Scenario();
        s.Add(P0, Make("Doubler", "Enchantment", """{ "creaturePlusOneCountersPlusOne": true }"""));
        s.Add(P0, Make("Pumper", "Creature — Wizard", """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "counters": 2, "what": "target" }], "text": "{0}: Put two +1/+1 counters on target creature." }] }""", pt: "1/1"));
        var mark = s.Add(yours ? P0 : P1, Creature("Mark", 0, 1));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(mark) };
        ActivateTimes(s, "Pumper", 1);
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(mark).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ tap and mill as a cost

    [Fact]
    public async Task ATapAndMillCostPutsACardIntoTheGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, Make("Gem Idol", "Artifact", """{ "abilities": [{ "cost": "{T}, mill:1", "effects": [{ "gainLife": 1 }], "text": "{T}, Mill a card: You gain 1 life." }] }"""));
        int library = 0, graveyard = 0;
        // Counted just before the activation, after the opening hand was drawn.
        ActivateTimes(s, "Gem Idol", 1, () => { library = s.Game.State.GetPlayer(P0).Library.Count; graveyard = s.Game.State.GetPlayer(P0).Graveyard.Count; });
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(library - 1, s.Game.State.GetPlayer(P0).Library.Count);
        Assert.Equal(graveyard + 1, s.Game.State.GetPlayer(P0).Graveyard.Count);
    }

    // ------------------------------------------------------------------ remove a counter as a cost

    [Theory]
    [InlineData(true, 23)] // a counter to remove: the ability is offered and used
    [InlineData(false, 20)] // no counter anywhere: never offered
    public async Task ACounterCanBeRemovedFromANonlandPermanentToPayACost(bool hasCounter, int life)
    {
        var s = new Scenario();
        var idol = s.Add(P0, Make("Charm Idol", "Artifact", """{ "abilities": [{ "cost": "{0}, removeCounterFrom:nonland", "effects": [{ "gainLife": 3 }], "text": "{0}, Remove a counter from a nonland permanent you control: You gain 3 life." }] }"""));
        var battery = s.Add(P0, Creature("Battery", 1, 1));
        if (hasCounter) s.Card(battery).Counters[CounterKind.Charge] = 1;
        bool offered = false;
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == battery).Select(o => o.Id).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == idol) is { } activate) { offered = true; return activate; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(hasCounter, offered);
        Assert.Equal(0, s.Card(battery).CounterCount(CounterKind.Charge));
    }

    // ------------------------------------------------------------------ "whenever counters are put on this creature" (per counter)

    [Fact]
    public async Task EachCounterPutOnItTriggersOnce()
    {
        var s = new Scenario();
        var ledger = s.Add(P0, Make("Ledger", "Creature — Scribe", """{ "abilities": [{ "trigger": "countersPlaced", "onSelf": true, "perCounter": true, "effects": [{ "gainLife": 1 }], "text": "Whenever counters are put on this creature, you gain 1 life for each one." }] }""", pt: "1/1"));
        s.Add(P0, Make("Pumper", "Creature — Wizard", """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "counters": 2, "what": "target" }], "text": "{0}: Put two +1/+1 counters on target creature." }] }""", pt: "1/1"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ledger) };
        ActivateTimes(s, "Pumper", 1);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "if you put a counter on a creature this turn" (end step)

    [Fact]
    public async Task AnEndStepRewardsPuttingACounterOnACreatureThisTurn()
    {
        var s = new Scenario();
        s.Add(P0, Make("Cheer", "Enchantment", """{ "abilities": [{ "trigger": "eachEndStep", "if": "youPutCounterOnCreatureThisTurn", "effects": [{ "gainLife": 5 }] }] }"""));
        var sprout = s.Add(P0, Make("Sprout", "Creature — Plant", """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "counters": 1, "what": "target" }], "text": "{0}: Put a +1/+1 counter on target creature." }] }""", pt: "0/1"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(sprout) };
        ActivateTimes(s, "Sprout", 1);
        await s.RunUntilTurn(3);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life); // turn 1's end step only
    }

    [Fact]
    public async Task AnEndStepRewardsPuttingACounterOnItself()
    {
        var s = new Scenario();
        s.Add(P0, Make("Grower", "Creature — Beast", """{ "abilities": [{ "trigger": "endStep", "if": "counterPutOnSelfThisTurn", "effects": [{ "gainLife": 2 }] }, { "cost": "{0}", "effects": [{ "counters": 1, "what": "self" }], "text": "{0}: Put a +1/+1 counter on this creature." }] }""", pt: "1/1"));
        ActivateTimes(s, "Grower", 1);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "a card left your graveyard this turn"

    [Theory]
    [InlineData(true, 24)] // a card was returned from it
    [InlineData(false, 20)] // nothing left it
    public async Task ACardLeavingYourGraveyardThisTurnRewardsYou(bool leaves, int life)
    {
        var s = new Scenario();
        s.Add(P0, Make("Archivist", "Creature — Scribe", """{ "abilities": [{ "trigger": "endStep", "if": "cardLeftYourGraveyardThisTurn", "effects": [{ "gainLife": 4 }] }, { "cost": "{0}", "targets": [{ "kind": "graveyardCard", "controller": "you" }], "effects": [{ "bounce": "target" }], "text": "{0}: Return target card from your graveyard to your hand." }] }""", pt: "1/1"));
        var note = s.InHand(P0, Creature("Old Note", 1, 1));
        ToGraveyard(s, note);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(note) };
        if (leaves) ActivateTimes(s, "Archivist", 1);
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "if a player lost 4 or more life this turn"

    [Fact]
    public async Task AnEndStepAfterAPlayerLostFourLifeRewardsYou()
    {
        var s = new Scenario();
        s.Add(P0, Make("Tithe", "Enchantment", """{ "abilities": [{ "trigger": "eachEndStep", "if": { "playerLostLife": 4 }, "effects": [{ "gainLife": 1 }] }] }"""));
        s.Lands(P0, 1);
        var blast = s.InHand(P0, Make("Blast Two", "Sorcery", """{ "spell": { "targets": ["opponent"], "effects": [{ "damage": 4, "to": "target" }] } }""", "{R}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P1) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == blast) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ "you gain life equal to the number of players who lost life this turn"

    [Fact]
    public async Task YouGainLifeForEachPlayerWhoLostLifeThisTurn()
    {
        var s = new Scenario();
        s.Add(P0, Make("Tidal Idol", "Artifact", """{ "abilities": [{ "cost": "{0}", "effects": [{ "loseLife": 1, "who": "everyone" }, { "gainLife": "playersWhoLostLifeThisTurn" }], "text": "{0}: Each player loses 1 life. You gain life equal to the number of players who lost life this turn." }] }"""));
        ActivateTimes(s, "Tidal Idol", 1);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // 20 - 1 + 2: both players lost life
    }

    // ------------------------------------------------------------------ moving counters between two creatures

    [Fact]
    public async Task MoveCountersMovesExactlyOneCounter()
    {
        var s = new Scenario();
        var source = s.Add(P0, Creature("Source", 1, 1));
        var target = s.Add(P0, Creature("Target", 1, 1));
        s.Card(source).Counters[CounterKind.PlusOnePlusOne] = 3;
        s.Add(P0, Make("Shifter", "Enchantment", """{ "abilities": [{ "cost": "{0}", "targets": ["creature:you", "creature:you"], "targetRule": "allDifferent", "effects": [{ "moveCounters": { "from": "target", "to": "target2", "count": 1 } }], "text": "{0}: Move a +1/+1 counter from a creature you control onto another creature you control." }] }"""));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(source), Target.Of(target) };
        ActivateTimes(s, "Shifter", 1);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(source).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(1, s.Card(target).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ "if you control a Cleric, a Rogue, a Warrior and a Wizard"

    [Theory]
    [InlineData("Cleric|Rogue|Warrior|Wizard", 25)] // four different creatures
    [InlineData("Cleric|Rogue|Warrior", 21)] // three
    [InlineData("Cleric+Rogue|Warrior|Wizard", 21)] // one creature is both a Cleric and a Rogue: it counts once
    public async Task FullPartyNeedsFourDifferentCreatureTypes(string party, int life)
    {
        var s = new Scenario();
        s.Add(P0, Make("Party Hall", "Enchantment", """{ "abilities": [{ "cost": "{0}", "effects": [{ "if": "fullParty", "then": [{ "gainLife": 5 }], "else": [{ "gainLife": 1 }] }], "text": "{0}: Gain 5 life if you have a full party, otherwise 1 life." }] }"""));
        for (int i = 0; i < party.Split('|').Length; i++)
            s.Add(P0, Creature($"Member {i}", 1, 1) with { Subtypes = party.Split('|')[i].Split('+') });
        ActivateTimes(s, "Party Hall", 1);
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "the first coin flip each turn you win"

    [Fact]
    public async Task TheFirstCoinFlipEachTurnIsAWin()
    {
        var s = new Scenario();
        s.Add(P0, Make("Lucky Charm", "Enchantment", """{ "replaces": ["FirstCoinFlipEachTurnWins"] }"""));
        s.Add(P0, Make("Winner", "Enchantment", """{ "abilities": [{ "trigger": "youWinCoinFlip", "effects": [{ "gainLife": 2 }], "text": "Whenever you win a coin flip, you gain 2 life." }] }"""));
        s.Add(P0, Make("Coin", "Artifact", """{ "abilities": [{ "cost": "{0}", "effects": [{ "flipCoin": true }], "text": "{0}: Flip a coin." }] }"""));
        ActivateTimes(s, "Coin", 1);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ training (702.149)

    [Theory]
    [InlineData(0, 0)] // attacks alone (no other creature)
    [InlineData(1, 0)] // with a creature that doesn't have greater power
    [InlineData(3, 1)] // with a creature with greater power
    public async Task TrainingPutsACounterWhenAGreaterPowerCreatureAttacksToo(int allyPower, int counters)
    {
        var s = new Scenario();
        var trainee = s.Add(P0, Creature("Trainee", 1, 1, Keyword.Training));
        if (allyPower > 0) s.Add(P0, Creature("Ally", allyPower, allyPower));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(trainee).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ "creatures you control that have counters"

    [Fact]
    public async Task AModifiedCreatureGetsOnePowerMore()
    {
        var s = new Scenario();
        s.Add(P0, Make("Lord of Marks", "Enchantment", """{ "abilities": [{ "static": { "affects": "creatures:you", "filter": { "modified": true }, "pump": [1, 0] } }] }"""));
        var marked = s.Add(P0, Creature("Marked", 1, 1));
        s.Card(marked).Counters[CounterKind.PlusOnePlusOne] = 1;
        var plain = s.Add(P0, Creature("Plain", 1, 1));
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(marked).Power); // 1, plus the counter, plus 1
        Assert.Equal(1, s.Card(plain).Power);
    }

    // ------------------------------------------------------------------ "if a creature you control attacked this turn" (end step)

    [Theory]
    [InlineData(true, 23)] // a creature attacked this turn
    [InlineData(false, 20)] // nothing attacked
    public async Task AnEndStepRewardsAttackingThisTurn(bool attacks, int life)
    {
        var s = new Scenario();
        s.Add(P0, Make("Sunset Bell", "Enchantment", """{ "abilities": [{ "trigger": "endStep", "if": { "control": { "types": ["creature"], "attackedThisTurn": true } }, "effects": [{ "gainLife": 3 }], "text": "At the beginning of your end step, if a creature you control attacked this turn, you gain 3 life." }] }"""));
        s.Add(P0, Creature("Runner", 1, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attacks ? attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList() : new List<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "choose a random mode"

    [Fact]
    public async Task ARandomModeIsPickedWithoutAsking()
    {
        var s = new Scenario();
        s.Add(P0, Make("Dice Idol", "Artifact", """{ "abilities": [{ "cost": "{0}", "randomMode": true, "chooseCount": 1, "modes": [{ "text": "Gain 1 life.", "effects": [{ "gainLife": 1 }] }, { "text": "Gain 2 life.", "effects": [{ "gainLife": 2 }] }], "text": "{0}: Choose a mode at random. Gain 1 or 2 life." }] }"""));
        s.Attacker.Modes = (_, _) => throw new InvalidOperationException("a random mode is never chosen by the controller");
        ActivateTimes(s, "Dice Idol", 1);
        await s.RunUntilTurn();
        Assert.True(s.Game.State.GetPlayer(P0).Life is 21 or 22);
    }

    // ------------------------------------------------------------------ "chosen number" on entering, attack matching

    [Theory]
    [InlineData(3, 1, 15)] // a 3/1: its power is the chosen number, so 2 more damage
    [InlineData(2, 2, 18)] // a 2/2: neither its power nor its toughness is 3
    public async Task AnAttackMatchingTheChosenNumberDealsTwoMore(int power, int toughness, int life)
    {
        var s = new Scenario();
        var banner = s.InHand(P0, Make("Oath Banner", "Enchantment", """{ "chooseOnEnter": "number", "abilities": [{ "trigger": "creaturesAttackOpponent", "if": "attackersMatchChosenNumber", "effects": [{ "damage": 2, "to": "triggeredPlayer" }], "text": "Whenever creatures attack an opponent, if one has power or toughness equal to the chosen number, this deals 2 damage to that opponent." }] }""", "{0}"));
        s.Add(P0, Creature("Charger", power, toughness));
        s.Attacker.Number = (_, _) => 3;
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == banner) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ "greatest power among creatures you control as it was cast"

    [Fact]
    public async Task GainLifeEqualToTheGreatestPowerAmongYourCreatures()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Titan", 4, 4));
        s.Add(P0, Creature("Squire", 2, 2));
        var measure = s.InHand(P0, Make("Measure", "Sorcery", """{ "spell": { "effects": [{ "gainLife": "greatestPowerAsCast" }] } }""", "{R}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == measure) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ a mana ability with another effect resolves at once

    [Fact]
    public async Task AManaAbilityWithAnotherEffectResolvesWithoutTheStack()
    {
        var s = new Scenario();
        var stone = s.Add(P0, Make("Gem Stone", "Artifact", """{ "abilities": [{ "cost": "{T}", "effects": [{ "addMana": "{G}" }, { "gainLife": 1 }], "text": "{T}: Add {G}. You gain 1 life." }] }"""));
        bool activated = false;
        int lifeAfterActivation = -1, stackAfterActivation = -1;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == stone) is { } activate) { activated = true; return activate; }
            if (activated && lifeAfterActivation < 0)
            {
                lifeAfterActivation = s.Game.State.GetPlayer(P0).Life;
                stackAfterActivation = s.Game.State.Stack.Count;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(21, lifeAfterActivation); // already resolved when priority came back
        Assert.Equal(0, stackAfterActivation); // nothing was put on the stack
    }

    // ------------------------------------------------------------------ "if it had counters" (take counters from a leaving permanent)

    [Fact]
    public async Task ACreatureLeavingMovesItsCountersOntoAnother()
    {
        var s = new Scenario();
        s.Add(P0, Make("Counter Thief", "Enchantment", """{ "abilities": [{ "trigger": "permanentLeaves", "filter": { "controller": "you" }, "if": "triggeredHadCounters", "targets": ["permanent:you"], "effects": [{ "takeCountersOfTriggered": true, "onto": "target" }], "text": "Whenever a permanent you control leaves the battlefield with counters on it, put those counters on target permanent you control." }] }"""));
        var heir = s.Add(P0, Creature("Heir", 1, 1));
        var hoarder = s.Add(P0, Make("Hoarder", "Creature — Beast", """{ "abilities": [{ "cost": "{0}, sacrifice", "effects": [{ "gainLife": 0 }], "text": "{0}, Sacrifice this creature." }] }""", pt: "1/1"));
        s.Card(hoarder).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(heir) };
        ActivateTimes(s, "Hoarder", 1);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(hoarder).Zone);
        Assert.Equal(2, s.Card(heir).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ "the number of counters on a creature that died"

    [Fact]
    public async Task YouGainLifeForTheCountersOnACreatureThatDied()
    {
        var s = new Scenario();
        s.Add(P0, Make("Tally Keeper", "Enchantment", """{ "abilities": [{ "trigger": "permanentDies", "filter": { "controller": "you", "other": true }, "if": "triggeredHadCounters", "effects": [{ "gainLife": "triggeredCounterCount" }], "text": "Whenever another permanent you control dies with counters on it, you gain life equal to the number of those counters." }] }"""));
        var veteran = s.Add(P0, Make("Veteran", "Creature — Soldier", """{ "abilities": [{ "cost": "{0}, sacrifice", "effects": [{ "gainLife": 0 }], "text": "{0}, Sacrifice this creature." }] }""", pt: "1/1"));
        s.Card(veteran).Counters[CounterKind.PlusOnePlusOne] = 3;
        ActivateTimes(s, "Veteran", 1);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(veteran).Zone);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ "distribute counters from this creature"

    [Fact]
    public async Task CountersMoveFromTheSourceOneByOneToChosenCreatures()
    {
        var s = new Scenario();
        var source = s.Add(P0, Make("Distributor", "Creature — Wizard", """{ "abilities": [{ "cost": "{0}", "effects": [{ "distributeCountersFromSelf": "+1/+1" }], "text": "{0}: Distribute counters from this creature." }] }""", pt: "1/1"));
        var receiver = s.Add(P0, Creature("Receiver", 1, 1));
        s.Card(source).Counters[CounterKind.PlusOnePlusOne] = 3;
        var picks = new Queue<CardId[]>(new[] { new[] { receiver }, Array.Empty<CardId>() });
        s.Attacker.Choose = (_, _) => picks.Count > 0 ? picks.Dequeue() : Array.Empty<CardId>();
        s.Attacker.Number = (_, _) => 2;
        ActivateTimes(s, "Distributor", 1);
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(source).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(2, s.Card(receiver).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ "as it enters, remove counters from creatures"

    [Fact]
    public async Task EnteringRemovesTheCountersOfChosenCreaturesAndDoublesThem()
    {
        var s = new Scenario();
        var donors = new[] { s.Add(P0, Creature("Donor A", 1, 1)), s.Add(P0, Creature("Donor B", 1, 1)) };
        s.Card(donors[0]).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Card(donors[1]).Counters[CounterKind.PlusOnePlusOne] = 1;
        var brood = s.InHand(P0, Make("Brood Mother", "Creature — Beast", """{ "entersRemovingCountersFrom": { "types": ["creature"] } }""", "{0}", pt: "1/1"));
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == brood) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.All(donors, id => Assert.Equal(0, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne)));
        Assert.Equal(6, s.Card(brood).CounterCount(CounterKind.PlusOnePlusOne)); // three removed, two each
    }

    // ------------------------------------------------------------------ search with "may put it onto the battlefield if an opponent has more lands"

    [Theory]
    [InlineData(true, Zone.Battlefield)] // an opponent has more lands: it may go onto the battlefield, tapped
    [InlineData(false, Zone.Hand)] // no opponent has more lands
    public async Task ASearchedLandGoesOntoTheBattlefieldOnlyWhenAnOpponentHasMoreLands(bool opponentHasMore, Zone zone)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Lands(P1, opponentHasMore ? 2 : 0);
        var field = s.Game.SetupInLibrary(P0, new CardDefinition { Name = "Field Tile", Types = CardType.Land, Subtypes = new[] { "Plains" } });
        s.Add(P0, Make("Seeker", "Enchantment", """{ "abilities": [{ "cost": "{0}", "effects": [{ "search": { "subtype": "Plains" }, "to": "hand", "reveal": true, "mayBattlefieldIf": { "opponentHasMore": "lands" } }], "text": "{0}: Search your library for a Plains card." }] }"""));
        s.Attacker.YesNo = (_, _) => true;
        s.Attacker.Choose = (_, request) => request.Options.Take(1).Select(o => o.Id).ToList();
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id != field).Take(count).Select(c => c.Id).ToList(); // the cleanup step, if the land is in hand
        ActivateTimes(s, "Seeker", 1);
        await s.RunUntilTurn();
        Assert.Equal(zone, s.Card(field).Zone);
        if (zone == Zone.Battlefield) Assert.True(s.Card(field).Tapped);
    }

    // ------------------------------------------------------------------ "add one mana of any color" to a target player

    [Fact]
    public async Task AddManaOfAnyColorGoesToTheTargetPlayer()
    {
        var s = new Scenario();
        var gift = s.InHand(P0, Make("Mana Gift", "Sorcery", """{ "spell": { "targets": ["player"], "effects": [{ "addManaAnyColor": 1, "who": "target" }] } }""", "{0}"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == gift) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is ManaAdded m && m.Player == P1);
    }

    // ------------------------------------------------------------------ "you may sacrifice another creature; opponents lose life equal to its mana value"

    [Fact]
    public async Task ASacrificeCostsEachOpponentTheManaValueOfTheSacrificed()
    {
        var s = new Scenario();
        s.Add(P0, Make("Altar", "Creature — Cleric", """{ "abilities": [{ "cost": "{0}", "effects": [{ "mayPay": "Sacrifice another creature?", "cost": { "sacrifice": { "types": ["creature"], "other": true } }, "effects": [{ "loseLife": "sacrificedManaValue", "who": "opponents" }] }], "text": "{0}: You may sacrifice another creature. Each opponent loses life equal to its mana value." }] }""", pt: "1/1"));
        s.Add(P0, Creature("Fodder", 4, 4) with { ManaCost = ManaCost.Parse("{4}") });
        s.Attacker.Choose = (_, request) => request.Options.Take(1).Select(o => o.Id).ToList();
        ActivateTimes(s, "Altar", 1);
        await s.RunUntilTurn();
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ "choose one artifact per opponent"

    [Fact]
    public async Task EachOpponentLosesOneArtifactOfYourChoosing()
    {
        var (game, players) = ThreePlayers();
        game.SetupPermanent(P0, Make("Purger", "Enchantment", """{ "abilities": [{ "cost": "{0}", "effects": [{ "choose": { "types": ["artifact"] }, "perOpponent": true }, { "destroy": "chosen" }], "text": "{0}: For each opponent, choose an artifact that player controls. Destroy those artifacts." }] }"""));
        foreach (var opponent in new[] { P1, P2 })
            for (int i = 0; i < 2; i++) game.SetupPermanent(opponent, Make("Trinket", "Artifact", "{}"));
        bool activated = false;
        players[0].Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => game.State.GetCard(a.Source).Name == "Purger") is { } activate) { activated = true; return activate; }
            return PassPriority.Instance;
        };
        players[0].Choose = (_, request) => request.Options.Take(1).Select(o => o.Id).ToList();
        await Run(game);
        int Trinkets(PlayerId owner) => game.State.Battlefield.Count(id => game.State.GetCard(id).Name == "Trinket" && game.State.GetCard(id).Owner == owner);
        Assert.Equal(1, Trinkets(P1));
        Assert.Equal(1, Trinkets(P2));
    }

    // ------------------------------------------------------------------ "the opponent chooses from the creatures with the greatest mana value"

    [Fact]
    public async Task AnOpponentChoosesFromTheGreatestManaValueCreaturesOnly()
    {
        var s = new Scenario();
        s.Add(P0, Make("Dread Gate", "Enchantment", """{ "abilities": [{ "cost": "{0}", "effects": [{ "choose": { "types": ["creature"], "controller": "you", "greatestManaValueAmongTheirs": true }, "chooser": "opponents" }, { "bounce": "chosen" }], "text": "{0}: An opponent chooses a creature with the greatest mana value among their creatures and returns it to hand." }] }"""));
        var cub = s.Add(P1, Creature("Cub", 1, 1) with { ManaCost = ManaCost.Parse("{2}") });
        var titan = s.Add(P1, Creature("Titan", 5, 5) with { ManaCost = ManaCost.Parse("{5}") });
        var offered = new List<CardId>();
        s.Defender.Choose = (_, request) =>
        {
            offered.AddRange(request.Options.Select(o => o.Id));
            return request.Options.Take(1).Select(o => o.Id).ToList();
        };
        ActivateTimes(s, "Dread Gate", 1);
        await s.RunUntilTurn();
        Assert.Equal(new[] { titan }, offered);
        Assert.Equal(Zone.Hand, s.Card(titan).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(cub).Zone);
    }

    // ------------------------------------------------------------------ "instant and sorcery cards in your graveyard" as a life loss for opponents

    [Fact]
    public async Task EachOpponentLosesLifeForEachInstantAndSorceryInYourGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, Make("Ledger Drain", "Enchantment", """{ "abilities": [{ "cost": "{0}", "effects": [{ "eachPlayer": [{ "loseLife": { "graveyard": { "types": ["instant", "sorcery"] }, "of": "sourceController" } }], "who": "opponents" }], "text": "{0}: Each opponent loses 1 life for each instant and sorcery card in your graveyard." }] }"""));
        foreach (var name in new[] { "Spent Ash", "Spent Oath" }) ToGraveyard(s, s.InHand(P0, Make(name, "Instant", "{}")));
        ActivateTimes(s, "Ledger Drain", 1);
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ a graveyard trigger on your attacks

    [Fact]
    public async Task ACardInYourGraveyardTriggersOnYourAttacks()
    {
        var s = new Scenario();
        var ghost = s.InHand(P0, Make("Battle Ghost", "Creature — Spirit", """{ "abilities": [{ "trigger": "youAttack", "fromGraveyard": true, "effects": [{ "gainLife": 4 }], "text": "Whenever you attack, you gain 4 life." }] }""", pt: "1/1"));
        ToGraveyard(s, ghost);
        s.Add(P0, Creature("Raider", 1, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ improvise given to the spells you cast (702.126)

    [Fact]
    public async Task NonartifactSpellsYouCastHaveImprovise()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Gallery", "Artifact", """{ "abilities": [{ "spellsHaveImprovise": { "not": ["artifact"] }, "text": "Nonartifact spells you cast have improvise." }] }"""));
        var a = s.Add(P0, Make("Cog", "Artifact", "{}", "{1}"));
        var b = s.Add(P0, Make("Gear", "Artifact", "{}", "{1}"));
        var ox = s.InHand(P0, Make("Heavy Ox", "Creature — Ox", "{}", "{4}", "3/3"));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Heavy Ox") as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(ox).Zone);
        Assert.True(s.Card(a).Tapped && s.Card(b).Tapped);
    }

    [Fact]
    public async Task ArtifactSpellsDontGetImproviseFromTheGallery()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Gallery", "Artifact", """{ "abilities": [{ "spellsHaveImprovise": { "not": ["artifact"] }, "text": "Nonartifact spells you cast have improvise." }] }"""));
        s.Add(P0, Make("Cog", "Artifact", "{}", "{1}"));
        s.Add(P0, Make("Gear", "Artifact", "{}", "{1}"));
        var spell = s.InHand(P0, Make("Heavy Idol", "Artifact", "{}", "{4}"));
        bool offered = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == spell); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    // ------------------------------------------------------------------ vow counters: keep one, sacrifice the rest, can't attack you

    private const string VowSpell = """{ "spell": { "effects": [{ "sacrificeAllBut": 1, "filter": { "types": ["creature"] }, "who": "everyone", "exactly": true, "putCounter": "vow", "keptCantAttackYou": true }] } }""";

    [Fact]
    public async Task EachPlayerKeepsOneCreatureWithAVowCounterAndSacrificesTheRest()
    {
        var s = new Scenario();
        s.Lands(P0, 5);
        var mine = new[] { s.Add(P0, Creature("Squire", 1, 1)), s.Add(P0, Creature("Knight", 2, 2)) };
        var theirs = new[] { s.Add(P1, Creature("Raider", 3, 3)), s.Add(P1, Creature("Brute", 4, 4)) };
        s.InHand(P0, Make("Binding Oath", "Sorcery", VowSpell, "{5}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Binding Oath") as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(1, mine.Count(id => s.Card(id).Zone == Zone.Battlefield));
        Assert.Equal(1, theirs.Count(id => s.Card(id).Zone == Zone.Battlefield));
        Assert.All(mine.Concat(theirs).Where(id => s.Card(id).Zone == Zone.Battlefield), id => Assert.Equal(1, s.Card(id).CounterCount(CounterKind.Vow)));
    }

    [Fact]
    public async Task AVowCreatureCantAttackTheSpellsControllerButCanAttackOthers()
    {
        var (game, players) = ThreePlayers();
        var oath = game.SetupInHand(P0, Make("Binding Oath", "Sorcery", VowSpell, "{0}"));
        var raider = game.SetupPermanent(P1, Creature("Raider", 3, 3));
        players[0].Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        players[0].Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == oath) as PlayerAction ?? PassPriority.Instance;
        players[1].Attack = (_, attackers, ds) => new[] { new AttackDeclaration(raider, P2) };
        await Run(game, 3);
        Assert.Equal(1, game.State.GetCard(raider).CounterCount(CounterKind.Vow));
        Assert.Equal(20, game.State.GetPlayer(P0).Life);
        Assert.Equal(17, game.State.GetPlayer(P2).Life); // it can still attack the others
    }

    [Fact]
    public async Task AVowCreatureIsRefusedAnAttackOnTheSpellsController()
    {
        var (game, players) = ThreePlayers();
        var oath = game.SetupInHand(P0, Make("Binding Oath", "Sorcery", VowSpell, "{0}"));
        var raider = game.SetupPermanent(P1, Creature("Raider", 3, 3));
        players[0].Act = (view, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == oath) as PlayerAction ?? PassPriority.Instance;
        players[1].Attack = (_, _, _) => new[] { new AttackDeclaration(raider, P0) };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(game, 3));
    }

    // ------------------------------------------------------------------ exile until a card, cast it free, the rest stay exiled; a creature gains haste and a sacrifice

    [Fact]
    public async Task ExiledCardsStayExiledAndACreatureCastThisWayIsSacrificedAtTheEnd()
    {
        var s = new Scenario();
        s.Add(P0, Make("Scribe", "Creature — Human", """{ "abilities": [{ "cost": "{T}", "sorcery": true, "targets": ["opponent"], "effects": [{ "revealUntil": { "types": ["instant", "sorcery", "creature"] }, "from": "target", "castFree": true, "rest": "exile", "creatureHasteAndSacrifice": true }], "text": "x" }] }""", "{1}", "1/1"));
        var first = s.Game.SetupInLibrary(P1, Make("Spare Idol", "Artifact", "{}", "{1}"));
        var second = s.Game.SetupInLibrary(P1, Make("Spare Relic", "Artifact", "{}", "{1}"));
        var hit = s.Game.SetupInLibrary(P1, Make("Hired Brute", "Creature — Human", "{}", "{2}", "3/3"));
        ActivateTimes(s, "Scribe", 1, () => s.Restack(P1, first, second, hit));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(first).Zone);
        Assert.Equal(Zone.Exile, s.Card(second).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(hit).Zone); // cast free, entered, sacrificed at the end step
    }

    // ------------------------------------------------------------------ modified creatures: a counter, base power and toughness, a sacrifice cost

    private const string FallenHeroScript = """
        { "abilities": [
          { "trigger": "attacks", "targets": ["creature"], "effects": [
              { "may": "Put a cell counter on target creature?", "effects": [{ "counters": 1, "what": "target", "kind": "cell" }] },
              { "become": { "each": { "types": ["creature"], "controller": "you", "modified": true } }, "power": 7, "toughness": 5 }],
            "text": "x" },
          { "cost": "{0}, sacrificeAny:creature, fromGraveyard", "sacrificeFilter": { "types": ["creature"], "modified": true }, "effects": [{ "reanimate": "self", "tapped": true }], "text": "y" }] }
        """;

    [Fact]
    public async Task ModifiedCreaturesBecomeSevenFiveAfterTheCounterIsPut()
    {
        var s = new Scenario();
        var hero = s.Add(P0, Make("Fallen Blade", "Creature — Human", FallenHeroScript, "{1}", "3/3"));
        var plain = s.Add(P0, Creature("Plain Squire", 1, 1));
        var marked = s.Add(P0, Creature("Marked Squire", 1, 1));
        s.Card(marked).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == hero).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(plain) };
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        bool checkedMidTurn = false;
        s.Attacker.Act = (_, _) =>
        {
            if (!checkedMidTurn && s.Game.State.Step == Step.DeclareBlockers)
            {
                checkedMidTurn = true;
                Assert.Equal(1, s.Card(plain).CounterCount(CounterKind.Cell));
                Assert.Equal(7, s.Card(plain).Power); Assert.Equal(5, s.Card(plain).Toughness); // modified by the counter it just got
                Assert.Equal(8, s.Card(marked).Power); // base 7/5 and its +1/+1 counter
                Assert.Equal(3, s.Card(hero).Power); // not modified: unchanged
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(checkedMidTurn);
    }

    [Fact]
    public async Task ASacrificeCostThatNeedsAModifiedCreatureIsRefusedWithoutOne()
    {
        var s = new Scenario();
        var hero = s.InHand(P0, Make("Fallen Blade", "Creature — Human", FallenHeroScript, "{1}", "3/3"));
        ToGraveyard(s, hero);
        s.Add(P0, Creature("Plain Squire", 1, 1));
        bool offered = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == hero); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task SacrificingAModifiedCreatureReturnsTheCardFromTheGraveyardTapped()
    {
        var s = new Scenario();
        var hero = s.InHand(P0, Make("Fallen Blade", "Creature — Human", FallenHeroScript, "{1}", "3/3"));
        ToGraveyard(s, hero);
        var marked = s.Add(P0, Creature("Marked Squire", 1, 1));
        s.Card(marked).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.Add(P0, Creature("Plain Squire", 1, 1));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Min).Select(o => o.Id).ToList();
        ActivateTimes(s, "Fallen Blade", 1);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(hero).Zone);
        Assert.True(s.Card(hero).Tapped);
        Assert.Equal(Zone.Graveyard, s.Card(marked).Zone);
    }

    // ------------------------------------------------------------------ "whenever an opponent becomes the monarch": control for as long as they are

    [Fact]
    public async Task ControlOfACreatureLastsOnlyWhileThatOpponentIsTheMonarch()
    {
        var s = new Scenario();
        s.Lands(P0, 0);
        var keeper = s.InHand(P0, Make("Court Thief", "Creature — Human Knight", """
            { "abilities": [
              { "trigger": "enters", "targets": ["opponent"], "effects": [{ "becomeMonarch": "target" }], "text": "a" },
              { "trigger": "opponentBecomesMonarch", "targets": [{ "kind": "creature", "controller": "opponent", "controlledByTriggeredPlayer": true }],
                "effects": [{ "gainControl": "target", "whileTriggeredPlayerIsMonarch": true }], "text": "b" },
              { "static": { "affects": "creatures:you", "filter": { "notOwnedByYou": true }, "pump": [2, 2], "cantBeSacrificed": true }, "text": "c" }] }
            """, "{0}", "3/4"));
        var brute = s.Add(P0, Creature("Brute", 2, 2));
        s.Add(P0, Make("Altar", "Artifact", """{ "abilities": [{ "cost": "{0}", "effects": [{ "sacrifice": 1, "filter": { "types": ["creature"] }, "who": "you" }], "text": "d" }] }""", "{0}"));
        var victim = s.Add(P1, Creature("Victim", 2, 2));
        var fodder = s.Add(P0, Creature("Fodder", 1, 1));
        var offeredToSacrifice = new List<CardId>();
        s.Attacker.Choose = (_, request) => { offeredToSacrifice.AddRange(request.Options.Select(o => o.Id)); return request.Options.Where(o => o.Id == fodder).Select(o => o.Id).ToList(); };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == brute).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        bool seenStolen = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == keeper) is { } cast) return cast;
            if (s.Game.State.Step == Step.BeginCombat && !seenStolen)
            {
                seenStolen = true;
                Assert.Equal(P0, s.Card(victim).Controller);
                Assert.Equal(4, s.Card(victim).Power); // +2/+2 for a creature its controller doesn't own
                if (legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == "Altar") is { } altar) return altar;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(seenStolen);
        Assert.Equal(P0, s.Game.State.Monarch);
        Assert.Equal(P1, s.Card(victim).Controller); // P0 became the monarch by combat damage, so control ended
        Assert.DoesNotContain(victim, offeredToSacrifice);
        Assert.Contains(brute, offeredToSacrifice);
        Assert.Equal(2, s.Card(victim).Power);
    }

    // ------------------------------------------------------------------ prevention that puts counters / prevention with a trigger

    [Fact]
    public async Task DamageToTheChosenCreatureIsPreventedAndBecomesCounters()
    {
        var s = new Scenario();
        s.Add(P0, Make("Pinger", "Artifact", """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "damage": 3, "to": "target" }], "text": "p" }] }"""));
        var hero = s.Add(P0, Creature("Hero", 1, 1));
        s.InHand(P0, Make("Warded Guard", "Creature — Human", """{ "abilities": [{ "trigger": "enters", "targets": ["creature:you"], "effects": [{ "preventDamage": true, "to": "target", "putCounters": true }], "text": "w" }] }""", "{0}", "1/1"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(hero) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool pinged = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Warded Guard") is { } cast) return cast;
            if (!pinged && s.Game.State.Stack.Count == 0 && s.Game.State.Battlefield.Any(id => s.Card(id).Name == "Warded Guard") && legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == "Pinger") is { } ping) { pinged = true; return ping; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(pinged);
        Assert.Equal(3, s.Card(hero).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(hero).Damage);
        Assert.Equal(Zone.Battlefield, s.Card(hero).Zone);
    }

    [Fact]
    public async Task DamageFromAnOpponentsCreatureIsPreventedAndYourCommanderRetaliates()
    {
        var leader = Make("Rally Leader", "Legendary Creature — Human", "{}", "{0}", "5/5");
        var players = new[] { new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() }, new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() } };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0, Commander = new CommanderRules() }, new[]
        {
            new PlayerSetup("A", players[0], Decks.Of((GenericCards.Forest, 30)), new[] { leader }),
            new PlayerSetup("B", players[1], Decks.Of((GenericCards.Forest, 30))),
        });
        var zapper = game.SetupPermanent(P1, Make("Zapper", "Creature — Wizard", """{ "abilities": [{ "cost": "{0}", "targets": ["player"], "effects": [{ "damage": 2, "to": "target" }], "text": "z" }] }""", "{0}", "2/2"));
        var oath = game.SetupInHand(P0, Make("Sovereign Oath", "Instant", """
            { "spell": { "effects": [{ "preventDamage": "toYou", "fromOpponents": true,
              "whenCreatureDamagePrevented": [{ "eachDealsDamageEqualToPower": { "each": { "types": ["creature"], "controller": "you", "commander": true } }, "to": "triggered" }] }] } }
            """, "{0}"));
        players[0].Act = (view, legal) =>
            legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Rally Leader") as PlayerAction
            ?? legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == oath) as PlayerAction ?? PassPriority.Instance;
        bool zapped = false;
        players[1].Targets = (_, _) => new[] { Target.Of(P0) };
        players[1].Act = (view, legal) =>
        {
            if (!zapped && game.State.Stack.Count == 0 && game.State.Battlefield.Any(id => game.State.GetCard(id).Name == "Rally Leader") && game.State.Cards.Values.Any(c => c.Id == oath && c.Zone == Zone.Graveyard)
                && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == zapper) is { } zap) { zapped = true; return zap; }
            return PassPriority.Instance;
        };
        await Run(game, 2);
        Assert.True(zapped);
        Assert.Equal(20, game.State.GetPlayer(P0).Life); // the damage was prevented
        Assert.Equal(Zone.Graveyard, game.State.GetCard(zapper).Zone); // the commander dealt 5 damage to it
    }

    // ------------------------------------------------------------------ a blocked creature, a granted trigger that looks at the top cards and casts one free

    private const string BlockedFlameScript = """
        { "spell": { "targets": [{ "kind": "creature", "controller": "you", "filter": { "blocked": true } }], "effects": [
            { "pump": ["X", 0], "what": "target", "keywords": ["Trample"] },
            { "become": "target", "abilities": [{ "trigger": "combatDamageToPlayer", "effects": [
                { "lookAtTop": "triggerAmount", "filter": { "not": ["land"] }, "take": 1, "to": "exile", "castFree": true, "restAnyOrder": false }],
              "text": "Whenever this creature deals combat damage to a player, look at that many cards from the top of your library." }] }] } }
        """;

    [Fact]
    public async Task ABlockedCreatureTramplesOverAndCastsACardFromTheTopOfTheLibrary()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var raider = s.Add(P0, Creature("Raider", 2, 2));
        var wall = s.Add(P1, Creature("Wall", 0, 1));
        s.InHand(P0, Make("Blazing Parry", "Instant", BlockedFlameScript, "{X}{R}{R}"));
        var land = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var boon = s.Game.SetupInLibrary(P0, Make("Quick Boon", "Instant", """{ "spell": { "effects": [{ "gainLife": 5 }] } }""", "{3}"));
        var rest = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == raider).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (_, blockers, attackers) => blockers.Select(b => new BlockDeclaration(b, attackers[0])).ToList();
        bool restacked = false;
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList();
        s.Attacker.Act = (view, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, boon, rest, land); }
            if (s.Game.State.Step == Step.DeclareBlockers && legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == "Blazing Parry") is { } cast) return cast;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(wall).Zone);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // 4 power, 1 to the wall, 3 trampled over
        Assert.Equal(Zone.Graveyard, s.Card(boon).Zone); // exiled, then cast free
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Library, s.Card(rest).Zone); // not taken: on the bottom
    }

    [Fact]
    public async Task OnlyABlockedCreatureCanBeTargetedByTheParry()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var raider = s.Add(P0, Creature("Raider", 2, 2));
        var spell = s.InHand(P0, Make("Blazing Parry", "Instant", BlockedFlameScript, "{X}{R}{R}"));
        bool offered = false;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == raider).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == spell); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered); // nothing is blocked
    }

    // ------------------------------------------------------------------ "cast as though it had flash if it targets a permanent you control"

    private const string MirrorRite = """{ "flashIfTargets": { "controller": "you" }, "spell": { "targets": ["permanent"], "effects": [{ "copy": "target" }] } }""";

    [Fact]
    public async Task ASorceryCanBeCastAtInstantSpeedOnlyTargetingYourOwnPermanent()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var mine = s.Add(P0, Creature("Statue", 1, 1));
        var theirs = s.Add(P1, Creature("Idol", 2, 2));
        var rite = s.InHand(P0, Make("Mirror Rite", "Sorcery", MirrorRite, "{4}"));
        var seen = new List<CardId>();
        s.Attacker.Targets = (_, request) => { seen.AddRange(request.LegalAt(0).Select(t => t.Card!.Value)); return new[] { Target.Of(mine) }; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool casting = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (s.Game.State.Step == Step.BeginCombat && !casting && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == rite) is { } cast) { casting = true; return cast; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(casting);
        Assert.Contains(mine, seen);
        Assert.DoesNotContain(theirs, seen); // only a permanent you control gives the flash
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Statue"));
    }

    [Fact]
    public async Task ASorceryWithFlashByTargetsIsNotOfferedAtInstantSpeedWithoutAPermanentOfYours()
    {
        var s = new Scenario();
        // no permanent of yours (not even a land) to target

        s.Add(P1, Creature("Idol", 2, 2));
        var rite = s.InHand(P0, Make("Mirror Rite", "Sorcery", MirrorRite, "{0}"));
        bool offeredInCombat = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) => { offeredInCombat |= s.Game.State.Step == Step.BeginCombat && legal.OfType<CastSpell>().Any(c => c.Card == rite); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offeredInCombat);
    }

    // ------------------------------------------------------------------ the first ability that targets your creature costs less; creatures targeted by an activated ability draw a card

    [Fact]
    public async Task TheFirstAbilityTargetingYourCreatureCostsLessAndTheTargetingDrawsOnlyOncePerTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Lab Chief", "Creature — Human", """
            { "abilities": [
              { "abilityCost": { "firstTargetingYourCreatureOnYourTurn": true, "amount": 2 }, "text": "a" },
              { "trigger": "creaturesTargetedByActivatedAbility", "oncePerTurn": true, "effects": [{ "draw": 1 }], "text": "b" }] }
            """, "{1}", "1/1"));
        var hero = s.Add(P0, Creature("Hero", 1, 1));
        s.Add(P0, Make("Pump Rig", "Artifact", """{ "abilities": [{ "cost": "{2}", "targets": ["creature:you"], "effects": [{ "pump": [1, 1], "what": "target" }], "text": "c" }] }"""));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(hero) };
        int libraryBefore = -1, activations = 0, powerInCombat = -1;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (view, legal) =>
        {
            if (powerInCombat < 0 && s.Game.State.Step == Step.BeginCombat) powerInCombat = s.Card(hero).Power;
            if (libraryBefore < 0 && s.Game.State.Step == Step.PrecombatMain) libraryBefore = s.Game.State.GetPlayer(P0).Library.Count;
            if (s.Game.State.Step == Step.PrecombatMain && s.Game.State.Stack.Count == 0 && activations < 4
                && legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == "Pump Rig") is { } rig) { activations++; return rig; }
            return PassPriority.Instance;
        };
        int libraryAfter = -1;
        s.Game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) libraryAfter = s.Game.State.GetPlayer(P0).Library.Count; };
        await s.RunUntilTurn();
        Assert.Equal(2, activations); // 2 lands pay for the second ({2}); the first was free; no third
        Assert.Equal(1 + 2, powerInCombat);
        Assert.Equal(libraryBefore - 1, libraryAfter); // one card, however many abilities targeted the creature
    }

    // ------------------------------------------------------------------ become copy of target creature at beginning of combat keeping its name, both buffed and attack

    [Fact]
    public async Task MimicBecomesCopyOfTargetAtBeginningOfCombatAndBothGetBuffAndAttack()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Grizzly", 3, 3, Keyword.Trample));
        var mimic = s.Add(P0, Make("Mimic", "Creature — Human", """
            { "abilities": [{
              "trigger": "beginCombat", "may": true,
              "targets": [{ "kind": "creature", "controller": "you", "other": true }],
              "effects": [
                { "becomeCopy": "self", "target": "target", "exceptName": "Mimic" },
                { "pump": [2, 0], "what": "self", "keywords": ["Haste", "AttacksEachCombat"] },
                { "pump": [2, 0], "what": "target", "keywords": ["Haste", "AttacksEachCombat"] }
              ],
              "text": "At the beginning of combat..."
            }] }
            """, "{3}{R}", "2/2"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToArray();
        int mimicPower = -1;
        bool mimicTrample = false;
        string mimicName = "";
        int bearPower = -1;
        s.Attacker.Act = (view, legal) =>
        {
            if (s.Game.State.Step == Step.BeginCombat)
            {
                mimicPower = s.Card(mimic).Power;
                mimicTrample = s.Card(mimic).Has(Keyword.Trample);
                mimicName = s.Card(mimic).Name;
                bearPower = s.Card(bear).Power;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(3 + 2, mimicPower);
        Assert.True(mimicTrample);
        Assert.Equal("Mimic", mimicName);
        Assert.Equal(3 + 2, bearPower);
        // After turn ends at cleanup, copy effect ends and Mimic reverts to its base power 2/2
        Assert.Equal(2, s.Card(mimic).Power);
    }

    // ------------------------------------------------------------------ become a copy for good, keeping its name, vigilance and the ability

    [Fact]
    public async Task GuardianBecomesALastingCopyKeepingItsNameVigilanceAndThisAbility()
    {
        var s = new Scenario();
        var ogre = s.Add(P1, Creature("Ogre", 4, 5, Keyword.Trample));
        var guardian = s.Add(P0, Make("Guardian", "Legendary Creature — Cat Warrior", """
            { "abilities": [{
              "trigger": "beginCombat",
              "targets": ["creature:opponent"],
              "effects": [
                { "counters": 1, "what": "self" },
                { "tap": "target" },
                { "may": "Become a copy?", "effects": [
                  { "becomeCopy": "self", "target": "target", "exceptName": "Guardian", "lasting": true, "keywords": ["Vigilance"], "keepThisAbility": true }
                ] }
              ],
              "text": "At the beginning of combat on your turn..."
            }] }
            """, "{2}{G}{U}", "3/3", "Vigilance", "Vigilance"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var seen = new List<(int Power, bool Trample, bool Vigilance, string Name, bool OgreTapped)>();
        s.Attacker.Act = (_, _) =>
        {
            if (s.Game.State.Step == Step.BeginCombat && s.Game.State.ActivePlayer == P0 && s.Game.State.Stack.Count == 0 && seen.Count < s.Game.State.TurnNumber / 2 + 1)
            {
                var g = s.Card(guardian);
                seen.Add((g.Power, g.Has(Keyword.Trample), g.Has(Keyword.Vigilance), g.Name, s.Card(ogre).Tapped));
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(4);
        Assert.Equal(2, seen.Count);
        Assert.Equal((4 + 1, true, true, "Guardian", true), seen[0]);
        // Still a copy on its next turn, and the kept ability triggered again (one instance: the ogre doesn't have it).
        Assert.Equal((4 + 2, true, true, "Guardian", true), seen[1]);
        Assert.Single(s.Card(guardian).Definition.Abilities);
    }

    // ------------------------------------------------------------------ exile face down, then play the cards exiled with it this turn for {2} less

    [Fact]
    public async Task SeerPlaysCardsItExiledFaceDownForTwoLessAndGainsLifeForEach()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var seer = s.Add(P0, Make("Seer", "Legendary Creature — Elf Advisor", """
            { "abilities": [
              { "trigger": "youPlayLand", "filter": { "fromExile": true }, "effects": [{ "gainLife": 2 }], "text": "Whenever you play a land from exile..." },
              { "trigger": "castSpell", "filter": { "fromExile": true }, "effects": [{ "gainLife": 2 }], "text": "...or cast a spell from exile, you gain 2 life." },
              { "cost": "{T}", "effects": [{ "exileTopFaceDown": "you", "optional": true }], "text": "{T}: Look at the top card..." },
              { "cost": "{T}", "effects": [{ "playExiledWithThisThisTurn": true, "costLess": 2 }], "text": "{T}: Until end of turn, you may play cards exiled with..." }
            ] }
            """, "{W}{U}", "1/3"));
        var ogre = s.InHand(P0, Make("Ogre", "Creature — Ogre", "{}", "{4}", "4/4"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool restacked = false;
        var activatedOn = new HashSet<int>();
        CardId? exiledLand = null;
        s.Attacker.Act = (_, legal) =>
        {
            var state = s.Game.State;
            if (!restacked) { restacked = true; s.Restack(P0, ogre); }
            if (state.ActivePlayer != P0 || !state.Step.IsMain()) return PassPriority.Instance;
            var mine = legal.OfType<ActivateAbility>().Where(a => a.Source == seer).OrderBy(a => a.Index).ToList();
            if (state.TurnNumber < 5 && mine.Count > 0 && activatedOn.Add(state.TurnNumber)) return mine[0];
            if (state.TurnNumber == 5)
            {
                if (mine.Count > 0 && activatedOn.Add(5)) return mine[^1];
                if (legal.OfType<PlayLand>().FirstOrDefault(l => s.Card(l.Card).Zone == Zone.Exile) is { } fromExile) { exiledLand = fromExile.Card; return fromExile; }
                if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == ogre) is { } cast) return cast;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(6);
        Assert.Equal(Zone.Battlefield, s.Card(ogre).Zone); // {4} less {2}: the mountain and the land played from exile
        Assert.NotNull(exiledLand);
        Assert.Equal(Zone.Battlefield, s.Card(exiledLand!.Value).Zone);
        Assert.Equal(20 + 2 + 2, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ exile from each graveyard, later cast one of them with mana of any type

    [Fact]
    public async Task SummonExilesFromEachGraveyardThenCastsOneWithItsRedManaOfAnyType()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        s.InHand(P0, Make("Drake Summon", "Enchantment Creature — Saga Drake", """
            { "abilities": [
              { "trigger": "chapter", "chapters": [1], "effects": [{ "exileOneFromEachGraveyard": { "types": ["instant", "sorcery"] } }] },
              { "trigger": "chapter", "chapters": [2, 3, 4], "effects": [
                { "addMana": "{R}", "times": { "counters": "lore" } },
                { "castExiledNow": "exiledWithThis", "oneOf": { "types": ["instant", "sorcery"] }, "anyMana": true }
              ] }
            ] }
            """, "{3}{R}", "3/3"));
        var insight = s.InHand(P1, Make("Insight", "Instant", """{ "spell": { "effects": [{ "gainLife": 7 }] } }""", "{1}{U}"));
        bool moved = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).Take(request.Max).ToList();
        var greedy = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            if (!moved)
            {
                moved = true;
                var defender = s.Game.State.GetPlayer(P1);
                defender.Hand.Remove(insight);
                s.Card(insight).Zone = Zone.Graveyard;
                defender.Graveyard.Add(insight);
            }
            return greedy(view, legal);
        };
        await s.RunUntilTurn(4);
        Assert.Equal(20 + 7, s.Game.State.GetPlayer(P0).Life); // {1}{U} paid with {R}{R}
        Assert.Equal(Zone.Graveyard, s.Card(insight).Zone);
        Assert.Contains(insight, s.Game.State.GetPlayer(P1).Graveyard);
    }

    // ------------------------------------------------------------------ chapter: "whenever you cast a noncreature spell this turn, copy a non-Saga token"

    [Fact]
    public async Task KingSummonCopiesANonSagaTokenForEachNoncreatureSpellThatTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 5);
        var saga = s.InHand(P0, Make("King Summon", "Enchantment Creature — Saga Moogle", """
            { "abilities": [
              { "trigger": "chapter", "chapters": [1], "effects": [{ "tokens": 2, "token": { "name": "Moogle", "types": "Creature — Moogle", "power": 1, "toughness": 2, "colors": ["W"], "keywords": ["Lifelink"] } }] },
              { "trigger": "chapter", "chapters": [2, 3], "effects": [{ "emblem": "King Summon", "untilEndOfTurn": true, "abilities": [
                { "trigger": "castSpell", "filter": { "not": ["creature"] }, "effects": [
                  { "choose": { "token": true, "notSubtype": "Saga", "controller": "you" } }, { "copy": "chosen" }
                ], "text": "Whenever you cast a noncreature spell this turn..." }] }] },
              { "trigger": "chapter", "chapters": [4], "effects": [{ "counters": 2, "what": { "each": { "subtype": "Moogle", "controller": "you", "other": true } } }] }
            ] }
            """, "{5}", "4/4"));
        var spark = s.InHand(P0, Make("Spark", "Instant", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            var state = s.Game.State;
            if (state.ActivePlayer != P0 || !state.Step.IsMain() || state.Stack.Count > 0) return PassPriority.Instance;
            if (state.TurnNumber == 1 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == saga) is { } castSaga) return castSaga;
            if (state.TurnNumber == 3 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spark) is { } castSpark) return castSpark;
            return PassPriority.Instance;
        };
        int Moogles() => s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Moogle" && s.Card(id).Definition.IsToken);
        await s.RunUntilTurn(2);
        Assert.Equal(2, Moogles());
        await s.RunUntilTurn(4);
        Assert.Equal(3, Moogles()); // one copy for the noncreature spell cast that turn
    }

    // ------------------------------------------------------------------ warp-strike: back tapped and attacking at the next declare attackers step, unblockable

    [Fact]
    public async Task WarpStrikeReturnsAttackingUnblockableAndItsEnteringDuringCombatAttachesEquipment()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var blade = s.Add(P0, Make("Blade", "Artifact — Equipment", "{}", "{1}"));
        var prince = s.Add(P0, Make("Prince", "Legendary Creature — Human Noble", """
            { "abilities": [
              { "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "you" }, "when": "duringCombat",
                "targets": [{ "kind": "artifact", "controller": "you", "filter": { "subtype": "Equipment" } }, "creature:you"],
                "effects": [{ "may": "Attach?", "effects": [{ "attach": "target", "to": "target2" }] }], "text": "Whenever a creature you control enters during combat..." },
              { "cost": "{3}", "effects": [{ "warpStrike": "self" }], "text": "Warp-strike {3}" }
            ] }
            """, "{W}{U}{B}", "2/3"));
        s.Add(P1, Creature("Wall", 0, 5));
        bool warped = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!warped && s.Game.State.Step == Step.PrecombatMain && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == prince) is { } warp)
            {
                warped = true;
                return warp;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.True(warped);
        Assert.Equal(Zone.Battlefield, s.Card(prince).Zone);
        Assert.True(s.Card(prince).Tapped);
        Assert.Equal(20 - 2, s.Game.State.GetPlayer(P1).Life); // the blockers and damage steps happened though nothing was declared
        Assert.Equal(prince, s.Card(blade).AttachedTo);
        // It couldn't be blocked: the defender, with an untapped creature, wasn't offered a block on it (or any block at all).
        var request = s.Defender.LastBlockRequest;
        Assert.True(request is null || !request.CanBlock.Any(pair => pair.Key == prince || pair.Value.Contains(prince)));
    }

    // ------------------------------------------------------------------ "when this dies, you may cast it from your graveyard as an Adventure until the end of your next turn"

    private static (Scenario S, CardId Gentleman) DyingGentleman()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var rise = Make("Rise", "Instant — Adventure", """{ "spell": { "effects": [{ "tokens": 1, "token": { "name": "Zombie", "types": "Creature — Zombie", "colors": ["B"], "power": 2, "toughness": 2 } }] } }""", "{2}");
        var gentleman = s.Add(P0, Make("Gentleman", "Legendary Creature — Human Detective", """
            { "abilities": [
              { "static": { "affects": "creatures:you", "filter": { "token": true }, "pump": [1, 1] }, "text": "Creature tokens you control get +1/+1." },
              { "trigger": "dies", "effects": [{ "adventureFromGraveyardUntilYourNextTurn": "self" }], "text": "When this dies, you may cast it from your graveyard as an Adventure..." }
            ] }
            """, "{1}{W}", "2/2") with { Adventure = rise });
        s.InHand(P0, Make("Doom", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{0}"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return (s, gentleman);
    }

    [Fact]
    public async Task ADeadAdventurerIsCastableOnlyAsItsAdventureUntilTheEndOfItsOwnersNextTurn()
    {
        var (s, gentleman) = DyingGentleman();
        var offered = new Dictionary<int, (bool Adventure, bool Card)>();
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Doom") is { } doom) return doom;
            if (s.Game.State.ActivePlayer == P0 && s.Game.State.Step == Step.PostcombatMain)
                offered[s.Game.State.TurnNumber] = (legal.Contains(new CastSpell(gentleman, Adventure: true)), legal.Contains(new CastSpell(gentleman)));
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(6);
        Assert.Equal(Zone.Graveyard, s.Card(gentleman).Zone);
        Assert.Equal((true, false), offered[1]);
        Assert.Equal((true, false), offered[3]); // through the end of its owner's next turn
        Assert.Equal((false, false), offered[5]);
    }

    [Fact]
    public async Task ADeadAdventurerCastAsItsAdventureGoesOnAnAdventure()
    {
        var (s, gentleman) = DyingGentleman();
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Doom") is { } doom) return doom;
            if (legal.Contains(new CastSpell(gentleman, Adventure: true))) return new CastSpell(gentleman, Adventure: true);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Exile, s.Card(gentleman).Zone);
        Assert.True(s.Card(gentleman).OnAdventure);
        Assert.Single(s.Game.State.Battlefield, id => s.Card(id).Name == "Zombie");
    }

    [Fact]
    public async Task ACreaturePutOntoTheBattlefieldAttackingCanAttackAnOpponentsPlaneswalker()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var walker = s.Add(P1, new CardDefinition { Name = "Sage", ManaCost = ManaCost.Parse("{2}"), Types = CardType.Planeswalker, Loyalty = 5 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        var prince = s.Add(P0, Make("Prince", "Creature — Human Noble", """
            { "abilities": [{ "cost": "{3}", "effects": [{ "warpStrike": "self" }], "text": "Warp-strike {3}" }] }
            """, "{W}{U}{B}", "2/3"));
        bool warped = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!warped && s.Game.State.Step == Step.PrecombatMain && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == prince) is { } warp)
            {
                warped = true;
                return warp;
            }
            return PassPriority.Instance;
        };
        IReadOnlyList<string>? choices = null;
        s.Attacker.Option = (_, request) => { choices = request.Options; return request.Options.ToList().FindIndex(o => o.StartsWith("Sage")); };
        await s.RunUntilTurn(2);
        Assert.Equal(2, choices!.Count); // the opponent or their planeswalker
        Assert.Equal(5 - 2, s.Card(walker).CounterCount(CounterKind.Loyalty));
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ tiered: castable only if a mode with legal targets can be paid for

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ATieredSpellIsOfferedOnlyWhenAModeWithLegalTargetsCanBePaid(bool tappedCreature, bool offered)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var limit = s.InHand(P0, Make("Limit", "Instant", """
            { "spell": { "modes": [
              { "text": "{0}: destroy target tapped creature", "cost": "{0}", "targets": [{ "kind": "creature", "filter": { "tapped": true } }], "effects": [{ "destroy": "target" }] },
              { "text": "{1}: destroy any number of tapped creatures", "cost": "{1}", "targets": [{ "kind": "creature", "filter": { "tapped": true }, "anyNumber": true }], "effects": [{ "destroy": "eachTarget" }] },
              { "text": "{4}: destroy all tapped creatures", "cost": "{4}", "effects": [{ "destroy": { "each": { "types": ["creature"], "tapped": true } } }] }
            ] } }
            """, "{2}", oracle: "Tiered", keywords: "Tiered"));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        if (tappedCreature) s.Card(bear).Tapped = true;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool? seen = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Game.State.Step == Step.PrecombatMain && seen is null) seen = legal.Contains(new CastSpell(limit));
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(offered, seen);
        Assert.Empty(s.Game.FailedActions);
    }
}
