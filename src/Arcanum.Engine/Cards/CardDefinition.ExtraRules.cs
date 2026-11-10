// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

/// <summary>More card-wide rules a permanent has while it is on the battlefield with its abilities (<see cref="Replacements"/> is full).</summary>
[Flags]
public enum ExtraRules : long
{
    None = 0,
    /// <summary>"You may look at face-down creatures your opponents control any time."</summary>
    LookAtOpponentsFaceDownCreatures = 1,
    /// <summary>"Creature spells you cast have convoke."</summary>
    YourCreatureSpellsHaveConvoke = 2,
    /// <summary>"Untap each creature you control during each other player's untap step."</summary>
    UntapYourCreaturesDuringOthersUntap = 4,
    /// <summary>"Creatures your opponents control can be the targets of spells and abilities as though they didn't have hexproof. Ward abilities of those creatures don't trigger."</summary>
    OpponentsCreaturesTargetableAndWardless = 8,
    /// <summary>"If a card would be put into an opponent's graveyard from anywhere, exile it instead."</summary>
    OpponentsCardsExiledInsteadOfGraveyard = 16,
    /// <summary>"You don't lose the game for having 0 or less life."</summary>
    NoLossForZeroLife = 32,
    /// <summary>"You may pay {W}{U}{B}{R}{G} rather than pay the mana cost for spells you cast."</summary>
    YourSpellsForFiveColors = 64,
    /// <summary>"If a source you control would deal damage to an opponent, prevent that damage and each opponent mills that many cards."</summary>
    YourDamageToOpponentsMillsInstead = 128,
    /// <summary>"If a card you didn't control would be put into an opponent's graveyard from anywhere, exile it instead" (exiled with this permanent).</summary>
    OpponentsCardsExiledWithThis = 256,
    /// <summary>"During your turn, you may play cards exiled with this. If you cast a spell this way, pay life equal to its mana value rather than pay its mana cost."</summary>
    PlayExiledWithThisOnYourTurnForLife = 512,
    /// <summary>"Delirium — If a source you control would deal noncombat damage to a permanent or player while there are four or more card types among cards in your graveyard, it deals double that damage instead."</summary>
    YourNoncombatDamageDoubledWithDelirium = 1024,
    /// <summary>"Creatures you control are the chosen type in addition to their other types. The same is true for creature spells you control and creature cards you own that aren't on the battlefield."</summary>
    YourCreaturesAreChosenType = 2048,
    /// <summary>An Aura's "as long as enchanted creature is face down, it can't be turned face up".</summary>
    EnchantedCantBeTurnedFaceUp = 4096,
    /// <summary>"Delirium — As long as there are four or more card types among cards in your graveyard, each opponent's maximum hand size is equal to seven minus the number of those card types."</summary>
    OpponentsHandSizeSevenMinusCardTypes = 8192,
    /// <summary>An Aura's "enchanted player can't gain life".</summary>
    EnchantedPlayerCantGainLife = 16384,
    /// <summary>"Once each turn, you may pay {0} rather than pay the mana cost for a spell you cast from exile."</summary>
    FreeFromExileOncePerTurn = 32768,
    /// <summary>"Stun counters can't be removed from permanents your opponents control."</summary>
    OpponentsStunCountersStay = 65536,
    /// <summary>"Once during each of your turns, you may cast a spell from your hand or the top of your library without paying its mana cost."</summary>
    FreeFromHandOrTopOncePerTurn = 131072,
    /// <summary>"During your turn, if an opponent lost life this turn, you may play lands and cast spells from among cards exiled with this."</summary>
    PlayExiledWithThisOnYourTurnIfOpponentLostLife = 262144,
    /// <summary>"You may play Forests from your graveyard."</summary>
    PlayForestsFromGraveyard = 524288,
    /// <summary>"Once during each of your turns, you may cast a spell from your hand by paying life equal to its mana value rather than paying its mana cost."</summary>
    LifeCastFromHandOncePerTurn = 1048576,
    /// <summary>"Once during each of your turns, you may cast an enchantment spell by paying life equal to its mana value rather than paying its mana cost."</summary>
    LifeCastEnchantmentOncePerTurn = 2097152,
    /// <summary>"Nonland permanents you control and permanent spells you control are enchantments in addition to their other types" (the spell part; a static gives the permanents).</summary>
    YourNonlandPermanentsAndPermanentSpellsAreEnchantments = 4194304,
    /// <summary>"Each enchantment card in your graveyard has escape. The escape cost is equal to the card's mana cost plus exile three other cards from your graveyard."</summary>
    GraveyardEnchantmentsHaveEscape = 8388608,
    /// <summary>"Each enchantment card in your hand has miracle. Its miracle cost is equal to its mana cost reduced by {4}."</summary>
    HandEnchantmentsHaveMiracle = 16777216,
    /// <summary>"Whenever a player taps a land for mana, if this creature is tapped, that player adds one mana of any type that land produced. Then that land deals 1 damage to that player."</summary>
    LandsTappedAddOneAndDamageWhileTapped = 33554432,
    /// <summary>"Each instant and sorcery card in your hand has miracle {2}."</summary>
    HandInstantsAndSorceriesHaveMiracleTwo = 67108864,
    /// <summary>"Once during each of your turns, you may cast an instant or sorcery spell from your hand without paying its mana cost."</summary>
    FreeInstantOrSorceryFromHandOncePerTurn = 134217728,
    /// <summary>"If an effect causes you to discard a card, discard it, but you may put it on top of your library instead of into your graveyard."</summary>
    EffectDiscardsMayGoOnTop = 268435456,
    /// <summary>"You can't cast permanent spells."</summary>
    YouCantCastPermanentSpells = 536870912,
}

public sealed partial record CardDefinition
{
    /// <summary>Card-wide rules beyond <see cref="Replaces"/>.</summary>
    public ExtraRules ExtraRules { get; init; }
}
