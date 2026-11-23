// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Modules;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// Finds the installed content module and makes its card data available: downloads the card file on first run,
/// converts it to Arcanum's compact form under user:// and loads it into a <see cref="CardDatabase"/>.
/// </summary>
public partial class ContentLoader : Node
{
    public ContentModule? Module { get; private set; }
    public CardDatabase? Cards { get; private set; }

    /// <summary>Human-readable progress ("Downloading card data… 42%").</summary>
    public event Action<string>? Progress;

    /// <summary>Loads the module and its card data. Returns false (and leaves <see cref="Cards"/> null) if unavailable.</summary>
    public async Task<bool> LoadAsync()
    {
        var moduleDir = FindModuleDirectory();
        if (moduleDir is null)
        {
            GD.Print("No content module found; using generic demo cards.");
            return false;
        }
        Module = ContentModule.Load(moduleDir);
        CardImageCache.Configure(Module);
        UI.Board.BoardStyle.SetModuleCardBacks(Module.Sources.CardBacks ?? Array.Empty<CardBackSource>());

        var dataDir = ProjectSettings.GlobalizePath($"user://card_data/{Module.Manifest.Id}");
        Directory.CreateDirectory(dataDir);
        var format = OracleJsonl.CompactFormat;
        // A module zip brings its card data already made (card-data.bin): used where it is, nothing to download or read
        // as text. Without it (a module checkout, a file made under other import rules) it is made from the source.
        var prepared = Path.Combine(moduleDir, OracleJsonl.PreparedFileName);
        bool usePrepared = CardCache.IsRecordsFile(prepared);
        var compact = Path.Combine(dataDir, $"cards.v{format}-{Module.Manifest.Version}.jsonl.gz");
        if (usePrepared || !File.Exists(compact))
            foreach (var old in Directory.EnumerateFiles(dataDir, "cards*.jsonl.gz")) File.Delete(old);
        if (!usePrepared && !File.Exists(compact) && !await PrepareFromSourceAsync(dataDir, compact)) return false;
        var cardFile = usePrepared ? prepared : compact;

        Report("Loading cards…");
        IProgress<string> progress = new Progress<string>(Report); // reports from other threads reach the screen on this one
        var module = Module;
        var cache = Path.Combine(dataDir, $"cards.v{format}-{module.Manifest.Version}.cache");
        var tagErrors = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string key = "";
        bool madeNow = false;
        Cards = await Task.Run(() =>
        {
            key = CardCache.Key(cardFile, module.ScriptsDirectory);
            var scriptFor = module.ScriptReader();
            var cards = CardCache.Read(cache, key, scriptFor);
            if (cards is null)
            {
                var stored = CardCache.StoredKey(cache);
                GD.Print(stored is null ? "Card cache: none yet; the cards are made as they are needed." :
                    stored == key ? "Card cache: unreadable; the cards are made again as they are needed." :
                    $"Card cache: made for other content or another game build; the cards are made again as they are needed.\n  was: {stored}\n  now: {key}");
                List<CardRecord> records;
                if (usePrepared) records = CardCache.ReadRecords(prepared) ?? throw new InvalidDataException($"{prepared} can't be read.");
                else using (var input = File.OpenRead(compact)) records = OracleJsonl.ReadCompact(input);
                cards = CardDatabase.MadeOnDemand(records, scriptFor);
                madeNow = true;
            }
            cards.SearchTags = module.LoadSearchTags(tagErrors);
            return cards;
        });
        foreach (var error in tagErrors) GD.PushWarning($"Search tags: {error}");
        if (madeNow)
        {
            // A database made now knows each card's support only once the card is made, and the screens ask for many
            // at once (a career's boosters, the deck builder's lists): every card is made first, on every processor,
            // while the menu shows how far it is; then the database is kept for the next starts.
            int shown = -1;
            await Cards.MakeDefinitionsAsync(Math.Max(1, System.Environment.ProcessorCount), (done, total) =>
            {
                int percent = (int)(done * 100L / Math.Max(1, total));
                if (Interlocked.Exchange(ref shown, percent) != percent)
                    progress.Report($"Preparing cards (once after an install or update)… {percent}%");
            });
            GD.Print($"Made {Cards.Count} cards in {clock.Elapsed.TotalSeconds:0.0} s ({System.Environment.ProcessorCount} processors).");
            var cards = Cards;
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var old in Directory.EnumerateFiles(dataDir, "cards*.cache")) File.Delete(old);
                    CardCache.Write(cards, cache, key);
                    GD.Print("Card cache saved.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    GD.PushWarning($"The card cache couldn't be saved ({e.Message}); the next start makes the cards again.");
                }
            });
        }
        else
        {
            GD.Print($"Loaded {Cards.Count} cards from module '{module.Manifest.Id}' in {clock.Elapsed.TotalSeconds:0.0} s.");
            // Cards make their definitions when first used; the rest are made in the background, so a search over all
            // of them doesn't wait.
            _ = Cards.MakeDefinitionsAsync();
        }
        if (Settings.Current.DownloadEveryPicture) CardImageCache.DownloadEvery(Cards);
        return true;
    }

    /// <summary>Downloads the source's card and printing files and makes the card data from them.</summary>
    private async Task<bool> PrepareFromSourceAsync(string dataDir, string compact)
    {
        var source = Path.Combine(dataDir, "source.download");
        Report("Looking up card data…");
        var downloadUrl = await ResolveDownloadUrlAsync(Module!.Sources.Cards.Index, Module.Sources.Cards.DownloadField);
        if (downloadUrl is null) return false;
        if (!await DownloadAsync(downloadUrl, source, Module.Sources.UserAgent, "card data")) return false;

        // Every printing of every card: sets, collector numbers, rarities and exact pictures.
        string? printingsFile = null;
        if (Module.Sources.Printings is { } printingSource)
        {
            Report("Looking up printings…");
            var printingsUrl = await ResolveDownloadUrlAsync(printingSource.Index, printingSource.DownloadField);
            if (printingsUrl is null) return false;
            printingsFile = Path.Combine(dataDir, "printings.download");
            if (!await DownloadAsync(printingsUrl, printingsFile, Module.Sources.UserAgent, "printings")) return false;
        }

        Report("Preparing card data…");
        var filter = Module.Sources.Cards.ToFilter();
        await Task.Run(() =>
        {
            using var input = File.OpenRead(source);
            using var printingsInput = printingsFile is null ? null : File.OpenRead(printingsFile);
            var temp = compact + ".tmp";
            using (var output = File.Create(temp)) OracleJsonl.Prepare(input, printingsInput, filter, output);
            Arcanum.Data.SafeFile.Replace(temp, compact);
        });
        File.Delete(source);
        if (printingsFile is not null) File.Delete(printingsFile);
        return true;
    }

    /// <summary>
    /// The module to use: ARCANUM_MODULE_PATH, then (editor runs) a module checkout next to the project, then an
    /// installed module (the one installed last when there are several). The game knows no module by name.
    /// </summary>
    private static string? FindModuleDirectory()
    {
        static bool IsModule(string dir) => File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "sources.json"));
        static IEnumerable<string> Folders(string parent)
        {
            try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : Array.Empty<string>(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
        var env = OS.GetEnvironment("ARCANUM_MODULE_PATH");
        if (!string.IsNullOrEmpty(env) && IsModule(env)) return env;
        if (OS.HasFeature("editor")
            && Folders(Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."))).Where(IsModule).Order(StringComparer.Ordinal).FirstOrDefault() is { } checkout)
            return checkout;
        return Folders(ProjectSettings.GlobalizePath("user://modules"))
            .Where(dir => !Path.GetFileName(dir).StartsWith('.') && IsModule(dir))
            .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "manifest.json")))
            .FirstOrDefault();
    }

    private async Task<string?> ResolveDownloadUrlAsync(string index, string downloadField)
    {
        var (ok, body) = await RequestAsync(index, Module!.Sources.UserAgent);
        if (!ok) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty(downloadField, out var url) ? url.GetString() : null;
    }

    /// <summary>
    /// A request, tried again a few times while the connection can't be made (a device's network may still be coming
    /// up when the game starts).
    /// </summary>
    private async Task<(bool Ok, byte[] Body)> RequestAsync(string url, string userAgent)
    {
        for (int attempt = 1; ; attempt++)
        {
            var (ok, body, connected) = await RequestOnceAsync(url, userAgent);
            if (ok || connected || attempt == ConnectAttempts) return (ok, body);
            Report("Waiting for the connection…");
            await ToSignal(GetTree().CreateTimer(attempt * 2), SceneTreeTimer.SignalName.Timeout);
        }
    }

    private const int ConnectAttempts = 6;

    private async Task<(bool Ok, byte[] Body, bool Connected)> RequestOnceAsync(string url, string userAgent)
    {
        var http = new HttpRequest { Timeout = 30 };
        AddChild(http);
        var tcs = new TaskCompletionSource<(bool, byte[])>();
        string outcome = "";
        bool connected = true;
        http.RequestCompleted += (result, code, _, body) =>
        {
            outcome = $"{(HttpRequest.Result)result}, HTTP {code}";
            connected = (HttpRequest.Result)result is not (HttpRequest.Result.CantConnect or HttpRequest.Result.CantResolve
                or HttpRequest.Result.ConnectionError or HttpRequest.Result.Timeout);
            tcs.TrySetResult((result == (long)HttpRequest.Result.Success && code == 200, body));
        };
        var started = http.Request(url, new[] { $"User-Agent: {userAgent}", "Accept: application/json" });
        if (started != Error.Ok)
        {
            outcome = $"not started ({started})";
            tcs.TrySetResult((false, Array.Empty<byte>()));
        }
        var response = await tcs.Task;
        http.QueueFree();
        if (!response.Item1) GD.PushWarning($"Request failed: {url} ({outcome})");
        return (response.Item1, response.Item2, connected);
    }

    private async Task<bool> DownloadAsync(string url, string path, string userAgent, string what)
    {
        var http = new HttpRequest { DownloadFile = path, Timeout = 0, UseThreads = true };
        AddChild(http);
        var tcs = new TaskCompletionSource<bool>();
        http.RequestCompleted += (result, code, _, _) => tcs.TrySetResult(result == (long)HttpRequest.Result.Success && code == 200);
        if (http.Request(url, new[] { $"User-Agent: {userAgent}" }) != Error.Ok) tcs.TrySetResult(false);

        while (!tcs.Task.IsCompleted)
        {
            int total = http.GetBodySize();
            int done = http.GetDownloadedBytes();
            Report(total > 0 ? $"Downloading {what}… {done * 100L / total}%" : $"Downloading {what}… {done / 1_048_576} MB");
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        }
        http.QueueFree();
        bool ok = tcs.Task.Result;
        if (!ok) GD.PushWarning($"Download failed: {url}");
        return ok;
    }

    private void Report(string message) => Progress?.Invoke(message);
}
