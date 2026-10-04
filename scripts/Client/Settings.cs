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
    /// <summary>Address of the last online game joined.</summary>
    public string LastHostAddress { get; set; } = "";
    /// <summary>Seat token of the online game in progress (to get back in after this device closed); empty when none.</summary>
    public string LastSeatToken { get; set; } = "";
    /// <summary>That token is an event's (draft, sealed), not a single game's.</summary>
    public bool LastSeatIsEvent { get; set; }
    public bool ConfirmManaPayment { get; set; } = true;
    public bool RevealHandsInHotseat { get; set; } = true;

    // Gameplay
    /// <summary>Animation speed multiplier: 0.5 = slower, 2 = faster.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public string[] OwnTurnStops { get; set; } = { nameof(Step.PrecombatMain), nameof(Step.PostcombatMain) };
    public string[] OpponentTurnStops { get; set; } = Array.Empty<string>();
    public bool FullControl { get; set; }

    // Appearance
    public string[] Playmats { get; set; } = { "grid", "grid" };
    public string CardBack { get; set; } = "arcane";

    // Audio (0..1)
    public double MasterVolume { get; set; } = 0.8;
    public double MusicVolume { get; set; } = 0.6;
    public double EffectsVolume { get; set; } = 0.8;

    // Video
    public bool Fullscreen { get; set; }
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
        DisplayServer.WindowSetMode(s.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetVsyncMode(s.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        if (Godot.Engine.GetMainLoop() is SceneTree tree) tree.Root.ContentScaleFactor = (float)Math.Clamp(s.UiScale, 0.6, 2.0);
        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb((float)Math.Clamp(s.MasterVolume, 0, 1)));
        UI.Board.BoardStyle.AnimationScale = (float)(1.0 / Math.Clamp(s.AnimationSpeed, 0.25, 4));
        UI.Board.BoardStyle.CardBack = s.CardBack;
    }

    public static IEnumerable<Step> ParseSteps(IEnumerable<string> names) =>
        names.Select(n => Enum.TryParse<Step>(n, out var step) ? (Step?)step : null).OfType<Step>();
}
