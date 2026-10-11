// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Arcanum.CareerService;

/// <summary>The Discord application. The secret comes from the environment (Discord__ClientSecret), never from the repository.</summary>
public sealed class DiscordOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenUrl { get; set; } = "https://discord.com/api/oauth2/token";
}

/// <param name="Code">The authorization code Discord sent to the game's loopback address.</param>
/// <param name="CodeVerifier">The PKCE verifier the game made for this sign-in.</param>
/// <param name="RedirectUri">The loopback address the game listened on (it must match the authorization request).</param>
public sealed record DiscordCodeRequest(string Code, string CodeVerifier, string RedirectUri);

public sealed record DiscordRefreshRequest(string RefreshToken);

/// <summary>What the game gets back: Discord's access token (for the EOS sign-in) and the refresh token to keep.</summary>
public sealed record DiscordTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn);

/// <summary>Exchanges Discord authorization codes and refresh tokens with the application's client secret.</summary>
public sealed partial class DiscordAuth(HttpClient http, IOptions<DiscordOptions> options, ILogger<DiscordAuth> log)
{
    // Only the game's own loopback callback: the service won't exchange codes meant for other places.
    [GeneratedRegex(@"^http://127\.0\.0\.1:\d{1,5}/callback$")]
    private static partial Regex LoopbackCallback();

    public async Task<IResult> Exchange(DiscordCodeRequest request, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.CodeVerifier)
            || request.CodeVerifier.Length is < 43 or > 128 || !LoopbackCallback().IsMatch(request.RedirectUri ?? ""))
            return Results.BadRequest(new { error = "invalid_request" });
        return await Token(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = request.Code,
            ["code_verifier"] = request.CodeVerifier,
            ["redirect_uri"] = request.RedirectUri!,
        }, cancel);
    }

    public async Task<IResult> Refresh(DiscordRefreshRequest request, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return Results.BadRequest(new { error = "invalid_request" });
        return await Token(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = request.RefreshToken,
        }, cancel);
    }

    private async Task<IResult> Token(Dictionary<string, string> form, CancellationToken cancel)
    {
        var app = options.Value;
        if (app.ClientId.Length == 0 || app.ClientSecret.Length == 0)
        {
            log.LogError("The Discord application isn't configured (Discord__ClientId / Discord__ClientSecret).");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        form["client_id"] = app.ClientId;
        form["client_secret"] = app.ClientSecret;
        using var response = await http.PostAsync(app.TokenUrl, new FormUrlEncodedContent(form), cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            // Discord's error is about the code or token the game sent (expired, used twice...): the game asks to sign in again.
            log.LogInformation("Discord refused a {Grant}: {Status}", form["grant_type"], (int)response.StatusCode);
            return Results.Json(new { error = "discord_refused" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return Results.Ok(new DiscordTokens(
            root.GetProperty("access_token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : "",
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 0));
    }
}
