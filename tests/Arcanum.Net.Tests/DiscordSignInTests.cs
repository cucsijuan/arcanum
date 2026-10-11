// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arcanum.Net.Services;

namespace Arcanum.Net.Tests;

public class DiscordSignInTests
{
    /// <summary>Stands in for the career service: records what it was sent and answers with tokens.</summary>
    private sealed class FakeService(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<(string Path, JsonElement Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel)).RootElement.Clone();
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"accessToken\":\"at\",\"refreshToken\":\"rt\",\"expiresIn\":604800}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void The_challenge_is_the_verifiers_SHA256_in_url_safe_base64()
    {
        // Checked against: printf %s <verifier> | openssl dgst -sha256 -binary | base64 (then + → -, / → _, no =).
        Assert.Equal("AZipJCukqhKjQAEgQZPQKcb9q3rJAjJDqMhMlFIn3w4", DiscordSignIn.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r8W2gFXjr4yfrN"));
        Assert.Equal(43, DiscordSignIn.NewVerifier().Length);
    }

    [Fact]
    public async Task Signing_in_sends_the_code_and_verifier_from_the_loopback_callback_to_the_service()
    {
        var fake = new FakeService();
        var signIn = new DiscordSignIn(new HttpClient(fake) { BaseAddress = new Uri("https://service.test/") }, "123", FreePort());
        using var browser = new HttpClient();
        string? opened = null;
        var tokens = await signIn.SignInAsync(url =>
        {
            opened = url;
            var query = Query(url);
            // Something else asking first is ignored; then Discord's answer with the right state arrives.
            _ = Task.Run(async () =>
            {
                await browser.GetAsync($"{query["redirect_uri"]}?code=nope&state=wrong");
                await browser.GetAsync($"{query["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(query["state"])}");
            });
        }, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.Equal(new DiscordTokens("at", "rt", 604800), tokens);
        var query = Query(opened!);
        Assert.Equal("123", query["client_id"]);
        Assert.Equal("identify", query["scope"]);
        var (path, body) = Assert.Single(fake.Requests);
        Assert.Equal("/auth/discord", path);
        Assert.Equal("the-code", body.GetProperty("code").GetString());
        Assert.Equal(query["code_challenge"], DiscordSignIn.Challenge(body.GetProperty("codeVerifier").GetString()!));
        Assert.Equal(signIn.RedirectUri, body.GetProperty("redirectUri").GetString());
    }

    [Fact]
    public async Task A_cancelled_authorization_is_refused()
    {
        var signIn = new DiscordSignIn(new HttpClient(new FakeService()) { BaseAddress = new Uri("https://service.test/") }, "123", FreePort());
        using var browser = new HttpClient();
        await Assert.ThrowsAsync<DiscordRefusedException>(() => signIn.SignInAsync(url =>
        {
            var query = Query(url);
            _ = browser.GetAsync($"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"])}");
        }, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
    }

    [Fact]
    public async Task Closing_the_sign_in_stops_listening()
    {
        var signIn = new DiscordSignIn(new HttpClient(new FakeService()), "123", FreePort());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn.SignInAsync(_ => { }, cancel.Token));
    }

    [Fact]
    public async Task A_refused_refresh_asks_to_sign_in_again()
    {
        var signIn = new DiscordSignIn(new HttpClient(new FakeService(HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("https://service.test/") }, "123");
        await Assert.ThrowsAsync<DiscordRefusedException>(() => signIn.RefreshAsync("old", CancellationToken.None));
    }

    /// <summary>Stands in for Discord's API: answers users/@me with a user.</summary>
    private sealed class FakeDiscord(string user) : HttpMessageHandler
    {
        public string? Authorization;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(user, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_players_name_is_their_display_name_or_else_their_user_name()
    {
        var discord = new FakeDiscord("{\"id\":\"1\",\"username\":\"juan_42\",\"global_name\":\"Juan\"}");
        Assert.Equal("Juan", await DiscordSignIn.NameAsync(new HttpClient(discord) { BaseAddress = new Uri("https://discord.test/api/") }, "at", CancellationToken.None));
        Assert.Equal("Bearer at", discord.Authorization);
        var plain = new FakeDiscord("{\"id\":\"1\",\"username\":\"juan_42\",\"global_name\":null}");
        Assert.Equal("juan_42", await DiscordSignIn.NameAsync(new HttpClient(plain) { BaseAddress = new Uri("https://discord.test/api/") }, "at", CancellationToken.None));
    }
}
