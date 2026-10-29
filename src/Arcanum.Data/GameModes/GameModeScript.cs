// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arcanum.Data.Formats;
using Arcanum.Data.Scripts;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.GameModes;
using MoonSharp.Interpreter;

namespace Arcanum.Data.GameModes;

/// <summary>
/// A game-mode plugin: a Lua script (modes/*.lua) that declares the mode with <c>mode { … }</c> (its name, deck rules,
/// game settings and each player's mode emblem) and may define hooks that change the game as it is played. Online the
/// host sends the script to the other players with the lobby; the hooks run on the host only. See docs/game-modes.md.
/// </summary>
public sealed partial class GameModeScript
{
    /// <summary>The plugin API this build offers; a script asking for a newer one is refused.</summary>
    public const int ApiVersion = 1;

    /// <summary>The longest script accepted (it travels to every player of an online game).</summary>
    public const int MaxSourceLength = 256 * 1024;

    public string Source { get; }

    /// <summary>SHA-256 of <see cref="Source"/> (hex): two players with the same hash have the same mode.</summary>
    public string Hash { get; }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    /// <summary>Deck rules (a format, with this mode as its <see cref="FormatRules.Mode"/>).</summary>
    public FormatRules Format { get; }

    public int MinPlayers { get; }
    public int MaxPlayers { get; }

    public int? StartingHandSize { get; }
    public int? MaxHandSize { get; }
    public int? LandsPerTurn { get; }
    public bool? FreeFirstMulligan { get; }
    public bool? StartingPlayerSkipsDraw { get; }

    /// <summary>Each player's mode emblem.</summary>
    public CardDefinition Emblem { get; }

    private static readonly string[] DeckKeys = { "legality", "minDeckSize", "maxDeckSize", "maxCopies", "sideboardMax", "basicLandsUnlimited", "commander" };
    private static readonly string[] GameKeys = { "startingLife", "handSize", "maxHandSize", "landsPerTurn", "freeMulligan", "skipFirstDraw" };
    private static readonly string[] ModeKeys = { "id", "name", "description", "api", "format", "deck", "game", "players", "emblem" };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    private GameModeScript(string source, Table declaration, IEnumerable<FormatRules> formats)
    {
        Source = source;
        Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        foreach (var key in declaration.Keys)
            if (key.Type != DataType.String || !ModeKeys.Contains(key.String)) throw new FormatException($"mode {{ … }} has an unknown field {key} (known: {string.Join(", ", ModeKeys)})");

        int api = (int)(declaration.Get("api").CastToNumber() ?? ApiVersion);
        if (api > ApiVersion) throw new FormatException($"this mode needs plugin API {api}; this version of the game has API {ApiVersion}");
        Id = declaration.Get("id").CastToString() is { } id && IdPattern().IsMatch(id) ? id : throw new FormatException("mode needs an id of lowercase letters, digits and dashes");
        Name = declaration.Get("name").CastToString() is { Length: > 0 } name ? name : throw new FormatException("mode needs a name");
        Description = declaration.Get("description").CastToString() ?? "";

        var players = Section(declaration, "players", new[] { "min", "max" });
        MinPlayers = (int)(players?.Get("min").CastToNumber() ?? 2);
        MaxPlayers = (int)(players?.Get("max").CastToNumber() ?? 4);
        if (MinPlayers < 2 || MaxPlayers < MinPlayers) throw new FormatException("players: min is 2 or more, max at least min");

        var game = Section(declaration, "game", GameKeys);
        int? Int(string key) => game?.Get(key).CastToNumber() is { } n ? (int)n : null;
        bool? Bool(string key) => game?.Get(key) is { Type: DataType.Boolean } b ? b.Boolean : null;
        StartingHandSize = Int("handSize");
        MaxHandSize = Int("maxHandSize");
        LandsPerTurn = Int("landsPerTurn");
        FreeFirstMulligan = Bool("freeMulligan");
        StartingPlayerSkipsDraw = Bool("skipFirstDraw");

        Format = DeckRules(declaration, formats, Int("startingLife")) with { Mode = this };
        Emblem = EmblemOf(declaration);
    }

    /// <summary>Reads a mode script; <paramref name="formats"/> are the formats a mode may build on (its <c>format</c>).</summary>
    /// <exception cref="FormatException">The script or its declaration is wrong (the message says how).</exception>
    public static GameModeScript Parse(string source, IEnumerable<FormatRules>? formats = null)
    {
        if (source.Length > MaxSourceLength) throw new FormatException($"the script is longer than {MaxSourceLength / 1024} KB");
        LuaGameMode read;
        try
        {
            read = LuaGameMode.Read(source, "mode");
        }
        catch (GameModeException e)
        {
            throw new FormatException(e.Message, e);
        }
        if (read.Declaration is not { } declaration) throw new FormatException("the script doesn't declare its mode: mode { id = …, name = … }");
        return new GameModeScript(source, declaration, formats ?? Array.Empty<FormatRules>());
    }

    /// <summary>A new mode object for one game (the script runs again: its globals start over).</summary>
    public IGameMode CreateMode() => LuaGameMode.ForGame(Source, Id, Name, Emblem);

    /// <summary>The game's settings with this mode's: its life, hand sizes, lands per turn, mulligan and first draw, and the mode itself.</summary>
    public GameConfig ApplyTo(GameConfig config) => config with
    {
        StartingLife = Format.StartingLife,
        StartingHandSize = StartingHandSize ?? config.StartingHandSize,
        MaxHandSize = MaxHandSize ?? config.MaxHandSize,
        LandsPerTurn = LandsPerTurn ?? config.LandsPerTurn,
        FreeFirstMulligan = FreeFirstMulligan ?? config.FreeFirstMulligan,
        StartingPlayerSkipsDraw = StartingPlayerSkipsDraw ?? config.StartingPlayerSkipsDraw,
        Commander = Format.Commander ? config.Commander ?? new CommanderRules() : null,
        Mode = CreateMode,
    };

    private static Table? Section(Table declaration, string name, string[] keys)
    {
        var value = declaration.Get(name);
        if (value.IsNil()) return null;
        if (value.Type != DataType.Table) throw new FormatException($"{name} must be a table");
        foreach (var key in value.Table.Keys)
            if (key.Type != DataType.String || !keys.Contains(key.String)) throw new FormatException($"{name} has an unknown field {key} (known: {string.Join(", ", keys)})");
        return value.Table;
    }

    /// <summary>The deck rules: those of the format named by <c>format</c> (or casual rules), changed by <c>deck</c>.</summary>
    private FormatRules DeckRules(Table declaration, IEnumerable<FormatRules> formats, int? startingLife)
    {
        var baseId = declaration.Get("format").CastToString();
        var based = baseId is null ? FormatRules.Casual
            : formats.FirstOrDefault(f => f.Id == baseId && !f.Limited) ?? throw new FormatException($"no format \"{baseId}\" to build the mode on");
        var fields = new Dictionary<string, object?>
        {
            ["id"] = Id, ["name"] = Name, ["description"] = Description, ["legality"] = based.Legality, ["minDeckSize"] = based.MinDeckSize,
            ["maxDeckSize"] = based.MaxDeckSize, ["maxCopies"] = based.MaxCopies, ["sideboardMax"] = based.SideboardMax,
            ["basicLandsUnlimited"] = based.BasicLandsUnlimited, ["commander"] = based.Commander, ["startingLife"] = startingLife ?? based.StartingLife,
        };
        if (Section(declaration, "deck", DeckKeys) is { } deck)
            foreach (var pair in deck.Pairs)
            {
                var kind = pair.Key.String switch
                {
                    "legality" => DataType.String,
                    "basicLandsUnlimited" or "commander" => DataType.Boolean,
                    _ => DataType.Number,
                };
                if (pair.Value.Type != kind || (kind == DataType.Number && pair.Value.Number != Math.Floor(pair.Value.Number)))
                    throw new FormatException($"deck: {pair.Key.String} has the wrong kind of value (it takes {(kind == DataType.Number ? "a whole number" : kind == DataType.String ? "a text" : "true or false")})");
                fields[pair.Key.String] = JsonSerializer.Deserialize<JsonElement>(LuaValues.ToJson(pair.Value));
            }
        return FormatRules.Parse(JsonSerializer.Serialize(fields));
    }

    /// <summary>
    /// The emblem: <c>emblem = { name = …, text = …, abilities = { … } }</c> in the card script vocabulary (any other card
    /// script field too); without one, an emblem with the mode's name and description.
    /// </summary>
    private CardDefinition EmblemOf(Table declaration)
    {
        var value = declaration.Get("emblem");
        if (value.IsNil()) return new CardDefinition { Name = Name, OracleText = Description };
        if (value.Type != DataType.Table) throw new FormatException("emblem must be a table");
        var script = new Table(value.Table.OwnerScript);
        foreach (var pair in value.Table.Pairs.Where(p => p.Key.String is not ("name" or "text")))
            script[pair.Key] = pair.Value;
        CardScript parsed;
        try
        {
            parsed = CardScriptParser.Parse(LuaValues.ToJson(DynValue.NewTable(script)) is "[]" ? "{}" : LuaValues.ToJson(DynValue.NewTable(script)));
        }
        catch (Exception e) when (e is not FormatException)
        {
            throw new FormatException($"emblem: {e.Message}", e);
        }
        var text = value.Table.Get("text").CastToString() ?? string.Join("\n", parsed.Abilities.Select(a => a.Text).Where(t => !string.IsNullOrEmpty(t)));
        return parsed.ApplyTo(new CardDefinition { Name = value.Table.Get("name").CastToString() ?? Name, OracleText = text, Abilities = parsed.Abilities });
    }
}
