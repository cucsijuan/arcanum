// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Offspring (rule 702.175a): "You may pay an additional [cost] as you cast this spell" and "When this permanent enters, if
/// its offspring cost was paid, create a token that's a copy of it, except it's 1/1."
/// </summary>
public class OffspringTests
{
    /// <summary>{R} 3/3 with "Offspring {2}", read from its rules text.</summary>
    private static CardDefinition Pup()
    {
        var (definition, support) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-ember-pup", Name = "Ember Pup", Layout = "normal", TypeLine = "Creature — Hound", ManaCost = "{R}",
            Power = "3", Toughness = "3", Keywords = new[] { "Offspring" },
            OracleText = "Offspring {2} (You may pay an additional {2} as you cast this spell. If you do, when this creature enters, create a 1/1 token copy of it.)",
        });
        Assert.Equal(CardSupport.Full, support);
        return definition;
    }

    /// <summary>Player 0 casts what it can and never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static List<Card> Pups(Scenario s) =>
        s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Ember Pup").ToList();

    [Fact]
    public void OffspringLineGivesTheCost()
    {
        Assert.Equal("{2}", Pup().Offspring!.ToString());
    }

    [Fact]
    public async Task PayingTheOffspringCostCreatesA1Of1TokenCopy()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Pup());
        await s.RunUntilTurn();
        var pups = Pups(s);
        Assert.Equal(2, pups.Count);
        var original = pups.Single(c => c.Id == id);
        Assert.Equal(3, original.Power);
        Assert.False(original.Definition.IsToken);
        var token = pups.Single(c => c.Id != id);
        Assert.True(token.Definition.IsToken);
        Assert.Equal(1, token.Power);
        Assert.Equal(1, token.Toughness);
        Assert.True(token.HasSubtype("Hound"));
        Assert.Equal(3, s.Game.State.Battlefield.Count(c => s.Card(c).Tapped)); // {R} plus {2}
    }

    [Fact]
    public async Task DecliningTheOffspringCostCreatesNoToken()
    {
        var s = Casting();
        s.Attacker.YesNo = (_, _) => false;
        s.Lands(P0, 3);
        s.InHand(P0, Pup());
        await s.RunUntilTurn();
        Assert.Single(Pups(s));
        Assert.Equal(1, s.Game.State.Battlefield.Count(c => s.Card(c).Tapped));
    }

    [Fact]
    public async Task TheCostIsOnlyOfferedWhenItCanBePaid()
    {
        var s = Casting();
        var asked = new List<string>();
        s.Attacker.YesNo = (_, request) => { asked.Add(request.Prompt); return true; };
        s.Lands(P0, 2);
        s.InHand(P0, Pup());
        await s.RunUntilTurn();
        Assert.Single(Pups(s));
        Assert.DoesNotContain(asked, p => p.Contains("offspring"));
    }

    [Fact]
    public async Task TheTokenIsStillCreatedWhenTheCreatureLeftBeforeTheTriggerResolved()
    {
        var s = Casting();
        s.Lands(P0, 3);
        // The opponent's trigger goes on the stack after (above) the active player's offspring trigger and resolves first.
        var trap = CardScriptParser.Parse("""
            { "abilities": [ { "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "opponent" },
              "effects": [ { "destroy": "triggered" } ], "text": "Whenever a creature an opponent controls enters, destroy it." } ] }
            """);
        s.Add(P1, new CardDefinition { Name = "Watchful Trap", Types = CardType.Enchantment, Abilities = trap.Abilities });
        var id = s.InHand(P0, Pup());
        var created = new List<Card>();
        s.Game.EventRaised += e => { if (e is TokenCreated t) created.Add(s.Card(t.Card)); };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(id).Zone);
        // The token is a copy of the creature as it last existed on the battlefield, except it's 1/1.
        var token = Assert.Single(created);
        Assert.Equal("Ember Pup", token.Name);
        Assert.Equal(1, token.Definition.Power);
    }
}
