// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Tests;

public static class Decks
{
    public static IReadOnlyList<CardDefinition> Of(params (CardDefinition Card, int Count)[] entries) =>
        entries.SelectMany(e => Enumerable.Repeat(e.Card, e.Count)).ToList();

    public static IReadOnlyList<CardDefinition> ForestCubs => Of((GenericCards.Forest, 20), (GenericCards.GladeCub, 20));

    public static Game NewGame(ulong seed, params (IReadOnlyList<CardDefinition> Deck, TestController Controller)[] players) =>
        new(new GameConfig { Seed = seed },
            players.Select((p, i) => new PlayerSetup($"Player {i + 1}", p.Controller, p.Deck)).ToList());

    public static async Task RunWithTimeout(this Game game, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await game.RunAsync(cts.Token);
    }
}
