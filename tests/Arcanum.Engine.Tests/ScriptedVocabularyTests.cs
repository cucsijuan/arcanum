// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
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
/// Script vocabulary played in real games: cards made from card scripts (invented names), so the parser and the rules are
/// checked together.
/// </summary>
public partial class ScriptedVocabularyTests
{
    /// <summary>A card made from a script, the way the content importer makes one.</summary>
    private static CardDefinition Scripted(string name, CardType types, string cost, string script, int power = 0, int toughness = 0,
        string[]? subtypes = null, string[]? colors = null, params string[] keywords)
    {
        var parsed = CardScriptParser.Parse(script);
        var card = new CardDefinition
        {
            Name = name, Types = types, ManaCost = ManaCost.Parse(cost), Power = power, Toughness = toughness,
            Subtypes = subtypes ?? Array.Empty<string>(), Colors = colors ?? Array.Empty<string>(), Keywords = keywords,
            Spell = parsed.Spell, Abilities = parsed.Abilities, EnchantTarget = parsed.Aura,
        };
        return parsed.ApplyTo(card);
    }

    private static CardDefinition Creature(string name, string script, int power = 1, int toughness = 1, string cost = "{1}", params string[] subtypes) =>
        Scripted(name, CardType.Creature, cost, script, power, toughness, subtypes);

    /// <summary>Moves a card to its owner's graveyard while the game runs (rules scenarios).</summary>
    private static void ToGraveyard(Scenario s, CardId id)
    {
        var card = s.Card(id);
        var owner = s.Game.State.GetPlayer(card.Owner);
        owner.Library.Remove(id);
        owner.Hand.Remove(id);
        s.Game.State.Battlefield.Remove(id);
        card.Zone = Zone.Graveyard;
        owner.Graveyard.Add(id);
    }

    /// <summary>Runs <paramref name="setup"/> before the first action, then plays: activations first, spells next, then passes.</summary>
    private static void Acting(Scenario s, Action? setup = null, int activations = 1)
    {
        bool first = true, settling = false;
        int used = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (first) { first = false; setup?.Invoke(); }
            // Let an activated ability resolve before doing anything else.
            if (settling) { settling = false; return PassPriority.Instance; }
            if (used < activations && legal.OfType<ActivateAbility>().FirstOrDefault() is { } activate) { used++; settling = true; return activate; }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    // ------------------------------------------------------------------ costs

    [Fact]
    public async Task SacrificeAnyCostCanSacrificeTheSourceItself()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var imp = s.Add(P0, Creature("Pitch Imp", """{ "abilities": [{ "cost": "sacrificeAny:creature", "effects": [{ "draw": 1 }] }] }"""));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(imp).Zone);
        Assert.Contains(s.Game.Log, e => e is AbilityActivated);
    }

    [Fact]
    public async Task SacrificeAnotherCostIsNotOfferedWithoutAnotherCreature()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Lonely Imp", """{ "abilities": [{ "cost": "sacrifice:creature", "effects": [{ "draw": 1 }] }] }"""));
        bool offered = false;
        s.Attacker.Act = (_, legal) => { offered |= legal.OfType<ActivateAbility>().Any(); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task ExilingACardOfAKindFromYourGraveyardIsACostOnlyItsKindCanPay()
    {
        var s = new Scenario();
        var marshal = s.Add(P0, Creature("Bone Warden", """{ "abilities": [{ "cost": "exileGraveyardCards:1:creature", "effects": [{ "gainLife": 1 }] }] }"""));
        var land = s.InHand(P0, GenericCards.Forest);
        var corpse = s.InHand(P0, Creature("Old Soldier", "{}"));
        bool offeredWithoutCreature = true;
        Acting(s, () => { ToGraveyard(s, land); });
        var act = s.Attacker.Act;
        int calls = 0;
        s.Attacker.Act = (v, legal) =>
        {
            var action = act(v, legal);
            if (++calls == 1) { offeredWithoutCreature = legal.OfType<ActivateAbility>().Any(); ToGraveyard(s, corpse); }
            return calls == 1 ? PassPriority.Instance : action;
        };
        await s.RunUntilTurn();
        Assert.False(offeredWithoutCreature); // only a land card in the graveyard
        Assert.Equal(Zone.Exile, s.Card(corpse).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(land).Zone);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Battlefield, s.Card(marshal).Zone);
    }

    // ------------------------------------------------------------------ quantities

    [Theory]
    [InlineData(20, 10)]
    [InlineData(7, 3)]
    [InlineData(1, 0)]
    public async Task HalfRoundedUpIsLostFromEachPlayersLifeTotal(int life, int expected)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.InHand(P0, Scripted("Winnowing Wind", CardType.Sorcery, "{R}", """{ "spell": { "effects": [{ "eachPlayer": [{ "loseLife": { "halfUp": "life" } }], "who": "everyone" }] } }"""));
        Acting(s, () => { s.Game.State.GetPlayer(P0).Life = life; s.Game.State.GetPlayer(P1).Life = life; }, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(expected, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task HalfRoundedUpOfHandAndCreaturesIsDiscardedAndSacrificedByEachPlayer()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        for (int i = 0; i < 3; i++) s.Add(P1, Creature($"Squire {i}", "{}"));
        s.InHand(P0, Scripted("Winnowing Wind", CardType.Sorcery, "{R}",
            """{ "spell": { "effects": [{ "eachPlayer": [{ "discard": { "halfUp": "handSize" } }], "who": "everyone" }, { "eachPlayer": [{ "sacrifice": { "halfUp": { "count": { "types": ["creature"] } } }, "filter": { "types": ["creature"] } }], "who": "everyone" }] } }"""));
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        // Three creatures: two are sacrificed (half of three, rounded up).
        Assert.Equal(1, s.Game.State.PermanentsControlledBy(P1).Count(c => c.IsCreature));
    }

    // ------------------------------------------------------------------ chosen name

    [Fact]
    public async Task LandsWithTheChosenNameLoseTheirTypesAndAbilitiesAndTapForAnyColor()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var quarry = s.Add(P1, new CardDefinition
        {
            Name = "Moss Quarry", Types = CardType.Land, Subtypes = new[] { "Forest" }, TapForMana = new[] { ManaType.Green },
        });
        var basic = s.Add(P1, GenericCards.Forest);
        s.InHand(P0, Scripted("Hush Stone", CardType.Enchantment, "{R}",
            """{ "chooseOnEnter": "cardName", "abilities": [{ "static": { "affects": "permanents", "filter": { "types": ["land"], "notSupertype": "basic", "controller": "opponent", "chosenName": true }, "setSubtypes": [], "losesAbilities": true, "grantsMana": "any" } }] }"""));
        s.Attacker.Option = (_, request) => request.Options.ToList().IndexOf("Moss Quarry");
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        var land = s.Card(quarry);
        Assert.Empty(land.CurrentSubtypes);
        Assert.Equal(5, land.ManaOptions.Single().Types.Count);
        Assert.True(s.Card(basic).HasSubtype("Forest")); // basic lands are never touched, whatever the name
    }

    [Fact]
    public async Task ANonbasicLandWithAnotherNameIsLeftAlone()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var other = s.Add(P1, new CardDefinition { Name = "Tidal Quarry", Types = CardType.Land, Subtypes = new[] { "Island" }, TapForMana = new[] { ManaType.Blue } });
        s.Add(P1, new CardDefinition { Name = "Moss Quarry", Types = CardType.Land, TapForMana = new[] { ManaType.Green } });
        s.InHand(P0, Scripted("Hush Stone", CardType.Enchantment, "{R}",
            """{ "chooseOnEnter": "cardName", "abilities": [{ "static": { "affects": "permanents", "filter": { "types": ["land"], "notSupertype": "basic", "controller": "opponent", "chosenName": true }, "setSubtypes": [], "losesAbilities": true, "grantsMana": "any" } }] }"""));
        s.Attacker.Option = (_, request) => request.Options.ToList().IndexOf("Moss Quarry");
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.True(s.Card(other).HasSubtype("Island"));
    }
}
