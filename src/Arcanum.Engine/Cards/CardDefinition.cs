// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>A choice made as a permanent enters (rule 614.12).</summary>
public enum EnterChoice { None, Color, CreatureType, CardName, OddOrEven, PayLifeOrTapped, RevealOrTapped, BasicLandType, LookAtOpponentsHandThenCardName, NonbasicLandCardName }

/// <summary>Replacement and rule-changing effects a permanent has while on the battlefield (rule 614).</summary>
[Flags]
public enum Replacements : long
{
    None = 0,
    /// <summary>Sources you control deal double damage to opponents and their permanents.</summary>
    DoubleDamageToOpponents = 1,
    /// <summary>Creatures you control deal double damage.</summary>
    DoubleCreatureDamage = 2,
    /// <summary>Effects create twice as many tokens under your control.</summary>
    DoubleTokens = 4,
    /// <summary>Twice as many counters are put on permanents you control.</summary>
    DoubleCounters = 8,
    /// <summary>Prevent all combat damage dealt to and by this creature.</summary>
    PreventCombatDamageToAndBySelf = 16,
    /// <summary>Prevent all noncombat damage that would be dealt to other creatures you control.</summary>
    PreventNoncombatDamageToYourOtherCreatures = 32,
    /// <summary>If you would gain life, you gain that much plus 1.</summary>
    ExtraLifeGain = 64,
    /// <summary>You can't lose the game and your opponents can't win.</summary>
    YouCantLose = 128,
    /// <summary>Instant and sorcery cards that would go to a graveyard from anywhere are exiled instead.</summary>
    ExileInstantsAndSorceries = 256,
    /// <summary>Creatures your opponents control enter tapped.</summary>
    OpponentsCreaturesEnterTapped = 512,
    /// <summary>If this card would be put into a graveyard from anywhere, it is shuffled into its owner's library instead (works in any zone).</summary>
    ShuffleIntoLibraryInsteadOfGraveyard = 1024,
    /// <summary>You may cast spells as though they had flash.</summary>
    YourSpellsHaveFlash = 2048,
    /// <summary>Your instant and sorcery spells can't be countered.</summary>
    YourInstantsAndSorceriesCantBeCountered = 4096,
    /// <summary>You have no maximum hand size.</summary>
    NoMaximumHandSize = 8192,
    /// <summary>You may cast spells from your hand without paying their mana costs.</summary>
    CastFromHandFree = 16384,
    /// <summary>You may cast creature spells from the top of your library (and look at it any time), spending mana of any type on them.</summary>
    CreaturesFromLibraryTop = 32768,
    /// <summary>During your turn, you may play cards you don't own with stash counters from exile, spending mana of any type.</summary>
    PlayStashedCards = 65536,
    /// <summary>During each of your turns, you may play a land and cast a permanent spell of each permanent type from your graveyard.</summary>
    PermanentsFromGraveyard = 131072,
    /// <summary>Activated abilities of sources with the name chosen as this entered can't be activated unless they're mana abilities.</summary>
    StopsChosenNameAbilities = 262144,
    /// <summary>Each other Angel you control enters with an additional +1/+1 counter for each Angel you already control.</summary>
    AngelsEnterWithCounters = 524288,
    /// <summary>You may play an additional land on each of your turns (while <see cref="CardDefinition.AdditionalLandPlayIf"/> holds).</summary>
    AdditionalLandPlay = 1048576,
    /// <summary>If you would draw a card except the first one you draw in each of your draw steps, draw two cards instead.</summary>
    DrawTwoExceptFirstInDrawStep = 2097152,
    /// <summary>If a creature an opponent controls would die, exile it instead (its "When you do" ability then triggers).</summary>
    OpponentsCreaturesExiledInsteadOfDying = 4194304,
    /// <summary>If you would create a Food token, instead create a Food token and a Treasure token.</summary>
    FoodAlsoTreasure = 8388608,
    /// <summary>You may look at the top card of your library and cast creature spells from there (paying normally).</summary>
    CastCreaturesFromLibraryTop = 16777216,
    /// <summary>If one or more tokens would be created under your control, those tokens plus an additional Food token are created instead.</summary>
    ExtraFoodWithTokens = 1L << 25,
    /// <summary>If you would draw a card while you have no cards in hand, draw two cards instead.</summary>
    DrawTwoWithEmptyHand = 1L << 26,
    /// <summary>If you would gain life while you have 5 or less life, you gain twice that much life instead.</summary>
    DoubleLifeGainAtFiveOrLess = 1L << 27,
    /// <summary>You don't lose unspent green mana as steps and phases end.</summary>
    KeepGreenMana = 1L << 28,
    /// <summary>If +1/+1 counters would be put on an Army, Goblin, or Orc you control, that many plus one are put on it instead.</summary>
    ExtraCounterOnArmiesGoblinsOrcs = 1L << 29,
    /// <summary>During your turn, prevent all damage that would be dealt to this permanent.</summary>
    PreventDamageToSelfDuringYourTurn = 1L << 30,
    /// <summary>During your turn, you may activate equip abilities any time you could cast an instant.</summary>
    EquipAtInstantSpeedOnYourTurn = 1L << 31,
    /// <summary>If a legendary permanent or an artifact entering or leaving the battlefield causes a triggered ability of a permanent you control to trigger, it triggers an additional time.</summary>
    ExtraTriggersFromLegendariesAndArtifactsMoving = 1L << 32,
    /// <summary>Mana of any type can be spent to activate this permanent's abilities.</summary>
    AnyManaForItsAbilities = 1L << 33,
    /// <summary>If an opponent would draw a card except the first one they draw in each of their draw steps, instead that player skips that draw and you draw a card.</summary>
    StealsOpponentsExtraDraws = 1L << 36,
    /// <summary>You may look at the top card of your library any time.</summary>
    LookAtLibraryTop = 1L << 37,
    /// <summary>As long as an opponent controls more lands than you, you may play lands from the top of your library.</summary>
    PlayLandsFromLibraryTopWhileBehind = 1L << 38,
    /// <summary>"If a source an opponent controls would deal damage to you, prevent 1 of that damage."</summary>
    PreventOneDamageFromOpponentsSources = 1L << 39,
    /// <summary>"If damage would be dealt to this creature, prevent that damage and remove that many +1/+1 counters from it."</summary>
    PreventDamageRemoveCounters = 1L << 40,
    /// <summary>"Your opponents play with their hands revealed": this permanent's controller sees its opponents' hands.</summary>
    OpponentsPlayWithHandsRevealed = 1L << 41,
    /// <summary>"Play with the top card of your library revealed": every player sees its controller's top card.</summary>
    PlayWithTopCardRevealed = 1L << 42,
    /// <summary>You can't win the game and your opponents can't lose the game.</summary>
    OpponentsCantLoseYouCantWin = 1L << 34,
    /// <summary>If you control a creature, damage that would reduce your life total to less than 1 reduces it to 1 instead.</summary>
    DamageCantReduceYourLifeBelowOne = 1L << 35,
    /// <summary>"You may play lands from your graveyard" (no limit besides the land plays allowed).</summary>
    LandsFromGraveyard = 1L << 43,
}

/// <summary>
/// Immutable characteristics of a card as printed (oracle). Shared by every instance of the card.
/// </summary>
public sealed record CardDefinition
{
    public required string Name { get; init; }

    /// <summary>Stable identity of the card's rules text across printings, used to find its ability script.</summary>
    public string? OracleId { get; init; }
    public ManaCost ManaCost
    {
        get => _manaCost;
        init { _manaCost = value; _colorList = null; }
    }

    private ManaCost _manaCost = ManaCost.Zero;
    public CardType Types { get; init; }
    public Supertype Supertypes { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();
    public int? Power { get; init; }
    public int? Toughness { get; init; }

    /// <summary>Printed loyalty (planeswalkers): it enters with that many loyalty counters (rule 306.5b).</summary>
    public int? Loyalty { get; init; }

    /// <summary>An emblem: an object in the command zone with abilities that work from there (rule 114).</summary>
    public bool IsEmblem { get; init; }
    public string OracleText { get; init; } = "";

    /// <summary>Keyword abilities printed on the card (e.g. "Flying"), as listed by the card data source.</summary>
    public IReadOnlyList<string> Keywords
    {
        get => _keywords;
        init { _keywords = value; _parsedKeywords = null; } // "with" copies the cache: reset it
    }

    private IReadOnlyList<string> _keywords = Array.Empty<string>();

    /// <summary>
    /// Mana this permanent can add with an intrinsic "{T}: Add one mana of these types" ability.
    /// Card data fills it from basic land types and simple "{T}: Add ..." rules text.
    /// </summary>
    public IReadOnlyList<ManaType> TapForMana { get; init; } = Array.Empty<ManaType>();

    /// <summary>Mana added per activation of its mana ability, all of one chosen type ("{T}: Add {C}{C}").</summary>
    public int ManaAmount { get; init; } = 1;

    /// <summary>Mana added per activation, worked out continuously ("Add {G} for each Elf you control").</summary>
    public Quantity? ManaAmountFrom { get; init; }

    /// <summary>Its mana ability adds mana of the color chosen as it entered.</summary>
    public bool ManaFromChosenColor { get; init; }

    /// <summary>"As this enters, choose a color / creature type."</summary>
    public EnterChoice ChooseOnEnter { get; init; }

    /// <summary>"This enters with a [kind] counter for each creature you control of the chosen type."</summary>
    public Abilities.CounterKind? CountersPerChosenType { get; init; }

    /// <summary>Further mana abilities besides <see cref="TapForMana"/> (e.g. a restricted "Add one mana of any color").</summary>
    public IReadOnlyList<ManaOption> ExtraManaOptions { get; init; } = Array.Empty<ManaOption>();

    /// <summary>Its mana ability also sacrifices it: "{T}, Sacrifice this artifact: Add one mana of any color."</summary>
    public bool SacrificeForMana { get; init; }

    /// <summary>What an instant or sorcery does (null for permanents and spells without effects).</summary>
    public SpellAbility? Spell { get; init; }

    /// <summary>Activated and triggered abilities of the permanent.</summary>
    public IReadOnlyList<AbilityDefinition> Abilities { get; init; } = Array.Empty<AbilityDefinition>();

    /// <summary>For an Aura: what it can enchant; casting it targets one such object (rule 303.4a).</summary>
    public TargetSpec? EnchantTarget { get; init; }

    /// <summary>Replacement effect "This permanent enters tapped" (rule 614.1c).</summary>
    public bool EntersTapped { get; init; }

    /// <summary>Replacement effect "This creature enters with N +1/+1 counters on it" (rule 614.1c).</summary>
    public int EntersWithCounters { get; init; }

    /// <summary>"Enters with X +1/+1 counters, where X is ..." worked out as it enters.</summary>
    public Quantity? EntersWithCountersFrom { get; init; }

    /// <summary>The <see cref="EntersWithCounters"/> replacement applies only if this holds ("if you attacked this turn", "if it was kicked").</summary>
    public Condition? EntersWithCountersIf { get; init; }

    /// <summary>"If a spell or ability an opponent controls causes you to discard this card, put it onto the battlefield instead."</summary>
    public bool OntoBattlefieldIfOpponentMakesYouDiscard { get; init; }

    /// <summary>"This creature enters with X +1/+1 counters on it" (X as chosen when it was cast).</summary>
    public bool EntersWithXCounters { get; init; }

    /// <summary>"Enters with twice X +1/+1 counters": how many counters per point of X (with <see cref="EntersWithXCounters"/>).</summary>
    public int XCountersMultiplier { get; init; } = 1;

    /// <summary>Colors for objects without a mana cost to derive them from (tokens). Letters W, U, B, R, G.</summary>
    public IReadOnlyList<string> Colors
    {
        get => _colors;
        init { _colors = value; _colorList = null; }
    }

    private IReadOnlyList<string> _colors = Array.Empty<string>();

    /// <summary>
    /// Identifies the exact picture of this object in the content module's image source (e.g. a token's printing),
    /// for objects whose name alone is ambiguous. Null: look the image up by name.
    /// </summary>
    public string? ImageKey { get; init; }

    /// <summary>A foil copy (how it looks only: a deck can ask for foil copies of printings that exist in foil).</summary>
    public bool Foil { get; init; }

    /// <summary>"This spell can't be countered."</summary>
    public bool CantBeCountered { get; init; }

    /// <summary>"If X is 5 or more, this spell can't be countered": the least X (null: never).</summary>
    public int? CantBeCounteredIfXAtLeast { get; init; }

    /// <summary>"This spell can't be copied."</summary>
    public bool CantBeCopied { get; init; }

    /// <summary>"You have hexproof." (while this permanent is on the battlefield)</summary>
    public bool GivesControllerHexproof { get; init; }

    /// <summary>"Hexproof from [color]": colors (W, U, B, R, G) of opponents' sources that can't target it.</summary>
    public IReadOnlyList<string> HexproofFromColors { get; init; } = Array.Empty<string>();

    /// <summary>"Players can't gain life." (while this permanent is on the battlefield)</summary>
    public bool PlayersCantGainLife { get; init; }

    /// <summary>"Your opponents can't gain life."</summary>
    public bool OpponentsCantGainLife { get; init; }

    /// <summary>Multikicker (702.33c), replicate (702.56), squad (702.157): additional costs that may be paid any number of times.</summary>
    public ManaCost? Multikicker { get; init; }
    public ManaCost? Replicate { get; init; }
    public ManaCost? Squad { get; init; }

    /// <summary>Dash (702.109): cast for this cost, it gains haste and returns to its owner's hand at the next end step.</summary>
    public ManaCost? Dash { get; init; }

    /// <summary>Splice onto instant or sorcery (702.47): revealed from the hand and paid as such a spell is cast, adding this card's effects.</summary>
    public ManaCost? Splice { get; init; }

    /// <summary>Miracle (702.94): cast for this cost when drawn as the first card of the turn.</summary>
    public ManaCost? Miracle { get; init; }

    /// <summary>"As an additional cost to cast this spell, pay X life" (X chosen as it's cast).</summary>
    public bool PayXLife { get; init; }

    /// <summary>"Spend only [color] mana on X": the X of its mana cost is paid with mana of this type only.</summary>
    public Mana.ManaType? XManaType { get; init; }

    /// <summary>"This spell costs {N} more to cast for each target beyond the first" (rule 601.2f).</summary>
    public int ExtraTargetCost { get; init; }

    /// <summary>Flashback with more than mana: "Flashback—{1}{U}, Pay 3 life"; "Exile X cards from your graveyard" (X is the number exiled); a reduction of the flashback cost.</summary>
    public ExtraCost? FlashbackExtra { get; init; }
    public bool FlashbackExilesX { get; init; }
    public Quantity? FlashbackReduction { get; init; }

    /// <summary>A split card (rule 709): its two halves; the second may be an aftermath half (cast only from a graveyard, then exiled).</summary>
    public IReadOnlyList<CardDefinition>? SplitHalves { get; init; }
    public bool Aftermath { get; init; }

    /// <summary>"This artifact can't be sacrificed."</summary>
    public bool CantBeSacrificed { get; init; }

    /// <summary>"Creatures with power greater than the number of cards in your hand can't attack."</summary>
    public bool CantAttackIfPowerAboveHandSize { get; init; }

    /// <summary>"This creature can't attack unless defending player controls [a permanent matching this]" (an Island...).</summary>
    public ObjectFilter? CantAttackUnlessDefenderControls { get; init; }

    /// <summary>Protection from these creature types ("protection from Demons and from Dragons", rule 702.16): from sources with any of them.</summary>
    public IReadOnlyList<string> ProtectionFromSubtypes { get; init; } = Array.Empty<string>();

    /// <summary>"As long as this card is in your graveyard, each [filter] you control enters with an additional +1/+1 counter."</summary>
    public ObjectFilter? GraveyardEnterBonus { get; init; }

    /// <summary>Devour N (702.82): as it enters, its controller may sacrifice any number of [filter: creatures]; it enters with N +1/+1 counters for each.</summary>
    public int Devour { get; init; }
    public ObjectFilter? DevourFilter { get; init; }

    /// <summary>"You may exert this creature as it attacks" (701.39), when <see cref="ExertIf"/> holds (if set).</summary>
    public bool Exert { get; init; }
    public Condition? ExertIf { get; init; }

    /// <summary>Storm (702.40), undaunted (702.125), delve (702.66), conspire (702.78).</summary>
    public bool Storm { get; init; }
    public bool Undaunted { get; init; }
    public bool Delve { get; init; }
    public bool Conspire { get; init; }

    /// <summary>Kicker cost: an optional additional cost paid as the spell is cast (rule 702.33).</summary>
    public ManaCost? Kicker { get; init; }

    /// <summary>Flashback cost: the card may be cast from the graveyard for it, then exiled (rule 702.34).</summary>
    public ManaCost? Flashback { get; init; }

    /// <summary>Ward: opponents' spells and abilities that target it are countered unless they pay this (rule 702.21).</summary>
    public ManaCost? WardMana { get; init; }

    /// <summary>Ward life payment ("Ward—Pay 3 life"); combined with <see cref="WardMana"/> when both are printed.</summary>
    public int WardLife { get; init; }

    /// <summary>Costs paid in addition to the mana cost ("As an additional cost to cast this spell, discard a card").</summary>
    public ExtraCost? AdditionalCost { get; init; }

    /// <summary>"This spell costs {N} less to cast if [condition]" — or for each matching object, see <see cref="CostReduction"/>.</summary>
    public CostReduction? SelfCostReduction { get; init; }

    /// <summary>"This creature attacks each combat if able."</summary>
    public bool AttacksEachCombat { get; init; }

    /// <summary>"This creature doesn't untap during your untap step."</summary>
    public bool DoesntUntap { get; init; }

    /// <summary>"Hexproof from instants" and similar: card types of opponents' sources that can't target it.</summary>
    public CardType HexproofFromTypes { get; init; }

    /// <summary>Additional costs of which the caster pays exactly one ("sacrifice a creature or pay {3}{B}").</summary>
    public IReadOnlyList<CostOption>? AdditionalCostOptions { get; init; }

    /// <summary>An alternative cost ("You may pay {B} rather than pay this spell's mana cost if ...").</summary>
    public AlternativeCost? AlternativeCost { get; init; }

    /// <summary>"You may cast this spell as though it had flash if you pay {2} more to cast it."</summary>
    public ManaCost? FlashExtraCost { get; init; }

    /// <summary>"If this card is in your opening hand, you may begin the game with it on the battlefield."</summary>
    public bool StartsOnBattlefieldFromOpeningHand { get; init; }

    /// <summary>
    /// The opening-hand option (<see cref="StartsOnBattlefieldFromOpeningHand"/>) only for a player who isn't the starting player,
    /// with a counter on it, and exiling cards from the hand if they do.
    /// </summary>
    public bool StartsOnlyIfNotStartingPlayer { get; init; }
    public CounterKind? StartsWithCounter { get; init; }
    public int StartsExilingFromHand { get; init; }

    /// <summary>What happens when mana from this source is spent.</summary>
    public ManaRider ManaRider { get; init; }

    /// <summary>Mana from this source can only be spent on spells matching this filter (rule 106.6).</summary>
    public ObjectFilter? ManaOnlyFor { get; init; }

    /// <summary>Counter kind for <see cref="EntersWithCounters"/> (default +1/+1).</summary>
    public CounterKind EntersWithCounterKind { get; init; } = CounterKind.PlusOnePlusOne;

    /// <summary>Extra cost to cast it from your graveyard ("by removing six counters from among creatures you control").</summary>
    public ExtraCost? GraveyardCastCost { get; init; }

    /// <summary>Characteristic-defining power/toughness ("equal to the number of creatures you control").</summary>
    public Quantity? PowerFrom { get; init; }
    public Quantity? ToughnessFrom { get; init; }

    /// <summary>"Can't be blocked by [filter]" (Walls, Humans, creatures with power 2 or less...).</summary>
    public ObjectFilter? CantBeBlockedBy { get; init; }

    /// <summary>Replacement and rule-changing effects of this permanent while it is on the battlefield.</summary>
    public Replacements Replaces { get; init; }

    /// <summary>"You may have this creature enter as a copy of any [filter] on the battlefield" (rule 707.2, 614.1c).</summary>
    public ObjectFilter? EntersAsCopyOf { get; init; }

    /// <summary>Marks a token definition (tokens cease to exist outside the battlefield, rule 111.7).</summary>
    public bool IsToken { get; init; }

    /// <summary>Ward paid with a non-mana cost ("Ward—Sacrifice a legendary artifact or legendary creature").</summary>
    public ExtraCost? WardCost { get; init; }

    /// <summary>"This creature can't be blocked except by N or more creatures."</summary>
    public int MinimumBlockers { get; init; }

    /// <summary>"Each other creature you control enters with additional +1/+1 counters equal to …".</summary>
    public Quantity? OthersEnterWithCounters { get; init; }

    /// <summary>Cascade instances (rule 702.85).</summary>
    public int Cascade { get; init; }

    /// <summary>Its color identity (rule 903.4): W, U, B, R, G. Empty: the colors of its mana cost.</summary>
    public IReadOnlyList<string> ColorIdentity { get; init; } = Array.Empty<string>();

    /// <summary>With <see cref="EnterChoice.RevealOrTapped"/>: the cards that may be revealed from the hand so it enters untapped.</summary>
    public ObjectFilter? EnterRevealFilter { get; init; }

    /// <summary>The life paid with <see cref="EnterChoice.PayLifeOrTapped"/>.</summary>
    public int EnterLife { get; init; }

    /// <summary>"Gift a [token]" (rule 702.174): an opponent the caster promises it to creates this token before the spell's other effects.</summary>
    public CardDefinition? Gift { get; init; }

    /// <summary>"You may cast this spell as though it had flash if [condition]."</summary>
    public Condition? FlashIf { get; init; }

    /// <summary>"Creatures can't attack you unless their controller pays [cost] for each of those creatures" (while <see cref="AttackTaxIf"/> holds).</summary>
    public ManaCost? AttackTax { get; init; }
    public Condition? AttackTaxIf { get; init; }

    /// <summary>The <see cref="Replacements.AdditionalLandPlay"/> applies only while this holds ("as long as you control another Elf").</summary>
    public Condition? AdditionalLandPlayIf { get; init; }

    /// <summary>"This land enters tapped unless [condition]."</summary>
    public Condition? EntersTappedUnless { get; init; }

    /// <summary>"Equip abilities you activate that target this creature cost {N} less to activate."</summary>
    public int EquipDiscount { get; init; }

    /// <summary>"You may pay {0} rather than pay the equip cost of the first equip ability you activate each turn" (while this holds).</summary>
    public Condition? FreeFirstEquipIf { get; init; }

    /// <summary>Its restricted mana (<see cref="ManaOnlyFor"/>) may also pay for abilities of matching sources.</summary>
    public bool ManaOnlyForAbilitiesToo { get; init; }

    /// <summary>
    /// For an adventurer card (rule 715): its Adventure, an instant or sorcery the card can be cast as instead. After
    /// that spell resolves the card is exiled, and its owner may cast the card itself from exile later.
    /// </summary>
    public CardDefinition? Adventure { get; init; }

    /// <summary>
    /// For a nonmodal double-faced card (rule 712.2): its back face. This definition is the front face; the card is cast
    /// with it up and has only its characteristics everywhere but the battlefield (712.8a). On the battlefield it can
    /// transform (701.27) and then has only the back face's characteristics, whose mana value is the front face's (712.8e).
    /// </summary>
    public CardDefinition? BackFace { get; init; }

    public bool Is(CardType type) => (Types & type) != 0;

    public bool IsCreature() => Is(CardType.Creature);

    /// <summary>A Saga's final chapter number (rule 714.2c); 0 for anything else.</summary>
    public int FinalChapter => Abilities.OfType<TriggeredAbility>().SelectMany(a => a.Chapters).DefaultIfEmpty(0).Max();

    private IReadOnlyList<string>? _colorList;

    /// <summary>Colors: the explicit <see cref="Colors"/>, otherwise those of the mana cost's colored symbols.</summary>
    public IReadOnlyList<string> ColorList => _colorList ??= Colors.Count > 0 ? Colors : ManaCost.Colors();

    private IReadOnlySet<Keyword>? _parsedKeywords;

    /// <summary>Engine-supported keywords among <see cref="Keywords"/>.</summary>
    public IReadOnlySet<Keyword> KeywordAbilities => _parsedKeywords ??= Cards.Keywords.ParseAll(Keywords);
}
