// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A card object in a game: its definition plus mutable in-game status.</summary>
public sealed class Card
{
    public CardId Id { get; }

    /// <summary>The card as printed (for an adventurer card, the card itself rather than its Adventure).</summary>
    public CardDefinition PrintedDefinition { get; }

    /// <summary>Its characteristics now: those of its Adventure while it is cast as one (rule 715.3), otherwise the printed card's.</summary>
    public CardDefinition Definition => AsAdventure && PrintedDefinition.Adventure is { } adventure ? adventure : PrintedDefinition;

    /// <summary>It is being cast, or is on the stack, as its Adventure (rule 715.3).</summary>
    public bool AsAdventure { get; set; }

    /// <summary>Exiled after resolving as an Adventure: its owner may cast it (not as an Adventure) from exile (rule 715.4).</summary>
    public bool OnAdventure { get; set; }
    public PlayerId Owner { get; }
    public PlayerId Controller { get; set; }

    /// <summary>Who controls it apart from static control-changing effects (rule 613.1b); see <see cref="Controller"/>.</summary>
    public PlayerId BaseController { get; set; }

    /// <summary>Sources that dealt damage to it this turn.</summary>
    public HashSet<CardId> DamagedThisTurnBy { get; } = new();

    /// <summary>Players this creature dealt combat damage to this turn.</summary>
    public HashSet<PlayerId> CombatDamagedPlayers { get; } = new();

    /// <summary>Times it attacked this turn.</summary>
    public int AttacksThisTurn { get; set; }

    /// <summary>Modes of its abilities already chosen ("choose one that hasn't been chosen").</summary>
    public HashSet<int> ChosenModes { get; } = new();

    /// <summary>Activated abilities with "Activate only once" already used.</summary>
    public HashSet<int> ActivatedEver { get; } = new();

    /// <summary>Card name chosen as it entered.</summary>
    public string? ChosenName { get; set; }

    /// <summary>"Odd" or "even", chosen as it entered.</summary>
    public string? ChosenParity { get; set; }

    /// <summary>As a spell: cast from a graveyard (for "if this spell was cast from a graveyard").</summary>
    public bool CastFromGraveyard { get; set; }

    /// <summary>As a spell: the mana spent to cast it.</summary>
    public int ManaSpent { get; set; }

    /// <summary>The spell it was (version) and its mana value with X, as it last existed on the stack.</summary>
    public (int Version, int ManaValue)? LastOnStack { get; set; }

    /// <summary>The X chosen when this permanent was cast (its enters abilities use it, rule 107.3m).</summary>
    public int CastX { get; set; }

    /// <summary>As a spell: mana from a Treasure was spent to cast it.</summary>
    public bool PaidWithTreasure { get; set; }

    /// <summary>As a spell: its caster promised the gift.</summary>
    public bool GiftPromised { get; set; }

    /// <summary>Exiled face down: only its owner may look at it.</summary>
    public bool FaceDown { get; set; }

    /// <summary>As a spell: it can't be countered (mana with that rider was spent on it).</summary>
    public bool Uncounterable { get; set; }

    /// <summary>Turn the modes of its "once each turn" modal ability were last chosen.</summary>
    public int ModesTurn { get; set; } = -1;

    /// <summary>Turn number when it last left the battlefield (for "put there from the battlefield this turn").</summary>
    public int LeftBattlefieldTurn { get; set; } = -1;

    /// <summary>Ward costs granted by other permanents (recomputed with continuous effects).</summary>
    internal List<Mana.ManaCost> GrantedWards { get; } = new();

    /// <summary>Gains haste until end of turn as it enters (mana rider).</summary>
    public bool HasteOnEnter { get; set; }
    public Zone Zone { get; set; }

    public bool Tapped { get; set; }
    public int Damage { get; set; }

    /// <summary>
    /// True once the controller has controlled this permanent continuously since their most recent
    /// turn began. Creatures without it are "summoning sick" (rule 302.6).
    /// </summary>
    public bool ControlledSinceTurnStart { get; set; }

    /// <summary>Dealt damage by a source with deathtouch since the last state-based action check (rule 704.5h).</summary>
    public bool DamagedByDeathtouch { get; set; }

    /// <summary>
    /// Incremented every time the card changes zones: after that it is a new object (rule 400.7), so targets and
    /// effects that referred to the old one no longer apply.
    /// </summary>
    public int Version { get; internal set; }

    public Dictionary<CounterKind, int> Counters { get; } = new();

    /// <summary>This card is its owner's commander (the designation follows it across zones, rule 903.3).</summary>
    public bool IsCommander { get; init; }

    /// <summary>The permanent this Aura or Equipment is attached to (rule 701.3).</summary>
    public CardId? AttachedTo { get; set; }

    // Continuous-effect modifications, recomputed by the engine (layer 7c and keyword grants in layer 6).
    internal int PowerBonus { get; set; }
    internal int ToughnessBonus { get; set; }
    internal HashSet<Keyword> GrantedKeywords { get; } = new();
    internal HashSet<Keyword> LostKeywords { get; } = new();

    /// <summary>Supertypes given by effects ("your Ring-bearer is legendary").</summary>
    internal Supertype GrantedSupertypes { get; set; }

    /// <summary>Players whose creatures can't block it this turn ("can't be blocked by creatures that player controls").</summary>
    internal HashSet<Core.PlayerId> UnblockableBy { get; } = new();

    /// <summary>Its supertypes now.</summary>
    public Supertype Supertypes => Definition.Supertypes | GrantedSupertypes;
    internal HashSet<string> GrantedSubtypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Base power/toughness from a characteristic-defining ability (rule 604.3).</summary>
    internal int? BasePowerOverride { get; set; }
    internal int? BaseToughnessOverride { get; set; }

    // Layers 4–6, recomputed: type, subtype, color and name changes, granted or lost abilities, granted mana.
    internal CardType? TypesOverride { get; set; }
    internal CardType GrantedTypes { get; set; }
    internal IReadOnlyList<string>? SubtypesOverride { get; set; }
    internal IReadOnlyList<string>? ColorsOverride { get; set; }
    internal string? NameOverride { get; set; }
    internal bool LosesAbilities { get; set; }
    internal List<AbilityDefinition> GrantedAbilities { get; } = new();
    internal List<ManaOption> GrantedManaOptions { get; } = new();

    /// <summary>
    /// Its timestamp as a permanent (rule 613.7d–e): given as it enters the battlefield and again when it becomes
    /// attached to something; 0 until the engine assigns one.
    /// </summary>
    public long Timestamp { get; internal set; }

    /// <summary>What the card was like the last time it was on the battlefield (rule 608.2h, last known information).</summary>
    public LastKnown? LastKnownInfo { get; internal set; }

    /// <summary>The permanent (id, version) that exiled this card "with it" (for "the exiled card").</summary>
    public (CardId Source, int Version)? ExiledWith { get; set; }

    /// <summary>Turn number when it last changed zones.</summary>
    public int ZoneChangedTurn { get; set; }

    /// <summary>Was attacking when it last left the battlefield.</summary>
    public bool WasAttacking { get; set; }

    /// <summary>It was blocking when it last left the battlefield.</summary>
    public bool WasBlocking { get; set; }

    /// <summary>Resolutions of each of its abilities this turn (for "if this is the second time this ability has resolved").</summary>
    public Dictionary<AbilityDefinition, int> ResolvedThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Was cast (from any zone) — for "if you cast it".</summary>
    public bool WasCast { get; set; }

    /// <summary>Was cast from its owner's hand (for "if you cast it from your hand").</summary>
    public bool CastFromHand { get; set; }

    public Card(CardId id, CardDefinition definition, PlayerId owner)
    {
        Id = id;
        PrintedDefinition = definition;
        Owner = owner;
        Controller = owner;
        BaseController = owner;
        Zone = Zone.Library;
    }

    public string Name => NameOverride ?? Definition.Name;

    // Characteristics are read through these so continuous effects (layers) apply everywhere.
    public CardType Types => (TypesOverride ?? Definition.Types) | GrantedTypes;

    /// <summary>Its abilities now: printed ones (unless it lost them) plus granted ones. Indices match <c>ActivateAbility.Index</c>.</summary>
    public IReadOnlyList<AbilityDefinition> Abilities =>
        LosesAbilities ? GrantedAbilities
        : GrantedAbilities.Count == 0 ? Definition.Abilities
        : Definition.Abilities.Concat(GrantedAbilities).ToList();

    /// <summary>Colors now (W, U, B, R, G).</summary>
    public IReadOnlyList<string> Colors => ColorsOverride ?? Definition.ColorList;
    public int Power => (BasePowerOverride ?? Definition.Power ?? 0) + PowerBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);
    public int Toughness => (BaseToughnessOverride ?? Definition.Toughness ?? 0) + ToughnessBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);

    public int CounterCount(CounterKind kind) => Counters.GetValueOrDefault(kind);

    public bool Is(CardType type) => (Types & type) != 0;

    public bool IsCreature => Is(CardType.Creature);

    /// <summary>Has this subtype (changelings have every creature type).</summary>
    public bool HasSubtype(string subtype) =>
        ((SubtypesOverride ?? Definition.Subtypes).Contains(subtype, StringComparer.OrdinalIgnoreCase) || GrantedSubtypes.Contains(subtype)
         || (Has(Keyword.Changeling) && !NonCreatureSubtypes.Contains(subtype)))
        && (TypesOverride is null || SubtypeFitsTypes(subtype));

    /// <summary>
    /// An object can't have a subtype that doesn't correspond to one of its card types (rule 205.3d): a permanent an
    /// effect turns into a land keeps its land types but loses its creature types.
    /// </summary>
    private bool SubtypeFitsTypes(string subtype) =>
        LandTypes.Contains(subtype) ? Is(CardType.Land)
        : ArtifactTypes.Contains(subtype) ? Is(CardType.Artifact)
        : EnchantmentTypes.Contains(subtype) ? Is(CardType.Enchantment)
        : SpellTypes.Contains(subtype) ? Is(CardType.Instant) || Is(CardType.Sorcery)
        : subtype.Equals("Siege", StringComparison.OrdinalIgnoreCase) ? Is(CardType.Battle)
        : Is(CardType.Creature) || Is(CardType.Planeswalker);

    private static readonly HashSet<string> LandTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Plains", "Island", "Swamp", "Mountain", "Forest", "Desert", "Gate", "Lair", "Locus", "Mine", "Power-Plant", "Tower", "Urza's", "Cave", "Sphere", "Town" };

    private static readonly HashSet<string> ArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Equipment", "Vehicle", "Treasure", "Food", "Clue", "Blood", "Gold", "Fortification", "Contraption", "Attraction", "Powerstone", "Map",
        "Incubator", "Book", "Junk", "Spacecraft", "Bobblehead", "Lander",
    };

    private static readonly HashSet<string> EnchantmentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Aura", "Saga", "Shrine", "Curse", "Cartouche", "Class", "Room", "Rune", "Background", "Case", "Role", "Shard" };

    private static readonly HashSet<string> SpellTypes = new(StringComparer.OrdinalIgnoreCase) { "Adventure", "Arcane", "Lesson", "Trap", "Omen" };

    /// <summary>Its subtypes now, after effects that change them (without the every-creature-type of changeling).</summary>
    public IReadOnlyList<string> CurrentSubtypes =>
        (SubtypesOverride ?? Definition.Subtypes).Concat(GrantedSubtypes).Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(t => TypesOverride is null || SubtypeFitsTypes(t)).ToList();

    /// <summary>Abilities it has from effects (not printed).</summary>
    public IReadOnlyList<AbilityDefinition> GainedAbilities => GrantedAbilities;

    /// <summary>Whether an effect made it lose its printed abilities.</summary>
    public bool LostAllAbilities => LosesAbilities;

    /// <summary>Whether a subtype is a creature type (not a land, artifact, enchantment, spell or battle type).</summary>
    internal static bool IsCreatureType(string subtype) => !NonCreatureSubtypes.Contains(subtype);

    /// <summary>Subtypes that aren't creature types (changeling grants only creature types, rule 702.73a).</summary>
    private static readonly HashSet<string> NonCreatureSubtypes =
        new(LandTypes.Concat(ArtifactTypes).Concat(EnchantmentTypes).Concat(SpellTypes).Append("Siege"), StringComparer.OrdinalIgnoreCase);

    /// <summary>Color chosen as it entered ("As this enters, choose a color"): W, U, B, R or G.</summary>
    public string? ChosenColor { get; set; }

    /// <summary>Creature type chosen as it entered.</summary>
    public string? ChosenType { get; set; }

    /// <summary>Its mana abilities now: printed ones (unless lost), with the chosen color, plus granted ones.</summary>
    public IReadOnlyList<ManaOption> ManaOptions
    {
        get
        {
            var options = new List<ManaOption>();
            if (!LosesAbilities)
            {
                var types = Definition.ManaFromChosenColor && ChosenColor is { } color && Mana.ManaTypeExtensions.TryParse(color[0], out var type)
                    ? new[] { type }
                    : Definition.TapForMana;
                if (types.Count > 0 && ManaAmount > 0) options.Add(new ManaOption(types, ManaAmount, Definition.ManaOnlyFor, Definition.ManaOnlyForAbilitiesToo));
                options.AddRange(Definition.ExtraManaOptions.Select(o => o.ColorsAmongYourPermanents ? o with { Types = ColorsAmongYourPermanents }
                    : o.ColorsAmongLegendaryCreatureCardsInGraveyard ? o with { Types = ColorsAmongGraveyardLegends } : o));
            }
            options.AddRange(GrantedManaOptions);
            return options;
        }
    }

    /// <summary>Colors among permanents its controller controls, as mana types (kept current by the engine).</summary>
    internal IReadOnlyList<Mana.ManaType> ColorsAmongYourPermanents { get; set; } = Array.Empty<Mana.ManaType>();

    /// <summary>Colors among legendary creature cards in its controller's graveyard (worked out with continuous effects).</summary>
    internal IReadOnlyList<Mana.ManaType> ColorsAmongGraveyardLegends { get; set; } = Array.Empty<Mana.ManaType>();

    /// <summary>Creature types noted for this permanent ("a creature type that hasn't been noted for this Saga").</summary>
    public List<string> NotedTypes { get; } = new();

    /// <summary>Card types it has protection from (layer 6, from effects).</summary>
    public CardType ProtectionFromTypes { get; set; }

    /// <summary>Optional actions limited to once each turn that were done this turn ("Do this only once each turn").</summary>
    public HashSet<Abilities.Effect> DoneThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Types of mana its first mana ability can add.</summary>
    public IReadOnlyList<Mana.ManaType> ManaTypes => ManaOptions.FirstOrDefault()?.Types ?? Array.Empty<Mana.ManaType>();

    /// <summary>Mana its printed mana ability adds per activation (recomputed for "Add {G} for each Elf you control").</summary>
    public int ManaAmount { get; internal set; } = 1;

    /// <summary>Cast with its kicker cost paid (kept as the spell becomes a permanent, rule 702.33).</summary>
    public bool Kicked { get; set; }

    /// <summary>Indices of "activate only once each turn" abilities already activated this turn.</summary>
    public HashSet<int> ActivatedThisTurn { get; } = new();

    /// <summary>A loyalty ability of this planeswalker was activated this turn (one per turn, rule 606.3).</summary>
    public bool LoyaltyActivatedThisTurn { get; set; }

    /// <summary>"Triggers only once each turn" abilities that already triggered this turn.</summary>
    public HashSet<AbilityDefinition> TriggeredThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    public bool Has(Keyword keyword) => !LostKeywords.Contains(keyword) && ((!LosesAbilities && Definition.KeywordAbilities.Contains(keyword)) || GrantedKeywords.Contains(keyword));

    /// <summary>A creature that can't attack or use {T} abilities yet (rule 302.6); haste removes the restriction.</summary>
    public bool IsSummoningSick => IsCreature && !ControlledSinceTurnStart && !Has(Keyword.Haste);

    /// <summary>Clears per-object status when the card changes zones (rule 400.7: it becomes a new object).</summary>
    internal void ResetStatus()
    {
        if (Zone == Zone.Battlefield)
        {
            var subtypes = (SubtypesOverride ?? Definition.Subtypes).Concat(GrantedSubtypes).Where(t => TypesOverride is null || SubtypeFitsTypes(t)).ToList();
            LastKnownInfo = new LastKnown(Power, Toughness, Controller, new Dictionary<CounterKind, int>(Counters), subtypes, Abilities.ToList(), Types)
            {
                Name = Name,
                Colors = Colors.ToList(),
                Keywords = Enum.GetValues<Keyword>().Where(Has).ToHashSet(),
                Tapped = Tapped,
                Attacking = WasAttacking,
                Blocking = WasBlocking,
                Supertypes = Supertypes,
                AttachedTo = AttachedTo,
                Version = Version,
                ManaValue = Definition.ManaCost.ManaValue,
            };
        }
        Tapped = false;
        Damage = 0;
        DamagedByDeathtouch = false;
        ControlledSinceTurnStart = false;
        Counters.Clear();
        AttachedTo = null;
        PowerBonus = 0;
        ToughnessBonus = 0;
        GrantedKeywords.Clear();
        LostKeywords.Clear();
        GrantedSubtypes.Clear();
        GrantedAbilities.Clear();
        Timestamp = 0;
        TypesOverride = null;
        GrantedTypes = 0;
        SubtypesOverride = null;
        ColorsOverride = null;
        NameOverride = null;
        LosesAbilities = false;
        GrantedManaOptions.Clear();
        CastFromHand = false;
        WasCast = false;
        BasePowerOverride = null;
        BaseToughnessOverride = null;
        Kicked = false;
        ChosenColor = null;
        ChosenType = null;
        ActivatedThisTurn.Clear();
        TriggeredThisTurn.Clear();
        LoyaltyActivatedThisTurn = false;
        ChosenModes.Clear();
        ActivatedEver.Clear();
        ChosenName = null;
        AttacksThisTurn = 0;
        AsAdventure = false;
        OnAdventure = false;
        ChosenParity = null;
        CastFromGraveyard = false;
        ManaSpent = 0;
        PaidWithTreasure = false;
        GiftPromised = false;
        FaceDown = false;
        Uncounterable = false;
        GrantedWards.Clear();
        Version++;
        Controller = Owner;
        BaseController = Owner;
    }

    public override string ToString() => $"{Name} {Id}";
}

/// <summary>Last known information about a permanent that left the battlefield (rule 608.2h): how it last existed there.</summary>
public sealed record LastKnown(int Power, int Toughness, Core.PlayerId Controller, IReadOnlyDictionary<CounterKind, int> Counters,
    IReadOnlyList<string> Subtypes, IReadOnlyList<AbilityDefinition> Abilities, CardType Types)
{
    /// <summary>The object (version) this describes.</summary>
    public int Version { get; init; }

    public int ManaValue { get; init; }

    public string Name { get; init; } = "";
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    public IReadOnlySet<Keyword> Keywords { get; init; } = new HashSet<Keyword>();
    public bool Tapped { get; init; }
    public bool Attacking { get; init; }
    public bool Blocking { get; init; }
    public Supertype Supertypes { get; init; }
    public Core.CardId? AttachedTo { get; init; }

    /// <summary>Had this subtype (changelings had every creature type).</summary>
    public bool HasSubtype(string subtype) =>
        Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase) || (Keywords.Contains(Keyword.Changeling) && Card.IsCreatureType(subtype));
}
