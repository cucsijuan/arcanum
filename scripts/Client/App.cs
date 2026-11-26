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

    /// <summary>The game mode (a plugin) whose rules the game adds; null for none.</summary>
    public Arcanum.Data.GameModes.GameModeScript? Mode { get; init; }

    /// <summary>What the player's Discord activity calls this game ("Modern match", an event's name); null: the event hook's title.</summary>
    public string? Label { get; init; }

    /// <summary>The set of a draft or sealed game (the computer guesses its opponent's cards from it); null otherwise.</summary>
    public string? LimitedSet { get; init; }

    /// <summary>A picture of a table both players share (its top half behind the opponent, its bottom half behind this player), instead of the playmats.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// At a <see cref="Table"/>, the playmat each seat puts on it (a built-in playmat id, or null for none). Where the mats
    /// lie comes from the JSON file next to the table's picture ("matTop", "matBottom").
    /// </summary>
    public IReadOnlyList<string?>? TableMats { get; init; }

    /// <summary>At a <see cref="Table"/>, a big tournament's mat for both players with this event's name on its crest, instead of <see cref="TableMats"/>.</summary>
    public string? TournamentMat { get; init; }

    /// <summary>A tournament's broadcast overlay on the board: the round's clock and the players' plates.</summary>
    public RoundBroadcast? Broadcast { get; init; }
}

/// <summary>A player's plate on a tournament's overlay: name, record in the event before this match, games won in it.</summary>
public sealed record BroadcastPlate(string Name, string Record, int Games);

/// <param name="Event">The event's name.</param>
/// <param name="Round">"Round 3", "Quarterfinals"…</param>
/// <param name="Deadline">When the round's time is up (UTC); null: untimed.</param>
/// <param name="Plates">One per seat, in seat order.</param>
/// <param name="BestOf">Games in the match (the plates show the wins it takes).</param>
public sealed record RoundBroadcast(string Event, string Round, DateTime? Deadline, IReadOnlyList<BroadcastPlate> Plates, int BestOf);

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
        // The console's sockets refuse no-delay on accepted connections (and are then unusable).
        Arcanum.Net.Transport.TcpConnection.UseNoDelay = OS.GetName() != "Switch";
        AddSymbolFallbacks();
        Settings.Load();
        Settings.Apply();
        // Kit scroll bars everywhere (screens set the rest of their look themselves).
        if (Arcanum.UI.UiArt.Theme() is { } theme) GetTree().Root.Theme = theme;
        ScenePictures.ReadAhead();
        AddChild(Content);
        AddChild(Online);
        // Controllers and touch screens: sliding a finger and the right stick scroll; the left stick moves a pointer.
        AddChild(new Arcanum.UI.ScrollGestures());
        // Controllers and keys: a panel of choices takes the focus on its answer (before the pointer, which gets the
        // buttons first while it shows).
        AddChild(new Arcanum.UI.PadFocus());
        AddChild(new Arcanum.UI.VirtualPointer());
        // ARCANUM_PAD_SOAK: games played with random controller presses (testing).
        if (Arcanum.UI.PadSoak.FromEnvironment() is { } soak) AddChild(soak);
        // ARCANUM_PAD_KEYS: the controller presses listed (testing).
        if (Arcanum.UI.PadScript.FromEnvironment() is { } presses) AddChild(presses);
        GetTree().SceneChanged += () => Presence.ShowScreen(GetTree().CurrentScene?.SceneFilePath ?? "");
        if (GetTree().CurrentScene is { } first) Presence.ShowScreen(first.SceneFilePath);
        // ARCANUM_SHOT=<file.png>[,seconds]: saves a picture of the screen after a while (default 8 s) and quits (screenshots).
        // ARCANUM_SHOT_POINTER=x,y moves the pointer there a moment before (to show what hovering does).
        if (OS.GetEnvironment("ARCANUM_SHOT") is { Length: > 0 } shot)
        {
            var parts = shot.Split(',');
            // With ARCANUM_SHOT_ON the board takes the picture when that announcement shows; this is only the fallback.
            double delay = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s
                : OS.GetEnvironment("ARCANUM_SHOT_ON").Length > 0 ? 600 : 8;
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
        // ARCANUM_SHOTS=<folder>[,seconds]: saves a numbered picture of the screen every so often (default 20 s) while
        // playing on (long test runs).
        if (OS.GetEnvironment("ARCANUM_SHOTS") is { Length: > 0 } shots)
        {
            var parts = shots.Split(',');
            double every = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 20;
            System.IO.Directory.CreateDirectory(parts[0]);
            var timer = new Godot.Timer { WaitTime = every, Autostart = true };
            int taken = 0;
            timer.Timeout += async () =>
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(parts[0], $"{++taken:D4}.png"));
            };
            AddChild(timer);
        }
        Content.Progress += message =>
        {
            ContentStatus = message;
            ContentProgress?.Invoke(message);
        };
        try
        {
            await OfferModuleZipsAsync();
            bool loaded = await Content.LoadAsync();
            if (Module is not null)
            {
                var errors = new List<string>();
                Formats.AddRange(Module.LoadFormats(errors));
                foreach (var error in errors) GD.PushWarning($"Format: {error}");
            }
            if (Formats.Count == 0) Formats.Add(FormatRules.Casual);
            LoadGameModes();
            if (loaded) await Career.InitAsync(Module!, Cards!);
            ContentStatus = loaded ? $"{Cards!.Count:N0} cards ready"
                : Module is not null ? "The card data couldn't be downloaded (is the device online?): generic demo only"
                : "No content module: generic demo only";
            ContentProgress?.Invoke(ContentStatus);
            _contentReady.TrySetResult(loaded);
            if (loaded) PrepareAhead(Module!, Cards!);
        }
        catch (Exception e)
        {
            GD.PushError($"Could not load content: {e}");
            ContentStatus = "Card data could not be loaded";
            ContentProgress?.Invoke(ContentStatus);
            _contentReady.TrySetResult(false);
        }
    }

    /// <summary>
    /// Module zips next to the game (see <see cref="ModuleZips"/>): each is offered on the first screen, and installed
    /// before the cards load when the player says yes.
    /// </summary>
    private async Task OfferModuleZipsAsync()
    {
        Arcanum.Data.Modules.ModuleInstaller.CleanUp(ProjectSettings.GlobalizePath("user://modules"));
        var offered = ModuleZips.Offered();
        if (offered.Count == 0) return;
        // The questions sit on a layer of their own over every screen: going to another screen meanwhile doesn't take
        // them away (the cards wait for their answers).
        var layer = new CanvasLayer { Layer = 100 };
        var screen = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        screen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(screen);
        AddChild(layer);
        foreach (var found in offered)
        {
            var name = $"{found.Manifest.Name} {found.Manifest.Version}";
            ContentStatus = $"Found {name} next to the game";
            ContentProgress?.Invoke(ContentStatus);
            var answer = new TaskCompletionSource<bool>();
            Arcanum.UI.Menu.MenuKit.Modal(screen, "Content module found",
                $"{name} is next to the game ({System.IO.Path.GetFileName(found.Path)})." +
                (found.Installed is { } installed ? $" It replaces the installed {installed}." : "") +
                "\nInstall it?" +
                (ModuleInstaller.BringsCardData(found.Path) ? "" : "\n\nThe first start with it downloads its card data, which takes a few minutes."),
                null, ("Don't install", () => answer.TrySetResult(false)), ("Install", () => answer.TrySetResult(true)));
            if (!await answer.Task)
            {
                ModuleZips.Decline(found);
                continue;
            }
            ContentStatus = $"Installing {name}…";
            ContentProgress?.Invoke(ContentStatus);
            try
            {
                var started = DateTime.UtcNow;
                int shown = -1;
                IProgress<string> report = new Progress<string>(message =>
                {
                    ContentStatus = message;
                    ContentProgress?.Invoke(message);
                });
                await Task.Run(() => Arcanum.Data.Modules.ModuleInstaller.InstallFromZip(found.Path, ProjectSettings.GlobalizePath("user://modules"),
                    (done, total) =>
                    {
                        int percent = total == 0 ? 100 : done * 100 / total;
                        if (Interlocked.Exchange(ref shown, percent) != percent) report.Report($"Installing {name}… {percent}%");
                    }));
                GD.Print($"Installed {name} in {(DateTime.UtcNow - started).TotalSeconds:F0} s.");
            }
            catch (Exception e)
            {
                GD.PushWarning($"Module install failed: {e}");
                var closed = new TaskCompletionSource();
                Arcanum.UI.Menu.MenuKit.Modal(screen, "Install failed", $"{name} couldn't be installed:\n{e.Message}", null,
                    ("OK", () => closed.TrySetResult()));
                await closed.Task;
            }
        }
        layer.QueueFree();
    }

    /// <summary>
    /// Symbols the interface writes as text (≡, ↶, ⚔, ✕...) come from bundled symbol fonts behind each of the game's
    /// fonts, so they show the same everywhere (some devices, such as consoles, have no system fonts to fall back on).
    /// </summary>
    private static void AddSymbolFallbacks()
    {
        var symbols = new Godot.Collections.Array<Font>();
        foreach (var file in new[] { "NotoSansMath-Regular.ttf", "NotoSansSymbols2-Regular.ttf", "NotoSansSymbols-Regular.ttf" })
            if (ResourceLoader.Exists($"res://fonts/noto-symbols/{file}")) symbols.Add(GD.Load<Font>($"res://fonts/noto-symbols/{file}"));
        if (symbols.Count == 0) return;
        foreach (var path in new[]
                 {
                     "res://fonts/alegreya-sans/AlegreyaSans-Medium.ttf", "res://fonts/bebas-neue/BebasNeue-Regular.ttf",
                     "res://fonts/lt-museum/LTMuseum-Bold.ttf", "res://fonts/ortica/Ortica-Bold.otf", "res://fonts/pixelify-sans/PixelifySans.ttf",
                 })
            if (ResourceLoader.Exists(path) && GD.Load<Font>(path) is { } font) font.Fallbacks = symbols; // the loaded font is shared by everything using it
        ThemeDB.FallbackFont.Fallbacks = symbols;
    }

    /// <summary>
    /// Works out ahead, on a thread of the lowest priority, what the first game and the screens listing decks need of the
    /// cards and the module's decks, so they don't wait for it.
    /// </summary>
    private void PrepareAhead(Arcanum.Data.Modules.ContentModule module, CardDatabase cards)
    {
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                cards.PrepareLists();
                foreach (var name in module.DeckNames())
                {
                    var deck = module.SharedDeck(name);
                    foreach (var entry in deck.Main.Concat(deck.Commander).Concat(deck.Sideboard))
                    {
                        if (_closing) return;
                        _ = cards.Find(entry.Name)?.Definition;
                    }
                }
            }
            catch (Exception e) { GD.PushWarning($"Decks: {e}"); } // worked out when it's needed then (and its problem shown)
        }) { IsBackground = true, Name = "Decks" };
        try { thread.Priority = System.Threading.ThreadPriority.Lowest; }
        catch (Exception e) when (e is PlatformNotSupportedException or System.Threading.ThreadStateException) { }
        thread.Start();
    }

    private volatile bool _closing;

    public override void _ExitTree()
    {
        _closing = true;
        Settings.Flush();
        Presence.Stop();
    }

    public override void _Process(double delta) => Settings.CheckScreen(delta);

    /// <summary>Folder where players put game modes of their own (modes/*.lua).</summary>
    public static string UserModesDir => ProjectSettings.GlobalizePath("user://modes");

    /// <summary>
    /// Game modes (plugins) become formats: the game's own examples (res://modes), the content module's, then the
    /// player's (user://modes). A mode whose id is taken already is left out.
    /// </summary>
    private void LoadGameModes()
    {
        var errors = new List<string>();
        var sources = new List<(string File, string Source)>();
        if (DirAccess.Open("res://modes") is { } dir)
            foreach (var file in dir.GetFiles().Where(f => f.EndsWith(".lua")).Order())
                sources.Add(($"res://modes/{file}", Godot.FileAccess.GetFileAsString($"res://modes/{file}")));
        foreach (var folder in new[] { Module is null ? null : Path.Combine(Module.Directory, "modes"), UserModesDir })
            if (folder is not null && Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.lua").Order())
                    sources.Add((file, File.ReadAllText(file)));
        foreach (var (file, source) in sources)
        {
            try
            {
                var mode = Arcanum.Data.GameModes.GameModeScript.Parse(source, Formats);
                if (Formats.Any(f => f.Id == mode.Id)) errors.Add($"{file}: the id \"{mode.Id}\" is taken already");
                else Formats.Add(mode.Format);
            }
            catch (FormatException e) { errors.Add($"{file}: {e.Message}"); }
        }
        foreach (var error in errors) GD.PushWarning($"Game mode: {error}");
    }

    public FormatRules FormatById(string id) => Formats.FirstOrDefault(f => f.Id == id) ?? Formats[0];

    public void GoTo(string scene) => GetTree().ChangeSceneToFile(scene);
}
