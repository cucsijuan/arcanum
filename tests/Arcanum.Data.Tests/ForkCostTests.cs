// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Arcanum.Bots;
using Arcanum.Bots.Simulation;
using Arcanum.Data.CardData;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Data.Tests;

public class ForkCostTests
{
    /// <summary>
    /// Optional measure for bot simulation: with ARCANUM_MODULE_PATH and ARCANUM_FORK_COST (a deck name prefix) set, plays
    /// bot games with recorded seats, then replays each recording into a fresh game and checks it ends the same way.
    /// Prints the time of playing, of the bots' thinking, and of replaying.
    /// </summary>
    [Fact]
    public async Task ReplayRebuildsTheGameWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var prefix = Environment.GetEnvironmentVariable("ARCANUM_FORK_COST");
        if (string.IsNullOrEmpty(dir) || prefix is null) return;
        var cardFile = RealCardData.Require(dir);
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        var decks = module.DeckNames().Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(d))).Where(d => d.Commander.Count == 0)
            .Select(d => d.Main.SelectMany(e => Enumerable.Repeat(db.TryGet(e.Name, e.Set, e.Number, out var c) ? c : null!, e.Count)).ToList())
            .Where(d => d.All(c => c is not null)).Select(d => (IReadOnlyList<CardDefinition>)d).ToList();
        Assert.True(decks.Count >= 2);
        TimeSpan play = default, think = default, replay = default;
        int games = 0, mismatches = 0, decisions = 0;
        for (int g = 0; g < 10; g++)
        {
            var deckA = decks[g % decks.Count];
            var deckB = decks[(g + 1) % decks.Count];
            var config = new GameConfig { Seed = (ulong)(g + 1) };
            var bots = new[] { new BotController(new PlayerId(0)), new BotController(new PlayerId(1)) };
            var recorders = bots.Select(b => new Recorder(b)).ToArray();
            var game = new Game(config, new[] { new PlayerSetup("A", recorders[0], deckA), new PlayerSetup("B", recorders[1], deckB) });
            Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
            foreach (var b in bots) b.UseCardRules(rules);
            var sw = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { continue; }
            play += sw.Elapsed;
            think += recorders[0].Thinking + recorders[1].Thinking;
            decisions += recorders.Sum(r => r.Answers.Count);

            // The replay: nobody thinks; any decision past the recording would be an error.
            // ARCANUM_FORK_COST_REPEAT: replay each game that many times (to profile the replay).
            int repeat = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_FORK_COST_REPEAT"), out var rep) ? rep : 1;
            var never = new FailingController();
            Game copy = null!;
            sw.Restart();
            for (int k = 0; k < repeat; k++)
            {
                var replayers = recorders.Select(r => new Replayer(r.Answers, never)).ToArray();
                copy = new Game(config, new[] { new PlayerSetup("A", replayers[0], deckA), new PlayerSetup("B", replayers[1], deckB) });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await copy.RunAsync(cts.Token);
            }
            replay += sw.Elapsed / repeat;
            games++;
            if (copy.State.Winner != game.State.Winner || copy.State.TurnNumber != game.State.TurnNumber || copy.Log.Count != game.Log.Count) mismatches++;
        }
        Console.WriteLine($"fork cost: {games} games, {decisions / Math.Max(1, games)} decisions/game; play {play.TotalMilliseconds / games:0} ms/game " +
                          $"(bots thinking {think.TotalMilliseconds / games:0} ms), replay {replay.TotalMilliseconds / games:0} ms/game; {mismatches} replays differ");
        Assert.Equal(0, mismatches);
    }

    private sealed class FailingController : Arcanum.Engine.Players.IPlayerController
    {
        private static Task<T> Fail<T>() => throw new InvalidOperationException("decision past the recording");
        public Task<bool> KeepHandAsync(Arcanum.Engine.Views.GameView view, int mulligansTaken) => Fail<bool>();
        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(Arcanum.Engine.Views.GameView view, int count) => Fail<IReadOnlyList<CardId>>();
        public Task<Arcanum.Engine.Players.PlayerAction> ChooseActionAsync(Arcanum.Engine.Views.GameView view, IReadOnlyList<Arcanum.Engine.Players.PlayerAction> legalActions) => Fail<Arcanum.Engine.Players.PlayerAction>();
        public Task<IReadOnlyList<Arcanum.Engine.Players.ManaTap>?> ChooseManaPaymentAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.ManaPaymentRequest request) => Fail<IReadOnlyList<Arcanum.Engine.Players.ManaTap>?>();
        public Task<IReadOnlyList<Arcanum.Engine.Abilities.Target>?> ChooseTargetsAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.TargetRequest request) => Fail<IReadOnlyList<Arcanum.Engine.Abilities.Target>?>();
        public Task<IReadOnlyList<Arcanum.Engine.Players.AttackDeclaration>> DeclareAttackersAsync(Arcanum.Engine.Views.GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) => Fail<IReadOnlyList<Arcanum.Engine.Players.AttackDeclaration>>();
        public Task<IReadOnlyList<Arcanum.Engine.Players.BlockDeclaration>> DeclareBlockersAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.BlockRequest request) => Fail<IReadOnlyList<Arcanum.Engine.Players.BlockDeclaration>>();
        public Task<Arcanum.Engine.Players.DamageAssignment> AssignCombatDamageAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.DamageAssignmentRequest request) => Fail<Arcanum.Engine.Players.DamageAssignment>();
        public Task<bool> ChooseYesNoAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.YesNoRequest request) => Fail<bool>();
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(Arcanum.Engine.Views.GameView view, int count) => Fail<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.CardChoiceRequest request) => Fail<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<int>?> ChooseModesAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.ModeRequest request) => Fail<IReadOnlyList<int>?>();
        public Task<int> ChooseNumberAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.NumberRequest request) => Fail<int>();
        public Task<int> ChooseOptionAsync(Arcanum.Engine.Views.GameView view, Arcanum.Engine.Players.OptionRequest request) => Fail<int>();
    }
}
