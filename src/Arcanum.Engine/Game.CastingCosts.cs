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
    private enum CastingWayKind { ManaCost, Dash, Alternative, WithoutPaying, PayLife, Overload, Blitz, Foretold, Impending, FaceDown, Escape, Evoke, Harmonize }

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
        /// <summary>The optional additional cost (<see cref="CardDefinition.OptionalAdditionalCost"/>) is paid.</summary>
        public bool AdditionalCostPaid { get; init; }
        public int KickCount { get; init; }
        public int ReplicateCount { get; init; }
        public int SquadCount { get; init; }
        public bool OffspringPaid { get; init; }

        /// <summary>The offspring costs paid of the offspring abilities the spell gained ("creature spells you cast gain offspring {2}").</summary>
        public IReadOnlyList<ManaCost> GrantedOffspringPaid { get; init; } = Array.Empty<ManaCost>();
        /// <summary>Permanents sacrificed for "you may sacrifice any number of …; it costs {1} less for each".</summary>
        public int SacrificeAnyCount { get; init; }
        /// <summary>Modes chosen beyond the first, each paying the escalate cost (rule 702.120).</summary>
        public int EscalatedModes { get; init; }
        /// <summary>Tiered (rule 702.183a): the additional cost of the chosen mode.</summary>
        public ManaCost? Tier { get; init; }
        /// <summary>Artifacts tapped for improvise (rule 702.126): kept out of the mana payment.</summary>
        public IReadOnlyCollection<CardId> Improvised { get; init; } = Array.Empty<CardId>();
        /// <summary>Creatures tapped for convoke (rule 702.51): kept out of the mana payment.</summary>
        public IReadOnlyCollection<CardId> Convoked { get; init; } = Array.Empty<CardId>();
        public IReadOnlyList<Card> Spliced { get; init; } = Array.Empty<Card>();
        /// <summary>Conspire instances paid for (rule 702.78b: each is paid and copies separately).</summary>
        public int ConspireCount { get; init; }
        /// <summary>The creature tapped for harmonize (702.180b): the spell costs {X} less, X its power.</summary>
        public CardId? HarmonizeTapped { get; init; }
        public int X { get; init; }
    }

    /// <summary>The ways the card can be cast for right now (118.9), whether or not they can be paid.</summary>
    private List<CastingWay> CastingWays(Card card, PlayerId caster)
    {
        if ((card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.WithoutPaying)) || _castFree.Contains(card.Id))
            return new() { new(CastingWayKind.WithoutPaying, ManaCost.Zero) };
        if (PaysLife(card)) return new() { new(CastingWayKind.PayLife, ManaCost.Zero, card.ManaValue) };
        // "You may cast spells from the top of your library. If you cast a spell this way, pay life equal to its mana value": the only way
        // through that permission; another permission to cast it from there lets it be paid for normally.
        if (card.Zone == Zone.Library && LibraryTopLifePermission(card, caster))
        {
            var life = new List<CastingWay> { new(CastingWayKind.PayLife, ManaCost.Zero, card.ManaValue) };
            if (LibraryTopPermissions(caster).Any() || LibraryTopManaPermission(card, caster)) life.Add(new(CastingWayKind.ManaCost, card.Definition.ManaCost));
            return life;
        }
        if (_miracleCost.TryGetValue(card.Id, out var miracle)) return new() { new(CastingWayKind.ManaCost, miracle) };
        // A foretold card: cast on a later turn for its foretell cost (702.143a), unless something else lets it be cast from exile.
        if (Foretold(card) && !PlayableExile(caster).Contains(card.Id)) return new() { new(CastingWayKind.Foretold, card.Definition.Foretell!) };
        if (card.Zone == Zone.Graveyard && card.Definition.Flashback is { } flashback && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version))
        {
            if (card.Definition.FlashbackReduction is { } reduction)
                flashback = flashback.MinusGeneric(Math.Max(0, Eval(reduction, new EffectContext(caster, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))));
            return new() { new(CastingWayKind.ManaCost, flashback) };
        }
        var ways = new List<CastingWay> { new(CastingWayKind.ManaCost, card.Definition.ManaCost) };
        if (card.Zone == Zone.Hand && card.Definition.Dash is { } dash) ways.Add(new(CastingWayKind.Dash, dash)); // 702.109a
        if (card.Definition.Evoke is { } evoke) ways.Add(new(CastingWayKind.Evoke, evoke)); // an alternative cost (702.74a)
        // "Gain evoke {N} as you cast them": one more evoke cost per permanent granting it.
        if (card.Zone == Zone.Hand && card.Types.IsPermanent())
            foreach (var granted in GrantedEvokeCosts(card, caster).Where(g => !ways.Any(w => w.Kind == CastingWayKind.Evoke && w.Mana.Equals(g))))
                ways.Add(new(CastingWayKind.Evoke, granted));
        if (card.Definition.Blitz is { } blitz && (card.Zone == Zone.Hand || BlitzFromGraveyard(card)))
        {
            // 702.152a; "you may cast this card from your graveyard using its blitz ability": only that way, unless something else allows it.
            if (card.Zone == Zone.Graveyard && !GraveyardCastByOtherPermission(card) && OncePerTurnGraveyardPermissions(card, caster).Count == 0)
                return new() { new(CastingWayKind.Blitz, blitz) };
            ways.Add(new(CastingWayKind.Blitz, blitz));
        }
        if (card.Zone == Zone.Graveyard && HarmonizeCost(card) is { } harmonize)
        {
            // 702.180a: from the graveyard only with harmonize, unless something else lets it be cast from there.
            if (!GraveyardCastByOtherPermission(card) && OncePerTurnGraveyardPermissions(card, caster).Count == 0 && !BlitzFromGraveyard(card))
                return new() { new(CastingWayKind.Harmonize, harmonize) };
            ways.Add(new(CastingWayKind.Harmonize, harmonize));
        }
        if (card.Zone == Zone.Graveyard && card.Definition.Escape is { } escape)
        {
            // 702.138a: from the graveyard only with escape, unless something else lets it be cast from there.
            if (!GraveyardCastByOtherPermission(card) && OncePerTurnGraveyardPermissions(card, caster).Count == 0 && !BlitzFromGraveyard(card))
                return new() { new(CastingWayKind.Escape, escape) };
            ways.Add(new(CastingWayKind.Escape, escape));
        }
        if (card.Definition.Overload is { } overload) ways.Add(new(CastingWayKind.Overload, overload)); // 702.96a
        if (card.Definition.Impending is { } impending) ways.Add(new(CastingWayKind.Impending, impending)); // 702.176a
        if (card.Zone == Zone.Hand && card.PrintedDefinition.Morph is not null) ways.Add(new(CastingWayKind.FaceDown, FaceDownCastCost)); // 702.37c
        if (card.Definition.AlternativeCost is { } alt && (alt.If is null || Holds(alt.If, caster, card))) ways.Add(new(CastingWayKind.Alternative, alt.Cost));
        if (card.Zone == Zone.Hand && Has(caster, Replacements.CastFromHandFree)) ways.Add(new(CastingWayKind.WithoutPaying, ManaCost.Zero));
        // "You may cast Dragon spells without paying their mana costs": wherever the card can be cast from.
        else if (State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Definition.CastSpellsFree is { } free
                     && Matches(free with { Controller = ControllerFilter.Any }, card, caster, p, caster)))
            ways.Add(new(CastingWayKind.WithoutPaying, ManaCost.Zero));
        // "Spells with mana value less than or equal to the number of creatures you control from your hand without paying their mana costs."
        else if (card.Zone == Zone.Hand && State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Definition.CastFromHandFreeUpTo is { } upTo
                     && ManaValueOf(card) <= Eval(upTo, new EffectContext(caster, p, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))))
            ways.Add(new(CastingWayKind.WithoutPaying, ManaCost.Zero));
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
        // "Costs {2} more to cast if it targets a Dragon": only once targets are chosen (before that, the cheaper case is possible).
        if (card.Definition.CostIncreaseIfTargetsFilter is { } dearer && targets is not null
            && targets.Any(t => t.Target.Card is { } tc && State.GetCard(tc) is var tcard && Matches(dearer with { Controller = ControllerFilter.Any }, tcard, tcard.Controller, card, caster)))
            cost = cost.PlusGeneric(card.Definition.CostIncreaseIfTargets);
        if (c.Option?.Mana is { } optionMana) cost = cost.Plus(optionMana);
        if (c.Kicked && card.Definition.Kicker is { } kicker) cost = cost.Plus(kicker);
        for (int i = 0; i < c.KickCount; i++) cost = cost.Plus(card.Definition.Multikicker!);
        for (int i = 0; i < c.ReplicateCount; i++) cost = cost.Plus(card.Definition.Replicate!);
        for (int i = 0; i < c.SquadCount; i++) cost = cost.Plus(card.Definition.Squad!);
        if (c.OffspringPaid && card.Definition.Offspring is { } offspring) cost = cost.Plus(offspring);
        foreach (var granted in c.GrantedOffspringPaid) cost = cost.Plus(granted);
        if (card.Definition.Escalate is { } escalate)
            for (int i = 0; i < c.EscalatedModes; i++) cost = cost.Plus(escalate);
        if (c.Tier is { } tier) cost = cost.Plus(tier);
        foreach (var other in c.Spliced) cost = cost.Plus(other.Definition.Splice!);
        foreach (var taxer in State.Battlefield.Select(State.GetCard))
            foreach (var increase in taxer.Abilities.OfType<SpellCostIncrease>())
                // "Spells your opponents cast cost {1} more": only an opponent of the taxer's controller pays it.
                if (Matches(increase.Spells with { Controller = ControllerFilter.Any }, card, caster, taxer, taxer.Controller)
                    && (increase.Spells.Controller != ControllerFilter.Opponent || State.OpponentsOf(taxer.Controller).Contains(caster))) cost = cost.PlusGeneric(increase.Amount);
        // "Costs {1} more for each target beyond the first": before targets are chosen, as if it had one.
        if (card.Definition.ExtraTargetCost > 0 && targets is not null)
            cost = cost.PlusGeneric(card.Definition.ExtraTargetCost * Math.Max(0, targets.Count(t => !t.Target.IsNone) - 1));
        int genericBefore = cost.Generic;
        int harmonized = c.HarmonizeTapped is { } tapped ? Math.Max(0, State.GetCard(tapped).Power) : 0;
        cost = MinusColoredReductions(cost.MinusGeneric(CostReductionFor(card, caster, targets) + (card.Definition.SacrificeAnyNoReduction ? 0 : c.SacrificeAnyCount) + harmonized), card, caster);
        // "Spend only black mana on X": the X part of the generic mana (what reductions left of it) is paid with that type only.
        if (card.Definition.XManaType is { } xType && c.X > 0 && c.Way.Mana.XCount > 0)
        {
            int xPart = c.X * c.Way.Mana.XCount;
            int reduced = genericBefore - cost.Generic;
            int xLeft = Math.Max(0, xPart - Math.Max(0, reduced - (genericBefore - xPart)));
            cost = new ManaCost(cost.Generic - xLeft, cost.Pips.Concat(Enumerable.Repeat(xType, xLeft)).ToList(), cost.Hybrid)
            { Phyrexian = cost.Phyrexian, MonoHybrid = cost.MonoHybrid, PendingGenericReduction = cost.PendingGenericReduction };
        }
        // "Mana of any type can be spent" to pay it.
        bool anyType = (card.Is(CardType.Creature) && Has(caster, Replacements.CreaturesFromLibraryTop))
                       || (card.Zone == Zone.Exile && card.CounterCount(CounterKind.Stash) > 0 && Has(caster, Replacements.PlayStashedCards))
                       || (card.Zone == Zone.Exile && State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.AnyManaType))
                       || (card.Zone == Zone.Graveyard && State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version && p.AnyManaType))
                       || ExiledCastableWith(card, caster) is not null;
        // "As though it were mana of any color": the colored symbols take any mana, a {C} still takes colorless mana.
        if (anyType && card.Zone == Zone.Exile && !(card.Is(CardType.Creature) && Has(caster, Replacements.CreaturesFromLibraryTop))
            && State.PlayableFromExile.Where(p => p.Card == card.Id && p.Version == card.Version && p.AnyManaType).ToList() is { Count: > 0 } perms && perms.All(p => p.AnyColorOnly)
            && !(card.CounterCount(CounterKind.Stash) > 0 && Has(caster, Replacements.PlayStashedCards)) && ExiledCastableWith(card, caster) is null)
        {
            int colorless = cost.Pips.Count(p => p == ManaType.Colorless);
            return new ManaCost(Math.Max(0, cost.ManaValue - colorless - cost.Phyrexian.Count - cost.MonoHybrid.Count - cost.PendingGenericReduction), Enumerable.Repeat(ManaType.Colorless, colorless).ToList()) { Phyrexian = cost.Phyrexian };
        }
        // With mana of any type, a {2/W} is best paid as one mana of its color (any mana does).
        if (anyType) return new ManaCost(Math.Max(0, cost.ManaValue - cost.Phyrexian.Count - cost.MonoHybrid.Count - cost.PendingGenericReduction), Array.Empty<ManaType>()) { Phyrexian = cost.Phyrexian };
        return BlackLifeRule(caster, cost);
    }

    /// <summary>
    /// Flashback a card in a graveyard has from an effect (its cost is its mana cost): "target card gains flashback until end of turn",
    /// or "each instant and sorcery card in your graveyard has flashback" from a permanent its owner controls.
    /// </summary>
    private bool HasGrantedFlashback(Card card) =>
        card.Zone == Zone.Graveyard
        && (State.FlashbackGranted.Any(p => p.Card == card.Id && p.Version == card.Version)
            || ((card.Is(CardType.Instant) || card.Is(CardType.Sorcery))
                && State.PermanentsControlledBy(card.Owner).Any(p => p.HasTextAbilities && p.Definition.GraveyardInstantsAndSorceriesHaveFlashback)));

    /// <summary>Cast with flashback (from the graveyard, exiled when it leaves the stack, 702.34a).</summary>
    private bool IsFlashbackCast(Card card) =>
        card.Zone == Zone.Graveyard
        && (card.Definition.Flashback is not null || HasGrantedFlashback(card))
        && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version);

    /// <summary>The non-mana costs the announcements commit to (601.2b): additional costs and those of casting from the graveyard.</summary>
    private List<ExtraCost> NonManaCastingCosts(Card card, CastingChoices c)
    {
        var costs = new List<ExtraCost?> { card.Definition.AdditionalCost, c.Option?.Extra, c.Kicked ? card.Definition.KickerExtra : null, c.FlashExtra ? card.Definition.FlashExtraCostExtra : null,
            c.AdditionalCostPaid ? card.Definition.OptionalAdditionalCost : null };
        if (card.Zone == Zone.Graveyard) costs.Add(GraveyardCost(card));
        // Cast with "… by removing three counters from among creatures you control in addition to paying their other costs" (no other permission).
        if (card.Zone == Zone.Exile && StaticExiledWithPermission(card, card.Owner) is { } holder && ExiledWithPlayRightFor(card, card.Owner) is null
            && !State.PlayableFromExile.Any(p => p.Card == card.Id && p.Version == card.Version && p.Player == card.Owner))
            costs.Add(holder.Definition.CastExiledWithThisExtra);
        if (c.Way.Kind == CastingWayKind.Alternative) costs.Add(card.Definition.AlternativeCost?.Extra);
        if (c.Way.Kind == CastingWayKind.WithoutPaying && _castFreeExtra.TryGetValue(card.Id, out var freeExtra)) costs.Add(freeExtra);
        if (c.SacrificeAnyCount > 0 && card.Definition.CastSacrificeAny is { } sacrificeAny) costs.Add(new ExtraCost(Sacrifice: sacrificeAny, SacrificeCount: c.SacrificeAnyCount));
        if (c.Way.Kind == CastingWayKind.Blitz) costs.Add(card.Definition.BlitzExtra);
        if (c.Way.Kind == CastingWayKind.Evoke && card.Definition.Evoke is { } ownEvoke && ownEvoke.Equals(c.Way.Mana)) costs.Add(card.Definition.EvokeExtra); // not a granted evoke
        if (c.Way.Kind == CastingWayKind.Escape) costs.Add(card.Definition.EscapeExtra);
        // A non-mana escalate cost, once for each mode beyond the first (702.120a).
        if (card.Definition.EscalateExtra is { } escalate && c.EscalatedModes > 0)
            costs.Add(escalate with
            {
                Discard = escalate.Discard * c.EscalatedModes, SacrificeCount = escalate.SacrificeCount * c.EscalatedModes,
                PayLife = escalate.PayLife * c.EscalatedModes, TapCount = escalate.TapCount * c.EscalatedModes,
            });
        if (IsFlashbackCast(card))
        {
            costs.Add(card.Definition.FlashbackExtra);
            if (card.Definition.FlashbackExilesX && c.X > 0) costs.Add(new ExtraCost { ExileFromGraveyard = c.X });
        }
        // "Blight X": X is announced by now (601.2b), so the cost is that many counters.
        return costs.OfType<ExtraCost>().Select(e => e.BlightX ? e with { BlightX = false, Blight = c.X } : e).ToList();
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
        if (life > 0 && (life > player.Life || player.CantLoseLifeNow(State.TurnNumber))) return false; // rule 119.4, 119.8
        int graveyard = player.Graveyard.Count(id => id != card.Id) - extras.Sum(e => e.ExileFromGraveyard);
        if (graveyard < 0) return false;
        if (c.ConspireCount > 0 && ConspireCandidates(card, caster).Count < 2 * c.ConspireCount) return false;
        if (HasDelve(card, caster)) total = total.MinusGeneric(graveyard); // 702.66a
        if (HasConvoke(card, caster) && total.ManaValue > 0) return CanPayWithConvoke(card, caster, c, total);
        if (HasImprovise(card, caster) && total.Generic > 0)
        {
            // 702.126a: each artifact tapped this way pays for {1}; try the artifacts that don't make mana first.
            var artifacts = ImproviseCandidates(card, caster);
            for (int n = 0; n <= Math.Min(total.Generic, artifacts.Count); n++)
                if (CanPayCastingMana(card, caster, c with { Improvised = artifacts.Take(n).Select(a => a.Id).ToList() }, total.MinusGeneric(n))) return true;
            return false;
        }
        return CanPayCastingMana(card, caster, c, total);
    }

    /// <summary>Untapped artifacts the caster controls that can be tapped for improvise, those without mana abilities first.</summary>
    private List<Card> ImproviseCandidates(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Where(a => a.Is(CardType.Artifact) && !a.Tapped && a.Id != card.Id)
            .OrderBy(a => a.ManaOptions.Count > 0 ? 1 : 0).ToList();

    private bool CanPayCastingMana(Card card, PlayerId caster, CastingChoices c, ManaCost mana) =>
        LifeAffordable(caster, mana).Any(m => m.Variants().Any(v => ManaPayment.FindPlan(State, caster, v, usable: SpellManaUsable(card, caster, c), unitUsable: UnitUsableFor(card, isAbility: false)) is not null));

    /// <summary>
    /// The mana costs a player could pay this cost with: each way of paying its Phyrexian symbols with life they can pay (rules 107.4f, 119.4).
    /// </summary>
    private IEnumerable<ManaCost> LifeAffordable(PlayerId player, ManaCost cost) =>
        cost.LifeOptions().Where(o => o.Life == 0 || (o.Life <= State.GetPlayer(player).Life && State.GetPlayer(player).CantLoseLifeTurn != State.TurnNumber)).Select(o => o.Mana);

    /// <summary>"For each {B} in a cost, you may pay 2 life rather than pay that mana" (a permanent the payer controls): its {B} become Phyrexian.</summary>
    private ManaCost BlackLifeRule(PlayerId payer, ManaCost cost) =>
        State.PermanentsControlledBy(payer).Any(c => c.HasTextAbilities && c.Definition.PayLifeForBlackMana) ? cost.PhyrexianFor(ManaType.Black) : cost;

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
        // Cast face down, it's a spell with no additional costs of its own, paid for as the face-down spell it is (702.37c, 708.2).
        return CastingWays(card, caster)
            .SelectMany(way => way.Kind == CastingWayKind.FaceDown
                ? new[] { new CastingChoices(way) { FlashExtra = flashExtra } }
                : options.Select(o => new CastingChoices(way) { Option = o, FlashExtra = flashExtra, Tier = CheapestTier(card, caster) }))
            .Select(c => c.Way.Kind == CastingWayKind.Harmonize && !CanPayToCast(card, caster, c) && HarmonizeTaps(card, caster, c) is [var tap, ..]
                ? c with { HarmonizeTapped = tap } : c)
            .Where(c => c.Way.Kind == CastingWayKind.FaceDown ? AsFaceDown(card, () => CanPayToCast(card, caster, c))
                : CanPayToCast(card, caster, c) || SacrificeAnyCounts(card, caster, c).Count > 0).ToList();
    }

    /// <summary>Untapped creatures the caster controls that could be tapped for harmonize (702.180b) with the spell still paid for, most powerful first.</summary>
    private List<CardId> HarmonizeTaps(Card card, PlayerId caster, CastingChoices c) =>
        State.PermanentsControlledBy(caster).Where(p => p.IsCreature && !p.Tapped).OrderByDescending(p => p.Power)
            .Where(p => CanPayToCast(card, caster, c with { HarmonizeTapped = p.Id })).Select(p => p.Id).ToList();

    /// <summary>The numbers of permanents the caster could sacrifice for "you may sacrifice any number of …" and still pay for the spell.</summary>
    private List<int> SacrificeAnyCounts(Card card, PlayerId caster, CastingChoices c)
    {
        if (card.Definition.CastSacrificeAny is not { } filter) return new();
        int most = SacrificeCandidates(caster, filter, card.Id).Count;
        return Enumerable.Range(0, most + 1).Where(n => CanPayToCast(card, caster, c with { SacrificeAnyCount = n })).ToList();
    }

    /// <summary>
    /// A tiered spell (702.183) is cast with one mode and its cost: for "can it be cast at all", the cheapest mode that could be chosen, one
    /// with legal targets for its requirements (601.2c). Paying for a cheaper mode that has none doesn't make the spell castable.
    /// </summary>
    private ManaCost? CheapestTier(Card card, PlayerId caster)
    {
        if (CastingTargets(card.Definition)?.Modes is not { } modes || !modes.Any(m => m.Cost is not null)) return null;
        var choosable = modes.Where(m => m.Targets.All(spec => IsUpTo(spec) || LegalTargets(spec, caster, card.Id).Any())).ToList();
        return (choosable.Count > 0 ? choosable : modes).Select(m => m.Cost ?? ManaCost.Zero).MinBy(m => m.ManaValue);
    }

    /// <summary>Whether the card may be cast now without paying extra for flash: an instant, flash, or a permission to cast it as though it had flash.</summary>
    private bool TimingAllows(Card card, PlayerId caster, bool sorcerySpeed) =>
        (sorcerySpeed || !State.OpponentsOf(caster).Any(o => State.PermanentsControlledBy(o).Any(c => c.HasTextAbilities && c.Definition.OpponentsCastAtSorcerySpeed))) &&
        TimingAllowsIgnoringRestrictions(card, caster, sorcerySpeed);

    private bool TimingAllowsIgnoringRestrictions(Card card, PlayerId caster, bool sorcerySpeed) =>
        TimingAllowsExceptFlashByTargets(card, caster, sorcerySpeed) || FlashByTargetsPossible(card, caster);

    /// <summary>
    /// "You may cast this spell as though it had flash if it targets a [filter]": some legal target of its single target requirement matches the filter,
    /// so it can be cast now (its target must then be one that does, see <see cref="NeedsFlashFromTargets"/>).
    /// </summary>
    private bool FlashByTargetsPossible(Card card, PlayerId caster) =>
        card.Definition.FlashIfTargets is { } wanted && CastingTargets(card.Definition) is { Targets.Count: 1 } ability
        && LegalTargets(ability.Targets[0], caster, card.Id).Any(t => FlashTargetMatches(wanted, t, card, caster));

    private bool FlashTargetMatches(ObjectFilter wanted, Abilities.Target t, Card card, PlayerId caster) =>
        t.Card is { } id && State.GetCard(id) is { Zone: Zone.Battlefield } target && Matches(wanted with { Controller = ControllerFilter.Any }, target, target.Controller, card, caster)
        && (wanted.Controller != ControllerFilter.You || target.Controller == caster);

    /// <summary>Casting the spell now is allowed only because of "as though it had flash if it targets …": its target has to be one that matches.</summary>
    private bool NeedsFlashFromTargets(Card card, PlayerId caster, bool sorcerySpeed) =>
        card.Definition.FlashIfTargets is not null && !TimingAllowsExceptFlashByTargets(card, caster, sorcerySpeed);

    private bool TimingAllowsExceptFlashByTargets(Card card, PlayerId caster, bool sorcerySpeed) =>
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
        LegendarySpellAllowed(card, caster) && !CastForbiddenByName(card, caster) && !CastForbiddenThisEarly(card, caster) && !NoncreatureForbidden(card, caster)
        && PayableCastingWays(card, caster, flashExtra).Any(w => w.Way.Kind == CastingWayKind.Overload
            ? HasLegalTargets(card.Definition.Overloaded, caster, card.Id) : HasLegalTargets(CastingTargets(card.Definition), caster, card.Id));

    /// <summary>"Your opponents can't cast noncreature spells this turn."</summary>
    private bool NoncreatureForbidden(Card card, PlayerId caster) =>
        !card.Is(CardType.Creature) && State.NoncreatureSpellsForbiddenFor.Contains((caster, State.TurnNumber));

    /// <summary>"You can't cast this spell during your first, second, or third turns of the game."</summary>
    /// <summary>The permanent letting <paramref name="caster"/> cast this card exiled with it ("as long as …, you may cast the exiled card"), if any.</summary>
    private Card? ExiledCastableWith(Card card, PlayerId caster) =>
        card.Zone == Zone.Exile && card.ExiledWith is { } with && State.GetCard(with.Source) is { Zone: Zone.Battlefield } holder && holder.Version == with.Version
        && holder.Controller == caster && holder.HasTextAbilities && holder.Definition.CastExiledWithThisIf is { } castIf && Holds(castIf, caster, holder) ? holder : null;

    private bool CastForbiddenThisEarly(Card card, PlayerId caster) =>
        card.Definition.CastOnlyIf is { } castIf && !Holds(castIf, caster, card) ||
        card.Definition.CantCastDuringFirstTurns > 0 && State.ActivePlayer == caster
        && State.GetPlayer(caster).TurnsTaken <= card.Definition.CantCastDuringFirstTurns;

    /// <summary>A card's harmonize cost: printed, or its mana cost while it "gains harmonize until end of turn".</summary>
    private ManaCost? HarmonizeCost(Card card) =>
        card.Definition.Harmonize
        ?? (State.HarmonizeGranted.Any(g => g.Card == card.Id && g.Version == card.Version && g.Turn == State.TurnNumber) ? card.Definition.ManaCost : null);

    /// <summary>Delve (702.66): printed, or given by a permanent the caster controls ("spells you cast have delve").</summary>
    private bool HasDelve(Card spell, PlayerId caster) =>
        spell.Definition.Delve || State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Definition.YourSpellsHaveDelve);

    /// <summary>Convoke (702.51): printed, or given by a permanent the caster controls ("creature spells you cast have convoke").</summary>
    private bool HasConvoke(Card spell, PlayerId caster) =>
        spell.Definition.Convoke || State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Definition.SpellsHaveConvoke is { } f
            && Matches(f with { Controller = ControllerFilter.Any }, spell, caster, p, caster));

    /// <summary>Conspire instances (702.78b): printed, plus one from each permanent the caster controls that gives the spell conspire.</summary>
    private int ConspireInstances(Card spell, PlayerId caster) =>
        (spell.Definition.Conspire ? 1 : 0) + State.PermanentsControlledBy(caster).Count(p => p.HasTextAbilities && p.Definition.SpellsHaveConspire is { } f
            && Matches(f with { Controller = ControllerFilter.Any }, spell, caster, p, caster));

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
        if (c.ConspireCount > 0)
        {
            var candidates = ConspireCandidates(card, caster);
            var manaCreatures = candidates.Where(s => s.ManaOptions.Count > 0).ToList();
            if (candidates.Count - manaCreatures.Count < 2 * c.ConspireCount) reserved.UnionWith(manaCreatures.Select(s => s.Id));
        }
        reserved.UnionWith(c.Improvised);
        reserved.UnionWith(c.Convoked);
        if (c.HarmonizeTapped is { } harmonizer) reserved.Add(harmonizer);
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
                CastingWayKind.Blitz => $"Cast {card.Name} for its blitz cost {BlitzCostText(card)}?",
                CastingWayKind.Evoke => $"Cast {card.Name} for its evoke cost {EvokeCostText(card, ways[1].Mana)} (it's sacrificed when it enters)?",
                CastingWayKind.Overload => $"Cast {card.Name} for its overload cost {ways[1].Mana}?",
                CastingWayKind.Impending => $"Cast {card.Name} for its impending cost {ways[1].Mana}?",
                CastingWayKind.Escape => $"Cast {card.Name} for its escape cost {EscapeCostText(card)}?",
                CastingWayKind.Harmonize => $"Cast {card.Name} for its harmonize cost {ways[1].Mana}?",
                CastingWayKind.FaceDown => $"Cast {card.Name} face down as a 2/2 creature for {ways[1].Mana}?",
                CastingWayKind.WithoutPaying => card.Definition.ManaCost.XCount > 0 ? $"Cast {card.Name} without paying its mana cost (X = 0)?" : $"Cast {card.Name} without paying its mana cost?",
                _ => $"Pay {ways[1].Mana} instead of the mana cost?",
            };
            return await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest(question, card.Id)) ? ways[1] : ways[0];
        }
        var labels = ways.Select(w => w.Kind switch
        {
            CastingWayKind.ManaCost => $"Pay its mana cost {w.Mana}",
            CastingWayKind.Dash => $"Dash {w.Mana}",
            CastingWayKind.Blitz => $"Blitz {BlitzCostText(card)}",
            CastingWayKind.Evoke => $"Evoke {EvokeCostText(card, w.Mana)}",
            CastingWayKind.Foretold => $"Foretell cost {w.Mana}",
            CastingWayKind.Overload => $"Overload {w.Mana}",
            CastingWayKind.Impending => $"Impending {w.Mana}",
            CastingWayKind.Escape => $"Escape {EscapeCostText(card)}",
            CastingWayKind.Harmonize => $"Harmonize {w.Mana}",
            CastingWayKind.FaceDown => $"Face down for {w.Mana}",
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
