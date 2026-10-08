// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// Autoload that downloads card images on demand from the source configured by the content module and caches
/// them under user://. Images are never bundled with the app. Requests are throttled as the module asks.
/// </summary>
public partial class CardImageCache : Node
{
    private const string CacheDir = "user://card_cache";

    private static CardImageCache? _instance;

    private readonly Dictionary<string, Texture2D> _memory = new();
    private readonly Dictionary<string, List<Action<Texture2D>>> _waiting = new();
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _failed = new();
    private HttpRequest _http = null!;
    private string? _inFlight;
    private double _cooldown;
    private Arcanum.Data.Modules.ContentModule? _module;

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
        _http = new HttpRequest { Timeout = 20 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;
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
            _queue.Enqueue(cardName);
        }
        callbacks.Add(onLoaded);
    }

    public override void _Process(double delta)
    {
        _cooldown -= delta;
        if (_inFlight is not null || _cooldown > 0 || _queue.Count == 0) return;

        _inFlight = _queue.Dequeue();
        var url = _inFlight.StartsWith(UrlPrefix) ? _inFlight[UrlPrefix.Length..]
            : _inFlight.StartsWith(BackPrefix)
            ? (_inFlight[BackPrefix.Length..] is var back && back.StartsWith("id:") ? _module!.BackImageUrlById(back[3..]) : _module!.BackImageUrl(back))
            : _inFlight.StartsWith("id:") ? _module!.ImageUrlById(_inFlight[3..]) : _module!.ImageUrl(_inFlight);
        if (url is null)
        {
            Fail(_inFlight);
            return;
        }
        var err = _http.Request(url, new[] { $"User-Agent: {_module.Sources.UserAgent}", "Accept: image/*" });
        if (err != Error.Ok) Fail(_inFlight);
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        var name = _inFlight!;
        _inFlight = null;
        _cooldown = (_module?.Sources.Images.MinIntervalMs ?? 100) / 1000.0;

        if (result != (long)HttpRequest.Result.Success || responseCode != 200 || TryCreateTexture(body) is not { } texture)
        {
            Fail(name);
            return;
        }

        using (var file = FileAccess.Open(CachePath(name), FileAccess.ModeFlags.Write)) file?.StoreBuffer(body);
        _memory[name] = texture;
        if (_waiting.Remove(name, out var callbacks))
            foreach (var callback in callbacks) callback(texture);
    }

    private void Fail(string name)
    {
        _inFlight = null;
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
