// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
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
/// Vocabulary for effects during resolution (surveil to hand, exile the top card and maybe cast it, the owner's
/// top-or-bottom choice, token copies of each creature), emblems until your next turn, the remembered and
/// toughness-above-power filters, and the planeswalker-types and opponents-who-discarded-fewer quantities, plus
/// the attacks-on-players-only trigger.
/// </summary>
public class ResolutionVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"rules-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().LastOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    /// <summary>Puts a card from hand on top of player 0's library at its first decision, once the game has dealt the hands.</summary>
    private static void TopOfLibraryFirst(Scenario s, CardId top)
    {
        var act = s.Attacker.Act;
        bool placed = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (!placed) { placed = true; s.Restack(P0, top); }
            return act(view, legal);
        };
    }

    /// <summary>A planeswalker with three loyalty counters and one subtype.</summary>
    private static CardId Walker(Scenario s, PlayerId owner, string subtype)
    {
        var walker = s.Add(owner, new CardDefinition { Name = "Tester Sage", Types = CardType.Planeswalker, Subtypes = new[] { subtype }, Loyalty = 3 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 3; // without loyalty counters the walker dies as a state-based action
        return walker;
    }

    /// <summary>The controller puts every card surveilled into the graveyard (the default keeps them on top).</summary>
    private static void PutEverySurveilledCardInGraveyard(Scenario s) =>
        s.Attacker.Choose = (_, request) => request.Purpose == CardChoicePurpose.SurveilToGraveyard
            ? request.Options.Select(c => c.Id).ToList()
            : request.Options.Take(request.Min).Select(c => c.Id).ToList();

    [Fact]
    public async Task SurveilledNonlandCardGoesToHandWhenTheControllerPutsItInTheGraveyard()
    {
        var s = Casting();
        s.Lands(P0, 1);
        PutEverySurveilledCardInGraveyard(s);
        var top = s.InHand(P0, Creature("Top Bear", 1, 1));
        TopOfLibraryFirst(s, top);
        s.InHand(P0, Scripted("Probe Note", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "surveil": 1 }, { "surveilledToHand": { "not": ["land"] } }] } }"""));
        await s.RunUntilTurn();
        // Back in hand (the oversized test hand may discard it again at cleanup).
        Assert.Contains(s.Game.Log, e => e is CardMoved { From: Zone.Graveyard, To: Zone.Hand } m && m.Card == top);
    }

    [Fact]
    public async Task SurveilledLandStaysInTheGraveyardWhenItIsNotANonlandCard()
    {
        var s = Casting();
        s.Lands(P0, 1);
        PutEverySurveilledCardInGraveyard(s);
        var top = s.InHand(P0, Arcanum.Cards.GenericCards.Mountain);
        TopOfLibraryFirst(s, top);
        s.InHand(P0, Scripted("Probe Note", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "surveil": 1 }, { "surveilledToHand": { "not": ["land"] } }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(top).Zone);
    }

    [Theory]
    [InlineData("{1}", 1, Zone.Hand)]
    [InlineData("{1}{R}", 1, Zone.Graveyard)] // mana value 2 is more than 1: it stays in the graveyard
    public async Task SurveilledToHandMaxManaValueLimitsWhichCardsMove(string manaCost, int limit, Zone zone)
    {
        var s = Casting();
        s.Lands(P0, 1);
        PutEverySurveilledCardInGraveyard(s);
        var top = s.InHand(P0, Creature("Top Bear", 1, 1) with { ManaCost = ManaCost.Parse(manaCost) });
        TopOfLibraryFirst(s, top);
        s.InHand(P0, Scripted("Probe Note", "Sorcery", "{R}",
            $$"""{ "spell": { "effects": [{ "surveil": 1 }, { "surveilledToHand": { "not": ["land"] }, "maxManaValue": {{limit}} }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(zone == Zone.Hand, s.Game.Log.Any(e => e is CardMoved { From: Zone.Graveyard, To: Zone.Hand } m && m.Card == top));
    }

    [Fact]
    public async Task ExiledTopLandStaysExiledAndTheOpponentTakesTheDamage()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var top = s.InHand(P0, Arcanum.Cards.GenericCards.Mountain);
        TopOfLibraryFirst(s, top);
        s.InHand(P0, Scripted("Dig Burn", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "exileTopMayCast": true, "ifNot": [{ "damage": 2, "to": "opponents" }] }] } }"""));
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(top).Zone);
        Assert.Equal(life - 2, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ExiledTopSpellIsCastWhenTheControllerSaysYes()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var top = s.InHand(P0, Creature("Quick Bear", 1, 1));
        TopOfLibraryFirst(s, top);
        s.InHand(P0, Scripted("Dig Burn", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "exileTopMayCast": true, "ifNot": [{ "damage": 2, "to": "opponents" }] }] } }"""));
        var cast = new List<CardId>();
        s.Game.EventRaised += e => { if (e is SpellCast sc) cast.Add(sc.Card); };
        s.Defender.YesNo = (_, _) => true;
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Contains(top, cast);
        Assert.Equal(Zone.Battlefield, s.Card(top).Zone);
        Assert.Equal(life, s.Game.State.GetPlayer(P1).Life);
    }

    [Theory]
    [InlineData(true, 2)]  // the owner puts it on top: the owner takes the damage
    [InlineData(false, 0)] // the owner puts it on the bottom: no damage
    public async Task OwnerChoosesTopOrBottomOfTheLibraryForTheTargetPermanent(bool onTop, int damage)
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Owned Bear", 2, 2));
        s.Defender.YesNo = (_, _) => onTop;
        s.InHand(P0, Scripted("Tuck Away", "Sorcery", "{R}",
            """{ "spell": { "targets": ["creature"], "effects": [{ "ownerMayPutOnTop": "target", "ifTop": [{ "damage": 2, "to": "targetOwner" }] }] } }"""));
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        var library = s.Game.State.GetPlayer(P1).Library;
        Assert.Equal(bear, onTop ? library[0] : library[^1]);
        Assert.Equal(life - damage, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task EachCreatureOfTheTargetPlayerBecomesATokenCopyWithHaste()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Bear", 2, 2));
        s.Add(P1, Creature("Cub", 1, 1));
        s.InHand(P0, Scripted("Twin Echo", "Sorcery", "{R}",
            """{ "spell": { "targets": ["opponent"], "effects": [{ "copy": { "each": { "types": ["creature"], "controller": "any" }, "controlledBy": "target" }, "each": true, "haste": true }] } }"""));
        await s.RunUntilTurn();
        var copies = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Controller == P0 && c.Name is ("Bear" or "Cub")).ToList();
        Assert.Equal(new[] { "Bear", "Cub" }, copies.Select(c => c.Name).OrderBy(n => n));
        Assert.All(copies, c => Assert.True(c.Has(Keyword.Haste)));
    }

    [Theory]
    [InlineData(2, 1)] // the opponent's turn: the emblem is still there
    [InlineData(3, 0)] // the owner's next turn has begun: it's gone
    public async Task EmblemUntilYourNextTurnEndsWhenItsOwnersNextTurnBegins(int turn, int emblems)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Oath Call", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "emblem": "Oath Call", "untilYourNextTurn": true, "abilities": [{ "trigger": "creatureAttacks", "effects": [{ "gainLife": 1 }], "text": "x" }] }] } }"""));
        await s.RunUntilTurn(turn);
        Assert.Equal(emblems, s.Game.State.Emblems.Count);
    }

    [Fact]
    public async Task RememberedFilterLetsOnlyTheCreatureThatMadeTheEmblemTriggerOnAttack()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Cub", 1, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.InHand(P0, Scripted("Oath Bear", "Creature — Bear", "{1}",
            """{ "abilities": [{ "static": { "affects": "self", "keywords": ["Haste"] }, "text": "x" }, { "trigger": "enters", "effects": [{ "emblem": "Oath Mark", "about": "self", "abilities": [{ "trigger": "creatureAttacks", "filter": { "remembered": true }, "effects": [{ "gainLife": 1 }], "text": "x" }] }], "text": "x" }] }""",
            power: "2", toughness: "2"));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life); // the bear attacks and triggers; the cub does not
    }

    [Fact]
    public async Task ToughnessAbovePowerFilterGrantsTheKeywordOnlyToCreaturesWithMoreToughness()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Sturdy Lord", "Enchantment", "{1}",
            """{ "abilities": [{ "static": { "affects": "creatures:you", "filter": { "toughnessAbovePower": true }, "keywords": ["Flying"] }, "text": "x" }] }"""));
        var wall = s.Add(P0, Creature("Wall", 1, 3));
        var striker = s.Add(P0, Creature("Striker", 3, 1));
        await s.RunUntilTurn();
        Assert.True(s.Card(wall).Has(Keyword.Flying));
        Assert.False(s.Card(striker).Has(Keyword.Flying));
    }

    [Fact]
    public async Task PlaneswalkerTypesCountsTheDistinctSubtypesOfYourPlaneswalkers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        Walker(s, P0, "Tester");
        Walker(s, P0, "Tester");
        Walker(s, P0, "Sage");
        Walker(s, P1, "Rival"); // an opponent's planeswalker doesn't count
        s.InHand(P0, Scripted("Walker Tally", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": "planeswalkerTypes" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life); // Tester and Sage
    }

    [Fact]
    public async Task OpponentsWhoDiscardedFewerCountsTheOpponentsUnderTheLimitForTheFilter()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var first = s.InHand(P1, Arcanum.Cards.GenericCards.Mountain);
        var second = s.InHand(P1, Arcanum.Cards.GenericCards.Mountain);
        s.Defender.Discard = (_, _) => new[] { first, second };
        s.InHand(P0, Scripted("Pressure Note", "Sorcery", "{R}",
            """{ "spell": { "effects": [{ "discard": 2, "who": "opponents" }, { "gainLife": { "opponentsWhoDiscardedFewer": 2, "filter": { "not": ["land"] } } }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life); // the opponent discarded two lands, no nonland cards: fewer than two
    }

    [Theory]
    [InlineData(true, 1)]  // an attack on the player: the trigger
    [InlineData(false, 0)] // an attack on the opponent's planeswalker: no trigger with "playersOnly"
    public async Task CreaturesAttackOpponentPlayersOnlyIgnoresAttacksOnPlaneswalkers(bool attackPlayer, int lifeGained)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Lookout", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "creaturesAttackOpponent", "playersOnly": true, "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var walker = Walker(s, P1, "Tester");
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == bear)
            .Select(a => attackPlayer ? new AttackDeclaration(a, defenders[0]) : new AttackDeclaration(a, defenders[0], walker))
            .ToList();
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + lifeGained, s.Game.State.GetPlayer(P0).Life);
    }
}
