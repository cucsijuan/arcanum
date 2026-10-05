// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Cards;

/// <summary>
/// Original, generic cards used by tests and the offline demo. Real card content is never part of this
/// repository: it comes from a card module downloaded at runtime.
/// </summary>
public static class GenericCards
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
    public static readonly CardDefinition BarrenFlats = Basic("Barren Flats", "", ManaType.Colorless) with { Subtypes = Array.Empty<string>() };

    public static readonly CardDefinition PlainsLion = Vanilla("Plains Lion", "{W}", "Cat", 2, 1);
    public static readonly CardDefinition GladeCub = Vanilla("Glade Cub", "{1}{G}", "Bear", 2, 2);
    public static readonly CardDefinition OgreBrute = Vanilla("Ogre Brute", "{2}{R}", "Ogre", 2, 2);
    public static readonly CardDefinition HillBrute = Vanilla("Hill Brute", "{3}{R}", "Giant", 3, 3);
    public static readonly CardDefinition GreatWurm = Vanilla("Great Wurm", "{4}{G}{G}", "Wurm", 6, 4);
    public static readonly CardDefinition StoneElemental = Vanilla("Stone Elemental", "{3}{R}{R}", "Elemental", 4, 5);
    public static readonly CardDefinition RiverScout = Vanilla("River Scout", "{U}", "Merfolk", 1, 1);

    /// <summary>Instant dealing 3 damage to any target.</summary>
    public static readonly CardDefinition EmberBolt = new()
    {
        Name = "Ember Bolt",
        Types = CardType.Instant,
        ManaCost = ManaCost.Parse("{R}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) } },
        OracleText = "Ember Bolt deals 3 damage to any target.",
    };

    /// <summary>Creature with a tap ability dealing 1 damage to any target.</summary>
    public static readonly CardDefinition SparkMage = new()
    {
        Name = "Spark Mage",
        ManaCost = ManaCost.Parse("{2}{R}"),
        Types = CardType.Creature,
        Subtypes = new[] { "Wizard" },
        Power = 1,
        Toughness = 1,
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility { Cost = AbilityCost.TapOnly, Text = "{T}: Spark Mage deals 1 damage to any target.", Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) } },
        },
        OracleText = "{T}: Spark Mage deals 1 damage to any target.",
    };

    /// <summary>Aura: enchanted creature gets +2/+2.</summary>
    public static readonly CardDefinition StoneSkin = new()
    {
        Name = "Stone Skin",
        ManaCost = ManaCost.Parse("{1}{G}"),
        Types = CardType.Enchantment,
        Subtypes = new[] { "Aura" },
        EnchantTarget = new TargetSpec(TargetKind.Creature),
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Enchanted), 2, 2) },
        OracleText = "Enchant creature\nEnchanted creature gets +2/+2.",
    };

    /// <summary>Equipment: equipped creature gets +2/+0; equip {1}.</summary>
    public static readonly CardDefinition IronBlade = new()
    {
        Name = "Iron Blade",
        ManaCost = ManaCost.Parse("{1}"),
        Types = CardType.Artifact,
        Subtypes = new[] { "Equipment" },
        Abilities = new AbilityDefinition[]
        {
            new StaticAbility(new AffectedFilter(AffectedScope.Equipped), 2, 0),
            new ActivatedAbility
            {
                Cost = new AbilityCost(ManaCost.Parse("{1}")),
                SorcerySpeed = true,
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
                Effects = new Effect[] { new AttachSelf(Subject.TargetAt(0)) },
                Text = "Equip {1}",
                IsEquip = true,
            },
        },
        OracleText = "Equipped creature gets +2/+0.\nEquip {1}",
    };

    /// <summary>Enchantment: creatures you control get +1/+1.</summary>
    public static readonly CardDefinition RallyBanner = new()
    {
        Name = "Rally Banner",
        ManaCost = ManaCost.Parse("{2}{W}"),
        Types = CardType.Enchantment,
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures), 1, 1) },
        OracleText = "Creatures you control get +1/+1.",
    };

    /// <summary>Instant: target creature gets +3/+3 until end of turn.</summary>
    public static readonly CardDefinition MightySurge = new()
    {
        Name = "Mighty Surge",
        ManaCost = ManaCost.Parse("{G}"),
        Types = CardType.Instant,
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0)) } },
        OracleText = "Target creature gets +3/+3 until end of turn.",
    };

    /// <summary>Sorcery: destroy target creature.</summary>
    public static readonly CardDefinition Rend = new()
    {
        Name = "Rend",
        ManaCost = ManaCost.Parse("{2}{B}"),
        Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        OracleText = "Destroy target creature.",
    };

    /// <summary>Instant: return target creature to its owner's hand.</summary>
    public static readonly CardDefinition GustAway = new()
    {
        Name = "Gust Away",
        ManaCost = ManaCost.Parse("{U}"),
        Types = CardType.Instant,
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new ReturnToHand(Subject.TargetAt(0)) } },
        OracleText = "Return target creature to its owner's hand.",
    };

    public static IReadOnlyList<CardDefinition> All { get; } = new[]
    {
        Plains, Island, Swamp, Mountain, Forest, BarrenFlats,
        PlainsLion, GladeCub, OgreBrute, HillBrute, GreatWurm, StoneElemental, RiverScout,
        EmberBolt, SparkMage, StoneSkin, IronBlade, RallyBanner, MightySurge, Rend, GustAway,
    };
}

public sealed class InMemoryCardDatabase : ICardDatabase
{
    private readonly Dictionary<string, CardDefinition> _byName;

    public InMemoryCardDatabase(IEnumerable<CardDefinition> cards)
    {
        _byName = cards.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static InMemoryCardDatabase Core { get; } = new(GenericCards.All);

    public bool TryGet(string name, out CardDefinition definition) => _byName.TryGetValue(name, out definition!);
}
