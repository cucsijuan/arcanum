// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.CardData;

/// <summary>A token a card can create, as listed by the card source (with the token's own printing id).</summary>
public sealed record RelatedToken(string Name, string Id, string TypeLine = "");

/// <summary>
/// One printing of a card in a set: where it was printed and which picture it has. Rules text is the same in every
/// printing; only the set, number, rarity and art change.
/// </summary>
/// <param name="Set">Set code, lower case ("abc").</param>
/// <param name="Id">The printing's identifier in the card source (used for its exact image).</param>
/// <param name="Booster">Whether this printing can be found in the set's boosters.</param>
/// <param name="Tokens">Tokens of this printing (the ones printed alongside it), for their exact images.</param>
public sealed record Printing(
    string Set, string SetName, string CollectorNumber, string Rarity, string Id, string Released, string SetType, bool Booster,
    IReadOnlyList<RelatedToken> Tokens);

/// <summary>A set as seen through its printings.</summary>
public sealed record SetInfo(string Code, string Name, string Released, string Type, int CardCount);

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

    /// <summary>The card source's default printing (its image is shown when no printing is chosen).</summary>
    public string? DefaultPrintingId { get; init; }

    /// <summary>Every paper printing of the card, oldest first (empty when the module has no printings source).</summary>
    public IReadOnlyList<Printing> Printings { get; init; } = Array.Empty<Printing>();

    /// <summary>The printing with this set code and collector number, if the card has one.</summary>
    public Printing? FindPrinting(string set, string? collectorNumber = null) =>
        Printings.FirstOrDefault(p => p.Set.Equals(set, StringComparison.OrdinalIgnoreCase)
                                      && (collectorNumber is null || p.CollectorNumber.Equals(collectorNumber, StringComparison.OrdinalIgnoreCase)));
}
