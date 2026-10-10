// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Soft sounds of a running-out clock (synthesized, like <see cref="TurnChime"/>): a gentle two-note call when little
/// time is left, then a quiet tick each of the last seconds. Volume follows the effects setting.
/// </summary>
public partial class ClockSound : AudioStreamPlayer
{
    private static AudioStreamWav? _warning, _tick;

    public ClockSound()
    {
        Bus = "Master";
        _warning ??= TurnChime.Render(0.5, (0.0, 659.25, 0.3, 0.7), (0.16, 523.25, 0.34, 0.8)); // E5 then C5, falling
        _tick ??= TurnChime.Render(0.16, (0.0, 880.0, 0.13, 0.45));
    }

    public void Warn() => PlaySound(_warning!);

    public void Tick() => PlaySound(_tick!);

    private void PlaySound(AudioStream stream)
    {
        double effects = Math.Clamp(Settings.Current.EffectsVolume, 0, 1);
        if (effects <= 0) return;
        Stream = stream;
        VolumeDb = Mathf.LinearToDb((float)effects);
        Play();
    }
}
