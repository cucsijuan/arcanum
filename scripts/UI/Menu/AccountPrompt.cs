// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>After signing in with Discord: offers the Discord name as the player's name (when it's another one).</summary>
public static class AccountPrompt
{
    /// <param name="used">After the Discord name became the player's.</param>
    public static void OfferName(Control screen, string? discordName, Action? used = null)
    {
        if (discordName is not { Length: > 0 } name || !GodotObject.IsInstanceValid(screen)) return;
        if (name.Length > 32) name = name[..32];
        if (name == Settings.PlayerName) return;
        MenuKit.Modal(screen, "Your name", $"Use your Discord name, {name}, as your name in Arcanum? You're {Settings.PlayerName} now; you can change it any time in Settings.", null,
            ($"Keep {Settings.PlayerName}", () => { }),
            ($"Use {name}", () =>
            {
                Settings.Current.PlayerNames[0] = name;
                Settings.Current.NameChosen = true;
                Settings.Save();
                used?.Invoke();
            }));
    }
}
