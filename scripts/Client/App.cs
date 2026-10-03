// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Formats;
using Arcanum.Data.Modules;
using Godot;

namespace Arcanum.Client;

/// <summary>A hotseat match chosen on the play screen, picked up by the game board.</summary>
public sealed record MatchSetup(GameSession.Seat First, GameSession.Seat Second, int StartingLife, bool Sandbox = false);

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

    public const string SourceUrl = "https://github.com/cucsijuan/arcanum";

    public static App Instance { get; private set; } = null!;

    private readonly TaskCompletionSource<bool> _contentReady = new();

    public ContentLoader Content { get; } = new();
    public DeckStore Decks { get; } = new();
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
        AddChild(Content);
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

    public FormatRules FormatById(string id) => Formats.FirstOrDefault(f => f.Id == id) ?? Formats[0];

    public void GoTo(string scene) => GetTree().ChangeSceneToFile(scene);
}
