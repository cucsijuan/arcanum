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
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Magic Origins cards whose rules needed new effects: sweeping hands, graveyards and permanents into libraries, casting revealed
/// cards while resolving, naming a card, per-object base P/T, token copies of creatures that left, storage counters and more.
/// The scripts are those of the content module.
/// </summary>
public class OriginsEffectsTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", string? loyalty = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"ori-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["loyalty"] = loyalty,
            ["keywords"] = oracle is "Flying" ? new[] { "Flying" } : Array.Empty<string>(),
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private delegate PlayerAction? Step(IReadOnlyList<PlayerAction> legal);

    private static void Script(Scenario s, params Step[] steps)
    {
        var q = new Queue<Step>(steps);
        s.Attacker.Act = (_, legal) =>
        {
            if (q.Count > 0 && q.Peek()(legal) is { } a) { q.Dequeue(); return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Step Cast(Scenario s, string name) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);
    private static Step Activate(Scenario s, string name, int index = -1) =>
        legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name && (index < 0 || a.Index == index));

    private static void ToGraveyard(Scenario s, PlayerId owner, CardId id)
    {
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
    }

    private static CardDefinition Bolt => Make("Test Bolt", "Instant", """{ "spell": { "targets": ["any"], "effects": [{ "damage": 3, "to": "target" }] } }""", "{1}");
    private static CardDefinition Glyph(string name, string cost = "{1}") => Make(name, "Enchantment", "{}", cost);

    // ------------------------------------------------------------------ Starfield of Nyx

    private const string Starfield = """
        { "abilities": [
          { "trigger": "upkeep", "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["enchantment"] } }],
            "effects": [{ "may": "Return it?", "effects": [{ "reanimate": "target" }] }] },
          { "static": { "affects": "permanents:you", "other": true, "filter": { "types": ["enchantment"], "notSubtype": "Aura" },
                        "while": { "control": { "types": ["enchantment"] }, "count": 5 }, "addTypes": ["creature"],
                        "setPower": { "manaValue": "affected" }, "setToughness": { "manaValue": "affected" } } }] }
        """;

    [Fact]
    public async Task StarfieldOfNyxGivesEachOtherNonAuraEnchantmentBasePowerAndToughnessEqualToItsOwnManaValue()
    {
        var s = new Scenario();
        var starfield = s.Add(P0, Make("Starfield of Nyx", "Enchantment", Starfield, "{4}{W}"));
        var one = s.Add(P0, Glyph("Small Glyph", "{1}"));
        var three = s.Add(P0, Glyph("Big Glyph", "{2}{W}"));
        var aura = s.Add(P0, Make("Odd Aura", "Enchantment — Aura", "{}", "{2}"));
        var theirs = s.Add(P1, Glyph("Their Glyph", "{4}"));
        Script(s);
        await s.RunUntilTurn();
        // Four enchantments: nothing happens.
        Assert.False(s.Card(one).IsCreature);

        var s2 = new Scenario();
        s2.Add(P0, Make("Starfield of Nyx", "Enchantment", Starfield, "{4}{W}"));
        var a = s2.Add(P0, Glyph("Small Glyph", "{1}"));
        var b = s2.Add(P0, Glyph("Big Glyph", "{2}{W}"));
        var aura2 = s2.Add(P0, Make("Odd Aura", "Enchantment — Aura", "{}", "{2}"));
        s2.Add(P0, Glyph("Fifth Glyph", "{5}"));
        var theirs2 = s2.Add(P1, Glyph("Their Glyph", "{4}"));
        Script(s2);
        await s2.RunUntilTurn();
        Assert.True(s2.Card(a).IsCreature);
        Assert.Equal((1, 1), (s2.Card(a).Power, s2.Card(a).Toughness));
        Assert.Equal((3, 3), (s2.Card(b).Power, s2.Card(b).Toughness));
        Assert.False(s2.Card(aura2).IsCreature); // Auras aren't affected
        Assert.False(s2.Card(theirs2).IsCreature); // only yours
        Assert.DoesNotContain(s2.Game.State.Battlefield, id => s2.Card(id).Name == "Starfield of Nyx" && s2.Card(id).IsCreature); // "each other"
        _ = starfield; _ = three; _ = aura; _ = theirs;
    }

    // ------------------------------------------------------------------ Tragic Arrogance

    [Fact]
    public async Task TragicArroganceLetsYouChooseForEachPlayerAndEveryoneSacrificesTheRestAtOnceButNotLands()
    {
        var arrogance = Make("Tragic Arrogance", "Sorcery", """
            { "spell": { "effects": [{ "keepOneOfEachType": "everyone", "chooser": "you", "kinds": ["artifact", "creature", "enchantment", "planeswalker"], "nonlandOnly": true }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var golem = s.Add(P1, Make("Iron Golem", "Artifact Creature — Golem", "{}", "{3}", "3/3"));
        var bear = s.Add(P1, Scenario.Creature("Bear", 2, 2));
        var glyphA = s.Add(P1, Glyph("Glyph A"));
        var glyphB = s.Add(P1, Glyph("Glyph B"));
        var theirLand = s.Add(P1, GenericCards.Forest);
        var mine1 = s.Add(P0, Scenario.Creature("Mine One", 1, 1));
        var mine2 = s.Add(P0, Scenario.Creature("Mine Two", 1, 1));
        s.InHand(P0, arrogance);
        // You choose: the golem both as artifact and creature, Glyph B, and your second creature.
        s.Attacker.Choose = (_, req) => new[] { req.Options.FirstOrDefault(o => o.Name is "Iron Golem" or "Glyph B" or "Mine Two")?.Id ?? req.Options[0].Id };
        Script(s, Cast(s, "Tragic Arrogance"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(glyphA).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(glyphB).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(theirLand).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(mine1).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(mine2).Zone);
        Assert.True(s.Game.State.Battlefield.Select(s.Card).Count(c => c.Name == "Mountain") == 1); // lands are never sacrificed
        Assert.Null(s.Defender.LastChoice); // the opponent chose nothing
    }

    // ------------------------------------------------------------------ Alhammarret, High Arbiter

    [Fact]
    public async Task AlhammarretNamesARevealedNonlandCardAndOpponentsCantCastItWhileItHasItsAbilities()
    {
        var arbiter = Make("Alhammarret, High Arbiter", "Legendary Creature — Sphinx",
            """{ "chooseOnEnter": "opponentsRevealHandsThenNonlandName", "opponentsCantCastChosenName": true }""", "{1}", "5/5", "Flying");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Lands(P1, 2);
        var bolt = s.InHand(P1, Bolt);
        var shard = s.InHand(P1, Make("Test Shard", "Instant", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{1}"));
        s.InHand(P0, arbiter);
        var offered = new List<string>();
        s.Attacker.Option = (_, req) => { offered.AddRange(req.Options); return req.Options.ToList().IndexOf("Test Bolt"); };
        Script(s, Cast(s, "Alhammarret, High Arbiter"));
        IReadOnlyList<PlayerAction>? theirs = null;
        s.Defender.Act = (_, legal) => { theirs ??= legal; return PassPriority.Instance; };
        await s.RunUntilTurn();
        var hal = s.Game.State.Battlefield.Select(s.Card).Single(c => c.Name.StartsWith("Alhammarret"));
        Assert.Equal("Test Bolt", hal.ChosenName);
        Assert.DoesNotContain("Forest", offered); // only nonland cards revealed this way
        Assert.Contains("Test Shard", offered);
        Assert.Contains(s.Game.Log, e => e is CardsRevealed r && r.Player == P1 && r.Cards.Contains(bolt));
        var legal = s.Game.GetLegalActions(P1);
        Assert.DoesNotContain(legal, a => a is CastSpell c && c.Card == bolt);
        Assert.Contains(legal, a => a is CastSpell c && c.Card == shard);

        // Losing its abilities ends the restriction (it's a static ability).
        var s2 = new Scenario();
        s2.Lands(P0, 2);
        s2.Lands(P1, 2);
        var bolt2 = s2.InHand(P1, Bolt);
        s2.InHand(P0, arbiter);
        var hush = s2.InHand(P0, Make("Hush", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "loseAllAbilities": "target" }] } }""", "{1}"));
        s2.Attacker.Option = (_, req) => req.Options.ToList().IndexOf("Test Bolt");
        IReadOnlyList<PlayerAction>? before = null, after = null;
        s2.Game.EventRaised += e =>
        {
            if (e is SpellResolved r && s2.Card(r.Card).Name.StartsWith("Alhammarret")) before = null;
            if (e is SpellCast c && c.Card == hush) before = s2.Game.GetLegalActions(P1);
            if (e is SpellResolved h && h.Card == hush) after = Array.Empty<PlayerAction>();
        };
        s2.Defender.Act = (_, legal) => { if (after is { Count: 0 }) after = legal; return PassPriority.Instance; };
        Script(s2, Cast(s2, "Alhammarret, High Arbiter"), Cast(s2, "Hush"));
        await s2.RunUntilTurn();
        Assert.DoesNotContain(before!, a => a is CastSpell c && c.Card == bolt2);
        Assert.Contains(after!, a => a is CastSpell c && c.Card == bolt2);
    }

    // ------------------------------------------------------------------ Day's Undoing, The Great Aurora

    [Fact]
    public async Task DaysUndoingShufflesHandsAndGraveyardsAwayDrawsSevenAndEndsYourTurn()
    {
        var undoing = Make("Day's Undoing", "Sorcery", """
            { "spell": { "effects": [{ "shuffleIntoLibraries": "everyone", "hand": true, "graveyard": true }, { "draw": 7, "who": "everyone" },
                                     { "if": "yourTurn", "then": [{ "endTurn": true }] }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var dead = s.InHand(P0, Scenario.Creature("Old Friend", 1, 1));
        ToGraveyard(s, P0, dead);
        var theirDead = s.InHand(P1, Scenario.Creature("Old Foe", 1, 1));
        ToGraveyard(s, P1, theirDead);
        var spell = s.InHand(P0, undoing);
        int moved = 0;
        s.Game.EventRaised += e => { if (e is CardMoved { To: Zone.Library }) moved++; };
        Script(s, Cast(s, "Day's Undoing"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(dead).Zone == Zone.Hand ? Zone.Library : s.Card(dead).Zone); // shuffled in (maybe drawn again)
        Assert.Empty(s.Game.State.GetPlayer(P0).Graveyard);
        Assert.Empty(s.Game.State.GetPlayer(P1).Graveyard);
        Assert.Equal(7, s.Game.State.GetPlayer(P0).Hand.Count);
        Assert.Equal(7, s.Game.State.GetPlayer(P1).Hand.Count);
        Assert.Equal(Zone.Exile, s.Card(spell).Zone); // the turn ended with it on the stack
        Assert.Equal(7 + 1 + 8, moved); // P0's hand of 7 and graveyard card, P1's hand of 7 and graveyard card: all into libraries
    }

    [Fact]
    public async Task TheGreatAuroraShufflesHandsAndOwnedPermanentsAwayDrawsThatManyAndPutsLandsOntoTheBattlefield()
    {
        var aurora = Make("The Great Aurora", "Sorcery", """
            { "spell": { "effects": [{ "shuffleIntoLibraries": "everyone", "hand": true, "permanents": true, "drawThatMany": true },
                                     { "eachPutsFromHand": { "types": ["land"] }, "who": "everyone" }, { "exileThisSpell": true }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 2); // two Mountains
        var token = s.Add(P0, Scenario.Creature("Spirit Token", 1, 1) with { IsToken = true });
        s.Add(P1, Scenario.Creature("Their Bear", 2, 2));
        var spell = s.InHand(P0, aurora);
        // P0 puts every land; P1 puts none.
        s.Attacker.Choose = (_, req) => req.Purpose == CardChoicePurpose.ToBattlefield ? req.Options.Select(o => o.Id).ToList() : req.Options.Take(req.Min).Select(o => o.Id).ToList();
        int p0Hand = 0;
        Script(s, legal => { p0Hand = s.Game.State.GetPlayer(P0).Hand.Count; return Cast(s, "The Great Aurora")(legal); });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(spell).Zone);
        Assert.False(s.Game.State.Cards.ContainsKey(token) && s.Card(token).Zone == Zone.Battlefield);
        // P0: hand (without the Aurora) + 2 Mountains + the token, all lands drawn (Forest library) and put onto the battlefield.
        int p0Drew = (p0Hand - 1) + 2 + 1;
        Assert.Equal(p0Drew, s.Game.State.PermanentsControlledBy(P0).Count(c => c.Is(CardType.Land)));
        Assert.Empty(s.Game.State.GetPlayer(P0).Hand);
        // P1: hand of 7 + the bear, drawn back (P1 then draws for turn 2 only after RunUntilTurn stops).
        Assert.Equal(8, s.Game.State.GetPlayer(P1).Hand.Count);
        Assert.Empty(s.Game.State.PermanentsControlledBy(P1));
    }

    // ------------------------------------------------------------------ Jace, Vryn's Prodigy // Jace, Telepath Unbound

    private static CardDefinition Jace()
    {
        var front = Make("Jace, Vryn's Prodigy", "Legendary Creature — Human Wizard", """
            { "abilities": [{ "cost": "{T}", "effects": [{ "draw": 1 }, { "discard": 1 }, { "if": { "graveyard": 5 }, "then": [{ "blink": "self", "transformed": true }] }] }] }
            """, "{1}{U}", "0/2");
        var back = Make("Jace, Telepath Unbound", "Legendary Planeswalker — Jace", """
            { "abilities": [
              { "cost": "+1", "targets": [{ "kind": "creature", "optional": true }], "effects": [{ "pump": [-2, 0], "what": "target", "untilYourNextTurn": true }] },
              { "cost": "-3", "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["instant", "sorcery"] } }],
                "effects": [{ "castFromGraveyardThisTurn": "target", "exileInstead": true }] },
              { "cost": "-9", "effects": [{ "emblem": "Jace, Telepath Unbound", "abilities": [
                  { "trigger": "castSpell", "targets": ["opponent"], "effects": [{ "mill": 5, "who": "target" }], "text": "Whenever you cast a spell, target opponent mills five cards." }] }] }] }
            """, "", null, "", "5");
        return front with { BackFace = back };
    }

    [Fact]
    public async Task JaceLootsAndWithFiveCardsInTheGraveyardComesBackTransformedWithLoyalty()
    {
        var s = new Scenario();
        var jace = s.Add(P0, Jace());
        for (int i = 0; i < 4; i++) ToGraveyard(s, P0, s.InHand(P0, Glyph($"Old {i}")));
        Script(s, Activate(s, "Jace, Vryn's Prodigy"));
        await s.RunUntilTurn();
        var card = s.Card(jace);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.True(card.Transformed);
        Assert.Equal("Jace, Telepath Unbound", card.Name);
        Assert.Equal(5, card.CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task JacesMinusThreeCastsAnInstantFromTheGraveyardThatIsThenExiled()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var jace = s.Add(P0, Jace());
        var bolt = s.InHand(P0, Bolt);
        ToGraveyard(s, P0, bolt);
        for (int i = 0; i < 3; i++) ToGraveyard(s, P0, s.InHand(P0, Glyph($"Old {i}")));
        s.Attacker.Targets = (_, req) => new[] { req.LegalAt(0).FirstOrDefault(t => t.Card == bolt || t.Player == P1) };
        s.Attacker.Act = (_, legal) =>
            legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault(a => ((ActivateAbility)a).Source == jace && (!s.Card(jace).Transformed || ((ActivateAbility)a).Index == 1))
            ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault(c => ((CastSpell)c).Card == bolt)
            ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Exile, s.Card(bolt).Zone);
        Assert.Equal(2, s.Card(jace).CounterCount(CounterKind.Loyalty));
    }

    // ------------------------------------------------------------------ Flameshadow Conjuring

    private const string Flameshadow = """
        { "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "token": false },
            "effects": [{ "mayPay": "Pay {R}?", "mana": "{R}", "effects": [{ "copy": "triggered", "gainsHaste": true, "atNextEndStep": [{ "exile": "triggered" }] }] }] }] }
        """;

    [Fact]
    public async Task FlameshadowConjuringMakesAHastyCopyWhoseHasteIsNotCopiableAndExilesItAtTheEndStep()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Flameshadow Conjuring", "Enchantment", Flameshadow, "{3}{R}"));
        s.InHand(P0, Scenario.Creature("Ogre", 3, 3));
        Card? token = null;
        bool hadHaste = false, copiableHaste = true;
        s.Game.EventRaised += e =>
        {
            if (e is TokenCreated t) token = s.Card(t.Card);
            if (e is AbilityResolved && token is { Zone: Zone.Battlefield } && !hadHaste)
            {
                hadHaste = token.Has(Keyword.Haste);
                copiableHaste = token.Definition.Keywords.Contains("Haste");
            }
        };
        Script(s, Cast(s, "Ogre"));
        await s.RunUntilTurn();
        Assert.NotNull(token);
        Assert.Equal("Ogre", token!.Name);
        Assert.True(hadHaste);
        Assert.False(copiableHaste);
        Assert.NotEqual(Zone.Battlefield, token.Zone); // exiled at the beginning of the end step
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == token.Id && m.To == Zone.Exile);
    }

    [Fact]
    public async Task FlameshadowConjuringCopiesACreatureThatAlreadyLeftUsingItsLastKnownValues()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Flameshadow Conjuring", "Enchantment", Flameshadow, "{3}{R}"));
        s.InHand(P0, Scenario.Creature("Hollow Shade", 0, 0)); // dies to state-based actions before the trigger resolves
        var made = new List<string>();
        s.Game.EventRaised += e => { if (e is TokenCreated t) made.Add(s.Card(t.Card).Name); };
        Script(s, Cast(s, "Hollow Shade"));
        await s.RunUntilTurn();
        Assert.Equal(new[] { "Hollow Shade" }, made);
    }

    // ------------------------------------------------------------------ Nissa's Revelation, Talent of the Telepath

    [Fact]
    public async Task NissasRevelationDrawsAndGainsFromTheRevealedCreaturesPowerAndToughness()
    {
        var revelation = Make("Nissa's Revelation", "Sorcery", """
            { "spell": { "effects": [{ "scry": 5 }, { "revealTop": { "types": ["creature"] }, "effects": [{ "draw": { "power": "found" } }, { "gainLife": { "toughness": "found" } }] }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var beast = s.Game.SetupInLibrary(P0, Scenario.Creature("Big Beast", 3, 4));
        s.InHand(P0, revelation);
        bool stacked = false;
        Script(s, legal => { if (!stacked) { stacked = true; s.Restack(P0, beast); } return Cast(s, "Nissa's Revelation")(legal); });
        int handBefore = 0;
        s.Game.EventRaised += e => { if (e is SpellCast) handBefore = s.Game.State.GetPlayer(P0).Hand.Count; };
        int drawn = 0;
        bool cast = false;
        s.Game.EventRaised += e => { if (e is SpellCast) cast = true; if (cast && e is CardDrawn d && d.Player == P0) drawn++; };
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(3, drawn);
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == beast && m.From == Zone.Library && m.To == Zone.Hand);
        _ = handBefore;
    }

    private static CardDefinition Talent() => Make("Talent of the Telepath", "Sorcery", """
        { "spell": { "targets": ["opponent"], "effects": [{ "revealTopCastFree": 7, "of": "target", "filter": { "types": ["instant", "sorcery"] }, "casts": 1,
            "moreCastsIf": { "graveyard": 2, "filter": { "types": ["instant", "sorcery"] } }, "moreCasts": 2, "rest": "graveyard" }] } }
        """, "{1}");

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task TalentOfTheTelepathCastsRevealedSpellsFreeAndTheRestGoToTheGraveyard(bool spellMastery, int casts)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bolts = Enumerable.Range(0, 3).Select(_ => s.Game.SetupInLibrary(P1, Bolt)).ToArray();
        if (spellMastery)
            for (int i = 0; i < 2; i++) ToGraveyard(s, P0, s.InHand(P0, Make($"Spent {i}", "Instant", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{1}")));
        s.InHand(P0, Talent());
        bool stacked = false;
        s.Attacker.Choose = (_, req) => req.Options.Take(1).Select(o => o.Id).ToList();
        s.Attacker.Targets = (_, req) => new[] { req.LegalAt(0).FirstOrDefault(t => t.Player == P1) };
        Script(s, legal => { if (!stacked) { stacked = true; s.Restack(P1, bolts); } return Cast(s, "Talent of the Telepath")(legal); });
        await s.RunUntilTurn();
        Assert.Equal(20 - 3 * casts, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(7, s.Game.State.GetPlayer(P1).Graveyard.Count); // the bolts cast went to their owner's graveyard after resolving
        Assert.Equal(casts, s.Game.Log.OfType<SpellCast>().Count(c => c.Player == P0 && s.Card(c.Card).Name == "Test Bolt"));
    }

    // ------------------------------------------------------------------ Infinite Obliteration, Nightsnare

    [Fact]
    public async Task InfiniteObliterationExilesCardsWithTheNameFromGraveyardHandAndLibrary()
    {
        var obliteration = Make("Infinite Obliteration", "Sorcery", """
            { "spell": { "targets": ["opponent"], "effects": [{ "nameAndExile": "target", "nameFilter": { "types": ["creature"] } }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var inHand = s.InHand(P1, Scenario.Creature("Grizzly", 2, 2));
        var inGrave = s.InHand(P1, Scenario.Creature("Grizzly", 2, 2));
        ToGraveyard(s, P1, inGrave);
        var inLibrary = s.Game.SetupInLibrary(P1, Scenario.Creature("Grizzly", 2, 2));
        s.InHand(P0, obliteration);
        List<string>? names = null;
        s.Attacker.Option = (_, req) => { names = req.Options.ToList(); return names.IndexOf("Grizzly"); };
        s.Attacker.Choose = (_, req) => req.Options.Select(o => o.Id).ToList();
        Script(s, Cast(s, "Infinite Obliteration"));
        await s.RunUntilTurn();
        Assert.DoesNotContain("Infinite Obliteration", names!); // only creature card names
        Assert.All(new[] { inHand, inGrave, inLibrary }, id => Assert.Equal(Zone.Exile, s.Card(id).Zone));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NightsnareDiscardsTheChosenNonlandCardOrElseTwoOfTheirChoice(bool choose)
    {
        var nightsnare = Make("Nightsnare", "Sorcery", """
            { "spell": { "targets": ["opponent"], "effects": [{ "discardChosen": "target", "filter": { "not": ["land"] }, "optional": true, "else": [{ "discard": 2, "who": "target" }] }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.InHand(P1, Scenario.Creature("Bear", 2, 2));
        s.InHand(P0, nightsnare);
        s.Attacker.Choose = (_, req) => choose ? req.Options.Take(1).Select(o => o.Id).ToList() : Array.Empty<CardId>();
        Script(s, Cast(s, "Nightsnare"));
        await s.RunUntilTurn();
        var discarded = s.Game.State.GetPlayer(P1).Graveyard;
        Assert.Equal(choose ? 1 : 2, discarded.Count);
        if (choose) Assert.Equal(bear, discarded.Single());
    }

    // ------------------------------------------------------------------ Turn to Frog

    [Fact]
    public async Task TurnToFrogKeepsTheLandTypesOfALandCreatureAndRemovesEverythingElse()
    {
        var frog = Make("Turn to Frog", "Instant", """
            { "spell": { "targets": ["creature"], "effects": [{ "become": "target", "power": 1, "toughness": 1, "setColors": ["U"], "setSubtypes": ["Frog"], "loseAbilities": true }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var dryad = s.Add(P1, Make("Grove Dryad", "Land Creature — Forest Dryad", "{}", "", "3/3", "Flying"));
        s.InHand(P0, frog);
        Card? seen = null;
        s.Game.EventRaised += e => { if (e is SpellResolved && seen is null) seen = s.Card(dryad); };
        int power = 0, toughness = 0; bool flying = true, forest = false, dryadType = true, frogType = false; IReadOnlyList<string>? colors = null;
        s.Game.EventRaised += e =>
        {
            if (e is not SpellResolved) return;
            var c = s.Card(dryad);
            (power, toughness, flying, forest, dryadType, frogType, colors) = (c.Power, c.Toughness, c.Has(Keyword.Flying), c.HasSubtype("Forest"), c.HasSubtype("Dryad"), c.HasSubtype("Frog"), c.Colors);
        };
        Script(s, Cast(s, "Turn to Frog"));
        await s.RunUntilTurn();
        Assert.Equal((1, 1), (power, toughness));
        Assert.False(flying);
        Assert.True(forest);
        Assert.False(dryadType);
        Assert.True(frogType);
        Assert.Equal(new[] { "U" }, colors);
        Assert.True(s.Card(dryad).Has(Keyword.Flying)); // until end of turn only
    }

    [Fact]
    public async Task AnAnimatedPlaneswalkerTurnedIntoAFrogKeepsItsPlaneswalkerType()
    {
        var frog = Make("Turn to Frog", "Instant", """
            { "spell": { "targets": ["creature"], "effects": [{ "become": "target", "power": 1, "toughness": 1, "setColors": ["U"], "setSubtypes": ["Frog"], "loseAbilities": true }] } }
            """, "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var gideon = s.Add(P1, Make("Animated Gideon", "Legendary Planeswalker Creature — Gideon Human Soldier", "{}", "", "4/4", loyalty: "3"));
        s.Card(gideon).Counters[CounterKind.Loyalty] = 3; // set up on the battlefield without entering, so no loyalty yet
        s.InHand(P0, frog);
        bool gideonType = false, humanType = true, frogType = false;
        s.Game.EventRaised += e =>
        {
            if (e is not SpellResolved) return;
            var c = s.Card(gideon);
            (gideonType, humanType, frogType) = (c.HasSubtype("Gideon"), c.HasSubtype("Human"), c.HasSubtype("Frog"));
        };
        Script(s, Cast(s, "Turn to Frog"));
        await s.RunUntilTurn();
        Assert.True(gideonType);
        Assert.False(humanType);
        Assert.True(frogType);
    }

    // ------------------------------------------------------------------ Mage-Ring Network

    private const string MageRing = """
        { "extraMana": [{ "types": "{C}", "removeCounters": "storage" }],
          "abilities": [{ "cost": "{1}, {T}", "effects": [{ "counters": 1, "what": "self", "kind": "storage" }] }] }
        """;

    private static CardDefinition Network() => Make("Mage-Ring Network", "Land", MageRing, "", null,
        "{T}: Add {C}.\n{1}, {T}: Put a storage counter on this land.\n{T}, Remove any number of storage counters from this land: Add {C} for each storage counter removed this way.");

    [Fact]
    public async Task MageRingNetworkPaysWithAsManyStorageCountersAsNeeded()
    {
        var s = new Scenario();
        var network = s.Add(P0, Network());
        s.Card(network).Counters[CounterKind.Storage] = 3;
        s.InHand(P0, Make("Pricey Gizmo", "Artifact", "{}", "{2}"));
        Script(s, Cast(s, "Pricey Gizmo"));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellCast c && s.Card(c.Card).Name == "Pricey Gizmo");
        Assert.Equal(1, s.Card(network).CounterCount(CounterKind.Storage)); // two removed for two mana, one left
        Assert.Equal(2, s.Card(network).ManaOptions.Count);
    }

    [Fact]
    public async Task MageRingNetworkAsksHowManyCountersWhenActivatedOnItsOwnAndStoresCounters()
    {
        var s = new Scenario();
        var network = s.Add(P0, Network());
        s.Card(network).Counters[CounterKind.Storage] = 3;
        s.Attacker.Number = (_, req) => 2;
        int pooled = -1;
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (done) return PassPriority.Instance;
            if (s.Card(network).Tapped) { pooled = s.Game.State.GetPlayer(P0).ManaPool.Total; done = true; return PassPriority.Instance; }
            return legal.OfType<ActivateManaAbility>().FirstOrDefault(a => a.Source == network && a.Option == 1) ?? (PlayerAction)PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, pooled);
        Assert.Equal(1, s.Card(network).CounterCount(CounterKind.Storage));

        // "{1}, {T}: Put a storage counter on this land" (paid by a Mountain).
        var s2 = new Scenario();
        s2.Lands(P0, 1);
        var network2 = s2.Add(P0, Network());
        Script(s2, Activate(s2, "Mage-Ring Network"));
        await s2.RunUntilTurn();
        Assert.Equal(1, s2.Card(network2).CounterCount(CounterKind.Storage));
    }
}
