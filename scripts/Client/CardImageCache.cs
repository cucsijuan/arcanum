// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// Autoload that downloads card images on demand from the source configured by the content module and caches
/// them under user://. Images are never bundled with the app. Card pictures come one at a time, throttled as the module
/// asks; pictures the module gives by address (booster packs, card backs) live on other hosts and download on their
/// own connection, so they never wait behind a pack's worth of card pictures.
/// </summary>
public partial class CardImageCache : Node
{
    private const string CacheDir = "user://card_cache";

    private static CardImageCache? _instance;

    private readonly Dictionary<string, Texture2D> _memory = new();
    private readonly Dictionary<string, List<Action<Texture2D>>> _waiting = new();
    private readonly HashSet<string> _failed = new();
    private Arcanum.Data.Modules.ContentModule? _module;

    /// <summary>A download queue with its own connection, one picture at a time.</summary>
    private sealed class Lane
    {
        public HttpRequest Http = null!;
        public readonly Queue<string> Queue = new();
        public string? InFlight;
        public double Cooldown;
        /// <summary>Waits the module's minimum interval between requests (its image source asks for it).</summary>
        public bool Throttled;
    }

    private readonly Lane _cards = new() { Throttled = true };
    private readonly Lane _addresses = new();
    private Lane LaneFor(string key) => key.StartsWith(UrlPrefix) ? _addresses : _cards;

    /// <summary>Sets where images come from. Until a module is configured no images are requested.</summary>
    public static void Configure(Arcanum.Data.Modules.ContentModule module)
    {
        if (_instance is null) return;
        _instance._module = module;
        _instance._failed.Clear();
    }

    public override void _Ready()
    {
        _instance = this;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(CacheDir));
        foreach (var lane in new[] { _cards, _addresses })
        {
            lane.Http = new HttpRequest { Timeout = 20 };
            AddChild(lane.Http);
            lane.Http.RequestCompleted += (result, code, headers, body) => OnRequestCompleted(lane, result, code, body);
        }
    }

    /// <summary>
    /// Image key for a card: its exact image id when it has one ("id:..."), its name otherwise. Tokens without an
    /// exact id get no key: many tokens share a name, so looking them up by name would show the wrong picture.
    /// </summary>
    public static string? KeyFor(Arcanum.Engine.Views.CardView view)
    {
        var key = view.ImageKey is { } id ? "id:" + id : view.IsToken ? null : view.Name;
        return key is not null && view.IsBackFace ? BackPrefix + key : key;
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

    /// <summary>Calls <paramref name="onLoaded"/> (possibly immediately) once the image for <paramref name="cardName"/> is available.</summary>
    public static void Request(string cardName, Action<Texture2D> onLoaded) => _instance?.RequestInternal(cardName, onLoaded);

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

        var path = CachePath(cardName);
        if (FileAccess.FileExists(path) && TryCreateTexture(FileAccess.GetFileAsBytes(path)) is { } fromDisk)
        {
            _memory[cardName] = fromDisk;
            onLoaded(fromDisk);
            return;
        }

        if (!_waiting.TryGetValue(cardName, out var callbacks))
        {
            _waiting[cardName] = callbacks = new List<Action<Texture2D>>();
            LaneFor(cardName).Queue.Enqueue(cardName);
        }
        callbacks.Add(onLoaded);
    }

    public override void _Process(double delta)
    {
        Next(_cards, delta);
        Next(_addresses, delta);
    }

    private void Next(Lane lane, double delta)
    {
        lane.Cooldown -= delta;
        if (lane.InFlight is not null || lane.Cooldown > 0 || lane.Queue.Count == 0) return;

        var key = lane.InFlight = lane.Queue.Dequeue();
        var url = key.StartsWith(UrlPrefix) ? key[UrlPrefix.Length..]
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

    private void OnRequestCompleted(Lane lane, long result, long responseCode, byte[] body)
    {
        var name = lane.InFlight!;
        lane.InFlight = null;
        if (lane.Throttled) lane.Cooldown = (_module?.Sources.Images.MinIntervalMs ?? 100) / 1000.0;

        if (result != (long)HttpRequest.Result.Success || responseCode != 200 || TryCreateTexture(body) is not { } texture)
        {
            Fail(lane, name);
            return;
        }

        using (var file = FileAccess.Open(CachePath(name), FileAccess.ModeFlags.Write)) file?.StoreBuffer(body);
        _memory[name] = texture;
        if (_waiting.Remove(name, out var callbacks))
            foreach (var callback in callbacks) callback(texture);
    }

    private void Fail(Lane lane, string name)
    {
        lane.InFlight = null;
        _failed.Add(name); // card keeps its text-only fallback face for this session
        _waiting.Remove(name);
        GD.PushWarning($"Could not load image for '{name}'.");
    }

    private static Texture2D? TryCreateTexture(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        var image = new Image();
        if (image.LoadJpgFromBuffer(bytes) != Error.Ok && image.LoadPngFromBuffer(bytes) != Error.Ok) return null;
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static string CachePath(string cardName)
    {
        // An address can be long and differ only in case or punctuation: its file is named by a hash of it.
        if (cardName.StartsWith(UrlPrefix))
            return $"{CacheDir}/url_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cardName)))[..32].ToLowerInvariant()}.img";
        var safe = string.Concat(cardName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        return $"{CacheDir}/{safe}.large.img";
    }
}
