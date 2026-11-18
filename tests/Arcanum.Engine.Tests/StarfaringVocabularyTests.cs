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

/// <summary>
/// Card-script vocabulary for warp and behold, stations, counters, mana restrictions, token copies, life totals, libraries
/// and the choices of parity and card type. The cards are invented.
/// </summary>
public class StarfaringVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null, params string[] keywords)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"starfaring-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
        if (keywords.Length > 0) fields["keywords"] = keywords;
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    /// <summary>Player 0 casts the given cards in order, each as soon as it can, and never attacks.</summary>
    private static void CastInOrder(Scenario s, params CardId[] cards)
    {
        s.Attacker.Act = (_, legal) =>
        {
            foreach (var card in cards)
                if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } cast) return cast;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    /// <summary>Puts a card from the owner's hand into the owner's exile, as if an effect had exiled it.</summary>
    private static CardId InExile(Scenario s, PlayerId owner, CardDefinition card, bool warped = false)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        s.Card(id).Zone = Zone.Exile;
        s.Card(id).WarpedInExile = warped;
        player.Exile.Add(id);
        return id;
    }

    /// <summary>Puts a card from the owner's hand into the owner's graveyard.</summary>
    private static CardId InGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        s.Card(id).Zone = Zone.Graveyard;
        player.Graveyard.Add(id);
        return id;
    }

    /// <summary>Player 0 activates the station ability of <paramref name="ship"/>, tapping the given creatures in turn.</summary>
    private static void StationWith(Scenario s, CardId ship, params CardId[] tapOrder)
    {
        int next = 0;
        s.Attacker.Act = (_, legal) =>
            next < tapOrder.Length && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ship) is { } a ? a : PassPriority.Instance;
        s.Attacker.Choose = (_, _) =>
        {
            var pick = tapOrder[Math.Min(next, tapOrder.Length - 1)];
            next++;
            return new[] { pick };
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static int CountOn(Scenario s, PlayerId owner, string name) =>
        s.Game.State.Battlefield.Select(s.Card).Count(c => c.Controller == owner && c.Name == name);

    // ------------------------------------------------------------------ scripts under test (invented wording)

    private const string DrillerScript = """{ "extraMana": [{ "types": "{U}", "amount": 1, "onlyFor": { "types": ["artifact"] } }, { "types": "{U}", "amount": 2, "onlyFor": { "types": ["artifact"] }, "abilitiesToo": true, "abilitiesOnly": true }] }""";

    private const string ShrineScript = """{ "abilities": [{ "trigger": "creatureDies", "effects": [{ "eachPlayer": [{ "choose": { "types": ["creature"], "controller": "you" } }, { "exile": "chosen", "linked": true }], "who": "opponents" }], "text": "invented", "filter": { "types": ["creature"], "controller": "you" } }, { "trigger": "endStep", "effects": [{ "sacrificeIt": "self" }, { "chooseOneOf": { "exiledWithThis": { "types": ["creature"] } } }, { "reanimate": "found", "counters": 2 }], "text": "invented", "if": { "aPlayerControlsNone": { "types": ["creature"] } } }] }""";

    private const string ShatterScript = """{ "abilities": [{ "static": { "affects": "enchanted", "pump": [-5, 0] }, "text": "invented" }, { "trigger": "attachedBecomesTapped", "effects": [{ "destroy": "triggered" }], "text": "invented" }, { "trigger": "attachedDealtDamage", "effects": [{ "destroy": "triggered" }], "text": "invented" }] }""";

    private const string EncounterScript = """{ "additionalCost": { "behold": { "types": ["creature"] }, "beholdWarpedExile": true }, "spell": { "targets": ["creature"], "effects": [{ "damage": "beheldPower", "to": "target" }], "text": "invented" } }""";

    private const string RushScript = """{ "spell": { "targets": ["creature:you"], "effects": [{ "pump": [3, 2], "what": "target" }, { "if": { "target": "target", "is": { "castForWarp": true } }, "then": [{ "pump": [0, 0], "what": "target", "keywords": ["Trample", "Haste"] }] }], "text": "invented" } }""";

    private const string BrawlerScript = """{ "abilities": [{ "trigger": "landfall", "effects": [{ "revealUntil": { "not": ["land"] }, "castThisTurn": true }], "text": "invented", "filter": { "types": ["land"] } }] }""";

    private const string BeastScript = """{ "yourCreatureSpellsCantBeCountered": true, "combatDamageCantBePrevented": true }""";

    private const string LancerScript = """{ "wardCost": { "discard": 1, "discardAtRandom": true }, "abilities": [{ "trigger": "attacks", "effects": [{ "loseLife": "affectedHalfLife", "who": "defendingPlayer" }], "text": "invented", "if": "void" }] }""";

    private const string ZoneScript = """{ "doublesCountersOnYour": { "anyOf": [{ "types": ["creature"] }, { "subtype": "Spacecraft" }, { "subtype": "Planet" }] }, "abilities": [] }""";

    private const string RiddlerScript = """{ "drawsOneMoreWithOneOrFewerInHand": true, "abilities": [{ "trigger": "enters", "effects": [{ "draw": 1 }], "text": "invented" }] }""";

    private const string WanderScript = """{ "firstSpellEachTurnFree": true, "abilities": [{ "trigger": "enters", "effects": [{ "shuffleHandIntoLibrary": "you", "withGraveyard": true }, { "draw": 7 }], "text": "invented", "if": "wasCast" }] }""";

    private const string ShuffleHand = """{ "abilities": [{ "trigger": "enters", "effects": [{ "shuffleHandIntoLibrary": "you" }, { "draw": 7 }], "text": "invented", "if": "wasCast" }] }""";

    private const string UndoScript = """{ "spell": { "targets": ["spell"], "effects": [{ "if": { "lessThan": ["manaSpentOnTarget", { "manaValue": "target" }] }, "then": [{ "counter": "target" }, { "draw": 1 }], "else": [{ "counter": "target" }] }], "text": "invented" } }""";

    private const string MediumScript = """{ "abilities": [{ "cost": "{T}", "sorcery": true, "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"] }, "text": "invented" }], "effects": [{ "reanimate": "target", "addSubtypes": ["Skeleton"], "noAbilities": true }], "text": "invented" }] }""";

    private const string Blade = """{ "abilities": [{ "trigger": "enters", "chooseCount": 1, "modes": [{ "text": "invented", "effects": [{ "counters": 2, "what": "self" }] }, { "text": "invented", "targets": [{ "kind": "exiledCard", "controller": "any", "filter": { "hasWarp": true }, "text": "invented" }], "effects": [{ "toLibrary": "target", "bottom": true }] }], "text": "invented" }] }""";

    private const string CageScript = """{ "abilities": [{ "trigger": "enters", "effects": [{ "exileUntilLeaves": { "each": { "anyOf": [{ "types": ["artifact"] }, { "types": ["creature"] }], "maxManaValue": 2, "controller": "any" } } }], "text": "invented" }, { "cost": "{6}{W}{W}", "effects": [{ "toGraveyard": "exiledWithThis" }, { "tokens": "putIntoGraveyardThisWay", "token": { "name": "Robot", "types": "Artifact Creature — Robot", "power": 2, "toughness": 2 } }, { "sacrificeIt": "self" }], "text": "invented" }] }""";

    private const string AmalgamScript = """{ "entersWithCounters": "manaSpentOnThis", "abilities": [{ "trigger": "youAttack", "effects": [{ "mayPay": "Invented test text.", "cost": { "removeCountersFromCreatures": 2, "removeCountersKind": "+1/+1", "removeCountersFromDifferent": true }, "effects": [{ "draw": 1 }, { "tokens": 1, "token": { "name": "Robot", "types": "Artifact Creature — Robot", "power": 2, "toughness": 2 } }] }], "text": "invented" }] }""";

    private const string SkirmisherScript = """{ "abilities": [{ "trigger": "attacks", "effects": [{ "mayPay": "Pay {2}?", "mana": "{2}", "effects": [{ "tokens": 1, "token": { "name": "Robot", "types": "Artifact Creature — Robot", "power": 2, "toughness": 2 }, "tapped": true, "attacking": true, "sacrificeAtEndOfCombat": true }] }], "text": "invented" }] }""";

    private const string ExplorerScript = """{ "abilities": [{ "trigger": "enters", "effects": [{ "tokens": 1, "token": "Lander", "sacrificeAtEndStepOfYourNextTurn": true }], "text": "invented" }] }""";

    private const string WatcherScript = """{ "abilities": [{ "trigger": "enters", "effects": [{ "exileFromHand": 1, "who": "opponents", "ownerMayPlay": true, "playCostMore": 1, "landsEnterTapped": true }], "text": "invented" }] }""";

    private const string PurgeScript = """{ "spell": { "effects": [{ "chooseParity": true }, { "destroy": { "each": { "types": ["creature"], "controller": "any", "manaValueParityChosen": true } } }, { "gainControl": { "each": { "types": ["creature"], "controller": "any" } }, "untilEndOfTurn": true }, { "untap": { "each": { "types": ["creature"], "controller": "any" } } }, { "pump": [0, 0], "what": { "each": { "types": ["creature"], "controller": "any" } }, "keywords": ["Haste"] }], "text": "invented" } }""";

    private const string KeystoneScript = """{ "abilities": [{ "trigger": "youPlayLand", "effects": [{ "draw": 1 }], "text": "invented" }, { "trigger": "castSpell", "effects": [{ "draw": 1 }], "text": "invented" }, { "trigger": "endStep", "effects": [{ "lifeBecomes": "halfStartingLife" }], "text": "invented" }] }""";

    private const string LamentScript = """{ "spell": { "effects": [{ "destroy": { "each": { "types": ["creature"], "controller": "any", "maxToughnessX": true } } }, { "loseLife": "X" }, { "if": { "atLeast": "X", "value": 6 }, "then": [{ "returnFromGraveyard": { "types": ["creature"] }, "to": "battlefield", "anyGraveyard": true, "amongDestroyed": true }] }], "text": "invented" } }""";

    private const string KeeperScript = """{ "stationsWithToughness": true, "abilities": [{ "static": { "affects": "creatures:you", "filter": { "toughnessAbovePower": true }, "pump": [0, 0], "keywords": ["Assigns damage by toughness"] }, "text": "invented" }] }""";

    private const string MonkScript = """{ "abilities": [{ "cost": "{2}, removePlusOneCounterFrom:creature", "targets": [{ "kind": "permanent", "filter": { "types": ["artifact", "creature"] }, "text": "invented" }], "effects": [{ "tap": "target" }], "text": "invented" }] }""";

    private const string LurkerScript = """{ "powerFrom": { "ownedInExile": {} }, "toughnessFrom": { "ownedInExile": {}, "offset": 1 } }""";

    private const string AuraScript = """{ "aura": { "kind": "permanent", "controller": "you", "filter": { "types": ["artifact", "creature"] }, "text": "invented" }, "firstTokensCopyEnchanted": true, "abilities": [] }""";

    private const string CaptainScript = """{ "grantsWarp": { "filter": { "anyOf": [{ "types": ["artifact"] }, { "types": ["creature"], "colors": ["R"] }] }, "cost": "{2}{R}" }, "abilities": [{ "static": { "affects": "creatures:you", "other": true, "pump": [0, 0], "keywords": ["Haste"] }, "text": "invented" }] }""";

    private const string AdeptScript = """{ "replaces": ["LookAtLibraryTop"], "castFromLibraryTop": { "types": ["artifact"] }, "abilities": [{ "static": { "affects": "permanents:you", "filter": { "types": ["artifact"] }, "grantsMana": "{U}", "grantsManaOnlyFor": { "notCastFromHand": true } }, "text": "invented" }] }""";

    private const string RevenantScript = """{ "warpFromGraveyard": true }""";

    private const string Note = """{ "spell": { "effects": [{ "gainLife": 1 }] } }""";

    // ------------------------------------------------------------------ mana, combat and stations

    [Fact]
    public async Task AnArtifactCreaturesManaPaysForAnArtifactsAbilityButNotForASpell()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Driller", "Artifact Creature — Robot", "{1}{U}", DrillerScript, "Flying", "1", "1"));
        var relic = s.Add(P0, Scripted("Test Relic", "Artifact", "{0}", """{ "abilities": [{ "cost": "{U}{U}", "effects": [{ "gainLife": 1 }], "text": "invented" }] }""", "{U}{U}: You gain 1 life."));
        bool activated = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == relic) is { } ability) { activated = true; return ability; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(activated);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task TheArtifactOnlyManaCannotPayForASpell()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Driller", "Artifact Creature — Robot", "{1}{U}", DrillerScript, "Flying", "1", "1"));
        var spell = s.InHand(P0, Scripted("Test Sorcery", "Sorcery", "{U}{U}", Note));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == spell); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task WhenNoPlayerControlsACreatureShrineIsSacrificedForACreatureFromExile()
    {
        var s = new Scenario();
        var shrine = s.Add(P0, Scripted("Test Shrine", "Legendary Enchantment", "{2}{B}{B}", ShrineScript));
        s.Add(P0, Creature("Bear", 2, 2));
        var ox = s.Add(P1, Creature("Ox", 3, 3));
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(shrine).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(ox).Zone);
        Assert.Equal(P0, s.Card(ox).Controller);
        Assert.Equal(2, s.Card(ox).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task TheShrineStaysWhileEveryPlayerControlsACreature()
    {
        var s = new Scenario();
        var shrine = s.Add(P0, Scripted("Test Shrine", "Legendary Enchantment", "{2}{B}{B}", ShrineScript));
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P1, Creature("Ox", 3, 3));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(shrine).Zone);
    }

    [Theory]
    [InlineData(true, Zone.Graveyard)]   // attacking taps the enchanted creature, and it is destroyed
    [InlineData(false, Zone.Battlefield)]
    public async Task AnEnchantedCreatureThatBecomesTappedIsDestroyed(bool attack, Zone zone)
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, Scripted("Test Shatter", "Enchantment — Aura", "{U}", ShatterScript, "Enchant creature"), attachTo: bear);
        if (!attack) s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(zone, s.Card(bear).Zone);
    }

    [Theory]
    [InlineData(true, Zone.Graveyard, true)]     // a warped creature card in exile can be beheld: its 4 power kills the 2/2
    [InlineData(false, Zone.Battlefield, false)] // an exiled creature card that was not warped can't be beheld
    public async Task BeholdAWarpedCreatureCardInExileForItsPower(bool warped, Zone bearZone, bool cast)
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        InExile(s, P0, Creature("Warped Ghost", 4, 4), warped);
        var spell = s.InHand(P0, Scripted("Test Encounter", "Instant", "{1}{G}", EncounterScript, "Invented test text."));
        CastInOrder(s, spell);
        await s.RunUntilTurn();
        Assert.Equal(cast, s.Game.Log.Any(e => e is SpellCast sc && sc.Card == spell));
        Assert.Equal(bearZone, s.Card(bear).Zone);
    }

    [Theory]
    [InlineData(true)]  // cast for its warp cost: trample and haste
    [InlineData(false)] // cast for its mana cost: only the +3/+2
    public async Task ACreatureCastForItsWarpCostGainsTrampleAndHasteFromTheSpell(bool warped)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).CastForWarp = warped;
        var spell = s.InHand(P0, Scripted("Test Rush", "Instant", "{R}", RushScript, "Invented test text."));
        CastInOrder(s, spell);
        bool trample = false, haste = false;
        int power = 0;
        s.Attacker.Attack = (_, _, _) =>
        {
            trample = s.Card(bear).Has(Keyword.Trample);
            haste = s.Card(bear).Has(Keyword.Haste);
            power = s.Card(bear).Power;
            return Array.Empty<AttackDeclaration>();
        };
        await s.RunUntilTurn();
        Assert.Equal(5, power);
        Assert.Equal(warped, trample);
        Assert.Equal(warped, haste);
    }

    [Fact]
    public async Task ACardRevealedByLandfallCanBeCastThatTurn()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Brawler", "Creature — Beast", "{4}{R}{R}", BrawlerScript, "Invented test text.", "6", "6"));
        var note = s.InHand(P0, Scripted("Probe Note", "Sorcery", "{R}", Note, "Gain 1 life."));
        s.Lands(P0, 1);
        bool restacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, note); }
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == note) is { } cast) return cast;
            if (legal.OfType<PlayLand>().FirstOrDefault() is { } land) return land;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellCast sc && sc.Card == note);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task CombatDamageCantBePreventedForYourCreaturesWithTheKeyword()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Beast", "Creature — Beast", "{G}{G}", BeastScript, "Invented test text.", "3", "2"));
        var fog = s.InHand(P1, Scripted("Test Mist", "Instant", "{1}", """{ "spell": { "effects": [{ "preventDamage": true, "combatOnly": true }] } }""", "Invented test text."));
        s.Lands(P1, 1);
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == fog) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // the 3 damage gets through the fog
    }

    [Fact]
    public async Task CombatDamageOfACreatureWithoutTheKeywordIsPrevented()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Plain Beast", 3, 2));
        var fog = s.InHand(P1, Scripted("Test Mist", "Instant", "{1}", """{ "spell": { "effects": [{ "preventDamage": true, "combatOnly": true }] } }""", "Invented test text."));
        s.Lands(P1, 1);
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == fog) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task CreatureSpellsYouControlCannotBeCountered()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Beast", "Creature — Beast", "{G}{G}", BeastScript, "Invented test text.", "3", "2"));
        s.Lands(P0, 1);
        var bear = s.InHand(P0, Creature("Bear", 2, 2));
        s.Lands(P1, 1);
        var denial = s.InHand(P1, Scripted("Test Denial", "Instant", "{1}", """{ "spell": { "targets": ["spell"], "effects": [{ "counter": "target" }] } }""", "Counter target spell."));
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == denial) is { } cast ? cast : PassPriority.Instance;
        CastInOrder(s, bear);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
    }

    [Fact]
    public async Task ACreatureSpellWithoutTheKeywordCanBeCountered()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.InHand(P0, Creature("Bear", 2, 2));
        s.Lands(P1, 1);
        var denial = s.InHand(P1, Scripted("Test Denial", "Instant", "{1}", """{ "spell": { "targets": ["spell"], "effects": [{ "counter": "target" }] } }""", "Counter target spell."));
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == denial) is { } cast ? cast : PassPriority.Instance;
        CastInOrder(s, bear);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
    }

    [Theory]
    [InlineData(true, 4)]  // the doubler: two counters become four
    [InlineData(false, 2)]
    public async Task CountersPutOnYourCreaturesAreDoubled(bool doubler, int expected)
    {
        var s = new Scenario();
        if (doubler) s.Add(P0, Scripted("Test Zone", "Enchantment", "{3}{G}", ZoneScript, "Invented test text."));
        s.Add(P0, GenericCards.Forest);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var charm = s.InHand(P0, Scripted("Probe Charm", "Sorcery", "{G}", """{ "spell": { "targets": ["creature:you"], "effects": [{ "counters": 2, "what": "target" }] } }""", "Invented test text."));
        CastInOrder(s, charm);
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Theory]
    [InlineData(0, 2)] // no other cards in hand: the draw is one more
    [InlineData(1, 2)] // one card in hand: still one or fewer
    [InlineData(2, 1)] // two cards in hand: no extra card
    public async Task WithOneOrFewerCardsInHandYouDrawOneMore(int others, int drawn)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Riddler", "Creature — Sphinx", "{3}{U}{U}", RiddlerScript, "Flying", "4", "6"));
        var draw = s.InHand(P0, Scripted("Probe Draw", "Sorcery", "{0}", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "Draw a card."));
        bool trimmed = false, counting = false;
        int drawnAfter = 0;
        s.Game.EventRaised += e =>
        {
            if (e is SpellCast { Card: var c } && c == draw) counting = true;
            else if (counting && e is CardDrawn { Player: var p } && p == P0) drawnAfter++;
        };
        s.Attacker.Act = (_, legal) =>
        {
            if (!trimmed)
            {
                // The opening hand is drawn when the game starts: keep the spell and the chosen number of other cards, exile the rest.
                trimmed = true;
                var player = s.Game.State.GetPlayer(P0);
                var kept = player.Hand.Where(id => id != draw).Take(others).ToHashSet();
                foreach (var id in player.Hand.Where(id => id != draw && !kept.Contains(id)).ToList())
                {
                    player.Hand.Remove(id);
                    s.Card(id).Zone = Zone.Exile;
                    player.Exile.Add(id);
                }
            }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == draw) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(drawn, drawnAfter);
    }

    [Fact]
    public async Task TheFirstSpellEachTurnCanBeCastWithoutPayingItsManaCost()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Wanderer", "Enchantment", "{4}{U}{U}", WanderScript, "Invented test text."));
        var first = s.InHand(P0, Scripted("Probe Note", "Sorcery", "{2}", Note));
        var second = s.InHand(P0, Scripted("Probe Note Two", "Sorcery", "{2}", Note));
        CastInOrder(s, first, second);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellCast sc && sc.Card == first);
        Assert.DoesNotContain(s.Game.Log, e => e is SpellCast sc && sc.Card == second);
    }

    [Fact]
    public async Task WhenCastItsHandAndGraveyardGoIntoTheLibraryBeforeYouDrawSeven()
    {
        var s = new Scenario();
        var weft = s.InHand(P0, Scripted("Test Wanderer", "Enchantment", "{1}", WanderScript));
        s.Lands(P0, 1);
        var dead = InGraveyard(s, P0, Creature("Probe Dead", 1, 1));
        CastInOrder(s, weft);
        await s.RunUntilTurn();
        var player = s.Game.State.GetPlayer(P0);
        Assert.Empty(player.Graveyard);
        Assert.Equal(7, player.Hand.Count);
        Assert.NotEqual(Zone.Graveyard, s.Card(dead).Zone);
    }

    [Fact]
    public async Task ShuffleHandIntoLibraryLeavesTheGraveyardAlone()
    {
        var s = new Scenario();
        var shuffler = s.InHand(P0, Scripted("Test Shuffler", "Enchantment", "{1}", ShuffleHand));
        s.Lands(P0, 1);
        InGraveyard(s, P0, Creature("Probe Dead", 1, 1));
        CastInOrder(s, shuffler);
        await s.RunUntilTurn();
        var player = s.Game.State.GetPlayer(P0);
        Assert.Single(player.Graveyard);
        Assert.Equal(7, player.Hand.Count);
    }

    [Theory]
    [InlineData(3, 10)]  // below half the starting total: it becomes half, rounded up
    [InlineData(35, 10)] // above it: it drops to half
    public async Task AtYourEndStepLifeBecomesHalfTheStartingTotal(int life, int expected)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Keystone", "Legendary Artifact", "{7}", KeystoneScript, "Invented test text."));
        s.Game.State.GetPlayer(P0).Life = life;
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData(true, 1)]  // cast without mana: less than its mana value, so the counter draws a card
    [InlineData(false, 0)] // paid in full: nothing is drawn
    public async Task CounteringDrawsOnlyWhenLessManaWasSpentThanTheSpellsValue(bool free, int drawn)
    {
        var s = new Scenario();
        if (free) s.Add(P0, Scripted("Test Charm", "Enchantment", "{1}", """{ "castFromHandFreeUpTo": { "count": { "types": ["creature"] } } }""", "Invented test text."));
        else s.Lands(P0, 2);
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Creature("Cub", 1, 1));
        var spell = s.InHand(P0, Scripted("Probe Note", "Sorcery", "{1}{R}", Note));
        CastInOrder(s, spell);
        s.Lands(P1, 2);
        var unravel = s.InHand(P1, Scripted("Test Undo", "Instant", "{1}{R}", UndoScript, "Invented test text."));
        s.Defender.Act = (view, legal) => view.Stack.Count > 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == unravel) is { } cast ? cast : PassPriority.Instance;
        bool counting = false;
        int drawnByCounter = 0;
        s.Game.EventRaised += e =>
        {
            if (e is SpellCast { Card: var c } && c == unravel) counting = true;
            else if (counting && e is CardDrawn { Player: var p } && p == P1) drawnByCounter++;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone);
        Assert.Equal(drawn, drawnByCounter);
    }

    [Fact]
    public async Task AWarpedBladeModeSendsAWarpedCardInExileToTheBottomOfItsOwnersLibrary()
    {
        var s = new Scenario();
        var scout = InExile(s, P0, Scripted("Test Scout", "Creature — Scout", "{2}{R}", """{ "warp": "{R}" }""", "Invented test text.", "2", "2"));
        s.Lands(P0, 1);
        var blade = s.InHand(P0, Scripted("Test Blade", "Creature — Insect Assassin", "{1}", Blade, "Invented test text.", "3", "1"));
        CastInOrder(s, blade);
        s.Attacker.Modes = (_, _) => new[] { 1 };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(scout).Zone);
        Assert.Equal(scout, s.Game.State.GetPlayer(P0).Library.Last());
    }

    [Fact]
    public async Task TheBottomOfLibraryModeIsNotChosenWithoutAWarpCardInExile()
    {
        var s = new Scenario();
        var plain = InExile(s, P0, Creature("Test Plain Card", 1, 1));
        s.Lands(P0, 1);
        var blade = s.InHand(P0, Scripted("Test Blade", "Creature — Insect Assassin", "{1}", Blade, "", "3", "1"));
        CastInOrder(s, blade);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(blade).CounterCount(CounterKind.PlusOnePlusOne)); // the counters mode is the only one with a legal choice
        Assert.Equal(Zone.Exile, s.Card(plain).Zone);                             // the card without warp is not a legal target
    }

    [Fact]
    public async Task AStationCountsTheToughnessOfACreatureWhoseToughnessIsAbovePower()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Scripted("Test Cutter", "Artifact — Spacecraft", "{0}", """{ "abilities": [] }""", "Station", null, null, "Station"));
        var warden = s.Add(P0, Scripted("Test Keeper", "Artifact Creature — Robot Soldier", "{3}{G}", KeeperScript, "Invented test text.", "3", "4"));
        StationWith(s, ship, warden);
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(ship).CounterCount(CounterKind.Charge));
    }

    [Fact]
    public async Task ACreatureWithPowerEqualToToughnessStationsItsPower()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Scripted("Test Cutter", "Artifact — Spacecraft", "{0}", """{ "abilities": [] }""", "Station", null, null, "Station"));
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        StationWith(s, ship, ox);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(ship).CounterCount(CounterKind.Charge));
    }

    [Fact]
    public async Task AttackingWithASkirmisherMakesARobotThatIsSacrificedAtEndOfCombat()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Scripted("Test Skirmisher", "Artifact Creature — Robot Soldier", "{R}", SkirmisherScript, "Invented test text.", "2", "1"));
        await s.RunUntilTurn();
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life); // the attacker and its Robot each deal 2
        Assert.Equal(0, CountOn(s, P0, "Robot"));
    }

    [Fact]
    public async Task WithoutManaTheRobotIsNotCreated()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Skirmisher", "Artifact Creature — Robot Soldier", "{R}", SkirmisherScript, "", "2", "1"));
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(0, CountOn(s, P0, "Robot"));
    }

    [Fact]
    public async Task TheLanderIsSacrificedAtTheEndStepOfYourNextTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var kav = s.InHand(P0, Scripted("Test Explorer", "Creature — Kavu Soldier", "{3}{R}", ExplorerScript, "Invented test text.", "4", "3"));
        CastInOrder(s, kav);
        var landers = new Dictionary<int, int>();
        s.Game.EventRaised += e => { if (e is TurnBegan t) landers[t.TurnNumber] = CountOn(s, P0, "Lander"); };
        await s.RunUntilTurn(4);
        Assert.Equal(1, landers[2]); // kept through the opponent's turn
        Assert.Equal(1, landers[3]); // still there on your next turn until its end step
        Assert.Equal(0, landers[4]);
    }

    [Fact]
    public async Task ACreatureWithAWarpCostCanBeCastFromTheGraveyardWhenItHasWarpFromGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Swamp);
        var culler = InGraveyard(s, P0, Scripted("Test Revenant", "Creature — Warlock", "{B}{B}", """{ "warp": "{B}", "warpFromGraveyard": true }""", "Invented test text.", "2", "2"));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == culler); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(offered);
    }

    [Fact]
    public async Task ACardWithoutWarpFromGraveyardCannotBeCastFromTheGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Swamp);
        var culler = InGraveyard(s, P0, Scripted("Test Revenant", "Creature — Warlock", "{B}{B}", """{ "warp": "{B}" }""", "Invented test text.", "2", "2"));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == culler); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Theory]
    [InlineData(true, true)]   // with the grant, an artifact can be cast for its {2}{R} warp cost, which three Mountains pay
    [InlineData(false, false)] // without it, the {5} cost is out of reach
    public async Task ArtifactCardsInYourHandHaveWarpWhileTheGranterIsOut(bool grant, bool castable)
    {
        var s = new Scenario();
        if (grant) s.Add(P0, Scripted("Test Captain", "Legendary Creature — Kavu", "{2}{R}{R}", CaptainScript, "Invented test text.", "3", "5"));
        s.Lands(P0, 3);
        var gizmo = s.InHand(P0, Scripted("Test Gizmo", "Artifact", "{5}", """{ "abilities": [] }""", "Test gizmo."));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == gizmo); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(castable, offered);
    }

    // ------------------------------------------------------------------ choices, tokens and exile

    [Fact]
    public async Task TheFirstTokensEachTurnAreCopiesOfTheEnchantedPermanent()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, Scripted("Test Aura", "Enchantment — Aura", "{2}{U}", AuraScript, "Invented test text."), attachTo: bear);
        var sigil = """{ "spell": { "effects": [{ "tokens": 1, "token": { "name": "Thopter", "types": "Artifact Creature — Thopter", "power": 1, "toughness": 1 } }] } }""";
        var first = s.InHand(P0, Scripted("Probe Sigil", "Sorcery", "{0}", sigil));
        var second = s.InHand(P0, Scripted("Probe Sigil Two", "Sorcery", "{0}", sigil));
        CastInOrder(s, first, second);
        await s.RunUntilTurn();
        var tokens = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Controller == P0 && c.Definition.IsToken).ToList();
        Assert.Single(tokens.Where(c => c.Name == "Bear"));    // the first batch is a copy of the enchanted creature
        Assert.Single(tokens.Where(c => c.Name == "Thopter")); // the next batch is ordinary
    }

    [Fact]
    public async Task ItsPowerAndToughnessAreTheCardsYouOwnInExile()
    {
        var s = new Scenario();
        InExile(s, P0, Creature("Probe A", 1, 1));
        InExile(s, P0, Creature("Probe B", 1, 1));
        InExile(s, P1, Creature("Probe C", 1, 1));
        var lurker = s.Add(P0, Scripted("Test Lurker", "Creature — Test Lurker", "{B}{G}", LurkerScript, "Invented test text.", "0", "1"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(lurker).Power);
        Assert.Equal(3, s.Card(lurker).Toughness);
    }

    [Fact]
    public async Task RemovingPlusOneCountersFromTwoDifferentCreaturesDrawsAndMakesARobot()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Amalgam", "Legendary Artifact Creature — Construct", "{X}{G}{W}", AmalgamScript, "Trample", "0", "1"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var cub = s.Add(P0, Creature("Cub", 1, 1));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.Card(cub).Counters[CounterKind.PlusOnePlusOne] = 1;
        bool attacking = false;
        int drawn = 0;
        s.Game.EventRaised += e => { if (attacking && e is CardDrawn { Player: var p } && p == P0) drawn++; };
        s.Attacker.Attack = (_, attackers, defenders) => { attacking = true; return attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList(); };
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(cub).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(1, CountOn(s, P0, "Robot"));
        Assert.Equal(1, drawn);
    }

    [Theory]
    [InlineData(CounterKind.PlusOnePlusOne, 2, CounterKind.PlusOnePlusOne, 0)] // one creature has the counters: they must come from two creatures
    [InlineData(CounterKind.Charge, 1, CounterKind.Charge, 1)]                  // the counters are the wrong kind
    public async Task RemovingCountersNeedsTwoDifferentCreaturesWithTheRightKind(CounterKind bearKind, int bearCounters, CounterKind cubKind, int cubCounters)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Amalgam", "Legendary Artifact Creature — Construct", "{X}{G}{W}", AmalgamScript, "Trample", "0", "1"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var cub = s.Add(P0, Creature("Cub", 1, 1));
        s.Card(bear).Counters[bearKind] = bearCounters;
        s.Card(cub).Counters[cubKind] = cubCounters;
        await s.RunUntilTurn();
        Assert.Equal(bearCounters, s.Card(bear).CounterCount(bearKind));
        Assert.Equal(cubCounters, s.Card(cub).CounterCount(cubKind));
        Assert.Equal(0, CountOn(s, P0, "Robot"));
    }

    [Fact]
    public async Task ARemovedCounterCostTapsAnyArtifactOrCreatureInstead()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var chaplain = s.Add(P0, Scripted("Test Monk", "Creature — Monk", "{2}{W}", MonkScript, "Invented test text.", "2", "2"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 1;
        var ox = s.Add(P1, Creature("Ox", 3, 3));
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == chaplain) is { } ability) { used = true; return ability; }
            return PassPriority.Instance;
        };
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ox) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool oxTapped = false;
        s.Game.EventRaised += e => { if (e is PermanentTapped { Card: var c } && c == ox) oxTapped = true; };
        await s.RunUntilTurn();
        Assert.True(used);
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.True(oxTapped); // the Ox untaps in its controller's next untap step, so the tap is checked as it happens
    }

    [Theory]
    [InlineData(0, true)]  // odd: the mana value 1 creature is destroyed, the mana value 2 creature survives
    [InlineData(1, false)] // even: the other way round
    public async Task DestroysEachCreatureOfTheChosenManaValueParity(int option, bool oddDies)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var odd = s.Add(P0, Creature("Odd Ox", 2, 2));
        var even = s.Add(P1, Creature("Even Bear", 2, 2) with { ManaCost = ManaCost.Parse("{1}{1}") });
        var massacre = s.InHand(P0, Scripted("Test Purge", "Sorcery", "{1}", PurgeScript, "Invented test text."));
        CastInOrder(s, massacre);
        s.Attacker.Option = (_, _) => option;
        await s.RunUntilTurn();
        Assert.Equal(oddDies ? Zone.Graveyard : Zone.Battlefield, s.Card(odd).Zone);
        Assert.Equal(oddDies ? Zone.Battlefield : Zone.Graveyard, s.Card(even).Zone);
    }

    [Fact]
    public async Task ZeroPointLamentDestroysOnlyCreaturesOfToughnessXOrLessAndLosesXLife()
    {
        var s = new Scenario();
        for (int i = 0; i < 4; i++) s.Add(P0, GenericCards.Swamp);
        var wall = s.Add(P0, Creature("Wall", 1, 3));
        var bear = s.Add(P1, Creature("Big Bear", 1, 4));
        var ballad = s.InHand(P0, Scripted("Test Lament", "Sorcery", "{X}{B}", LamentScript, "Invented test text."));
        s.Attacker.Number = (_, _) => 3;
        CastInOrder(s, ballad);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(wall).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.Equal(17, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AtSixOrMoreZeroPointLamentReturnsTheCreatureItDestroyedUnderYourControl()
    {
        var s = new Scenario();
        for (int i = 0; i < 7; i++) s.Add(P0, GenericCards.Swamp);
        var wall = s.Add(P0, Creature("Wall", 1, 3));
        s.Add(P1, Creature("Big Bear", 1, 7));
        var ballad = s.InHand(P0, Scripted("Test Lament", "Sorcery", "{X}{B}", LamentScript, "Invented test text."));
        s.Attacker.Number = (_, _) => 6;
        CastInOrder(s, ballad);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(wall).Zone);
        Assert.Equal(P0, s.Card(wall).Controller);
        Assert.Equal(14, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task TheCageCardsExiledWithItGoToTheirGraveyardsAndYouGetARobotForEach()
    {
        var s = new Scenario();
        var star = s.Add(P0, Scripted("Test Cage", "Artifact", "{1}{W}{W}", CageScript, "Invented test text."));
        var first = InExile(s, P1, Creature("Ox", 1, 1));
        var second = InExile(s, P1, Creature("Yak", 2, 1));
        s.Card(first).ExiledWith = (star, s.Card(star).Version);
        s.Card(second).ExiledWith = (star, s.Card(star).Version);
        s.Lands(P0, 6);
        s.Add(P0, GenericCards.Plains);
        s.Add(P0, GenericCards.Plains);
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == star) is { } ability ? ability : PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(first).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(second).Zone);
        Assert.Equal(2, CountOn(s, P0, "Robot"));
        Assert.Equal(Zone.Graveyard, s.Card(star).Zone);
    }

    [Fact]
    public async Task AReanimatedCreatureIsASkeletonWithNoAbilities()
    {
        var s = new Scenario();
        var xu = s.Add(P0, Scripted("Test Medium", "Legendary Creature — Human Wizard", "{1}{B}{B}", MediumScript, "Invented test text.", "2", "3"));
        var ghost = InGraveyard(s, P0, Creature("Ghost", 2, 2, Keyword.Flying));
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == xu) is { } ability) { used = true; return ability; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        var card = s.Card(ghost);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal(P0, card.Controller);
        Assert.Contains("Skeleton", card.CurrentSubtypes);
        Assert.False(card.Has(Keyword.Flying));
    }

    [Fact]
    public async Task ArtifactManaCannotPayForASpellCastFromYourHand()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Adept", "Legendary Creature — Jellyfish Advisor", "{3}{U}{U}", AdeptScript, "Invented test text.", "3", "4"));
        s.Add(P0, Scripted("Test Relic", "Artifact", "{0}", """{ "abilities": [] }""", ""));
        var warper = s.InHand(P0, Scripted("Test Warper", "Creature — Warlock", "{U}{U}", """{ "warp": "{U}" }""", "", "2", "2"));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == warper); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task ArtifactManaPaysForASpellCastFromTheGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Adept", "Legendary Creature — Jellyfish Advisor", "{3}{U}{U}", AdeptScript, "", "3", "4"));
        s.Add(P0, Scripted("Test Relic", "Artifact", "{0}", """{ "abilities": [] }""", ""));
        var warper = InGraveyard(s, P0, Scripted("Test Warper", "Creature — Warlock", "{U}{U}", """{ "warp": "{U}", "warpFromGraveyard": true }""", "", "2", "2"));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == warper); return PassPriority.Instance; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(offered);
    }

    [Fact]
    public async Task ArtifactOnTopOfTheLibraryCanBeCastWithTheArtifactOnlyMana()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Adept", "Legendary Creature — Jellyfish Advisor", "{3}{U}{U}", AdeptScript, "", "3", "4"));
        s.Add(P0, Scripted("Test Relic", "Artifact", "{0}", """{ "abilities": [] }""", ""));
        var gadget = s.InHand(P0, Scripted("Test Gadget", "Artifact", "{U}", """{ "abilities": [] }""", ""));
        bool restacked = false, offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, gadget); }
            offered |= legal.OfType<CastSpell>().Any(c => c.Card == gadget);
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(offered);
    }

    [Theory]
    [InlineData(0, false)] // the card costs {1} more, and with no mana it stays in exile
    [InlineData(1, true)]  // one mana pays the extra {1}
    public async Task TheOpponentMayPlayTheExiledCardForOneMoreMana(int mountains, bool cast)
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Plains);
        var lightstall = s.InHand(P0, Scripted("Test Watcher", "Creature — Angel Wizard", "{W}", WatcherScript, "Invented test text.", "2", "1"));
        CastInOrder(s, lightstall);
        s.Lands(P1, mountains);
        var probe = s.InHand(P1, Scripted("Probe Note", "Instant", "{0}", Note));
        s.Defender.Choose = (_, request) => request.Options.Where(c => c.Id == probe).Select(c => c.Id).ToList();
        s.Defender.Act = (_, legal) => s.Card(probe).Zone == Zone.Exile && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == probe) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(cast, s.Game.Log.Any(e => e is SpellCast sc && sc.Card == probe));
    }

    [Fact]
    public async Task ALandPlayedFromExileThisWayEntersTapped()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Plains);
        var lightstall = s.InHand(P0, Scripted("Test Watcher", "Creature — Angel Wizard", "{W}", WatcherScript, "", "2", "1"));
        CastInOrder(s, lightstall);
        var land = s.InHand(P1, GenericCards.Forest);
        s.Defender.Choose = (_, request) => request.Options.Where(c => c.Id == land).Select(c => c.Id).ToList();
        s.Defender.Act = (_, legal) => legal.OfType<PlayLand>().FirstOrDefault(p => p.Card == land) is { } play ? play : PassPriority.Instance;
        await s.RunUntilTurn(3);
        Assert.Equal(Zone.Battlefield, s.Card(land).Zone);
        Assert.True(s.Card(land).Tapped);
    }

    [Fact]
    public async Task AWardCostDiscardsARandomCardInsteadOfLettingTheTargetingPlayerChoose()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Lancer", "Legendary Creature — Human Cleric", "{3}{B}{B}", LancerScript, "Ward—Discard a card at random.", "3", "3"));
        s.Lands(P1, 1);
        var nudge = s.InHand(P1, Scripted("Test Nudge", "Instant", "{1}", """{ "spell": { "targets": ["creature"], "effects": [{ "pump": [0, 0], "what": "target" }] } }""", "Target creature."));
        int discardAsked = 0;
        s.Defender.Discard = (view, count) => { discardAsked++; return view.Self.Hand.TakeLast(count).Select(c => c.Id).ToList(); };
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == nudge) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(0, discardAsked);
        Assert.Equal(Zone.Graveyard, s.Card(nudge).Zone);
    }
}
