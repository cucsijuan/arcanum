// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

public static class Modes
{
    /// <summary>
    /// The ability narrowed to the chosen modes (indices into <see cref="AbilityDefinition.Modes"/>, ascending): their
    /// targets in order, each mode's effects reading its own targets.
    /// </summary>
    public static T WithModes<T>(this T ability, IReadOnlyList<int> chosen) where T : AbilityDefinition
    {
        var modes = ability.Modes ?? throw new InvalidOperationException("Not a modal ability.");
        var targets = new List<TargetSpec>();
        var effects = new List<Effect>();
        foreach (var i in chosen)
        {
            effects.Add(new ModeEffects(targets.Count, modes[i].Effects));
            targets.AddRange(modes[i].Targets);
        }
        return ability with { Modes = null, Targets = targets, Effects = effects, Text = string.Join(" / ", chosen.Select(i => modes[i].Text)) };
    }

    /// <summary>One mode on its own, as an ability (for evaluating it).</summary>
    public static SpellAbility AsAbility(this Mode mode) => new() { Targets = mode.Targets, Effects = mode.Effects, Text = mode.Text };
}
