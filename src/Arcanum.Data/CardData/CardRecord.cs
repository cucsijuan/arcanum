// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.CardData;

/// <summary>A token a card can create, as listed by the card source (with the token's own printing id).</summary>
public sealed record RelatedToken(string Name, string Id, string TypeLine = "");

/// <summary>One face of a card with several (split, transforming, adventure...).</summary>
public sealed record CardFaceRecord(string Name, string ManaCost, string TypeLine, string OracleText, string? Power, string? Toughness);

/// <summary>
/// Raw card data as imported from a module's card source, before it becomes an engine
/// <see cref="Engine.Cards.CardDefinition"/>. Stored locally in a compact form so startup doesn't re-import.
/// </summary>
public sealed record CardRecord
{
    public required string OracleId { get; init; }
    public required string Name { get; init; }
    public required string Layout { get; init; }
    public string ManaCost { get; init; } = "";
    public string TypeLine { get; init; } = "";
    public string OracleText { get; init; } = "";
    public string? Power { get; init; }
    public string? Toughness { get; init; }
    public string? Loyalty { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ColorIdentity { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProducedMana { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Legalities { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<CardFaceRecord> Faces { get; init; } = Array.Empty<CardFaceRecord>();
    public bool IsToken { get; init; }

    /// <summary>Tokens this card creates, for picking the exact token image.</summary>
    public IReadOnlyList<RelatedToken> RelatedTokens { get; init; } = Array.Empty<RelatedToken>();
}
