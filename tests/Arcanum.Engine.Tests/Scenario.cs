// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Rules scenario: both players start with chosen permanents and land-only libraries, player 0 goes first, and
/// the game is stopped once a given turn begins so the outcome of a single combat can be inspected.
/// </summary>
public sealed class Scenario
{
    public static readonly PlayerId P0 = new(0), P1 = new(1);

    public TestController Attacker { get; } = new();
    public TestController Defender { get; } = new()
    {
        Attack = (_, _, _) => Array.Empty<Players.AttackDeclaration>(),
    };
    public Game Game { get; }

    public Scenario(int seed = 1)
    {
        var lands = Decks.Of((GenericCards.Forest, 20));
        Game = new Game(new GameConfig { Seed = (ulong)seed, StartingPlayer = P0 }, new[]
        {
            new PlayerSetup("Attacker", Attacker, lands),
            new PlayerSetup("Defender", Defender, lands),
        });
    }

    public CardId Add(PlayerId owner, CardDefinition card) => Game.SetupPermanent(owner, card);

    public State.Card Card(CardId id) => Game.State.GetCard(id);

    /// <summary>Runs until <paramref name="turn"/> begins (default: the end of player 0's first turn).</summary>
    public async Task RunUntilTurn(int turn = 2)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == turn) cts.Cancel(); };
        try { await Game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    public static CardDefinition Creature(string name, int power, int toughness, params Keyword[] keywords) => new()
    {
        Name = name,
        ManaCost = ManaCost.Parse("{1}"),
        Types = CardType.Creature,
        Power = power,
        Toughness = toughness,
        Keywords = keywords.Select(Keywords.DisplayName).ToList(),
    };
}
