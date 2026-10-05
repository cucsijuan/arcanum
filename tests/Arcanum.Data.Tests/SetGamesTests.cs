// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Data.CardData;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;

namespace Arcanum.Data.Tests;

public class SetGamesTests
{
    /// <summary>
    /// Optional soak test of a real set: with ARCANUM_MODULE_PATH, ARCANUM_CARD_FILE and ARCANUM_SMOKE_SET (a set code)
    /// set, the computer plays ARCANUM_SMOKE_GAMES games (default 40) against itself with random two-color decks of the
    /// set's cards. Every game must run without an engine error; cards never cast are listed.
    /// </summary>
    [Fact]
    public async Task SetGamesPlayWithoutErrorsWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var cardFile = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        var code = Environment.GetEnvironmentVariable("ARCANUM_SMOKE_SET");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(cardFile) || string.IsNullOrEmpty(code)) return;
        int games = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_SMOKE_GAMES"), out var n) ? n : 40;
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        var set = module.LoadSets().First(s => s.Code == code);
        var pool = set.Cards.Select(name => db.TryGet(name, set.Code, null, out var d) ? d : null).OfType<CardDefinition>()
            .Where(d => !d.Is(CardType.Land) || (d.Supertypes & Supertype.Basic) == 0).ToList();
        var basics = new Dictionary<string, CardDefinition>();
        foreach (var (color, land) in new[] { ("W", "Plains"), ("U", "Island"), ("B", "Swamp"), ("R", "Mountain"), ("G", "Forest") })
            if (db.TryGet(land, out var basic)) basics[color] = basic;

        var cast = new HashSet<string>();
        var errors = new List<string>();
        for (int g = 0; g < games; g++)
        {
            var rng = new Random(1000 + g);
            IReadOnlyList<CardDefinition> Deck()
            {
                var colors = basics.Keys.OrderBy(_ => rng.Next()).Take(2).ToList();
                var spells = pool.Where(d => d.ColorList.All(colors.Contains) && (d.Adventure?.ColorList.All(colors.Contains) ?? true)).OrderBy(_ => rng.Next()).Take(23).ToList();
                var lands = Enumerable.Range(0, 17).Select(i => basics[colors[i % 2]]);
                return spells.Concat(lands).ToList();
            }
            var a = new BotController(new PlayerId(0));
            var b = new BotController(new PlayerId(1));
            var game = new Game(new GameConfig { Seed = (ulong)(g + 1) }, new[] { new PlayerSetup("A", a, Deck()), new PlayerSetup("B", b, Deck()) });
            Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
            a.UseCardRules(rules);
            b.UseCardRules(rules);
            game.EventRaised += e => { if (e is SpellCast c) lock (cast) cast.Add(game.State.GetCard(c.Card).PrintedDefinition.Name); };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await game.RunAsync(cts.Token); }
            catch (OperationCanceledException) { if (!game.State.IsGameOver) Console.WriteLine($"game {g}: still running after 30 s (turn {game.State.TurnNumber})"); }
            catch (Exception ex)
            {
                var recent = string.Join(" | ", game.Log.TakeLast(6).Select(e => e.ToString()));
                errors.Add($"game {g} turn {game.State.TurnNumber}: {ex.GetType().Name}: {ex.Message}\n   {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}\n   recent: {recent}");
            }
        }
        var never = pool.Select(d => d.Name).Where(name => !cast.Contains(name) && !(db.Find(name)?.Definition.Is(CardType.Land) ?? false)).ToList();
        Console.WriteLine($"set {code}: {games} games, {errors.Count} errors, {cast.Count} different cards cast; never cast: {string.Join(", ", never)}");
        foreach (var error in errors) Console.WriteLine(error);
        Assert.Empty(errors);
    }

    /// <summary>
    /// Optional soak test of a module's commander decks: with ARCANUM_MODULE_PATH, ARCANUM_CARD_FILE and ARCANUM_SMOKE_DECKS (a deck
    /// name prefix) set, the computer plays ARCANUM_SMOKE_GAMES four-player Commander games (default 20) with those decks. Every game
    /// must run without an engine error.
    /// </summary>
    [Fact]
    public async Task CommanderDeckGamesPlayWithoutErrorsWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var cardFile = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        var prefix = Environment.GetEnvironmentVariable("ARCANUM_SMOKE_DECKS");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(cardFile) || string.IsNullOrEmpty(prefix)) return;
        int games = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_SMOKE_GAMES"), out var n) ? n : 20;
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
        var decks = module.DeckNames().Where(d => d.StartsWith(prefix, StringComparison.Ordinal)).Select(d => Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(d))).ToList();
        Assert.NotEmpty(decks);
        List<CardDefinition> Cards(IEnumerable<Arcanum.Data.Decks.DeckEntry> entries) =>
            entries.SelectMany(e => Enumerable.Repeat(db.TryGet(e.Name, e.Set, e.Number, out var d) ? d : throw new InvalidOperationException($"Unknown card {e.Name}"), e.Count)).ToList();
        var cast = new HashSet<string>();
        var errors = new List<string>();
        int finished = 0;
        int start = int.TryParse(Environment.GetEnvironmentVariable("ARCANUM_SMOKE_START"), out var first) ? first : 0;
        for (int g = start; g < start + games; g++)
        {
            var bots = Enumerable.Range(0, 4).Select(i => new BotController(new PlayerId(i))).ToList();
            var setups = bots.Select((bot, i) =>
            {
                var deck = decks[(g + i) % decks.Count];
                return new PlayerSetup($"P{i}", bot, Cards(deck.Main), Cards(deck.Commander));
            }).ToArray();
            var game = new Game(new GameConfig { Seed = (ulong)(g + 1), StartingLife = 40, Commander = new CommanderRules() }, setups);
            Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
            foreach (var bot in bots) bot.UseCardRules(rules);
            game.EventRaised += e => { if (e is SpellCast c) lock (cast) cast.Add(game.State.GetCard(c.Card).PrintedDefinition.Name); };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await game.RunAsync(cts.Token); finished++; }
            catch (OperationCanceledException)
            {
                if (!game.State.IsGameOver)
                {
                    Console.WriteLine($"game {g}: still running after 90 s (turn {game.State.TurnNumber}, {game.Log.Count} events); recent: "
                                      + string.Join(" | ", game.Log.TakeLast(12).Select(e => e.ToString())));
                    if (game.State.PriorityPlayer is { } stuck)
                    {
                        var legal = game.GetLegalActions(stuck);
                        var pick = await bots[stuck.Value].ChooseActionAsync(game.ViewFor(stuck), legal);
                        string Describe(Arcanum.Engine.Players.PlayerAction a) => a switch
                        {
                            Arcanum.Engine.Players.CastSpell c => $"cast {game.State.GetCard(c.Card).Name} ({game.State.GetCard(c.Card).Zone})",
                            Arcanum.Engine.Players.ActivateAbility aa => $"activate {game.State.GetCard(aa.Source).Name} #{aa.Index}",
                            _ => a.ToString() ?? "",
                        };
                        Console.WriteLine($"   bot picks: {Describe(pick)}; legal: {string.Join(", ", legal.Select(Describe))}");
                    }
                }
            }
            catch (Exception ex)
            {
                var recent = string.Join(" | ", game.Log.TakeLast(8).Select(e => e.ToString()));
                errors.Add($"game {g} turn {game.State.TurnNumber}: {ex.GetType().Name}: {ex.Message}\n   {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}\n   recent: {recent}");
            }
        }
        var all = decks.SelectMany(d => d.Main.Concat(d.Commander)).Select(e => e.Name).Distinct()
            .Where(name => !(db.Find(name)?.Definition.Is(CardType.Land) ?? false)).ToList();
        Console.WriteLine($"decks {prefix}: {games} games ({finished} finished), {errors.Count} errors, {cast.Count} different cards cast; never cast: {string.Join(", ", all.Where(c => !cast.Contains(c)))}");
        foreach (var error in errors) Console.WriteLine(error);
        Assert.Empty(errors);
    }
}
