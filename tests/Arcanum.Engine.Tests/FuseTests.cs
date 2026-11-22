// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Fuse (rule 702.102): a split card with fuse may be cast from its owner's hand with both halves, for both mana costs, the left
/// half's instructions followed by the right half's, each reading its own targets. The cards are invented.
/// </summary>
public class FuseTests
{
    private static CardDefinition SparkAndDrain()
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = "fuse-0", ["name"] = "Test Spark // Test Drain", ["layout"] = "split", ["mana_cost"] = "{1} // {2}",
            ["type_line"] = "Instant // Instant", ["keywords"] = new[] { "Fuse" },
            ["card_faces"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Test Spark", ["mana_cost"] = "{1}", ["type_line"] = "Instant",
                    ["oracle_text"] = "Test Spark deals 2 damage to target creature.\nFuse (You may cast one or both halves of this card from your hand.)" },
                new Dictionary<string, object?> { ["name"] = "Test Drain", ["mana_cost"] = "{2}", ["type_line"] = "Instant",
                    ["oracle_text"] = "Target player loses 3 life.\nFuse (You may cast one or both halves of this card from your hand.)" },
            },
        };
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse("""
            { "split": [
              { "spell": { "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }], "text": "invented" } },
              { "spell": { "targets": ["player"], "effects": [{ "loseLife": 3, "who": "target" }], "text": "invented" } } ] }
            """);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    private static void AimAtOpponent(Scenario s) => s.Attacker.Targets = (_, req) =>
    {
        var chosen = new List<Target>();
        for (int i = 0; i < req.Specs.Count; i++)
            chosen.Add(req.LegalAt(i).FirstOrDefault(t => req.IsAllowed(i, t, chosen) && (t.Player == P1 || t.Card is { } c && s.Card(c).Controller == P1)));
        return chosen;
    };

    [Fact]
    public async Task AFusedSpellPaysBothCostsAndFollowsBothHalvesEachWithItsOwnTargets()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 3);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var card = s.InHand(P0, SparkAndDrain());
        AimAtOpponent(s);
        var offered = new List<int?>();
        s.Attacker.Act = (_, legal) =>
        {
            var casts = legal.OfType<CastSpell>().Where(c => c.Card == card).ToList();
            offered.AddRange(casts.Select(c => c.Half));
            return (PlayerAction?)casts.FirstOrDefault(c => c.Half == CardDefinition.FusedHalves) ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Contains(0, offered);
        Assert.Contains(1, offered);
        Assert.Contains(CardDefinition.FusedHalves, offered);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
    }

    [Fact]
    public async Task WithoutManaForBothHalvesOnlyEachHalfIsOffered()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        s.Add(P1, Creature("Bear", 2, 2));
        var card = s.InHand(P0, SparkAndDrain());
        var offered = new List<int?>();
        s.Attacker.Act = (_, legal) =>
        {
            offered.AddRange(legal.OfType<CastSpell>().Where(c => c.Card == card).Select(c => c.Half));
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Contains(0, offered);
        Assert.DoesNotContain(CardDefinition.FusedHalves, offered);
    }
}
