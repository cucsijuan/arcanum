// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
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
    /// <summary>Which of a source's mana abilities may pay for what is being paid (restricted mana, rule 106.6).</summary>
    public delegate bool OptionUsable(Card source, ManaOption option);

    /// <summary>Unrestricted mana abilities only (floating mana, unknown use).</summary>
    public static readonly OptionUsable Unrestricted = (_, option) => option.OnlyFor is null;

    /// <summary>Mana abilities usable here that their controller can also afford (life costs, rule 119.4).</summary>
    public static OptionUsable Affordable(GameState state, OptionUsable? usable) =>
        (source, option) => (usable ?? Unrestricted)(source, option) && option.LifeCost <= state.GetPlayer(source.Controller).Life
                            && (option.LifeCost == 0 || state.GetPlayer(source.Controller).CantLoseLifeTurn != state.TurnNumber); // rule 119.8

    /// <summary>Indices of the source's mana abilities usable here.</summary>
    public static List<int> UsableOptions(Card source, OptionUsable? usable) =>
        Enumerable.Range(0, source.ManaOptions.Count).Where(i => (usable ?? Unrestricted)(source, source.ManaOptions[i]) && source.ManaOptions[i].Produces > 0).ToList();

    /// <param name="exclude">A permanent that can't be tapped for mana here (it is tapping for an ability's cost).</param>
    public static IEnumerable<Card> AvailableSources(GameState state, PlayerId player, CardId? exclude = null, OptionUsable? usable = null) =>
        state.PermanentsControlledBy(player)
            .Where(c => !c.Tapped && c.Id != exclude && !c.AbilitiesCantBeActivated && UsableOptions(c, Affordable(state, usable)).Count > 0)
            // Creatures can't use {T} abilities while summoning sick (rule 302.6).
            .Where(c => !c.IsSummoningSick);

    /// <summary>
    /// Spends floating mana on <paramref name="cost"/>: specific pips first, then generic. Restricted mana is used only
    /// where <paramref name="unitUsable"/> allows, and before ordinary mana (it's the less flexible kind).
    /// <paramref name="plainUsed"/> and <paramref name="specialUsed"/> receive what would be spent.
    /// </summary>
    public static (List<ManaType> FromPool, ManaCost Remaining) ApplyPool(ManaCost cost, ManaPool pool, Func<ManaUnit, bool>? unitUsable = null,
        List<ManaType>? plainUsed = null, List<ManaUnit>? specialUsed = null)
    {
        var plain = pool.Clone();
        var specials = pool.Special.Where(u => unitUsable?.Invoke(u) ?? u.OnlyFor is null).ToList();
        var fromPool = new List<ManaType>();
        var remainingPips = new List<ManaType>();
        bool Take(ManaType type)
        {
            int i = specials.FindIndex(u => u.Type == type);
            if (i >= 0) { specialUsed?.Add(specials[i]); specials.RemoveAt(i); fromPool.Add(type); return true; }
            if (plain[type] > 0) { plain.Remove(type); plainUsed?.Add(type); fromPool.Add(type); return true; }
            return false;
        }
        foreach (var pip in cost.Pips)
            if (!Take(pip)) remainingPips.Add(pip);
        int generic = cost.Generic;
        while (generic > 0 && specials.Count > 0) { Take(specials[0].Type); generic--; }
        foreach (var type in Enum.GetValues<ManaType>())
            while (generic > 0 && plain[type] > 0) { Take(type); generic--; }
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

    public static PaymentPlan? FindPlan(GameState state, PlayerId player, ManaCost cost, CardId? exclude = null, OptionUsable? usable = null,
        Func<ManaUnit, bool>? unitUsable = null)
    {
        var (fromPool, rest) = ApplyPool(cost, state.GetPlayer(player).ManaPool, unitUsable);
        usable = Affordable(state, usable);
        var all = AvailableSources(state, player, exclude, usable).ToList();
        // Sources with an ability that adds several mana are decided first (skip, or each ability and type); the
        // others (one mana per activation, from any of their usable abilities) pay what is left with the pip solver.
        var multi = all.Where(c => UsableOptions(c, usable).Any(i => c.ManaOptions[i].Produces > 1)).ToList();
        var single = all.Except(multi)
            .OrderBy(c => c.Definition.SacrificeForMana) // keep one-shot sources (Treasure) for last
            .ThenBy(c => SingleTypes(c, usable).Count)
            .ThenBy(c => c.Id.Value)
            .ToList();
        return SolveMulti(multi, 0, rest, new List<ManaTap>(), single, usable) is { } taps ? new PaymentPlan(fromPool, taps) : null;
    }

    /// <summary>Every mana type a one-mana source can add here, with the ability that adds it.</summary>
    private static List<(ManaType Type, int Option)> SingleTypes(Card source, OptionUsable? usable) =>
        UsableOptions(source, usable).SelectMany(i => source.ManaOptions[i].Types.Select(t => (t, i))).GroupBy(x => x.t).Select(g => g.First()).ToList();

    private static List<ManaTap>? SolveMulti(List<Card> multi, int index, ManaCost remaining, List<ManaTap> chosen, List<Card> single, OptionUsable? usable)
    {
        if (remaining.ManaValue == 0) return chosen.ToList();
        if (index == multi.Count)
        {
            var taps = SolveSingle(single, remaining, usable);
            return taps is null ? null : chosen.Concat(taps).ToList();
        }
        // Prefer not to use a big source when the rest can pay (saves it for later), then try each of its abilities and types.
        if (SolveMulti(multi, index + 1, remaining, chosen, single, usable) is { } without) return without;
        var source = multi[index];
        foreach (var option in UsableOptions(source, usable))
            foreach (var tap in TapsOf(source, option))
            {
                var (left, _) = Apply(remaining, Produced(source, tap));
                chosen.Add(tap);
                var result = SolveMulti(multi, index + 1, left, chosen, single, usable);
                chosen.RemoveAt(chosen.Count - 1);
                if (result is not null) return result;
            }
        return null;
    }

    /// <summary>The different ways one activation of the ability can be made: each type, each combination, or all types at once.</summary>
    private static IEnumerable<ManaTap> TapsOf(Card source, int option)
    {
        var mana = source.ManaOptions[option];
        if (mana.Combination) return mana.Combinations().Select(c => new ManaTap(source.Id, c[0], option, c));
        // "Remove any number of counters: add one mana for each": each number of counters, fewest first.
        if (mana.RemovesCounters is not null)
            return mana.Types.Distinct().SelectMany(t => Enumerable.Range(1, mana.Amount).Select(n => new ManaTap(source.Id, t, option) { Amount = n }));
        return (mana.OneOfEach ? mana.Types.Take(1) : mana.Types.Distinct()).Select(t => new ManaTap(source.Id, t, option));
    }

    private static List<ManaTap>? SolveSingle(List<Card> sources, ManaCost rest, OptionUsable? usable)
    {
        var types = sources.Select(s => SingleTypes(s, usable)).ToList();
        var used = new bool[sources.Count];
        var taps = new List<ManaTap>();
        if (!AssignPips(rest.Pips, 0, sources, types, used, taps)) return null;

        int generic = rest.Generic;
        for (int i = 0; i < sources.Count && generic > 0; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, types[i][0].Type, types[i][0].Option));
            generic--;
        }
        return generic > 0 ? null : taps;
    }

    /// <summary>Every mana a set of taps adds (abilities that add several mana count each one).</summary>
    public static IEnumerable<ManaType> Produced(GameState state, IEnumerable<ManaTap> taps) =>
        taps.SelectMany(t => Produced(state.GetCard(t.Source), t));

    /// <summary>The mana one activation adds: one of each type for "one mana of each color" abilities.</summary>
    public static IEnumerable<ManaType> Produced(Card source, ManaTap tap) =>
        tap.Option < source.ManaOptions.Count && source.ManaOptions[tap.Option].OneOfEach
            ? source.ManaOptions[tap.Option].Types
            : tap.Option < source.ManaOptions.Count && source.ManaOptions[tap.Option].Combination && tap.Combination is { } combination
                ? combination
                : Enumerable.Repeat(tap.Type, AmountOf(source, tap));

    /// <summary>The mana one activation adds: for an ability that removes any number of counters, as many as the tap removes.</summary>
    public static int AmountOf(Card source, ManaTap tap) =>
        tap.Option < source.ManaOptions.Count && source.ManaOptions[tap.Option].RemovesCounters is not null && tap.Amount is { } chosen
            ? Math.Min(chosen, source.ManaOptions[tap.Option].Amount)
            : AmountOf(source, tap.Option);

    /// <summary>Whether the tap's chosen mana is something the ability can add.</summary>
    public static bool IsValid(Card source, ManaTap tap)
    {
        if (tap.Option >= source.ManaOptions.Count) return false;
        var mana = source.ManaOptions[tap.Option];
        if (tap.Amount is { } removed && (mana.RemovesCounters is null || removed < 0 || removed > mana.Amount)) return false;
        if (!mana.Combination) return tap.Combination is null && mana.Types.Contains(tap.Type);
        return tap.Combination is { } c && c.Count == mana.Amount && c.All(mana.Types.Contains);
    }

    public static int AmountOf(Card source, int option) => option < source.ManaOptions.Count ? source.ManaOptions[option].Produces : 1;

    private static bool AssignPips(IReadOnlyList<ManaType> pips, int index, List<Card> sources, List<List<(ManaType Type, int Option)>> types, bool[] used, List<ManaTap> taps)
    {
        if (index == pips.Count) return true;
        var pip = pips[index];
        for (int i = 0; i < sources.Count; i++)
        {
            if (used[i] || types[i].FindIndex(t => t.Type == pip) is var at && at < 0) continue;
            used[i] = true;
            taps.Add(new ManaTap(sources[i].Id, pip, types[i][at].Option));
            if (AssignPips(pips, index + 1, sources, types, used, taps)) return true;
            taps.RemoveAt(taps.Count - 1);
            used[i] = false;
        }
        return false;
    }
}
