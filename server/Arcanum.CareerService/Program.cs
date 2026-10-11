// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.RateLimiting;
using Arcanum.CareerService;
using Microsoft.AspNetCore.RateLimiting;

// The career service: signs career profiles and keeps the secrets the game can't hold (see docs/career-mode.md).
// It listens on the loopback address only; the web server in front of it (deploy/Caddyfile) adds HTTPS.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.AddHttpClient<DiscordAuth>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per address (the web server in front passes it on): sign-ins are rare, so a few a minute is plenty.
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
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

app.Run();
