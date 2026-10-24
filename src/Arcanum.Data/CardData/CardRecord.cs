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
/// <param name="Foil">Whether this printing was also made in foil (a deck may then ask for foil copies of it).</param>
public sealed record Printing(
    string Set, string SetName, string CollectorNumber, string Rarity, string Id, string Released, string SetType, bool Booster,
    IReadOnlyList<RelatedToken> Tokens, bool Foil = false)
{
    /// <summary>Made only in foil (collector booster treatments, surge foils): every copy of it is a foil.</summary>
    public bool FoilOnly { get; init; }

    /// <summary>Its foils are fracture foils (a foil treatment with a shattered pattern): every foil copy shows it.</summary>
    public bool Fracture { get; init; }

    /// <summary>The artwork's identifier in the card source (the same picture across printings), when it has one.</summary>
    public string? Illustration { get; init; }

    /// <summary>What a copy sold for when the card data was downloaded (null: not listed): Cardmarket in euros, TCGplayer in dollars.</summary>
    public PrintingPrices Prices { get; init; } = PrintingPrices.None;

    /// <summary>Made in etched foil.</summary>
    public bool Etched { get; init; }

    /// <summary>Made in glossy finish.</summary>
    public bool Glossy { get; init; }

    /// <summary>Full-art card: the art covers the text box.</summary>
    public bool FullArt { get; init; }

    /// <summary>Printed without rules text.</summary>
    public bool Textless { get; init; }

    /// <summary>The card source has a high-resolution scan of it.</summary>
    public bool HighRes { get; init; }

    /// <summary>Border color ("black", "white", "borderless", "silver", "gold").</summary>
    public string Border { get; init; } = "";

    /// <summary>Frame edition ("1993", "1997", "2003", "2015", "future").</summary>
    public string Frame { get; init; } = "";

    /// <summary>Frame effects ("showcase", "extendedart", "colorshifted", "inverted", ...).</summary>
    public IReadOnlyList<string> FrameEffects { get; init; } = Array.Empty<string>();

    /// <summary>The name printed on it when it differs from the card's (another language's, a world's own).</summary>
    public string? PrintedName { get; init; }

    /// <summary>The other name it is printed with beside the card's own (a crossover printing).</summary>
    public string? FlavorName { get; init; }

    /// <summary>Promo kinds ("prerelease", "datestamped", "buyabox", "boosterfun", "serialized", ...).</summary>
    public IReadOnlyList<string> PromoTypes { get; init; } = Array.Empty<string>();
}

/// <summary>Market prices of one printing (null: not listed).</summary>
public sealed record PrintingPrices(double? Eur, double? EurFoil, double? Usd, double? UsdFoil)
{
    public static readonly PrintingPrices None = new(null, null, null, null);

    /// <summary>The euro price of a copy: foil or not when known, else the cheaper one listed.</summary>
    public double? EurFor(bool? foil) => foil switch { true => EurFoil, false => Eur, _ => Min(Eur, EurFoil) };

    /// <summary>The dollar price of a copy: foil or not when known, else the cheaper one listed.</summary>
    public double? UsdFor(bool? foil) => foil switch { true => UsdFoil, false => Usd, _ => Min(Usd, UsdFoil) };

    private static double? Min(double? a, double? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;
}

/// <summary>A set as seen through its printings.</summary>
public sealed record SetInfo(string Code, string Name, string Released, string Type, int CardCount);

/// <summary>One face of a card with several (split, transforming, adventure...).</summary>
/// <param name="Colors">The face's colors (from its mana cost, or its color indicator on a back face without one); empty when the source doesn't say.</param>
public sealed record CardFaceRecord(string Name, string ManaCost, string TypeLine, string OracleText, string? Power, string? Toughness)
{
    public string? Loyalty { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
}

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

    /// <summary>On the reserved list (never reprinted).</summary>
    public bool Reserved { get; init; }

    /// <summary>On the Commander format's list of cards that change games the most.</summary>
    public bool GameChanger { get; init; }

    /// <summary>Tokens this card creates, for picking the exact token image.</summary>
    public IReadOnlyList<RelatedToken> RelatedTokens { get; init; } = Array.Empty<RelatedToken>();

    /// <summary>The card source's default printing (its image is shown when no printing is chosen).</summary>
    public string? DefaultPrintingId { get; init; }

    /// <summary>Every paper printing of the card, oldest first (empty when the module has no printings source).</summary>
    public IReadOnlyList<Printing> Printings { get; init; } = Array.Empty<Printing>();

    /// <summary>The printing shown when none is chosen (the card source's default), if it is one of the card's printings.</summary>
    public Printing? DefaultPrinting => Printings.FirstOrDefault(p => p.Id == DefaultPrintingId);

    /// <summary>The printing with this set code and collector number, if the card has one.</summary>
    public Printing? FindPrinting(string set, string? collectorNumber = null) =>
        Printings.FirstOrDefault(p => p.Set.Equals(set, StringComparison.OrdinalIgnoreCase)
                                      && (collectorNumber is null || p.CollectorNumber.Equals(collectorNumber, StringComparison.OrdinalIgnoreCase)));
}
