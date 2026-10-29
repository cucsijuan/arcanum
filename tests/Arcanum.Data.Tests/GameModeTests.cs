// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Cards;
using Arcanum.Data.Formats;
using Arcanum.Data.GameModes;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;

namespace Arcanum.Data.Tests;

public class GameModeTests
{
    private static readonly FormatRules[] Formats =
    {
        new() { Id = "casual", Name = "Casual", MinDeckSize = 40 },
        new() { Id = "wide", Name = "Wide", Legality = "wide", MinDeckSize = 100, MaxCopies = 1, Commander = true, StartingLife = 40 },
    };

    private static IReadOnlyList<CardDefinition> Deck() =>
        Enumerable.Repeat(GenericCards.Forest, 17).Concat(Enumerable.Repeat(GenericCards.GladeCub, 13)).Concat(Enumerable.Repeat(GenericCards.Mountain, 3))
            .Concat(Enumerable.Repeat(GenericCards.OgreBrute, 7)).ToList();

    private static Game BotGame(GameModeScript mode, ulong seed = 7)
    {
        var a = new BotController(new PlayerId(0));
        var b = new BotController(new PlayerId(1));
        var game = new Game(mode.ApplyTo(new GameConfig { Seed = seed }), new[] { new PlayerSetup("A", a, Deck()), new PlayerSetup("B", b, Deck()) });
        Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
        a.UseCardRules(rules);
        b.UseCardRules(rules);
        return game;
    }

    private static async Task<Game> PlayAsync(string source, ulong seed = 7)
    {
        var game = BotGame(GameModeScript.Parse(source, Formats), seed);
        await game.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(game.State.IsGameOver);
        return game;
    }

    private static IEnumerable<string> Announced(Game game) => game.Log.OfType<ModeAnnounced>().Select(m => m.Text);

    [Fact]
    public void ADeclarationGivesTheDeckRulesGameSettingsAndEmblem()
    {
        var mode = GameModeScript.Parse("""
            mode {
              id = "big-hands", name = "Big Hands", description = "Bigger hands.",
              format = "wide",
              deck = { maxCopies = 2, commander = false },
              game = { startingLife = 25, handSize = 9, maxHandSize = 10, landsPerTurn = 2, freeMulligan = true, skipFirstDraw = false },
              players = { min = 2, max = 3 },
              emblem = { text = "Upkeep: gain 1.", abilities = { { trigger = "upkeep", effects = { { gainLife = 1 } }, text = "At the beginning of your upkeep, you gain 1 life." } } },
            }
            """, Formats);
        Assert.Equal("big-hands", mode.Id);
        Assert.Equal(64, mode.Hash.Length);
        Assert.Same(mode, mode.Format.Mode);
        Assert.Equal(("wide", 100, 2, false, 25), (mode.Format.Legality, mode.Format.MinDeckSize, mode.Format.MaxCopies, mode.Format.Commander, mode.Format.StartingLife));
        Assert.Equal((2, 3), (mode.MinPlayers, mode.MaxPlayers));
        Assert.Equal("Upkeep: gain 1.", mode.Emblem.OracleText);
        Assert.Single(mode.Emblem.Abilities);

        var config = mode.ApplyTo(new GameConfig());
        Assert.Equal((25, 9, 10, 2, true, false), (config.StartingLife, config.StartingHandSize, config.MaxHandSize, config.LandsPerTurn, config.FreeFirstMulligan, config.StartingPlayerSkipsDraw));
        Assert.Null(config.Commander);
        Assert.NotNull(config.Mode);
        Assert.NotSame(config.Mode!(), config.Mode!()); // one mode object per game
    }

    [Theory]
    [InlineData("local x = 1", "doesn't declare")]
    [InlineData("mode { id = \"x\", name = \"X\", colour = 1 }", "unknown field")]
    [InlineData("mode { id = \"x\", name = \"X\", api = 99 }", "API 99")]
    [InlineData("mode { id = \"Bad Id\", name = \"X\" }", "id")]
    [InlineData("mode { id = \"x\", name = \"X\", format = \"none\" }", "no format")]
    [InlineData("mode { id = \"x\", name = \"X\", deck = { copies = 1 } }", "unknown field")]
    [InlineData("mode { id = \"x\", name = \"X\", deck = { maxCopies = \"four\" } }", "wrong kind of value")]
    [InlineData("mode { id = \"x\", name = \"X\" ", "")]
    [InlineData("os.exit() mode { id = \"x\", name = \"X\" }", "")]
    [InlineData("io.open(\"f\") mode { id = \"x\", name = \"X\" }", "")]
    [InlineData("load(\"return 1\")() mode { id = \"x\", name = \"X\" }", "")]
    [InlineData("game.draw(1) mode { id = \"x\", name = \"X\" }", "only be used during the game")]
    [InlineData("while true do end", "too long")]
    public void BadScriptsAreRefused(string source, string message)
    {
        var e = Assert.Throws<FormatException>(() => GameModeScript.Parse(source, Formats));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task EveryPlayerGetsTheModeEmblemAndItsAbilitiesWork()
    {
        var game = await PlayAsync("""
            mode {
              id = "tithe", name = "Tithe",
              game = { startingLife = 30 },
              emblem = { abilities = { { trigger = "upkeep", effects = { { gainLife = 1 } }, text = "At the beginning of your upkeep, you gain 1 life." } } },
            }
            """);
        var emblems = game.State.Emblems.Select(game.State.GetCard).Where(c => c.Name == "Tithe").ToList();
        Assert.Equal(new[] { 0, 1 }, emblems.Select(e => e.Owner.Value).Order());
        Assert.Equal("At the beginning of your upkeep, you gain 1 life.", emblems[0].Definition.OracleText);
        Assert.Equal(30, game.Log.OfType<LifeChanged>().First().OldLife);
        foreach (var player in game.State.Players)
            Assert.Contains(game.Log, e => e is LifeChanged l && l.Player == player.Id && l.NewLife == l.OldLife + 1);
    }

    [Fact]
    public async Task HooksSeeTheGameAndChangeIt()
    {
        var game = await PlayAsync("""
            mode { id = "race", name = "Race" }
            local draws = 0
            function on_step(game, step)
              if step == "upkeep" then game.draw(game.active(), 1) end
            end
            function on_event(game, e)
              if e.type == "CardDrawn" and e.player == 1 then draws = draws + 1 end
            end
            function check(game)
              if game.turn() >= 6 then
                game.announce("player 1 drew " .. draws .. " cards, life " .. game.player(1).life)
                game.win(1)
              end
            end
            """);
        Assert.Equal(new PlayerId(0), game.State.Winner);
        Assert.Equal(6, game.State.TurnNumber);
        int drawn = game.Log.OfType<CardDrawn>().Count(d => d.Player == new PlayerId(0));
        Assert.Contains($"player 1 drew {drawn} cards, life {game.State.Players[0].Life}", Announced(game));
        Assert.True(drawn >= 7 + 2 + 3, $"{drawn} draws {string.Join(",", game.Log.OfType<TurnBegan>().Select(t => t.ActivePlayer.Value))}"); // opening hand, two or three draw steps, three upkeeps
        Assert.Contains(game.Log, e => e is PlayerLost { Reason: "A won the game" });
    }

    [Fact]
    public async Task QuestionsAndChoicesTakeTheSuggestedAnswerFromTheComputer()
    {
        var game = await PlayAsync("""
            mode { id = "ask", name = "Ask" }
            function on_start(game)
              if game.ask(1, "Gain 5 life?", true) then game.gain_life(1, 5) end
              local picked = game.choose(2, "Pick one", { "red", "green", "blue" }, 3)
              game.announce("picked " .. picked)
              game.lose(2, "picked blue")
            end
            """);
        Assert.Contains(game.Log, e => e is LifeChanged { NewLife: 25 } l && l.Player == new PlayerId(0));
        Assert.Contains("picked 3", Announced(game));
        Assert.Contains(game.Log, e => e is PlayerLost { Reason: "picked blue" });
        Assert.Equal(0, game.State.TurnNumber);
    }

    [Theory]
    [InlineData("function on_step(game, step) while true do end end", "too long")]
    [InlineData("function on_step(game, step) error(\"broken\") end", "broken")]
    [InlineData("function on_step(game, step) game.apply(1, { notAnEffect = 1 }) end", "aren't valid")]
    [InlineData("function check(game) game.gain_life(1, 1) end", "1000 times in a row")]
    [InlineData("function on_event(game, e) game.gain_life(1, 1) end", "events in a row")]
    public async Task AFailingModeStopsAndTheGameGoesOn(string hooks, string message)
    {
        var game = await PlayAsync("mode { id = \"broken\", name = \"Broken\" }\n" + hooks);
        var stopped = Assert.Single(Announced(game));
        Assert.Contains("stopped working", stopped);
        Assert.Contains(message, stopped);
        Assert.True(game.State.TurnNumber > 2);
    }

    [Fact]
    public async Task RandomNumbersComeFromTheGameSeed()
    {
        const string source = """
            mode { id = "dice", name = "Dice" }
            function on_step(game, step)
              if step == "upkeep" then game.announce("rolled " .. math.random(6) .. " " .. math.random(2, 3)) end
            end
            """;
        var first = Announced(await PlayAsync(source, seed: 11)).ToList();
        var second = Announced(await PlayAsync(source, seed: 11)).ToList();
        Assert.True(first.Count > 2);
        Assert.Equal(first, second);
        Assert.NotEqual(first, Announced(await PlayAsync(source, seed: 12)).ToList());
    }

    public static IEnumerable<object[]> ExampleModes()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Arcanum.csproj"))) root = Path.GetDirectoryName(root);
        if (root is null || !Directory.Exists(Path.Combine(root, "modes"))) yield break;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "modes"), "*.lua").Order()) yield return new object[] { Path.GetFileName(file), File.ReadAllText(file) };
    }

    [Theory]
    [MemberData(nameof(ExampleModes))]
    public async Task TheGamesExampleModesPlayWithoutFailing(string file, string source)
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var game = await PlayAsync(source, seed);
            Assert.DoesNotContain(Announced(game), a => a.Contains("stopped working"));
            Assert.True(game.State.Emblems.Count >= 2, file);
        }
    }

    [Fact]
    public async Task TheSandboxHasNoFilesOrOperatingSystem()
    {
        var game = await PlayAsync("""
            mode { id = "peek", name = "Peek" }
            function on_start(game)
              game.announce(tostring(os) .. tostring(io) .. tostring(load) .. tostring(require) .. tostring(dofile) .. tostring(coroutine) .. tostring(debug))
              game.lose(2, "done")
            end
            """);
        Assert.Contains("nilnilnilnilnilnilnil", Announced(game));
    }
}
