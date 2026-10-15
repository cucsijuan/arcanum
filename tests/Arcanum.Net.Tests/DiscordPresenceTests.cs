// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Arcanum.Net.Services;

namespace Arcanum.Net.Tests;

public class DiscordPresenceTests
{
    [Fact]
    public void AFrameIsTheOpcodeAndLengthThenTheJson()
    {
        var frame = DiscordPresence.Encode(1, new JsonObject { ["a"] = "é" });
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(frame.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal("{\"a\":\"\\u00E9\"}", Encoding.UTF8.GetString(frame, 8, frame.Length - 8));
    }

    [Fact]
    public void TheActivityCarriesItsLinesTimePartyAndPicture()
    {
        var presence = new DiscordPresence("123", "https://example.org/icon.png", "Game");
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var command = presence.Command(new DiscordActivity("Commander game", "Turn 7", start, 3, 4), 42);

        Assert.Equal("SET_ACTIVITY", (string?)command["cmd"]);
        Assert.Equal(42, (int?)command["args"]!["pid"]);
        var activity = command["args"]!["activity"]!;
        Assert.Equal("Commander game", (string?)activity["details"]);
        Assert.Equal("Turn 7", (string?)activity["state"]);
        Assert.Equal(1_700_000_000L, (long?)activity["timestamps"]!["start"]);
        Assert.Equal(new[] { 3, 4 }, activity["party"]!["size"]!.AsArray().Select(n => (int)n!).ToArray());
        Assert.Equal("https://example.org/icon.png", (string?)activity["assets"]!["large_image"]);
        Assert.Equal("Game", (string?)activity["assets"]!["large_text"]);
        Assert.False(string.IsNullOrEmpty((string?)command["nonce"]));
    }

    [Fact]
    public void LinesAreKeptWithinWhatDiscordTakes()
    {
        var activity = new DiscordPresence("1").Command(new DiscordActivity(new string('x', 300), "a"), 1)["args"]!["activity"]!;
        Assert.Equal(128, ((string)activity["details"]!).Length);
        Assert.Equal(2, ((string)activity["state"]!).Length); // one character is too short for Discord
        Assert.Null(activity["party"]);
        Assert.Null(activity["timestamps"]);
        Assert.Null(activity["assets"]);
    }

    [Fact]
    public void NoActivityClearsIt()
    {
        var command = new DiscordPresence("1").Command(null, 1);
        Assert.True(command["args"]!.AsObject().ContainsKey("activity"));
        Assert.Null(command["args"]!["activity"]);
    }

    [Fact]
    public void TheSocketIsLookedForInTheRuntimeFolderThenFlatpakAndSnap()
    {
        var paths = DiscordPresence.SocketPaths(name => name == "XDG_RUNTIME_DIR" ? "/run/user/7" : null).ToList();
        Assert.Equal("/run/user/7/discord-ipc-0", paths[0]);
        Assert.Equal("/run/user/7/discord-ipc-9", paths[9]);
        Assert.Equal("/run/user/7/app/com.discordapp.Discord/discord-ipc-0", paths[10]);
        Assert.Equal("/run/user/7/snap.discord/discord-ipc-0", paths[20]);
        Assert.Contains("/tmp/discord-ipc-0", paths);
    }

    [Fact]
    public async Task TheActivityReachesTheDiscordApp()
    {
        if (OperatingSystem.IsWindows()) return; // the app's pipe can't be stood in for here
        var folder = Directory.CreateTempSubdirectory("presence");
        try
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(Path.Combine(folder.FullName, "discord-ipc-0")));
            listener.Listen();
            using var presence = new DiscordPresence("555", environment: name => name == "XDG_RUNTIME_DIR" ? folder.FullName : null);
            presence.Set(new DiscordActivity("Game", "Turn 1"));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var app = await listener.AcceptAsync(timeout.Token);
            await using var stream = new NetworkStream(app);
            var (op, hello) = await Read(stream, timeout.Token);
            Assert.Equal(0, op);
            Assert.Equal("555", (string?)hello["client_id"]);
            await stream.WriteAsync(DiscordPresence.Encode(1, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = "READY" }), timeout.Token);

            var (_, first) = await Read(stream, timeout.Token);
            Assert.Equal("Turn 1", (string?)first["args"]!["activity"]!["state"]);
            await stream.WriteAsync(DiscordPresence.Encode(1, new JsonObject { ["cmd"] = "SET_ACTIVITY" }), timeout.Token);

            presence.Set(new DiscordActivity("Game", "Turn 1")); // the same activity again: nothing sent
            presence.Set(new DiscordActivity("Game", "Turn 2"));
            var (_, second) = await Read(stream, timeout.Token);
            Assert.Equal("Turn 2", (string?)second["args"]!["activity"]!["state"]);
            listener.Dispose();
        }
        finally { folder.Delete(recursive: true); }
    }

    private static async Task<(int Op, JsonNode Body)> Read(Stream stream, CancellationToken cancel)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancel);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await stream.ReadExactlyAsync(body, cancel);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonNode.Parse(body)!);
    }
}
