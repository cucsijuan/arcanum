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

        var dataDir = ProjectSettings.GlobalizePath($"user://card_data/{Module.Manifest.Id}");
        Directory.CreateDirectory(dataDir);
        // Bump the version when import rules change so existing installs re-import.
        var compact = Path.Combine(dataDir, $"cards.v5-{Module.Manifest.Version}.jsonl.gz");

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
                File.Move(temp, compact, overwrite: true);
            });
            File.Delete(source);
            if (printingsFile is not null) File.Delete(printingsFile);
        }

        Report("Loading cards…");
        var scriptErrors = new List<string>();
        var scripts = Module.LoadScripts(scriptErrors);
        foreach (var error in scriptErrors) GD.PushWarning($"Card script: {error}");
        Cards = await Task.Run(() =>
        {
            using var input = File.OpenRead(compact);
            return new CardDatabase(OracleJsonl.ReadCompact(input), scripts);
        });
        GD.Print($"Loaded {Cards.Count} cards from module '{Module.Manifest.Id}'.");
        return true;
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
