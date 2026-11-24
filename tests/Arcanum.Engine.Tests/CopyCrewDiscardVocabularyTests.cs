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
/// Vocabulary for a discard that is followed by effects for the player who discarded, copies that enter from a graveyard
/// with their own name, power and toughness and an exile of the copied card, and a crew cost that counts a creature's crew
/// power bonus, plus a land that chooses a basic land type and may pay life to enter untapped.
/// </summary>
public class CopyCrewDiscardVocabularyTests
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

    private static CardDefinition Spell(string name, string cost, TargetSpec? target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = target is null ? Array.Empty<TargetSpec>() : new[] { target }, Effects = effects },
    };

    /// <summary>Activates abilities (up to <paramref name="activations"/>) before casting spells; never attacks.</summary>
    private static Scenario Acting(int activations = 1)
    {
        var s = new Scenario();
        int used = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (used < activations && legal.OfType<ActivateAbility>().FirstOrDefault() is { } activate) { used++; return activate; }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    /// <summary>A land that chooses a basic land type as it enters, and may pay 2 life to enter untapped; it has the chosen type.</summary>
    private static CardDefinition Passage() => new()
    {
        Name = "Test Passage", Types = CardType.Land, ChooseOnEnter = EnterChoice.BasicLandType, EnterLife = 2,
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Self)) { SetChosenLandType = true } },
    };

    /// <summary>A 4/4 legendary that enters as a copy of a creature card in any graveyard, keeping its name at 4/4 with three added subtypes, exiling the original.</summary>
    private static readonly CardDefinition MindSwapper = Creature("Mind Swapper", 4, 4) with
    {
        Supertypes = Supertype.Legendary,
        EntersAsCopyOf = new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any),
        EntersAsCopyFromGraveyards = true,
        EntersAsCopyKeepName = true,
        EntersAsCopyPower = 4,
        EntersAsCopyToughness = 4,
        EntersAsCopyExileOriginal = true,
        EntersAsCopyAddSubtypes = new[] { "Spider", "Human", "Hero" },
    };

    [Fact]
    public async Task TheChosenPlayerDrawsACardAfterTheChosenCardIsExiled()
    {
        var s = Acting();
        s.Lands(P0, 1);
        var hound = s.InHand(P1, Creature("Test Hound", 2, 2));
        var pick = s.InHand(P0, Spell("Test Pick", "{R}", new TargetSpec(TargetKind.Player, ControllerFilter.Opponent),
            new DiscardChosenByYou(Subject.TargetAt(0), new ObjectFilter(ExcludedTypes: CardType.Land))
            {
                ExileInstead = true,
                Then = new Effect[] { new DrawCards(1, Subject.TriggeredPlayer) },
            }));
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == hound).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        int castAt = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == pick);
        Assert.True(castAt >= 0);
        Assert.Equal(Zone.Exile, s.Card(hound).Zone);
        Assert.Contains(hound, s.Game.State.GetPlayer(P1).Exile);
        var draw = Assert.Single(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P1));
        Assert.Equal(Zone.Hand, s.Card(draw.Card).Zone);
        Assert.Empty(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P0));
    }

    [Fact]
    public async Task ALandThatChoosesABasicLandTypeEntersUntappedWhenItsControllerPaysTwoLife()
    {
        var s = new Scenario();
        var land = s.InHand(P0, Passage());
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault(p => p is PlayLand play && play.Card == land) ?? PassPriority.Instance;
        s.Attacker.Option = (_, request) => request.Options.ToList().IndexOf("Island");
        s.Attacker.YesNo = (_, _) => true;
        await s.RunUntilTurn();
        var card = s.Card(land);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.True(card.HasSubtype("Island"));
        Assert.False(card.Tapped);
        Assert.Equal(18, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ALandThatChoosesABasicLandTypeEntersTappedWhenItsControllerDoesNotPayLife()
    {
        var s = new Scenario();
        var land = s.InHand(P0, Passage());
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault(p => p is PlayLand play && play.Card == land) ?? PassPriority.Instance;
        s.Attacker.Option = (_, request) => request.Options.ToList().IndexOf("Island");
        s.Attacker.YesNo = (_, _) => false;
        await s.RunUntilTurn();
        var card = s.Card(land);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.True(card.HasSubtype("Island"));
        Assert.True(card.Tapped);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ACopyFromAGraveyardKeepsItsNameAndPowerAndAddsSubtypesAndExilesTheOriginal()
    {
        var s = Acting();
        s.Lands(P0, 1);
        var bear = PutInGraveyard(s, P1, Creature("Old Bear", 2, 2) with { Subtypes = new[] { "Bear" } });
        var swapper = s.InHand(P0, MindSwapper);
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == bear).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        var copy = s.Card(swapper);
        Assert.Equal(Zone.Battlefield, copy.Zone);
        Assert.Equal("Mind Swapper", copy.Name);
        Assert.Equal(4, copy.Power);
        Assert.Equal(4, copy.Toughness);
        Assert.True(copy.HasSubtype("Bear"));
        Assert.True(copy.HasSubtype("Spider"));
        Assert.True(copy.HasSubtype("Human"));
        Assert.True(copy.HasSubtype("Hero"));
        Assert.Equal(Zone.Exile, s.Card(bear).Zone); // the reflexive trigger exiled the copied card
    }

    [Fact]
    public async Task ACrewCostCountsTheCrewPowerBonusOfACreature()
    {
        var hauler = Scripted("Test Hauler", "Artifact — Vehicle", "{2}", """{ "abilities": [{ "cost": "crew:3", "effects": [{ "become": "self", "addTypes": ["creature"] }], "text": "Crew 3: This Vehicle becomes an artifact creature until end of turn." }] }""", "Invented.", "3", "3");
        var s = new Scenario();
        var vehicle = s.Add(P0, hauler);
        var crewer = s.Add(P0, Creature("Test Pawn", 1, 1) with { CrewPowerBonus = 2 });
        bool crewed = false, becameCreature = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!crewed && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == vehicle && a.Index == 0) is { } crew) { crewed = true; return crew; }
            if (crewed && s.Card(vehicle).IsCreature) becameCreature = true; // checked before the end of the turn ends the effect
            return PassPriority.Instance;
        };
        s.Attacker.Choose = (_, request) => request.Options.Where(o => o.Id == crewer).Select(o => o.Id).ToList();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(crewed);
        Assert.True(becameCreature);
        Assert.True(s.Card(crewer).Tapped); // the only creature tapped to pay the crew cost

        // A creature with power 1 and no bonus can't pay crew 3 alone.
        var plain = new Scenario();
        var lone = plain.Add(P0, hauler);
        plain.Add(P0, Creature("Test Pawn", 1, 1));
        bool offered = false;
        plain.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == lone && a.Index == 0);
            return PassPriority.Instance;
        };
        plain.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await plain.RunUntilTurn();
        Assert.False(offered);
        Assert.False(plain.Card(lone).IsCreature);
    }
}
