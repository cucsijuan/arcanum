// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// The academy bonus sheet's vocabulary: spree, Role tokens, infect, a prevention shield that strikes back at once, a life floor,
/// uncounterable spells and hexproof from colors this turn, basic lands for exiled creatures, a pitch alternative cost, discards put on
/// top of the library, a ban on permanent spells, a delayed "when you next cast a spell" exile, extra draws paid for, and a repeated
/// reveal. The cards are invented.
/// </summary>
public class BonusSheetTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"bonus-sheet-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, script.Length == 0 ? null : CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    /// <summary>Player 0 takes the first legal action <paramref name="pick"/> returns, then passes; no attacks.</summary>
    private static void Play(Scenario s, Func<IReadOnlyList<PlayerAction>, PlayerAction?> pick)
    {
        s.Attacker.Act = (_, legal) => pick(legal) ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Func<IReadOnlyList<PlayerAction>, PlayerAction?> CastOnce(params CardId[] cards)
    {
        var cast = new HashSet<CardId>();
        return legal =>
        {
            foreach (var card in cards)
                if (!cast.Contains(card) && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } a) { cast.Add(card); return a; }
            return null;
        };
    }

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    // ------------------------------------------------------------------ spree

    private static CardDefinition TideRaid() => Make("Tide Raid", "Sorcery",
        """{ "spell": { "text": "Spree", "chooseCount": 2, "upTo": true, "modes": [ { "cost": "{1}", "text": "Each opponent loses 2 life.", "effects": [{ "loseLife": 2, "who": "opponents" }] }, { "cost": "{1}", "text": "You gain 3 life.", "effects": [{ "gainLife": 3 }] } ] } }""",
        "{R}", null, "Spree\n+ {1} — Each opponent loses 2 life.\n+ {1} — You gain 3 life.", "Spree");

    [Theory]
    [InlineData(3, 18, 23)] // both modes: {R} + {1} + {1}
    [InlineData(2, 18, 20)] // only one mode can be paid for
    public async Task SpreeAddsTheCostOfEachChosenMode(int lands, int opponentLife, int life)
    {
        var s = new Scenario();
        s.Lands(P0, lands);
        var raid = s.InHand(P0, TideRaid());
        s.Attacker.Modes = (_, request) => request.Possible.Take(1).ToList(); // keeps adding modes while one can be paid for
        Play(s, CastOnce(raid));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(raid).Zone);
        Assert.Equal(opponentLife, Life(s, P1));
        Assert.Equal(life, Life(s, P0));
    }

    // ------------------------------------------------------------------ Role tokens

    [Fact]
    public async Task OnlyTheNewestRoleAPlayerControlsStaysOnAPermanent()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var pup = s.Add(P0, Creature("Pup", 1, 1));
        const string script = """{ "spell": { "targets": ["creature"], "effects": [{ "tokens": 1, "token": { "name": "Brute Role", "types": "Enchantment — Aura Role", "colors": [], "aura": "creature", "text": "Enchant creature\nEnchanted creature gets +1/+1 and has trample.", "abilities": [{ "static": { "affects": "enchanted", "pump": [1, 1], "keywords": ["Trample"] }, "text": "Enchanted creature gets +1/+1 and has trample." }] }, "attachTo": "target" }] } }""";
        var first = s.InHand(P0, Make("Role Call", "Sorcery", script, "{R}", null, "Create a Brute Role token attached to target creature."));
        var second = s.InHand(P0, Make("Role Call", "Sorcery", script, "{R}", null, "Create a Brute Role token attached to target creature."));
        Play(s, CastOnce(first, second));
        await s.RunUntilTurn();
        Assert.Single(s.Game.State.Battlefield, id => s.Card(id).Name == "Brute Role");
        Assert.Equal(2, s.Card(pup).Power);
    }

    // ------------------------------------------------------------------ infect

    [Fact]
    public async Task InfectDamageGivesPlayersPoisonCountersAndCreaturesMinusCounters()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Blight Rat", 2, 2, Keyword.Infect));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.GetPlayer(P1).Poison);
        Assert.Equal(20, Life(s, P1));

        var t = new Scenario();
        t.Lands(P0, 1);
        var rat = t.Add(P0, Creature("Blight Rat", 2, 2, Keyword.Infect));
        var wall = t.Add(P1, Creature("Brick Barrier", 0, 4));
        var fight = t.InHand(P0, Make("Clash Note", "Sorcery", """{ "spell": { "targets": ["creature:you", "creature:opponent"], "effects": [{ "fight": "target", "with": "target2" }] } }""",
            "{R}", null, "Target creature you control fights target creature you don't control."));
        Play(t, CastOnce(fight));
        await t.RunUntilTurn();
        Assert.Equal(2, t.Card(wall).CounterCount(Abilities.CounterKind.MinusOneMinusOne));
        Assert.Equal(0, t.Card(wall).Damage);
        Assert.Equal(Zone.Battlefield, t.Card(rat).Zone);
    }

    // ------------------------------------------------------------------ prevention that strikes back at once

    [Fact]
    public async Task APreventionShieldThatStrikesBackDealsTheDamageToTheSourcesController()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var totem = s.Add(P1, Make("Spark Totem", "Artifact", """{ "abilities": [{ "cost": "{T}", "effects": [{ "damage": 3, "to": "opponents" }], "text": "{T}: This artifact deals 3 damage to each opponent." }] }""",
            "{2}", null, "{T}: This artifact deals 3 damage to each opponent."));
        var palm = s.InHand(P0, Make("Mirror Palm", "Instant", """{ "spell": { "effects": [{ "preventNextFromChosenSource": { "immediately": true, "effects": [{ "damage": "triggerAmount", "to": "triggeredPlayer" }], "text": "If damage is prevented this way, Mirror Palm deals that much damage to that source's controller." } }] } }""",
            "{R}", null, "The next time a source of your choice would deal damage to you this turn, prevent that damage. If damage is prevented this way, Mirror Palm deals that much damage to that source's controller."));
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == totem).Select(o => o.Id).Take(1).ToList();
        Play(s, CastOnce(palm));
        bool used = false;
        s.Defender.Act = (_, legal) =>
        {
            if (!used && s.Card(palm).Zone == Zone.Graveyard && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == totem) is { } a) { used = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(used);
        Assert.Equal(20, Life(s, P0));
        Assert.Equal(17, Life(s, P1));
    }

    [Theory]
    [InlineData("Mirror Palm", 17)] // the shield first: all 3 is prevented this way and dealt back
    [InlineData("Prevent 1", 18)] // 1 prevented first: the shield prevents the other 2 and deals 2 back
    public async Task TheAffectedPlayerOrdersAPartialPreventionAndAShieldThatStrikesBack(string first, int life)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Warden", 3, 4) with { Replaces = Replacements.PreventOneDamageFromOpponentsSources });
        var totem = s.Add(P1, Make("Spark Totem", "Artifact", """{ "abilities": [{ "cost": "{T}", "effects": [{ "damage": 3, "to": "opponents" }], "text": "{T}: This artifact deals 3 damage to each opponent." }] }""",
            "{2}", null, "{T}: This artifact deals 3 damage to each opponent."));
        var palm = s.InHand(P0, Make("Mirror Palm", "Instant", """{ "spell": { "effects": [{ "preventNextFromChosenSource": { "immediately": true, "effects": [{ "damage": "triggerAmount", "to": "triggeredPlayer" }], "text": "If damage is prevented this way, Mirror Palm deals that much damage to that source's controller." } }] } }""",
            "{R}", null, "The next time a source of your choice would deal damage to you this turn, prevent that damage. If damage is prevented this way, Mirror Palm deals that much damage to that source's controller."));
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == totem).Select(o => o.Id).Take(1).ToList();
        var asked = new List<OptionRequest>();
        s.Attacker.Option = (_, request) => { asked.Add(request); return request.Options.ToList().FindIndex(o => o.Contains(first)); };
        Play(s, CastOnce(palm));
        bool used = false;
        s.Defender.Act = (_, legal) =>
        {
            if (!used && s.Card(palm).Zone == Zone.Graveyard && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == totem) is { } a) { used = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        var question = Assert.Single(asked);
        Assert.EndsWith("which effect applies next?", question.Prompt);
        Assert.Equal(2, question.Options.Count);
        Assert.Equal(20, Life(s, P0));
        Assert.Equal(life, Life(s, P1));
    }

    // ------------------------------------------------------------------ a life floor

    [Fact]
    public async Task DamageCantReduceLifeBelowOneThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Game.State.GetPlayer(P0).Life = 3;
        var grace = s.InHand(P0, Make("Last Grace", "Instant", """{ "spell": { "effects": [{ "cantLoseGameThisTurn": true }, { "damageCantReduceLifeBelowOneThisTurn": true }] } }""",
            "{R}", null, "You can't lose the game this turn and your opponents can't win the game this turn. Until end of turn, damage that would reduce your life total to less than 1 reduces it to 1 instead."));
        var backfire = s.InHand(P0, Make("Self Scorch", "Sorcery", """{ "spell": { "effects": [{ "damage": 10, "to": "you" }] } }""", "{R}", null,
            "Self Scorch deals 10 damage to you."));
        Play(s, CastOnce(grace, backfire));
        await s.RunUntilTurn();
        Assert.Equal(1, Life(s, P0));
        Assert.False(s.Game.State.GetPlayer(P0).HasLost);
    }

    // ------------------------------------------------------------------ spells that can't be countered this turn

    [Fact]
    public async Task SpellsYouControlCantBeCounteredThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var veil = s.InHand(P0, Make("Summer Veil", "Instant", """{ "spell": { "effects": [{ "yourSpellsCantBeCounteredThisTurn": true }, { "hexproofFromColorsThisTurn": ["U", "B"] }] } }""",
            "{R}", null, "Spells you control can't be countered this turn. You and permanents you control gain hexproof from blue and from black until end of turn."));
        var pup = s.InHand(P0, Make("Pup", "Creature — Dog", "", "{R}", "1/1"));
        var negate = s.InHand(P0, Make("No Note", "Instant", """{ "spell": { "targets": ["spell"], "effects": [{ "counter": "target" }] } }""", "{R}", null, "Counter target spell."));
        Play(s, CastOnce(veil, pup, negate));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(pup).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(negate).Zone);
    }

    [Fact]
    public async Task HexproofFromBlueStopsAnOpponentsBlueSpell()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var veil = s.InHand(P0, Make("Summer Veil", "Instant", """{ "spell": { "effects": [{ "hexproofFromColorsThisTurn": ["U", "B"] }] } }""",
            "{R}", null, "You and permanents you control gain hexproof from blue and from black until end of turn."));
        var pup = s.Add(P0, Creature("Pup", 1, 1));
        for (int i = 0; i < 2; i++) s.Add(P1, GenericCards.Island);
        var ping = s.InHand(P1, Make("Frost Ping", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{U}", null, "Destroy target creature."));
        Play(s, CastOnce(veil));
        s.Defender.Act = (_, legal) => s.Card(veil).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == ping) is { } a ? a : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(pup).Zone);
        Assert.Equal(Zone.Hand, s.Card(ping).Zone);
    }

    // ------------------------------------------------------------------ a basic land for each exiled creature

    [Fact]
    public async Task EachExiledCreaturesControllerSearchesForABasicLandTapped()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Burly Cub", 2, 2));
        var forest = s.Game.SetupInLibrary(P1, GenericCards.Forest);
        var winds = s.InHand(P0, Make("Exile Gust", "Sorcery", """{ "spell": { "targets": ["creature:opponent"], "effects": [{ "exile": "target" }, { "controllersSearchBasicPerExiled": true }] } }""",
            "{R}", null, "Exile target creature you don't control. For each creature exiled this way, its controller searches their library for a basic land card. Those players put those cards onto the battlefield tapped, then shuffle."));
        s.Defender.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        bool stacked = false;
        var cast = CastOnce(winds);
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P1, forest); return PassPriority.Instance; }
            return cast(legal) ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool tappedAtEndOfTurn = false;
        s.Game.EventRaised += e => { if (e is Events.TurnBegan { TurnNumber: 2 }) tappedAtEndOfTurn = s.Card(forest).Tapped; }; // before its controller untaps
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(forest).Zone);
        Assert.Equal(P1, s.Card(forest).Controller);
        Assert.True(tappedAtEndOfTurn);
    }

    // ------------------------------------------------------------------ a pitch alternative cost

    [Fact]
    public async Task APitchCostExilesABlueCardAndPaysOneLife()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var bear = s.InHand(P0, Make("Burly Cub", "Creature — Bear", "", "{2}", "2/2"));
        var will = s.InHand(P0, Make("Pitch Will", "Instant", """{ "alternativeCost": { "cost": "{0}", "extra": { "life": 1, "exileFromHand": { "colors": ["U"] } } }, "spell": { "targets": ["spell"], "effects": [{ "counter": "target" }] } }""",
            "{3}{U}{U}", null, "You may pay 1 life and exile a blue card from your hand rather than pay this spell's mana cost.\nCounter target spell."));
        var blue = s.InHand(P0, Make("Blue Note", "Instant", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{U}", null, "Draw a card."));
        Play(s, CastOnce(bear, will));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(will).Zone);
        Assert.Equal(Zone.Exile, s.Card(blue).Zone);
        Assert.Equal(19, Life(s, P0));
    }

    // ------------------------------------------------------------------ discards put on top of the library

    [Fact]
    public async Task ADiscardAnEffectCausesMayGoOnTopOfTheLibrary()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Leng Shelf", "Artifact", """{ "replaces": ["NoMaximumHandSize"], "rules": ["EffectDiscardsMayGoOnTop"] }""", "{1}", null,
            "You have no maximum hand size.\nIf an effect causes you to discard a card, discard it, but you may put it on top of your library instead of into your graveyard."));
        var rot = s.InHand(P0, Make("Mind Itch", "Sorcery", """{ "spell": { "effects": [{ "discard": 1, "who": "you" }] } }""", "{R}", null, "Discard a card."));
        var kept = s.InHand(P0, Make("Keep Note", "Instant", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{U}", null, "Draw a card."));
        s.Attacker.Discard = (_, _) => new[] { kept };
        Play(s, CastOnce(rot));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(kept).Zone);
        Assert.Equal(kept, s.Game.State.GetPlayer(P0).Library[0]);
    }

    // ------------------------------------------------------------------ no permanent spells

    [Fact]
    public async Task YouCantCastPermanentSpells()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Codex Lock", "Artifact", """{ "rules": ["YouCantCastPermanentSpells"] }""", "{3}", null, "You can't cast permanent spells."));
        var bear = s.InHand(P0, Make("Burly Cub", "Creature — Bear", "", "{1}", "2/2"));
        var note = s.InHand(P0, Make("Blue Note", "Instant", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}", null, "You gain 1 life."));
        Play(s, CastOnce(bear, note));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(bear).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(note).Zone);
    }

    // ------------------------------------------------------------------ "when you next cast a spell this turn"

    [Fact]
    public async Task WhenYouNextCastASpellExileUntilALesserInstantCastableFreeThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var codex = s.InHand(P0, Make("Codex Note", "Sorcery", """{ "spell": { "effects": [{ "whenYouNextCastThisTurn": { "effects": [{ "exileUntilLesserCastFree": { "types": ["instant", "sorcery"] }, "untilEndOfTurn": true }], "text": "When you next cast a spell this turn, exile cards from the top of your library until you exile an instant or sorcery card with lesser mana value. Until end of turn, you may cast that card without paying its mana cost. Put each other card exiled this way on the bottom of your library in a random order." } }] } }""",
            "{R}", null, "When you next cast a spell this turn, exile cards from the top of your library until you exile an instant or sorcery card with lesser mana value. Until end of turn, you may cast that card without paying its mana cost. Put each other card exiled this way on the bottom of your library in a random order."));
        var big = s.InHand(P0, Make("Big Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}{R}", null, "You gain 1 life."));
        var land = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var trick = s.Game.SetupInLibrary(P0, Make("Cheap Trick", "Instant", """{ "spell": { "effects": [{ "loseLife": 2, "who": "opponents" }] } }""", "{1}{R}", null, "Each opponent loses 2 life."));
        bool stacked = false;
        var cast = CastOnce(codex, big);
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, land, trick); return PassPriority.Instance; }
            if (s.Card(trick).Zone == Zone.Exile && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == trick) is { } free) return free;
            return cast(legal) ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(trick).Zone); // cast free from exile
        Assert.Equal(18, Life(s, P1));
        var library = s.Game.State.GetPlayer(P0).Library;
        Assert.Equal(land, library[^1]); // the land went to the bottom
    }

    // ------------------------------------------------------------------ extra draws paid for

    [Theory]
    [InlineData(0, 12)] // pay 4 life for each of the two chosen cards
    [InlineData(1, 20)] // put both back on top
    public async Task DrawTwoMoreThenPayOrPutBackTwoCardsDrawnThisTurn(int option, int life)
    {
        var s = new Scenario();
        s.Add(P0, Make("Grove Shelf", "Enchantment", """{ "abilities": [{ "trigger": "eachDrawStep", "when": "triggeredPlayerIsYou", "effects": [{ "drawTwoPayOrReturnDrawn": true }] }] }""", "{1}{G}", null,
            "At the beginning of your draw step, you may draw two additional cards. If you do, choose two cards in your hand drawn this turn. For each of those cards, pay 4 life or put the card on top of your library."));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Option = (_, _) => option;
        int libraryBefore = 0;
        s.Game.EventRaised += e => { if (e is Events.TurnBegan { TurnNumber: 3 }) libraryBefore = s.Game.State.GetPlayer(P0).Library.Count; };
        await s.RunUntilTurn(4);
        Assert.Equal(life, Life(s, P0));
        Assert.Equal(option == 0 ? libraryBefore - 3 : libraryBefore - 1, s.Game.State.GetPlayer(P0).Library.Count);
    }

    // ------------------------------------------------------------------ a repeated reveal

    [Fact]
    public async Task RevealToHandAndLoseLifeRepeatsWhileThePlayerWants()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var nausea = s.InHand(P0, Make("Sick Note", "Instant", """{ "spell": { "effects": [{ "revealToHandLoseLifeRepeat": true }] } }""", "{R}", null,
            "Reveal the top card of your library and put that card into your hand. You lose life equal to its mana value. You may repeat this process any number of times."));
        var three = s.Game.SetupInLibrary(P0, Make("Three", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}{R}", null, "You gain 1 life."));
        var two = s.Game.SetupInLibrary(P0, Make("Two", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{1}{R}", null, "You gain 1 life."));
        var four = s.Game.SetupInLibrary(P0, Make("Four", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{3}{R}", null, "You gain 1 life."));
        bool stacked = false;
        var cast = CastOnce(nausea);
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, three, two, four); return PassPriority.Instance; }
            return cast(legal) ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        int asked = 0;
        s.Attacker.YesNo = (_, request) => !request.Prompt.Contains("reveal the next card") || ++asked == 1; // one more, then stop
        s.Attacker.Discard = (view, count) => view.Self.Hand.Select(c => c.Id).Where(id => id != three && id != two).Take(count).ToList(); // hand size
        await s.RunUntilTurn();
        Assert.Equal(15, Life(s, P0));
        Assert.Equal(Zone.Hand, s.Card(three).Zone);
        Assert.Equal(Zone.Hand, s.Card(two).Zone);
        Assert.Equal(Zone.Library, s.Card(four).Zone);
    }

    // ------------------------------------------------------------------ warp

    private static CardDefinition WarpScout() => Make("Warp Scout", "Creature — Scout", """{ "warp": "{R}" }""", "{2}{R}", "2/2",
        "Warp {R} (You may cast this card from your hand for its warp cost. Exile this creature at the beginning of the next end step, then you may cast it from exile on a later turn.)", "Warp");

    [Fact]
    public async Task AWarpedCreatureIsExiledAtTheEndStepAndCastableFromExileOnALaterTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var scout = s.InHand(P0, WarpScout());
        s.Attacker.YesNo = (_, request) => request.Prompt.Contains("warp cost") || !request.Prompt.Contains("warp");
        var casts = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == scout) is { } a) { casts++; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool exiledAtTurnTwo = false;
        s.Game.EventRaised += e => { if (e is Events.TurnBegan { TurnNumber: 2 }) exiledAtTurnTwo = s.Card(scout).Zone == Zone.Exile; };
        await s.RunUntilTurn(4);
        Assert.True(exiledAtTurnTwo); // warped on turn 1, exiled at its end step, not cast again that turn
        Assert.Equal(2, casts); // cast from exile on turn 3
        Assert.Equal(Zone.Battlefield, s.Card(scout).Zone); // cast from exile for its mana cost, so it stays
    }

    // ------------------------------------------------------------------ a Case

    [Fact]
    public async Task ACaseIsSolvedAtTheEndStepAndItsSolvedAbilityWorksOnlyThen()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Game.State.GetPlayer(P0).Life = 20;
        var caseCard = s.Add(P0, Make("Case of the Lost Lunch", "Enchantment — Case", """{ "abilities": [{ "trigger": "endStep", "if": { "all": [{ "not": "solved" }, { "gainedLife": 5 }] }, "effects": [{ "solve": true }], "text": "To solve — You've gained 5 or more life this turn. (If unsolved, solve at the beginning of your end step.)" }, { "cost": "{T}", "activateIf": "solved", "effects": [{ "draw": 1 }], "text": "Solved — {T}: Draw a card." }] }""",
            "{W}", null, "To solve — You've gained 5 or more life this turn. (If unsolved, solve at the beginning of your end step.)\nSolved — {T}: Draw a card.", "Solved"));
        var feast = s.InHand(P0, Make("Big Feast", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 5 }] } }""", "{R}", null, "You gain 5 life."));
        bool canActivateBefore = true;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(feast).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == feast) is { } cast)
            {
                canActivateBefore = legal.OfType<ActivateAbility>().Any(a => a.Source == caseCard);
                return cast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.False(canActivateBefore);
        Assert.True(s.Card(caseCard).Solved);
    }

    // ------------------------------------------------------------------ exile from a hand until this leaves

    [Fact]
    public async Task ACardExiledFromAHandUntilThisLeavesReturnsToThatHand()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var bat = s.InHand(P0, Make("Cave Flapper", "Creature — Bat", """{ "abilities": [{ "trigger": "enters", "targets": ["opponent"], "effects": [{ "exileFromHandUntilLeaves": { "not": ["land"] } }] }] }""",
            "{1}", "1/1", "When this creature enters, look at target opponent's hand. You may exile a nonland card from it until this creature leaves the battlefield."));
        var loot = s.InHand(P1, Make("Loot Note", "Sorcery", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{U}", null, "Draw a card."));
        var snap = s.InHand(P0, Make("Snap Note", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}", null, "Destroy target creature."));
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == loot).Select(o => o.Id).Take(1).ToList();
        bool exiledMeanwhile = false;
        var cast = CastOnce(bat, snap);
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(bat).Zone == Zone.Battlefield && s.Card(loot).Zone == Zone.Exile) exiledMeanwhile = true;
            return cast(legal) ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(exiledMeanwhile);
        Assert.Equal(Zone.Graveyard, s.Card(bat).Zone);
        Assert.Equal(Zone.Hand, s.Card(loot).Zone);
    }

    // ------------------------------------------------------------------ X targets of an enters trigger

    [Fact]
    public async Task AnEntersTriggerTargetsXCreaturesWithTheXPaidAndStunsOnlyTheOpponents()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var mine = s.Add(P0, Creature("Mine", 1, 1));
        var theirs = s.Add(P1, Creature("Theirs", 1, 1));
        var maze = s.InHand(P0, Make("Mazework Snare", "Enchantment", """{ "abilities": [{ "trigger": "enters", "targets": [{ "kind": "creature", "countOf": "X", "text": "X target creatures" }], "effects": [{ "tap": "eachTarget" }, { "counters": 1, "kind": "stun", "what": { "eachTarget": { "controller": "opponent" } } }] }] }""",
            "{X}{R}{R}", null, "When this enchantment enters, tap X target creatures. Put a stun counter on each of those creatures you don't control."));
        s.Attacker.Number = (_, _) => 2;
        Play(s, CastOnce(maze));
        var seen = (MineTapped: false, TheirsTapped: false, MineStun: -1, TheirsStun: -1);
        s.Game.EventRaised += e =>
        {
            if (e is Events.TurnBegan { TurnNumber: 2 }) // before the opponent untaps (a stun counter is removed instead)
                seen = (s.Card(mine).Tapped, s.Card(theirs).Tapped, s.Card(mine).CounterCount(Abilities.CounterKind.Stun), s.Card(theirs).CounterCount(Abilities.CounterKind.Stun));
        };
        await s.RunUntilTurn();
        Assert.True(seen.MineTapped);
        Assert.True(seen.TheirsTapped);
        Assert.Equal(0, seen.MineStun);
        Assert.Equal(1, seen.TheirsStun);
    }
}
