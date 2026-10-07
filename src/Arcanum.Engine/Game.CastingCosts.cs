// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// What casting a spell costs (rule 601.2b, 601.2f-h), worked out in one place: the ways a card can be paid for, the
/// total cost of a way with what's been announced, and whether that can be paid. Both the list of legal actions and the
/// casting procedure use it, so a spell is offered exactly when it can be paid for.
/// </summary>
public sealed partial class Game
{
    /// <summary>What a spell is cast for (rule 118.9): its mana cost or one alternative cost.</summary>
    private enum CastingWayKind { ManaCost, Dash, Alternative, WithoutPaying, PayLife }

    /// <summary>
    /// One way to pay for casting a card: the cost it's cast for (its mana cost, or the flashback or miracle cost it's being
    /// cast with, or one alternative cost) as mana and life, before additional costs, increases and reductions.
    /// </summary>
    private sealed record CastingWay(CastingWayKind Kind, ManaCost Mana, int Life = 0);

    /// <summary>Everything announced for a spell (601.2b) that changes what casting it costs.</summary>
    private sealed record CastingChoices(CastingWay Way)
    {
        /// <summary>The chosen one of the additional costs the caster pays exactly one of.</summary>
        public CostOption? Option { get; init; }
        /// <summary>Paying extra to cast it as though it had flash.</summary>
        public bool FlashExtra { get; init; }
        public bool Kicked { get; init; }
        public int KickCount { get; init; }
        public int ReplicateCount { get; init; }
        public int SquadCount { get; init; }
        public IReadOnlyList<Card> Spliced { get; init; } = Array.Empty<Card>();
        public bool Conspire { get; init; }
        public int X { get; init; }
    }

    /// <summary>The ways the card can be cast for right now (118.9), whether or not they can be paid.</summary>
    private List<CastingWay> CastingWays(Card card, PlayerId caster)
    {
        if ((card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.WithoutPaying)) || _castFree.Contains(card.Id))
            return new() { new(CastingWayKind.WithoutPaying, ManaCost.Zero) };
        if (PaysLife(card)) return new() { new(CastingWayKind.PayLife, ManaCost.Zero, card.Definition.ManaCost.ManaValue) };
        if (_miracleCost.TryGetValue(card.Id, out var miracle)) return new() { new(CastingWayKind.ManaCost, miracle) };
        if (card.Zone == Zone.Graveyard && card.Definition.Flashback is { } flashback && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version))
        {
            if (card.Definition.FlashbackReduction is { } reduction)
                flashback = flashback.MinusGeneric(Math.Max(0, Eval(reduction, new EffectContext(caster, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))));
            return new() { new(CastingWayKind.ManaCost, flashback) };
        }
        var ways = new List<CastingWay> { new(CastingWayKind.ManaCost, card.Definition.ManaCost) };
        if (card.Zone == Zone.Hand && card.Definition.Dash is { } dash) ways.Add(new(CastingWayKind.Dash, dash)); // 702.109a
        if (card.Definition.AlternativeCost is { } alt && (alt.If is null || Holds(alt.If, caster, card))) ways.Add(new(CastingWayKind.Alternative, alt.Cost));
        if (card.Zone == Zone.Hand && Has(caster, Replacements.CastFromHandFree)) ways.Add(new(CastingWayKind.WithoutPaying, ManaCost.Zero));
        return ways;
    }

    /// <summary>
    /// The total cost (rule 601.2f): the way's cost with X (zero when not paying the mana cost, 107.3b), plus commander tax
    /// (903.8), additional mana costs and increases, minus reductions, which reduce generic mana only, X included. Before
    /// targets are chosen (<paramref name="targets"/> null), a reduction for what the spell targets assumes the best legal target.
    /// </summary>
    private ManaCost TotalCastingCost(Card card, PlayerId caster, CastingChoices c, IReadOnlyList<ChosenTarget>? targets)
    {
        var cost = c.Way.Mana.WithX(c.X);
        if (card.Zone == Zone.Command && Config.Commander is { } rules)
            cost = cost.PlusGeneric(rules.TaxPerCast * State.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id));
        if (c.FlashExtra && card.Definition.FlashExtraCost is { } flash) cost = cost.Plus(flash);
        if (c.Option?.Mana is { } optionMana) cost = cost.Plus(optionMana);
        if (c.Kicked && card.Definition.Kicker is { } kicker) cost = cost.Plus(kicker);
        for (int i = 0; i < c.KickCount; i++) cost = cost.Plus(card.Definition.Multikicker!);
        for (int i = 0; i < c.ReplicateCount; i++) cost = cost.Plus(card.Definition.Replicate!);
        for (int i = 0; i < c.SquadCount; i++) cost = cost.Plus(card.Definition.Squad!);
        foreach (var other in c.Spliced) cost = cost.Plus(other.Definition.Splice!);
        foreach (var taxer in State.Battlefield.Select(State.GetCard))
            foreach (var increase in taxer.Abilities.OfType<SpellCostIncrease>())
                if (Matches(increase.Spells with { Controller = ControllerFilter.Any }, card, caster, taxer, taxer.Controller)) cost = cost.PlusGeneric(increase.Amount);
        // "Costs {1} more for each target beyond the first": before targets are chosen, as if it had one.
        if (card.Definition.ExtraTargetCost > 0 && targets is not null)
            cost = cost.PlusGeneric(card.Definition.ExtraTargetCost * Math.Max(0, targets.Count(t => !t.Target.IsNone) - 1));
        int genericBefore = cost.Generic;
        cost = cost.MinusGeneric(CostReductionFor(card, caster, targets));
        // "Spend only black mana on X": the X part of the generic mana (what reductions left of it) is paid with that type only.
        if (card.Definition.XManaType is { } xType && c.X > 0 && c.Way.Mana.XCount > 0)
        {
            int xPart = c.X * c.Way.Mana.XCount;
            int reduced = genericBefore - cost.Generic;
            int xLeft = Math.Max(0, xPart - Math.Max(0, reduced - (genericBefore - xPart)));
            cost = new ManaCost(cost.Generic - xLeft, cost.Pips.Concat(Enumerable.Repeat(xType, xLeft)).ToList(), cost.Hybrid);
        }
        // "Mana of any type can be spent" to pay it.
        bool anyType = (card.Is(CardType.Creature) && Has(caster, Replacements.CreaturesFromLibraryTop))
                       || (card.Zone == Zone.Exile && card.CounterCount(CounterKind.Stash) > 0 && Has(caster, Replacements.PlayStashedCards))
                       || (card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.AnyManaType));
        return anyType ? new ManaCost(cost.ManaValue, Array.Empty<ManaType>()) : cost;
    }

    /// <summary>Cast with flashback (from the graveyard, exiled when it leaves the stack, 702.34a).</summary>
    private bool IsFlashbackCast(Card card) =>
        card.Zone == Zone.Graveyard
        && (card.Definition.Flashback is not null || State.FlashbackGranted.Any(p => p.Card == card.Id && p.Version == card.Version))
        && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version);

    /// <summary>The non-mana costs the announcements commit to (601.2b): additional costs and those of casting from the graveyard.</summary>
    private List<ExtraCost> NonManaCastingCosts(Card card, CastingChoices c)
    {
        var costs = new List<ExtraCost?> { card.Definition.AdditionalCost, c.Option?.Extra };
        if (card.Zone == Zone.Graveyard) costs.Add(GraveyardCost(card));
        if (IsFlashbackCast(card))
        {
            costs.Add(card.Definition.FlashbackExtra);
            if (card.Definition.FlashbackExilesX && c.X > 0) costs.Add(new ExtraCost { ExileFromGraveyard = c.X });
        }
        return costs.OfType<ExtraCost>().ToList();
    }

    /// <summary>
    /// Whether the caster can pay everything the announcements commit to (601.2g-h): the total mana cost (delve exiling
    /// cards from the graveyard for generic mana), life, conspire and every non-mana cost.
    /// </summary>
    private bool CanPayCastingCost(Card card, PlayerId caster, CastingChoices c, ManaCost total)
    {
        var player = State.GetPlayer(caster);
        var extras = NonManaCastingCosts(card, c);
        if (!extras.All(e => CanPayExtra(caster, e, card.Id))) return false;
        int life = c.Way.Life + extras.Sum(e => e.PayLife) + (card.Definition.PayXLife ? c.X : 0);
        if (life > 0 && (life > player.Life || player.CantLoseLifeTurn == State.TurnNumber)) return false; // rule 119.4, 119.8
        int graveyard = player.Graveyard.Count(id => id != card.Id) - extras.Sum(e => e.ExileFromGraveyard);
        if (graveyard < 0) return false;
        if (c.Conspire && ConspireCandidates(card, caster).Count < 2) return false;
        if (card.Definition.Delve) total = total.MinusGeneric(graveyard); // 702.66a
        return CanPayCastingMana(card, caster, c, total);
    }

    private bool CanPayCastingMana(Card card, PlayerId caster, CastingChoices c, ManaCost mana) =>
        mana.Variants().Any(v => ManaPayment.FindPlan(State, caster, v, usable: SpellManaUsable(card, caster, c), unitUsable: UnitUsableFor(card, isAbility: false)) is not null);

    /// <summary>Whether the announcements can be paid for, with the targets chosen (or, before that, the best legal ones).</summary>
    private bool CanPayToCast(Card card, PlayerId caster, CastingChoices c, IReadOnlyList<ChosenTarget>? targets = null) =>
        CanPayCastingCost(card, caster, c, TotalCastingCost(card, caster, c, targets));

    /// <summary>
    /// The ways the caster could pay for casting the card now with nothing optional announced and X = 0: one for each way
    /// and choice of additional cost that can be paid. The card can be cast only if there is one (601.2h).
    /// </summary>
    private List<CastingChoices> PayableCastingWays(Card card, PlayerId caster, bool flashExtra)
    {
        var options = card.Definition.AdditionalCostOptions is { Count: > 0 } listed ? listed.Select(o => (CostOption?)o).ToList() : new List<CostOption?> { null };
        return CastingWays(card, caster)
            .SelectMany(way => options.Select(o => new CastingChoices(way) { Option = o, FlashExtra = flashExtra }))
            .Where(c => CanPayToCast(card, caster, c)).ToList();
    }

    /// <summary>Whether the card may be cast now without paying extra for flash: an instant, flash, or a permission to cast it as though it had flash.</summary>
    private bool TimingAllows(Card card, PlayerId caster, bool sorcerySpeed) =>
        card.Is(CardType.Instant) || card.Definition.KeywordAbilities.Contains(Keyword.Flash) || sorcerySpeed
        || Has(caster, Replacements.YourSpellsHaveFlash)
        || (card.Definition.FlashIf is { } flashIf && Holds(flashIf, caster, card))
        || GrantedFlash(card, caster);

    /// <summary>A legendary instant or sorcery needs a legendary creature or planeswalker (rule 205.4d).</summary>
    private bool LegendarySpellAllowed(Card card, PlayerId caster) =>
        (card.Definition.Supertypes & Supertype.Legendary) == 0 || !(card.Is(CardType.Instant) || card.Is(CardType.Sorcery))
        || State.PermanentsControlledBy(caster).Any(c => (c.Supertypes & Supertype.Legendary) != 0 && (c.IsCreature || c.Is(CardType.Planeswalker)));

    /// <summary>Whether the card can be cast now, timing aside: its legality, a legal target for each requirement and a way to pay.</summary>
    private bool CanBeCast(Card card, PlayerId caster, bool flashExtra) =>
        LegendarySpellAllowed(card, caster) && HasLegalTargets(CastingTargets(card.Definition), caster, card.Id)
        && PayableCastingWays(card, caster, flashExtra).Count > 0;

    /// <summary>Untapped creatures the caster controls that share a color with the spell (conspire, 702.78a).</summary>
    private List<Card> ConspireCandidates(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Where(c => c.IsCreature && !c.Tapped && c.Colors.Any(card.Colors.Contains)).ToList();

    /// <summary>
    /// Mana sources usable for a spell, keeping back sources that sacrifice themselves for mana (Treasures) when they're
    /// needed for a sacrifice the spell also costs, and creatures with mana abilities when they're needed for conspire.
    /// </summary>
    private ManaPayment.OptionUsable SpellManaUsable(Card card, PlayerId caster, CastingChoices c)
    {
        var usable = UsableFor(card, isAbility: false);
        var reserved = new HashSet<CardId>();
        foreach (var extra in new[] { card.Definition.AdditionalCost, c.Option?.Extra })
        {
            if (extra?.Sacrifice is not { } filter) continue;
            var candidates = SacrificeCandidates(caster, filter, card.Id);
            var selfSacrificing = candidates.Where(s => s.Definition.SacrificeForMana).ToList();
            if (candidates.Count - selfSacrificing.Count < extra.SacrificeCount) reserved.UnionWith(selfSacrificing.Select(s => s.Id));
        }
        if (c.Conspire)
        {
            var candidates = ConspireCandidates(card, caster);
            var manaCreatures = candidates.Where(s => s.ManaOptions.Count > 0).ToList();
            if (candidates.Count - manaCreatures.Count < 2) reserved.UnionWith(manaCreatures.Select(s => s.Id));
        }
        return reserved.Count == 0 ? usable : (source, o) => !reserved.Contains(source.Id) && usable(source, o);
    }

    /// <summary>Which way to cast the card for (601.2b, 118.9), among those that can be paid.</summary>
    private async Task<CastingWay> ChooseCastingWayAsync(PlayerId player, Card card, IReadOnlyList<CastingWay> ways)
    {
        if (ways.Count == 1) return ways[0];
        if (ways.Count == 2 && ways[0].Kind == CastingWayKind.ManaCost)
        {
            var question = ways[1].Kind switch
            {
                CastingWayKind.Dash => $"Cast {card.Name} for its dash cost {ways[1].Mana}?",
                CastingWayKind.WithoutPaying => card.Definition.ManaCost.XCount > 0 ? $"Cast {card.Name} without paying its mana cost (X = 0)?" : $"Cast {card.Name} without paying its mana cost?",
                _ => $"Pay {ways[1].Mana} instead of the mana cost?",
            };
            return await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest(question, card.Id)) ? ways[1] : ways[0];
        }
        var labels = ways.Select(w => w.Kind switch
        {
            CastingWayKind.ManaCost => $"Pay its mana cost {w.Mana}",
            CastingWayKind.Dash => $"Dash {w.Mana}",
            CastingWayKind.WithoutPaying => "Without paying its mana cost",
            _ => $"Pay {w.Mana} instead of the mana cost",
        }).ToList();
        int pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player), new OptionRequest($"{card.Name}: choose how to pay for it", card.Id, labels, OptionKind.Other));
        Require(pick >= 0 && pick < ways.Count, "Choose one of the ways to pay.");
        return ways[pick];
    }

    /// <summary>How many times to pay a cost that may be paid any number of times (as many as can be paid, at most).</summary>
    private async Task<int> ChooseTimesAsync(PlayerId player, Card card, Func<int, CastingChoices> paying, ManaCost each, string what)
    {
        int max = 0;
        while (max < 30 && CanPayToCast(card, player, paying(max + 1))) max++;
        if (max == 0) return 0;
        int n = await ControllerOf(player).ChooseNumberAsync(ViewFor(player), new NumberRequest($"{card.Name}: pay its {what} cost {each} how many times?", card.Id, 0, max));
        Require(n >= 0 && n <= max, $"Choose between 0 and {max}.");
        return n;
    }
}
