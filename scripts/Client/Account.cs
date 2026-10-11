// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Net.Services;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>
/// The player's account: the external sign-in (Discord) that the online services use instead of a device account, so the
/// player's identity follows them to other devices. Only Discord's refresh token is kept (user://account.json, readable
/// only by this user); access tokens are asked for when needed.
/// </summary>
public static class Account
{
    /// <summary>The Discord application (public: its secret stays on the career service).</summary>
    public const string DiscordClientId = "1558829463402315846";

    /// <summary>The career service (ARCANUM_CAREER_SERVICE changes it, for a local test service).</summary>
    public static readonly Uri ServiceUrl = new(System.Environment.GetEnvironmentVariable("ARCANUM_CAREER_SERVICE") is { Length: > 0 } url
        ? url.TrimEnd('/') + "/" : "https://arcanum-career.duckdns.org/");

    private const string Path = "user://account.json";
    private static readonly System.Net.Http.HttpClient Http = new() { BaseAddress = ServiceUrl, Timeout = TimeSpan.FromSeconds(20) };
    private static readonly DiscordSignIn Discord = new(Http, DiscordClientId);
    private static readonly System.Net.Http.HttpClient DiscordApi = new() { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(15) };
    private static string? _refreshToken;
    private static bool _loaded;
    private static readonly SemaphoreSlim Refreshing = new(1, 1);
    private static (string Token, DateTime Until)? _access;

    /// <summary>
    /// Testing two instances on one computer without two Discord accounts: ARCANUM_EOS_DEVICE=1 (or ARCANUM_EOS_NEW_DEVICE=1)
    /// signs in to the online services with a device account instead.
    /// </summary>
    public static bool UsesDeviceAccount =>
        System.Environment.GetEnvironmentVariable("ARCANUM_EOS_DEVICE") == "1" || System.Environment.GetEnvironmentVariable("ARCANUM_EOS_NEW_DEVICE") == "1";

    /// <summary>The account changed (signed in or out).</summary>
    public static event Action? Changed;

    /// <summary>The player has signed in with Discord on this device.</summary>
    public static bool HasDiscord
    {
        get { Load(); return _refreshToken is not null; }
    }

    /// <summary>
    /// Opens Discord's sign-in in the browser and waits for the player (until <paramref name="cancel"/>); returns their
    /// Discord name (null when Discord doesn't say).
    /// </summary>
    public static async Task<string?> SignInWithDiscordAsync(CancellationToken cancel)
    {
        var tokens = await Discord.SignInAsync(url => OS.ShellOpen(url), cancel);
        Keep(tokens);
        Changed?.Invoke();
        try { return await DiscordSignIn.NameAsync(DiscordApi, tokens.AccessToken, cancel); }
        catch (Exception) { return null; } // the name is only offered
    }

    /// <summary>
    /// A Discord access token for the online services' sign-in, or null when the player hasn't signed in with Discord
    /// (or Discord no longer accepts the sign-in: then the player is signed out and asked again).
    /// </summary>
    public static async Task<string?> DiscordAccessTokenAsync(CancellationToken cancel = default)
    {
        Load();
        await Refreshing.WaitAsync(cancel);
        try
        {
            if (_access is { } access && access.Until > DateTime.UtcNow.AddMinutes(5)) return access.Token;
            if (_refreshToken is not { } refresh) return null;
            try
            {
                Keep(await Discord.RefreshAsync(refresh, cancel));
                return _access?.Token;
            }
            catch (DiscordRefusedException)
            {
                SignOut();
                return null;
            }
        }
        finally { Refreshing.Release(); }
    }

    public static void SignOut()
    {
        _refreshToken = null;
        _access = null;
        Save();
        Changed?.Invoke();
    }

    private static void Keep(DiscordTokens tokens)
    {
        _access = (tokens.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn)));
        if (tokens.RefreshToken.Length > 0) _refreshToken = tokens.RefreshToken;
        Save();
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        if (!FileAccess.FileExists(Path)) return;
        try
        {
            using var json = JsonDocument.Parse(FileAccess.GetFileAsString(Path));
            _refreshToken = json.RootElement.TryGetProperty("discordRefreshToken", out var token) ? token.GetString() : null;
        }
        catch (JsonException e) { GD.PushWarning($"Account file unreadable: {e.Message}"); }
    }

    private static void Save()
    {
        string file = ProjectSettings.GlobalizePath(Path);
        if (_refreshToken is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = System.IO.FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(file, options);
        JsonSerializer.Serialize(stream, new Dictionary<string, string> { ["discordRefreshToken"] = _refreshToken });
    }
}
