// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Engine pieces for single cards: gold tokens, combat damage dealt to opponents by Warriors, divided damage that skips
/// trailing targets, destroy-all then return as a black Zombie, void counters on opponents' cards, differently named tokens, casting
/// spells from the graveyard that locks further spells this turn and the negated triggered-player condition. The cards are invented.
/// </summary>
public class SpecialEffectsTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"dsc-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private static void CastFirst(Scenario s, CardId card)
    {
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static CardId PutInGraveyard(Scenario s, CardDefinition card)
    {
        var id = s.InHand(P0, card);
        var player = s.Game.State.GetPlayer(P0);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    [Fact]
    public async Task GoldTokenSacrificesForOneManaOfAnyColor()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var spell = s.InHand(P0, Make("Gold Rush", "Sorcery", """{ "spell": { "effects": [{ "tokens": 1, "token": "Gold" }] } }""", "{1}"));
        CastFirst(s, spell);
        await s.RunUntilTurn();
        var gold = Assert.Single(s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Gold"));
        Assert.Equal(P0, gold.Controller);
        Assert.True(gold.Is(CardType.Artifact));
        Assert.Single(gold.Definition.Abilities.OfType<ActivatedAbility>());
    }

    private const string WarriorWatch = """
        { "abilities": [ { "trigger": "opponentsDealtCombatDamage", "if": { "batchHas": { "subtype": "Warrior" } }, "effects": [{ "draw": 1 }, { "loseLife": 1 }], "text": "Whenever your opponents are dealt combat damage, if any of that damage was dealt by a Warrior, you draw a card and you lose 1 life." } ] }
        """;

    /// <summary>Attacks with the one creature of the given type line and returns P0's hand size and life afterwards.</summary>
    private static async Task<(int Drawn, int Life)> AttackWith(string typeLine)
    {
        var s = new Scenario();
        s.Add(P0, Make("Ash Watcher", typeLine, WarriorWatch, "{1}", "2/2",
            "Whenever your opponents are dealt combat damage, if any of that damage was dealt by a Warrior, you draw a card and you lose 1 life."));
        // Cards to draw: put on top of the library once the game runs.
        var fillers = new[] { s.InHand(P0, Creature("Filler", 1, 1)), s.InHand(P0, Creature("Filler", 1, 1)) };
        bool stacked = false;
        s.Attacker.Act = (_, _) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, fillers); }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int drawn = 0;
        s.Game.EventRaised += e => { if (e is CardDrawn { Player: var who } && who == P0) drawn++; };
        await s.RunUntilTurn();
        return (drawn, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task CombatDamageToOpponentsByAWarriorDrawsACard()
    {
        var warrior = await AttackWith("Creature — Human Warrior");
        var human = await AttackWith("Creature — Human");
        Assert.Equal(human.Drawn + 1, warrior.Drawn); // the trigger's card
        Assert.Equal(19, warrior.Life); // and its life
        Assert.Equal(20, human.Life); // no trigger without a Warrior
    }

    [Fact]
    public async Task DividedDamageSkipsTheTrailingTargets()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var spell = s.InHand(P0, Make("Split Shot", "Instant", """
            { "spell": {
              "targets": [
                { "kind": "any", "optional": true, "distinctFrom": 0 },
                { "kind": "any", "optional": true, "distinctFrom": 0 },
                { "kind": "permanent", "distinctFrom": 2 } ],
              "effects": [ { "divideDamage": 3, "notAmongLast": 1 }, { "tap": "target3" } ] } }
            """, "{R}"));
        var big = s.Add(P1, Creature("Big Ox", 5, 5));
        var small = s.Add(P1, Creature("Small Ox", 5, 5));
        CastFirst(s, spell);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(big), Target.None, Target.Of(small) };
        // Damage is removed in the cleanup step and the tap is undone in the next untap step, so the events are checked as they happen.
        int bigDamage = 0, smallDamage = 0;
        bool smallTapped = false;
        s.Game.EventRaised += e =>
        {
            if (e is DamageDealt d) { if (d.TargetCard == big) bigDamage += d.Amount; if (d.TargetCard == small) smallDamage += d.Amount; }
            if (e is PermanentTapped t && t.Card == small) smallTapped = true;
        };
        await s.RunUntilTurn();
        Assert.Equal(3, bigDamage);
        Assert.Equal(0, smallDamage);
        Assert.True(smallTapped);
    }

    [Fact]
    public async Task DestroyAllThenReturnOneAsABlackZombie()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Swamp);
        var spell = s.InHand(P0, Make("Grave Sweep", "Sorcery", """
            { "spell": { "effects": [
              { "destroy": { "each": { "types": ["creature"] } } },
              { "returnFromGraveyard": { "types": ["creature"] }, "to": "battlefield", "anyGraveyard": true, "amongDestroyed": true },
              { "become": "enteredThisWay", "addColors": ["B"], "addSubtypes": ["Zombie"], "permanent": true } ] } }
            """, "{B}"));
        s.Add(P1, Make("Green Ox", "Creature — Ox", "{}", "{G}", "3/3"));
        CastFirst(s, spell);
        s.Attacker.Choose = (_, request) => request.Options.Take(1).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        var ox = Assert.Single(s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Green Ox"));
        Assert.Equal(P0, ox.Controller);
        Assert.True(ox.HasSubtype("Zombie"));
        Assert.Contains("G", ox.Colors);
        Assert.Contains("B", ox.Colors);
    }

    [Fact]
    public async Task OpponentsCardsGoToExileWithAVoidCounter()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Swamp);
        s.Add(P0, Make("Void Walker", "Creature — Rogue", """{ "opponentsCardsExiledWithVoid": true }""", "{1}", "1/1",
            "If a card an opponent controls would be put into a graveyard from anywhere, exile it with a void counter on it instead."));
        var ox = s.Add(P1, Make("Ox", "Creature — Ox", "{}", "{1}", "2/2"));
        var spell = s.InHand(P0, Make("Unmake", "Sorcery", """{ "spell": { "targets": ["creature:opponent"], "effects": [{ "destroy": "target" }] } }""", "{B}"));
        CastFirst(s, spell);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(ox).Zone);
        Assert.Equal(1, s.Card(ox).CounterCount(CounterKind.Void));
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone);
        Assert.Contains(spell, s.Game.State.GetPlayer(P0).Graveyard);
    }

    [Fact]
    public async Task CountsDifferentlyNamedTokens()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var spell = s.InHand(P0, Make("Hoard Call", "Sorcery", """
            { "spell": { "effects": [
              { "tokens": 2, "token": "Treasure" },
              { "tokens": 1, "token": "Gold" },
              { "gainLife": "differentlyNamedTokens" } ] } }
            """, "{1}"));
        CastFirst(s, spell);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // Treasure and Gold: two different names
    }

    [Fact]
    public async Task CastThisWayLocksFurtherSpellsThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var conduit = s.Add(P0, Make("Old Conduit", "Artifact", """
            { "abilities": [ {
              "cost": "{T}", "sorcery": true,
              "targets": [ { "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"] } } ],
              "effects": [
                { "castFromGraveyard": {}, "card": "target", "noExile": true },
                { "if": "castThisWay", "then": [ { "youCantCastSpellsThisTurn": true } ] } ],
              "text": "{T}: You may cast target creature card from your graveyard. If you do, you can't cast additional spells this turn." } ] }
            """, "{2}", null, "{T}: You may cast target creature card from your graveyard. If you do, you can't cast additional spells this turn."));
        var graveOx = PutInGraveyard(s, Make("Grave Ox", "Creature — Ox", "{}", "{1}", "2/2"));
        var handOx = s.InHand(P0, Make("Hand Ox", "Creature — Ox", "{}", "{1}", "1/1"));
        bool activated = false, offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == conduit) is { } ability) { activated = true; return ability; }
            if (s.Game.State.Battlefield.Contains(graveOx)) offered |= legal.OfType<CastSpell>().Any(c => c.Card == handOx);
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.YesNo = (_, _) => true;
        s.Attacker.Choose = (_, request) => request.Options.Take(1).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.True(activated);
        Assert.Contains(graveOx, s.Game.State.Battlefield);
        Assert.False(offered); // one land is left to pay {1}, but no more spells this turn
        Assert.Equal(Zone.Hand, s.Card(handOx).Zone);
    }

    [Fact]
    public async Task NegatedTriggeredPlayerConditionHoldsForAnOpponent()
    {
        var s = new Scenario();
        s.Add(P0, Make("Upkeep Chime", "Enchantment", """
            { "abilities": [ { "trigger": "eachUpkeep", "effects": [ { "if": { "not": "triggeredPlayerIsYou" }, "then": [ { "gainLife": 1 } ] } ],
              "text": "At the beginning of each upkeep, if it isn't your upkeep, you gain 1 life." } ] }
            """, "{1}", null, "At the beginning of each upkeep, if it isn't your upkeep, you gain 1 life."));
        await s.RunUntilTurn(3); // turn 1 (player 0) and turn 2 (player 1) upkeeps have passed
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // only player 1's upkeep gains life
    }
}
