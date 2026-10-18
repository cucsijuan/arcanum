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
/// Vocabulary for quantities from the battlefield and graveyards, filters, discards, counters, uncounterable spells,
/// cast-trigger targets and mana tokens that are sacrificed for mana.
/// </summary>
public class CardRulesVocabularyTests
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

    /// <summary>An instant or sorcery dealing 2 damage with the given "to" subject ("opponents" or "you").</summary>
    private static CardDefinition Burn(string to) => Scripted("Scorch Ping", "Instant", "{R}",
        $$"""{ "spell": { "effects": [{ "damage": 2, "to": "{{to}}" }] } }""");

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().LastOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    /// <summary>Puts a card straight into its owner's graveyard before the game starts.</summary>
    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    [Fact]
    public async Task DrawnThisWayLosesLifeForEachCardDrawn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Lean Draw", "Sorcery", "{R}", """{ "spell": { "effects": [{ "draw": 2 }, { "loseLife": "drawnThisWay" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life - 2, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task DrawnThisWayCountsOnlyTheCardsActuallyDrawn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Lean Draw", "Sorcery", "{R}", """{ "spell": { "effects": [{ "draw": 2 }, { "loseLife": "drawnThisWay" }] } }"""));
        bool trimmed = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            if (!trimmed) // keep one card in the library, once the game has dealt the hands
            {
                trimmed = true;
                var library = s.Game.State.GetPlayer(P0).Library;
                library.RemoveRange(1, library.Count - 1);
            }
            return act(view, legal);
        };
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life - 1, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task TargetLoyaltyIsTheDamageDealtByThatPlaneswalker()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var walker = s.Add(P0, new CardDefinition { Name = "Tester Sage", Types = CardType.Planeswalker, Subtypes = new[] { "Tester" }, Loyalty = 3 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 3;
        var bear = s.Add(P1, Creature("Bear", 2, 3));
        s.InHand(P0, Scripted("Walker Blast", "Sorcery", "{R}",
            """{ "spell": { "targets": ["planeswalker:you", "creature"], "effects": [{ "damage": { "loyalty": "target" }, "to": "target2", "from": "target" }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone); // three damage to a creature with three toughness
    }

    [Fact]
    public async Task DistinctPowersCountsEachPowerOnceAmongYourCreatures()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear", 1, 1));
        s.Add(P0, Creature("Cub", 1, 1));
        s.Add(P0, Creature("Ogre", 3, 3));
        s.Add(P1, Creature("Foe", 5, 5));
        s.InHand(P0, Scripted("Tally Gain", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": "distinctPowers" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life); // powers 1 and 3 among your creatures
    }

    [Fact]
    public async Task DomainCountsTheBasicLandTypesYouControl()
    {
        var s = Casting();
        s.Lands(P0, 2); // two Mountains: one basic land type
        s.InHand(P0, Scripted("Domain Gain", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": "domain" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task GreatestManaValueInGraveyardIsTheHighestAmongItsCards()
    {
        var s = Casting();
        s.Lands(P0, 1);
        PutInGraveyard(s, P0, Creature("Low Bear", 1, 1));
        PutInGraveyard(s, P0, Creature("High Bear", 1, 1) with { ManaCost = ManaCost.Parse("{3}{R}") });
        s.InHand(P0, Scripted("Grave Gain", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": { "greatestManaValueInGraveyard": {} } }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 4, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task MilledThisTurnCountsTheCardsMilledSoFarAndDrawsThatMany()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Mill and Draw", "Sorcery", "{R}",
            """{ "spell": { "targets": ["opponent"], "effects": [{ "mill": 3, "who": "target" }, { "draw": { "milledThisTurn": "target" } }] } }"""));
        int? libraryBefore = null;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            libraryBefore ??= s.Game.State.GetPlayer(P0).Library.Count; // the first priority, before the spell is cast
            return act(view, legal);
        };
        await s.RunUntilTurn();
        Assert.Equal(3, s.Game.State.GetPlayer(P1).Graveyard.Count);
        Assert.Equal(libraryBefore - 3, s.Game.State.GetPlayer(P0).Library.Count);
    }

    [Fact]
    public async Task GreatestManaValueAmongTheirsWithPlaneswalkersMakesTheOpponentSacrificeIt()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2) with { ManaCost = ManaCost.Parse("{1}{R}") });
        var walker = s.Add(P1, new CardDefinition { Name = "Tester Sage", ManaCost = ManaCost.Parse("{3}{R}"), Types = CardType.Planeswalker, Loyalty = 3 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 3; // without loyalty counters the walker dies as a state-based action
        s.InHand(P0, Scripted("Edict", "Sorcery", "{R}",
            """{ "spell": { "targets": ["opponent"], "effects": [{ "sacrifice": 1, "who": "target", "filter": { "types": ["creature", "planeswalker"], "greatestManaValueAmongTheirs": true } }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(walker).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
    }

    [Fact]
    public void PreparedSpellFilterParsesOnACastTrigger()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "trigger": "castSpell", "filter": { "preparedSpell": true }, "effects": [{ "gainLife": 1 }], "text": "x" }] }
            """);
        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(script.Abilities));
        Assert.True(trigger.Filter!.PreparedSpell);
    }

    [Fact]
    public async Task PlayerDiscardsWatcherTriggersOnceForADiscardOfTwo()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Scripted("Discard Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "playerDiscards", "batchedPerPlayer": true, "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.InHand(P0, Scripted("Mind Dump", "Sorcery", "{R}", """{ "spell": { "effects": [{ "discard": 2 }] } }"""));
        int p1 = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(p1 + 1, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task DiscardedThisGainsItsOwnerLifeWhenDiscarded()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var card = s.InHand(P0, Scripted("Pale Note", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "discardedThis", "effects": [{ "gainLife": 3 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.InHand(P0, Scripted("Mind Dump", "Sorcery", "{R}", """{ "spell": { "effects": [{ "discard": 1 }] } }"""));
        s.Attacker.Discard = (_, _) => new[] { card };
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 3, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData(6, false)] // six cards in the graveyard: not yet
    [InlineData(7, true)]  // seven: castable
    public void CastOnlyIfGraveyardCountGatesCasting(int graveyard, bool castable)
    {
        var s = new Scenario();
        var creature = s.InHand(P0, Scripted("Gated Ogre", "Creature — Ogre", "{1}", """{ "castOnlyIf": { "graveyard": 7 } }""", power: "1", toughness: "1"));
        s.Lands(P0, 1);
        for (int i = 0; i < graveyard; i++) PutInGraveyard(s, P0, Arcanum.Cards.GenericCards.Mountain);
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        Assert.Equal(castable, s.Game.GetLegalActions(P0).OfType<CastSpell>().Any(a => a.Card == creature));
    }

    [Fact]
    public async Task RemoveUpToCountersTakesTheMostTheControllerChoosesWithinTheLimit()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var creature = s.Add(P0, Creature("Pile", 1, 1));
        s.Card(creature).Counters[CounterKind.PlusOnePlusOne] = 4;
        s.InHand(P0, Scripted("Thin Out", "Sorcery", "{R}",
            """{ "spell": { "targets": ["creature"], "effects": [{ "removeUpToCounters": 3, "from": "target" }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(creature).CounterCount(CounterKind.PlusOnePlusOne)); // the default number choice is the maximum, three
    }

    [Fact]
    public async Task NextSpellUncounterableAppliesToTheNextSpellCastThisTurn()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var first = s.InHand(P0, Scripted("Ward Call", "Sorcery", "{R}", """{ "spell": { "effects": [{ "nextSpellUncounterable": true }] } }"""));
        var second = s.InHand(P0, Scripted("Plain Note", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        bool seen = false;
        s.Attacker.Act = (_, legal) =>
        {
            seen |= s.Card(second).Zone == Zone.Stack && s.Card(second).Uncounterable; // checked while the second spell is on the stack
            return legal.OfType<CastSpell>().Where(c => c.Card == first).Cast<PlayerAction>().FirstOrDefault()
                ?? legal.OfType<CastSpell>().Where(c => c.Card == second).Cast<PlayerAction>().FirstOrDefault()
                ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.True(seen);
        Assert.False(s.Game.State.GetPlayer(P0).NextSpellUncounterableThisTurn);
    }

    [Theory]
    [InlineData("opponent", 1)]     // the spell targets an opponent: the trigger
    [InlineData("creature:you", 0)] // it targets a creature you control: no trigger
    public async Task SpellTargetsOpponentTriggersOnlyForASpellAimedAtAnOpponent(string target, int lifeGained)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Ally", 2, 2));
        s.Add(P1, Creature("Foe", 2, 2));
        s.Add(P0, Scripted("Spell Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "castSpell", "spellTargetsOpponent": true, "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.InHand(P0, Scripted("Aimed Ping", "Instant", "{R}", $$"""{ "spell": { "targets": ["{{target}}"], "effects": [{ "damage": 1, "to": "target" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + lifeGained, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData("creature:you", 1)]      // a spell targeting a creature you control: the spell's target matches
    [InlineData("creature:opponent", 0)] // a spell targeting an opponent's creature: neither the filter nor the target
    public async Task OrSpellTargetsTriggersOnTheFilterOrTheTargetOfTheSpell(string target, int lifeGained)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Ally", 2, 2));
        s.Add(P1, Creature("Foe", 2, 2));
        s.Add(P0, Scripted("Equipment Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "castSpell", "filter": { "subtype": "Equipment" }, "spellTargets": { "types": ["creature"], "controller": "you" }, "orSpellTargets": true, "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.InHand(P0, Scripted("Quick Pump", "Instant", "{R}", $$"""{ "spell": { "targets": ["{{target}}"], "effects": [{ "pump": [1, 1], "what": "target" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + lifeGained, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public void ManaTokenSacrificedForManaParsesItsAmountAndAnyColor()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [{ "tokens": 1, "token": { "name": "Ember Cache", "types": "Artifact", "tapForMana": "any", "sacrificeForMana": true, "manaAmount": 3 } }] } }
            """);
        var token = Assert.IsType<CreateTokens>(script.Spell!.Effects[0]).Token;
        Assert.True(token.SacrificeForMana);
        Assert.Equal(3, token.ManaAmount);
        Assert.Equal(new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }, token.TapForMana);
    }
}
