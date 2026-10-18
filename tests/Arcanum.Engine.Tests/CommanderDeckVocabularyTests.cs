// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Vocabulary for deck-building cards: destroying everything except the tokens a spell made, life lost this turn and
/// poison counters, token batches, cards chosen on entering, exiled cards put into graveyards, delayed end-step effects,
/// revealing until a bottom order, attack triggers, incubate, impending and the Map token.
/// </summary>
public class CommanderDeckVocabularyTests
{
    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    /// <summary>The tokens on the battlefield, whoever controls them.</summary>
    private static int TokenCount(Scenario s) => s.Game.State.Battlefield.Count(id => s.Card(id).Definition.IsToken);

    [Fact]
    public void DestroyExceptCreatedIsAFlag()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "destroy": { "each": { "types": ["creature"] } }, "exceptCreated": true }] } }""");
        var destroy = Assert.IsType<Destroy>(script.Spell!.Effects[0]);
        Assert.True(destroy.ExceptCreatedThisWay);
    }

    [Theory]
    [InlineData(5, false, 5)] // X = 5: both bears die, the five tokens the spell made survive
    [InlineData(4, true, 4)]  // X = 4: nothing is destroyed, the bears and the four tokens stay
    public async Task DestroyExceptCreatedSparesTheTokensTheSpellMade(int x, bool bearsLive, int tokens)
    {
        var s = Casting();
        s.Attacker.Number = (_, _) => x;
        s.Lands(P0, x);
        var ownBear = s.Add(P0, Creature("Bear", 2, 2));
        var theirBear = s.Add(P1, Creature("Bear", 2, 2));
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [
                { "tokens": "X", "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "colors": ["W"] } },
                { "if": { "atLeast": "X", "value": 5 }, "then": [{ "destroy": { "each": { "types": ["creature"] } }, "exceptCreated": true }] }
            ] } }
            """);
        s.InHand(P0, new CardDefinition { Name = "Ash Bell", ManaCost = ManaCost.Parse("{X}"), Types = CardType.Sorcery, Spell = script.Spell });
        await s.RunUntilTurn();
        Assert.Equal(bearsLive ? Zone.Battlefield : Zone.Graveyard, s.Card(ownBear).Zone);
        Assert.Equal(bearsLive ? Zone.Battlefield : Zone.Graveyard, s.Card(theirBear).Zone);
        Assert.Equal(tokens, TokenCount(s));
    }

    [Fact]
    public void AffectedLifeLostThisTurnIsAQuantity()
    {
        using var doc = JsonDocument.Parse("\"affectedLifeLostThisTurn\"");
        var quantity = CardScriptParser.ParseQuantity(doc.RootElement);
        Assert.Equal(QuantityKind.AffectedLifeLostThisTurn, quantity.Kind);
    }

    [Fact]
    public async Task EachEndStepLosesAsMuchLifeAsTheOpponentLostThisTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var drain = CardScriptParser.Parse("""{ "spell": { "effects": [{ "loseLife": 3, "who": "opponents" }] } }""");
        s.InHand(P0, new CardDefinition { Name = "Ember Drain", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = drain.Spell });
        var echo = CardScriptParser.Parse("""{ "abilities": [{ "trigger": "eachEndStep", "effects": [{ "loseLife": "affectedLifeLostThisTurn", "who": "opponents" }], "text": "x" }] }""");
        s.Add(P0, new CardDefinition { Name = "Echo Vial", Types = CardType.Artifact, Abilities = echo.Abilities.ToArray() });
        await s.RunUntilTurn();
        // 20 - 3 from the spell - 3 more from the end step, which repeats the life the opponent lost this turn.
        Assert.Equal(14, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public void ToxicTokenAndHasToxicFilterParse()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [{ "tokens": 1, "token": { "name": "Venom Pup", "types": "Creature — Pup", "power": 1, "toughness": 1, "toxic": 1 } }] } }
            """);
        var token = Assert.IsType<CreateTokens>(script.Spell!.Effects[0]).Token;
        Assert.Equal(1, token.Toxic);
        using var doc = JsonDocument.Parse("""{ "hasToxic": true }""");
        Assert.True(CardScriptParser.ParseFilter(doc.RootElement).HasToxic);
    }

    [Fact]
    public void GreatestOpponentPoisonIsAQuantity()
    {
        using var doc = JsonDocument.Parse("\"greatestOpponentPoison\"");
        var quantity = CardScriptParser.ParseQuantity(doc.RootElement);
        Assert.Equal(QuantityKind.GreatestOpponentPoison, quantity.Kind);
    }

    [Fact]
    public async Task ToxicCreatureGivesPoisonForItsCombatDamage()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Venom Hound", 2, 2) with { Toxic = 2 });
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.GetPlayer(P1).Poison);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public void TokensCreatedTriggerParses()
    {
        var script = CardScriptParser.Parse("""{ "abilities": [{ "trigger": "tokensCreated", "filter": { "types": ["creature"] }, "effects": [{ "gainLife": 1 }], "text": "x" }] }""");
        var ability = Assert.IsType<TriggeredAbility>(script.Abilities[0]);
        Assert.Equal(TriggerEvent.TokensCreated, ability.Trigger);
        Assert.Equal(CardType.Creature, ability.Filter!.Types);
    }

    [Fact]
    public async Task TokensCreatedTriggersOncePerEffectThatMakesTokens()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var watch = CardScriptParser.Parse("""{ "abilities": [{ "trigger": "tokensCreated", "filter": { "types": ["creature"] }, "effects": [{ "gainLife": 1 }], "text": "x" }] }""");
        s.Add(P0, new CardDefinition { Name = "Hatch Watch", Types = CardType.Enchantment, Abilities = watch.Abilities.ToArray() });
        var hatchery = CardScriptParser.Parse("""{ "spell": { "effects": [{ "tokens": 3, "token": { "name": "Spawn", "types": "Creature — Spawn", "power": 1, "toughness": 1 } }] } }""");
        s.InHand(P0, new CardDefinition { Name = "Mire Hatchery", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = hatchery.Spell });
        await s.RunUntilTurn();
        // Three creature tokens from one effect make one batch: the watch gains 1 life, not 3.
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public void ChooseCardTypeOnEnterWithProtectionParsesToTheCardFlags()
    {
        var script = CardScriptParser.Parse("""{ "chooseOnEnter": "CardType", "protectionFromChosenCardType": true }""");
        Assert.Equal(EnterChoice.CardType, script.ChooseOnEnter);
        Assert.True(script.ProtectionFromChosenCardType);
        var definition = script.ApplyTo(new CardDefinition { Name = "Ward Totem", Types = CardType.Artifact });
        Assert.Equal(EnterChoice.CardType, definition.ChooseOnEnter);
        Assert.True(definition.ProtectionFromChosenCardType);
    }

    [Fact]
    public void PutExiledWithThisIntoGraveyardParsesBothBranches()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "putExiledWithThisIntoGraveyard": true, "ifLand": [{ "gainLife": 1 }], "ifNonland": [{ "gainLife": 2 }] }] } }""");
        var effect = Assert.IsType<PutExiledWithThisIntoGraveyard>(script.Spell!.Effects[0]);
        Assert.IsType<GainLife>(Assert.Single(effect.IfLand));
        Assert.IsType<GainLife>(Assert.Single(effect.IfNonland));
    }

    [Fact]
    public void TriggeredInGraveyardConditionParses()
    {
        using var doc = JsonDocument.Parse("\"triggeredInGraveyard\"");
        Assert.IsType<TriggeredInGraveyard>(CardScriptParser.ParseCondition(doc.RootElement));
    }

    [Fact]
    public void AtTheNextEndStepParsesToADelayedEndStepTrigger()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "atTheNextEndStep": { "effects": [{ "gainLife": "triggerAmount" }], "text": "x" }, "amount": 3 }] } }""");
        var effect = Assert.IsType<AtNextUpkeep>(script.Spell!.Effects[0]);
        Assert.True(effect.EndStep);
        Assert.Equal(3, effect.Amount!.Value);
    }

    [Fact]
    public async Task AtTheNextEndStepGainsTheAmountAtThatEndStep()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var later = CardScriptParser.Parse("""{ "spell": { "effects": [{ "atTheNextEndStep": { "effects": [{ "gainLife": "triggerAmount" }], "text": "x" }, "amount": 3 }] } }""");
        s.InHand(P0, new CardDefinition { Name = "Ember Vow", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = later.Spell });
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public void RevealUntilRestInBottomAnyOrderParses()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "revealUntil": { "types": ["land"] }, "to": "hand", "rest": "bottomAnyOrder" }] } }""");
        var reveal = Assert.IsType<RevealUntil>(script.Spell!.Effects[0]);
        Assert.True(reveal.RestBottomAnyOrder);
        Assert.False(reveal.RestToGraveyard);
    }

    [Fact]
    public void ExtraTriggersFromAttackingParsesWithItsOption()
    {
        var script = CardScriptParser.Parse("""{ "extraTriggersFromAttacking": true, "extraTriggersFromAttackingOption": "Mardu" }""");
        Assert.True(script.ExtraTriggersFromAttacking);
        Assert.Equal("Mardu", script.ExtraTriggersFromAttackingOption);
    }

    [Fact]
    public void IncubateParsesToTheIncubatorToken()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "incubate": 3 }] } }""");
        var incubate = Assert.IsType<Incubate>(script.Spell!.Effects[0]);
        Assert.Same(PredefinedTokens.Incubator, incubate.Token);
        Assert.Equal(CardType.Artifact, incubate.Token.Types);
        Assert.Equal(CardType.Artifact | CardType.Creature, incubate.Token.BackFace!.Types);
        Assert.Equal(0, incubate.Token.BackFace.Power);
        Assert.Equal(0, incubate.Token.BackFace.Toughness);
    }

    [Fact]
    public async Task IncubateMakesAnIncubatorWithThatManyCounters()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var incubate = CardScriptParser.Parse("""{ "spell": { "effects": [{ "incubate": 3 }] } }""");
        s.InHand(P0, new CardDefinition { Name = "Seed Tank", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = incubate.Spell });
        await s.RunUntilTurn();
        var incubator = s.Game.State.Battlefield.Select(s.Card).Single(c => c.Definition.Name == "Incubator");
        Assert.Equal(3, incubator.CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public void ImpendingParsesToTheCardCostAndCount()
    {
        var script = CardScriptParser.Parse("""{ "impending": { "count": 4, "cost": "{2}{W}{W}" } }""");
        var definition = script.ApplyTo(new CardDefinition { Name = "Dusk Colossus", Types = CardType.Creature });
        Assert.Equal("{2}{W}{W}", definition.Impending!.ToString());
        Assert.Equal(4, definition.Impending.ManaValue);
        Assert.Equal(4, definition.ImpendingCount);
    }

    [Fact]
    public void MapTokenIsPredefinedWithASorcerySpeedAbility()
    {
        var map = PredefinedTokens.ByName("map");
        Assert.NotNull(map);
        Assert.Equal(CardType.Artifact, map.Types);
        var ability = Assert.IsType<ActivatedAbility>(Assert.Single(map.Abilities));
        Assert.True(ability.SorcerySpeed);
    }
}
