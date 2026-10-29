// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.GameModes;
using Arcanum.Engine.State;
using MoonSharp.Interpreter;

namespace Arcanum.Data.GameModes;

/// <summary>
/// A game mode run from its Lua script, in a sandbox: no files, operating system, loading of other code or coroutines,
/// random numbers from the game, and a limit on how long each hook may run. One per game (the script's globals are its
/// state). The <c>game</c> table it gets is documented in docs/game-modes.md.
/// </summary>
internal sealed class LuaGameMode : IGameMode
{
    /// <summary>Lua instructions between two checks of the budget.</summary>
    private const int YieldEvery = 1_000;

    /// <summary>Budget checks a hook (or the script's first run) may pass: about two million instructions.</summary>
    private const int MaxChecks = 2_000;

    private const int MaxStringLength = 1_000_000;

    private const CoreModules Modules = CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators | CoreModules.Metatables
        | CoreModules.String | CoreModules.Table | CoreModules.Math | CoreModules.Bit32 | CoreModules.ErrorHandling | CoreModules.Coroutine;

    /// <summary>Lua helpers: the actions that wait for the game yield to <see cref="RunAsync"/>; then coroutines are taken away.</summary>
    private const string Prelude = """
        local yield = coroutine.yield
        coroutine = nil
        function game.apply(p, effects) return yield("apply", p, effects) end
        function game.ask(p, question, bot) return yield("ask", p, question, bot) end
        function game.choose(p, prompt, options, bot) return yield("choose", p, prompt, options, bot) end
        function game.draw(p, n) game.apply(p, { draw = n or 1 }) end
        function game.gain_life(p, n) game.apply(p, { gainLife = n }) end
        function game.lose_life(p, n) game.apply(p, { loseLife = n }) end
        function game.mill(p, n) game.apply(p, { mill = n }) end
        function game.emblem(p, emblem)
          local effect = { emblem = emblem.name or "Emblem", abilities = emblem.abilities }
          if emblem.text then effect.text = emblem.text end
          game.apply(p, effect)
        end
        """;

    private readonly Script _script;
    private readonly Dictionary<string, IReadOnlyList<Effect>> _effects = new();
    private Game.ModeHost? _host;

    public string Name { get; }
    public CardDefinition Emblem { get; }

    /// <summary>The table the script gave to <c>mode { … }</c>.</summary>
    public Table? Declaration { get; private set; }

    private LuaGameMode(string source, string chunkName, string name, CardDefinition? emblem)
    {
        Name = name;
        Emblem = emblem ?? new CardDefinition { Name = name };
        _script = new Script(Modules);
        _script.Options.DebugPrint = text => System.Diagnostics.Debug.WriteLine($"[{chunkName}] {text}");
        var game = new Table(_script);
        _script.Globals["game"] = game;
        _script.Globals["mode"] = DynValue.NewCallback((_, args) =>
        {
            if (Declaration is not null) throw new ScriptRuntimeException("mode { … } is given more than once");
            Declaration = args.Count > 0 && args[0].Type == DataType.Table ? args[0].Table : throw new ScriptRuntimeException("mode needs a table: mode { id = …, name = … }");
            return DynValue.Nil;
        });
        Bind(game);
        var math = _script.Globals.Get("math").Table;
        math["random"] = DynValue.NewCallback((_, args) => Random(args));
        math["randomseed"] = DynValue.NewCallback((_, _) => DynValue.Nil); // the game's seed decides
        var rep = _script.Globals.Get("string").Table.Get("rep");
        _script.Globals.Get("string").Table["rep"] = DynValue.NewCallback((ctx, args) =>
        {
            if (args.Count >= 2 && args[0].CastToString() is { } s && args[1].CastToNumber() is { } n && s.Length * n > MaxStringLength)
                throw new ScriptRuntimeException("string.rep: the string would be too long");
            return ctx.GetScript().Call(rep, args.GetArray());
        });
        _script.DoString(Prelude, null, "prelude");
        try
        {
            var chunk = _script.LoadString(source, null, chunkName);
            RunAsync(chunk).GetAwaiter().GetResult(); // no game yet: the actions that wait fail at once
        }
        catch (SyntaxErrorException e) { throw new GameModeException(e.DecoratedMessage ?? e.Message, e); }
    }

    /// <summary>Runs the script once to read its <c>mode { … }</c> declaration.</summary>
    public static LuaGameMode Read(string source, string chunkName) => new(source, chunkName, chunkName, null);

    /// <summary>A fresh mode for one game.</summary>
    public static LuaGameMode ForGame(string source, string chunkName, string name, CardDefinition emblem) => new(source, chunkName, name, emblem);

    public Task StartAsync(Game.ModeHost game) => CallHookAsync(game, "on_start");

    public Task StepAsync(Game.ModeHost game, Step step) => CallHookAsync(game, "on_step", DynValue.NewString(LuaValues.Camel(step.ToString())));

    public Task EventAsync(Game.ModeHost game, GameEvent e)
    {
        if (_script.Globals.Get("on_event").Type != DataType.Function) return Task.CompletedTask;
        return CallHookAsync(game, "on_event", DynValue.NewTable(LuaValues.Event(_script, e)));
    }

    public Task CheckAsync(Game.ModeHost game) => CallHookAsync(game, "check");

    private async Task CallHookAsync(Game.ModeHost host, string hook, params DynValue[] args)
    {
        var function = _script.Globals.Get(hook);
        if (function.Type == DataType.Nil) return;
        if (function.Type != DataType.Function) throw new GameModeException($"{hook} is not a function");
        _host = host;
        try
        {
            await RunAsync(function, args.Prepend(_script.Globals.Get("game")).ToArray());
        }
        finally
        {
            _host = null;
        }
    }

    /// <summary>
    /// Runs a function as a coroutine: each action that waits for the game (an effect, a player's answer) yields here,
    /// is done, and its answer resumes the function. The instruction budget is checked every <see cref="YieldEvery"/>.
    /// </summary>
    private async Task RunAsync(DynValue function, params DynValue[] args)
    {
        var co = _script.CreateCoroutine(function).Coroutine;
        co.AutoYieldCounter = YieldEvery;
        int checks = 0;
        DynValue result = Resume(co, args);
        while (co.State != CoroutineState.Dead)
        {
            if (co.State == CoroutineState.ForceSuspended)
            {
                if (++checks > MaxChecks) throw new GameModeException("it ran for too long");
                result = Resume(co, Array.Empty<DynValue>());
                continue;
            }
            var reply = await PerformAsync(result.Type == DataType.Tuple ? result.Tuple : new[] { result });
            result = Resume(co, new[] { reply });
        }
    }

    private static DynValue Resume(Coroutine co, DynValue[] args)
    {
        try
        {
            return co.Resume(args);
        }
        catch (InterpreterException e)
        {
            throw new GameModeException(e.DecoratedMessage ?? e.Message, e);
        }
    }

    private async Task<DynValue> PerformAsync(DynValue[] request)
    {
        string op = request.Length > 0 && request[0].Type == DataType.String ? request[0].String : "?";
        var host = Host($"game.{op}");
        DynValue Arg(int i) => i < request.Length ? request[i] : DynValue.Nil;
        switch (op)
        {
            case "apply":
                await host.ApplyAsync(PlayerArg(host, Arg(1), "game.apply"), Effects(Arg(2)));
                return DynValue.Nil;
            case "ask":
            {
                var player = PlayerArg(host, Arg(1), "game.ask");
                bool? bot = Arg(3).Type == DataType.Boolean ? Arg(3).Boolean : null;
                return DynValue.NewBoolean(await host.AskAsync(player, StringArg(Arg(2), "game.ask"), bot));
            }
            case "choose":
            {
                var player = PlayerArg(host, Arg(1), "game.choose");
                if (Arg(3).Type != DataType.Table) throw new GameModeException("game.choose needs a list of options");
                var options = Enumerable.Range(1, Arg(3).Table.Length).Select(i => Arg(3).Table.Get(i).CastToString() ?? "").ToList();
                int? bot = Arg(4).Type == DataType.Number ? (int)Arg(4).Number - 1 : null;
                if (bot is { } b && (b < 0 || b >= options.Count)) bot = null;
                return DynValue.NewNumber(await host.ChooseAsync(player, StringArg(Arg(2), "game.choose"), options, bot) + 1);
            }
            default:
                throw new GameModeException($"unknown action {op}");
        }
    }

    /// <summary>Effects in the card script vocabulary: one effect table or a list of them, without targets.</summary>
    private IReadOnlyList<Effect> Effects(DynValue value)
    {
        if (value.Type != DataType.Table) throw new GameModeException("game.apply needs an effect table, or a list of them");
        string json;
        try
        {
            json = LuaValues.ToJson(value);
        }
        catch (ScriptRuntimeException e)
        {
            throw new GameModeException($"game.apply: {e.Message}", e);
        }
        if (json.StartsWith('{')) json = $"[{json}]";
        if (_effects.TryGetValue(json, out var effects)) return effects;
        try
        {
            var spell = CardScriptParser.Parse("{\"spell\":{\"effects\":" + json + "}}").Spell!;
            effects = spell.Effects;
        }
        catch (Exception e) when (e is not GameModeException)
        {
            throw new GameModeException($"game.apply: these effects aren't valid ({e.Message}): {json}", e);
        }
        return _effects[json] = effects;
    }

    private Game.ModeHost Host(string what) => _host ?? throw new GameModeException($"{what} can only be used during the game (in on_start, on_step, on_event or check)");

    private static PlayerId PlayerArg(Game.ModeHost host, DynValue value, string what)
    {
        int n = value.Type == DataType.Number ? (int)value.Number : 0;
        if (n < 1 || n > host.State.Players.Count || n != value.Number) throw new GameModeException($"{what}: {value} is not a player of this game");
        return new PlayerId(n - 1);
    }

    private static string StringArg(DynValue value, string what) =>
        value.Type is DataType.String or DataType.Number ? value.CastToString() : throw new GameModeException($"{what} needs a text");

    // The functions that answer at once. Their errors are Lua errors (pcall can catch them).

    private PlayerId Player(DynValue value, string what)
    {
        var host = _host ?? throw new ScriptRuntimeException($"{what} can only be used during the game");
        int n = value.Type == DataType.Number ? (int)value.Number : 0;
        if (n < 1 || n > host.State.Players.Count || n != value.Number) throw new ScriptRuntimeException($"{what}: {value} is not a player of this game");
        return new PlayerId(n - 1);
    }

    private GameState State(string what) => (_host ?? throw new ScriptRuntimeException($"{what} can only be used during the game")).State;

    private DynValue Random(CallbackArguments args)
    {
        var host = _host ?? throw new ScriptRuntimeException("math.random can only be used during the game");
        double Next(int count) => host.Rng.Next(count);
        if (args.Count == 0) return DynValue.NewNumber(Next(1 << 30) / (1 << 30));
        int low = 1, high = (int)args[0].CastToNumber().GetValueOrDefault();
        if (args.Count >= 2) { low = high; high = (int)args[1].CastToNumber().GetValueOrDefault(); }
        if (high < low) throw new ScriptRuntimeException("math.random: the interval is empty");
        return DynValue.NewNumber(low + Next(high - low + 1));
    }

    private void Bind(Table game)
    {
        void Fn(string name, Func<CallbackArguments, DynValue> body) => game[name] = DynValue.NewCallback((_, args) => body(args));

        Fn("players", _ => List(State("game.players").Players.Select(p => LuaValues.Player(p.Id))));
        Fn("living", _ => List(State("game.living").LivingPlayers.Select(p => LuaValues.Player(p.Id))));
        Fn("opponents", a =>
        {
            var player = Player(a[0], "game.opponents");
            return List(State("game.opponents").OpponentsOf(player).Select(LuaValues.Player));
        });
        Fn("player", a => DynValue.NewTable(PlayerTable(Player(a[0], "game.player"))));
        Fn("turn", _ => DynValue.NewNumber(State("game.turn").TurnNumber));
        Fn("active", _ => LuaValues.Player(State("game.active").ActivePlayer));
        Fn("step", _ => DynValue.NewString(LuaValues.Camel(State("game.step").Step.ToString())));
        Fn("card", a =>
        {
            var state = State("game.card");
            int id = a[0].Type == DataType.Number ? (int)a[0].Number : 0;
            return state.Cards.TryGetValue(new CardId(id), out var card) ? DynValue.NewTable(CardTable(card)) : DynValue.Nil;
        });
        Fn("cards", a =>
        {
            var state = State("game.cards");
            var player = state.GetPlayer(Player(a[0], "game.cards"));
            IEnumerable<CardId> ids = (a[1].CastToString() ?? "") switch
            {
                "hand" => player.Hand,
                "library" => player.Library, // top first
                "graveyard" => player.Graveyard,
                "exile" => player.Exile,
                "command" => player.Command,
                "battlefield" => state.PermanentsControlledBy(player.Id).Select(c => c.Id),
                var zone => throw new ScriptRuntimeException($"game.cards: no zone \"{zone}\" (hand, library, graveyard, exile, command, battlefield)"),
            };
            return List(ids.Select(c => DynValue.NewNumber(c.Value)));
        });
        Fn("emblem_of", a => DynValue.NewNumber(_host!.EmblemOf(Player(a[0], "game.emblem_of")).Value));
        Fn("lose", a =>
        {
            var player = Player(a[0], "game.lose");
            _host!.Lose(player, a[1].CastToString() ?? $"lost by the rules of {Name}");
            return DynValue.Nil;
        });
        Fn("win", a => { _host!.Win(Player(a[0], "game.win")); return DynValue.Nil; });
        Fn("extra_turn", a => { _host!.ExtraTurn(Player(a[0], "game.extra_turn")); return DynValue.Nil; });
        Fn("announce", a =>
        {
            if (_host is null) throw new ScriptRuntimeException("game.announce can only be used during the game");
            _host.Announce(a[0].CastToString() ?? throw new ScriptRuntimeException("game.announce needs a text"));
            return DynValue.Nil;
        });
    }

    private DynValue List(IEnumerable<DynValue> values)
    {
        var table = new Table(_script);
        foreach (var value in values) table.Append(value);
        return DynValue.NewTable(table);
    }

    private Table PlayerTable(PlayerId id)
    {
        var state = _host!.State;
        var player = state.GetPlayer(id);
        var t = new Table(_script);
        t["id"] = id.Value + 1;
        t["name"] = player.Name;
        t["life"] = player.Life;
        t["poison"] = player.Poison;
        t["hand"] = player.Hand.Count;
        t["library"] = player.Library.Count;
        t["graveyard"] = player.Graveyard.Count;
        t["exile"] = player.Exile.Count;
        t["lost"] = player.HasLost;
        t["monarch"] = state.Monarch == id;
        return t;
    }

    private Table CardTable(Card card)
    {
        var t = new Table(_script);
        t["id"] = card.Id.Value;
        t["name"] = card.Name;
        t["zone"] = LuaValues.Camel(card.Zone.ToString());
        t["owner"] = card.Owner.Value + 1;
        t["controller"] = card.Controller.Value + 1;
        t["types"] = List(Enum.GetValues<CardType>().Where(type => type != 0 && card.Is(type)).Select(type => DynValue.NewString(type.ToString())));
        t["supertypes"] = List(Enum.GetValues<Supertype>().Where(s => s != 0 && (card.Supertypes & s) != 0).Select(s => DynValue.NewString(s.ToString())));
        t["subtypes"] = List(card.CurrentSubtypes.Select(DynValue.NewString));
        t["colors"] = List(card.Colors.Select(DynValue.NewString));
        if (card.IsCreature) { t["power"] = card.Power; t["toughness"] = card.Toughness; }
        t["tapped"] = card.Tapped;
        t["token"] = card.Definition.IsToken;
        var counters = new Table(_script);
        foreach (var (kind, count) in card.Counters.Where(c => c.Value > 0)) counters[LuaValues.Camel(kind.ToString())] = count;
        t["counters"] = counters;
        return t;
    }
}
