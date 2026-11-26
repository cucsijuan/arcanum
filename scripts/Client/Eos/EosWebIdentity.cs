// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcanum.Client.Eos;

/// <summary>
/// The player's identity token from the online services' web interface, for systems the services' library doesn't run on
/// (a console): the Discord access token is exchanged for the same identity the library signs in to (the same player on
/// every device), which the career service accepts. Kept until shortly before it expires.
/// </summary>
internal static class EosWebIdentity
{
    private const string TokenUrl = "https://api.epicgames.dev/auth/v1/oauth/token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Asking = new(1, 1);
    private static (string Token, DateTime Until)? _kept;

    /// <summary>Whether this build has the services' keys.</summary>
    public static bool Available => EosKeys.Current is not null;

    /// <summary>A current identity token, or null when the player hasn't signed in with Discord (or the services refused).</summary>
    public static async Task<string?> IdTokenAsync(CancellationToken cancel = default)
    {
        if (EosKeys.Current is not { } keys) return null;
        await Asking.WaitAsync(cancel);
        try
        {
            if (_kept is { } kept && kept.Until > DateTime.UtcNow.AddMinutes(5)) return kept.Token;
            if (await Account.DiscordAccessTokenAsync(cancel) is not { } discord) return null;
            // The request is made and its answer read on a worker thread: setting up a connection takes long on slow devices.
            var (ok, status, answer) = await Task.Run(() => AskAsync(keys, discord, cancel), cancel);
            if (!ok)
            {
                Godot.GD.Print($"Online services: the web sign-in was refused ({status}).");
                // A Discord token the services no longer take: the next try asks Discord for a fresh one.
                if (status is 400 or 401 or 500) Account.ForgetAccessToken();
                return null;
            }
            _kept = answer;
            return answer?.Token;
        }
        finally { Asking.Release(); }
    }

    private static async Task<(bool Ok, int Status, (string Token, DateTime Until)? Answer)> AskAsync(EosKeys keys, string discord, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "external_auth",
                ["external_auth_type"] = "discord_access_token",
                ["external_auth_token"] = discord,
                ["deployment_id"] = keys.DeploymentId,
                ["nonce"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keys.ClientId}:{keys.ClientSecret}")));
        using var response = await Http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) return (false, (int)response.StatusCode, null);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var token = (root.TryGetProperty("id_token", out var id) ? id : root.GetProperty("access_token")).GetString();
        if (token is not { Length: > 0 }) return (true, (int)response.StatusCode, null);
        var until = root.TryGetProperty("expires_at", out var at) && at.GetString() is { } text && DateTime.TryParse(text, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var expires)
            ? expires
            : DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600);
        return (true, (int)response.StatusCode, (token, until));
    }
}
