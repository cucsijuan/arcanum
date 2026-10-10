// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// A short, soft two-note chime played when the local player's turn begins. The sound is synthesized at start-up
/// (no audio asset): two low-mid sine-based notes a major third apart with a soft attack and a smooth decay, and
/// nothing above a couple of kilohertz so it stays gentle. Volume follows the effects setting (master volume is
/// applied by the audio bus).
/// </summary>
public partial class TurnChime : AudioStreamPlayer
{
    private const int MixRate = 44100;
    private static AudioStreamWav? _stream;

    public TurnChime()
    {
        Bus = "Master";
        Stream = _stream ??= Synthesize();
    }

    public void Chime()
    {
        double effects = Math.Clamp(Settings.Current.EffectsVolume, 0, 1);
        if (effects <= 0) return;
        VolumeDb = Mathf.LinearToDb((float)effects);
        Play();
    }

    /// <summary>F4 then A4, about 0.43 s in all.</summary>
    private static AudioStreamWav Synthesize() => Render(0.43, (0.0, 349.23, 0.28, 0.8), (0.12, 440.0, 0.31, 1.0));

    /// <summary>Soft bell-like notes (start, frequency, duration, gain) mixed into a mono 16-bit sound <paramref name="length"/> seconds long.</summary>
    internal static AudioStreamWav Render(double length, params (double Start, double Frequency, double Duration, double Gain)[] notes)
    {
        var samples = new double[(int)(length * MixRate)];
        foreach (var note in notes) AddNote(samples, note.Start, note.Frequency, note.Duration, note.Gain);
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            double fadeOut = Math.Min(1.0, (samples.Length - 1 - i) / (0.03 * MixRate)); // end on silence, no click
            short value = (short)Math.Round(Math.Clamp(samples[i] * 0.34 * fadeOut, -1, 1) * short.MaxValue);
            data[i * 2] = (byte)(value & 0xff);
            data[i * 2 + 1] = (byte)((value >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = MixRate, Stereo = false, Data = data };
    }

    /// <summary>A soft bell-like note: fundamental plus quiet 2nd and 3rd harmonics, 18 ms attack, exponential decay.</summary>
    private static void AddNote(double[] samples, double start, double frequency, double duration, double gain)
    {
        int first = (int)(start * MixRate), count = (int)(duration * MixRate);
        for (int n = 0; n < count && first + n < samples.Length; n++)
        {
            double t = n / (double)MixRate;
            double attack = t < 0.018 ? 0.5 - 0.5 * Math.Cos(Math.PI * t / 0.018) : 1;
            double decay = Math.Exp(-t * 8.5);
            double phase = 2 * Math.PI * frequency * t;
            double tone = Math.Sin(phase) + 0.22 * Math.Sin(2 * phase) * Math.Exp(-t * 6) + 0.06 * Math.Sin(3 * phase) * Math.Exp(-t * 10);
            samples[first + n] += tone * attack * decay * gain;
        }
    }
}
