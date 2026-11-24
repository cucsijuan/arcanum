// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Arcanum.Bots;
using Arcanum.Bots.Simulation;
using Arcanum.Data.CardData;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Data.Tests;

public class GameCopyTests
{
    /// <summary>
    /// Optional check of copying a game: with ARCANUM_MODULE_PATH and ARCANUM_COPY_CHECK (a deck name prefix, or "all") set,
    /// plays bot games between the module's decks with recorded seats, then replays each one and, at copy points along the
    /// way, copies the game and plays the copy on with the same answers still to come: the copy must end exactly as the
    /// original did (the same events), and the original, played on after it, too. ARCANUM_COPY_GAMES sets how many games
    /// (default 20). Prints how long a copy takes next to replaying the game.
    /// </summary>
    [Fact]
    public async Task CopiesPlayOnLikeTheOriginalWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var prefix = Environment.GetEnvironmentVariable("ARCANUM_COPY_CHECK");
        if (string.IsNullOrEmpty(dir) || prefix is null) return;
        int games = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_COPY_GAMES"), out var n) ? n : 20;
        var cardFile = RealCardData.Require(dir);
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        var decks = module.DeckNames().Where(d => prefix == "all" || d.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(d))).Where(d => d.Commander.Count == 0)
            .Select(d => d.Main.SelectMany(e => Enumerable.Repeat(db.TryGet(e.Name, e.Set, e.Number, out var c) ? c : null!, e.Count)).ToList())
            .Where(d => d.Count >= 40 && d.All(c => c is not null)).Select(d => (IReadOnlyList<CardDefinition>)d).ToList();
        Assert.True(decks.Count >= 2);

        int played = 0, copies = 0, unsupported = 0, attackCopies = 0, blockCopies = 0;
        var failures = new List<string>();
        TimeSpan copying = default, replaying = default;
        // ARCANUM_COPY_FROM: the first game to play (to look at one game again).
        int from = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_COPY_FROM"), out var f0) ? f0 : 0;
        for (int g = from; g < from + games; g++)
        {
            var deckA = decks[(g * 7) % decks.Count];
            var deckB = decks[(g * 7 + 1 + g / decks.Count) % decks.Count];
            var config = new GameConfig { Seed = (ulong)(g + 1) };
            var bots = new[] { new BotController(new PlayerId(0)), new BotController(new PlayerId(1)) };
            var recorders = bots.Select(b => new Recorder(b)).ToArray();
            var game = new Game(config, new[] { new PlayerSetup("A", recorders[0], deckA), new PlayerSetup("B", recorders[1], deckB) });
            foreach (var b in bots) b.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                try { await game.RunAsync(cts.Token); }
                catch (OperationCanceledException) { continue; }
                catch (Exception e)
                {
                    Console.WriteLine($"game {g}: the bots' game itself failed (not a copy problem): {e.Message}");
                    if (games == 1)
                    {
                        foreach (var line in game.Log.TakeLast(15)) Console.WriteLine($"  event: {line}");
                        Console.WriteLine(e.StackTrace);
                    }
                    continue;
                }
            var expected = game.Log.Select(e => e.ToString()).ToList();
            played++;

            // The replay, copied at every so many copy points along the way.
            var answers = recorders.Select(r => r.Answers).ToArray();
            Game replay = null!;
            var replayers = answers.Select(a => new Replayer(a, new FailingController())).ToArray();
            int points = 0, every = Math.Max(1, answers.Sum(a => a.Count) / 40);
            var copyTasks = new List<string>();
            async Task AtDecision(string kind)
            {
                if (!replay.CanCopy || points++ % every != 0) return;
                var rest = replayers.Select(r => new Replayer(r.Remaining, new FailingController())).ToArray();
                var sw = Stopwatch.StartNew();
                Game copy;
                try { copy = replay.Copy(rest); }
                catch (NotSupportedException e) { unsupported++; copyTasks.Add(e.Message); return; }
                copying += sw.Elapsed;
                copies++;
                if (kind == "attackers") attackCopies++;
                if (kind == "blockers") blockCopies++;
                if (Aliased(replay, copy) is { } shared) failures.Add($"game {g}: the copy shares {shared}");
                string? problem = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await copy.RunAsync(cts.Token);
                    var got = copy.Log.Select(e => e.ToString()).ToList();
                    int differs = Enumerable.Range(0, Math.Min(got.Count, expected.Count)).FirstOrDefault(i => got[i] != expected[i], -1);
                    if (differs >= 0) problem = $"event {differs}: expected {expected[differs]}, got {got[differs]}";
                    else if (got.Count != expected.Count) problem = $"{got.Count} events, expected {expected.Count}";
                }
                catch (Exception e) when (e is not OperationCanceledException) { problem = e.GetType().Name + ": " + e.Message; }
                if (problem is not null) failures.Add($"game {g} copied at turn {replay.State.TurnNumber} {replay.State.Step}: {problem}");
            }
            var seats = replayers.Select(r => (IPlayerController)new Hook(r, AtDecision)).ToArray();
            replay = new Game(config, new[] { new PlayerSetup("A", seats[0], deckA), new PlayerSetup("B", seats[1], deckB) });
            var watch = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                await replay.RunAsync(cts.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException) { failures.Add($"game {g}: the original failed after being copied: {e.Message}"); }
            replaying += watch.Elapsed;
            if (!replay.Log.Select(e => e.ToString()).SequenceEqual(expected)) failures.Add($"game {g}: the original played on differently after being copied");
            foreach (var m in copyTasks.Distinct()) Console.WriteLine($"game {g}: not copied: {m}");
        }
        Console.WriteLine($"game copy: {played} games, {copies} copies played on ({attackCopies} at attackers, {blockCopies} at blockers, {unsupported} not supported), " +
                          $"{copying.TotalMilliseconds / Math.Max(1, copies):0.00} ms per copy; {failures.Count} differ");
        foreach (var f in failures.Take(20)) Console.WriteLine(f);
        foreach (var (type, count) in DeepCopier.CopiedByReflection) Console.WriteLine($"copied through reflection: {type} ({count})");
        Assert.Empty(failures);
        Assert.Empty(DeepCopier.CopiedByReflection); // every type has a generated copier (see CopierGenerator)
        Assert.Equal(0, unsupported);
    }

    /// <summary>A changeable object reachable from both games (outside their configuration and controllers), described by its path; null when none.</summary>
    private static string? Aliased(Game original, Game copy)
    {
        var seen = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        Walk(original, "game", o => { }, seen);
        string? found = null;
        Walk(copy, "copy", o => found ??= seen.TryGetValue(o.Obj, out var path) ? $"{o.Path} (also {path}, a {o.Obj.GetType()})" : null,
            new Dictionary<object, string>(ReferenceEqualityComparer.Instance));
        return found;
    }

    private static void Walk(object root, string rootPath, Action<(object Obj, string Path)> visit, Dictionary<object, string> seen)
    {
        var stack = new Stack<(object, string)>();
        stack.Push((root, rootPath));
        while (stack.Count > 0)
        {
            var (o, path) = stack.Pop();
            var type = o.GetType();
            if (Arcanum.Engine.Core.DeepCopier.IsShared(type) || o is Delegate || o is IPlayerController) continue;
            if (o is Array { Length: 0 }) continue; // nothing to change in it (empty lists share one)
            if (seen.ContainsKey(o)) continue;
            seen[o] = path;
            visit((o, path));
            if (o is Array array)
            {
                int i = 0;
                foreach (var e in array) { if (e is not null) stack.Push((e, $"{path}[{i}]")); i++; }
                continue;
            }
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (f.DeclaringType == typeof(Game) && f.Name.Contains("Config")) continue;
                    if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                    if (f.GetValue(o) is { } v) stack.Push((v, $"{path}.{f.Name}"));
                }
        }
    }

    /// <summary>Runs a hook before each action decision, then lets the replayer answer.</summary>
    private sealed class Hook : IPlayerController, IReplayingController
    {
        private readonly Replayer _inner;
        private readonly Func<string, Task> _before;
        public Hook(Replayer inner, Func<string, Task> before) { _inner = inner; _before = before; }
        public bool Replaying => _inner.Replaying;
        public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => _inner.KeepHandAsync(view, mulligansTaken);
        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => _inner.ChooseCardsToBottomAsync(view, count);
        public async Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions)
        {
            await _before("action");
            return await _inner.ChooseActionAsync(view, legalActions);
        }
        public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => _inner.ChooseManaPaymentAsync(view, request);
        public Task<IReadOnlyList<Arcanum.Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => _inner.ChooseTargetsAsync(view, request);
        public async Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders)
        {
            await _before("attackers");
            return await _inner.DeclareAttackersAsync(view, possibleAttackers, defenders);
        }
        public async Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request)
        {
            await _before("blockers");
            return await _inner.DeclareBlockersAsync(view, request);
        }
        public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => _inner.AssignCombatDamageAsync(view, request);
        public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => _inner.ChooseYesNoAsync(view, request);
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => _inner.ChooseDiscardAsync(view, count);
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => _inner.ChooseCardsAsync(view, request);
        public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => _inner.ChooseModesAsync(view, request);
        public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => _inner.ChooseNumberAsync(view, request);
        public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => _inner.ChooseOptionAsync(view, request);
    }

    private sealed class FailingController : IPlayerController
    {
        private static Task<T> Fail<T>() => throw new InvalidOperationException("decision past the recording");
        public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => Fail<bool>();
        public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => Fail<IReadOnlyList<CardId>>();
        public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) => Fail<PlayerAction>();
        public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => Fail<IReadOnlyList<ManaTap>?>();
        public Task<IReadOnlyList<Arcanum.Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => Fail<IReadOnlyList<Arcanum.Engine.Abilities.Target>?>();
        public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) => Fail<IReadOnlyList<AttackDeclaration>>();
        public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) => Fail<IReadOnlyList<BlockDeclaration>>();
        public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => Fail<DamageAssignment>();
        public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Fail<bool>();
        public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => Fail<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => Fail<IReadOnlyList<CardId>>();
        public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => Fail<IReadOnlyList<int>?>();
        public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => Fail<int>();
        public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => Fail<int>();
    }
}
