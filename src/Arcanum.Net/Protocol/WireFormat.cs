// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;

namespace Arcanum.Net.Protocol;

/// <summary>
/// JSON encoding of <see cref="NetMessage"/>s. On the host, each connection has its own format with that player's
/// <see cref="CardAliases"/>: real card ids never leave the host. Players use a format without aliases.
/// </summary>
public sealed class WireFormat
{
    /// <summary>Bumped whenever messages change in a way older versions can't read.</summary>
    public const int ProtocolVersion = 1;

    private readonly JsonSerializerOptions _options;

    public WireFormat(CardAliases? aliases = null)
    {
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipComputed, EngineHierarchies } },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new CardIdConverter(aliases), new PlayerIdConverter(), new ManaCostConverter() },
        };
    }

    public string Write(NetMessage message) => JsonSerializer.Serialize(message, _options);

    /// <exception cref="JsonException">Malformed message, or a card id this player was never given.</exception>
    public NetMessage Read(string json) =>
        JsonSerializer.Deserialize<NetMessage>(json, _options) ?? throw new JsonException("Empty message.");

    public JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, _options);

    public T? FromElement<T>(JsonElement element) => element.Deserialize<T>(_options);

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _options);

    /// <summary>Leaves out properties computed from others (no setter), and delegates, which can't be sent.</summary>
    private static void SkipComputed(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        for (int i = info.Properties.Count - 1; i >= 0; i--)
        {
            var p = info.Properties[i];
            bool ctorParam = info.Type.GetConstructors().Any(c =>
                c.GetParameters().Any(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
            if ((p.Set is null && !ctorParam) || typeof(Delegate).IsAssignableFrom(p.PropertyType)) info.Properties.RemoveAt(i);
            else p.IsRequired = false; // nulls aren't written, so a "required" null value arrives missing
        }
    }

    private static readonly Assembly EngineAssembly = typeof(GameEvent).Assembly;

    /// <summary>Abstract engine types (events, actions) are sent with their concrete type's name.</summary>
    private static void EngineHierarchies(JsonTypeInfo info)
    {
        var type = info.Type;
        if (!type.IsAbstract || type.Assembly != EngineAssembly || info.Kind != JsonTypeInfoKind.Object) return;
        var options = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$t" };
        foreach (var derived in EngineAssembly.GetTypes().Where(t => !t.IsAbstract && type.IsAssignableFrom(t)))
            options.DerivedTypes.Add(new JsonDerivedType(derived, derived.Name));
        info.PolymorphismOptions = options;
    }

    private sealed class CardIdConverter(CardAliases? aliases) : JsonConverter<CardId>
    {
        private int Out(CardId id) => aliases?.ToAlias(id.Value) ?? id.Value;

        private CardId In(int value)
        {
            if (aliases is null) return new CardId(value);
            return aliases.TryToReal(value, out int real) ? new CardId(real) : throw new JsonException($"Unknown card {value}.");
        }

        public override CardId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => In(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, CardId value, JsonSerializerOptions options) => writer.WriteNumberValue(Out(value));

        public override CardId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            In(int.Parse(reader.GetString()!));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, CardId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(Out(value).ToString());
    }

    private sealed class PlayerIdConverter : JsonConverter<PlayerId>
    {
        public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);

        public override PlayerId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(int.Parse(reader.GetString()!));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Value.ToString());
    }

    private sealed class ManaCostConverter : JsonConverter<ManaCost>
    {
        public override ManaCost Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ManaCost.Parse(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, ManaCost value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
