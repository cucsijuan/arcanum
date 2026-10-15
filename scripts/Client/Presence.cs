// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;

namespace Arcanum.Client;

/// <summary>
/// What the player's Discord profile shows they are doing (when the Discord app runs on this computer and the setting is on):
/// the screen they are on, or the game they play. Screens set it as they open; the game board keeps it up to date.
/// </summary>
public static class Presence
{
    /// <summary>The picture shown with the activity: the game's icon.</summary>
    private const string Icon = "https://raw.githubusercontent.com/cucsijuan/arcanum/main/icon.png";

    private static readonly DiscordPresence Discord = new(Account.DiscordClientId, Icon, "Arcanum");
    private static DiscordActivity? _shown;
    private static bool _listening;

    /// <summary>Shows <paramref name="activity"/> (null shows nothing).</summary>
    public static void Show(DiscordActivity? activity)
    {
        if (!_listening)
        {
            _listening = true;
            Settings.Changed += Push;
        }
        _shown = activity;
        Push();
    }

    /// <summary>The activity for a screen that just opened (the game board shows its own).</summary>
    public static void ShowScreen(string scene)
    {
        var app = App.Instance;
        DiscordActivity? activity = scene switch
        {
            App.PlaySetupScene => new("Choosing a game"),
            App.DeckBuilderScene => new("Building a deck"),
            App.LimitedScene => new(app.Limited.Current?.Mode == LimitedMode.Sealed ? "Sealed event" : "Draft"),
            App.OnlineScene => Lobby(app.Online.Lobby?.State),
            App.CareerScene => new("Career", "Around town"),
            App.DeckDeskScene => new("Career", "Building a deck"),
            App.EventScene => new("Career", "At an event"),
            App.GameBoardScene => _shown, // the board says which game once it starts
            _ => new("In the menus"),
        };
        Show(activity);
    }

    private static DiscordActivity Lobby(LobbyState? lobby) =>
        lobby is null
            ? new("Online", "Looking for a game")
            : new("Online", lobby.Commander ? "Commander lobby" : "In a lobby",
                PartySize: lobby.Seats.Count(s => s.Kind != LobbySeatKind.Open), PartyMax: lobby.Seats.Count);

    public static void Stop() => Discord.Dispose();

    private static void Push() => Discord.Set(Settings.Current.DiscordActivity ? _shown : null);
}
