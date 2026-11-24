// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// Autoload that downloads card images on demand from the source configured by the content module and caches
/// them under user://. Images are never bundled with the app. Card pictures come over a few connections at once, the
/// requests started no closer together than the module asks; pictures the module gives by address (booster packs, card
/// backs) live on other hosts and download on their own connection, so they never wait behind a pack's worth of card
/// pictures. A game's cards can be fetched ahead (<see cref="Prefetch"/>): they download after the pictures on screen.
/// When the player asks for it, every card's picture downloads in the background after both
/// (<see cref="DownloadEvery"/>), on a few of the connections only.
/// Pictures are read and decoded away from the main thread; only making them into textures happens on it, a few per
/// frame, so a board full of new pictures doesn't stall.
/// </summary>
public partial class CardImageCache : Node
{
    private const string CacheDir = "user://card_cache";

    /// <summary>Connections for card pictures (their requests still start the module's interval apart).</summary>
    private const int CardConnections = 6;

    /// <summary>Seconds to wait after a host answers "too many requests" without saying how long.</summary>
    private const double TooManyRequestsPause = 30;

    /// <summary>Connections the background download of every card's picture may use at once (the rest stay free).</summary>
    private const int EveryConnections = 3;

    /// <summary>Milliseconds a frame may spend making decoded pictures into textures.</summary>
    private const ulong TextureBudgetMs = 6;

    private static CardImageCache? _instance;

    private readonly Dictionary<string, Texture2D> _memory = new();
    private readonly Dictionary<string, List<Action<Texture2D>>> _waiting = new();
    private readonly HashSet<string> _failed = new();
    private Arcanum.Data.Modules.ContentModule? _module;

    /// <summary>A connection, one picture at a time.</summary>
    private sealed class Lane
    {
        public HttpRequest Http = null!;
        public string? InFlight;
    }

    private readonly List<Lane> _cardLanes = new();
    private readonly Lane _addressLane = new();
    private readonly LinkedList<string> _cardQueue = new(), _prefetchQueue = new();
    private readonly Queue<string> _addressQueue = new();

    /// <summary>Keys waiting in a queue or downloading (a second request for one only waits for it).</summary>
    private readonly HashSet<string> _pending = new();

    /// <summary>
    /// Seconds until the next card picture request to the source may start, the next one by name (the source may allow
    /// fewer), and the next one to its file host.
    /// </summary>
    private double _cardCooldown, _nameCooldown, _fileCooldown;

    /// <summary>Pictures by id the file host didn't have: they come from the source's own address instead.</summary>
    private readonly HashSet<string> _notOnFileHost = new();

    /// <summary>Every card's picture not on disk yet, while the player has them all downloaded (null: not asked or not
    /// known yet).</summary>
    private Queue<string>? _every;
    /// <summary>Those of them downloading now (or waiting again after "too many requests").</summary>
    private readonly HashSet<string> _everyActive = new();
    private int _everyTotal, _everyHave, _everyFailed;
    private string? _everyStopped;
    private int _everyRun;

    /// <summary>Pictures being read and decoded, and the decoded ones waiting to become textures.</summary>
    private readonly HashSet<string> _decoding = new();
    private readonly BlockingCollection<(string Key, string Path, byte[]? Bytes)> _toDecode = new();
    private readonly ConcurrentQueue<(string Key, Image? Image)> _decoded = new();
    private System.Threading.Thread? _decoder;
    private volatile bool _closing;

    /// <summary>The longest the game waits, closing, for the decoding thread to leave.</summary>
    private static readonly TimeSpan ClosingWait = TimeSpan.FromSeconds(2);

    /// <summary>Sets where images come from. Until a module is configured no images are requested.</summary>
    public static void Configure(Arcanum.Data.Modules.ContentModule module)
    {
        if (_instance is null) return;
        _instance._module = module;
        _instance._failed.Clear();
        _instance._notOnFileHost.Clear();
    }

    public override void _Ready()
    {
        _instance = this;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(CacheDir));
        for (int i = 0; i < CardConnections; i++) _cardLanes.Add(new Lane());
        foreach (var lane in _cardLanes.Append(_addressLane))
        {
            lane.Http = new HttpRequest { Timeout = 20 };
            AddChild(lane.Http);
            lane.Http.RequestCompleted += (result, code, headers, body) => OnRequestCompleted(lane, result, code, headers, body);
        }
        _decoder = new System.Threading.Thread(Decode) { IsBackground = true, Name = "Card pictures" };
        try { _decoder.Priority = System.Threading.ThreadPriority.BelowNormal; }
        catch (Exception e) when (e is PlatformNotSupportedException or System.Threading.ThreadStateException) { }
        _decoder.Start();
    }

    /// <summary>The engine is taken down after this: the decoding thread (it uses the engine's images) leaves first, with
    /// the picture in hand done and the rest of its queue left.</summary>
    public override void _ExitTree()
    {
        _closing = true;
        _toDecode.CompleteAdding();
        if (_decoder?.Join(ClosingWait) == false) GD.Print("Closing: the picture being decoded didn't finish in time.");
    }

    /// <summary>
    /// Image key for a card: its exact image id when it has one ("id:..."), its name otherwise. Tokens without an
    /// exact id get no key: many tokens share a name, so looking them up by name would show the wrong picture.
    /// </summary>
    public static string? KeyFor(Arcanum.Engine.Views.CardView view)
    {
        var key = view.ImageKey is { } id ? "id:" + id : view.IsToken && !view.ImageByName ? null : view.Name;
        return key is not null && view.IsBackFace ? BackPrefix + key : key;
    }

    /// <summary>The same keys for a card's printed faces (front, and back for a double-faced card).</summary>
    public static IEnumerable<string> KeysFor(Arcanum.Engine.Cards.CardDefinition card)
    {
        var key = card.ImageKey is { } id ? "id:" + id : card.IsToken && !card.ImageByName ? null : card.Name;
        if (key is null) yield break;
        yield return key;
        if (card.BackFace is not null) yield return BackPrefix + key;
    }

    /// <summary>Marks the key of a double-faced card's back face picture ("back:id:…" or "back:Name").</summary>
    private const string BackPrefix = "back:";

    /// <summary>
    /// Marks the key of a picture the module gives by its address ("url:https://…"), such as a set's booster pack or a
    /// card back design: a new address is a new picture.
    /// </summary>
    private const string UrlPrefix = "url:";

    /// <summary>Image key for a picture at an address the module gives.</summary>
    public static string UrlKey(string url) => UrlPrefix + url;

    /// <summary>
    /// Downloads every card's picture (its usual printing, both faces of a double-faced card) in the background, after
    /// the pictures on screen and a game's cards; the ones on disk already are skipped, so it continues where it stopped.
    /// Null stops it.
    /// </summary>
    public static void DownloadEvery(Arcanum.Data.CardData.CardDatabase? cards)
    {
        if (_instance is not { } cache) return;
        int run = ++cache._everyRun;
        cache._every = null;
        cache._everyStopped = null;
        cache._everyTotal = cache._everyHave = cache._everyFailed = 0;
        // Those downloading now finish (uncounted); the ones waiting again go.
        foreach (var key in cache._everyActive)
            if (!cache._cardLanes.Any(l => l.InFlight == key) && !cache._waiting.ContainsKey(key) && cache._prefetchQueue.Remove(key))
                cache._pending.Remove(key);
        cache._everyActive.Clear();
        if (cards is null) return;
        var records = cards.Records.ToList();
        var dir = ProjectSettings.GlobalizePath(CacheDir);
        // Listing some thirty thousand files takes a while on a memory card: away from the main thread (_Process
        // picks the result up).
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var keys = records.Where(r => r.DefaultPrintingId is not null)
                    .SelectMany(r => r.Layout is "transform" or "modal_dfc" && r.Faces.Count == 2
                        ? new[] { "id:" + r.DefaultPrintingId, BackPrefix + "id:" + r.DefaultPrintingId }
                        : new[] { "id:" + r.DefaultPrintingId })
                    .ToList();
                var onDisk = System.IO.Directory.EnumerateFiles(dir).Select(System.IO.Path.GetFileName).ToHashSet(StringComparer.Ordinal);
                var missing = keys.Where(k => !onDisk.Contains(FileName(k))).ToList();
                cache._everyFound = Tuple.Create(run, keys.Count, (List<string>?)missing);
            }
            catch (Exception e)
            {
                cache._everyFound = Tuple.Create(run, 0, (List<string>?)null);
                GD.PushWarning($"Card pictures: couldn't list those on this device ({e.Message}).");
            }
        }) { IsBackground = true, Name = "Card pictures list" };
        try { thread.Priority = System.Threading.ThreadPriority.BelowNormal; }
        catch (Exception e) when (e is PlatformNotSupportedException or System.Threading.ThreadStateException) { }
        thread.Start();
    }

    /// <summary>What the listing thread found: for which run, how many pictures, and those not on disk (null: failed).</summary>
    private volatile Tuple<int, int, List<string>?>? _everyFound;

    /// <summary>
    /// How the download of every card's picture goes: pictures on disk, all of them, those that couldn't be downloaded,
    /// and why it stopped (null while it goes or when done); null when it isn't on or still looking at the disk.
    /// </summary>
    public static (int Have, int Total, int Failed, string? Stopped)? EveryProgress =>
        _instance is { _every: not null } cache ? (cache._everyHave, cache._everyTotal, cache._everyFailed, cache._everyStopped) : null;

    /// <summary>Calls <paramref name="onLoaded"/> (possibly immediately) once the image for <paramref name="cardName"/> is available.</summary>
    public static void Request(string cardName, Action<Texture2D> onLoaded) => _instance?.RequestInternal(cardName, onLoaded);

    /// <summary>
    /// Downloads these pictures ahead, after the ones asked for on screen, without loading them: a game's cards, so
    /// they are on disk by the time they are seen.
    /// </summary>
    public static void Prefetch(IEnumerable<string> keys)
    {
        if (_instance is not { _module: not null } cache) return;
        foreach (var key in keys)
        {
            if (key.StartsWith(UrlPrefix) || cache._memory.ContainsKey(key) || cache._failed.Contains(key)
                || cache._pending.Contains(key) || FileAccess.FileExists(CachePath(key))) continue;
            cache._pending.Add(key);
            cache._prefetchQueue.AddLast(key);
        }
    }

    /// <summary>
    /// Like <see cref="Request"/>, but returns a copy resampled once with Lanczos to exactly <paramref name="size"/>.
    /// Used for the big hover preview, where mipmapped scaling looks soft and washed out.
    /// </summary>
    public static void RequestSharp(string cardName, Vector2I size, Action<Texture2D> onLoaded)
    {
        if (_instance is null) return;
        var key = $"{cardName}@{size.X}x{size.Y}";
        if (_instance._memory.TryGetValue(key, out var cached)) { onLoaded(cached); return; }
        _instance.RequestInternal(cardName, texture =>
        {
            var image = texture.GetImage();
            image.ClearMipmaps();
            image.Resize(size.X, size.Y, Image.Interpolation.Lanczos);
            var sharp = ImageTexture.CreateFromImage(image);
            _instance._memory[key] = sharp;
            onLoaded(sharp);
        });
    }

    private void RequestInternal(string cardName, Action<Texture2D> onLoaded)
    {
        if (_memory.TryGetValue(cardName, out var cached)) { onLoaded(cached); return; }
        if (_failed.Contains(cardName) || _module is null) return;

        if (!_waiting.TryGetValue(cardName, out var callbacks)) _waiting[cardName] = callbacks = new List<Action<Texture2D>>();
        callbacks.Add(onLoaded);
        if (_decoding.Contains(cardName) || _pending.Contains(cardName) && !_prefetchQueue.Contains(cardName)) return;

        var path = CachePath(cardName);
        if (FileAccess.FileExists(path))
        {
            StartDecoding(cardName, path, null);
            return;
        }
        // Wanted on screen now: ahead of the pictures fetched ahead (a prefetched key left in that queue is skipped).
        _pending.Add(cardName);
        Enqueue(cardName);
    }

    private void StartDecoding(string key, string path, byte[]? bytes)
    {
        _decoding.Add(key);
        _toDecode.Add((key, ProjectSettings.GlobalizePath(path), bytes));
    }

    /// <summary>The decoding thread: reads a picture from disk (or takes the downloaded bytes) and decodes it.</summary>
    private void Decode()
    {
        foreach (var (key, path, bytes) in _toDecode.GetConsumingEnumerable())
        {
            if (_closing) break;
            Image? image = null;
            try
            {
                var data = bytes ?? System.IO.File.ReadAllBytes(path);
                image = TryDecode(data);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
            _decoded.Enqueue((key, image));
        }
    }

    public override void _Process(double delta)
    {
        _cardCooldown -= delta;
        _nameCooldown -= delta;
        _fileCooldown -= delta;
        if (_everyFound is { } found)
        {
            _everyFound = null;
            if (found.Item1 == _everyRun)
            {
                _everyTotal = found.Item2;
                _everyHave = found.Item2 - (found.Item3?.Count ?? 0);
                _every = new Queue<string>(found.Item3 ?? new List<string>());
                if (found.Item3 is null) _everyStopped = "The pictures on this device couldn't be listed.";
                GD.Print($"Card pictures: {_everyHave} of {_everyTotal} on this device.");
            }
        }
        foreach (var lane in _cardLanes)
        {
            if (lane.InFlight is not null) continue;
            if (NextCard() is not { } key) break;
            var images = _module!.Sources.Images;
            if (FileUrl(key) is not null) _fileCooldown = images.FileMinIntervalMs / 1000.0;
            else
            {
                _cardCooldown = images.MinIntervalMs / 1000.0;
                if (ByName(key)) _nameCooldown = (images.ByNameMinIntervalMs ?? images.MinIntervalMs) / 1000.0;
            }
            Start(lane, key);
        }
        if (_addressLane.InFlight is null && _addressQueue.TryDequeue(out var address)) Start(_addressLane, address);

        // Decoded pictures become textures, a few per frame.
        ulong start = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - start < TextureBudgetMs && _decoded.TryDequeue(out var done))
        {
            _decoding.Remove(done.Key);
            if (done.Image is null)
            {
                // A broken file on disk: download it again if it's still wanted.
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(CachePath(done.Key)));
                if (_waiting.ContainsKey(done.Key) && _pending.Add(done.Key)) Enqueue(done.Key);
                continue;
            }
            var texture = ImageTexture.CreateFromImage(done.Image);
            _memory[done.Key] = texture;
            if (_waiting.Remove(done.Key, out var callbacks))
                foreach (var callback in callbacks) callback(texture);
        }
    }

    private void Enqueue(string key)
    {
        if (key.StartsWith(UrlPrefix)) _addressQueue.Enqueue(key);
        else _cardQueue.AddLast(key);
    }

    /// <summary>A card picture asked for by the card's name (not an exact image id).</summary>
    private static bool ByName(string key) => !(key.StartsWith("id:") || key.StartsWith(BackPrefix + "id:"));

    /// <summary>The picture file on the module's file host, for a picture by id it may have; null otherwise.</summary>
    private string? FileUrl(string key)
    {
        bool back = key.StartsWith(BackPrefix);
        var id = back ? key[BackPrefix.Length..] : key;
        return id.StartsWith("id:") && !_notOnFileHost.Contains(key) ? _module!.FileImageUrlById(id[3..], back) : null;
    }

    /// <summary>Whether a request for this picture may start now (each host and kind of request has its own pace).</summary>
    private bool MayStart(string key) =>
        FileUrl(key) is not null ? _fileCooldown <= 0 : _cardCooldown <= 0 && (_nameCooldown <= 0 || !ByName(key));

    /// <summary>
    /// The next card picture to download: one wanted on screen, else one fetched ahead; the first one whose host and
    /// kind of request may start now.
    /// </summary>
    private string? NextCard()
    {
        if (Take(_cardQueue) is { } key) return key;
        // Done meanwhile, or asked for on screen meanwhile (then the other queue has it).
        for (var node = _prefetchQueue.First; node is not null;)
        {
            var next = node.Next;
            if (!_pending.Contains(node.Value) || _waiting.ContainsKey(node.Value)) _prefetchQueue.Remove(node);
            node = next;
        }
        return Take(_prefetchQueue) ?? TakeEvery();

        // Every card's picture, last, on a few connections only.
        string? TakeEvery()
        {
            if (_every is not { } every || _everyStopped is not null) return null;
            if (_cardLanes.Count(l => l.InFlight is { } k && _everyActive.Contains(k)) >= EveryConnections) return null;
            while (every.TryPeek(out var key))
            {
                // Loaded already, or failed this session; downloading for the screen or a game: counted when it ends.
                if (_memory.ContainsKey(key)) { every.Dequeue(); _everyHave++; continue; }
                if (_failed.Contains(key)) { every.Dequeue(); _everyFailed++; continue; }
                if (_pending.Contains(key)) { every.Dequeue(); _everyActive.Add(key); continue; }
                if (!MayStart(key)) return null;
                every.Dequeue();
                _pending.Add(key);
                _everyActive.Add(key);
                return key;
            }
            return null;
        }

        string? Take(LinkedList<string> queue)
        {
            for (var node = queue.First; node is not null; node = node.Next)
            {
                if (!MayStart(node.Value)) continue;
                queue.Remove(node);
                return node.Value;
            }
            return null;
        }
    }

    private void Start(Lane lane, string key)
    {
        if (_cardLanes.Append(_addressLane).Any(l => l.InFlight == key)) return; // already downloading on another connection
        lane.InFlight = key;
        var url = key.StartsWith(UrlPrefix) ? key[UrlPrefix.Length..]
            : FileUrl(key) is { } file ? file
            : key.StartsWith(BackPrefix)
            ? (key[BackPrefix.Length..] is var back && back.StartsWith("id:") ? _module!.BackImageUrlById(back[3..]) : _module!.BackImageUrl(back))
            : key.StartsWith("id:") ? _module!.ImageUrlById(key[3..]) : _module!.ImageUrl(key);
        if (url is null)
        {
            Fail(lane, key);
            return;
        }
        var err = lane.Http.Request(url, new[] { $"User-Agent: {_module!.Sources.UserAgent}", "Accept: image/*" });
        if (err != Error.Ok) Fail(lane, key);
    }

    /// <summary>The seconds a Retry-After header asks for (0 without one).</summary>
    private static double RetryAfter(string[] headers)
    {
        foreach (var header in headers)
            if (header.StartsWith("Retry-After:", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(header["Retry-After:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return seconds;
        return 0;
    }

    private void OnRequestCompleted(Lane lane, long result, long responseCode, string[] headers, byte[] body)
    {
        var key = lane.InFlight!;
        lane.InFlight = null;
        bool fromFileHost = !key.StartsWith(UrlPrefix) && FileUrl(key) is not null;
        // Too many requests: the host wants a pause (the source blocks requests for 30 seconds after one, and keeps
        // blocking a client that doesn't wait). The picture goes back to the front of its queue.
        if (responseCode == 429)
        {
            double pause = Math.Max(TooManyRequestsPause, RetryAfter(headers));
            if (fromFileHost) _fileCooldown = Math.Max(_fileCooldown, pause);
            else
            {
                _cardCooldown = Math.Max(_cardCooldown, pause);
                if (ByName(key)) _nameCooldown = Math.Max(_nameCooldown, pause);
            }
            Requeue(key);
            return;
        }
        // The file host doesn't have it: ask the source's own address.
        if (fromFileHost && (result != (long)HttpRequest.Result.Success || responseCode != 200 || body.Length == 0))
        {
            _notOnFileHost.Add(key);
            Requeue(key);
            return;
        }
        _pending.Remove(key);
        if (result != (long)HttpRequest.Result.Success || responseCode != 200 || body.Length == 0)
        {
            Fail(lane, key, $"result {result}, HTTP {responseCode}");
            return;
        }
        bool every = _everyActive.Remove(key);
        using (var file = FileAccess.Open(CachePath(key), FileAccess.ModeFlags.Write))
        {
            file?.StoreBuffer(body);
            bool stored = file is not null && file.GetError() == Error.Ok;
            if (every && stored) _everyHave++;
            // Most likely the memory card is full: the rest would fail the same way.
            if (every && !stored) _everyStopped = "No room left to save the pictures.";
        }
        // Fetched ahead and not wanted yet: it stays on disk only.
        if (_waiting.ContainsKey(key)) StartDecoding(key, CachePath(key), body);
    }

    private void Requeue(string key)
    {
        if (key.StartsWith(UrlPrefix)) _addressQueue.Enqueue(key);
        else (_waiting.ContainsKey(key) ? _cardQueue : _prefetchQueue).AddFirst(key);
    }

    private void Fail(Lane lane, string name, string? why = null)
    {
        lane.InFlight = null;
        _pending.Remove(name);
        if (_everyActive.Remove(name)) _everyFailed++;
        _failed.Add(name); // card keeps its text-only fallback face for this session
        _waiting.Remove(name);
        GD.PushWarning($"Could not load image for '{name}'{(why is null ? "" : $" ({why})")}.");
    }

    private static Image? TryDecode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        var image = new Image();
        if (image.LoadJpgFromBuffer(bytes) != Error.Ok && image.LoadPngFromBuffer(bytes) != Error.Ok) return null;
        image.GenerateMipmaps();
        return image;
    }

    private static string CachePath(string cardName) => $"{CacheDir}/{FileName(cardName)}";

    private static string FileName(string cardName)
    {
        // An address can be long and differ only in case or punctuation: its file is named by a hash of it.
        if (cardName.StartsWith(UrlPrefix))
            return $"url_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cardName)))[..32].ToLowerInvariant()}.img";
        var safe = string.Concat(cardName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        return $"{safe}.large.img";
    }
}
