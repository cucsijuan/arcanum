// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Script vocabulary of a set of invented cards: card-wide replacements and costs, subjects for "you" and "each other
/// player", filters on entering creatures, counter removal with counted draws, extra mana that can't pay generic
/// costs, counter-spells with tokens, targeting spells on the stack or creatures, evoke from hand, copying triggers,
/// revealing until a shared creature type, and sacrifices that keep one creature type. The cards are invented.
/// </summary>
public class SetVocabularyTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"vocab-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
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

    [Theory]
    [InlineData(true, 22)]
    [InlineData(false, 21)]
    public async Task AnEntersTriggerRunsTwiceWhileAPermanentWithExtraTriggersIsOnTheBattlefield(bool withEcho, int life)
    {
        var echo = Make("Echo Gate", "Enchantment", """{ "replaces": ["ExtraTriggersFromPermanentsEntering"] }""", "{1}");
        var walker = Make("Gate Walker", "Creature — Bear", """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 1 }] }] }""", "{1}", "2/2");
        var s = new Scenario();
        s.Lands(P0, 1);
        if (withEcho) s.Add(P0, echo);
        s.InHand(P0, walker);
        Play(s, Cast(s, "Gate Walker"));
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AnOnlyOpponentsAbilityIsNotOfferedToItsController()
    {
        var relic = Make("Hush Relic", "Artifact", """{ "abilities": [{ "cost": "{1}", "onlyOpponents": true, "effects": [{ "draw": 1 }] }] }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.Add(P0, relic);
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == card);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task AnOnlyOpponentsAbilityIsOfferedToAnOpponentWithPriorityAndAnUntappedLand()
    {
        var relic = Make("Hush Relic", "Artifact", """{ "abilities": [{ "cost": "{1}", "onlyOpponents": true, "effects": [{ "draw": 1 }] }] }""", "{1}");
        var s = new Scenario();
        s.Lands(P1, 1);
        var card = s.Add(P0, relic);
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
    public async Task ADiesTriggerMakesTheSourceOwnerDrawAndEachOtherPlayerLoseLife()
    {
        var mite = Make("Doomed Mite", "Creature — Insect", """{ "abilities": [{ "trigger": "dies", "effects": [{ "draw": 1, "who": "sourceOwner" }, { "loseLife": 2, "who": "playersOtherThanSourceOwner" }] }] }""", "{1}", "1/1");
        var snuff = Make("Snuff Spell", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.Add(P0, mite);
        s.InHand(P0, snuff);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(card) };
        int? library = null;
        s.Attacker.Act = (_, legal) =>
        {
            library ??= s.Game.State.GetPlayer(P0).Library.Count;
            return Cast(s, "Snuff Spell")(legal) ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(library!.Value - 1, s.Game.State.GetPlayer(P0).Library.Count); // the owner drew one
    }

    [Theory]
    [InlineData(5, 25)]
    [InlineData(3, 20)]
    public async Task AnEnteringCreatureWithMorePowerThanEveryOtherCreatureTriggersAGainOnlyWhenItIsGreater(int power, int life)
    {
        var watch = Make("Watch Eye", "Enchantment", """{ "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "any", "other": true }, "effects": [{ "if": { "triggered": { "powerGreaterThanEachOther": true } }, "then": [{ "gainLife": 5 }] }] }] }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, watch);
        s.Add(P1, Creature("Ox", 3, 3));
        s.InHand(P0, Creature("Entrant", power, power));
        Play(s, Cast(s, "Entrant"));
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task RemovingCountersDrawsOneCardForEachCounterRemovedWhenTheMaximumIsChosen()
    {
        var spite = Make("Gather Spite", "Sorcery", """{ "spell": { "effects": [{ "removeUpToCounters": 9999, "from": { "each": {} } }, { "draw": "countersRemovedThisWay" }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var first = s.Add(P0, Creature("Sprout A", 1, 1));
        var second = s.Add(P0, Creature("Sprout B", 1, 1));
        s.Card(first).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Card(second).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.InHand(P0, spite);
        s.Attacker.Number = (_, request) => request.Max;
        int? library = null;
        s.Attacker.Act = (_, legal) =>
        {
            library ??= s.Game.State.GetPlayer(P0).Library.Count;
            return Cast(s, "Gather Spite")(legal) ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(first).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(second).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(library!.Value - 3, s.Game.State.GetPlayer(P0).Library.Count); // three counters, three cards
    }

    [Fact]
    public async Task AnExtraManaOptionThatCantPayGenericCostsPaysOnlyColoredCosts()
    {
        var prism = Make("Prism Relic", "Artifact", """{ "extraMana": [{ "types": "{W}{U}{B}{R}{G}", "oneOfEach": true, "cantPayGeneric": true }] }""", "{1}");
        var heavy = Make("Heavy Rite", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{5}");
        var light = Make("Light Rite", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{W}{U}");
        var s = new Scenario();
        s.Add(P0, prism);
        var heavyCard = s.InHand(P0, heavy);
        var lightCard = s.InHand(P0, light);
        bool heavyOffered = false, lightOffered = false;
        s.Attacker.Act = (_, legal) =>
        {
            heavyOffered |= legal.OfType<CastSpell>().Any(c => c.Card == heavyCard);
            lightOffered |= legal.OfType<CastSpell>().Any(c => c.Card == lightCard);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(heavyOffered);
        Assert.True(lightOffered);
    }

    [Fact]
    public async Task AnArtifactCreatureThatAddsManaOfAColorBecomesThatColorUntilTheEndOfTheTurn()
    {
        var gem = Make("Chameleon Gem", "Artifact Creature — Construct", """{ "abilities": [{ "cost": "{1}", "effects": [{ "addManaAnyColor": 1, "sourceBecomesThatColor": true }] }] }""", "{2}", "2/2");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.Add(P0, gem);
        s.Attacker.Option = (_, request) => request.Options.ToList().FindIndex(o => o.Contains("Red"));
        bool activated = false;
        string[]? colors = null;
        s.Attacker.Act = (view, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == card) is { } a) { activated = true; return a; }
            if (activated && view.Stack.Count == 0) colors ??= s.Card(card).Colors.ToArray(); // read before the turn ends
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(new[] { "R" }, colors);
    }

    [Fact]
    public async Task ACounterSpellCountersTheOpponentsSpellAndMakesOneSpriteForItsController()
    {
        var quell = Make("Quell Storm", "Instant", """{ "spell": { "effects": [{ "counterAllOfOpponents": true }, { "tokens": "spellsAndAbilitiesCounteredThisWay", "token": { "name": "Sprite", "types": "Creature — Faerie", "power": 1, "toughness": 1, "colors": ["U"], "keywords": ["Flying"] } }] } }""", "{1}{U}");
        var gift = Make("Gift Scroll", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}");
        var s = new Scenario();
        s.Add(P0, GenericCards.Island);
        s.Lands(P0, 1);
        s.Add(P1, GenericCards.Mountain);
        var counter = s.InHand(P0, quell);
        var spell = s.InHand(P1, gift);
        s.Attacker.Act = (view, legal) =>
        {
            if (view.Stack.Count > 0 && view.Stack[^1].Controller == P1 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == counter) is { } a) return a;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(3); // the opponent casts its spell in its own turn
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone);
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Sprite" && s.Card(id).Controller == P0));
    }

    [Fact]
    public async Task ASpellCanPutACreatureOnTopOfItsOwnersLibrary()
    {
        var recall = Make("Recall Charm", "Instant", """{ "spell": { "targets": ["spellOrCreature"], "effects": [{ "toLibrary": "target", "top": true }] } }""", "{U}");
        var s = new Scenario();
        s.Add(P0, GenericCards.Island);
        var ox = s.Add(P1, Creature("Stray Ox", 3, 3));
        s.InHand(P0, recall);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ox) };
        Play(s, Cast(s, "Recall Charm"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(ox).Zone);
        Assert.Equal(P1, s.Card(ox).Owner);
        Assert.Equal(ox, s.Game.State.GetPlayer(P1).Library[0]);
    }

    [Theory]
    [InlineData(true, 21)] // the unblocked two damage and the three life
    [InlineData(false, 20)]
    public async Task ACostReducedByAnAttackingCreatureIsPayableOnlyWhileACreatureAttacksYou(bool attacked, int life)
    {
        var ward = Make("Rebuff Ward", "Instant", """{ "costReduction": { "amount": 2, "if": "creatureAttackingYou" }, "spell": { "effects": [{ "gainLife": 3 }] } }""", "{2}{U}{U}");
        var s = new Scenario();
        s.Add(P0, GenericCards.Island);
        s.Add(P0, GenericCards.Island);
        var card = s.InHand(P0, ward);
        s.Add(P1, Creature("Raider", 2, 2));
        if (attacked) s.Defender.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        Play(s, legal => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card));
        await s.RunUntilTurn(3); // the opponent's turn, when its creature attacks
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(attacked ? Zone.Graveyard : Zone.Hand, s.Card(card).Zone);
    }

    [Fact]
    public async Task SacrificeKeepsOnlyCreaturesSharingTheTypeOfTheCreatureChosenForEachPlayer()
    {
        var purge = Make("Purge Call", "Sorcery", """{ "spell": { "effects": [{ "sacrificeNotSharingTypeWithChosen": true }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var elfOne = s.Add(P0, Make("Elf Scout", "Creature — Elf", "{}", "{1}", "1/1"));
        var elfTwo = s.Add(P0, Make("Elf Guard", "Creature — Elf", "{}", "{1}", "1/1"));
        var goblin = s.Add(P0, Make("Goblin Runner", "Creature — Goblin", "{}", "{1}", "1/1"));
        var theirGoblin = s.Add(P1, Make("Goblin Rival", "Creature — Goblin", "{}", "{1}", "1/1"));
        var merfolk = s.Add(P1, Make("Merfolk Tide", "Creature — Merfolk", "{}", "{1}", "1/1"));
        s.InHand(P0, purge);
        // The caster chooses the creature each player keeps: its own Elf, then the opponent's Goblin.
        s.Attacker.Choose = (_, request) => request.Options.Any(o => o.Id == theirGoblin) ? new[] { theirGoblin } : new[] { elfOne };
        Play(s, Cast(s, "Purge Call"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(elfOne).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(elfTwo).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(goblin).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(theirGoblin).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(merfolk).Zone);
    }

    [Fact]
    public async Task ExileTopCardsOfAnOpponentUntilTheirManaValueReachesFiveAndLetsYouCastThem()
    {
        var dig = Make("Shadow Dig", "Sorcery", """{ "spell": { "effects": [{ "exileTopPlayable": 0, "of": "opponents", "untilTotalManaValue": 5, "chooseOne": false, "free": true, "castOnly": true }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, dig);
        var two = Make("Mite Two", "Creature — Mite", "{}", "{2}", "2/2");
        var twoAgain = Make("Mite Two Again", "Creature — Mite", "{}", "{2}", "2/2");
        var three = Make("Mite Three", "Creature — Mite", "{}", "{3}", "3/3");
        var four = Make("Mite Four", "Creature — Mite", "{}", "{4}", "4/4");
        var a = s.Game.SetupInLibrary(P1, two);
        var b = s.Game.SetupInLibrary(P1, twoAgain);
        var c = s.Game.SetupInLibrary(P1, three);
        var d = s.Game.SetupInLibrary(P1, four);
        bool stacked = false;
        Play(s, legal =>
        {
            if (!stacked) { stacked = true; s.Restack(P1, a, b, c, d); }
            return Cast(s, "Shadow Dig")(legal);
        });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(a).Zone);
        Assert.Equal(Zone.Exile, s.Card(b).Zone);
        Assert.Equal(Zone.Exile, s.Card(c).Zone); // 2 + 2 is not yet 5; the third card makes it 7
        Assert.Equal(Zone.Library, s.Card(d).Zone);
    }

    [Fact]
    public async Task TheFirstTokensMadeEachTurnAreCopiesOfTheEquippedCreatureWhenTheEquipmentSaysSo()
    {
        var blade = Make("Mirror Blade", "Artifact — Equipment", """{ "firstTokensCopyEquipped": true }""", "{1}");
        var rally = Make("Rally Call", "Sorcery", """{ "spell": { "effects": [{ "tokens": 1, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1 } }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 2);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var equipment = s.Add(P0, blade);
        s.Card(equipment).AttachedTo = bear;
        var spells = new[] { s.InHand(P0, rally), s.InHand(P0, rally) };
        s.Attacker.Act = (view, legal) =>
        {
            if (view.Stack.Count == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => spells.Contains(c.Card)) is { } cast) return cast;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Bear" && s.Card(id).Controller == P0)); // the original and the copy
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Soldier")); // only the first token copied
    }

    [Fact]
    public async Task ACopyOfAnEntersTriggerMakesItsControllerGainLifeTwice()
    {
        var walker = Make("Gate Walker", "Creature — Bear", """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 3 }] }] }""", "{1}", "2/2");
        var echo = Make("Twin Echo", "Instant", """{ "spell": { "targets": [{ "kind": "ability", "controller": "you", "triggeredOnly": true }], "effects": [{ "copyAbility": "target" }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 2);
        var creature = s.InHand(P0, walker);
        var copy = s.InHand(P0, echo);
        s.Attacker.Targets = (view, _) => new[] { Target.OfStack(view.Stack.First(i => i.Card.Name == "Gate Walker").Id) };
        s.Attacker.Act = (view, legal) =>
        {
            if (view.Stack.Count > 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == copy) is { } echoed) return echoed;
            if (view.Stack.Count == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == creature) is { } cast) return cast;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(26, s.Game.State.GetPlayer(P0).Life); // the trigger's three and its copy's three
    }

    [Fact]
    public async Task AnElementalCreatureCanBeCastForTheEvokeCostFromHandWhileItsFullCostIsTooHigh()
    {
        var patron = Make("Gate Patron", "Enchantment", """{ "spellsFromHandGainEvoke": { "filter": { "subtype": "Elemental" }, "cost": "{4}" } }""", "{1}");
        var tide = Make("Tide Elemental", "Creature — Elemental", "{}", "{6}", "3/3");
        var s = new Scenario();
        s.Lands(P0, 4);
        s.Add(P0, patron);
        var card = s.InHand(P0, tide);
        s.Attacker.YesNo = (_, _) => true; // the evoke way
        Play(s, Cast(s, "Tide Elemental"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone); // evoked: sacrificed when it entered
    }

    [Fact]
    public async Task ARevealedCreatureSharingACreatureTypeWithTheFoundOneGoesToHandAndTheRestStays()
    {
        var search = Make("Kin Search", "Sorcery", """{ "spell": { "effects": [{ "revealUntil": { "types": ["creature"] }, "to": "hand" }, { "revealUntil": { "types": ["creature"] }, "sharesCreatureTypeWith": "found", "to": "hand" }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, search);
        var elfOne = s.Game.SetupInLibrary(P0, Make("Elf Seeker", "Creature — Elf", "{}", "{1}", "1/1"));
        var goblin = s.Game.SetupInLibrary(P0, Make("Goblin Digger", "Creature — Goblin", "{}", "{1}", "1/1"));
        var elfTwo = s.Game.SetupInLibrary(P0, Make("Elf Finder", "Creature — Elf", "{}", "{1}", "1/1"));
        bool stacked = false;
        Play(s, legal =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, elfOne, goblin, elfTwo); }
            return Cast(s, "Kin Search")(legal);
        });
        await s.RunUntilTurn();
        Assert.NotEqual(Zone.Library, s.Card(elfOne).Zone);
        Assert.NotEqual(Zone.Library, s.Card(elfTwo).Zone); // shares the Elf type with the first one found
        Assert.Equal(Zone.Library, s.Card(goblin).Zone); // skipped, and put back
    }

    private const string ChampionScript = """
        { "abilities": [
          { "trigger": "enters", "effects": [{ "champion": { "subtype": "Faerie" } }] },
          { "trigger": "leaves", "effects": [{ "returnLinkedExiled": true }] },
          { "trigger": "championed", "effects": [{ "gainLife": 3 }] } ] }
        """;

    [Fact]
    public async Task AChampionWithNothingToExileIsSacrificed()
    {
        var liege = Make("Wisp Liege", "Creature — Faerie Wizard", ChampionScript, "{1}", "4/4");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.InHand(P0, liege);
        Play(s, Cast(s, "Wisp Liege"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AChampionedCardIsExiledAndComesBackWhenTheChampionLeaves()
    {
        var liege = Make("Wisp Liege", "Creature — Faerie Wizard", ChampionScript, "{1}", "4/4");
        var sweep = Make("Wizard Sweep", "Sorcery", """{ "spell": { "effects": [{ "destroy": { "each": { "subtype": "Wizard" } } }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 2);
        var sprite = s.Add(P0, Creature("Glow Sprite", 1, 1) with { Subtypes = new[] { "Faerie" } });
        var champion = s.InHand(P0, liege);
        s.InHand(P0, sweep);
        s.Attacker.Choose = (_, request) => request.Options.Any(o => o.Id == sprite) ? new[] { sprite } : request.Options.Take(request.Min).Select(o => o.Id).ToList();
        Zone? exiledThen = null;
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (step == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Wisp Liege") is { } first) { step = 1; return first; }
            if (step == 1 && s.Card(champion).Zone == Zone.Battlefield && legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Wizard Sweep") is { } second)
            {
                exiledThen = s.Card(sprite).Zone;
                step = 2;
                return second;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, exiledThen);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // the "championed" trigger
        Assert.Equal(Zone.Graveyard, s.Card(champion).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(sprite).Zone);
    }

    [Fact]
    public async Task ACreatureThatJustEnteredCanUseItsTapAbilityWithTheElixirEffect()
    {
        var elixir = Make("Ageless Draught", "Artifact", """{ "yourCreaturesActivateAsThoughHaste": true }""", "{3}");
        var tapper = Make("Fresh Tapper", "Creature — Elf", """{ "abilities": [{ "cost": "{T}", "effects": [{ "gainLife": 2 }] }] }""", "{1}", "1/1");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, elixir);
        var card = s.InHand(P0, tapper);
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (step == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } cast) { step = 1; return cast; }
            if (step == 1 && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == card) is { } use) { step = 2; return use; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task EveryCardEverywhereIsTheChosenColorToo()
    {
        var painter = Make("Hue Servant", "Artifact Creature — Scarecrow", """{ "chooseOnEnter": "color", "everythingIsChosenColor": true }""", "{1}", "1/3");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.InHand(P0, painter);
        var other = s.InHand(P1, Creature("Plain Ox", 2, 2));
        s.Attacker.Option = (_, _) => 1; // blue
        Play(s, Cast(s, "Hue Servant"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(card).Zone);
        Assert.Contains("U", s.Card(other).Colors);
        Assert.Contains("U", s.Card(card).Colors);
    }

    [Fact]
    public async Task CombatDamageToYourAttackingCreaturesIsPrevented()
    {
        var gate = Make("Stone Arch", "Artifact", """{ "preventCombatDamageToYourAttackers": true }""", "{2}");
        var s = new Scenario();
        s.Add(P0, gate);
        var attacker = s.Add(P0, Creature("Bold Ox", 2, 2));
        var wall = s.Add(P1, Creature("Wall Bear", 3, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == attacker).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(wall, attacker) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(attacker).Zone); // a 2/2 blocked by a 3/3 survives: the damage to it is prevented
    }
}
