// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Rules;

/// <summary>Which pool mana and which sources (tapped for which type) pay a cost.</summary>
public sealed record PaymentPlan(IReadOnlyList<ManaType> FromPool, IReadOnlyList<ManaTap> Taps);

/// <summary>
/// Mana payment helpers. Floating mana is always spent first; the auto-pay solver then pays specific pips
/// with the least flexible sources and generic with whatever is left (auto-tap).
/// </summary>
public static class ManaPayment
{
    public static IEnumerable<Card> AvailableSources(GameState state, PlayerId player) =>
        state.PermanentsControlledBy(player)
            .Where(c => !c.Tapped && c.Definition.TapForMana.Count > 0)
            // Creatures can't use {T} abilities while summoning sick (rule 302.6).
            .Where(c => !c.IsCreature || c.ControlledSinceTurnStart);

    /// <summary>Spends floating mana on <paramref name="cost"/>: specific pips first, then generic.</summary>
    public static (List<ManaType> FromPool, ManaCost Remaining) ApplyPool(ManaCost cost, ManaPool pool)
    {
        var available = pool.Clone();
        var fromPool = new List<ManaType>();
        var remainingPips = new List<ManaType>();
        foreach (var pip in cost.Pips)
        {
            if (available[pip] > 0) { available.Remove(pip); fromPool.Add(pip); }
            else remainingPips.Add(pip);
        }
        int generic = cost.Generic;
        foreach (var type in Enum.GetValues<ManaType>())
        {
            while (generic > 0 && available[type] > 0) { available.Remove(type); fromPool.Add(type); generic--; }
        }
        return (fromPool, new ManaCost(generic, remainingPips));
    }

    /// <summary>
    /// What is still owed after paying <paramref name="cost"/> with <paramref name="paid"/> mana, and how many of
    /// those mana were surplus (could not be applied to anything).
    /// </summary>
    public static (ManaCost Remaining, int Excess) Apply(ManaCost cost, IEnumerable<ManaType> paid)
    {
        var pips = cost.Pips.ToList();
        var leftovers = new List<ManaType>();
        foreach (var type in paid)
        {
            int index = pips.IndexOf(type);
            if (index >= 0) pips.RemoveAt(index);
            else leftovers.Add(type);
        }
        int generic = cost.Generic;
        int excess = 0;
        foreach (var _ in leftovers)
        {
            if (generic > 0) generic--;
            else excess++;
        }
        return (new ManaCost(generic, pips), excess);
    }

    public static PaymentPlan? FindPlan(GameState state, PlayerId player, ManaCost cost)
    {
        var (fromPool, rest) = ApplyPool(cost, state.GetPlayer(player).ManaPool);

        var sources = AvailableSources(state, player)
            .OrderBy(c => c.Definition.TapForMana.Count)
            .ThenBy(c => c.Id.Value)
            .ToList();
        var used = new bool[sources.Count];
        var taps = new List<ManaTap>();
        if (!AssignPips(rest.Pips, 0, sources, used, taps)) return null;

        int generic = rest.Generic;
        for (int i = 0; i < sources.Count && generic > 0; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, sources[i].Definition.TapForMana[0]));
            generic--;
        }
        return generic > 0 ? null : new PaymentPlan(fromPool, taps);
    }

    private static bool AssignPips(IReadOnlyList<ManaType> pips, int index, List<Card> sources, bool[] used, List<ManaTap> taps)
    {
        if (index == pips.Count) return true;
        var pip = pips[index];
        for (int i = 0; i < sources.Count; i++)
        {
            if (used[i] || !sources[i].Definition.TapForMana.Contains(pip)) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, pip));
            if (AssignPips(pips, index + 1, sources, used, taps)) return true;
            taps.RemoveAt(taps.Count - 1);
            used[i] = false;
        }
        return false;
    }
}
