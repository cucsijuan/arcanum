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
/// Vocabulary for combat and casting: the planeswalker attack limit, artifact tokens that become Dragons, equip costs
/// reduced per counter, extra red from Mountains, behold as an additional cost and on entering, combat damage assigned
/// from a negative power, free casting from hand up to a mana value, casting cards exiled with a permanent, the
/// opponents-dealt-combat-damage trigger and the empty-library condition.
/// </summary>
public class CombatAndCastingVocabularyTests
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

    /// <summary>An equipment whose equip costs {3}, reduced by the counters on the creature it targets.</summary>
    private static CardDefinition CounterEquipment() => new()
    {
        Name = "Rally Pole", Types = CardType.Artifact, Subtypes = new[] { "Equipment" },
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility
            {
                Cost = new AbilityCost(ManaCost.Parse("{3}")), SorcerySpeed = true, IsEquip = true, CostReductionPerTargetCounter = true, Text = "Equip {3}",
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
                Effects = new Effect[] { new AttachSelf(Subject.TargetAt(0)) },
            },
        },
    };

    /// <summary>Puts cards into a player's graveyard before the game starts (they come from the hand, so they are real cards).</summary>
    private static void PutInGraveyard(Scenario s, PlayerId owner, int count)
    {
        var player = s.Game.State.GetPlayer(owner);
        for (int i = 0; i < count; i++)
        {
            var id = s.InHand(owner, Arcanum.Cards.GenericCards.Forest);
            player.Hand.Remove(id);
            s.Card(id).Zone = Zone.Graveyard;
            player.Graveyard.Add(id);
        }
    }

    /// <summary>Moves every card out of a player's library (into exile, so the cards stay accounted for).</summary>
    private static void EmptyLibrary(Scenario s, PlayerId owner)
    {
        var player = s.Game.State.GetPlayer(owner);
        foreach (var id in player.Library.ToList())
        {
            s.Card(id).Zone = Zone.Exile;
            player.Exile.Add(id);
        }
        player.Library.Clear();
    }

    [Fact]
    public async Task NoMoreThanOneCreatureCanAttackAPlaneswalkerEachCombat()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var cub = s.Add(P0, Creature("Cub", 1, 1));
        var walker = s.Add(P1, new CardDefinition
        {
            Name = "Gatehouse", Types = CardType.Planeswalker, Loyalty = 3, Keywords = new[] { Keywords.DisplayName(Keyword.OneAttackerAtMost) },
        });
        s.Card(walker).Counters[CounterKind.Loyalty] = 3; // without loyalty counters the walker dies as a state-based action
        AttackRequest? request = null;
        s.Attacker.Attack = (view, _, _) => { request = view.AttackRequest; return Array.Empty<AttackDeclaration>(); };
        await s.RunUntilTurn();
        Assert.NotNull(request);
        var both = new[] { new AttackDeclaration(bear, P1, walker), new AttackDeclaration(cub, P1, walker) };
        Assert.False(request!.IsLegal(both, out var reason));
        Assert.Contains("No more than one", reason);
        Assert.True(request.IsLegal(new[] { new AttackDeclaration(bear, P1, walker) }, out _));
        Assert.Single(request.Complete(both).Where(d => d.Planeswalker == walker));
    }

    [Fact]
    public async Task ArtifactTokensBecomeDragonsInsteadWhenTheyWouldBeCreated()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Dragon Kiln", "Enchantment", "{1}", """{ "artifactTokensBecomeDragons": true }"""));
        s.InHand(P0, Scripted("Treasure Note", "Sorcery", "{R}", """{ "spell": { "effects": [{ "tokens": 1, "token": "Treasure" }] } }"""));
        await s.RunUntilTurn();
        var mine = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Controller == P0).ToList();
        Assert.DoesNotContain(mine, c => c.Name == "Treasure");
        var dragon = Assert.Single(mine.Where(c => c.Name == "Dragon"));
        Assert.True(dragon.Definition.IsToken);
        Assert.Equal(5, dragon.Power);
        Assert.Equal(5, dragon.Toughness);
        Assert.Contains("R", dragon.Definition.Colors);
        Assert.True(dragon.Has(Keyword.Flying));
    }

    [Theory]
    [InlineData(2, true)]  // two +1/+1 counters on the target: the equip costs {1}, which the one Mountain pays
    [InlineData(0, false)] // no counters on any creature: {3}, more than the one Mountain makes
    public async Task EquipCostsOneLessForEachPlusOneCounterOnItsTarget(int counters, bool affordable)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var pole = s.Add(P0, CounterEquipment());
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = counters;
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            var equip = legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == pole);
            offered |= equip is not null;
            return equip is null ? PassPriority.Instance : (PlayerAction)equip;
        };
        await s.RunUntilTurn();
        Assert.Equal(affordable, offered);
        Assert.Equal(affordable, s.Card(pole).AttachedTo == bear);
    }

    [Fact]
    public async Task AfterMountainsAddExtraRedThisTurnATappedMountainAddsTwoRed()
    {
        var s = Casting();
        var mountain = s.Add(P0, Arcanum.Cards.GenericCards.Mountain);
        var first = s.InHand(P0, Scripted("Surge Note", "Sorcery", "{0}", """{ "spell": { "effects": [{ "mountainsAddExtraRedThisTurn": true }] } }"""));
        var second = s.InHand(P0, Scripted("Red Note", "Sorcery", "{R}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        bool firstCast = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (!firstCast && legal.OfType<CastSpell>().Any(c => c.Card == first)) { firstCast = true; return new CastSpell(first); }
            if (firstCast && view.Stack.Count == 0 && legal.OfType<CastSpell>().Any(c => c.Card == second)) return new CastSpell(second);
            return PassPriority.Instance;
        };
        var red = new List<ManaType>();
        s.Game.EventRaised += e => { if (e is ManaAdded { Source: var source } m && source == mountain) red.Add(m.Type); };
        await s.RunUntilTurn();
        Assert.Equal(new[] { ManaType.Red, ManaType.Red }, red);
    }

    [Theory]
    [InlineData(true, true)]   // a Tester to behold: the {R} spell is paid by the one Mountain
    [InlineData(false, false)] // nothing to behold: the other option needs a second mana
    public async Task BeholdAsAnAdditionalCostNeedsAMatchingPermanentOrAnotherMana(bool tester, bool castable)
    {
        var s = Casting();
        s.Lands(P0, 1);
        if (tester) s.Add(P0, Creature("Tester Bear", 1, 1) with { Subtypes = new[] { "Tester" } });
        var spell = s.InHand(P0, Scripted("Beheld Note", "Sorcery", "{R}",
            """{ "additionalCostOptions": [{ "cost": { "behold": { "subtype": "Tester" } } }, { "mana": "{1}" }], "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(castable, s.Game.Log.Any(e => e is SpellCast sc && sc.Card == spell));
    }

    [Theory]
    [InlineData(true, false)] // a Tester to behold: the land enters untapped
    [InlineData(false, true)] // nothing to behold: the land enters tapped
    public async Task LandEntersUntappedWhenItsControllerBeholdsAndTappedOtherwise(bool tester, bool tapped)
    {
        var s = new Scenario();
        if (tester) s.Add(P0, Creature("Tester Bear", 1, 1) with { Subtypes = new[] { "Tester" } });
        var shore = s.InHand(P0, Scripted("Probe Shore", "Land", "",
            """{ "chooseOnEnter": "beholdOrTapped", "enterReveal": { "subtype": "Tester" } }"""));
        s.Attacker.Choose = (_, request) => request.Options.Take(1).Select(c => c.Id).ToList(); // picks the card to behold
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Where(p => p.Card == shore).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(shore).Zone);
        Assert.Equal(tapped, s.Card(shore).Tapped);
    }

    [Theory]
    [InlineData(true, 18)]  // the keyword: the -2 power is assigned as 2 damage
    [InlineData(false, 20)] // without it: a negative power assigns no damage
    public async Task NegativePowerIsAssignedAsPositiveWithTheKeyword(bool keyword, int life)
    {
        var s = new Scenario();
        s.Add(P0, keyword
            ? Creature("Shade", -2, 2, Keyword.AssignsNegativePowerAsPositive)
            : Creature("Shade", -2, 2));
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P1).Life);
    }

    [Theory]
    [InlineData("{1}{R}", true)]  // mana value 2 with two creatures: cast without paying
    [InlineData("{2}{R}", false)] // mana value 3 is more than two
    public async Task SpellsFromHandAreCastFreeUpToTheNumberOfCreaturesYouControl(string manaCost, bool castable)
    {
        var s = Casting();
        s.Add(P0, Scripted("Free Charm", "Enchantment", "{1}", """{ "castFromHandFreeUpTo": { "count": { "types": ["creature"] } } }"""));
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Creature("Cub", 1, 1));
        var spell = s.InHand(P0, Scripted("Probe Note", "Sorcery", manaCost, """{ "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        await s.RunUntilTurn();
        Assert.Equal(castable, s.Game.Log.Any(e => e is SpellCast sc && sc.Card == spell));
    }

    [Theory]
    [InlineData(7, true)]  // seven cards in the graveyard: the exiled card can be cast
    [InlineData(6, false)] // six: it stays exiled and can't be cast
    public async Task CardsExiledWithAPermanentCanBeCastWithAnyManaWhileTheConditionHolds(int graveyard, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        PutInGraveyard(s, P0, graveyard);
        var holder = s.InHand(P0, Scripted("Probe Warden", "Creature — Spirit", "{1}",
            """{ "castExiledWithThisIf": { "graveyard": 7 }, "abilities": [{ "trigger": "enters", "effects": [{ "discardChosen": "opponents", "exileInstead": true }], "text": "x" }] }""",
            power: "1", toughness: "1"));
        var target = s.InHand(P1, Scripted("Stolen Note", "Sorcery", "{G}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        s.Attacker.Choose = (_, request) => request.Options.Where(c => c.Id == target).Select(c => c.Id).ToList();
        bool cast = false;
        bool legal = false;
        s.Attacker.Act = (_, actions) =>
        {
            legal |= actions.OfType<CastSpell>().Any(c => c.Card == target);
            if (!cast && actions.OfType<CastSpell>().Any(c => c.Card == holder)) { cast = true; return new CastSpell(holder); }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(target).Zone);
        Assert.Equal(holder, s.Card(target).ExiledWith?.Source);
        Assert.Equal(castable, legal);
    }

    [Fact]
    public async Task OpponentsDealtCombatDamageDuringYourTurnTriggersOncePerCombatDamageStep()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Watch Post", "Enchantment", "{1}",
            """{ "abilities": [{ "trigger": "opponentsDealtCombatDamageDuringYourTurn", "effects": [{ "gainLife": 1 }], "text": "x" }] }"""));
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Creature("Cub", 1, 1));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life); // both creatures hit the opponent in one step: one trigger
    }

    [Theory]
    [InlineData(true, 1)]  // no cards in the library: the condition holds
    [InlineData(false, 0)] // cards in the library: it doesn't
    public async Task LibraryEmptyConditionHoldsOnlyWithNoCardsInTheLibrary(bool empty, int gained)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Vault Charm", "Enchantment", "{1}",
            """{ "abilities": [{ "trigger": "enters", "if": "libraryEmpty", "effects": [{ "gainLife": 1 }], "text": "x" }] }"""));
        // An empty library at the start would lose the opening draw, so the library is emptied at the first decision.
        var act = s.Attacker.Act;
        bool emptied = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (empty && !emptied) { emptied = true; EmptyLibrary(s, P0); }
            return act(view, legal);
        };
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + gained, s.Game.State.GetPlayer(P0).Life);
    }
}
