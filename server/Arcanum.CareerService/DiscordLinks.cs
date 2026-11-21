// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Arcanum.CareerService;

/// <summary>What a device without a browser gets to start a sign-in: the code to show, and its own secret to ask with.</summary>
public sealed record LinkStart(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("deviceToken")] string DeviceToken,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

public sealed record LinkPoll(string DeviceToken);

/// <summary>
/// Signing in on a device that can't show Discord's pages (a console, a television): the device asks for a link and shows
/// its short code; the player opens the link page on a phone or computer, types the code and signs in with Discord there;
/// Discord sends the answer to this service, which exchanges it with the client secret and keeps the tokens until the
/// device, asking with its own secret, takes them (once). Links last ten minutes and live only in memory.
/// </summary>
public sealed class DiscordLinks(DiscordAuth discord, IOptions<DiscordOptions> options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int PollSeconds = 3;

    // No 0/O, 1/I/L: the code is read off a screen and typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private sealed class Link
    {
        public required string Code, DeviceToken, State;
        public required DateTimeOffset Expires;
        public DiscordTokens? Tokens;
        public bool Refused;
    }

    private readonly ConcurrentDictionary<string, Link> _byCode = new();

    public LinkStart Start()
    {
        Prune();
        string code;
        do code = NewCode(); while (_byCode.ContainsKey(code));
        var link = new Link { Code = code, DeviceToken = Secret(32), State = Secret(16), Expires = clock.GetUtcNow() + Lifetime };
        _byCode[code] = link;
        return new LinkStart(Shown(code), link.DeviceToken, options.Value.LinkPage, (int)Lifetime.TotalSeconds, PollSeconds);
    }

    /// <summary>The device's question: 202 while waiting, the tokens once, 401 when Discord refused, 404 when gone.</summary>
    public IResult Poll(LinkPoll request)
    {
        Prune();
        var link = _byCode.Values.FirstOrDefault(l => FixedEquals(l.DeviceToken, request.DeviceToken ?? ""));
        if (link is null) return Results.NotFound(new { error = "expired" });
        if (link.Refused)
        {
            _byCode.TryRemove(link.Code, out _);
            return Results.Json(new { error = "discord_refused" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        if (link.Tokens is not { } tokens) return Results.Json(new { status = "pending" }, statusCode: StatusCodes.Status202Accepted);
        _byCode.TryRemove(link.Code, out _);
        return Results.Ok(tokens);
    }

    /// <summary>Discord's authorization page for the link with this code, or null when there is none.</summary>
    public string? AuthorizeUrl(string? typed)
    {
        Prune();
        if (Normalize(typed) is not { } code || !_byCode.TryGetValue(code, out var link) || link.Tokens is not null) return null;
        var app = options.Value;
        return "https://discord.com/oauth2/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(app.ClientId)}&scope=identify"
            + $"&redirect_uri={Uri.EscapeDataString(app.LinkRedirectUri)}&state={Uri.EscapeDataString(link.State)}";
    }

    /// <summary>Discord's answer: exchanges the code and keeps the tokens for the device. False when no link waits for it.</summary>
    public async Task<bool> Complete(string? state, string? code, string? error, CancellationToken cancel)
    {
        Prune();
        var link = _byCode.Values.FirstOrDefault(l => state is not null && FixedEquals(l.State, state));
        if (link is null || link.Tokens is not null) return false;
        if (string.IsNullOrEmpty(code) || error is not null)
        {
            link.Refused = true;
            return true;
        }
        var tokens = await discord.Tokens(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.Value.LinkRedirectUri,
        }, cancel);
        if (tokens is null) link.Refused = true;
        else link.Tokens = tokens;
        return true;
    }

    /// <summary>The code as typed (any case, with or without the dash or spaces), or null when it can't be one.</summary>
    public static string? Normalize(string? typed)
    {
        var code = new string((typed ?? "").ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return code.Length == 8 && code.All(Alphabet.Contains) ? code : null;
    }

    /// <summary>How the code is shown: two groups of four.</summary>
    public static string Shown(string code) => $"{code[..4]}-{code[4..]}";

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (code, link) in _byCode)
            if (link.Expires <= now) _byCode.TryRemove(code, out _);
    }

    private static string NewCode() => new(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    private static string Secret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>The link page: the code box, or a message (after Discord's answer, or for a code that isn't waiting).</summary>
    public static string Page(string? message = null, string? code = null, bool error = false)
    {
        string body = message is null
            ? $"""
               <p>Type the code the game shows, then sign in with Discord.</p>
               <form action="link/go" method="get">
                 <input name="code" value="{WebUtility.HtmlEncode(code ?? "")}" placeholder="ABCD-2345" maxlength="9" autocomplete="off" autocapitalize="characters" spellcheck="false" autofocus>
                 <button>Continue with Discord</button>
               </form>
               """
            : $"<p class=\"{(error ? "error" : "done")}\">{WebUtility.HtmlEncode(message)}</p>";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <base href="/"><title>Arcanum sign-in</title>
            <style>
              :root { color-scheme: dark; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #0d0b14; color: #ece8f4; font: 18px/1.5 system-ui, sans-serif; }
              main { width: min(420px, calc(100vw - 32px)); text-align: center; }
              h1 { font-size: 28px; letter-spacing: .08em; margin: 0 0 16px; }
              input { box-sizing: border-box; width: 100%; padding: 14px; font: 600 28px/1 ui-monospace, monospace; letter-spacing: .15em; text-align: center; text-transform: uppercase;
                      background: #1b1726; color: inherit; border: 1px solid #5b4a86; border-radius: 10px; }
              button { margin-top: 14px; width: 100%; padding: 14px; font: 600 18px system-ui, sans-serif; color: #fff; background: #5865f2; border: 0; border-radius: 10px; }
              .error { color: #ff9b8f; } .done { color: #9fe0a8; }
            </style></head>
            <body><main><h1>ARCANUM</h1>{{body}}</main></body></html>
            """;
    }
}
