// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Reflection;

namespace Arcanum.Engine.Core;

/// <summary>What a copy does with one field of an object.</summary>
public enum FieldRule
{
    /// <summary>Copied deeply (the default).</summary>
    Copy,
    /// <summary>The copy refers to the same object as the original.</summary>
    Keep,
    /// <summary>Left empty (null or zero) in the copy.</summary>
    Clear,
}

/// <summary>
/// Copies an object graph field by field, by each object's runtime type: objects reached twice (or in a cycle) are copied
/// once, collections keep their internal layout (so they enumerate the same way afterwards, whatever is added or removed
/// later), and immutable types are shared instead of copied (card definitions, abilities, effects, mana costs, events,
/// views, strings, enums). It refuses what it can't copy safely (<see cref="NotSupportedException"/>): a delegate, a
/// system object other than a collection, or a hash collection keyed by an object that is copied (its hash would change).
/// Types with a generated copier (code written ahead of time, see Game.Copiers.g.cs) are copied by it, without reflection,
/// which is slow on devices whose code is compiled ahead of time; the others field by field through reflection.
/// </summary>
public sealed class DeepCopier
{
    private readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly Func<FieldInfo, FieldRule>? _rules;
    private readonly IReadOnlyDictionary<Type, Func<object, DeepCopier, object>>? _generated;

    /// <param name="rules">Exceptions to copying, by field (wiring such as controllers or events).</param>
    /// <param name="generated">Copiers written ahead of time, by type; each makes the copy and remembers it (<see cref="Shallow"/>).</param>
    public DeepCopier(Func<FieldInfo, FieldRule>? rules = null, IReadOnlyDictionary<Type, Func<object, DeepCopier, object>>? generated = null)
    {
        _rules = rules;
        _generated = generated;
    }

    /// <summary>Objects copied through reflection, by type, across all copiers given generated copiers (none is expected).</summary>
    public static readonly ConcurrentDictionary<Type, int> CopiedByReflection = new();

    /// <summary>A deep copy of <paramref name="value"/>.</summary>
    public T Copy<T>(T value) => (T)CopyObject(value)!;

    /// <summary>A field-for-field copy of the object (its fields still refer to what the original's do), remembered as its copy.</summary>
    public object Shallow(object original)
    {
        var copy = Clone(original);
        _copies[original] = copy;
        return copy;
    }

    /// <summary>Remembers <paramref name="copy"/> as the copy of <paramref name="original"/>.</summary>
    public void Remember(object original, object copy) => _copies[original] = copy;

    /// <summary>A copy of a list, with the same capacity, its elements copied by <paramref name="element"/> (kept when null).</summary>
    public List<T> CopyList<T>(List<T> list, Func<T, DeepCopier, T>? element)
    {
        var copy = new List<T>(list.Capacity);
        _copies[list] = copy;
        FillList(list, copy, element);
        return copy;
    }

    /// <summary>Adds the elements of <paramref name="from"/> to <paramref name="to"/>, copied by <paramref name="element"/> (kept when null).</summary>
    public void FillList<T>(List<T> from, List<T> to, Func<T, DeepCopier, T>? element)
    {
        if (element is null) to.AddRange(from);
        else foreach (var x in from) to.Add(element(x, this));
    }

    /// <summary>A copy of an array, its elements copied by <paramref name="element"/> (kept when null).</summary>
    public T[] CopyArray<T>(T[] array, Func<T, DeepCopier, T>? element)
    {
        var copy = (T[])array.Clone();
        _copies[array] = copy;
        if (element is not null)
            for (int i = 0; i < copy.Length; i++) copy[i] = element(copy[i], this);
        return copy;
    }

    /// <summary>
    /// A copy of a hash set whose elements are kept: its entries are laid out as the original's (free slots included), so
    /// it enumerates the same way whatever is added or removed later.
    /// </summary>
    public HashSet<T> CopyHashSet<T>(HashSet<T> set)
    {
        var copy = (HashSet<T>)Clone(set);
        copy.EnsureCapacity(copy.EnsureCapacity(0) + 1); // its own storage: entries copied in place, buckets made anew
        _copies[set] = copy;
        return copy;
    }

    /// <summary>
    /// A copy of a dictionary whose keys are kept and whose values are copied by <paramref name="value"/> (kept when null),
    /// laid out as the original (see <see cref="CopyHashSet{T}"/>).
    /// </summary>
    public Dictionary<TKey, TValue> CopyDictionary<TKey, TValue>(Dictionary<TKey, TValue> dictionary, Func<TValue, DeepCopier, TValue>? value)
        where TKey : notnull
    {
        var copy = (Dictionary<TKey, TValue>)Clone(dictionary);
        copy.EnsureCapacity(copy.EnsureCapacity(0) + 1);
        DictionaryViews<TKey, TValue>.Forget(copy); // the original's key and value views would show the original
        _copies[dictionary] = copy;
        if (value is not null)
            foreach (var (k, v) in dictionary)
                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(copy, k) = value(v, this);
        return copy;
    }

    private static class DictionaryViews<TKey, TValue> where TKey : notnull
    {
        private static readonly FieldInfo? Keys = typeof(Dictionary<TKey, TValue>).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? Values = typeof(Dictionary<TKey, TValue>).GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Forget(Dictionary<TKey, TValue> copy)
        {
            if (Keys?.GetValue(copy) is not null) Keys.SetValue(copy, null);
            if (Values?.GetValue(copy) is not null) Values.SetValue(copy, null);
        }
    }

    /// <summary>A copy of a queue, its elements copied by <paramref name="element"/> (kept when null).</summary>
    public Queue<T> CopyQueue<T>(Queue<T> queue, Func<T, DeepCopier, T>? element)
    {
        var copy = new Queue<T>(queue.Count);
        _copies[queue] = copy;
        foreach (var x in queue) copy.Enqueue(element is null ? x : element(x, this));
        return copy;
    }

    private static readonly Func<object, object> Clone = (Func<object, object>)Delegate.CreateDelegate(
        typeof(Func<object, object>), typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!);

    private object? CopyObject(object? value)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (IsShared(type)) return value;
        if (_copies.TryGetValue(value, out var done)) return done;
        if (_generated is not null)
        {
            if (_generated.TryGetValue(type, out var generated)) return generated(value, this);
            CopiedByReflection.AddOrUpdate(type, 1, (_, n) => n + 1);
        }
        if (type.IsArray)
        {
            var array = (Array)value;
            var copy = (Array)array.Clone();
            _copies[value] = copy;
            var element = type.GetElementType()!;
            if (element.IsValueType ? NeedsFixing(element) : !IsShared(element) || element == typeof(object))
                for (int i = 0; i < array.Length; i++) copy.SetValue(CopyElement(array.GetValue(i)), i);
            return copy;
        }
        var plan = PlanFor(type);
        var clone = Clone(value);
        _copies[value] = clone;
        foreach (var (field, rule) in plan.Fields)
        {
            var original = field.GetValue(value);
            object? replaced = rule switch
            {
                FieldRule.Clear => null,
                FieldRule.Keep => original,
                _ => field.FieldType.IsValueType ? CopyElement(original) : CopyObject(original),
            };
            if (rule == FieldRule.Clear && field.FieldType.IsValueType) replaced = Activator.CreateInstance(field.FieldType);
            field.SetValue(clone, replaced);
        }
        return clone;
    }

    /// <summary>A copy of an array element or a field value: a struct whose fields hold copied objects is fixed field by field.</summary>
    private object? CopyElement(object? value)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (!type.IsValueType) return CopyObject(value);
        if (!NeedsFixing(type)) return value;
        var boxed = Clone(value); // a fresh box: setting its fields leaves the original alone
        foreach (var (field, _) in PlanFor(type).Fields) field.SetValue(boxed, CopyElement(field.GetValue(boxed)));
        return boxed;
    }

    private sealed record Plan(IReadOnlyList<(FieldInfo Field, FieldRule Rule)> Fields);

    private static readonly ConcurrentDictionary<Type, Plan> Plans = new();
    private static readonly ConcurrentDictionary<Type, bool> Shared = new();
    private static readonly ConcurrentDictionary<Type, bool> Fixing = new();

    /// <summary>The fields of a type (its base types' too) that a copy must look at: those that can hold copied objects.</summary>
    private Plan PlanFor(Type type)
    {
        if (_rules is null && Plans.TryGetValue(type, out var cached)) return cached;
        if (type.IsSubclassOf(typeof(Delegate))) throw new NotSupportedException($"Can't copy a delegate ({type}).");
        if (type.Namespace is { } ns && ns.StartsWith("System", StringComparison.Ordinal) && !IsSystemCollection(type))
            throw new NotSupportedException($"Can't copy a {type}.");
        CheckHashKeys(type);
        var fields = new List<(FieldInfo, FieldRule)>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var rule = _rules?.Invoke(field) ?? FieldRule.Copy;
                if (rule == FieldRule.Copy && (field.FieldType.IsValueType ? !NeedsFixing(field.FieldType) : IsShared(field.FieldType) && field.FieldType != typeof(object)))
                    continue; // copied with the object itself
                fields.Add((field, rule));
            }
        var plan = new Plan(fields);
        if (_rules is null) Plans[type] = plan;
        return plan;
    }

    private static bool IsSystemCollection(Type type) =>
        type.Namespace is "System.Collections.Generic" or "System.Collections.ObjectModel"
        || (type.IsValueType && type.Namespace == "System" && (type.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || type.Name.StartsWith("Nullable", StringComparison.Ordinal)));

    /// <summary>A hash set or dictionary keyed by an object that would be copied can't be copied as it is laid out.</summary>
    private static void CheckHashKeys(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType) continue;
            var def = t.GetGenericTypeDefinition();
            if (def != typeof(Dictionary<,>) && def != typeof(HashSet<>)) continue;
            var key = t.GetGenericArguments()[0];
            if (key.IsValueType ? NeedsFixing(key) : !IsShared(key))
                throw new NotSupportedException($"Can't copy a {t}: its keys would be copied.");
        }
    }

    /// <summary>Whether objects of the type are never changed, so a copy can share them.</summary>
    public static bool IsShared(Type type) => Shared.GetOrAdd(type, static t =>
    {
        if (t.IsArray) return false; // an array can be written to, whatever its elements (a list's storage among them)
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t.IsPointer) return true;
        if (typeof(Type).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)) return true;
        if (t == typeof(object) || t.IsInterface) return false; // decided by the object's own type
        // Comparers (default, reference, ordinal) hold no state.
        if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && t.Name.Contains("Comparer", StringComparison.Ordinal)) return true;
        var ns = t.Namespace ?? "";
        if (t == typeof(Mana.ManaPool)) return false;
        return ns.StartsWith("Arcanum.Engine.Cards", StringComparison.Ordinal) || ns.StartsWith("Arcanum.Engine.Abilities", StringComparison.Ordinal)
               || ns.StartsWith("Arcanum.Engine.Mana", StringComparison.Ordinal) || ns.StartsWith("Arcanum.Engine.Events", StringComparison.Ordinal)
               || ns.StartsWith("Arcanum.Engine.Views", StringComparison.Ordinal);
    });

    /// <summary>Whether a struct holds (directly or in its own structs) objects that are copied.</summary>
    public static bool NeedsFixing(Type type) => Fixing.GetOrAdd(type, static t =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => f.FieldType.IsValueType ? f.FieldType != t && NeedsFixing(f.FieldType) : !IsShared(f.FieldType)));
}
