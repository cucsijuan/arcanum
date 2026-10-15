// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Arcanum.Engine.State;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Arcanum.Client;

/// <summary>User preferences, saved to user://settings.json.</summary>
public sealed class SettingsData
{
    // General
    public string[] PlayerNames { get; set; } = { "Player 1", "Player 2" };

    /// <summary>The player chose their name (asked once, at the first start; changed in the settings).</summary>
    public bool NameChosen { get; set; }

    /// <summary>The seat (token) of an unfinished online game the player chose not to get back into: not offered again.</summary>
    public string RejoinDeclined { get; set; } = "";

    /// <summary>The mode last chosen on the Play screen: a format id, "draft" or "sealed".</summary>
    public string PlayMode { get; set; } = "casual";
    /// <summary>Address of the last online game joined.</summary>
    public string LastHostAddress { get; set; } = "";
    /// <summary>Seat token of the online game in progress (to get back in after this device closed); empty when none.</summary>
    public string LastSeatToken { get; set; } = "";
    /// <summary>How the game in progress was reached when joined through the online services (a saved lobby route); empty for an address.</summary>
    public string LastLobbyRoute { get; set; } = "";
    /// <summary>That token is an event's (draft, sealed), not a single game's.</summary>
    public bool LastSeatIsEvent { get; set; }
    /// <summary>Online games this device hosts: seconds a player has for each decision before the computer makes it (0: no limit).</summary>
    public int DecisionSeconds { get; set; }
    public bool ConfirmManaPayment { get; set; } = true;
    public bool RevealHandsInHotseat { get; set; } = true;
    /// <summary>Show on the player's Discord profile what they are doing in the game (when the Discord app runs).</summary>
    public bool DiscordActivity { get; set; } = true;

    // Gameplay
    /// <summary>Animation speed multiplier: 0.5 = slower, 2 = faster.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public string[] OwnTurnStops { get; set; } = { nameof(Step.PrecombatMain), nameof(Step.PostcombatMain) };
    public string[] OpponentTurnStops { get; set; } = Array.Empty<string>();
    public bool FullControl { get; set; }

    // Updates
    /// <summary>Look for a newer game or content module release when the game starts.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A game version the player chose to skip (not offered again; a later one is).</summary>
    public string? SkippedGameVersion { get; set; }
    /// <summary>A content module version the player chose to skip.</summary>
    public string? SkippedModuleVersion { get; set; }

    // Appearance
    public string[] Playmats { get; set; } = { "grid", "grid" };
    public string CardBack { get; set; } = "arcane";
    /// <summary>How boosters open in draft: "tear" (swipe across the tear line yourself), "auto" or "skip" (no animation).</summary>
    public string BoosterOpening { get; set; } = "tear";
    /// <summary>How strongly foil cards shine under the pointer (0 = not at all, 1 = full).</summary>
    public double FoilShine { get; set; } = 0.45;

    // Audio (0..1)
    public double MasterVolume { get; set; } = 0.8;
    public double MusicVolume { get; set; } = 0.6;
    public double EffectsVolume { get; set; } = 0.8;

    // Video
    public bool Fullscreen { get; set; }
    /// <summary>The window's size when not fullscreen, "1600x900"; empty keeps the size it opens with.</summary>
    public string WindowSize { get; set; } = "";
    public bool VSync { get; set; } = true;
    public double UiScale { get; set; } = 1.0;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsData))]
internal partial class SettingsJsonContext : JsonSerializerContext;

public static class Settings
{
    private const string Path = "user://settings.json";

    public static SettingsData Current { get; private set; } = new();

    /// <summary>This player's name: in online games, and for the first seat on this device.</summary>
    public static string PlayerName => Current.PlayerNames.ElementAtOrDefault(0) is { Length: > 0 } name ? name : "Player 1";

    public static event Action? Changed;

    public static void Load()
    {
        if (!FileAccess.FileExists(Path)) return;
        try
        {
            Current = JsonSerializer.Deserialize(FileAccess.GetFileAsString(Path), SettingsJsonContext.Default.SettingsData) ?? new SettingsData();
        }
        catch (JsonException e)
        {
            GD.PushWarning($"Settings file unreadable, using defaults: {e.Message}");
            Current = new SettingsData();
        }
    }

    public static void Save()
    {
        using var file = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        file?.StoreString(JsonSerializer.Serialize(Current, SettingsJsonContext.Default.SettingsData));
        Apply();
        Changed?.Invoke();
    }

    /// <summary>Applies window, audio and scale settings to the running app.</summary>
    public static void Apply()
    {
        var s = Current;
        bool modeChanged = _appliedFullscreen != s.Fullscreen;
        _appliedFullscreen = s.Fullscreen;
        if (modeChanged) DisplayServer.WindowSetMode(s.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        // The size is set only when it (or the mode) changed, so saving other settings keeps a window the player resized by hand.
        if (!s.Fullscreen && (modeChanged || _appliedSize != s.WindowSize) && ParseSize(s.WindowSize) is { } size && DisplayServer.GetName() != "headless")
        {
            if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            int screen = DisplayServer.WindowGetCurrentScreen();
            var usable = DisplayServer.ScreenGetUsableRect(screen);
            size = new Vector2I(Math.Min(size.X, usable.Size.X), Math.Min(size.Y, usable.Size.Y));
            DisplayServer.WindowSetSize(size);
            DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
        }
        _appliedSize = s.WindowSize;
        DisplayServer.WindowSetVsyncMode(s.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        if (Godot.Engine.GetMainLoop() is SceneTree tree) tree.Root.ContentScaleFactor = (float)Math.Clamp(s.UiScale, 0.6, 2.0);
        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb((float)Math.Clamp(s.MasterVolume, 0, 1)));
        UI.Board.BoardStyle.AnimationScale = (float)(1.0 / Math.Clamp(s.AnimationSpeed, 0.25, 4));
        UI.Board.BoardStyle.CardBack = s.CardBack;
        UI.Board.BoardStyle.FoilShine = (float)Math.Clamp(s.FoilShine, 0, 1);
    }

    private static bool? _appliedFullscreen;
    private static string? _appliedSize;

    /// <summary>Window sizes offered in the settings (16:9 and 16:10), as "WIDTHxHEIGHT".</summary>
    public static readonly string[] WindowSizes =
        { "1280x720", "1280x800", "1366x768", "1440x900", "1600x900", "1680x1050", "1920x1080", "1920x1200", "2560x1440", "2560x1600", "3840x2160" };

    public static Vector2I? ParseSize(string text) =>
        text.Split('x') is [var w, var h] && int.TryParse(w, out int width) && int.TryParse(h, out int height) && width >= 640 && height >= 360
            ? new Vector2I(width, height) : null;

    public static IEnumerable<Step> ParseSteps(IEnumerable<string> names) =>
        names.Select(n => Enum.TryParse<Step>(n, out var step) ? (Step?)step : null).OfType<Step>();
}
