// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Reflection;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Bots;

/// <summary>The kinds of things a deck can be built around.</summary>
public enum ThemeKind
{
    /// <summary>A creature type ("Goblins you control get +1/+1", "whenever a Rat enters").</summary>
    Subtype,
    /// <summary>Artifacts or enchantments ("whenever an artifact enters", "for each artifact you control").</summary>
    CardTypes,
    /// <summary>Instants and sorceries ("whenever you cast an instant or sorcery spell").</summary>
    Spells,
    /// <summary>Many creatures: tokens, anthems, "whenever a creature enters", sacrifice outlets.</summary>
    GoWide,
    /// <summary>+1/+1 counters.</summary>
    Counters,
    /// <summary>Creatures dying or being sacrificed.</summary>
    Death,
    /// <summary>Gaining life.</summary>
    LifeGain,
    /// <summary>Drawing cards.</summary>
    Draw,
    /// <summary>Lands entering.</summary>
    Landfall,
    /// <summary>Cards in the graveyard.</summary>
    Graveyard,
}

/// <summary>One theme: its kind, and the creature type or card types for the kinds that have one.</summary>
public readonly record struct Theme(ThemeKind Kind, string? Subtype = null, CardType Types = 0);

/// <summary>
/// What a card rewards (its payoff themes) and what it brings to a theme (how much it enables it), read from the card's
/// abilities. Used for the bot's own deck and for any board it can see.
/// </summary>
public static class Synergy
{
    private static readonly ConcurrentDictionary<CardDefinition, IReadOnlyList<Theme>> PayoffCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>The themes the card rewards.</summary>
    public static IReadOnlyList<Theme> Payoffs(CardDefinition card) => PayoffCache.GetOrAdd(card, c => FindPayoffs(c).Distinct().ToList());

    /// <summary>How much the card feeds a theme: about one for a card that counts once, more for one that makes several.</summary>
    public static double Enables(CardDefinition card, Theme theme)
    {
        var effects = AllAbilities(card).SelectMany(EffectTree.All).ToList();
        double TokenCount(Func<CardDefinition, bool> which) => effects.OfType<CreateTokens>()
            .Where(t => t.Controller.Kind == SubjectKind.You && which(t.Token)).Sum(t => Math.Clamp(t.Count.Estimate, 1, 3));
        switch (theme.Kind)
        {
            case ThemeKind.Subtype:
                double own = card.Subtypes.Contains(theme.Subtype!, StringComparer.OrdinalIgnoreCase)
                             || card.IsCreature() && card.Keywords.Contains("Changeling", StringComparer.OrdinalIgnoreCase) ? 1 : 0;
                return own + 0.8 * TokenCount(t => t.Subtypes.Contains(theme.Subtype!, StringComparer.OrdinalIgnoreCase));
            case ThemeKind.CardTypes:
                return ((card.Types & theme.Types) != 0 ? 1 : 0) + 0.6 * TokenCount(t => (t.Types & theme.Types) != 0);
            case ThemeKind.Spells:
                return card.Is(CardType.Instant) || card.Is(CardType.Sorcery) ? 1 : 0;
            case ThemeKind.GoWide:
                return 0.8 * TokenCount(t => t.IsCreature()) + (card.IsCreature() && ManaValueOf(card) <= 2 ? 0.3 : 0);
            case ThemeKind.Counters:
                return effects.OfType<AddCounters>().Count(a => a.Kind == CounterKind.PlusOnePlusOne)
                       + (card.EntersWithCounters > 0 || card.EntersWithXCounters ? 0.5 : 0)
                       + (card.Keywords.Any(k => k is "Modular" or "Evolve" or "Outlast" or "Adapt" or "Backup" or "Bolster" or "Support") ? 0.7 : 0);
            case ThemeKind.Death:
                return 0.6 * TokenCount(t => t.IsCreature())
                       + (IsSacrificeOutlet(card) ? 1 : 0)
                       + effects.OfType<Sacrifice>().Count(s => s.Who.Kind == SubjectKind.You) * 0.7
                       + (card.IsCreature() && card.Abilities.OfType<TriggeredAbility>().Any(t => t.Trigger == TriggerEvent.Dies) ? 0.6 : 0);
            case ThemeKind.LifeGain:
                return effects.OfType<GainLife>().Count(g => g.Who.Kind == SubjectKind.You)
                       + (card.Keywords.Contains("Lifelink", StringComparer.OrdinalIgnoreCase) ? 1 : 0);
            case ThemeKind.Draw:
                return effects.OfType<DrawCards>().Where(d => d.Who.Kind == SubjectKind.You).Sum(d => Math.Clamp(d.Count.Estimate, 1, 3));
            case ThemeKind.Landfall:
                return (card.Is(CardType.Land) ? 0.5 : 0)
                       + effects.OfType<SearchLibrary>().Count(s => (s.Filter.Types & CardType.Land) != 0 && s.To == Arcanum.Engine.State.Zone.Battlefield);
            case ThemeKind.Graveyard:
                return effects.OfType<Mill>().Count(m => m.Who.Kind == SubjectKind.You)
                       + effects.Count(e => e is Surveil)
                       + (card.Abilities.OfType<TriggeredAbility>().Any(t => t.Trigger == TriggerEvent.Dies) ? 0.3 : 0);
        }
        return 0;
    }

    /// <summary>"Sacrifice a creature: ..." on a permanent: an activated ability that sacrifices something other than itself.</summary>
    public static bool IsSacrificeOutlet(CardDefinition card) =>
        card.Abilities.OfType<ActivatedAbility>().Any(a => a.Cost.Extra?.Sacrifice is { } f && (f.Types == 0 || (f.Types & CardType.Creature) != 0 || f.Token == true));

    /// <summary>The card's own abilities: its spell, its permanent abilities, its Adventure and back face.</summary>
    public static IEnumerable<AbilityDefinition> AllAbilities(CardDefinition card)
    {
        if (card.Spell is { } spell) yield return spell;
        foreach (var a in card.Abilities)
        {
            yield return a;
            if (a is StaticAbility { GrantsAbilities: { } granted }) foreach (var g in granted) yield return g;
        }
        if (card.Adventure?.Spell is { } adventure) yield return adventure;
    }

    private static int ManaValueOf(CardDefinition card) => card.ManaCost.ManaValue;

    private static IEnumerable<Theme> FindPayoffs(CardDefinition card)
    {
        foreach (var ability in AllAbilities(card))
        {
            switch (ability)
            {
                case TriggeredAbility t:
                    foreach (var theme in TriggerThemes(t)) yield return theme;
                    break;
                case StaticAbility s when s.Affects.Scope is AffectedScope.YourCreatures or AffectedScope.YourPermanents && !s.GivesControl:
                    var subtype = s.Affects.Subtype ?? s.Filter?.Subtype;
                    if (subtype is not null) yield return new Theme(ThemeKind.Subtype, subtype);
                    else if (s.Filter?.HasCounters == true) yield return new Theme(ThemeKind.Counters);
                    else if (s.Filter is { Types: var types } && (types & (CardType.Artifact | CardType.Enchantment)) != 0)
                        yield return new Theme(ThemeKind.CardTypes, Types: types & (CardType.Artifact | CardType.Enchantment));
                    else if (s.Power + s.Toughness > 0 || s.GrantedKeywords.Count > 0 || s.PowerBonus is not null) yield return new Theme(ThemeKind.GoWide);
                    break;
                case SpellCostReduction r:
                    if (r.Spells.Subtype is { } st) yield return new Theme(ThemeKind.Subtype, st);
                    else if ((r.Spells.Types & (CardType.Instant | CardType.Sorcery)) != 0) yield return new Theme(ThemeKind.Spells);
                    else if ((r.Spells.Types & (CardType.Artifact | CardType.Enchantment)) != 0)
                        yield return new Theme(ThemeKind.CardTypes, Types: r.Spells.Types & (CardType.Artifact | CardType.Enchantment));
                    break;
            }
            // "For each Goblin you control", "for each card in your graveyard", "for each creature with a +1/+1 counter".
            foreach (var q in ability.Effects.SelectMany(e => QuantitiesOf(e)).Concat(StaticQuantities(ability)))
            {
                if (q.Kind is QuantityKind.GraveyardCount or QuantityKind.CardsInAllGraveyards) { yield return new Theme(ThemeKind.Graveyard); continue; }
                if (q.Kind != QuantityKind.PermanentCount || q.Filter is not { } f || f.Controller == ControllerFilter.Opponent) continue;
                if (FilterTheme(f, creaturesMeanWide: true) is { } theme) yield return theme;
            }
        }
        if (IsSacrificeOutlet(card)) yield return new Theme(ThemeKind.GoWide);
        if (card.DoublesCountersOnYour is not null) yield return new Theme(ThemeKind.Counters);
        if (card.GraveyardCastFilter is not null) yield return new Theme(ThemeKind.Graveyard);
    }

    private static IEnumerable<Theme> TriggerThemes(TriggeredAbility t)
    {
        var f = t.Filter;
        if (f?.Controller == ControllerFilter.Opponent) yield break;
        switch (t.Trigger)
        {
            case TriggerEvent.CreatureEnters or TriggerEvent.PermanentEnters:
                yield return FilterTheme(f, creaturesMeanWide: true) ?? new Theme(ThemeKind.GoWide);
                break;
            case TriggerEvent.YouCastSpell or TriggerEvent.YouCastOrCopySpell:
                if (f is null) break;
                if (f.Subtype is { } s) yield return new Theme(ThemeKind.Subtype, s);
                else if ((f.Types & (CardType.Instant | CardType.Sorcery)) != 0) yield return new Theme(ThemeKind.Spells);
                else if ((f.Types & (CardType.Artifact | CardType.Enchantment)) != 0)
                    yield return new Theme(ThemeKind.CardTypes, Types: f.Types & (CardType.Artifact | CardType.Enchantment));
                else if ((f.ExcludedTypes & CardType.Creature) != 0) yield return new Theme(ThemeKind.Spells);
                break;
            case TriggerEvent.CreatureAttacks or TriggerEvent.YouAttackWithMatching:
                if (f?.Subtype is { } attacker) yield return new Theme(ThemeKind.Subtype, attacker);
                break;
            case TriggerEvent.TokenCreated or TriggerEvent.TokensCreated:
                yield return new Theme(ThemeKind.GoWide);
                break;
            case TriggerEvent.CountersPlaced:
                yield return new Theme(ThemeKind.Counters);
                break;
            case TriggerEvent.CreatureDies or TriggerEvent.YouSacrifice or TriggerEvent.CreatureLeaves:
                yield return f?.Subtype is { } dying ? new Theme(ThemeKind.Subtype, dying) : new Theme(ThemeKind.Death);
                break;
            case TriggerEvent.YouGainLife or TriggerEvent.YouGainOrLoseLife:
                yield return new Theme(ThemeKind.LifeGain);
                break;
            case TriggerEvent.YouDrawCard:
                yield return new Theme(ThemeKind.Draw);
                break;
            case TriggerEvent.LandEnters or TriggerEvent.YouPlayLand:
                yield return new Theme(ThemeKind.Landfall);
                break;
            case TriggerEvent.CardPutIntoYourGraveyard or TriggerEvent.LeavesGraveyard or TriggerEvent.CardPutIntoGraveyardNotFromBattlefield:
                yield return new Theme(ThemeKind.Graveyard);
                break;
        }
    }

    /// <summary>The theme a filter of permanents stands for: its creature type, artifacts or enchantments, counters, or creatures in general.</summary>
    private static Theme? FilterTheme(ObjectFilter? f, bool creaturesMeanWide)
    {
        if (f is null) return null;
        if (f.Subtype is { } s) return new Theme(ThemeKind.Subtype, s);
        if (f.HasCounters == true) return new Theme(ThemeKind.Counters);
        if ((f.Types & (CardType.Artifact | CardType.Enchantment)) != 0 && (f.Types & CardType.Creature) == 0)
            return new Theme(ThemeKind.CardTypes, Types: f.Types & (CardType.Artifact | CardType.Enchantment));
        if (f.Token == true || creaturesMeanWide && (f.Types & CardType.Creature) != 0) return new Theme(ThemeKind.GoWide);
        if ((f.Types & CardType.Land) != 0) return new Theme(ThemeKind.Landfall);
        return null;
    }

    private static IEnumerable<Quantity> StaticQuantities(AbilityDefinition ability) => ability is StaticAbility s
        ? new[] { s.PowerBonus, s.ToughnessBonus }.OfType<Quantity>()
        : Array.Empty<Quantity>();

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> QuantityProperties = new();

    /// <summary>The quantities an effect (and the effects inside it) is measured by, found through its properties.</summary>
    private static IEnumerable<Quantity> QuantitiesOf(Effect effect)
    {
        foreach (var e in EffectTree.All(new SpellAbility { Effects = new[] { effect } }))
        {
            var props = QuantityProperties.GetOrAdd(e.GetType(), t => t.GetProperties().Where(p => p.PropertyType == typeof(Quantity) && p.GetIndexParameters().Length == 0).ToArray());
            foreach (var p in props)
                if (p.GetValue(e) is Quantity q) yield return q;
        }
    }
}
