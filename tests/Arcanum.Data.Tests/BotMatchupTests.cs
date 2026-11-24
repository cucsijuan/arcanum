// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Data.CardData;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Data.Tests;

public class BotMatchupTests
{
    /// <summary>
    /// Optional measure of a bot change: with ARCANUM_MODULE_PATH and ARCANUM_MATCHUP set (card data as in
    /// <see cref="RealCardData"/>), the bot that plays by its deck plan meets the bot without one on the module's 60-card
    /// decks whose names start with ARCANUM_MATCHUP (empty: "all"). Each pairing is played ARCANUM_MATCHUP_GAMES times
    /// (default 2) in each seat order with the same seeds, so only the bot differs. Prints the plan bot's share of the wins,
    /// overall and per deck. ARCANUM_MATCHUP_SAME=1 gives both seats the same bot (the measure's own noise).
    /// </summary>
    [Fact]
    public async Task PlanBotAgainstPlainBotWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var prefix = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP");
        if (string.IsNullOrEmpty(dir) || prefix is null) return;
        if (prefix == "all") prefix = "";
        int games = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_GAMES"), out var n) ? n : 2;
        bool same = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_SAME") == "1";
        var parts = Enum.TryParse<PlanParts>(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_PARTS"), out var chosen) ? chosen : PlanParts.All;
        var cardFile = RealCardData.Require(dir);
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        List<CardDefinition>? Cards(IEnumerable<Arcanum.Data.Decks.DeckEntry> entries)
        {
            var list = new List<CardDefinition>();
            foreach (var e in entries)
            {
                if (!db.TryGet(e.Name, e.Set, e.Number, out var d)) return null;
                list.AddRange(Enumerable.Repeat(d, e.Count));
            }
            return list;
        }
        var decks = module.DeckNames().Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => (Name: d, List: Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(d))))
            .Where(d => d.List.Commander.Count == 0)
            .Select(d => (d.Name, Cards: Cards(d.List.Main)))
            .Where(d => d.Cards is { Count: >= 40 })
            .Select(d => (d.Name, Cards: (IReadOnlyList<CardDefinition>)d.Cards!))
            .ToList();
        Assert.True(decks.Count >= 2, "need two decks");
        if (Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_PLANS") == "1")
            foreach (var d in decks) Console.WriteLine($"plan {d.Name}: {DeckPlan.Build(d.Cards, Array.Empty<CardDefinition>())}");

        // Pairings: each deck meets the next ones in a ring (all pairs when there are few decks).
        var pairs = new List<(int A, int B)>();
        int reach = decks.Count <= 12 ? decks.Count - 1 : 3;
        for (int i = 0; i < decks.Count; i++)
            for (int k = 1; k <= reach; k++)
            {
                int j = (i + k) % decks.Count;
                if (decks.Count <= 12 && j < i) continue;
                pairs.Add((i, j));
            }

        double planScore = 0;
        int played = 0, timeouts = 0, turns = 0;
        var perDeck = decks.ToDictionary(d => d.Name, _ => (Score: 0.0, Games: 0));
        // What the searching bot imagines in its opponent's hidden cards: "set" the cards of the decks' set (as in a
        // limited game), "seen" the sets of the cards it has seen, "none" more of the seen cards.
        var guessFrom = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_GUESS") ?? "seen";
        var guess = new CardPoolGuess(db, guessFrom == "set" ? prefix.Split('-')[0] : null, set => module.LoadDraftRatings(set));
        Arcanum.Bots.Simulation.CardCandidates? candidates = guessFrom == "none" ? null
            : seen => guess.For(seen).Select(x => new Arcanum.Bots.Simulation.CardGuess(x.Card, x.Weight)).ToList();
        // ARCANUM_MATCHUP_WORKERS: how many rollouts may run at once (a small device's processors).
        if (int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_WORKERS"), out var workers)) Arcanum.Bots.Simulation.Simulator.Workers = new SemaphoreSlim(workers);
        var sem = new SemaphoreSlim(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_SERIAL") == "1" ? 1 : Math.Max(1, Environment.ProcessorCount - 1));
        var tasks = new List<Task>();
        var gate = new object();
        foreach (var (a, b) in pairs)
            for (int g = 0; g < games; g++)
                foreach (bool planFirst in new[] { true, false })
                {
                    ulong seed = (ulong)(1 + g * 7919 + a * 104729 + b * 1299709);
                    int ia = a, ib = b;
                    // Each deck is played by the plan bot once per seed, against the plain bot with the other deck.
                    foreach (bool planOnA in new[] { true, false })
                    {
                        bool pf = planFirst, pa = planOnA;
                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync();
                            try
                            {
                                var (planDeck, plainDeck) = pa ? (decks[ia], decks[ib]) : (decks[ib], decks[ia]);
                                var plan = new BotController(new PlayerId(pf ? 0 : 1)) { Parts = parts };
                                var plain = new BotController(new PlayerId(pf ? 1 : 0)) { UsePlan = same };
                                var seats = pf
                                    ? new[] { new PlayerSetup("P", plan, planDeck.Cards), new PlayerSetup("Q", plain, plainDeck.Cards) }
                                    : new[] { new PlayerSetup("Q", plain, plainDeck.Cards), new PlayerSetup("P", plan, planDeck.Cards) };
                                var config = new GameConfig { Seed = seed };
                                Arcanum.Bots.Simulation.GameRecording? live = null;
                                if (Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_SEARCH") is { Length: > 0 } search)
                                {
                                    var (recording, recorded) = Arcanum.Bots.Simulation.GameRecording.Record(config, seats, candidates: candidates);
                                    seats = recorded;
                                    var knobs = search.Split('x');
                                    plan.Search = new Arcanum.Bots.Simulation.Simulator(recording, plan.Player)
                                        {
                                            Rollouts = int.Parse(knobs[0]), Horizon = knobs.Length > 1 ? int.Parse(knobs[1]) : 4,
                                            Budget = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_BUDGET"), out var ms) ? TimeSpan.FromMilliseconds(ms) : null,
                                            // ARCANUM_MATCHUP_COPY=0: rebuild the game for every rollout instead of copying it.
                                            UseCopies = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_COPY") != "0",
                                        };
                                    live = recording;
                                }
                                var game = new Game(config, seats);
                                if (live is not null) live.Live = () => game;
                                Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
                                plan.UseCardRules(rules);
                                plain.UseCardRules(rules);
                                plan.KnowDeck(planDeck.Cards);
                                plain.KnowDeck(plainDeck.Cards);
                                var logTo = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_LOG");
                                if (logTo is not null)
                                    game.EventRaised += e =>
                                    {
                                        if (e is Arcanum.Engine.Events.PriorityGiven or Arcanum.Engine.Events.ManaAdded or Arcanum.Engine.Events.StepBegan) return;
                                        var st = game.State;
                                        string Name(string id) => int.TryParse(id, out var v) && st.Cards.Keys.FirstOrDefault(k => k.Value == v) is { } cid && st.Cards.TryGetValue(cid, out var card) ? card.Name : "#" + id;
                                        var line = System.Text.RegularExpressions.Regex.Replace(e.ToString() ?? "", "#(\\d+)", m => Name(m.Groups[1].Value));
                                        if (e is Arcanum.Engine.Events.TurnBegan)
                                            line += "\n   " + string.Join("\n   ", st.Players.Select(p => $"P{p.Id.Value} life {p.Life} hand: {string.Join(", ", p.Hand.Select(h => st.GetCard(h).Name))}; board: {string.Join(", ", st.Battlefield.Select(st.GetCard).Where(c => c.Controller == p.Id).Select(c => c.Name + (c.Definition.Power is { } pw ? $" {pw}/{c.Definition.Toughness}" : "")))}"));
                                        lock (gate) File.AppendAllText(Path.Combine(logTo, $"{planDeck.Name}-vs-{plainDeck.Name}-{seed}-{pf}.log"), $"T{st.TurnNumber} P{st.ActivePlayer.Value} {st.Step} | {line}\n");
                                    };
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(plan.Search is null ? 60 : 1800));
                                bool timedOut = false;
                                try { await game.RunAsync(cts.Token); }
                                catch (OperationCanceledException) { timedOut = !game.State.IsGameOver; }
                                catch (Exception) { timedOut = true; }
                                double score = game.State.Winner is { } w ? (w == plan.Player ? 1 : 0) : 0.5;
                                lock (gate)
                                {
                                    if (timedOut) timeouts++;
                                    planScore += score;
                                    played++;
                                    turns += game.State.TurnNumber;
                                    var p = perDeck[planDeck.Name];
                                    perDeck[planDeck.Name] = (p.Score + score, p.Games + 1);
                                }
                            }
                            finally { sem.Release(); }
                        }));
                    }
                }
        await Task.WhenAll(tasks);
        double rate = planScore / played;
        double err = Math.Sqrt(rate * (1 - rate) / played);
        if (BotController.SearchedDecisions > 0)
            Console.WriteLine($"search: {BotController.SearchedDecisions} decisions, {TimeSpan.FromTicks(0).Add(System.Diagnostics.Stopwatch.GetElapsedTime(0, BotController.SearchTicks / BotController.SearchedDecisions)).TotalMilliseconds:0} ms each, longest {System.Diagnostics.Stopwatch.GetElapsedTime(0, BotController.LongestSearchTicks).TotalMilliseconds:0} ms; " +
                              $"{Arcanum.Bots.Simulation.Simulator.RoundsPlayed / (double)Math.Max(1, BotController.SearchedDecisions):0.0} rounds each, {Arcanum.Bots.Simulation.Simulator.OutOfTime} out of time, {Arcanum.Bots.Simulation.Simulator.CopiedRollouts} rollouts copied/{Arcanum.Bots.Simulation.Simulator.ReplayedRollouts} rebuilt, {turns / (double)Math.Max(1, played):0.0} turns/game, searching {System.Diagnostics.Stopwatch.GetElapsedTime(0, BotController.SearchTicks).TotalSeconds / Math.Max(1, turns / 2.0):0.00} s per own turn");
        Console.WriteLine($"matchup: {decks.Count} decks, {played} games, plan bot {rate:P1} ± {2 * err:P1} (95%), {timeouts} timeouts/errors");
        foreach (var (name, (score, count)) in perDeck.OrderBy(kv => kv.Value.Score / Math.Max(1, kv.Value.Games)))
            Console.WriteLine($"   {name,-34} {score / Math.Max(1, count):P0} of {count}");
    }

    /// <summary>
    /// The same measure in four-player Commander: with ARCANUM_MODULE_PATH and ARCANUM_MATCHUP_COMMANDER set (a deck name
    /// prefix, or "all"), ARCANUM_MATCHUP_GAMES tables (default 20) of four of the module's commander decks are each played
    /// four times with the same seed and decks, the plan bot in a different seat each time. Equal bots win 25%.
    /// </summary>
    [Fact]
    public async Task PlanBotInCommanderWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var prefix = Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_COMMANDER");
        if (string.IsNullOrEmpty(dir) || prefix is null) return;
        if (prefix == "all") prefix = "";
        int tables = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_GAMES"), out var n) ? n : 20;
        var parts = Enum.TryParse<PlanParts>(Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_PARTS"), out var chosen) ? chosen : PlanParts.All;
        var cardFile = RealCardData.Require(dir);
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        List<CardDefinition>? Cards(IEnumerable<Arcanum.Data.Decks.DeckEntry> entries)
        {
            var list = new List<CardDefinition>();
            foreach (var e in entries)
            {
                if (!db.TryGet(e.Name, e.Set, e.Number, out var d)) return null;
                list.AddRange(Enumerable.Repeat(d, e.Count));
            }
            return list;
        }
        var decks = module.DeckNames().Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => (Name: d, List: Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(d))))
            .Where(d => d.List.Commander.Count > 0)
            .Select(d => (d.Name, Main: Cards(d.List.Main), Commander: Cards(d.List.Commander)))
            .Where(d => d.Main is not null && d.Commander is not null)
            .Select(d => (d.Name, Main: (IReadOnlyList<CardDefinition>)d.Main!, Commander: (IReadOnlyList<CardDefinition>)d.Commander!))
            .ToList();
        Assert.True(decks.Count >= 4, "need four commander decks");
        if (Environment.GetEnvironmentVariable("ARCANUM_MATCHUP_PLANS") == "1")
            foreach (var d in decks) Console.WriteLine($"plan {d.Name}: {DeckPlan.Build(d.Main, d.Commander)}");

        double score = 0;
        int played = 0, unfinished = 0;
        var perDeck = decks.ToDictionary(d => d.Name, _ => (Score: 0.0, Games: 0));
        var sem = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
        var gate = new object();
        var tasks = new List<Task>();
        for (int t = 0; t < tables; t++)
        {
            var rng = new Random(500 + t);
            var table = Enumerable.Range(0, decks.Count).OrderBy(_ => rng.Next()).Take(4).Select(i => decks[i]).ToList();
            ulong seed = (ulong)(31 + t * 7919);
            for (int seat = 0; seat < 4; seat++)
            {
                int planSeat = seat;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var bots = Enumerable.Range(0, 4).Select(i => new BotController(new PlayerId(i)) { UsePlan = i == planSeat, Parts = parts }).ToList();
                        var setups = bots.Select((bot, i) => new PlayerSetup($"P{i}", bot, table[i].Main, table[i].Commander)).ToArray();
                        var game = new Game(new GameConfig { Seed = seed, StartingLife = 40, Commander = new CommanderRules() }, setups);
                        Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
                        for (int i = 0; i < 4; i++) { bots[i].UseCardRules(rules); bots[i].KnowDeck(table[i].Main, table[i].Commander); }
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                        bool done = true;
                        try { await game.RunAsync(cts.Token); }
                        catch (Exception) { done = game.State.IsGameOver; }
                        // An unfinished game: shared among the players still in it.
                        double s = game.State.Winner is { } w ? (w.Value == planSeat ? 1 : 0)
                            : game.State.GetPlayer(new PlayerId(planSeat)).HasLost ? 0 : 1.0 / Math.Max(1, game.State.LivingPlayers.Count());
                        lock (gate)
                        {
                            if (!done) unfinished++;
                            score += s;
                            played++;
                            var p = perDeck[table[planSeat].Name];
                            perDeck[table[planSeat].Name] = (p.Score + s, p.Games + 1);
                        }
                    }
                    finally { sem.Release(); }
                }));
            }
        }
        await Task.WhenAll(tasks);
        double rate = score / played;
        double err = Math.Sqrt(rate * (1 - rate) / played);
        Console.WriteLine($"commander matchup: {decks.Count} decks, {played} games, plan bot {rate:P1} ± {2 * err:P1} (95%; equal bots 25%), {unfinished} unfinished");
        foreach (var (name, (sc, count)) in perDeck.Where(kv => kv.Value.Games > 0).OrderBy(kv => kv.Value.Score / kv.Value.Games))
            Console.WriteLine($"   {name,-34} {sc / count:P0} of {count}");
    }
}
