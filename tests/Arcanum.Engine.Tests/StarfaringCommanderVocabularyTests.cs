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
/// Card-script vocabulary for extra combat phases, crime triggers, proliferate variants, phasing out, equip and activation costs
/// paid with counters, sacrifice-linked exile play, discard choices, card-type and parity choices, copies and sunburst.
/// The cards are invented.
/// </summary>
public class StarfaringCommanderVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"starfaring-commander-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
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

    /// <summary>Puts a card from the owner's hand into the owner's exile, as if an effect had exiled it.</summary>
    private static CardId InExile(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        s.Card(id).Zone = Zone.Exile;
        player.Exile.Add(id);
        return id;
    }

    /// <summary>Player 0 activates the first ability of <paramref name="source"/> once, when it is offered.</summary>
    private static void ActivateOnce(Scenario s, CardId source)
    {
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == source) is { } ability) { used = true; return ability; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    // ------------------------------------------------------------------ scripts under test (invented wording)

    private const string ChargerScript = """{ "abilities": [{ "static": { "affects": "creatures:you", "pump": ["affectedAttacksThisTurn", 0] }, "text": "invented" }, { "trigger": "landfall", "effects": [{ "additionalCombatAfterThisMain": true, "atItsBeginning": { "effects": [{ "untap": { "each": { "types": ["creature"], "controller": "you" } } }], "text": "invented" } }], "text": "invented", "filter": { "types": ["land"] }, "if": "yourMainPhase" }] }""";

    private const string ClimberScript = """{ "entersWithCounters": { "graveyard": { "types": ["land"] }, "of": "everyone" }, "abilities": [{ "trigger": "cardPutIntoAnyGraveyard", "fromGraveyard": true, "filter": { "types": ["land"], "controller": "any" }, "effects": [{ "mayPay": "Pay {G}{G}?", "mana": "{G}{G}", "effects": [{ "bounce": "self" }] }], "text": "invented" }] }""";

    private const string KeyScript = """{ "chooseOnEnter": "cardType", "chooseCardTypeFrom": ["artifact", "creature", "enchantment", "instant", "sorcery"], "abilities": [{ "spellCost": { "spells": { "chosenCardType": true }, "amount": 1 }, "text": "invented" }] }""";

    private const string CopycatScript = """{ "entersAsCopy": { "types": ["artifact", "creature"] }, "entersAsCopyAddTypes": ["artifact"] }""";

    private const string ArtilleryScript = """{ "abilities": [{ "trigger": "castSpell", "effects": [{ "gainSunburst": "triggered" }], "text": "invented", "filter": { "allTypes": ["artifact", "creature"] } }, { "trigger": "endStep", "effects": [{ "damage": 10, "to": "opponents" }], "text": "invented", "if": { "atLeast": { "countersAmong": { "types": ["artifact", "creature"], "controller": "you" }, "kind": "any" }, "value": 30 } }] }""";

    private const string GuardScript = """{ "entersWithCounters": 2, "abilities": [{ "trigger": "opponentCommitsCrime", "effects": [{ "proliferate": true }], "text": "invented" }] }""";

    private const string HexScript = """{ "spell": { "targets": ["creature"], "effects": [{ "pump": [0, 0], "what": "target" }] } }""";

    private const string SageScript = """{ "proliferateTwice": true, "abilities": [{ "cost": "{1}{U/P}{U/P}, removeCountersFromAmong:3:artifact|creature|planeswalker:other", "effects": [{ "counters": 1, "kind": "indestructible", "what": "self" }], "text": "invented" }] }""";

    private const string SpreadScript = """{ "spell": { "effects": [{ "proliferate": true }], "text": "invented" } }""";

    private const string RippleScript = """{ "spell": { "effects": [{ "proliferate": true, "thenPhaseOutChosen": true }], "text": "invented" } }""";

    private const string HammerScript = """{ "abilities": [{ "trigger": "beginCombat", "effects": [{ "counters": 1, "kind": "charge", "what": "self" }], "text": "invented" }, { "static": { "affects": "equipped", "pump": [{ "counters": "any" }, { "counters": "any" }] }, "text": "invented" }, { "static": { "affects": "equipped", "pump": [0, 0], "keywords": ["Double strike"], "while": { "counters": 4, "kind": "any" } }, "text": "invented" }, { "cost": "{3}", "costOptions": [{ "cost": "{3}", "label": "Pay {3}" }, { "cost": "removeCounterFrom:self", "label": "Invented test text." }], "sorcery": true, "equip": true, "targets": ["creature:you"], "effects": [{ "attach": "target" }], "text": "invented" }] }""";

    private const string BrusherScript = """{ "castExiledWithThis": { "filter": {}, "yourTurn": true, "play": true, "while": { "sacrificedThisTurn": { "token": false } } }, "abilities": [{ "trigger": "youSacrifice", "filter": { "token": false, "controller": "any" }, "effects": [{ "exileTop": 1, "linked": true }], "text": "invented" }] }""";

    private const string WasterScript = """{ "abilities": [{ "trigger": "attacks", "effects": [{ "discardOrSacrifice": ["you", "defendingPlayer"] }, { "draw": { "putIntoGraveyardThisWay": { "types": ["land"] } } }], "text": "invented" }] }""";

    private const string SatyrScript = """{ "abilities": [{ "trigger": "enters", "effects": [{ "discardAnyNumber": true, "filter": { "types": ["land"] } }, { "whenYouDo": { "if": { "atLeast": { "discarded": {} }, "value": 1 }, "amount": { "discarded": {} }, "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["artifact", "creature", "enchantment", "planeswalker", "battle"], "not": ["land"] }, "optional": true, "upTo": "triggerAmount", "text": "invented" }], "effects": [{ "bounce": "eachTarget" }], "text": "invented" } }], "text": "invented" }] }""";

    private const string WorksScript = """{ "abilities": [{ "cost": "{2}, {T}, removeXCountersFrom:artifact|creature", "sorcery": true, "text": "invented", "modes": [{ "text": "invented", "targets": ["artifact"], "effects": [{ "counters": "X", "kind": "charge", "what": "target" }] }, { "text": "invented", "targets": ["creature"], "effects": [{ "counters": "X", "what": "target" }] }] }] }""";

    // ------------------------------------------------------------------ combat, crime and proliferate

    [Fact]
    public async Task AMainPhaseLandAddsAnAdditionalCombatWhoseBeginningUntapsYourCreatures()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Charger", "Legendary Creature — Minotaur Warrior", "{4}{R}{R}", ChargerScript, "Invented test text.", "6", "6"));
        int combats = 0;
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.BeginCombat }) combats++; };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, combats); // the Forest played in the first main phase adds a combat after it
    }

    [Fact]
    public async Task EachCreatureGetsAPowerBonusForEachTimeItHasAttackedThisTurn()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Charger", "Legendary Creature — Minotaur Warrior", "{4}{R}{R}", ChargerScript, "", "6", "6"));
        var watch = s.Add(P0, Creature("Watch", 2, 2, Keyword.Vigilance)); // stays untapped, so it attacks in both combats
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == watch).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        // First attack in the additional combat: +1/+0 (3 damage); second attack in the regular combat: +2/+0 (4 damage).
        Assert.Equal(13, s.Game.State.GetPlayer(P1).Life);
    }

    [Theory]
    [InlineData(true, Zone.Hand)]      // the land put into a graveyard: GG is paid and the card returns to hand
    [InlineData(false, Zone.Graveyard)]
    public async Task ALandPutIntoAnyGraveyardOffersToReturnThisCardForGG(bool pay, Zone expected)
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Mountain);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        s.Add(P1, GenericCards.Forest);
        var crasher = InGraveyard(s, P0, Scripted("Test Climber", "Creature — Plant Centaur", "{3}{G}", ClimberScript, "Invented test text.", "1", "1"));
        var sweep = s.InHand(P0, Scripted("Test Sweep", "Sorcery", "{R}", """{ "spell": { "targets": ["land:opponent"], "effects": [{ "destroy": "target" }] } }""", "Invented test text."));
        CastInOrder(s, sweep);
        s.Attacker.YesNo = (_, _) => pay;
        await s.RunUntilTurn();
        // Cleanup discards down to hand size, so check the move back to hand rather than where the card ends the turn.
        bool returned = s.Game.Log.Any(e => e is CardMoved { Card: var c, From: Zone.Graveyard, To: Zone.Hand } && c == crasher);
        Assert.Equal(expected == Zone.Hand, returned);
    }

    [Theory]
    [InlineData(true, 3)]  // the opponent commits a crime by targeting your creature: proliferate adds a counter
    [InlineData(false, 2)] // you target your own creature: no crime
    public async Task AnOpponentsCrimeMakesYouProliferate(bool opponentCasts, int expected)
    {
        var s = new Scenario();
        var peacemaker = s.Add(P0, Scripted("Test Guard", "Artifact Creature — Robot Soldier", "{2}{W}", GuardScript, "Invented test text.", "0", "0"));
        s.Card(peacemaker).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Attacker.Choose = (_, request) => request.Options.Select(c => c.Id).ToList();
        var jinx = s.InHand(opponentCasts ? P1 : P0, Scripted("Test Hex", "Instant", "{R}", HexScript, "Target creature."));
        if (opponentCasts)
        {
            s.Lands(P1, 1);
            s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == jinx) is { } cast ? cast : PassPriority.Instance;
            s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        }
        else
        {
            s.Lands(P0, 1);
            CastInOrder(s, jinx);
        }
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(peacemaker).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Theory]
    [InlineData(true, 4)]  // with the doubler, the proliferate happens twice: 2 + 2
    [InlineData(false, 3)] // without it, once
    public async Task IfYouWouldProliferateYouProliferateTwice(bool doubler, int expected)
    {
        var s = new Scenario();
        if (doubler) s.Add(P0, Scripted("Test Sage", "Legendary Creature — Test Horror", "{2}{U}{U}", SageScript, "Invented test text.", "3", "5"));
        var peacemaker = s.Add(P0, Scripted("Test Guard", "Artifact Creature — Robot Soldier", "{2}{W}", GuardScript, "", "0", "0"));
        s.Card(peacemaker).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Attacker.Choose = (_, request) => request.Options.Select(c => c.Id).ToList();
        var spread = s.InHand(P0, Scripted("Probe Spreader", "Sorcery", "{0}", SpreadScript, "Proliferate."));
        CastInOrder(s, spread);
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(peacemaker).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task RemovingThreeCountersFromAmongOthersPutsAnIndestructibleCounterOnIt()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var tekuthal = s.Add(P0, Scripted("Test Sage", "Legendary Creature — Test Horror", "{2}{U}{U}", SageScript, "", "3", "5"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 2;
        var cub = s.Add(P0, Creature("Cub", 1, 1));
        s.Card(cub).Counters[CounterKind.PlusOnePlusOne] = 1;
        ActivateOnce(s, tekuthal);
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(tekuthal).CounterCount(CounterKind.Indestructible));
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(cub).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ProliferateThenAChosenPermanentThatGotACounterPhasesOut()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, GenericCards.Island);
        var peacemaker = s.Add(P0, Scripted("Test Guard", "Artifact Creature — Robot Soldier", "{2}{W}", GuardScript, "", "0", "0"));
        s.Card(peacemaker).Counters[CounterKind.PlusOnePlusOne] = 2;
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var ripples = s.InHand(P0, Scripted("Test Ripple", "Instant", "{1}{U}", RippleScript, "Invented test text."));
        s.Attacker.Choose = (_, request) => request.Options.Select(c => c.Id).ToList();
        CastInOrder(s, ripples);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.State.PhasedOut, p => p.Card == peacemaker);
        Assert.DoesNotContain(s.Game.State.PhasedOut, p => p.Card == bear);
    }

    [Fact]
    public async Task ChoosingNoneAfterProliferatingPhasesNothingOut()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, GenericCards.Island);
        var peacemaker = s.Add(P0, Scripted("Test Guard", "Artifact Creature — Robot Soldier", "{2}{W}", GuardScript, "", "0", "0"));
        s.Card(peacemaker).Counters[CounterKind.PlusOnePlusOne] = 2;
        var ripples = s.InHand(P0, Scripted("Test Ripple", "Instant", "{1}{U}", RippleScript, ""));
        s.Attacker.Choose = (_, request) =>
        {
            if (request.Prompt.Contains("phase out")) return new List<CardId>();
            return request.Options.Select(c => c.Id).ToList();
        };
        CastInOrder(s, ripples);
        await s.RunUntilTurn();
        Assert.DoesNotContain(s.Game.State.PhasedOut, p => p.Card == peacemaker);
    }

    // ------------------------------------------------------------------ choices and copies

    [Theory]
    [InlineData(1, "Creature", true)]    // the Key chooses creature: a creature spell costs {1} less and is castable with two Mountains
    [InlineData(1, "Sorcery", false)]    // a sorcery is not reduced by the creature choice
    [InlineData(4, "Sorcery", true)]     // the sorcery choice reduces the sorcery
    public async Task AsItEntersChooseACardTypeWhoseSpellsCostLess(int option, string spellType, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var key = s.InHand(P0, Scripted("Test Key", "Artifact", "{2}", KeyScript, "Invented test text."));
        var spell = spellType == "Creature"
            ? s.InHand(P0, Scripted("Probe Bear", "Creature — Bear", "{3}", """{ "abilities": [] }""", "", "1", "1"))
            : s.InHand(P0, Scripted("Probe Note", "Sorcery", "{3}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", ""));
        s.Attacker.Option = (_, _) => option;
        CastInOrder(s, key, spell);
        await s.RunUntilTurn();
        Assert.Equal(castable, s.Game.Log.Any(e => e is SpellCast sc && sc.Card == spell));
    }

    [Fact]
    public async Task ACopyOfACreatureEntersAsAnArtifactCreatureToo()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var ox = s.Add(P1, Creature("Test Ox", 3, 3));
        var meta = s.InHand(P0, Scripted("Test Copycat", "Artifact Creature — Shapeshifter", "{2}", CopycatScript, "Invented test text.", "0", "0"));
        s.Attacker.Choose = (_, request) => request.Options.Where(c => c.Id == ox).Select(c => c.Id).ToList();
        CastInOrder(s, meta);
        await s.RunUntilTurn();
        var copy = s.Card(meta);
        Assert.Equal(Zone.Battlefield, copy.Zone);
        Assert.True(copy.Types.HasFlag(CardType.Artifact));
        Assert.True(copy.IsCreature);
        Assert.Equal(3, copy.Power);
    }

    [Theory]
    [InlineData("Artifact Creature — Golem", 2)] // an artifact creature spell gains sunburst: one counter per color spent (red and green)
    [InlineData("Creature — Bear", 0)]           // other creature spells do not
    public async Task AnArtifactCreatureSpellGainsSunburst(string typeLine, int counters)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Artillery", "Artifact", "{4}", ArtilleryScript, "Invented test text."));
        s.Add(P0, GenericCards.Mountain);
        s.Add(P0, GenericCards.Forest);
        var spell = s.InHand(P0, Scripted("Test Golem", typeLine, "{R}{G}", """{ "abilities": [] }""", "", "2", "2"));
        CastInOrder(s, spell);
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(spell).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ costs, equipment and the exile play

    [Theory]
    [InlineData(1, 1)] // a counter in the first main phase: the equip removes it (the combat counter then makes one again)
    [InlineData(0, 0)] // no counter in the first main phase: the equip waits for the combat counter, then removes it in the second main phase
    public async Task EquipCanBePaidByRemovingACounterFromTheEquipment(int counters, int countersAtEnd)
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var gavel = s.Add(P0, Scripted("Test Hammer", "Artifact — Equipment", "{2}", HammerScript, "Invented test text.", null, null));
        s.Card(gavel).Counters[CounterKind.Charge] = counters;
        ActivateOnce(s, gavel);
        await s.RunUntilTurn();
        Assert.Equal(bear, s.Card(gavel).AttachedTo);
        Assert.Equal(countersAtEnd, s.Card(gavel).CounterCount(CounterKind.Charge));
    }

    [Theory]
    [InlineData(true, true)]   // you sacrificed a nontoken permanent this turn: the card exiled with the creature can be played
    [InlineData(false, false)] // without a sacrifice it can't
    public async Task CardsExiledWithItCanBePlayedAfterYouSacrificeOnYourTurn(bool sacrifice, bool playable)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Brusher", "Creature — Insect Warrior", "{2}{R}", BrusherScript, "Invented test text.", "2", "2"));
        var idol = s.Add(P0, Scripted("Test Idol", "Artifact", "{0}", """{ "abilities": [{ "cost": "{T}, sacrifice", "effects": [{ "gainLife": 1 }], "text": "invented" }] }""", "Invented test text."));
        bool offered = false, used = false;
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<PlayLand>().Any(p => s.Card(p.Card).Zone == Zone.Exile);
            if (sacrifice && !used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == idol) is { } ability) { used = true; return ability; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(playable, offered);
    }

    [Fact]
    public async Task AttackingMakesYouAndTheDefendingPlayerDiscardOrSacrificeAndYouDrawPerLand()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Test Waster", "Creature — Insect Cleric", "{2}{B}{B}", WasterScript, "Invented test text.", "4", "4"));
        bool attacking = false;
        int drawn = 0;
        s.Game.EventRaised += e => { if (attacking && e is CardDrawn { Player: var p } && p == P0) drawn++; };
        s.Attacker.Attack = (_, attackers, defenders) => { attacking = true; return attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList(); };
        await s.RunUntilTurn();
        Assert.Single(s.Game.State.GetPlayer(P0).Graveyard); // you discarded a land
        Assert.Single(s.Game.State.GetPlayer(P1).Graveyard); // the defending player discarded a land
        Assert.Equal(2, drawn);                                       // one card for each land put into a graveyard
    }

    [Theory]
    [InlineData(1, 1)] // one land discarded: one nonland permanent card comes back
    [InlineData(0, 0)] // none discarded: none comes back
    public async Task DiscardingLandsReturnsUpToThatManyNonlandPermanentCards(int discard, int returned)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var first = InGraveyard(s, P0, Creature("Probe Bear", 2, 2));
        var second = InGraveyard(s, P0, Creature("Probe Cub", 1, 1));
        var faun = s.InHand(P0, Scripted("Test Satyr", "Creature — Satyr Druid", "{1}", SatyrScript, "Invented test text.", "3", "3"));
        s.Attacker.Choose = (_, request) => request.Options.Take(discard).Select(c => c.Id).ToList();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(first) };
        CastInOrder(s, faun);
        await s.RunUntilTurn();
        int inHand = new[] { first, second }.Count(id => s.Card(id).Zone == Zone.Hand);
        Assert.Equal(returned, inHand);
    }

    [Fact]
    public async Task RemovingXCountersFromACreatureFundsCharge()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var refinery = s.Add(P0, Scripted("Test Works", "Artifact", "{2}", WorksScript, "", null, null));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 3;
        ActivateOnce(s, refinery);
        s.Attacker.Modes = (_, _) => new[] { 0 };
        s.Attacker.Number = (_, _) => 2;
        s.Attacker.Choose = (_, request) => request.Options.Where(c => c.Id == bear).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(2, s.Card(refinery).CounterCount(CounterKind.Charge));
    }
}
