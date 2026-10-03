// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A card object in a game: its definition plus mutable in-game status.</summary>
public sealed class Card
{
    public CardId Id { get; }
    public CardDefinition Definition { get; }
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
    internal IReadOnlyList<Mana.ManaType>? ManaTypesOverride { get; set; }

    /// <summary>Keywords, creature types and abilities given "permanently" by resolved effects (until it leaves the battlefield).</summary>
    public HashSet<Keyword> PermanentKeywords { get; } = new();
    public HashSet<string> PermanentSubtypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string>? PermanentSubtypesOverride { get; set; }
    public List<AbilityDefinition> PermanentAbilities { get; } = new();
    public int? PermanentBasePower { get; set; }
    public int? PermanentBaseToughness { get; set; }

    /// <summary>What the card was like the last time it was on the battlefield (rule 608.2h, last known information).</summary>
    public LastKnown? LastKnownInfo { get; internal set; }

    /// <summary>The permanent (id, version) that exiled this card "with it" (for "the exiled card").</summary>
    public (CardId Source, int Version)? ExiledWith { get; set; }

    /// <summary>Was attacking when it last left the battlefield.</summary>
    public bool WasAttacking { get; set; }

    /// <summary>Resolutions of each of its abilities this turn (for "if this is the second time this ability has resolved").</summary>
    public Dictionary<AbilityDefinition, int> ResolvedThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Was cast from its owner's hand (for "if you cast it from your hand").</summary>
    public bool CastFromHand { get; set; }

    public Card(CardId id, CardDefinition definition, PlayerId owner)
    {
        Id = id;
        Definition = definition;
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
        (SubtypesOverride ?? Definition.Subtypes).Contains(subtype, StringComparer.OrdinalIgnoreCase) || GrantedSubtypes.Contains(subtype) || (Has(Keyword.Changeling) && subtype is not ("Equipment" or "Aura" or "Treasure" or "Food" or "Clue"));

    /// <summary>Color chosen as it entered ("As this enters, choose a color"): W, U, B, R or G.</summary>
    public string? ChosenColor { get; set; }

    /// <summary>Creature type chosen as it entered.</summary>
    public string? ChosenType { get; set; }

    /// <summary>Types of mana its mana ability can add (the chosen color for "Add one mana of the chosen color").</summary>
    public IReadOnlyList<Mana.ManaType> ManaTypes =>
        ManaTypesOverride is { } overridden ? overridden
        : LosesAbilities ? Array.Empty<Mana.ManaType>()
        : Definition.ManaFromChosenColor && ChosenColor is { } color && Mana.ManaTypeExtensions.TryParse(color[0], out var type)
            ? new[] { type }
            : Definition.TapForMana;

    /// <summary>Mana its mana ability adds per activation (recomputed for "Add {G} for each Elf you control").</summary>
    public int ManaAmount { get; internal set; } = 1;

    /// <summary>Cast with its kicker cost paid (kept as the spell becomes a permanent, rule 702.33).</summary>
    public bool Kicked { get; set; }

    /// <summary>Indices of "activate only once each turn" abilities already activated this turn.</summary>
    public HashSet<int> ActivatedThisTurn { get; } = new();

    /// <summary>A loyalty ability of this planeswalker was activated this turn (one per turn, rule 606.3).</summary>
    public bool LoyaltyActivatedThisTurn { get; set; }

    /// <summary>"Triggers only once each turn" abilities that already triggered this turn.</summary>
    public HashSet<AbilityDefinition> TriggeredThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    public bool Has(Keyword keyword) => (!LosesAbilities && Definition.KeywordAbilities.Contains(keyword)) || GrantedKeywords.Contains(keyword);

    /// <summary>A creature that can't attack or use {T} abilities yet (rule 302.6); haste removes the restriction.</summary>
    public bool IsSummoningSick => IsCreature && !ControlledSinceTurnStart && !Has(Keyword.Haste);

    /// <summary>Clears per-object status when the card changes zones (rule 400.7: it becomes a new object).</summary>
    internal void ResetStatus()
    {
        if (Zone == Zone.Battlefield)
            LastKnownInfo = new LastKnown(Power, Toughness, Controller, new Dictionary<CounterKind, int>(Counters),
                Definition.Subtypes.Concat(GrantedSubtypes).ToList(), Abilities.ToList(), Types);
        Tapped = false;
        Damage = 0;
        DamagedByDeathtouch = false;
        ControlledSinceTurnStart = false;
        Counters.Clear();
        AttachedTo = null;
        PowerBonus = 0;
        ToughnessBonus = 0;
        GrantedKeywords.Clear();
        GrantedSubtypes.Clear();
        PermanentKeywords.Clear();
        PermanentSubtypes.Clear();
        PermanentSubtypesOverride = null;
        PermanentAbilities.Clear();
        PermanentBasePower = null;
        PermanentBaseToughness = null;
        GrantedAbilities.Clear();
        TypesOverride = null;
        GrantedTypes = 0;
        SubtypesOverride = null;
        ColorsOverride = null;
        NameOverride = null;
        LosesAbilities = false;
        ManaTypesOverride = null;
        CastFromHand = false;
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
        Version++;
        Controller = Owner;
        BaseController = Owner;
    }

    public override string ToString() => $"{Name} {Id}";
}

/// <summary>Last known information about a permanent that left the battlefield.</summary>
public sealed record LastKnown(int Power, int Toughness, Core.PlayerId Controller, IReadOnlyDictionary<CounterKind, int> Counters,
    IReadOnlyList<string> Subtypes, IReadOnlyList<AbilityDefinition> Abilities, CardType Types);
