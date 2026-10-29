// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Arcanum.CareerService;
using Arcanum.Data.Career;
using Microsoft.AspNetCore.RateLimiting;

// The career service: signs career profiles and keeps the secrets the game can't hold (see docs/career-mode.md).
// It listens on the loopback address only; the web server in front of it (deploy/Caddyfile) adds HTTPS.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.AddHttpClient<DiscordAuth>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.Configure<EosOptions>(builder.Configuration.GetSection("Eos"));
builder.Services.AddHttpClient<EosIdentity>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<CareerStore>();
builder.Services.AddSingleton<CareerWorlds>();
builder.Services.AddSingleton<EventDesk>();
builder.Services.AddSingleton(services =>
{
    // The sealing key never leaves this machine (deploy/setup.sh makes it); the game has its public key (CareerSeals.PublicKeys).
    var config = builder.Configuration;
    var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(config["Career:SealKeyFile"]
        ?? Path.Combine(Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") ?? "/etc/arcanum-career", "seal-key.pem")));
    var worlds = services.GetRequiredService<CareerWorlds>();
    return new CareerChecker(worlds.For, config["Career:Game"] ?? throw new InvalidOperationException("Career:Game (the game version) isn't set."),
        DateTime.Parse(config["Career:ImportBefore"] ?? "2026-11-01T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        key, config["Career:SealKeyId"] ?? "k1", onlineResult: services.GetRequiredService<EventDesk>().Check,
        // The version checked before this one, and since when this one is (deploy.sh writes both when the version changes).
        previousGame: config["Career:PreviousGame"],
        switchedAt: config["Career:Since"] is { } since ? DateTime.Parse(since, null, System.Globalization.DateTimeStyles.AdjustToUniversal) : default);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per address (the web server in front passes it on): sign-ins are rare, so a few a minute is plenty.
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Careers sync a few seconds after each change: generous, but not a flood.
    options.AddPolicy("career", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseRateLimiter();

// Alive, and the service's clock (dailies and event windows use it when the game is online).
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

// Discord sign-in: the game sends the authorization code it got on its loopback address; the service exchanges it
// with the client secret and returns Discord's tokens to the game.
app.MapPost("/auth/discord", async (DiscordCodeRequest request, DiscordAuth discord, CancellationToken cancel) =>
        await discord.Exchange(request, cancel))
    .RequireRateLimiting("auth");

// A new access token from the refresh token, so the game signs in again without asking the player.
app.MapPost("/auth/discord/refresh", async (DiscordRefreshRequest request, DiscordAuth discord, CancellationToken cancel) =>
        await discord.Refresh(request, cancel))
    .RequireRateLimiting("auth");

// The career world takes a while to build on a small machine: built at start and kept current in the background.
var warm = new Timer(_ => app.Services.GetRequiredService<CareerWorlds>().Warm(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
app.Lifetime.ApplicationStopping.Register(() => warm.Dispose());

// One sync at a time per player, so two devices can't both build on the same seal.
var syncing = new ConcurrentDictionary<string, SemaphoreSlim>();

// A career's new actions, checked with the rules and sealed (see CareerChecker).
app.MapPost("/career/sync", async (HttpRequest http, CareerSyncRequest request, EosIdentity eos, CareerStore store, CareerChecker checker, ILogger<CareerStore> log, CancellationToken cancel) =>
    {
        if (!eos.Configured) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (await eos.PlayerAsync(http, cancel) is not { } player) return Results.Unauthorized();
        var gate = syncing.GetOrAdd(player, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            using var db = store.Open();
            var (response, next) = checker.Check(player, store.Career(db, player), request);
            if (next is not null) store.Save(db, player, next);
            if (response.Status == CareerSyncStatus.Rejected)
            {
                store.Refused(db, player, response.Reason ?? "");
                log.LogInformation("Career sync of {Player} refused: {Reason}", player, response.Reason);
            }
            return Results.Ok(response);
        }
        finally
        {
            gate.Release();
        }
    })
    .RequireRateLimiting("career");

// The career as the service checked it: a new device, or a career that went apart from the checked one, starts from it.
app.MapGet("/career/profile", async (HttpRequest http, EosIdentity eos, CareerStore store, CancellationToken cancel) =>
    {
        if (!eos.Configured) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (await eos.PlayerAsync(http, cancel) is not { } player) return Results.Unauthorized();
        using var db = store.Open();
        return store.Career(db, player) is { } career ? Results.Ok(new CareerProfile(career.Seal, career.Journal, career.State)) : Results.NotFound();
    })
    .RequireRateLimiting("career");

// Online events: check in for a start, find the table, the host's lobby code, the results, the leaderboards.
async Task<string?> PlayerOf(HttpRequest http, EosIdentity eos, CancellationToken cancel) => eos.Configured ? await eos.PlayerAsync(http, cancel) : null;

app.MapPost("/events/checkin", async (HttpRequest http, CheckInRequest request, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.CheckIn(player, request, DateTime.UtcNow) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapGet("/events/table", async (HttpRequest http, string content, string @event, DateOnly day, DateTime start, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.Table(player, content, @event, day, start, DateTime.UtcNow) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapGet("/events/tables/{id}", async (HttpRequest http, string id, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.TableById(player, id, DateTime.UtcNow) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapPost("/events/tables/{id}/code", async (HttpRequest http, string id, CodeRequest request, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.Code(player, id, request.Code, DateTime.UtcNow) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapPost("/events/tables/{id}/report", async (HttpRequest http, string id, TableReport report, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.Report(player, id, report) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapPost("/events/tables/{id}/result", async (HttpRequest http, string id, OwnResult result, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        await PlayerOf(http, eos, cancel) is { } player ? desk.Result(player, id, result) : Results.Unauthorized())
    .RequireRateLimiting("career");
app.MapGet("/leaderboard/{board}", async (HttpRequest http, string board, EosIdentity eos, EventDesk desk, CancellationToken cancel) =>
        board is "rank" or "month" && await PlayerOf(http, eos, cancel) is { } player ? Results.Ok(desk.Leaderboard(player, board, DateTime.UtcNow)) : Results.Unauthorized())
    .RequireRateLimiting("career");

app.Run();
