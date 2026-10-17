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

/// <summary>Preparation cards ("While it's prepared, you may cast a copy of its spell. Doing so unprepares it.") and empower Jace.</summary>
public class PreparedTests
{
    private static int _next;

    /// <summary>A preparation card from card data and a script: the creature face, then its prepare spell.</summary>
    private static CardDefinition Preparation(string script, string creatureText = "This creature enters prepared.")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"prep-{_next++}", ["name"] = "Studious Owl // Owl Notes", ["layout"] = "prepare", ["mana_cost"] = "{R} // {R}",
            ["type_line"] = "Creature — Bird // Sorcery", ["keywords"] = new[] { "Prepared" },
            ["card_faces"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "Studious Owl", ["mana_cost"] = "{R}", ["type_line"] = "Creature — Bird", ["oracle_text"] = creatureText, ["power"] = "1", ["toughness"] = "1" },
                new Dictionary<string, object?> { ["name"] = "Owl Notes", ["mana_cost"] = "{R}", ["type_line"] = "Sorcery", ["oracle_text"] = "Draw a card." },
            },
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var (definition, support) = CardFactory.Create(record, CardScriptParser.Parse(script));
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, CardScriptParser.Parse(script))));
        return definition;
    }

    private const string EntersPrepared = """{ "entersPrepared": true, "prepare": { "spell": { "effects": [{ "draw": 1 }] } } }""";

    /// <summary>Player 0 casts whatever it can, the copy of a prepare spell first; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().LastOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardId? CopyIn(Scenario s, PlayerId player) =>
        s.Game.State.GetPlayer(player).Exile.Select(id => (CardId?)id).FirstOrDefault(id => s.Card(id!.Value).PreparedSpellOf is not null);

    [Fact]
    public async Task CreatureEntersPreparedAndItsControllerCastsTheCopyWhichUnpreparesIt()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var owl = s.InHand(P0, Preparation(EntersPrepared));
        await s.RunUntilTurn();
        var creature = s.Card(owl);
        Assert.Equal(Zone.Battlefield, creature.Zone);
        Assert.Null(creature.PreparedCopy); // cast the copy: unprepared
        Assert.Null(CopyIn(s, P0));
        int cast = s.Game.Log.ToList().FindLastIndex(e => e is SpellCast c && c.Card != owl);
        Assert.True(cast > 0);
        var copy = s.Card(((SpellCast)s.Game.Log[cast]).Card);
        Assert.Equal("Owl Notes", copy.Name);
        Assert.True(copy.CeasedToExist); // a copy, not a card: it doesn't stay in the graveyard
        Assert.DoesNotContain(copy.Id, s.Game.State.GetPlayer(P0).Graveyard);
        Assert.Contains(s.Game.Log.Skip(cast), e => e is CardDrawn { Player.Value: 0 });
    }

    [Fact]
    public async Task WithoutManaForTheCopyTheCreatureStaysPrepared()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var owl = s.InHand(P0, Preparation(EntersPrepared));
        await s.RunUntilTurn();
        Assert.NotNull(s.Card(owl).PreparedCopy);
        var copy = CopyIn(s, P0);
        Assert.NotNull(copy);
        Assert.Equal("Owl Notes", s.Card(copy!.Value).Name);
        Assert.Equal(owl, s.Card(copy.Value).PreparedSpellOf);
    }

    [Fact]
    public async Task OnlyTheControllerOfThePreparedCreatureMayCastTheCopyAndOnlyAtItsSpellsTiming()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Lands(P1, 2);
        var owl = s.InHand(P0, Preparation(EntersPrepared));
        await s.RunUntilTurn();
        var copy = CopyIn(s, P0)!.Value;
        s.Game.State.ActivePlayer = P1;
        s.Game.State.Step = Step.PrecombatMain;
        Assert.DoesNotContain(new CastSpell(copy), s.Game.GetLegalActions(P1));
        Assert.DoesNotContain(new CastSpell(copy), s.Game.GetLegalActions(P0)); // a sorcery: not on another player's turn
        s.Card(owl).Controller = P1; // control changes: the new controller may cast it
        Assert.Contains(new CastSpell(copy), s.Game.GetLegalActions(P1));
    }

    [Fact]
    public async Task LeavingTheBattlefieldUnpreparesAndTheCopyCeasesToExist()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var owl = s.InHand(P0, Preparation(EntersPrepared) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero, SacrificeSelf: true), Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "Sacrifice this creature: You gain 1 life." },
            },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Where(c => c.Card == owl).Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        CardId? seen = null;
        s.Game.EventRaised += e => { if (e is SpellCast c && c.Card == owl) seen = null; if (e is CardMoved { To: Zone.Battlefield } m && m.Card == owl) seen = s.Card(owl).PreparedCopy; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(owl).Zone);
        Assert.NotNull(seen);
        Assert.True(s.Card(seen!.Value).CeasedToExist);
        Assert.Null(CopyIn(s, P0));
    }

    [Fact]
    public async Task BecomesPreparedOnlyWithAPrepareSpellAndOnlyOnceAtATime()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var plain = s.Add(P0, Creature("Plain Bird", 1, 1));
        var owl = s.Add(P0, Preparation("""{ "prepare": { "spell": { "effects": [{ "draw": 1 }] } } }""", creatureText: ""));
        var preparer = s.InHand(P0, new CardDefinition
        {
            Name = "Ready All", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new BecomePrepared(Subject.Each(new ObjectFilter(CardType.Creature))), new BecomePrepared(Subject.Each(new ObjectFilter(CardType.Creature))) } },
        });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(preparer).Zone);
        Assert.Null(s.Card(plain).PreparedCopy);
        Assert.NotNull(s.Card(owl).PreparedCopy);
        Assert.Single(s.Game.State.GetPlayer(P0).Exile, id => s.Card(id).PreparedSpellOf == owl);
        Assert.Single(s.Game.Log, e => e is PreparedChanged { Prepared: true } p && p.Card == owl);
    }

    [Fact]
    public async Task EmpowerJaceCreatesTheTokenOnceThenAddsLoyaltyToIt()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var empower = new CardDefinition
        {
            Name = "Study Session", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant,
            Spell = new SpellAbility { Effects = new Effect[] { new EmpowerJace(3) } },
        };
        s.InHand(P0, empower);
        s.InHand(P0, empower);
        await s.RunUntilTurn();
        var jaces = s.Game.State.Battlefield.Select(s.Card).Where(c => c.HasSubtype("Jace")).ToList();
        var jace = Assert.Single(jaces);
        Assert.True(jace.PrintedDefinition.IsToken);
        Assert.True(jace.Is(CardType.Planeswalker));
        Assert.Equal(new[] { "U" }, jace.Colors);
        Assert.Equal(6, jace.CounterCount(CounterKind.Loyalty));
        Assert.Equal(2, jace.Abilities.OfType<ActivatedAbility>().Count(a => a.Cost.Loyalty is -1 or -3));
    }

    [Fact]
    public void PrepareVocabularyParses()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
              { "trigger": "upkeep", "if": { "not": "prepared" }, "effects": [{ "becomePrepared": "self" }], "text": "prepare" },
              { "trigger": "enters", "effects": [{ "becomeUnprepared": "self" }, { "empowerJace": 2 }], "text": "unprepare" } ] }
            """);
        var effects = script.Abilities.OfType<TriggeredAbility>().SelectMany(a => a.Effects).ToList();
        Assert.Contains(effects, e => e is BecomePrepared { Prepared: true });
        Assert.Contains(effects, e => e is BecomePrepared { Prepared: false });
        Assert.Contains(effects, e => e is EmpowerJace);
    }
}
