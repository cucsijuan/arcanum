// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// Autoload that downloads card images from Scryfall on demand and caches them under user://.
/// Images are never bundled with the app. Requests are throttled to respect Scryfall's rate limits.
/// </summary>
public partial class CardImageCache : Node
{
    private const string CacheDir = "user://card_cache";
    private const double RequestInterval = 0.12; // Scryfall asks for 50–100 ms between requests

    private static CardImageCache? _instance;

    private readonly Dictionary<string, Texture2D> _memory = new();
    private readonly Dictionary<string, List<Action<Texture2D>>> _waiting = new();
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _failed = new();
    private HttpRequest _http = null!;
    private string? _inFlight;
    private double _cooldown;

    public override void _Ready()
    {
        _instance = this;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(CacheDir));
        _http = new HttpRequest { Timeout = 20 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;
    }

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
        if (_failed.Contains(cardName)) return;

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
        var url = "https://api.scryfall.com/cards/named?format=image&version=large&exact=" + Uri.EscapeDataString(_inFlight);
        var err = _http.Request(url, new[] { "User-Agent: Arcanum/0.1 (open-source card game client)", "Accept: image/*" });
        if (err != Error.Ok) Fail(_inFlight);
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        var name = _inFlight!;
        _inFlight = null;
        _cooldown = RequestInterval;

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
        var safe = string.Concat(cardName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        return $"{CacheDir}/{safe}.large.img";
    }
}
