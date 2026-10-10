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
    public CardDefinition Definition => EffectCopy ?? CopiedDefinition ?? (Transformed && BackFaceDefinition is { } back ? back
        : AsAdventure && PrintedDefinition.Adventure is { } adventure ? adventure
        : CastHalf is { } half && PrintedDefinition.SplitHalves is { } halves ? halves[half]
        : PrintedDefinition);

    /// <summary>
    /// A double-faced permanent with its back face up (rule 701.27g: a "transformed permanent"). Only a permanent can be
    /// transformed: in every other zone the card has its front face up (712.8a), and it enters the battlefield front face
    /// up unless an effect puts it there transformed (712.14).
    /// </summary>
    public bool Transformed { get; internal set; }

    /// <summary>Times this object has transformed (rule 701.27f: an ability of it transforms it only if it hasn't since).</summary>
    public int TransformCount { get; internal set; }

    /// <summary>A double-faced card or token (rule 712.2): it can transform; a copy of one isn't (701.27c).</summary>
    public bool IsDoubleFaced => PrintedDefinition.BackFace is not null;

    private CardDefinition? _backFace;

    /// <summary>The back face as this object has it: a token's back face is a token too, a foil card's back is foil.</summary>
    private CardDefinition? BackFaceDefinition => PrintedDefinition.BackFace is not { } back ? null
        : _backFace ??= back with { IsToken = PrintedDefinition.IsToken, Foil = PrintedDefinition.Foil, OracleId = back.OracleId ?? PrintedDefinition.OracleId };

    /// <summary>
    /// Its mana value (rule 202.3): with its back face up, that of its front face's mana cost (712.8e), or 0 for a copy of a
    /// back face (202.3b) — a double-faced token is one (707.8a). A spell's X is added where it is on the stack.
    /// </summary>
    public int ManaValue => !IsCopy && Transformed && PrintedDefinition.BackFace is not null
        ? (PrintedDefinition.IsToken ? 0 : PrintedDefinition.ManaCost.ManaValue)
        : Definition.ManaCost.ManaValue;

    /// <summary>
    /// The copiable values it has from a copy effect ("enter as a copy of any creature", rule 707): while on the battlefield
    /// its characteristics are those of the copied object (themselves copiable), not its own.
    /// </summary>
    internal CardDefinition? CopiedDefinition { get; set; }

    /// <summary>
    /// The copiable values a continuous copy effect gives it ("enchanted creature is a copy of the chosen creature", layer 1, rule 613.1a):
    /// worked out again whenever continuous effects are, so it lasts as long as the effect does.
    /// </summary>
    internal CardDefinition? EffectCopy { get; set; }

    /// <summary>It is a copy of something, by entering as one or through a copy effect.</summary>
    public bool IsCopy => CopiedDefinition is not null || EffectCopy is not null;

    /// <summary>The creature chosen as this permanent entered ("as this Aura enters, choose a creature"), card and version.</summary>
    public (CardId Card, int Version)? ChosenCreature { get; set; }

    /// <summary>The copiable values the chosen creature last had on the battlefield (kept when it leaves, rule 608.2h).</summary>
    internal CardDefinition? ChosenCreatureValues { get; set; }

    /// <summary>It entered as a copy of another object: its own printed name (shown with the copy's).</summary>
    public string? CopyOfName => IsCopy ? PrintedDefinition.Name : null;

    /// <summary>A split card being cast as (or on the stack as) one of its halves (rule 709.3).</summary>
    public int? CastHalf { get; set; }

    /// <summary>It is being cast, or is on the stack, as its Adventure (rule 715.3).</summary>
    public bool AsAdventure { get; set; }

    /// <summary>Exiled after resolving as an Adventure: its owner may cast it (not as an Adventure) from exile (rule 715.4).</summary>
    public bool OnAdventure { get; set; }
    public PlayerId Owner { get; }
    public PlayerId Controller { get; set; }

    /// <summary>Who controls it apart from static control-changing effects (rule 613.1b); see <see cref="Controller"/>.</summary>
    public PlayerId BaseController { get; set; }

    /// <summary>For an emblem standing for a delayed ability: the object it calls "that creature" (card and version).</summary>
    public (CardId Card, int Version)? Remembered { get; set; }

    /// <summary>
    /// A token that left the battlefield, or a copy of a card or spell that left the stack or was never cast: it has ceased to
    /// exist (rules 111.7, 704.5d-e) and can't move to another zone or come back to the battlefield (rule 111.8).
    /// </summary>
    public bool CeasedToExist { get; internal set; }

    /// <summary>Players this creature dealt combat damage to this turn.</summary>
    public HashSet<PlayerId> CombatDamagedPlayers { get; } = new();

    /// <summary>It has dealt damage (any amount, to anything) since it came to the battlefield.</summary>
    public bool HasDealtDamage { get; set; }

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

    /// <summary>Exiled face down: only the players in <see cref="KnownTo"/> may look at it (rule 406.3).</summary>
    public bool FaceDown { get; set; }

    /// <summary>
    /// Players who know this card although where it is hides it from them: in a hand (besides its owner), in a library,
    /// or exiled face down. A player learns it by looking at it or when it's revealed, and keeps knowing it as it moves
    /// from where they could see it, until its place is lost to them: its library is shuffled, or it's put among other
    /// cards in an order they don't see (rule 401.4).
    /// </summary>
    public HashSet<PlayerId> KnownTo { get; } = new();

    /// <summary>Whether <paramref name="viewer"/> may see this card's face where it is now.</summary>
    public bool IsVisibleTo(PlayerId viewer) => Zone switch
    {
        Zone.Hand => Owner == viewer || KnownTo.Contains(viewer),
        Zone.Library => KnownTo.Contains(viewer),
        _ => !FaceDown || KnownTo.Contains(viewer),
    };

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

    /// <summary>Damage static abilities prevent to it ("prevent all noncombat damage that would be dealt to equipped creature").</summary>
    public StaticDamagePrevention StaticDamagePrevention { get; internal set; }

    /// <summary>"Its activated abilities can't be activated" applies to it (mana abilities too).</summary>
    public bool AbilitiesCantBeActivated { get; internal set; }

    /// <summary>Times each of its activated abilities (by index) was activated this turn.</summary>
    public Dictionary<int, int> ActivationsThisTurn { get; } = new();

    /// <summary>Supertypes given by effects ("your Ring-bearer is legendary").</summary>
    internal Supertype GrantedSupertypes { get; set; }

    /// <summary>Players whose creatures can't block it this turn ("can't be blocked by creatures that player controls").</summary>
    internal HashSet<Core.PlayerId> UnblockableBy { get; } = new();

    /// <summary>Blockers matching one of these can't block it ("can't be blocked by creatures with power 2 or less"), from effects.</summary>
    internal List<Abilities.ObjectFilter> BlockRestrictions { get; } = new();

    /// <summary>Players it can't attack ("can't attack you"), from static abilities.</summary>
    internal HashSet<Core.PlayerId> CantAttackPlayers { get; } = new();

    /// <summary>Players it has protection from (rule 702.16j), and "protection from Ring-bearers".</summary>
    internal HashSet<Core.PlayerId> ProtectedFromPlayers { get; } = new();
    internal bool ProtectedFromRingBearers { get; set; }

    /// <summary>It is its controller's Ring-bearer (kept current by the engine).</summary>
    internal bool IsRingBearerNow { get; set; }

    /// <summary>Players goading it through static abilities ("enchanted creature is goaded").</summary>
    internal HashSet<Core.PlayerId> StaticGoaders { get; } = new();

    /// <summary>It has "This creature can't attack its owner".</summary>
    internal bool CantAttackOwner { get; set; }

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

    /// <summary>
    /// An effect set its land subtype (rule 305.7): it loses the abilities from its rules text (and its old land types' mana
    /// abilities) and has the mana ability of each basic land type it has now. Abilities granted by effects stay.
    /// </summary>
    internal bool LosesTextAbilities { get; set; }

    /// <summary>
    /// It has the abilities of its rules text: no effect made it lose all its abilities, nor the abilities from its rules text
    /// (a land whose subtype an effect set, rule 305.7). Static rules of the card itself apply only then.
    /// </summary>
    public bool HasTextAbilities => !LosesAbilities && !LosesTextAbilities;

    /// <summary>The replacement and prevention effects of its rules text it has now: none once it lost those abilities.</summary>
    public Replacements Replaces => HasTextAbilities ? Definition.Replaces : Replacements.None;
    internal List<AbilityDefinition> GrantedAbilities { get; } = new();
    internal List<ManaOption> GrantedManaOptions { get; } = new();

    /// <summary>
    /// Its timestamp as a permanent (rule 613.7d–e): given as it enters the battlefield and again when it becomes
    /// attached to something; 0 until the engine assigns one.
    /// </summary>
    public long Timestamp { get; internal set; }

    /// <summary>What the card was like the last time it was on the battlefield (rule 608.2h, last known information).</summary>
    public LastKnown? LastKnownInfo { get; internal set; }

    /// <summary>Last known information of each object (version) this card was on the battlefield, by version.</summary>
    private readonly Dictionary<int, LastKnown> _lastKnownByVersion = new();

    /// <summary>
    /// How the object <paramref name="version"/> of this card last existed on the battlefield, if it was a permanent that left it
    /// (rule 608.2h): kept for each object, so a card that came back and left again still has the earlier object's.
    /// </summary>
    public LastKnown? LastKnownOf(int version) => _lastKnownByVersion.GetValueOrDefault(version);

    /// <summary>The permanent (id, version) that exiled this card "with it" (for "the exiled card").</summary>
    public (CardId Source, int Version)? ExiledWith { get; set; }

    /// <summary>The zone it came from the last time it changed zones ("a permanent that entered from a graveyard").</summary>
    public Zone EnteredFrom { get; set; }

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
        LosesAbilities || LosesTextAbilities ? GrantedAbilities
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
        : PlaneswalkerTypes.Contains(subtype) ? Is(CardType.Planeswalker)
        : Is(CardType.Creature) || Is(CardType.Kindred);

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

    /// <summary>Planeswalker types (rule 205.3j), kept apart from creature types: an animated Gideon that becomes a Frog is still a Gideon.</summary>
    private static readonly HashSet<string> PlaneswalkerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ajani", "Aminatou", "Angrath", "Arlinn", "Ashiok", "Bahamut", "Basri", "Bolas", "Calix", "Chandra", "Comet", "Dack", "Dakkon",
        "Daretti", "Davriel", "Dihada", "Domri", "Dovin", "Ellywick", "Elminster", "Elspeth", "Estrid", "Freyalise", "Garruk", "Gideon",
        "Grist", "Guff", "Huatli", "Jace", "Jared", "Jaya", "Jeska", "Kaito", "Karn", "Kasmina", "Kaya", "Kiora", "Koth", "Liliana",
        "Lolth", "Lukka", "Minsc", "Mordenkainen", "Nahiri", "Narset", "Niko", "Nissa", "Nixilis", "Oko", "Quintorius", "Ral", "Rowan",
        "Saheeli", "Samut", "Sarkhan", "Serra", "Sivitri", "Sorin", "Szat", "Tamiyo", "Tasha", "Teferi", "Teyo", "Tezzeret", "Tibalt",
        "Tyvar", "Ugin", "Urza", "Venser", "Vivien", "Vraska", "Vronos", "Will", "Windgrace", "Wrenn", "Xenagos", "Yanggu", "Yanling", "Zariel",
    };

    /// <summary>Its subtypes now, after effects that change them (without the every-creature-type of changeling).</summary>
    public IReadOnlyList<string> CurrentSubtypes =>
        (SubtypesOverride ?? Definition.Subtypes).Concat(GrantedSubtypes).Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(t => TypesOverride is null || SubtypeFitsTypes(t)).ToList();

    /// <summary>Abilities it has from effects (not printed).</summary>
    public IReadOnlyList<AbilityDefinition> GainedAbilities => GrantedAbilities;

    /// <summary>Whether an effect made it lose its printed abilities.</summary>
    public bool LostAllAbilities => LosesAbilities;

    /// <summary>Whether a subtype is a land type.</summary>
    internal static bool IsLandType(string subtype) => LandTypes.Contains(subtype);

    /// <summary>Which set of subtypes (rule 205.3) a subtype belongs to: land, artifact, enchantment, spell, battle, planeswalker or creature types.</summary>
    private static int SubtypeSet(string subtype) =>
        LandTypes.Contains(subtype) ? 1 : ArtifactTypes.Contains(subtype) ? 2 : EnchantmentTypes.Contains(subtype) ? 3 : SpellTypes.Contains(subtype) ? 4
        : subtype.Equals("Siege", StringComparison.OrdinalIgnoreCase) ? 5 : PlaneswalkerTypes.Contains(subtype) ? 6 : 0;

    /// <summary>
    /// An effect that sets subtypes ("becomes a blue Frog") replaces only the existing subtypes of the same sets as the new ones
    /// (rule 205.1a): a land creature that becomes a Frog is still a Forest.
    /// </summary>
    internal static List<string> ReplaceSubtypes(IEnumerable<string> current, IReadOnlyList<string> set)
    {
        if (set.Count == 0) return new List<string>(); // "loses all subtypes"
        var replaced = set.Select(SubtypeSet).ToHashSet();
        return current.Where(s => !replaced.Contains(SubtypeSet(s))).Concat(set).ToList();
    }

    /// <summary>Whether a subtype is a creature type (not a land, artifact, enchantment, spell, battle or planeswalker type).</summary>
    internal static bool IsCreatureType(string subtype) => !NonCreatureSubtypes.Contains(subtype);

    /// <summary>Subtypes that aren't creature types (changeling grants only creature types, rule 702.73a).</summary>
    private static readonly HashSet<string> NonCreatureSubtypes =
        new(LandTypes.Concat(ArtifactTypes).Concat(EnchantmentTypes).Concat(SpellTypes).Concat(PlaneswalkerTypes).Append("Siege"), StringComparer.OrdinalIgnoreCase);

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
            if (LosesTextAbilities && !LosesAbilities)
            {
                // Its land types were set: only the intrinsic mana abilities of the basic land types it has now (rule 305.7).
                foreach (var (landType, mana) in BasicLandMana)
                    if (HasSubtype(landType)) options.Add(new ManaOption(new[] { mana }));
            }
            else if (!LosesAbilities)
            {
                var types = Definition.ManaFromChosenColor && ChosenColor is { } color && Mana.ManaTypeExtensions.TryParse(color[0], out var type)
                    ? new[] { type }
                    : Definition.TapForMana;
                if (types.Count > 0 && ManaAmount > 0) options.Add(new ManaOption(types, ManaAmount, Definition.ManaOnlyFor, Definition.ManaOnlyForAbilitiesToo));
                options.AddRange(Definition.ExtraManaOptions.Select((o, i) =>
                {
                    var resolved = o.ColorsAmongYourPermanents ? o with { Types = ColorsAmongYourPermanents }
                        : o.ColorsAmongLegendaryCreatureCardsInGraveyard ? o with { Types = ColorsAmongGraveyardLegends }
                        : o.CommanderIdentity ? o with { Types = CommanderIdentityTypes }
                        : o.ColorsOpponentsLandsCouldProduce ? o with { Types = OpponentsLandColors }
                        : o.TypesYourLandsCouldProduce ? o with { Types = YourLandTypes }
                        : o;
                    // "Remove any number of [kind] counters: add one mana for each": at most as many as it has now.
                    if (resolved.RemovesCounters is { } kind) resolved = resolved with { Amount = CounterCount(kind) };
                    // An ability that can't add mana now keeps its place (abilities are numbered) but adds none.
                    return resolved.Types.Count == 0 || InactiveManaOptions.Contains(i) ? resolved with { Amount = 0, OneOfEach = false } : resolved;
                }));
                // A land with a basic land type has that type's mana ability (rule 305.6), also when an effect gives it the type.
                if (Is(CardType.Land))
                    foreach (var (landType, mana) in BasicLandMana)
                        if (HasSubtype(landType) && !Definition.Subtypes.Contains(landType, StringComparer.OrdinalIgnoreCase) && !types.Contains(mana))
                            options.Add(new ManaOption(new[] { mana }));
            }
            options.AddRange(GrantedManaOptions);
            return options;
        }
    }

    private static readonly (string Type, Mana.ManaType Mana)[] BasicLandMana =
    {
        ("Plains", Mana.ManaType.White), ("Island", Mana.ManaType.Blue), ("Swamp", Mana.ManaType.Black), ("Mountain", Mana.ManaType.Red), ("Forest", Mana.ManaType.Green),
    };

    /// <summary>Colors in its controller's commander's color identity, as mana types (kept current by the engine).</summary>
    internal IReadOnlyList<Mana.ManaType> CommanderIdentityTypes { get; set; } = Array.Empty<Mana.ManaType>();

    /// <summary>Colors lands its controller's opponents could produce / types lands its controller could produce.</summary>
    internal IReadOnlyList<Mana.ManaType> OpponentsLandColors { get; set; } = Array.Empty<Mana.ManaType>();
    internal IReadOnlyList<Mana.ManaType> YourLandTypes { get; set; } = Array.Empty<Mana.ManaType>();

    /// <summary>Indices of its extra mana abilities whose condition doesn't hold now.</summary>
    internal HashSet<int> InactiveManaOptions { get; } = new();

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

    /// <summary>Suspended (rule 702.62): in exile with time counters and suspend.</summary>
    public bool Suspended { get; set; }

    /// <summary>As a spell: cast by its controller during their own main phase (addendum).</summary>
    public bool CastDuringMainPhase { get; set; }

    /// <summary>Renowned (rule 702.112b): stays until it leaves the battlefield.</summary>
    public bool Renowned { get; set; }

    /// <summary>Exerted: the turn it was exerted, and whether it skips its controller's next untap step (rule 701.39).</summary>
    public int ExertedTurn { get; set; } = -1;
    public bool SkipsNextUntap { get; set; }

    /// <summary>Regeneration shields on it this turn (rule 701.19).</summary>
    public int RegenerationShields { get; set; }

    /// <summary>Times its multikicker / squad cost was paid as it was cast (kept as the spell becomes a permanent).</summary>
    public int TimesKicked { get; set; }
    public int SquadPaid { get; set; }

    /// <summary>Indices of "activate only once each turn" abilities already activated this turn.</summary>
    public HashSet<int> ActivatedThisTurn { get; } = new();

    /// <summary>A loyalty ability of this planeswalker was activated this turn (one per turn, rule 606.3).</summary>
    public bool LoyaltyActivatedThisTurn { get; set; }

    /// <summary>"Triggers only once each turn" abilities that already triggered this turn.</summary>
    public HashSet<AbilityDefinition> TriggeredThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    public bool Has(Keyword keyword) => !LostKeywords.Contains(keyword) && ((!LosesAbilities && !LosesTextAbilities && Definition.KeywordAbilities.Contains(keyword)) || GrantedKeywords.Contains(keyword));

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
                ManaValue = ManaValue,
                // Its copiable values (rule 707.2): a double-faced card's whole card, with the face that was up.
                CopiableValues = !IsCopy && IsDoubleFaced ? PrintedDefinition : Definition,
                CopiableBackFaceUp = !IsCopy && IsDoubleFaced && Transformed,
                Renowned = Renowned,
                HasTextAbilities = HasTextAbilities,
            };
            _lastKnownByVersion[Version] = LastKnownInfo;
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
        LosesTextAbilities = false;
        CopiedDefinition = null;
        EffectCopy = null;
        ChosenCreature = null;
        ChosenCreatureValues = null;
        Transformed = false; // it leaves the battlefield and is front face up again (712.8a)
        TransformCount = 0;
        GrantedManaOptions.Clear();
        CastFromHand = false;
        WasCast = false;
        BasePowerOverride = null;
        BaseToughnessOverride = null;
        Kicked = false;
        TimesKicked = 0;
        SquadPaid = 0;
        Renowned = false;
        Suspended = false;
        ExertedTurn = -1;
        SkipsNextUntap = false;
        RegenerationShields = 0;
        ChosenColor = null;
        ChosenType = null;
        ActivatedThisTurn.Clear();
        ActivationsThisTurn.Clear();
        StaticDamagePrevention = StaticDamagePrevention.None;
        AbilitiesCantBeActivated = false;
        TriggeredThisTurn.Clear();
        LoyaltyActivatedThisTurn = false;
        ChosenModes.Clear();
        ActivatedEver.Clear();
        ChosenName = null;
        AttacksThisTurn = 0;
        HasDealtDamage = false;
        AsAdventure = false;
        CastHalf = null;
        OnAdventure = false;
        ChosenParity = null;
        CastFromGraveyard = false;
        ManaSpent = 0;
        PaidWithTreasure = false;
        GiftPromised = false;
        FaceDown = false;
        KnownTo.Clear();
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

    /// <summary>It was renowned (rule 702.112b).</summary>
    public bool Renowned { get; init; }

    /// <summary>It had the abilities of its rules text (it hadn't lost them, <see cref="Card.HasTextAbilities"/>).</summary>
    public bool HasTextAbilities { get; init; } = true;
    public Supertype Supertypes { get; init; }
    public Core.CardId? AttachedTo { get; init; }

    /// <summary>Its copiable values as it last existed on the battlefield ("create a token that's a copy of that creature", rule 707.4).</summary>
    public CardDefinition? CopiableValues { get; init; }
    public bool CopiableBackFaceUp { get; init; }

    /// <summary>Had this subtype (changelings had every creature type).</summary>
    public bool HasSubtype(string subtype) =>
        Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase) || (Keywords.Contains(Keyword.Changeling) && Card.IsCreatureType(subtype));
}
