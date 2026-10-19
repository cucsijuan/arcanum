// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Ravenous (rule 702.156a): "This permanent enters with X +1/+1 counters on it" and "When this permanent enters, if X is 5 or more,
/// draw a card." Also the other numbered keyword lines read from rules text: toxic (702.164) and vanishing (702.63).
/// </summary>
public class RavenousTests
{
    /// <summary>{X}{R} 0/0 with ravenous.</summary>
    private static CardDefinition Glutton()
    {
        var (definition, support) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-moss-glutton", Name = "Moss Glutton", Layout = "normal", TypeLine = "Creature — Beast", ManaCost = "{X}{R}",
            Power = "0", Toughness = "0", Keywords = new[] { "Ravenous" },
            OracleText = "Ravenous (This creature enters with X +1/+1 counters on it. If X is 5 or more, draw a card when it enters.)",
        });
        Assert.Equal(CardSupport.Full, support);
        return definition;
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(5, 1)]
    public async Task ItEntersWithXCountersAndDrawsWhenXIsFiveOrMore(int x, int draws)
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Number = (_, _) => x;
        s.Lands(P0, x + 1);
        var id = s.InHand(P0, Glutton());
        int drawn = 0;
        bool entered = false; // the opening hand's draws don't count
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved { To: Zone.Battlefield } m && m.Card == id) entered = true;
            if (e is CardDrawn d && d.Player == P0 && entered) drawn++;
        };
        await s.RunUntilTurn();
        Assert.Equal(x, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(draws, drawn);
    }

    [Fact]
    public void ToxicAndVanishingLinesGiveTheirNumbers()
    {
        var (definition, support) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-fading-adder", Name = "Fading Adder", Layout = "normal", TypeLine = "Creature — Snake", ManaCost = "{1}{G}",
            Power = "2", Toughness = "2", Keywords = new[] { "Toxic", "Vanishing" },
            OracleText = "Toxic 2 (Players dealt combat damage by this creature also get two poison counters.)\n"
                         + "Vanishing 3 (This creature enters with three time counters on it. At the beginning of your upkeep, remove a time counter from it. When the last is removed, sacrifice it.)",
        });
        Assert.Equal(CardSupport.Full, support);
        Assert.Equal(2, definition.Toxic);
        Assert.Equal(3, definition.Vanishing);
    }
}
