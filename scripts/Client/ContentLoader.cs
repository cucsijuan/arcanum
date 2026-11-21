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
    private const string ModuleId = "arcanum-classic";

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
        // Bump the version when import rules change so existing installs re-import.
        var compact = Path.Combine(dataDir, $"cards.v14-{Module.Manifest.Version}.jsonl.gz");

        if (!File.Exists(compact))
        {
            foreach (var old in Directory.EnumerateFiles(dataDir, "cards*.jsonl.gz")) File.Delete(old);
            var source = Path.Combine(dataDir, "source.download");
            Report("Looking up card data…");
            var downloadUrl = await ResolveDownloadUrlAsync(Module.Sources.Cards.Index, Module.Sources.Cards.DownloadField);
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
            await Task.Run(() =>
            {
                Dictionary<string, List<Printing>>? printings = null;
                if (printingsFile is not null)
                {
                    using var printingsInput = File.OpenRead(printingsFile);
                    printings = OracleJsonl.ImportPrintings(printingsInput);
                }
                using var input = File.OpenRead(source);
                var temp = compact + ".tmp";
                var records = OracleJsonl.Import(input, Module.Sources.Cards.ToFilter());
                if (printings is not null) records = OracleJsonl.WithPrintings(records, printings);
                using (var output = File.Create(temp)) OracleJsonl.WriteCompact(records, output);
                Arcanum.Data.SafeFile.Replace(temp, compact);
            });
            File.Delete(source);
            if (printingsFile is not null) File.Delete(printingsFile);
        }

        Report("Loading cards…");
        var module = Module;
        var cache = Path.Combine(dataDir, $"cards.v14-{module.Manifest.Version}.cache");
        IProgress<string> progress = new Progress<string>(Report); // reports from the worker reach the screen on this thread
        var scriptErrors = new List<string>();
        var tagErrors = new List<string>();
        Cards = await Task.Run(() =>
        {
            var key = CardCache.Key(compact, module.ScriptsDirectory);
            var cards = CardCache.Read(cache, key, module.ScriptReader());
            if (cards is null)
            {
                var stored = CardCache.StoredKey(cache);
                GD.Print(stored is null ? "Card cache: none yet; making the cards." :
                    stored == key ? "Card cache: unreadable; making the cards again." :
                    $"Card cache: made for other content or another game build; making the cards again.\n  was: {stored}\n  now: {key}");
                cards = MakeCards(module, compact, cache, key, scriptErrors, progress);
            }
            cards.SearchTags = module.LoadSearchTags(tagErrors);
            return cards;
        });
        foreach (var error in scriptErrors) GD.PushWarning($"Card script: {error}");
        foreach (var error in tagErrors) GD.PushWarning($"Search tags: {error}");
        // Cards make their definitions when first used; the rest are made in the background, so a search over all of
        // them doesn't wait.
        _ = Cards.MakeDefinitionsAsync();
        if (Settings.Current.DownloadEveryPicture) CardImageCache.DownloadEvery(Cards);
        GD.Print($"Loaded {Cards.Count} cards from module '{module.Manifest.Id}'.");
        return true;
    }

    /// <summary>
    /// Makes every card from the card data and the module's scripts (the first start with this content or game
    /// version), and keeps the result in <paramref name="cache"/> for the next starts.
    /// </summary>
    private static CardDatabase MakeCards(ContentModule module, string compact, string cache, string key, List<string> scriptErrors,
        IProgress<string> progress)
    {
        // Shares of the work, as measured on the slowest device: scripts, reading the card data, making the cards.
        // Only a new percentage is reported: each report changes the screen's text, and one per card kept the screen
        // busy enough on slow devices to stop it from being drawn until the end.
        int shown = -1;
        void Step(double from, double share, double done)
        {
            int percent = (int)((from + share * done) * 100);
            if (Interlocked.Exchange(ref shown, percent) != percent)
                progress.Report($"Preparing cards (once after an install or update)… {percent}%");
        }
        var scripts = module.LoadScripts(scriptErrors, (done, total) => Step(0, 0.12, (double)done / total));
        List<CardRecord> records;
        using (var input = File.OpenRead(compact)) records = OracleJsonl.ReadCompact(input, done => Step(0.12, 0.30, done));
        var cards = new CardDatabase(records, scripts, (done, total) => Step(0.42, 0.58, (double)done / total));
        try
        {
            foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(cache)!, "cards*.cache")) File.Delete(old);
            CardCache.Write(cards, cache, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"The card cache couldn't be saved ({e.Message}); the next start makes the cards again.");
        }
        return cards;
    }

    /// <summary>
    /// ARCANUM_MODULE_PATH, then a sibling checkout next to the project (editor runs), then the installed copy.
    /// </summary>
    private static string? FindModuleDirectory()
    {
        var candidates = new List<string>();
        var env = OS.GetEnvironment("ARCANUM_MODULE_PATH");
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);
        if (OS.HasFeature("editor")) candidates.Add(Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", ModuleId)));
        candidates.Add(ProjectSettings.GlobalizePath($"user://modules/{ModuleId}"));
        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "manifest.json")));
    }

    private async Task<string?> ResolveDownloadUrlAsync(string index, string downloadField)
    {
        var (ok, body) = await RequestAsync(index, Module!.Sources.UserAgent);
        if (!ok) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty(downloadField, out var url) ? url.GetString() : null;
    }

    private async Task<(bool Ok, byte[] Body)> RequestAsync(string url, string userAgent)
    {
        var http = new HttpRequest { Timeout = 30 };
        AddChild(http);
        var tcs = new TaskCompletionSource<(bool, byte[])>();
        http.RequestCompleted += (result, code, _, body) => tcs.TrySetResult((result == (long)HttpRequest.Result.Success && code == 200, body));
        if (http.Request(url, new[] { $"User-Agent: {userAgent}", "Accept: application/json" }) != Error.Ok) tcs.TrySetResult((false, Array.Empty<byte>()));
        var response = await tcs.Task;
        http.QueueFree();
        if (!response.Item1) GD.PushWarning($"Request failed: {url}");
        return response;
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
