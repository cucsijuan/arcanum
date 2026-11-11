// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

// The order of continuous effects inside one layer or sublayer (rules 613.7 and 613.8): timestamps, except that an effect
// waits for the effects it depends on.
public sealed partial class Game
{
    /// <summary>
    /// What a continuous effect changes, or looks at to know whether it exists, what it applies to and what it does. Only used to
    /// skip the dependency check between two effects that can't touch each other: every doubt counts as <see cref="All"/>.
    /// </summary>
    [Flags]
    private enum Aspect
    {
        None = 0,
        /// <summary>Copiable values (layer 1): everything printed on the object.</summary>
        Copy = 1 << 0,
        Control = 1 << 1,
        Name = 1 << 2,
        /// <summary>Card types and subtypes.</summary>
        Types = 1 << 3,
        /// <summary>The abilities of a land's rules text, lost as an effect sets its land type (rule 305.7).</summary>
        TextAbilities = 1 << 4,
        Colors = 1 << 5,
        /// <summary>Which ability objects it has: all lost, or some gained.</summary>
        Abilities = 1 << 6,
        Keywords = 1 << 7,
        /// <summary>Having every creature type through an ability (seen by "is a [subtype]").</summary>
        Changeling = 1 << 8,
        BasePowerToughness = 1 << 9,
        PowerToughness = 1 << 10,
        /// <summary>Anything else an effect of the layer writes (seen only by effects that may look at anything).</summary>
        Other = 1 << 11,
        All = ~0,
    }

    /// <summary>Identity of an effect within one recomputation: its ability object (by reference) on its source, or its place in a list.</summary>
    private readonly struct EffectKey : IEquatable<EffectKey>
    {
        private readonly int _kind, _index;
        private readonly CardId _card;
        private readonly object? _ability;

        public EffectKey(int kind, CardId card, int index, object? ability = null) => (_kind, _card, _index, _ability) = (kind, card, index, ability);

        public bool Equals(EffectKey other) => _kind == other._kind && _card == other._card && _index == other._index && ReferenceEquals(_ability, other._ability);
        public override bool Equals(object? obj) => obj is EffectKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_kind, _card, _index, _ability is null ? 0 : RuntimeHelpers.GetHashCode(_ability));
    }

    private const int StaticEffectKind = 0, LastingEffectKind = 1, ImpendingEffectKind = 2, HoneEffectKind = 3, DefinedEffectKind = 4, ControlEffectKind = 5;

    /// <summary>One continuous effect, as it applies in one layer or sublayer.</summary>
    private sealed class LayerEffect
    {
        public required EffectKey Key { get; init; }
        public required long Timestamp { get; init; }

        /// <summary>The object whose static ability generates it; none for the effect of a resolved spell or ability.</summary>
        public Card? Source { get; init; }
        public Aspect Writes { get; init; }

        /// <summary>What it looks at on any object.</summary>
        public Aspect Reads { get; init; }

        /// <summary>What it looks at on its own source only (whether the source still has the ability, who controls it).</summary>
        public Aspect ReadsOfSource { get; init; }

        /// <summary>Applying it may bring new effects of this layer into existence (it grants static abilities).</summary>
        public bool Spawns { get; init; }

        /// <summary>Whether it exists now: its source has the ability and the ability's condition holds.</summary>
        public Func<bool> Exists { get; init; } = static () => true;

        /// <summary>The objects it applies to now.</summary>
        public required Func<IReadOnlyList<Card>> Targets { get; init; }

        /// <summary>
        /// The part of what it does to those objects that the game state decides (a value worked out, a player, a set of abilities),
        /// as something comparable; null when it always does the same.
        /// </summary>
        public Func<IReadOnlyList<Card>, object?>? Does { get; init; }
        public required Action<IReadOnlyList<Card>> Apply { get; init; }
    }

    /// <summary>The permanents and ability sources of one recomputation.</summary>
    private sealed record LayerContext(List<Card> Battlefield, List<Card> Sources, List<Card> GraveyardSources);

    /// <summary>A static ability on its source; <paramref name="Occurrence"/> tells apart the same ability object held twice.</summary>
    private readonly record struct StaticSource(Card Source, StaticAbility Ability, int Occurrence);

    private static EffectKey StaticKey(StaticSource candidate) =>
        new(StaticEffectKind, candidate.Source.Id, candidate.Ability.FromGraveyard ? -1 - candidate.Occurrence : candidate.Occurrence, candidate.Ability);

    /// <summary>
    /// The static abilities whose effect has started to apply in an earlier layer of one recomputation, and the objects it applies
    /// to: it keeps applying to those in every later layer, even if the ability is lost on the way (rule 613.6).
    /// </summary>
    private sealed class EffectLocks
    {
        public readonly Dictionary<EffectKey, IReadOnlyList<Card>> Targets = new();
        public readonly Dictionary<EffectKey, StaticSource> Sources = new();
    }

    /// <summary>True while an effect is applied only to see what it would change (rule 613.8a): nothing outside the layer's own state may be kept.</summary>
    private bool _layerTrial;

    /// <summary>
    /// Applies the effects of one layer or sublayer. An effect that depends on another waits until that one has been applied
    /// (rule 613.8b); effects that are ready apply in timestamp order (rule 613.7), as do the effects of a dependency loop; the
    /// order is worked out again after each effect (rule 613.8c), since applying one can create or end dependencies, make an
    /// effect exist or stop it existing.
    /// </summary>
    /// <param name="collect">The effects that may apply in the layer, whether they exist yet or not, in tie-break order.</param>
    /// <param name="save">Captures what the layer has written on an object so far.</param>
    /// <param name="restore">Puts that back.</param>
    private void ApplyLayer(Func<List<LayerEffect>> collect, Func<Card, object?> save, Action<Card, object?> restore)
    {
        var pool = collect();
        if (pool.Count == 0) return;
        if (pool.Count == 1 || CantInteract(pool))
        {
            // No effect changes anything another looks at: plain timestamp order.
            foreach (var effect in pool.OrderBy(e => e.Timestamp))
                if (effect.Exists()) effect.Apply(effect.Targets());
            return;
        }
        var applied = new HashSet<EffectKey>();
        while (true)
        {
            var waiting = pool.Where(e => !applied.Contains(e.Key) && e.Exists()).OrderBy(e => e.Timestamp).ToList();
            if (waiting.Count == 0) return;
            var next = waiting.Count == 1 ? waiting[0] : NextInDependencyOrder(waiting, save, restore);
            next.Apply(next.Targets());
            applied.Add(next.Key);
            if (next.Spawns) pool = collect();
        }
    }

    private static bool CantInteract(List<LayerEffect> pool)
    {
        Aspect writes = Aspect.None, reads = Aspect.None;
        foreach (var effect in pool)
        {
            if (effect.Spawns) return false;
            writes |= effect.Writes;
            reads |= effect.Reads | effect.ReadsOfSource;
        }
        if ((writes & reads) == 0) return true;
        // Looked at pair by pair: an effect reading what only it writes depends on nothing.
        for (int a = 0; a < pool.Count; a++)
            for (int b = 0; b < pool.Count; b++)
                if (a != b && (pool[b].Writes & (pool[a].Reads | pool[a].ReadsOfSource)) != 0) return false;
        return true;
    }

    /// <summary>What can be seen of an effect from outside: whether it exists, what it applies to and what it does to that.</summary>
    private readonly record struct EffectView(bool Exists, IReadOnlyList<Card>? Targets, object? Does)
    {
        public bool Same(EffectView other) =>
            Exists == other.Exists && (!Exists || (Targets!.SequenceEqual(other.Targets!) && SameDoing(Does, other.Does)));

        private static bool SameDoing(object? a, object? b) =>
            a is int[] x && b is int[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);
    }

    private static EffectView View(LayerEffect effect)
    {
        if (!effect.Exists()) return new EffectView(false, null, null);
        var targets = effect.Targets();
        return new EffectView(true, targets, effect.Does?.Invoke(targets));
    }

    /// <summary>
    /// The next effect to apply among those waiting (in timestamp order, two or more). Each effect is applied on trial, alone, on
    /// the state reached so far; an effect whose existence, affected objects or result that changes depends on it (rule 613.8a).
    /// All the effects here are on the same side of the characteristic-defining test of rule 613.8a: the sublayer for
    /// characteristic-defining abilities holds only those, and no other layer run holds any.
    /// </summary>
    private LayerEffect NextInDependencyOrder(List<LayerEffect> waiting, Func<Card, object?> save, Action<Card, object?> restore)
    {
        int n = waiting.Count;
        var targets = new IReadOnlyList<Card>?[n];
        var before = new EffectView?[n];
        bool[,]? dependsOn = null;
        var readers = new List<int>();
        for (int b = 0; b < n; b++)
        {
            var other = waiting[b];
            if (other.Writes == Aspect.None) continue;
            readers.Clear();
            for (int a = 0; a < n; a++)
            {
                if (a == b) continue;
                var effect = waiting[a];
                if ((other.Writes & effect.Reads) != 0
                    || ((other.Writes & effect.ReadsOfSource) != 0 && effect.Source is { } source && (targets[b] ??= other.Targets()).Contains(source)))
                    readers.Add(a);
            }
            if (readers.Count == 0) continue;
            foreach (int a in readers) before[a] ??= View(waiting[a]);
            var touched = targets[b] ??= other.Targets();
            var saved = new object?[touched.Count];
            for (int i = 0; i < touched.Count; i++) saved[i] = save(touched[i]);
            bool wasTrial = _layerTrial;
            _layerTrial = true;
            try
            {
                other.Apply(touched);
                foreach (int a in readers)
                    if (!View(waiting[a]).Same(before[a]!.Value)) (dependsOn ??= new bool[n, n])[a, b] = true;
            }
            finally
            {
                for (int i = 0; i < touched.Count; i++) restore(touched[i], saved[i]);
                _layerTrial = wasTrial;
            }
        }
        if (dependsOn is null) return waiting[0];

        // Effects that depend on each other, directly or through others, form a loop.
        var reaches = (bool[,])dependsOn.Clone();
        for (int k = 0; k < n; k++)
            for (int i = 0; i < n; i++)
                if (reaches[i, k])
                    for (int j = 0; j < n; j++)
                        if (reaches[k, j]) reaches[i, j] = true;
        bool SameLoop(int a, int b) => a == b || (reaches[a, b] && reaches[b, a]);
        // An effect is ready when nothing it waits for is left outside its own loop, and the same goes for the rest of the loop.
        for (int a = 0; a < n; a++)
        {
            bool ready = true;
            for (int member = 0; member < n && ready; member++)
            {
                if (!SameLoop(a, member)) continue;
                for (int b = 0; b < n && ready; b++)
                    if (dependsOn[member, b] && !SameLoop(a, b)) ready = false;
            }
            if (ready) return waiting[a];
        }
        return waiting[0];
    }

    // ------------------------------------------------------------------ static abilities as effects

    /// <summary>
    /// The static abilities that may apply in a layer: each one its source has now, oldest source first, whether or not its
    /// condition holds yet (it may come to hold as the layer is applied). With <paramref name="locks"/>, also those whose effect
    /// started to apply in an earlier layer and whose source has lost them since (rule 613.6).
    /// </summary>
    private static List<StaticSource> StaticCandidates(LayerContext context, Func<StaticAbility, bool> relevant, EffectLocks? locks = null)
    {
        var found = StaticCandidatesNow(context, relevant);
        if (locks is null || locks.Sources.Count == 0) return found;
        var present = found.Select(StaticKey).ToHashSet();
        foreach (var (key, candidate) in locks.Sources)
            if (relevant(candidate.Ability) && !present.Contains(key)) found.Add(candidate);
        return found;
    }

    private static List<StaticSource> StaticCandidatesNow(LayerContext context, Func<StaticAbility, bool> relevant)
    {
        var found = new List<StaticSource>();
        foreach (var source in context.Sources.OrderBy(c => c.Timestamp))
        {
            var abilities = source.Abilities;
            for (int i = 0; i < abilities.Count; i++)
            {
                if (abilities[i] is not StaticAbility { FromGraveyard: false } ability || !relevant(ability)) continue;
                int occurrence = 0;
                for (int j = 0; j < i; j++)
                    if (ReferenceEquals(abilities[j], ability)) occurrence++;
                found.Add(new StaticSource(source, ability, occurrence));
            }
        }
        foreach (var source in context.GraveyardSources)
        {
            var abilities = source.Definition.Abilities;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i] is StaticAbility { FromGraveyard: true } ability && relevant(ability)) found.Add(new StaticSource(source, ability, i));
        }
        return found;
    }

    /// <summary>Whether the source has the ability now and the ability's condition holds (an ability that was lost generates no effect).</summary>
    private bool StaticExists(StaticSource candidate)
    {
        var (source, ability, occurrence) = candidate;
        if (ability.FromGraveyard) return ability.While is not { } whileThere || Holds(whileThere, source.Owner, source);
        int seen = 0;
        bool has = false;
        foreach (var held in source.Abilities)
            if (ReferenceEquals(held, ability) && seen++ == occurrence) { has = true; break; }
        return has && (ability.While is not { } condition || Holds(condition, source.Controller, source));
    }

    private IEnumerable<Card> AffectedBy(LayerContext context, Card source, StaticAbility ability) =>
        Affected(source, ability.Affects, context.Battlefield).Where(a =>
            ability.Filter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, a, a.Controller, source, source.Controller));

    /// <summary>
    /// A static ability's effect in one layer. With <paramref name="locked"/>, it keeps applying to the objects it first applied
    /// to in an earlier layer, even if the ability has since been lost (rule 613.6).
    /// </summary>
    private LayerEffect StaticEffect(LayerContext context, StaticSource candidate, Aspect writes, Action<Card> apply,
        Func<IReadOnlyList<Card>, object?>? does = null, Aspect reads = Aspect.None, Aspect readsOfSource = Aspect.None, bool spawns = false,
        EffectLocks? locked = null, Action? onApply = null)
    {
        var (source, ability, _) = candidate;
        var key = StaticKey(candidate);
        return new LayerEffect
        {
            Key = key,
            Timestamp = source.Timestamp,
            Source = source,
            Writes = writes,
            Reads = reads | (ability.While is not null ? Aspect.All : ScopeReads(ability.Affects) | FilterReads(ability.Filter)),
            ReadsOfSource = readsOfSource | (ability.FromGraveyard ? Aspect.None : Aspect.Abilities | Aspect.TextAbilities | Aspect.Copy),
            Spawns = spawns,
            Exists = () => (locked is not null && locked.Targets.ContainsKey(key)) || StaticExists(candidate),
            Targets = () => locked is not null && locked.Targets.TryGetValue(key, out var kept) ? kept : AffectedBy(context, source, ability).ToList(),
            Does = does,
            Apply = targets =>
            {
                if (locked is not null && !_layerTrial && locked.Targets.TryAdd(key, targets)) locked.Sources[key] = candidate;
                onApply?.Invoke();
                foreach (var card in targets) apply(card);
            },
        };
    }

    /// <summary>What deciding the objects in a scope looks at.</summary>
    private static Aspect ScopeReads(AffectedFilter affects) =>
        (affects.Scope switch
        {
            AffectedScope.YourCreatures or AffectedScope.OpponentsCreatures => Aspect.Types | Aspect.Control,
            AffectedScope.AllCreatures => Aspect.Types,
            AffectedScope.YourPermanents or AffectedScope.OpponentsPermanents => Aspect.Control,
            AffectedScope.Self or AffectedScope.Enchanted or AffectedScope.Equipped or AffectedScope.AllPermanents => Aspect.None,
            _ => Aspect.All,
        }) | (affects.Subtype is null ? Aspect.None : SubtypeReads);

    /// <summary>"Is a [subtype]" looks at types, and at the ability that gives every creature type.</summary>
    private const Aspect SubtypeReads = Aspect.Types | Aspect.Changeling | Aspect.TextAbilities | Aspect.Copy;

    private static readonly ObjectFilter NoRequirement = new();

    /// <summary>What matching a filter looks at: worked out for the requirements listed here, everything for any other.</summary>
    private static Aspect FilterReads(ObjectFilter? filter)
    {
        if (filter is null) return Aspect.None;
        var others = filter with
        {
            Types = 0, AllTypes = 0, ExcludedTypes = 0, Subtype = null, ExcludedSubtype = null, Supertype = 0, ExcludedSupertype = 0,
            Controller = NoRequirement.Controller, Other = false, Token = null, Colors = null, NotColors = null, Keyword = null, WithoutKeyword = null,
            MinPower = null, MaxPower = null, MinToughness = null, MaxToughness = null, Tapped = null, Attacking = null, Blocking = null, InCombat = null,
        };
        if (!others.Equals(NoRequirement)) return Aspect.All;
        var reads = Aspect.Types | Aspect.Control | Aspect.Copy;
        if (filter.Subtype is not null || filter.ExcludedSubtype is not null) reads |= SubtypeReads;
        if (filter.Colors is not null || filter.NotColors is not null) reads |= Aspect.Colors;
        if (filter.Keyword is not null || filter.WithoutKeyword is not null) reads |= Aspect.Keywords | Aspect.Changeling | Aspect.TextAbilities;
        if (filter.MinPower is not null || filter.MaxPower is not null || filter.MinToughness is not null || filter.MaxToughness is not null)
            reads |= Aspect.BasePowerToughness | Aspect.PowerToughness;
        return reads;
    }

    /// <summary>What working out a quantity looks at: worked out for the kinds listed here, everything for any other.</summary>
    private static Aspect QuantityReads(Quantity? quantity)
    {
        if (quantity is null) return Aspect.None;
        if (quantity.Parts is { } parts)
            return quantity.Kind == QuantityKind.Sum ? parts.Aggregate(Aspect.None, (reads, part) => reads | QuantityReads(part)) : Aspect.All;
        return quantity.Kind switch
        {
            QuantityKind.Fixed or QuantityKind.CountersOnSource or QuantityKind.SourceCounters or QuantityKind.HandSize or QuantityKind.YourLife => Aspect.None,
            QuantityKind.PermanentCount => Aspect.Control | FilterReads(quantity.Filter ?? ObjectFilter.YourCreatures),
            _ => Aspect.All,
        };
    }
}
