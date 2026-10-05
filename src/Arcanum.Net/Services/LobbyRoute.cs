// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Net.Services;

/// <summary>
/// How a joined lobby was reached through the online services (its listing, including the provider), saved as text so a
/// player can get back into the game after closing the app: the address of such a lobby means nothing on its own.
/// </summary>
public static class LobbyRoute
{
    public static string Save(LobbyListing listing) => JsonSerializer.Serialize(listing);

    /// <summary>Reads a saved route; false for empty or unreadable text.</summary>
    public static bool TryLoad(string? text, out LobbyListing listing)
    {
        listing = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { listing = JsonSerializer.Deserialize<LobbyListing>(text)!; }
        catch (JsonException) { return false; }
        return listing is not null && !string.IsNullOrEmpty(listing.LobbyId);
    }
}
