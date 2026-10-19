// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Escape (rule 702.138): "You may cast this card from your graveyard by paying [cost] rather than paying its mana cost"; a permanent
/// cast with escape "escapes with" the counters its text says (702.138c).
/// </summary>
public class EscapeTests
{
    /// <summary>{R} 1/1 with "Escape—{2}, Exile two other cards from your graveyard" and "This creature escapes with two +1/+1 counters on it."</summary>
    private static CardDefinition Wraith()
    {
        var (definition, support) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-ash-wraith", Name = "Ash Wraith", Layout = "normal", TypeLine = "Creature — Spirit", ManaCost = "{R}",
            Power = "1", Toughness = "1", Keywords = new[] { "Escape" },
            OracleText = "Escape—{2}, Exile two other cards from your graveyard. (You may cast this card from your graveyard for its escape cost.)\n"
                         + "This creature escapes with two +1/+1 counters on it.",
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
    public void EscapeLinesGiveTheCostAndTheCounters()
    {
        var wraith = Wraith();
        Assert.Equal("{2}", wraith.Escape!.ToString());
        Assert.Equal(2, wraith.EscapeExtra!.ExileFromGraveyard);
        Assert.Equal(2, wraith.EscapesWithCounters);
    }

    [Fact]
    public async Task EscapingCastsItFromTheGraveyardExilingOtherCardsAndItEntersWithCounters()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var id = PutInGraveyard(s, P0, Wraith());
        var bones = new[] { PutInGraveyard(s, P0, Creature("Bone A", 1, 1)), PutInGraveyard(s, P0, Creature("Bone B", 1, 1)) };
        await s.RunUntilTurn();
        var wraith = s.Card(id);
        Assert.Equal(Zone.Battlefield, wraith.Zone);
        Assert.Equal(2, wraith.CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(3, wraith.Power);
        Assert.All(bones, b => Assert.Equal(Zone.Exile, s.Card(b).Zone));
    }

    [Fact]
    public async Task ItCantEscapeWithoutEnoughOtherCards()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var id = PutInGraveyard(s, P0, Wraith());
        PutInGraveyard(s, P0, Creature("Bone A", 1, 1));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(id).Zone);
    }

    [Fact]
    public async Task CastFromHandItHasNoCounters()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var id = s.InHand(P0, Wraith());
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
        Assert.Equal(0, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne));
    }
}
