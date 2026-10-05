// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
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
/// Rules mostly found in multiplayer sets: the monarch, voting, attack restrictions and the mechanics that come with them.
/// </summary>
public class MultiplayerMechanicsTests
{
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    private static CardDefinition Sorcery(string name, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    private static CardDefinition TargetedSorcery(string name, TargetSpec target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Targets = new[] { target }, Effects = effects },
    };

    [Fact]
    public async Task TheMonarchDrawsACardAtTheBeginningOfTheirEndStep()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Crowning", new BecomeMonarch(Subject.You)));
        int drawsAfterCrowning = -1;
        s.Game.EventRaised += e =>
        {
            if (e is MonarchChanged) drawsAfterCrowning = 0;
            else if (e is CardDrawn { Player.Value: 0 } && drawsAfterCrowning >= 0) drawsAfterCrowning++;
        };
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Game.State.Monarch);
        Assert.Equal(1, drawsAfterCrowning);
    }

    [Fact]
    public async Task ACreatureDealingCombatDamageToTheMonarchMakesItsControllerTheMonarch()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Raider", 2, 2));
        s.Game.State.Monarch = P1;
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Game.State.Monarch);
    }

    [Fact]
    public async Task ACardExiledUntilAnOpponentBecomesTheMonarchReturnsThen()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var guard = s.Add(P1, Creature("Guard", 2, 2));
        s.InHand(P0, TargetedSorcery("Jailing", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent),
            new BecomeMonarch(Subject.You), new ExileUntilOpponentIsMonarch(Subject.TargetAt(0))));
        bool exiled = false;
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved { To: Zone.Exile } m && m.Card == guard) exiled = true;
        };
        // Once the guard is exiled, the opponent becomes the monarch with a second spell.
        var crown = s.InHand(P0, TargetedSorcery("Abdication", new TargetSpec(TargetKind.Player, ControllerFilter.Opponent), new BecomeMonarch(Subject.TargetAt(0))));
        s.Attacker.Act = (v, legal) =>
            legal.OfType<CastSpell>().FirstOrDefault(c => c.Card != crown || exiled) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.True(exiled);
        Assert.Equal(P1, s.Game.State.Monarch);
        Assert.Equal(Zone.Battlefield, s.Card(guard).Zone);
        Assert.Equal(P1, s.Card(guard).Controller);
    }

    [Fact]
    public async Task APlayerWhoDoesNotPayCannotAttackThatPlayerThisCombat()
    {
        var s = new Scenario();
        var raider = s.Add(P0, Creature("Raider", 2, 2));
        // The defender's permanent: "At the beginning of combat on each opponent's turn, if you're the monarch, that opponent may
        // pay {X}, where X is the number of cards in their hand. If they don't, they can't attack you this combat."
        s.Add(P1, Creature("Warden", 1, 1) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.EachBeginCombat, Condition = new All(new Condition[] { new IsMonarch(), new Not(new YourTurn()) }),
                    Effects = new Effect[] { new PlayerMayPay(Subject.TriggeredPlayer, new Quantity(0, QuantityKind.AffectedHandSize), new Effect[] { new CantAttackYouThisCombat(Subject.TriggeredPlayer) }) },
                    Text = "toll",
                },
            },
        });
        s.Game.State.Monarch = P1;
        s.Attacker.YesNo = (_, _) => false;
        IReadOnlyList<CardId>? offered = null;
        s.Attacker.Attack = (_, attackers, defenders) => { offered = attackers; return Array.Empty<AttackDeclaration>(); };
        await s.RunUntilTurn();
        Assert.Null(offered); // no creature could attack: the only opponent is protected this combat
        Assert.False(s.Card(raider).Tapped);
        Assert.Equal(P1, s.Game.State.Monarch);
    }

    [Fact]
    public async Task TheMonarchControlsAPermanentThatSaysSo()
    {
        var s = new Scenario();
        var hound = s.Add(P0, Creature("Hound", 2, 2) with
        {
            Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Self)) { GivesControlToMonarch = true } },
        });
        s.Game.State.Monarch = P1;
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(P1, s.Card(hound).Controller);
    }

    [Fact]
    public async Task WhenTheMonarchLeavesTheGameTheActivePlayerBecomesTheMonarch()
    {
        var p2 = new PlayerId(2);
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players.Skip(1)) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        players[0].Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupInHand(P0, TargetedSorcery("Doom", new TargetSpec(TargetKind.Player, ControllerFilter.Opponent), new LoseGame { Who = Subject.TargetAt(0) }));
        players[0].Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        players[0].Targets = (_, r) => new[] { Target.Of(p2) };
        game.State.Monarch = p2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(game.State.GetPlayer(p2).HasLost);
        Assert.Equal(P0, game.State.Monarch);
    }
}
