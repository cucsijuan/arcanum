// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Controlling another player (rule 722): during the controlled player's next turn, their controller makes all their choices and
/// decisions and sees everything they can see; the control starts with that turn, even if a turn is skipped first. The cards are invented.
/// </summary>
public class PlayerControlTests
{
    private const string Granted = "{15}, Exile Test Circlet: You control target opponent during their next turn. This ability costs {X} less to activate, where X is this creature's power. Activate only as a sorcery.";

    private static CardDefinition Circlet()
    {
        var script = JsonSerializer.Serialize(new
        {
            abilities = new object[]
            {
                new { @static = new { affects = "equipped", pump = new[] { 1, 1 } }, text = "Equipped creature gets +1/+1." },
                new
                {
                    @static = new
                    {
                        affects = "equipped",
                        grants = new object[]
                        {
                            new
                            {
                                cost = "{15}, exileGranter", costReductionFrom = new { power = "self" }, sorcery = true, targets = new[] { "opponent" },
                                effects = new object[] { new { controlPlayerNextTurn = "target" } }, text = Granted,
                            },
                        },
                    },
                    text = $"Equipped creature has \"{Granted}\"",
                },
            },
        });
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = "player-control-circlet", ["name"] = "Test Circlet", ["layout"] = "normal", ["mana_cost"] = "{2}",
            ["type_line"] = "Legendary Artifact — Equipment", ["keywords"] = new[] { "Equip" },
            ["oracle_text"] = $"Equipped creature gets +1/+1 and has \"{Granted}\"\nEquip {{1}}",
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var (definition, support) = CardFactory.Create(record, CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, support);
        return definition;
    }

    /// <summary>Player 0 has an 11/11 (12/12 equipped) wearing the circlet and four lands: the {15} ability costs {3}.</summary>
    private static (Scenario S, CardId Circlet, CardId Giant) Setup()
    {
        var s = new Scenario();
        var giant = s.Add(P0, Creature("Giant", 11, 11));
        var circlet = s.Game.SetupPermanent(P0, Circlet(), attachTo: giant);
        s.Lands(P0, 4);
        bool used = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (view.Viewer == P0 && !used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == giant) is { } activate)
            {
                used = true;
                return activate;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return (s, circlet, giant);
    }

    [Fact]
    public async Task TheControllerMakesTheControlledPlayersDecisionsDuringTheirNextTurnAndSeesTheirHand()
    {
        var (s, circlet, _) = Setup();
        var asked = new List<(int Turn, PlayerId Viewer, bool HandVisible)>();
        var defenderAskedOnTurn2 = 0;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            asked.Add((view.TurnNumber, view.Viewer, view.Players[1].Hand.All(c => !c.IsHidden)));
            return act(view, legal);
        };
        s.Defender.Act = (view, legal) =>
        {
            if (view.TurnNumber == 2) defenderAskedOnTurn2++;
            return PassPriority.Instance;
        };
        var controlled = new List<PlayerControlled>();
        s.Game.EventRaised += e => { if (e is PlayerControlled pc) controlled.Add(pc); };
        bool p0SawHand = false;
        s.Game.EventRaised += e =>
        {
            if (e is StepBegan { Step: Step.PrecombatMain } && s.Game.State.ActivePlayer == P1)
                p0SawHand = s.Game.ViewFor(P0).Players[1].Hand.Count > 0 && s.Game.ViewFor(P0).Players[1].Hand.All(c => !c.IsHidden)
                            && s.Game.ViewFor(P0).ControlledView?.Viewer == P1;
        };
        await s.RunUntilTurn(3);

        Assert.Equal(Zone.Exile, s.Card(circlet).Zone); // exiled to pay the cost
        Assert.Equal(new PlayerControlled(P1, P0), Assert.Single(controlled));
        Assert.Equal(0, defenderAskedOnTurn2);
        Assert.Contains(asked, a => a.Turn == 2 && a.Viewer == P1 && a.HandVisible);
        Assert.True(p0SawHand);
    }

    [Fact]
    public async Task TheControlEndsWithThatTurn()
    {
        var (s, _, _) = Setup();
        var defenderTurns = new HashSet<int>();
        s.Defender.Act = (view, _) =>
        {
            defenderTurns.Add(view.TurnNumber);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(5);

        Assert.DoesNotContain(2, defenderTurns);
        Assert.Contains(4, defenderTurns);
        Assert.Null(s.Game.State.ActivePlayerControlledBy);
        Assert.True(s.Game.ViewFor(P0).Players[1].Hand.All(c => c.IsHidden));
    }

    [Fact]
    public async Task ASkippedTurnDoesNotUseUpTheControl()
    {
        var (s, _, _) = Setup();
        // Player 1 skips their next turn: the control waits for the turn they actually take (turn 3).
        s.Game.EventRaised += e =>
        {
            if (e is PlayerControlled) return;
            if (e is TurnBegan { TurnNumber: 1 }) s.Game.State.GetPlayer(P1).TurnsToSkip = 1;
        };
        var controlledOn = new List<int>();
        s.Game.EventRaised += e => { if (e is PlayerControlled) controlledOn.Add(s.Game.State.TurnNumber); };
        await s.RunUntilTurn(4);

        Assert.Equal(new[] { 3 }, controlledOn);
    }
}
