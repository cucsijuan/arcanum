// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Net.Services;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Signing in with Discord: the code to type on a phone or computer where the game can't open a browser, and afterwards
/// the Discord name offered as the player's name (when it's another one).
/// </summary>
public static class AccountPrompt
{
    /// <summary>
    /// Shows a device sign-in's code until the sign-in ends (the caller frees the returned dialog); Cancel stops waiting.
    /// </summary>
    public static Control ShowCode(Control screen, DiscordLinkCode code, CancellationTokenSource cancel)
    {
        var page = code.Url.Replace("https://", "").Replace("http://", "");
        var shown = BoardStyle.MakeTitle(code.Code, 56);
        shown.HorizontalAlignment = HorizontalAlignment.Center;
        return MenuKit.Modal(screen, "Sign in with Discord",
            $"On your phone or computer, open {page} and type this code. The game continues by itself once you've signed in " +
            $"(the code lasts {code.Lasts.TotalMinutes:0} minutes).", shown,
            ("Cancel", cancel.Cancel));
    }

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
