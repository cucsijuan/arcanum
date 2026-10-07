// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>"You can't lose the game and your opponents can't win the game" and drawing from an empty library (rules 104, 704.5b).</summary>
public class WinningAndLosingTests
{
    private static readonly CardDefinition Angel = Creature("Platinum Angel", 4, 4) with { Replaces = Replacements.YouCantLose };

    private static CardDefinition Sorcery(string name, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    [Fact]
    public async Task AnOpponentWhoCantLoseStopsEveryOpponentFromLosingToAWinEffect()
    {
        var p2 = new PlayerId(2);
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupPermanent(p2, Angel);
        game.SetupInHand(P0, Sorcery("Triumph", new WinGame()));
        players[0].Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        bool resolved = false;
        game.EventRaised += e => { if (e is CardMoved { To: State.Zone.Graveyard } m && game.State.GetCard(m.Card).Name == "Triumph") resolved = true; };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(resolved);
        Assert.False(game.State.GetPlayer(P1).HasLost); // P2's Angel: "your opponents can't win the game"
        Assert.False(game.State.GetPlayer(p2).HasLost);
        Assert.False(game.State.IsGameOver);
    }

    [Fact]
    public async Task ADrawFromAnEmptyLibraryWhileUnableToLoseIsForgottenAfterTheCheck()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        var angel = s.Add(P0, Angel);
        var draw = s.InHand(P0, Sorcery("Deep Draw", new DrawCards(40, Subject.You)));
        var doom = s.InHand(P0, Sorcery("Doom", new Destroy(Subject.Each(new ObjectFilter(Name: "Platinum Angel", Controller: ControllerFilter.Any)))));
        bool drewDry = false;
        s.Attacker.Act = (_, legal) =>
            legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == draw) is { } first ? first
            : legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == doom && drewDry) is { } second ? second
            : PassPriority.Instance;
        s.Game.EventRaised += e => { if (e is CardMoved m && m.Card == draw && m.To == State.Zone.Graveyard) drewDry = true; };
        await s.RunUntilTurn();
        Assert.True(drewDry);
        Assert.Equal(State.Zone.Graveyard, s.Card(angel).Zone);
        Assert.Empty(s.Game.State.GetPlayer(P0).Library);
        Assert.False(s.Game.State.GetPlayer(P0).HasLost); // 704.5b only sees draws since the last check
    }
}
