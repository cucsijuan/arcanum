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
    /// <param name="exclude">A permanent that can't be tapped for mana here (it is tapping for an ability's cost).</param>
    public static IEnumerable<Card> AvailableSources(GameState state, PlayerId player, CardId? exclude = null) =>
        state.PermanentsControlledBy(player)
            .Where(c => !c.Tapped && c.ManaTypes.Count > 0 && c.ManaAmount > 0 && c.Id != exclude)
            // Creatures can't use {T} abilities while summoning sick (rule 302.6).
            .Where(c => !c.IsSummoningSick);

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

    public static PaymentPlan? FindPlan(GameState state, PlayerId player, ManaCost cost, CardId? exclude = null)
    {
        var (fromPool, rest) = ApplyPool(cost, state.GetPlayer(player).ManaPool);
        var all = AvailableSources(state, player, exclude).ToList();
        // Sources that add several mana per activation are decided first (skip, or each of their types); the
        // single-mana sources then pay what is left with the fast pip solver below.
        var multi = all.Where(c => c.ManaAmount > 1).ToList();
        var single = all.Where(c => c.ManaAmount == 1)
            .OrderBy(c => c.Definition.SacrificeForMana) // keep one-shot sources (Treasure) for last
            .ThenBy(c => c.ManaTypes.Count)
            .ThenBy(c => c.Id.Value)
            .ToList();
        return SolveMulti(multi, 0, rest, new List<ManaTap>(), single) is { } taps ? new PaymentPlan(fromPool, taps) : null;
    }

    private static List<ManaTap>? SolveMulti(List<Card> multi, int index, ManaCost remaining, List<ManaTap> chosen, List<Card> single)
    {
        if (remaining.ManaValue == 0) return chosen.ToList();
        if (index == multi.Count)
        {
            var taps = SolveSingle(single, remaining);
            return taps is null ? null : chosen.Concat(taps).ToList();
        }
        // Prefer not to use a big source when the rest can pay (saves it for later), then try each of its types.
        if (SolveMulti(multi, index + 1, remaining, chosen, single) is { } without) return without;
        var source = multi[index];
        foreach (var type in source.ManaTypes.Distinct())
        {
            var (left, _) = Apply(remaining, Enumerable.Repeat(type, source.ManaAmount));
            chosen.Add(new ManaTap(source.Id, type));
            var result = SolveMulti(multi, index + 1, left, chosen, single);
            chosen.RemoveAt(chosen.Count - 1);
            if (result is not null) return result;
        }
        return null;
    }

    private static List<ManaTap>? SolveSingle(List<Card> sources, ManaCost rest)
    {
        var used = new bool[sources.Count];
        var taps = new List<ManaTap>();
        if (!AssignPips(rest.Pips, 0, sources, used, taps)) return null;

        int generic = rest.Generic;
        for (int i = 0; i < sources.Count && generic > 0; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, sources[i].ManaTypes[0]));
            generic--;
        }
        return generic > 0 ? null : taps;
    }

    /// <summary>Every mana a set of taps adds (sources that add several mana count each one).</summary>
    public static IEnumerable<ManaType> Produced(GameState state, IEnumerable<ManaTap> taps) =>
        taps.SelectMany(t => Enumerable.Repeat(t.Type, state.GetCard(t.Source).ManaAmount));

    private static bool AssignPips(IReadOnlyList<ManaType> pips, int index, List<Card> sources, bool[] used, List<ManaTap> taps)
    {
        if (index == pips.Count) return true;
        var pip = pips[index];
        for (int i = 0; i < sources.Count; i++)
        {
            if (used[i] || !sources[i].ManaTypes.Contains(pip)) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, pip));
            if (AssignPips(pips, index + 1, sources, used, taps)) return true;
            taps.RemoveAt(taps.Count - 1);
            used[i] = false;
        }
        return false;
    }
}
