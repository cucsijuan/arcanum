// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Formats;
using Arcanum.Data.Modules;
using Godot;

namespace Arcanum.Client;

/// <summary>A match chosen on the play screen, picked up by the game board.</summary>
/// <param name="Seats">Seat 0 is the person in front of the screen; later seats are opponents (people or the computer).</param>
public sealed record MatchSetup(IReadOnlyList<GameSession.Seat> Seats, int StartingLife, bool Commander = false, bool Sandbox = false)
{
    /// <summary>A game that is part of an event: who won is reported back, and the game ends with a way back to it.</summary>
    public EventHook? Event { get; init; }

    /// <summary>A picture of a table both players share (its top half behind the opponent, its bottom half behind this player), instead of the playmats.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// At a <see cref="Table"/>, the playmat each seat puts on it (a built-in playmat id, or null for none). Where the mats
    /// lie comes from the JSON file next to the table's picture ("matTop", "matBottom").
    /// </summary>
    public IReadOnlyList<string?>? TableMats { get; init; }
}

/// <param name="RecordWinner">Called once when the game ends, with the winning seat index (null for a draw).</param>
/// <param name="ReturnScene">Where "Back to event" goes.</param>
public sealed record EventHook(Action<int?> RecordWinner, string ReturnScene, string Title)
{
    /// <summary>The game over screen's button back to <see cref="ReturnScene"/>.</summary>
    public string ReturnText { get; init; } = "Back to event";
}

/// <summary>
/// Autoload that owns app-wide services: content (module, cards, formats), settings, saved decks and navigation
/// between screens. Content starts loading as soon as the app starts; screens await <see cref="ContentReady"/>.
/// </summary>
public partial class App : Node
{
    public const string MainMenuScene = "res://scenes/main_menu/MainMenu.tscn";
    public const string PlaySetupScene = "res://scenes/play_setup/PlaySetup.tscn";
    public const string DeckBuilderScene = "res://scenes/deck_builder/DeckBuilder.tscn";
    public const string SettingsScene = "res://scenes/settings/SettingsScreen.tscn";
    public const string ExtrasScene = "res://scenes/extras/Extras.tscn";
    public const string GameBoardScene = "res://scenes/game_board/GameBoard.tscn";
    public const string LimitedScene = "res://scenes/limited/Limited.tscn";
    public const string OnlineScene = "res://scenes/online/Online.tscn";
    public const string CareerScene = "res://scenes/career/Career.tscn";
    public const string DeckDeskScene = "res://scenes/career/DeckDesk.tscn";
    public const string EventScene = "res://scenes/career/Event.tscn";

    public const string SourceUrl = "https://github.com/cucsijuan/arcanum";

    public static App Instance { get; private set; } = null!;

    private readonly TaskCompletionSource<bool> _contentReady = new();

    public ContentLoader Content { get; } = new();
    public DeckStore Decks { get; } = new();
    public LimitedService Limited { get; } = new();
    public OnlineService Online { get; } = new();
    public CareerService Career { get; } = new();
    public List<FormatRules> Formats { get; } = new();

    /// <summary>Latest content loading message, for screens that open while loading is still going on.</summary>
    public string ContentStatus { get; private set; } = "Starting…";

    public event Action<string>? ContentProgress;

    /// <summary>Completes with true when card data is loaded, false when no content module is available.</summary>
    public Task<bool> ContentReady => _contentReady.Task;

    public ContentModule? Module => Content.Module;
    public CardDatabase? Cards => Content.Cards;

    /// <summary>The match the game board should start; null starts the default demo.</summary>
    public MatchSetup? PendingMatch { get; set; }

    public override async void _Ready()
    {
        Instance = this;
        Settings.Load();
        Settings.Apply();
        // Kit scroll bars everywhere (screens set the rest of their look themselves).
        if (Arcanum.UI.UiArt.Theme() is { } theme) GetTree().Root.Theme = theme;
        AddChild(Content);
        AddChild(Online);
        GetTree().SceneChanged += () => Presence.ShowScreen(GetTree().CurrentScene?.SceneFilePath ?? "");
        if (GetTree().CurrentScene is { } first) Presence.ShowScreen(first.SceneFilePath);
        // ARCANUM_SHOT=<file.png>[,seconds]: saves a picture of the screen after a while (default 8 s) and quits (screenshots).
        // ARCANUM_SHOT_POINTER=x,y moves the pointer there a moment before (to show what hovering does).
        if (OS.GetEnvironment("ARCANUM_SHOT") is { Length: > 0 } shot)
        {
            var parts = shot.Split(',');
            double delay = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 8;
            if (OS.GetEnvironment("ARCANUM_SHOT_POINTER").Split(',') is [var px, var py]
                && float.TryParse(px, System.Globalization.CultureInfo.InvariantCulture, out var x) && float.TryParse(py, System.Globalization.CultureInfo.InvariantCulture, out var y))
                GetTree().CreateTimer(Math.Max(0, delay - 1.5)).Timeout += () =>
                {
                    GetViewport().WarpMouse(new Vector2(x, y));
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = new Vector2(x, y), GlobalPosition = new Vector2(x, y), Relative = new Vector2(1, 1) });
                };
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(parts[0]);
                GetTree().Quit();
            };
        }
        Content.Progress += message =>
        {
            ContentStatus = message;
            ContentProgress?.Invoke(message);
        };
        try
        {
            bool loaded = await Content.LoadAsync();
            if (Module is not null)
            {
                var errors = new List<string>();
                Formats.AddRange(Module.LoadFormats(errors));
                foreach (var error in errors) GD.PushWarning($"Format: {error}");
            }
            if (Formats.Count == 0) Formats.Add(FormatRules.Casual);
            if (loaded) Career.Init(Module!, Cards!);
            ContentStatus = loaded ? $"{Cards!.Count:N0} cards ready" : "No content module: generic demo only";
            ContentProgress?.Invoke(ContentStatus);
            _contentReady.TrySetResult(loaded);
        }
        catch (Exception e)
        {
            GD.PushError($"Could not load content: {e}");
            ContentStatus = "Card data could not be loaded";
            ContentProgress?.Invoke(ContentStatus);
            _contentReady.TrySetResult(false);
        }
    }

    public override void _ExitTree() => Presence.Stop();

    public FormatRules FormatById(string id) => Formats.FirstOrDefault(f => f.Id == id) ?? Formats[0];

    public void GoTo(string scene) => GetTree().ChangeSceneToFile(scene);
}
