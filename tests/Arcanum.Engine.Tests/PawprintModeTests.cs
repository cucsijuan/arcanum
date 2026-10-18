// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>"Choose up to five {P} worth of modes. You may choose the same mode more than once."</summary>
public class PawprintModeTests
{
    /// <summary>{1} sorcery: {P} gain 1 life; {P}{P} gain 10 life; {P}{P}{P} gain 100 life.</summary>
    private static CardDefinition Feast() => new()
    {
        Name = "Burrow Feast", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Sorcery,
        Spell = CardScriptParser.Parse("""
            { "spell": { "pawprints": 5, "text": "Choose up to five {P} worth of modes.", "modes": [
              { "pawprints": 1, "text": "{P} — You gain 1 life.", "effects": [ { "gainLife": 1 } ] },
              { "pawprints": 2, "text": "{P}{P} — You gain 10 life.", "effects": [ { "gainLife": 10 } ] },
              { "pawprints": 3, "text": "{P}{P}{P} — You gain 100 life.", "effects": [ { "gainLife": 100 } ] } ] } }
            """).Spell,
    };

    private static async Task<(int Life, List<ModeRequest> Asked)> Cast(Func<ModeRequest, IReadOnlyList<int>?> choose)
    {
        var s = new Scenario();
        var asked = new List<ModeRequest>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Modes = (_, request) => { asked.Add(request); return choose(request); };
        s.Lands(P0, 1);
        s.InHand(P0, Feast());
        await s.RunUntilTurn();
        return (s.Game.State.GetPlayer(P0).Life, asked);
    }

    [Fact]
    public async Task TheSameModeCanBeChosenUntilTheBudgetIsSpent()
    {
        var (life, asked) = await Cast(r => r.Possible.Contains(1) ? new[] { 1 } : new[] { r.Possible[0] });
        Assert.Equal(20 + 10 + 10 + 1, life); // {P}{P} twice, then only a {P} mode fits
        Assert.Equal(3, asked.Count);
        Assert.Equal(new[] { 0 }, asked[2].Possible);
    }

    [Fact]
    public async Task ModesThatDontFitTheRestAreNotOffered()
    {
        var (life, asked) = await Cast(r => r.Possible.Contains(2) ? new[] { 2 } : r.Possible.Contains(1) ? new[] { 1 } : Array.Empty<int>());
        Assert.Equal(20 + 100 + 10, life);
        Assert.DoesNotContain(2, asked[1].Possible);
    }

    [Fact]
    public async Task ThePlayerMayStopEarlyButMustChooseOne()
    {
        var (life, asked) = await Cast(r => r.Min == 0 ? Array.Empty<int>() : new[] { 0 });
        Assert.Equal(21, life);
        Assert.Equal(1, asked[0].Min);
        Assert.Equal(0, asked[1].Min);
    }
}
