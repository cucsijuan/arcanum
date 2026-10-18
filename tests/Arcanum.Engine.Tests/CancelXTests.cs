// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Announcing X can be backed out of: the spell goes back where it was and nothing is paid.</summary>
public class CancelXTests
{
    [Fact]
    public async Task CancellingXLeavesTheSpellInHandUnpaid()
    {
        var s = new Scenario();
        var quake = new CardDefinition
        {
            Name = "Ground Shudder", ManaCost = ManaCost.Parse("{X}{R}"), Types = CardType.Instant,
            Spell = CardScriptParser.Parse("""{ "spell": { "effects": [ { "damage": "X", "to": { "each": { "types": ["creature"] } } } ], "text": "X damage to each creature." } }""").Spell,
        };
        var id = s.InHand(P0, quake);
        s.Lands(P0, 3);
        bool tried = false, offeredCancel = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!tried && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == id) is { } cast) { tried = true; return cast; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Number = (_, request) => { offeredCancel |= request.CanCancel; return NumberRequest.Cancelled; };
        await s.RunUntilTurn();
        Assert.True(tried);
        Assert.True(offeredCancel);
        Assert.Equal(Zone.Hand, s.Card(id).Zone);
        Assert.Equal(0, s.Game.State.Battlefield.Count(c => s.Card(c).Tapped));
    }
}
