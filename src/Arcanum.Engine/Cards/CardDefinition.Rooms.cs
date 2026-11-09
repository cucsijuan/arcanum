// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Reflection;

namespace Arcanum.Engine.Cards;

/// <summary>
/// Rooms (rule 709.5): split permanent cards with one shared type line. Each half is a door; on the battlefield a Room has the name,
/// mana cost and rules text of its unlocked doors only (709.5c), so a Room with both doors locked has no name, no mana cost and no rules text.
/// </summary>
public sealed partial record CardDefinition
{
    /// <summary>A split card whose halves are both permanents (rule 709.5): a Room.</summary>
    public bool IsRoom => SplitHalves is { Count: 2 } halves && halves.All(h => h.Types.IsPermanent());

    /// <summary>Each Room's definitions by unlocked doors, kept per definition object (not in a field: "with" would copy it, and records compare fields).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardDefinition, CardDefinition?[]> Rooms = new();

    /// <summary>
    /// This Room as a permanent with the doors in <paramref name="unlocked"/> unlocked (bit 0: the left door, bit 1: the right one): the
    /// card's own types and printing, with the name, mana cost and rules of those doors.
    /// </summary>
    public CardDefinition AsRoom(int unlocked)
    {
        var rooms = Rooms.GetValue(this, _ => new CardDefinition?[4]);
        lock (rooms) return rooms[unlocked & 3] ??= CombineDoors(SplitHalves!.Where((_, i) => (unlocked & (1 << i)) != 0).ToList());
    }

    private static readonly PropertyInfo[] Settable = typeof(CardDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();

    /// <summary>What a door gives: everything but the card's identity, which the whole card has (its types, colour identity, printing).</summary>
    private static readonly HashSet<string> DoorRules = new() { nameof(Name), nameof(ManaCost), nameof(OracleText), nameof(Keywords) };

    private CardDefinition CombineDoors(IReadOnlyList<CardDefinition> doors)
    {
        var room = new CardDefinition
        {
            Name = string.Join(" // ", doors.Select(d => d.Name)),
            ManaCost = doors.Aggregate(Mana.ManaCost.Zero, (cost, d) => cost.Plus(d.ManaCost)),
            OracleText = string.Join("\n//\n", doors.Select(d => d.OracleText)),
            Keywords = doors.SelectMany(d => d.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        foreach (var p in Settable)
        {
            if (DoorRules.Contains(p.Name)) continue;
            var own = p.GetValue(this);
            // The card's identity (types, oracle id, picture, foil, the halves themselves) is the whole card's.
            object? value = !IsDefault(own) ? own : Merge(p.PropertyType, doors.Select(d => p.GetValue(d)).ToList());
            if (value is not null) p.SetValue(room, value);
        }
        return room;
    }

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        bool b => !b,
        string s => s.Length == 0,
        ICollection c => c.Count == 0,
        IEnumerable e => !e.Cast<object>().Any(),
        Enum en => Convert.ToInt64(en) == 0,
        int i => i == 0,
        long l => l == 0,
        _ => false,
    };

    /// <summary>Both unlocked doors' rules: flags either has, lists of both, and otherwise the first door's value that says something.</summary>
    private static object? Merge(Type type, IReadOnlyList<object?> values)
    {
        var said = values.Where(v => !IsDefault(v)).ToList();
        if (said.Count == 0) return values.FirstOrDefault();
        if (said.Count == 1) return said[0];
        if (type == typeof(bool)) return true;
        if (type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() is not null)
            return Enum.ToObject(type, said.Aggregate(0L, (all, v) => all | Convert.ToInt64(v)));
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var v in said.OfType<IEnumerable>()) foreach (var item in v) list.Add(item);
            return list;
        }
        return said[0];
    }
}
