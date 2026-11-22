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
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Script vocabulary of a second set of invented cards: subjects that any player may use an ability, counters per artifact mana
/// spent, protection from colors outside a commander's identity, taking milled cards, damage-counted tokens, myriad with a legendary
/// creature in a three-player game, modes with different targets, moving counters, riot for nontoken creatures, escape granted to
/// the graveyard, skipped extra turns, poison counters placed by a spell, paying life in rounds, and draws that trigger twice.
/// The cards are invented.
/// </summary>
public class SetVocabulary2Tests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"vocab2-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
            ["loyalty"] = typeLine.Contains("Planeswalker") ? "5" : null,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private static void Play(Scenario s, Func<IReadOnlyList<PlayerAction>, PlayerAction?> step)
    {
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && step(legal) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Func<IReadOnlyList<PlayerAction>, PlayerAction?> Cast(Scenario s, string name) =>
        legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);

    [Fact]
    public async Task AnAnyPlayerAbilityIsOfferedToTheOpponentWithPriority()
    {
        var pulse = Make("Shared Pulse", "Creature — Golem", """{ "abilities": [{ "cost": "{1}", "anyPlayer": true, "effects": [{ "gainLife": 1 }] }] }""", "{1}", "2/2");
        var s = new Scenario();
        s.Lands(P1, 1);
        var card = s.Add(P0, pulse);
        bool offered = false;
        s.Defender.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == card);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(offered);
    }

    [Fact]
    public async Task AnAbilityWithoutAnyPlayerIsNotOfferedToTheOpponent()
    {
        var pulse = Make("Shared Pulse", "Creature — Golem", """{ "abilities": [{ "cost": "{1}", "effects": [{ "gainLife": 1 }] }] }""", "{1}", "2/2");
        var s = new Scenario();
        s.Lands(P1, 1);
        var card = s.Add(P0, pulse);
        bool offered = false;
        s.Defender.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == card);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Theory]
    [InlineData(2, 0, 2)] // two artifact mana spent: two counters
    [InlineData(1, 1, 1)] // one of each kind of mana: one counter
    [InlineData(0, 2, 0)] // land mana only: none
    public async Task ACreatureEntersWithOneCounterForEachArtifactManaSpentWhileTheCardIsOut(int stones, int lands, int counters)
    {
        var ledger = Make("Counting Ledger", "Artifact", """{ "countersPerArtifactManaSpent": true }""", "{0}");
        var stone = Make("Ore Stone", "Artifact", """{ "tapForMana": "{C}" }""", "{0}");
        var grower = Make("Test Grower", "Creature — Beast", "{}", "{2}", "1/1");
        var s = new Scenario();
        s.Lands(P0, lands);
        for (int i = 0; i < stones; i++) s.Add(P0, stone);
        s.Add(P0, ledger);
        var card = s.InHand(P0, grower);
        Play(s, Cast(s, "Test Grower"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(card).Zone);
        Assert.Equal(counters, s.Card(card).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Theory]
    [InlineData(true, false)] // every color is protected against: the red spell has no legal target
    [InlineData(false, true)] // without the card the creature can be targeted
    public async Task WithNoCommanderACreatureYouControlHasProtectionFromEveryColor(bool withSigil, bool zapOffered)
    {
        var sigil = Make("Warding Sigil", "Enchantment", """{ "abilities": [{ "static": { "affects": "creatures:you", "protectionFromColorsOutsideCommanderIdentity": true } }] }""", "{1}");
        var zap = Make("Test Zap", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 1 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        if (withSigil) s.Add(P0, sigil);
        s.Add(P0, Make("Test Bear", "Creature — Bear", "{}", "{1}", "2/2"));
        s.Lands(P1, 1);
        var zapCard = s.InHand(P1, zap);
        bool offered = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, legal) =>
        {
            offered |= legal.OfType<CastSpell>().Any(c => c.Card == zapCard);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(zapOffered, offered);
    }

    [Fact]
    public async Task ACreatureMilledThisWayIsChosenAndPutOntoTheBattlefieldUnderYourControl()
    {
        var dig = Make("Grave Dig", "Sorcery", """{ "spell": { "effects": [{ "mill": 4 }, { "takeMilled": { "types": ["creature"] }, "count": 1, "to": "battlefield" }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, dig);
        var first = s.Game.SetupInLibrary(P0, Make("Test Ash", "Creature — Elf", "{}", "{1}", "1/1"));
        var second = s.Game.SetupInLibrary(P0, Make("Test Birch", "Creature — Elf", "{}", "{1}", "2/2"));
        var third = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var fourth = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        bool stacked = false;
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == second).Select(o => o.Id).ToList();
        Play(s, legal =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, first, second, third, fourth); } // the four cards on top: all of them are milled
            return Cast(s, "Grave Dig")(legal);
        });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(second).Zone);
        Assert.Equal(P0, s.Card(second).Controller);
        Assert.Equal(Zone.Graveyard, s.Card(first).Zone); // milled, not chosen
    }

    [Fact]
    public async Task OneGermIsMadeForEachCreatureTheSpellDealtDamageTo()
    {
        var spread = Make("Spread Spark", "Sorcery", """{ "spell": { "targets": ["creature", "creature"], "effects": [{ "damage": 2, "to": "eachTarget" }, { "tokens": "damagedThisWay", "token": { "name": "Germ", "types": "Creature — Germ", "power": 1, "toughness": 1 } }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P1, Make("Test Ox", "Creature — Ox", "{}", "{1}", "3/3"));
        s.Add(P1, Make("Test Ox Two", "Creature — Ox", "{}", "{1}", "3/3"));
        s.InHand(P0, spread);
        Play(s, Cast(s, "Spread Spark"));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Germ" && s.Card(id).Controller == P0));
    }

    [Theory]
    [InlineData(true, 1)]  // one creature in the graveyard, a counter as the nontoken riot choice
    [InlineData(false, 0)] // a token creature gets no riot choice
    public async Task RiotIsChosenForNontokenCreaturesYouCastButNotForTokens(bool nontoken, int counters)
    {
        var tide = Make("Riot Tide", "Enchantment", """{ "nontokenCreaturesYouControlHaveRiot": true }""", "{1}");
        var rally = Make("Rally Tide", "Sorcery", """{ "spell": { "effects": [{ "tokens": 1, "token": { "name": "Test Cub", "types": "Creature — Cub", "power": 1, "toughness": 1 } }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, tide);
        bool asked = false;
        s.Attacker.YesNo = (_, _) => { asked = true; return true; }; // yes: a +1/+1 counter
        s.InHand(P0, nontoken ? Make("Test Bear", "Creature — Bear", "{}", "{1}", "2/2") : rally);
        Play(s, nontoken ? Cast(s, "Test Bear") : Cast(s, "Rally Tide"));
        await s.RunUntilTurn();
        var creature = s.Game.State.Battlefield.Single(id => s.Card(id).Name is "Test Bear" or "Test Cub");
        Assert.Equal(nontoken, asked);
        Assert.Equal(counters, s.Card(creature).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task AMyriadTokenOfALegendaryCreatureIsNotLegendaryAndIsSacrificedAtEndOfCombat()
    {
        var p2 = new PlayerId(2);
        TestController Passive() => new() { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var champion = Make("Lone Champion", "Legendary Creature — Warrior",
            """{ "abilities": [{ "trigger": "attacks", "effects": [{ "myriad": true, "mandatory": true, "playerOnly": true, "notLegendary": true, "sacrificeAtEndOfCombat": true }] }] }""", "{1}", "2/2");
        var attacker = new TestController { Act = (_, _) => PassPriority.Instance };
        attacker.Attack = (_, attackers, _) => attackers.Select(x => new AttackDeclaration(x, P1)).ToList();
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, new[]
        {
            new PlayerSetup("Attacker", attacker, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", Passive(), Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Bystander", Passive(), Decks.Of((GenericCards.Forest, 20))),
        });
        game.SetupPermanent(P0, champion);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(18, game.State.GetPlayer(P1).Life);
        Assert.Equal(18, game.State.GetPlayer(p2).Life); // the token copy attacked the other opponent: it was not legendary, so the legend rule did not remove it
        Assert.Single(game.State.Battlefield, id => game.State.GetCard(id).Name == "Lone Champion"); // the token was sacrificed at end of combat
    }

    [Fact]
    public async Task ModesWithAllDifferentTargetsChooseTwoDifferentPlayers()
    {
        var herald = Make("Twin Herald", "Creature — Herald",
            """{ "abilities": [{ "trigger": "enters", "chooseCount": 2, "upTo": true, "targetRule": "allDifferent", "modes": [{ "text": "Target player loses 1 life.", "targets": ["player"], "effects": [{ "loseLife": 1, "who": "target" }] }, { "text": "Target player loses 2 life.", "targets": ["player"], "effects": [{ "loseLife": 2, "who": "target" }] }] }] }""", "{1}", "2/2");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, herald);
        s.Attacker.Modes = (_, _) => new[] { 0, 1 }; // both modes
        bool repeatAllowed = true;
        s.Attacker.Targets = (_, request) =>
        {
            var chosen = new List<Target>();
            for (int i = 0; i < request.Specs.Count; i++)
                chosen.Add(request.LegalAt(i).First(t => request.IsAllowed(i, t, chosen)));
            repeatAllowed = request.IsAllowed(1, chosen[0], new[] { chosen[0] }); // the second mode may not take the first mode's player
            return chosen;
        };
        Play(s, Cast(s, "Twin Herald"));
        await s.RunUntilTurn();
        Assert.False(repeatAllowed);
        int lostByYou = 20 - s.Game.State.GetPlayer(P0).Life, lostByOpponent = 20 - s.Game.State.GetPlayer(P1).Life;
        Assert.Equal(3, lostByYou + lostByOpponent); // one mode for 1 life and one for 2
        Assert.True(lostByYou > 0 && lostByOpponent > 0); // each player was the target of one mode
    }

    [Fact]
    public async Task WhenAnotherCreatureEntersOneCounterMovesFromTheCreatureToTheEnteringOne()
    {
        var graft = Make("Graft Shoot", "Creature — Plant",
            """{ "entersWithCounters": 2, "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "any", "other": true }, "effects": [{ "moveCounter": { "kind": "+1/+1", "from": "self", "to": "triggered" } }] }] }""", "{1}", "1/1");
        var sprout = Make("Test Sprout", "Creature — Plant", "{}", "{1}", "1/1");
        var s = new Scenario();
        s.Lands(P0, 2);
        var graftCard = s.InHand(P0, graft);
        var newcomer = s.InHand(P0, sprout);
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == graftCard) is { } first) return first;
            if (s.Card(graftCard).Zone == Zone.Battlefield && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == newcomer) is { } second) return second;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(graftCard).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(1, s.Card(newcomer).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ASorceryInYourGraveyardEscapesForItsCostAndExilesThreeOtherCards()
    {
        var charter = Make("Escape Charter", "Enchantment", """{ "rules": ["GraveyardNonlandCardsHaveEscape"] }""", "{1}");
        var rite = Make("Grave Rite", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 3 }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, charter);
        var riteCard = s.InHand(P0, rite);
        var others = new[]
        {
            s.InHand(P0, Make("Test Rot A", "Creature — Zombie", "{}", "{1}", "1/1")),
            s.InHand(P0, Make("Test Rot B", "Creature — Zombie", "{}", "{1}", "1/1")),
            s.InHand(P0, Make("Test Rot C", "Creature — Zombie", "{}", "{1}", "1/1")),
        };
        var player = s.Game.State.GetPlayer(P0);
        foreach (var id in others.Append(riteCard))
        {
            player.Hand.Remove(id);
            player.Graveyard.Add(id);
            s.Card(id).Zone = Zone.Graveyard;
        }
        Play(s, Cast(s, "Grave Rite"));
        await s.RunUntilTurn();
        Assert.Equal(23, player.Life); // the spell resolved: its cost was paid and its three life gained
        Assert.All(others, id => Assert.Equal(Zone.Exile, s.Card(id).Zone));
        Assert.Equal(Zone.Graveyard, s.Card(riteCard).Zone); // a sorcery goes to the graveyard after it resolves
    }

    [Fact]
    public async Task ACardWithNoManaCostIsNotCastFromTheGraveyardOrFromHand()
    {
        var charter = Make("Escape Charter", "Enchantment", """{ "rules": ["GraveyardNonlandCardsHaveEscape"] }""", "{1}");
        var blank = Make("Blank Rite", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 3 }] } }""", "");
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P0, charter);
        var graveyardCard = s.InHand(P0, blank);
        var handCard = s.InHand(P0, blank);
        var others = new[]
        {
            s.InHand(P0, Make("Test Rot A", "Creature — Zombie", "{}", "{1}", "1/1")),
            s.InHand(P0, Make("Test Rot B", "Creature — Zombie", "{}", "{1}", "1/1")),
            s.InHand(P0, Make("Test Rot C", "Creature — Zombie", "{}", "{1}", "1/1")),
        };
        var player = s.Game.State.GetPlayer(P0);
        foreach (var id in others.Append(graveyardCard))
        {
            player.Hand.Remove(id);
            player.Graveyard.Add(id);
            s.Card(id).Zone = Zone.Graveyard;
        }
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<CastSpell>().Any(c => c.Card == graveyardCard || c.Card == handCard);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
        Assert.Equal(20, player.Life);
    }

    [Theory]
    [InlineData(true)]  // the opponent's card: the extra turn is skipped and the opponent's normal turn comes next
    [InlineData(false)]
    public async Task AnExtraTurnIsSkippedWhileAnOpponentControlsOpponentsSkipExtraTurns(bool withWarden)
    {
        var warden = Make("Turn Warden", "Enchantment", """{ "opponentsSkipExtraTurns": true }""", "{1}");
        var respite = Make("Extra Respite", "Sorcery", """{ "spell": { "effects": [{ "extraTurn": true }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        if (withWarden) s.Add(P1, warden);
        s.InHand(P0, respite);
        var turns = new List<PlayerId>();
        var skipped = new List<PlayerId>();
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan began) turns.Add(began.ActivePlayer);
            if (e is TurnSkipped skip) skipped.Add(skip.Player);
        };
        Play(s, Cast(s, "Extra Respite"));
        await s.RunUntilTurn(3);
        if (withWarden)
        {
            Assert.Single(skipped);
            Assert.Equal(P0, skipped[0]);
            Assert.Equal(new[] { P0, P1, P0 }, turns); // turn 2 is the opponent's normal turn
        }
        else
        {
            Assert.Empty(skipped);
            Assert.Equal(new[] { P0, P0, P1 }, turns.Take(3)); // the extra turn is taken
        }
    }

    [Fact]
    public async Task PoisonCountersGivenToAnOpponentTriggerAWatchForTheAmountGiven()
    {
        var watcher = Make("Venom Watch", "Enchantment",
            """{ "abilities": [{ "trigger": "countersPlaced", "counterKind": "any", "placedByYou": true, "orPlayer": true, "filter": { "controller": "any" }, "targets": ["opponent"], "effects": [{ "damage": "triggerAmount", "to": "target" }] }] }""", "{1}");
        var toxin = Make("Test Toxin", "Sorcery", """{ "spell": { "targets": ["opponent"], "effects": [{ "poison": 2, "who": "target" }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, watcher);
        s.InHand(P0, toxin);
        Play(s, Cast(s, "Test Toxin"));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.GetPlayer(P1).Poison);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // the trigger dealt the two counters' amount as damage
    }

    [Fact]
    public async Task PayingLifeInRoundsMakesTokensUntilARoundWhereNobodyPays()
    {
        var bargain = Make("Blood Bargain", "Sorcery",
            """{ "spell": { "effects": [{ "payLifeRounds": [{ "tokens": "X", "token": { "name": "Rat", "types": "Creature — Rat", "power": 1, "toughness": 1 } }] }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, bargain);
        var attackerPays = new Queue<int>(new[] { 3, 0 });
        var defenderPays = new Queue<int>(new[] { 2, 0 });
        int attackerAsks = 0, defenderAsks = 0;
        s.Attacker.Number = (_, _) => { attackerAsks++; return attackerPays.Count > 0 ? attackerPays.Dequeue() : 0; };
        s.Defender.Number = (_, _) => { defenderAsks++; return defenderPays.Count > 0 ? defenderPays.Dequeue() : 0; };
        Play(s, Cast(s, "Blood Bargain"));
        await s.RunUntilTurn();
        Assert.Equal(2, attackerAsks); // round one and round two: nobody paid in round two, so the rounds stop
        Assert.Equal(2, defenderAsks);
        Assert.Equal(17, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(3, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Rat" && s.Card(id).Controller == P0));
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Rat" && s.Card(id).Controller == P1));
    }

    [Theory]
    [InlineData(true, 2)]  // two triggers for the opponent's one draw in turn two
    [InlineData(false, 1)]
    public async Task AnOpponentsDrawTriggersTwiceWhenThePermanentSaysSo(bool twice, int gained)
    {
        string flag = twice ? "true" : "false";
        var watch = Make("Draw Watch", "Enchantment", $$"""{ "drawTriggersTwice": {{flag}}, "abilities": [{ "trigger": "opponentDraws", "effects": [{ "gainLife": 1 }] }] }""", "{1}");
        var s = new Scenario();
        s.Add(P0, watch);
        int? lifeAtTurnTwo = null;
        s.Game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) lifeAtTurnTwo = s.Game.State.GetPlayer(P0).Life; };
        await s.RunUntilTurn(3); // the opponent draws in its turn, turn two; the gain is counted from the start of that turn (the test setup puts the permanent before the opening hands are drawn)
        Assert.Equal(gained, s.Game.State.GetPlayer(P0).Life - lifeAtTurnTwo!.Value);
    }
}
