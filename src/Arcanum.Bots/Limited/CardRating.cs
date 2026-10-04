// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Bots.Limited;

/// <summary>
/// How good a card is in a limited deck, roughly 0–10: efficient creatures, removal and card advantage score high;
/// expensive or narrow cards low. Used by draft bots and limited deck building.
/// </summary>
public static class CardRating
{
    public static double Rate(CardDefinition card, string rarity = "")
    {
        if (card.Is(CardType.Land)) return LandValue(card);
        double score = card.IsCreature() ? CreatureScore(card) : SpellScore(card);
        int mv = card.ManaCost.ManaValue;
        if (mv >= 7) score -= (mv - 6) * 0.6; // hard to cast in a 17-land deck
        score += rarity switch { "mythic" => 0.6, "rare" => 0.4, _ => 0 };
        return Math.Clamp(score, 0, 10);
    }

    private static double LandValue(CardDefinition card)
    {
        if ((card.Supertypes & Supertype.Basic) != 0) return 0;
        bool fetches = AllEffects(card).Any(e => e is SearchLibrary);
        return card.TapForMana.Distinct().Count() >= 2 || fetches ? 3 : 1.5;
    }

    private static double CreatureScore(CardDefinition card)
    {
        int mv = Math.Max(1, card.ManaCost.ManaValue);
        double stats = (card.Power ?? 0) * 1.1 + (card.Toughness ?? 0) * 0.8;
        foreach (var k in card.KeywordAbilities)
            stats += k switch
            {
                Keyword.Flying => 1.4, Keyword.Deathtouch => 1.3, Keyword.DoubleStrike => 2, Keyword.FirstStrike => 0.8, Keyword.Lifelink => 0.8,
                Keyword.Trample => 0.4, Keyword.Vigilance => 0.4, Keyword.Menace => 0.7, Keyword.Hexproof => 0.8, Keyword.Indestructible => 1.5,
                Keyword.Haste => 0.5, Keyword.Flash => 0.5, Keyword.Defender => -1.5, Keyword.CantBlock => -0.8, _ => 0.2,
            };
        double expected = 1.7 * mv + 0.8;
        double score = 4.5 + (stats - expected) * 0.7;
        score += AbilityScore(card) * 0.8;
        return score;
    }

    private static double SpellScore(CardDefinition card)
    {
        double score = 2.5 + AbilityScore(card);
        int mv = card.ManaCost.ManaValue;
        if (mv >= 5) score -= (mv - 4) * 0.4;
        return score;
    }

    /// <summary>What the card's spell and abilities do: removal, card draw, tokens, pump.</summary>
    private static double AbilityScore(CardDefinition card)
    {
        double score = 0;
        foreach (var ability in (card.Spell is null ? card.Abilities : card.Abilities.Prepend(card.Spell)))
        {
            bool targetsCreatures = ability.Targets.Any(t => t.Kind is TargetKind.Creature or TargetKind.Any or TargetKind.CreatureOrPlaneswalker or TargetKind.Permanent);
            foreach (var effect in Flatten(ability.Effects))
            {
                score += effect switch
                {
                    Destroy or ExileIt or ExileUntilSourceLeaves when targetsCreatures => 3.5,
                    DealDamage d when targetsCreatures => 1.5 + Math.Min(d.Amount.Estimate, 5) * 0.4,
                    Fight => 2.5,
                    DealDamage => 0.8,
                    ReturnToHand when targetsCreatures => 1.2,
                    PumpUntilEndOfTurn p when p.Power.Estimate + p.Toughness.Estimate < 0 => 2.5,
                    TapIt => 0.6,
                    CounterSpell => 1.5,
                    DrawCards d => 0.9 * Math.Max(1, d.Count.Estimate),
                    CreateTokens t => 0.9 * Math.Max(1, t.Count.Estimate) * (1 + ((t.Token.Power ?? 0) + (t.Token.Toughness ?? 0)) / 4.0),
                    AddCounters a when a.Kind == CounterKind.PlusOnePlusOne => 0.5 * Math.Max(1, a.Count.Estimate),
                    PumpUntilEndOfTurn => 0.8,
                    GainLife => 0.3,
                    SearchLibrary => 0.6,
                    _ => 0.2,
                };
            }
            if (ability is ActivatedAbility) score += 0.3;
            if (ability is StaticAbility st && st.Affects.Scope == AffectedScope.YourCreatures && st.Power + st.Toughness > 0) score += 1.2;
        }
        return score;
    }

    private static IEnumerable<Effect> Flatten(IEnumerable<Effect> effects) =>
        effects.SelectMany(e => e is IfThen i ? Flatten(i.Then).Concat(Flatten(i.Else ?? Array.Empty<Effect>())) : new[] { e });

    private static IEnumerable<Effect> AllEffects(CardDefinition card) =>
        (card.Spell is null ? card.Abilities : card.Abilities.Prepend(card.Spell)).SelectMany(a => Flatten(a.Effects));

    /// <summary>The colors a card needs (W, U, B, R, G); hybrid symbols count as either color.</summary>
    public static IReadOnlySet<ManaType> ColoredPips(CardDefinition card) => card.ManaCost.Pips.Where(p => p != ManaType.Colorless).ToHashSet();

    /// <summary>Whether a deck of these colors can cast the card (every colored symbol, or one half of each hybrid symbol).</summary>
    public static bool Castable(CardDefinition card, IReadOnlySet<ManaType> colors) =>
        ColoredPips(card).All(colors.Contains) && card.ManaCost.Hybrid.All(h => colors.Contains(h.First) || colors.Contains(h.Second));
}
