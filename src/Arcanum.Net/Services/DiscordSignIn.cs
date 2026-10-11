// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Arcanum.Net.Services;

/// <summary>Discord's tokens for the player: the access token signs in to the online services, the refresh token gets new ones.</summary>
public sealed record DiscordTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn);

/// <summary>Discord said no: the code or refresh token is no longer good (expired, revoked); the player signs in again.</summary>
public sealed class DiscordRefusedException(string message) : Exception(message);

/// <summary>
/// Signing in with Discord: the browser shows Discord's authorization page (PKCE, "identify" scope only), Discord sends the
/// code back to a loopback address the game listens on for that moment, and the career service exchanges it for tokens
/// (the application's secret stays on the service).
/// </summary>
public sealed class DiscordSignIn(HttpClient service, string clientId, int port = DiscordSignIn.DefaultPort)
{
    /// <summary>The port registered as the Discord application's redirect (http://127.0.0.1:47080/callback).</summary>
    public const int DefaultPort = 47080;

    public string RedirectUri => $"http://127.0.0.1:{port}/callback";

    /// <summary>A new PKCE verifier (43 characters of URL-safe base64).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Discord's authorization page for this sign-in.</summary>
    public string AuthorizeUrl(string verifier, string state) =>
        "https://discord.com/oauth2/authorize?response_type=code"
        + $"&client_id={Uri.EscapeDataString(clientId)}&scope=identify"
        + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={Uri.EscapeDataString(state)}"
        + $"&code_challenge={Challenge(verifier)}&code_challenge_method=S256&prompt=none";

    /// <summary>
    /// Signs in: opens Discord's page with <paramref name="openBrowser"/>, waits for the player to authorize (until
    /// <paramref name="cancel"/>), and returns the tokens.
    /// </summary>
    public async Task<DiscordTokens> SignInAsync(Action<string> openBrowser, CancellationToken cancel)
    {
        string verifier = NewVerifier(), state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = cancel.Register(listener.Stop);
        openBrowser(AuthorizeUrl(verifier, state));
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
            var query = context.Request.QueryString;
            if (context.Request.Url?.AbsolutePath != "/callback" || query["state"] != state)
            {
                await Answer(context, HttpStatusCode.NotFound, "Nothing here.");
                continue; // not Discord's answer to this sign-in
            }
            if (query["code"] is not { Length: > 0 } code)
            {
                await Answer(context, HttpStatusCode.OK, "Signing in was cancelled. You can close this page.");
                throw new DiscordRefusedException(query["error"] == "access_denied" ? "Discord sign-in was cancelled." : $"Discord refused: {query["error"]}.");
            }
            try
            {
                var tokens = await Post("auth/discord", new { code, codeVerifier = verifier, redirectUri = RedirectUri }, cancel);
                await Answer(context, HttpStatusCode.OK, "Signed in. You can close this page and go back to the game.");
                return tokens;
            }
            catch (Exception)
            {
                await Answer(context, HttpStatusCode.OK, "Signing in didn't work. Go back to the game to try again.");
                throw;
            }
        }
    }

    /// <summary>New tokens from a refresh token, without the player.</summary>
    public Task<DiscordTokens> RefreshAsync(string refreshToken, CancellationToken cancel) =>
        Post("auth/discord/refresh", new { refreshToken }, cancel);

    private async Task<DiscordTokens> Post(string path, object body, CancellationToken cancel)
    {
        using var response = await service.PostAsJsonAsync(path, body, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new DiscordRefusedException("Discord sign-in expired; sign in again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DiscordTokens>(cancel)
            ?? throw new InvalidOperationException("The career service sent no tokens.");
    }

    private static async Task Answer(HttpListenerContext context, HttpStatusCode status, string message)
    {
        var page = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Arcanum</title><body style=\"font-family:sans-serif;padding:3em\"><p>{WebUtility.HtmlEncode(message)}</p>");
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
