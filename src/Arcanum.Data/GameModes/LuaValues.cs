// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Arcanum.Engine.Core;
using MoonSharp.Interpreter;

namespace Arcanum.Data.GameModes;

/// <summary>Turns Lua values into JSON (card script vocabulary) and engine values into Lua values.</summary>
internal static class LuaValues
{
    /// <summary>
    /// A Lua value as JSON: a table with keys 1..n is an array (an empty table too), any other table an object with
    /// string keys.
    /// </summary>
    public static string ToJson(DynValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value, 0);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, DynValue v, int depth)
    {
        if (depth > 64) throw new ScriptRuntimeException("a table is nested too deeply (or contains itself)");
        switch (v.Type)
        {
            case DataType.Nil or DataType.Void:
                w.WriteNullValue();
                break;
            case DataType.Boolean:
                w.WriteBooleanValue(v.Boolean);
                break;
            case DataType.Number:
                if (v.Number == Math.Floor(v.Number) && Math.Abs(v.Number) < 1e15) w.WriteNumberValue((long)v.Number);
                else w.WriteNumberValue(v.Number);
                break;
            case DataType.String:
                w.WriteStringValue(v.String);
                break;
            case DataType.Table:
                var table = v.Table;
                int length = table.Length;
                if (length == table.Pairs.Count())
                {
                    w.WriteStartArray();
                    for (int i = 1; i <= length; i++) Write(w, table.Get(i), depth + 1);
                    w.WriteEndArray();
                }
                else
                {
                    w.WriteStartObject();
                    foreach (var pair in table.Pairs)
                    {
                        if (pair.Key.Type != DataType.String) throw new ScriptRuntimeException("a table mixes a list with named fields");
                        w.WritePropertyName(pair.Key.String);
                        Write(w, pair.Value, depth + 1);
                    }
                    w.WriteEndObject();
                }
                break;
            default:
                throw new ScriptRuntimeException($"a {v.Type.ToLuaTypeString()} can't be part of a card script");
        }
    }

    /// <summary>A name in Lua style: "PrecombatMain" → "precombatMain".</summary>
    public static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Players are numbered from 1 in Lua.</summary>
    public static DynValue Player(PlayerId player) => DynValue.NewNumber(player.Value + 1);

    private static readonly Dictionary<Type, PropertyInfo[]> Properties = new();

    /// <summary>A game event as a table: <c>type</c> is its name, then its fields (players as numbers from 1, cards as ids).</summary>
    public static Table Event(Script script, object e)
    {
        var type = e.GetType();
        if (!Properties.TryGetValue(type, out var properties))
            Properties[type] = properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray();
        var table = new Table(script);
        table["type"] = type.Name;
        foreach (var property in properties) table[Camel(property.Name)] = Value(script, property.GetValue(e), 0);
        return table;
    }

    private static DynValue Value(Script script, object? value, int depth) => value switch
    {
        null => DynValue.Nil,
        PlayerId p => Player(p),
        CardId c => DynValue.NewNumber(c.Value),
        bool b => DynValue.NewBoolean(b),
        string s => DynValue.NewString(s),
        int or long or short or byte or uint or ulong or double or float => DynValue.NewNumber(Convert.ToDouble(value)),
        Enum en => DynValue.NewString(Camel(en.ToString())),
        IEnumerable list when depth < 4 => DynValue.NewTable(List(script, list, depth)),
        _ when depth < 4 && value.GetType().IsValueType is false && value.GetType().Namespace?.StartsWith("Arcanum.Engine") == true
            => DynValue.NewTable(Record(script, value, depth)),
        _ => DynValue.NewString(value.ToString() ?? ""),
    };

    private static Table List(Script script, IEnumerable list, int depth)
    {
        var table = new Table(script);
        foreach (var item in list) table.Append(Value(script, item, depth + 1));
        return table;
    }

    private static Table Record(Script script, object value, int depth)
    {
        var table = new Table(script);
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            table[Camel(property.Name)] = Value(script, property.GetValue(value), depth + 1);
        return table;
    }
}
