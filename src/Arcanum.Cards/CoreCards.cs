// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Cards;

/// <summary>
/// Hand-written definitions for the M1 card pool: basic lands and vanilla creatures.
/// Replaced by the Scryfall import + ability DSL in M4.
/// </summary>
public static class CoreCards
{
    private static CardDefinition Basic(string name, string subtype, ManaType mana) => new()
    {
        Name = name,
        Types = CardType.Land,
        Supertypes = Supertype.Basic,
        Subtypes = new[] { subtype },
        TapForMana = new[] { mana },
        OracleText = $"({{T}}: Add {{{mana.ToSymbol()}}}.)",
    };

    private static CardDefinition Vanilla(string name, string cost, string subtypes, int power, int toughness) => new()
    {
        Name = name,
        ManaCost = ManaCost.Parse(cost),
        Types = CardType.Creature,
        Subtypes = subtypes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        Power = power,
        Toughness = toughness,
    };

    public static readonly CardDefinition Plains = Basic("Plains", "Plains", ManaType.White);
    public static readonly CardDefinition Island = Basic("Island", "Island", ManaType.Blue);
    public static readonly CardDefinition Swamp = Basic("Swamp", "Swamp", ManaType.Black);
    public static readonly CardDefinition Mountain = Basic("Mountain", "Mountain", ManaType.Red);
    public static readonly CardDefinition Forest = Basic("Forest", "Forest", ManaType.Green);
    public static readonly CardDefinition Wastes = Basic("Wastes", "", ManaType.Colorless) with { Subtypes = Array.Empty<string>() };

    public static readonly CardDefinition SavannahLions = Vanilla("Savannah Lions", "{W}", "Cat", 2, 1);
    public static readonly CardDefinition GrizzlyBears = Vanilla("Grizzly Bears", "{1}{G}", "Bear", 2, 2);
    public static readonly CardDefinition GrayOgre = Vanilla("Gray Ogre", "{2}{R}", "Ogre", 2, 2);
    public static readonly CardDefinition HillGiant = Vanilla("Hill Giant", "{3}{R}", "Giant", 3, 3);
    public static readonly CardDefinition CrawWurm = Vanilla("Craw Wurm", "{4}{G}{G}", "Wurm", 6, 4);
    public static readonly CardDefinition EarthElemental = Vanilla("Earth Elemental", "{3}{R}{R}", "Elemental", 4, 5);
    public static readonly CardDefinition MerfolkOfThePearlTrident = Vanilla("Merfolk of the Pearl Trident", "{U}", "Merfolk", 1, 1);

    public static IReadOnlyList<CardDefinition> All { get; } = new[]
    {
        Plains, Island, Swamp, Mountain, Forest, Wastes,
        SavannahLions, GrizzlyBears, GrayOgre, HillGiant, CrawWurm, EarthElemental, MerfolkOfThePearlTrident,
    };
}

public sealed class InMemoryCardDatabase : ICardDatabase
{
    private readonly Dictionary<string, CardDefinition> _byName;

    public InMemoryCardDatabase(IEnumerable<CardDefinition> cards)
    {
        _byName = cards.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static InMemoryCardDatabase Core { get; } = new(CoreCards.All);

    public bool TryGet(string name, out CardDefinition definition) => _byName.TryGetValue(name, out definition!);
}
