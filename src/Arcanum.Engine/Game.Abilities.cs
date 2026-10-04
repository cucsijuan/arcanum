// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private sealed record PendingTrigger(CardId Source, TriggeredAbility Ability, PlayerId Controller, TriggerInfo? Info = null)
    {
        /// <summary>The source's object version when it triggered.</summary>
        public int SourceVersion { get; init; } = -1;
    }

    private readonly List<PendingTrigger> _pendingTriggers = new();

    // ------------------------------------------------------------------ targeting (rule 115)

    /// <summary>
    /// Whether the ability can be put on the stack: every required target has a legal choice (optional ones always
    /// do), and for a modal ability enough modes are possible.
    /// </summary>
    private bool HasLegalTargets(AbilityDefinition? ability, PlayerId controller, CardId source)
    {
        if (ability is null) return true;
        if (ability.Modes is { } modes)
            return modes.Count(m => m.Targets.All(spec => spec.Optional || LegalTargets(spec, controller, source).Any())) >= (ability.UpToModes ? 1 : ability.ModeCount);
        return ability.Targets.All(spec => spec.Optional || LegalTargets(spec, controller, source).Any());
    }

    /// <summary>The player a pending trigger is about, while its targets are chosen and checked ("that player controls").</summary>
    private PlayerId? _triggeredPlayer;

    private IEnumerable<Target> LegalTargets(TargetSpec spec, PlayerId controller, CardId source)
    {
        bool ControllerOk(PlayerId owner) => spec.Controller switch
        {
            ControllerFilter.You => owner == controller,
            ControllerFilter.Opponent => owner != controller,
            _ => true,
        } && (!spec.ControlledByTriggeredPlayer || owner == _triggeredPlayer);
        var sourceCard = State.GetCard(source);
        bool FilterOk(Card card) => spec.Filter is not { } f || Matches(f with { Controller = ControllerFilter.Any }, card, card.Controller, sourceCard, controller);

        if (spec.Optional) yield return Target.None;

        if (spec.Kind is TargetKind.Any or TargetKind.Player or TargetKind.PlayerOrPlaneswalker)
            foreach (var p in State.LivingPlayers.Where(p => ControllerOk(p.Id)))
                if (!PlayerHasHexproof(p.Id, controller)) yield return Target.Of(p.Id);

        switch (spec.Kind)
        {
            case TargetKind.Spell:
                foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Card != source && ControllerOk(s.Controller)))
                    if (FilterOk(State.GetCard(spell.Card)) && (!spec.SingleTargetOnly || spell.Targets.Count == 1)) yield return Target.Of(spell.Card);
                yield break;
            case TargetKind.SpellOrAbility:
                foreach (var item in State.Stack.Where(s => ControllerOk(s.Controller) && (!spec.SingleTargetOnly || s.Targets.Count == 1)))
                {
                    if (item is SpellOnStack sp && sp.Card != source) yield return Target.Of(sp.Card);
                    else if (item is AbilityOnStack) yield return Target.OfStack(item.Id);
                }
                yield break;
            case TargetKind.Player:
                yield break;
            case TargetKind.GraveyardCard:
                foreach (var player in State.Players.Where(p => ControllerOk(p.Id)))
                    foreach (var id in player.Graveyard)
                        if (FilterOk(State.GetCard(id))) yield return Target.Of(id);
                yield break;
        }

        foreach (var card in State.Battlefield.Select(State.GetCard))
        {
            if (!ControllerOk(card.Controller) || !MatchesKind(card, spec.Kind) || !FilterOk(card)) continue;
            if (card.Has(Keyword.Shroud) || (card.Has(Keyword.Hexproof) && card.Controller != controller)) continue; // 702.18, 702.11
            if (card.Controller != controller && HexproofFrom(card, sourceCard)) continue;
            if (ProtectedFrom(card, sourceCard)) continue; // 702.16b
            if (card.Controller != controller && (card.Definition.HexproofFromTypes & sourceCard.Types) != 0) continue;
            yield return Target.Of(card.Id);
        }
    }

    /// <summary>
    /// Protection (rule 702.16): from everything, or from a color of <paramref name="source"/> (a source that left the
    /// battlefield has the colors it last had there).
    /// </summary>
    internal static bool ProtectedFrom(Card card, Card? source)
    {
        if (card.Has(Keyword.ProtectionFromEverything)) return true;
        if (source is null) return false;
        var colors = source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lki ? lki.Colors : source.Colors;
        return colors.Any(c => Keywords.ProtectionFrom(c) is { } protection && card.Has(protection));
    }

    /// <summary>"You have hexproof" (a permanent with that static ability) protects a player from opponents' targeting.</summary>
    private bool PlayerHasHexproof(PlayerId player, PlayerId targetingPlayer) =>
        player != targetingPlayer && State.PermanentsControlledBy(player).Any(c => c.Definition.GivesControllerHexproof);

    /// <summary>"Hexproof from [color]": opponents' sources of that color can't target it.</summary>
    private static bool HexproofFrom(Card card, Card source) =>
        card.Definition.HexproofFromColors.Count > 0 && ColorsOf(source).Any(card.Definition.HexproofFromColors.Contains);

    private static bool MatchesKind(Card card, TargetKind kind) => kind switch
    {
        TargetKind.Any or TargetKind.Creature => card.IsCreature || (kind == TargetKind.Any && card.Is(CardType.Planeswalker)),
        TargetKind.Permanent => true,
        TargetKind.Artifact => card.Is(CardType.Artifact),
        TargetKind.Enchantment => card.Is(CardType.Enchantment),
        TargetKind.Land => card.Is(CardType.Land),
        TargetKind.Planeswalker or TargetKind.PlayerOrPlaneswalker => card.Is(CardType.Planeswalker),
        TargetKind.CreatureOrPlaneswalker => card.IsCreature || card.Is(CardType.Planeswalker),
        _ => false,
    };

    /// <summary>Asks for targets (601.2c / 603.3d). Returns an empty list for untargeted abilities, null if cancelled.</summary>
    /// <param name="affordable">For a single-target spell or ability whose cost depends on its target: the targets it can still be paid with.</param>
    private async Task<IReadOnlyList<ChosenTarget>?> ChooseTargetsAsync(
        PlayerId player, AbilityDefinition? ability, CardId source, string text, bool canCancel, Func<Target, bool>? affordable = null)
    {
        if (ability is null || ability.Targets.Count == 0) return Array.Empty<ChosenTarget>();
        var legal = ability.Targets.Select(spec => (IReadOnlyList<Target>)LegalTargets(spec, player, source)
            .Where(t => affordable is null || t.IsNone || affordable(t)).ToList()).ToList();
        var request = new TargetRequest(source, text, ability.Targets, legal, canCancel)
        {
            Allowed = ability.TargetRule == TargetRule.None && ability.Targets.All(t => t.AttachedToTarget is null)
                ? null
                : (index, candidate, before) => TargetAllowed(ability, index, candidate, before),
        };

        var chosen = await ControllerOf(player).ChooseTargetsAsync(ViewFor(player), request);
        if (chosen is null)
        {
            Require(canCancel, "These targets must be chosen.");
            return null;
        }
        Require(request.IsComplete(chosen.Count) && (request.LastIsAnyNumber || chosen.Count == legal.Count), $"Choose {legal.Count} target(s).");
        // Each choice must be legal, different from earlier ones for the same "target" word (115.3), and follow the rules between targets.
        for (int i = 0; i < chosen.Count; i++)
            Require(request.IsAllowed(i, chosen[i], chosen.Take(i).ToList()), $"Illegal target {chosen[i]}.");
        return chosen.Select(t => new ChosenTarget(t, VersionOf(t))).ToList();
    }

    /// <summary>Rules between targets (different objects, different controllers, same graveyard, attached to an earlier target).</summary>
    private bool TargetAllowed(AbilityDefinition ability, int index, Target candidate, IReadOnlyList<Target> before)
    {
        if (candidate.IsNone) return true;
        var spec = ability.Targets[Math.Min(index, ability.Targets.Count - 1)];
        if (spec.AttachedToTarget is { } host)
        {
            if (host >= before.Count || before[host].Card is not { } hostCard || candidate.Card is not { } att || State.GetCard(att).AttachedTo != hostCard) return false;
        }
        var earlier = before.Where(t => !t.IsNone).ToList();
        switch (ability.TargetRule)
        {
            case TargetRule.AllDifferent:
                return !earlier.Contains(candidate);
            case TargetRule.DifferentControllers:
                return candidate.Card is not { } c || earlier.All(t => t.Card is not { } e || State.GetCard(e).Controller != State.GetCard(c).Controller);
            case TargetRule.SameGraveyard:
                return candidate.Card is not { } g || earlier.All(t => t.Card is not { } e || State.GetCard(e).Owner == State.GetCard(g).Owner);
            case TargetRule.ShareCardType:
                return candidate.Card is not { } s || earlier.All(t => t.Card is not { } e || (State.GetCard(e).Types & State.GetCard(s).Types
                    & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0);
        }
        return true;
    }

    private static TargetSpec SpecAt(AbilityDefinition ability, int index) => ability.Targets[Math.Min(index, ability.Targets.Count - 1)];

    private void PushStack(StackItem item)
    {
        item = item with { Id = State.NextStackId++ };
        State.Stack.Add(item);
        // Ward (702.21): becoming the target of an opponent's spell or ability triggers "counter it unless that player pays".
        foreach (var target in item.Targets.Select(t => t.Target.Card).Where(c => c is not null).Distinct())
        {
            var warded = State.GetCard(target!.Value);
            if (warded.Zone != Zone.Battlefield || warded.Controller == item.Controller) continue;
            Queue(warded.Id, TriggerEvent.BecomesTargetOfOpponent, warded.Controller, new TriggerInfo(Player: item.Controller));
            // Each instance of ward triggers separately (rule 702.21b): printed, then granted by other permanents.
            var wards = new List<(ManaCost Mana, int Life)>();
            if (warded.Definition.WardMana is not null || warded.Definition.WardLife > 0) wards.Add((warded.Definition.WardMana ?? ManaCost.Zero, warded.Definition.WardLife));
            wards.AddRange(warded.GrantedWards.Select(w => (w, 0)));
            foreach (var (mana, life) in wards)
            {
                var ward = new TriggeredAbility
                {
                    Trigger = TriggerEvent.EntersBattlefield,
                    Effects = new Effect[] { new CounterUnlessPays(item.Id, mana, life) },
                    Text = $"Ward — counter it unless its controller pays {DescribeWard(mana, life)}.",
                };
                _pendingTriggers.Add(new PendingTrigger(warded.Id, ward, warded.Controller));
            }
        }
    }

    private static string DescribeWard(ManaCost mana, int life) =>
        string.Join(" and ", new[] { mana.ManaValue > 0 ? mana.ToString() : null, life > 0 ? $"{life} life" : null }.Where(x => x is not null));

    private int VersionOf(Target target) => target.Card is { } c ? State.GetCard(c).Version : 0;

    private bool IsStillLegal(ChosenTarget chosen, TargetSpec spec, PlayerId controller, CardId source)
    {
        if (chosen.Target.IsNone) return false;
        if (chosen.Target.StackObject is { } so) return State.Stack.Any(s => s.Id == so);
        if (chosen.Target.Card is { } card && State.GetCard(card).Version != chosen.Version) return false; // new object
        return LegalTargets(spec, controller, source).Contains(chosen.Target);
    }

    // ------------------------------------------------------------------ modes (rule 700.2)

    /// <summary>
    /// For a modal ability, asks which modes to use and returns the ability narrowed to them (their targets in
    /// order, each mode's effects reading its own targets). Non-modal abilities come back unchanged; null if cancelled.
    /// </summary>
    private async Task<T?> ChooseModesAsync<T>(PlayerId player, T ability, CardId source, bool canCancel) where T : AbilityDefinition
    {
        if (ability.Modes is not { } modes) return ability;
        var sourceCard = State.GetCard(source);
        var possible = Enumerable.Range(0, modes.Count)
            .Where(i => modes[i].Targets.All(spec => spec.Optional || LegalTargets(spec, player, source).Any()))
            .Where(i => !ability.ModesOncePerObject || !sourceCard.ChosenModes.Contains(i))
            .ToList();
        int min = ability.UpToModes ? 1 : Math.Min(ability.ModeCount, possible.Count);
        int max = Math.Min(ability.ModeCount, possible.Count);
        if (possible.Count == 0) return null;
        IReadOnlyList<int> chosen;
        if (possible.Count == 1 && min == 1) chosen = possible;
        else
        {
            var request = new ModeRequest(source, ability.Text, modes.Select(m => m.Text).ToList(), possible, min, max, canCancel);
            var answer = await ControllerOf(player).ChooseModesAsync(ViewFor(player), request);
            if (answer is null)
            {
                Require(canCancel, "Modes must be chosen.");
                return null;
            }
            Require(answer.Count >= min && answer.Count <= max && answer.Distinct().Count() == answer.Count && answer.All(possible.Contains),
                $"Choose {min}–{max} of the possible modes.");
            chosen = answer.OrderBy(i => i).ToList();
        }
        if (ability.ModesOncePerObject) sourceCard.ChosenModes.UnionWith(chosen);
        return ability.WithModes(chosen);
    }

    // ------------------------------------------------------------------ resolution (rule 608)

    /// <summary>Carries out a resolving spell's or ability's effects. Returns false if every target became illegal.</summary>
    private async Task<bool> ApplyResolutionAsync(StackItem item, AbilityDefinition ability, Card source)
    {
        _triggeredPlayer = (item as AbilityOnStack)?.Trigger?.Player;
        var legal = new bool[item.Targets.Count];
        for (int i = 0; i < item.Targets.Count; i++)
            legal[i] = IsStillLegal(item.Targets[i], SpecAt(ability, i), item.Controller, source.Id);
        // 608.2b: only targets actually chosen count ("up to one" left empty doesn't make it fizzle).
        if (item.Targets.Any(t => !t.Target.IsNone) && !legal.Any(l => l)) return false;

        // An intervening "if" clause is checked again on resolution (rule 603.4).
        if (ability is TriggeredAbility { Condition: { } condition } && !Holds(condition, item.Controller, source)) return true;

        var context = new EffectContext(item.Controller, source, item.Targets, legal, item.X, item.Kicked)
        {
            Trigger = (item as AbilityOnStack)?.Trigger, GrantedBy = ability.GrantedBy, SourceVersion = (item as AbilityOnStack)?.SourceVersion,
        };
        context.Results.Sacrificed.AddRange(item.SacrificedForCost);
        // A promised gift is given before the spell's other effects (rule 702.174b).
        if (item is SpellOnStack { GiftTo: { } giftee } && source.Definition.Gift is { } gift && !State.GetPlayer(giftee).HasLost)
        {
            BeginEnteringTogether();
            for (int i = 0, n = Has(giftee, Replacements.DoubleTokens) ? 2 : 1; i < n; i++) CreateToken(gift, giftee);
            EndEnteringTogether();
        }
        if (item is AbilityOnStack { Ability: var counted }) source.ResolvedThisTurn[counted] = source.ResolvedThisTurn.GetValueOrDefault(counted) + 1;
        await ApplyAllAsync(ability.Effects, context);
        RecomputeContinuousEffects();
        return true;
    }

    private async Task ApplyAllAsync(IEnumerable<Effect> effects, EffectContext ctx)
    {
        foreach (var effect in effects)
        {
            if (State.IsGameOver) return;
            await ApplyAsync(effect, ctx);
            RecomputeContinuousEffects(); // later effects see earlier ones ("then it fights")
        }
    }

    private sealed record EffectContext(PlayerId Controller, Card Source, IReadOnlyList<ChosenTarget> Targets, bool[] TargetLegal,
        int X = 0, bool Kicked = false, int TargetOffset = 0)
    {
        public TriggerInfo? Trigger { get; init; }

        /// <summary>For an ability: its source's object version when it triggered or was activated.</summary>
        public int? SourceVersion { get; init; }

        /// <summary>The permanent that granted the resolving ability, if it was granted.</summary>
        public CardId? GrantedBy { get; init; }

        /// <summary>What earlier effects of this spell or ability did ("this way"): shared by all its effects.</summary>
        public EffectResults Results { get; init; } = new();

        /// <summary>The chosen target an effect calls "target N", if it is still legal.</summary>
        public Target? TargetAt(int index)
        {
            int i = TargetOffset + index;
            return i < Targets.Count && TargetLegal[i] ? Targets[i].Target : null;
        }

        /// <summary>The chosen target even if it became illegal (for "equal to its power" and the like).</summary>
        /// <summary>The object version the target had when it was chosen.</summary>
        public int? ChosenVersionAt(int index)
        {
            int i = TargetOffset + index;
            return i < Targets.Count && !Targets[i].Target.IsNone ? Targets[i].Version : null;
        }

        public Target? ChosenAt(int index)
        {
            int i = TargetOffset + index;
            return i < Targets.Count && !Targets[i].Target.IsNone ? Targets[i].Target : null;
        }
    }

    private sealed class EffectResults
    {
        public List<CardId> Sacrificed { get; } = new();
        public bool YouSacrificed { get; set; }
        public int LifeLost { get; set; }
        public List<CardId> Milled { get; } = new();
        public int Destroyed { get; set; }
        public int ExcessDamage { get; set; }
        public List<CardId> Exiled { get; } = new();
        public List<CardId> Created { get; } = new();
        public CardId? Amassed { get; set; }
        public List<CardId> Discarded { get; } = new();
        public List<CardId> Found { get; } = new();
        public List<CardId> Returned { get; } = new();
    }

    private IEnumerable<Card> CardsFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Card is { } id => new[] { State.GetCard(id) },
        SubjectKind.Self when ctx.Source.Zone == Zone.Battlefield => new[] { ctx.Source },
        SubjectKind.EachTarget => Enumerable.Range(subject.Index, Math.Max(0, ctx.Targets.Count - ctx.TargetOffset - subject.Index))
            .Select(i => ctx.TargetAt(i)?.Card).Where(c => c is not null).Select(c => State.GetCard(c!.Value)).ToList(),
        SubjectKind.GranterPermanent when ctx.GrantedBy is { } granter && State.GetCard(granter).Zone == Zone.Battlefield => new[] { State.GetCard(granter) },
        SubjectKind.Attached when ctx.Source.Zone == Zone.Battlefield && ctx.Source.AttachedTo is { } host => new[] { State.GetCard(host) },
        SubjectKind.Amassed when ctx.Results.Amassed is { } army && State.GetCard(army).Zone == Zone.Battlefield => new[] { State.GetCard(army) },
        SubjectKind.Created => ctx.Results.Created.Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList(),
        SubjectKind.Found => ctx.Results.Found.Select(State.GetCard).ToList(),
        SubjectKind.Discarded => ctx.Results.Discarded.Select(State.GetCard)
            .Where(c => c.Zone == Zone.Graveyard && (subject.Filter is null || Matches(subject.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller))).ToList(),
        SubjectKind.Triggered when ctx.Trigger is { Subject: { } id } info && State.GetCard(id).Version == info.SubjectVersion => new[] { State.GetCard(id) },
        SubjectKind.Each when subject.Filter is { } filter =>
            State.Battlefield.Select(State.GetCard).Where(c => Matches(filter, c, c.Controller, ctx.Source, ctx.Controller)
                                                               && (!subject.ControlledByTarget || ctx.TargetAt(subject.Index)?.Player == c.Controller)
                                                               && (!subject.ExceptTargets || ctx.Targets.All(t => t.Target.Card != c.Id))).ToList(),
        _ => Array.Empty<Card>(),
    };

    private IEnumerable<PlayerId> PlayersFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.You => new[] { ctx.Controller },
        SubjectKind.EachOpponent => State.OpponentsOf(ctx.Controller).ToList(),
        SubjectKind.EachPlayer => State.LivingPlayers.Select(p => p.Id).ToList(),
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Player is { } p => new[] { p },
        SubjectKind.TargetController when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { ControllerOrLastKnown(State.GetCard(c), ctx.ChosenVersionAt(subject.Index)) },
        SubjectKind.TargetOwner when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { State.GetCard(c).Owner },
        SubjectKind.TriggeredPlayer when ctx.Trigger?.Player is { } p => new[] { p },
        SubjectKind.FixedPlayer when subject.Player is { } fixedPlayer => new[] { fixedPlayer },
        SubjectKind.EachTarget => Enumerable.Range(subject.Index, Math.Max(0, ctx.Targets.Count - ctx.TargetOffset - subject.Index))
            .Select(i => ctx.TargetAt(i)?.Player).Where(p => p is not null).Select(p => p!.Value).ToList(),
        SubjectKind.Triggered when ctx.Trigger?.Subject is { } c => new[] { State.GetCard(c).Controller },
        _ => Array.Empty<PlayerId>(),
    };

    /// <summary>Works out a quantity as the effect happens.</summary>
    private int Eval(Quantity q, EffectContext ctx)
    {
        Card? TargetCard() => ctx.ChosenAt(q.Index)?.Card is { } id ? State.GetCard(id) : null;
        // A target that left the battlefield during resolution ("its toughness" after it was moved) is used as it
        // last existed there (rule 608.2h).
        LastKnown? TargetLastKnown() => TargetCard() is { } t && ctx.ChosenVersionAt(q.Index) is { } v && v != t.Version
                                        && t.Zone != Zone.Battlefield ? t.LastKnownInfo : null;
        int value = q.Kind switch
        {
            QuantityKind.Fixed => q.Value,
            QuantityKind.X => ctx.X,
            QuantityKind.PermanentCount when q.ControlledByTarget => ctx.TargetAt(q.Index)?.Player is { } owner
                ? State.PermanentsControlledBy(owner).Count(c => Matches((q.Filter ?? ObjectFilter.YourCreatures) with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))
                : 0,
            QuantityKind.PermanentCount => State.Battlefield.Select(State.GetCard)
                .Count(c => Matches(q.Filter ?? ObjectFilter.YourCreatures, c, c.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.DiscardedThisWay => ctx.Results.Discarded.Count(id => q.Filter is null
                || Matches(q.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), State.GetCard(id).Owner, ctx.Source, ctx.Controller)),
            QuantityKind.GraveyardsWithAtLeast => State.LivingPlayers.Count(p => p.Graveyard.Count >= q.Value),
            QuantityKind.ManaSpent => ctx.Trigger?.Subject is { } spent ? State.GetCard(spent).ManaSpent : 0,
            QuantityKind.ReturnedThisWay => ctx.Results.Returned.Count,
            QuantityKind.AttachedPower => ctx.Source.Zone == Zone.Battlefield && ctx.Source.AttachedTo is { } host ? State.GetCard(host).Power : 0,
            QuantityKind.SourceToughness => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null ? ctx.Source.Toughness : ctx.Source.LastKnownInfo.Toughness,
            QuantityKind.GraveyardCount => State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.SourcePower => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null ? ctx.Source.Power : ctx.Source.LastKnownInfo.Power,
            QuantityKind.TargetPower => TargetLastKnown()?.Power ?? TargetCard()?.Power ?? 0,
            QuantityKind.TargetToughness => TargetLastKnown()?.Toughness ?? TargetCard()?.Toughness ?? 0,
            QuantityKind.TargetManaValue => TargetCard() is { } tmv ? ManaValueOf(tmv) : 0,
            QuantityKind.LifeGainedThisTurn => State.GetPlayer(ctx.Controller).LifeGainedThisTurn,
            QuantityKind.YourLife => State.GetPlayer(ctx.Controller).Life,
            QuantityKind.HandSize => State.GetPlayer(ctx.Controller).Hand.Count,
            QuantityKind.TriggerAmount => ctx.Trigger?.Amount ?? 0,
            QuantityKind.SacrificedToughness => ctx.Results.Sacrificed.Sum(id => State.GetCard(id).LastKnownInfo?.Toughness ?? State.GetCard(id).Toughness),
            QuantityKind.SacrificedPower => ctx.Results.Sacrificed.Sum(id => State.GetCard(id).LastKnownInfo?.Power ?? State.GetCard(id).Power),
            QuantityKind.LifeLostThisWay => ctx.Results.LifeLost,
            QuantityKind.MilledThisWay => ctx.Results.Milled.Count(id => q.Filter is null
                || Matches(q.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), State.GetCard(id).Owner, ctx.Source, ctx.Controller)),
            QuantityKind.DestroyedThisWay => ctx.Results.Destroyed,
            QuantityKind.ExcessDamage => ctx.Results.ExcessDamage,
            QuantityKind.ExiledThisWay => ctx.Results.Exiled.Count(id => q.Filter is null
                || Matches(q.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), State.GetCard(id).Owner, ctx.Source, ctx.Controller)),
            QuantityKind.DistinctManaValues => State.PermanentsControlledBy(ctx.Controller)
                .Where(c => q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller))
                .Select(c => c.Definition.ManaCost.ManaValue).Distinct().Count(),
            QuantityKind.SpellsCastThisTurn => State.GetPlayer(ctx.Controller).SpellsCastThisTurn.Select(State.GetCard)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.SpellsCastBeforeTriggered => SpellsCastBefore(ctx)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.TriggeredColors => ctx.Trigger?.Subject is { } colored ? ColorsOf(State.GetCard(colored)).Count : 0,
            QuantityKind.SourceCounters => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null
                ? ctx.Source.CounterCount(q.Counter) : ctx.Source.LastKnownInfo.Counters.GetValueOrDefault(q.Counter),
            QuantityKind.OpponentsGraveyardCount => State.OpponentsOf(ctx.Controller).Sum(o => State.GetPlayer(o).Graveyard.Count),
            QuantityKind.GreatestOtherPower => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Id != ctx.Source.Id)
                .Select(c => c.Power).DefaultIfEmpty(0).Max(),
            QuantityKind.TriggeredPower => ctx.Trigger?.Subject is { } t ? State.GetCard(t).Power : 0,
            QuantityKind.AttackingCount => State.Combat?.Attacks.Select(a => State.GetCard(a.Attacker))
                .Count(c => q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller)) ?? 0,
            _ => throw new NotSupportedException($"Quantity {q.Kind} is not implemented."),
        };
        return value * q.Multiplier + q.Offset;
    }

    private async Task ApplyAsync(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case ModeEffects m:
                await ApplyAllAsync(m.Effects, ctx with { TargetOffset = m.TargetOffset });
                break;
            case ExileUntilSourceLeaves eu:
                // If the source already left the battlefield, nothing is exiled (it would return at once).
                if (ctx.Source.Zone != Zone.Battlefield) break;
                foreach (var card in CardsFor(eu.What, ctx).ToList())
                {
                    MoveCard(card.Id, Zone.Exile);
                    if (!card.Definition.IsToken) State.LinkedExiles.Add(new LinkedExile(ctx.Source.Id, ctx.Source.Version, card.Id, card.Version));
                }
                break;
            case ExileAndReturnAtEndStep er:
                foreach (var card in CardsFor(er.What, ctx).ToList())
                {
                    MoveCard(card.Id, Zone.Exile);
                    if (!card.Definition.IsToken) State.AtNextEndStep.Add(new DelayedAction(card.Id, card.Version, Return: true, er.UnderYourControl ? ctx.Controller : card.Owner));
                }
                break;
            case ExtraTurn et:
                foreach (var player in PlayersFor(et.Who, ctx)) State.ExtraTurns.Add(player);
                break;
            case ReflexiveTrigger reflexive when reflexive.If is null || HoldsIn(reflexive.If, ctx):
                // "When you do, …": a new triggered ability, put on the stack the next time a player would receive
                // priority, with its own targets (rule 603.12).
                var about = reflexive.About is { } aboutSubject ? CardsFor(aboutSubject, ctx).FirstOrDefault() : null;
                var info = about is null && reflexive.Amount is null ? ctx.Trigger
                    : new TriggerInfo(about?.Id ?? ctx.Trigger?.Subject, about?.Version ?? ctx.Trigger?.SubjectVersion ?? 0, ctx.Trigger?.Player,
                        reflexive.Amount is { } reflexiveAmount ? Eval(reflexiveAmount, ctx) : ctx.Trigger?.Amount ?? 0);
                _pendingTriggers.Add(new PendingTrigger(ctx.Source.Id, reflexive.Ability, ctx.Controller, info));
                break;
            case ReflexiveTrigger:
                break; // the action didn't happen: no "when you do"
            case CreateTokenCopy tc:
            {
                int count = Eval(tc.Count, ctx);
                // The original may be a permanent, or a card (in exile or a graveyard) for "a copy of it".
                var original = tc.Of.Kind == SubjectKind.Self ? ctx.Source
                    : tc.Of.Kind == SubjectKind.Target && ctx.ChosenAt(tc.Of.Index)?.Card is { } chosenCard ? State.GetCard(chosenCard)
                    : CardsFor(tc.Of, ctx).FirstOrDefault();
                if (original is null) break;
                // The exceptions ("except it has haste and 'At the beginning of the end step, sacrifice this token'")
                // become part of the copy's own characteristics (rule 707.9b): copiable, and lost with its abilities.
                var copy = original.Definition with
                {
                    IsToken = true,
                    Supertypes = tc.NotLegendary ? original.Definition.Supertypes & ~Supertype.Legendary : original.Definition.Supertypes,
                    Keywords = tc.Haste ? original.Definition.Keywords.Append("Haste").ToList() : original.Definition.Keywords,
                    Subtypes = tc.AddSubtypes is { } extra ? original.Definition.Subtypes.Concat(extra).Distinct().ToList() : original.Definition.Subtypes,
                    Abilities = tc.SacrificeAtEndStep ? original.Definition.Abilities.Append(SacrificeThisAtEndStep).ToList() : original.Definition.Abilities,
                };
                int copies = count * (Has(ctx.Controller, Replacements.DoubleTokens) ? 2 : 1);
                BeginEnteringTogether();
                for (int i = 0; i < copies; i++)
                    if (CreateToken(copy, ctx.Controller) is { } made) ctx.Results.Created.Add(made);
                EndEnteringTogether();
                break;
            }
            case SacrificeIt si:
                foreach (var card in (si.What.Kind == SubjectKind.Self ? (ctx.Source.Zone == Zone.Battlefield ? new[] { ctx.Source } : Array.Empty<Card>()) : CardsFor(si.What, ctx)).ToList())
                {
                    if (card.Zone != Zone.Battlefield) continue;
                    bool yours = card.Controller == ctx.Controller;
                    SacrificePermanent(card.Id);
                    ctx.Results.Sacrificed.Add(card.Id);
                    if (yours) ctx.Results.YouSacrificed = true;
                }
                break;
            case ExileIfDiesThisTurn ed:
                foreach (var card in CardsFor(ed.What, ctx)) State.ExileIfDies.Add((card.Id, card.Version));
                break;
            case PreventCombatDamageTo pc:
                foreach (var card in CardsFor(pc.What, ctx)) State.CombatDamagePrevented.Add((card.Id, card.Version));
                break;
            case LookAtTopTake lt:
                await LookAtTopTakeAsync(ctx.Controller, lt with
                {
                    Count = lt.CountIsX ? ctx.X : lt.Count,
                    Filter = lt.MaxManaValueX ? (lt.Filter ?? ObjectFilter.Anything) with { MaxManaValue = ctx.X } : lt.Filter,
                    Take = lt.Take < 0 ? 99 : lt.Take,
                }, ctx.Source);
                break;
            case MayPayX mx:
            {
                int max = MaxAffordableX(ctx.Controller, ManaCost.Parse("{X}"), null);
                if (max == 0) break;
                int x = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller), new NumberRequest(mx.Prompt, ctx.Source.Id, 0, max));
                Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
                if (x == 0 || !await PayManaAsync(State.GetPlayer(ctx.Controller), ctx.Source.Id, new ManaCost(x, Array.Empty<ManaType>()), null)) break;
                await ApplyAllAsync(mx.Effects, ctx with { X = x });
                break;
            }
            case NoMaximumHandSizeForever:
                State.GetPlayer(ctx.Controller).NoMaximumHandSize = true;
                break;
            case CopyNextInstantOrSorcery:
                State.CopyNextInstantOrSorcery.Add((ctx.Controller, State.TurnNumber));
                break;
            case CastFromEachLibraryTopFree:
            {
                var exiled = new List<CardId>();
                foreach (var player in State.ApnapOrder().ToList())
                    if (State.GetPlayer(player).Library.FirstOrDefault() is { } top && State.GetPlayer(player).Library.Count > 0)
                    {
                        MoveCard(top, Zone.Exile);
                        exiled.Add(top);
                    }
                // Cast any number of the nonland cards, free, timing aside (rule 608.2g).
                while (true)
                {
                    var castable = exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && !c.Is(CardType.Land)
                        && HasLegalTargets(CastingTargets(c.Definition), ctx.Controller, c.Id)).ToList();
                    if (castable.Count == 0) break;
                    var options = castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList();
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("Cast a spell from among the exiled cards for free (or none)", ctx.Source.Id, options, 0, 1, CardChoicePurpose.ToBattlefield));
                    if (pick.Count == 0) break;
                    Require(castable.Any(c => c.Id == pick[0]), "Choose one of the exiled cards.");
                    State.PlayableFromExile.Add(new PlayableFromExile(pick[0], State.GetCard(pick[0]).Version, ctx.Controller, State.TurnNumber, WithoutPaying: true));
                    if (!await CastSpellAsync(State.GetPlayer(ctx.Controller), pick[0])) break;
                }
                break;
            }
            case ReturnAtNextEndStepWithOneFewer rf:
            {
                var card = ctx.Source;
                int had = card.LastKnownInfo?.Counters.GetValueOrDefault(rf.Kind) ?? 0;
                if (card.Zone != Zone.Graveyard || had <= 0) break;
                State.AtNextEndStep.Add(new DelayedAction(card.Id, card.Version, Return: true, card.Owner) { Counters = (rf.Kind, had - 1) });
                break;
            }
            case DestroyManaValueXOfDamagedPlayers:
                foreach (var card in State.Battlefield.Select(State.GetCard)
                             .Where(c => !c.Is(CardType.Land) && c.Definition.ManaCost.ManaValue == ctx.X && ctx.Source.CombatDamagedPlayers.Contains(c.Controller)
                                         && !c.Has(Keyword.Indestructible)).ToList())
                {
                    MoveCard(card.Id, Zone.Graveyard);
                    Emit(new PermanentDestroyed(card.Id));
                }
                break;
            case AddManaUntilEndOfTurn mu2:
                foreach (var type in mu2.Types)
                {
                    State.GetPlayer(ctx.Controller).ManaPool.AddUntilEndOfTurn(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            case DiscardChosenByYou dc:
                foreach (var player in PlayersFor(dc.Who, ctx).ToList())
                {
                    var hand = State.GetPlayer(player).Hand.Select(State.GetCard).ToList();
                    Emit(new HandRevealed(player, hand.Select(c => c.Id).ToList()));
                    var options = hand.Where(c => dc.Filter is null || Matches(dc.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller))
                        .Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList();
                    int n = Math.Min(dc.Count, options.Count);
                    if (n == 0) continue;
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest($"Choose {n} card(s) for {State.GetPlayer(player).Name} to discard", ctx.Source.Id, options, n, n, CardChoicePurpose.Discard));
                    Require(chosen.Count == n && chosen.Distinct().Count() == n && chosen.All(id => options.Any(o => o.Id == id)), "Choose among the revealed cards.");
                    foreach (var id in chosen) DiscardCard(player, id, ctx.Controller);
                }
                break;
            case CreateEmblem ce:
            {
                var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                var emblem = new Card(id, new CardDefinition { Name = ce.Name, Abilities = ce.Abilities, IsEmblem = true, IsToken = true,
                    OracleText = string.Join("\n", ce.Abilities.Select(a => a.Text)) }, ctx.Controller) { Zone = Zone.Command };
                State.Cards.Add(id, emblem);
                State.Emblems.Add(id);
                if (ce.UntilEndOfTurn) State.EmblemsUntilEndOfTurn.Add(id);
                Emit(new EmblemCreated(id, ctx.Controller));
                break;
            }
            case ExileTopPlayable ep:
            {
                var owners = ep.From is { } from ? PlayersFor(from, ctx).ToList() : new List<PlayerId> { ctx.Controller };
                if (owners.Count == 0) break; // "target opponent's library": the target is gone
                var player = State.GetPlayer(owners[0]);
                var top = player.Library.Take(ep.CountFrom is { } countFrom ? Eval(countFrom, ctx) : ep.Count).ToList();
                foreach (var id in top)
                {
                    MoveCard(id, Zone.Exile);
                    if (ep.FaceDown) State.GetCard(id).FaceDown = true;
                }
                IReadOnlyList<CardId> playable = top;
                if (ep.ChooseOne && top.Count > 1)
                {
                    var options = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                    playable = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("Choose a card you may play this turn", ctx.Source.Id, options, 1, 1, CardChoicePurpose.ToHand));
                    Require(playable.Count == 1 && top.Contains(playable[0]), "Choose one of the exiled cards.");
                }
                int until = ep.Forever ? int.MaxValue : State.TurnNumber + (ep.UntilEndOfNextTurn ? State.LivingPlayers.Count() : 0);
                foreach (var id in playable)
                    State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, until, ep.WithoutPaying)
                    {
                        PayLife = ep.PayLife, While = ep.While,
                    });
                break;
            }
            case DealDamageDivided dd:
            {
                var chosen = Enumerable.Range(0, ctx.Targets.Count - ctx.TargetOffset).Select(i => ctx.TargetAt(i)).Where(t => t is not null).Select(t => t!.Value).ToList();
                if (chosen.Count == 0) break;
                int remaining = dd.Total;
                for (int i = 0; i < chosen.Count; i++)
                {
                    int amount = remaining;
                    int left = chosen.Count - i - 1;
                    if (left > 0)
                    {
                        var name = chosen[i].Card is { } cid ? State.GetCard(cid).Name : State.GetPlayer(chosen[i].Player!.Value).Name;
                        amount = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                            new NumberRequest($"Damage to {name} ({remaining} left)", ctx.Source.Id, 1, remaining - left));
                        Require(amount >= 1 && amount <= remaining - left, "Each target gets at least 1.");
                    }
                    remaining -= amount;
                    if (chosen[i].Card is { } c) DamageCreature(ctx.Source, State.GetCard(c), amount);
                    else DamagePlayer(ctx.Source, chosen[i].Player!.Value, amount);
                }
                break;
            }
            case KeepOneOfEachType ko:
                foreach (var player in PlayersFor(ko.Who, ctx).ToList())
                {
                    var mine = State.PermanentsControlledBy(player).ToList();
                    var keep = new HashSet<CardId>();
                    foreach (var type in new[] { CardType.Artifact, CardType.Creature, CardType.Enchantment, CardType.Land, CardType.Planeswalker })
                    {
                        var ofType = mine.Where(c => c.Is(type)).ToList();
                        if (ofType.Count == 0) continue;
                        var options = ofType.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList();
                        var pick = ofType.Count == 1 ? new[] { ofType[0].Id } : await ControllerOf(player).ChooseCardsAsync(ViewFor(player),
                            new CardChoiceRequest($"Choose a {type.ToString().ToLowerInvariant()} to keep", ctx.Source.Id, options, 1, 1, CardChoicePurpose.ToHand));
                        Require(pick.Count == 1 && ofType.Any(c => c.Id == pick[0]), "Choose one to keep.");
                        keep.Add(pick[0]);
                    }
                    BeginSimultaneous();
                    foreach (var card in mine.Where(c => !keep.Contains(c.Id))) SacrificePermanent(card.Id);
                    EndSimultaneous();
                }
                break;
            case CounterUnlessPays cu:
            {
                var item = State.Stack.FirstOrDefault(x => x.Id == cu.StackObject);
                if (item is null) break;
                var payer = State.GetPlayer(item.Controller);
                bool canPay = payer.Life >= cu.Life && (cu.Mana.ManaValue == 0 || Payable(item.Controller, cu.Mana, null));
                bool paid = false;
                if (canPay && await ControllerOf(item.Controller).ChooseYesNoAsync(ViewFor(item.Controller),
                        new YesNoRequest($"Pay {(cu.Mana.ManaValue > 0 ? cu.Mana.ToString() : "")}{(cu.Mana.ManaValue > 0 && cu.Life > 0 ? " and " : "")}{(cu.Life > 0 ? $"{cu.Life} life" : "")} so {State.GetCard(item.SourceCard).Name} isn't countered?", ctx.Source.Id)))
                {
                    paid = cu.Mana.ManaValue == 0 || await PayManaAsync(payer, item.SourceCard, cu.Mana, null);
                    if (paid && cu.Life > 0) ChangeLife(item.Controller, -cu.Life);
                }
                if (paid) break;
                if (item is SpellOnStack sp)
                {
                    if (CanBeCountered(State.GetCard(sp.Card))) CounterSpellOnStack(sp.Card);
                }
                else
                {
                    State.Stack.Remove(item);
                    Emit(new AbilityCountered(item.SourceCard));
                }
                break;
            }
            case ChangeTarget ct:
            {
                var target = ct.What.Kind == SubjectKind.Target ? ctx.TargetAt(ct.What.Index) : null;
                var item = target is { StackObject: { } so } ? State.Stack.FirstOrDefault(s => s.Id == so)
                    : target is { Card: { } targetedSpell } ? State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == targetedSpell) : null;
                if (item is null || item.Targets.Count != 1) break;
                var ability = item switch
                {
                    SpellOnStack sp => sp.Ability ?? CastingTargets(State.GetCard(sp.Card).Definition),
                    AbilityOnStack ab => ab.Ability,
                    _ => null,
                };
                if (ability is null) break;
                var current = item.Targets[0].Target;
                var saved = _triggeredPlayer;
                _triggeredPlayer = (item as AbilityOnStack)?.Trigger?.Player;
                var options = LegalTargets(SpecAt(ability, 0), item.Controller, item.SourceCard).Where(t => t != current && !t.IsNone).ToList();
                _triggeredPlayer = saved;
                if (options.Count == 0) break;
                var request = new TargetRequest(ctx.Source.Id, $"Choose the new target for {State.GetCard(item.SourceCard).Name}", new[] { SpecAt(ability, 0) },
                    new[] { (IReadOnlyList<Target>)options }, CanCancel: true);
                var pick = await ControllerOf(ctx.Controller).ChooseTargetsAsync(ViewFor(ctx.Controller), request);
                if (pick is not { Count: 1 } || !options.Contains(pick[0])) break;
                int index = State.Stack.IndexOf(item);
                State.Stack[index] = item with { Targets = new[] { new ChosenTarget(pick[0], VersionOf(pick[0])) } };
                break;
            }
            case DestroySameName dn:
                foreach (var target in CardsFor(dn.What, ctx).ToList())
                {
                    var name = target.Name;
                    foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Name == name && !c.Has(Keyword.Indestructible)).ToList())
                    {
                        bool creature = card.IsCreature;
                        MoveCard(card.Id, Zone.Graveyard);
                        ctx.Results.Destroyed++;
                        Emit(new PermanentDestroyed(card.Id));
                        if (creature) Emit(new CreatureDied(card.Id));
                    }
                }
                break;
            case DistributeCounters dc:
            {
                var chosen = Enumerable.Range(0, ctx.Targets.Count - ctx.TargetOffset).Select(i => ctx.TargetAt(i)?.Card).Where(c => c is not null).Select(c => State.GetCard(c!.Value)).ToList();
                int remaining = dc.Total;
                for (int i = 0; i < chosen.Count && remaining > 0; i++)
                {
                    int left = chosen.Count - i - 1;
                    int n = left == 0 ? remaining : await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                        new NumberRequest($"Counters on {chosen[i].Name} ({remaining} left)", ctx.Source.Id, 1, remaining - left));
                    Require(n >= 1 && n <= remaining - left, "Each target gets at least one counter.");
                    remaining -= n;
                    PutCounters(chosen[i], CounterKind.PlusOnePlusOne, n, ctx.Controller);
                }
                break;
            }
            case RevealUntil ru:
            {
                var player = State.GetPlayer(ctx.Controller);
                var revealed = new List<CardId>();
                CardId? found = null;
                foreach (var id in player.Library.ToList())
                {
                    if (Matches(ru.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)) { found = id; break; }
                    revealed.Add(id);
                }
                Emit(new CardsRevealed(ctx.Controller, found is { } f ? revealed.Append(f).ToList() : revealed));
                if (found is { } hit)
                {
                    var to = ru.BattlefieldIf is { } toBattlefield
                             && Matches(toBattlefield with { Controller = ControllerFilter.Any }, State.GetCard(hit), ctx.Controller, ctx.Source, ctx.Controller)
                        ? Zone.Battlefield : ru.To;
                    MoveCard(hit, to, controller: ctx.Controller);
                }
                Rng.Shuffle(revealed);
                foreach (var id in revealed) { player.Library.Remove(id); player.Library.Add(id); }
                break;
            }
            case Unless un:
                foreach (var player in PlayersFor(un.Who, ctx).ToList())
                {
                    var payable = un.Options.Where(o => CanPayExtra(player, o, ctx.Source.Id)).ToList();
                    int pick = payable.Count;
                    if (payable.Count > 0)
                    {
                        var labels = payable.Select(DescribeCost).Append("Neither").ToList();
                        pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player), new OptionRequest($"{ctx.Source.Name}: pay a cost, or suffer the effect", ctx.Source.Id, labels, OptionKind.Other));
                        Require(pick >= 0 && pick <= payable.Count, "Choose one of the options.");
                    }
                    if (pick < payable.Count) await PayExtraAsync(player, payable[pick], ctx.Source.Id, causedBy: ctx.Controller);
                    else await ApplyAllAsync(un.Otherwise, ctx with { Trigger = new TriggerInfo(Player: player) });
                }
                break;
            case OpponentMaySacrifice os:
                foreach (var player in State.ApnapOrder().Where(p => p != ctx.Controller).ToList())
                {
                    var candidates = SacrificeCandidates(player, os.Filter, ctx.Source.Id);
                    if (candidates.Count == 0) continue;
                    if (!await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"Sacrifice a creature to {ctx.Source.Name}?", ctx.Source.Id))) continue;
                    var gone = await SacrificeAsync(player, 1, os.Filter, ctx.Source);
                    if (gone.Count == 0) continue;
                    await ApplyAllAsync(os.Effects, ctx);
                    break;
                }
                break;
            case Piles pl:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(pl.Count).ToList();
                if (top.Count == 0) break;
                var options = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                var faceUp = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest("Choose the cards for the face-up pile (the rest go face down)", ctx.Source.Id, options, 0, top.Count, CardChoicePurpose.ToHand));
                Require(faceUp.All(top.Contains) && faceUp.Distinct().Count() == faceUp.Count, "Choose among the top cards.");
                var faceDown = top.Where(id => !faceUp.Contains(id)).ToList();
                var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who picks the pile") ?? State.OpponentsOf(ctx.Controller).First();
                var labels = new[]
                {
                    $"Face-up pile ({faceUp.Count}): {string.Join(", ", faceUp.Select(id => State.GetCard(id).Name))}",
                    $"Face-down pile ({faceDown.Count} cards)",
                };
                int chosen = await ControllerOf(opponent).ChooseOptionAsync(ViewFor(opponent),
                    new OptionRequest($"Choose the pile {player.Name} puts into their hand", ctx.Source.Id, labels, OptionKind.Other));
                var toHand = chosen == 0 ? faceUp : faceDown;
                foreach (var id in toHand) MoveCard(id, Zone.Hand);
                foreach (var id in top.Where(id => !toHand.Contains(id))) MoveCard(id, Zone.Graveyard);
                break;
            }
            case WinGame:
                foreach (var opponent in State.OpponentsOf(ctx.Controller).ToList())
                    if (!Has(opponent, Replacements.YouCantLose)) Lose(opponent, $"{State.GetPlayer(ctx.Controller).Name} won the game");
                break;
            case UntapUpTo uu:
            {
                var tapped = State.PermanentsControlledBy(ctx.Controller).Where(c => c.Tapped && Matches(uu.Filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                if (tapped.Count == 0) break;
                var options = tapped.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList();
                var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest($"Untap up to {uu.Count}", ctx.Source.Id, options, 0, Math.Min(uu.Count, tapped.Count), CardChoicePurpose.ToBattlefield));
                foreach (var id in chosen.Where(id => tapped.Any(c => c.Id == id)))
                {
                    State.GetCard(id).Tapped = false;
                    Emit(new PermanentUntapped(id));
                }
                break;
            }
            case AddPoison ap:
                foreach (var player in PlayersFor(ap.Who, ctx))
                {
                    State.GetPlayer(player).Poison += ap.Count;
                    Emit(new PoisonGiven(player, ap.Count));
                }
                break;
            case EndTheTurn:
                // Exile every spell on the stack and drop abilities; skip to the cleanup step (rule 724).
                foreach (var item in State.Stack.ToList())
                {
                    State.Stack.Remove(item);
                    if (item is SpellOnStack sp) MoveCard(sp.Card, Zone.Exile);
                }
                State.Combat = null;
                _endTurnRequested = true;
                break;
            case AdditionalCombat ac:
                _extraCombats++;
                if (ac.UntapCreatures)
                    foreach (var card in State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Tapped).ToList())
                    {
                        card.Tapped = false;
                        Emit(new PermanentUntapped(card.Id));
                    }
                break;
            case CopySpell cs:
            {
                var originalId = cs.What.Kind == SubjectKind.Triggered ? ctx.Trigger?.Subject : CardsFor(cs.What, ctx).FirstOrDefault()?.Id;
                if (originalId is not { } oid || State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == oid) is not { } original) break;
                int copies = Eval(cs.Count, ctx);
                for (int i = 0; i < copies; i++) await CopySpellAsync(original, ctx.Controller);
                break;
            }
            case AddManaOfAnyColor anyColor:
            {
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest("Choose a color of mana", ctx.Source.Id, ColorNames, OptionKind.Color));
                Require(i >= 0 && i < 5, "Choose a color.");
                var type = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }[i];
                for (int n = 0; n < anyColor.Count; n++)
                {
                    State.GetPlayer(ctx.Controller).ManaPool.Add(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            }
            case ReturnExiledWithThis rew:
            {
                var exiled = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id
                                                           && (w.Version == ctx.Source.Version || w.Version == ctx.Source.Version - 1)).ToList();
                if (rew.Count > 0 && exiled.Count > rew.Count)
                {
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest($"Choose {rew.Count} exiled card(s) to put into its owner's hand",
                        ctx.Source.Id, exiled.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), rew.Count, rew.Count, CardChoicePurpose.ToHand));
                    Require(pick.Count == rew.Count && pick.All(id => exiled.Any(c => c.Id == id)), "Choose among the exiled cards.");
                    exiled = exiled.Where(c => pick.Contains(c.Id)).ToList();
                }
                foreach (var card in exiled) MoveCard(card.Id, Zone.Hand);
                break;
            }
            case SearchAndExileWithThis se:
            {
                var player = State.GetPlayer(ctx.Controller);
                var options = player.Library.Select(State.GetCard).Where(c => Matches(se.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller))
                    .Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList();
                if (options.Count > 0)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest($"Search your library: choose up to {se.Count} to exile", ctx.Source.Id, options, 0, Math.Min(se.Count, options.Count), CardChoicePurpose.ToHand));
                    foreach (var id in chosen.Where(id => options.Any(o => o.Id == id)))
                    {
                        MoveCard(id, Zone.Exile);
                        State.GetCard(id).ExiledWith = (ctx.Source.Id, ctx.Source.Version);
                    }
                }
                Shuffle(player);
                break;
            }
            case GrantFlashback gf:
                foreach (var card in CardsFor(gf.What, ctx).Where(c => c.Zone == Zone.Graveyard))
                    State.FlashbackGranted.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber));
                break;
            case PlayableFromGraveyardThisTurn pg:
                foreach (var card in CardsFor(pg.What, ctx).Where(c => c.Zone == Zone.Graveyard))
                    State.PlayableFromGraveyard.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber));
                break;
            case Become b:
            {
                if (b.WhileSourceRemains && ctx.Source.Zone != Zone.Battlefield) break;
                int? power = b.Power ?? (b.PowerFrom is { } pf && !b.Continuous ? Eval(pf, ctx) : null);
                int? toughness = b.Toughness ?? (b.ToughnessFrom is { } tf && !b.Continuous ? Eval(tf, ctx) : null);
                foreach (var card in CardsFor(b.What, ctx).ToList())
                {
                    var becomes = new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, b.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                    {
                        AddTypes = b.AddTypes, SetPower = power, SetToughness = toughness, AddSubtypes = b.AddSubtypes, SetSubtypes = b.SetSubtypes,
                        SetPowerFrom = b.Continuous ? b.PowerFrom : null, SetToughnessFrom = b.Continuous ? b.ToughnessFrom : null,
                        Abilities = b.Abilities?.Select(a => BindGranter(a, ctx.Controller)).ToList(),
                        WhileSource = b.WhileSourceRemains ? (ctx.Source.Id, ctx.Source.Version) : null,
                        Timestamp = NewTimestamp(),
                    };
                    (b.Permanent || b.WhileSourceRemains ? State.LastingEffects : State.UntilEndOfTurn).Add(becomes);
                }
                break;
            }
            case MillUntil mu:
                foreach (var player in PlayersFor(mu.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(player).Library;
                    while (library.Count > 0)
                    {
                        var top = State.GetCard(library[0]);
                        bool found = Matches(mu.Until with { Controller = ControllerFilter.Any }, top, player, ctx.Source, ctx.Controller);
                        MoveCard(top.Id, Zone.Graveyard);
                        if (found) break;
                    }
                }
                break;
            case ExileGraveyard eg:
                foreach (var player in PlayersFor(eg.Who, ctx).ToList())
                    foreach (var id in State.GetPlayer(player).Graveyard.ToList()) MoveCard(id, Zone.Exile);
                break;
            case DoubleCounters dbl:
                foreach (var card in CardsFor(dbl.What, ctx).ToList())
                    foreach (var (kind, have) in card.Counters.Where(kv => kv.Value > 0 && (dbl.Kind is null || kv.Key == dbl.Kind)).ToList())
                        PutCounters(card, kind, have, ctx.Controller);
                break;
            case RemoveCounters rc:
            {
                int count = Eval(rc.Count, ctx);
                foreach (var card in CardsFor(rc.What, ctx)) card.Counters[rc.Kind] = Math.Max(0, card.CounterCount(rc.Kind) - count);
                break;
            }
            case ShuffleGraveyardIntoLibrary sg:
                foreach (var player in PlayersFor(sg.Who, ctx).ToList())
                {
                    foreach (var id in State.GetPlayer(player).Graveyard.ToList()) MoveCard(id, Zone.Library);
                    Shuffle(State.GetPlayer(player));
                }
                break;
            case AddMana am:
                foreach (var type in am.Types)
                {
                    State.GetPlayer(ctx.Controller).ManaPool.Add(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            case DealsDamageEqualToPower dp:
            {
                var biter = CardsFor(dp.Source, ctx).FirstOrDefault();
                if (biter is null || !biter.IsCreature) break;
                int power = biter.Power;
                foreach (var card in CardsFor(dp.To, ctx).ToList()) DamageCreature(biter, card, power);
                foreach (var player in PlayersFor(dp.To, ctx)) DamagePlayer(biter, player, power);
                break;
            }
            case ReanimateAll ra:
                BeginEnteringTogether();
                foreach (var player in PlayersFor(ra.Who, ctx).ToList())
                    foreach (var card in State.GetPlayer(player).Graveyard.Select(State.GetCard)
                                 .Where(c => Matches(ra.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller)).ToList())
                    {
                        MoveCard(card.Id, Zone.Battlefield, controller: ctx.Controller);
                        if (ra.As is { } becomes) BecomeAsItEnters(card, becomes, ctx);
                    }
                EndEnteringTogether();
                break;
            case BounceAll ba:
            {
                var relative = ba.RelativeTo is { } rel ? PlayersFor(rel, ctx).FirstOrDefault() : ctx.Controller;
                foreach (var card in State.Battlefield.Select(State.GetCard)
                             .Where(c => Matches(ba.Filter, c, c.Controller, ctx.Source, relative)).ToList())
                    MoveCard(card.Id, Zone.Hand);
                break;
            }
            case PutOntoBattlefield p:
            {
                // "Return it": only the same object (a card that changed zones again is a new object, rule 400.7).
                var cards = p.What.Kind == SubjectKind.Self
                    ? (ctx.Source.Zone is Zone.Graveyard or Zone.Hand or Zone.Exile && (ctx.SourceVersion is not { } sv || sv == ctx.Source.Version)
                        ? new[] { ctx.Source } : Array.Empty<Card>())
                    : p.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc
                      && tc.Version == ti.SubjectVersion && tc.Zone != Zone.Battlefield ? new[] { tc }
                    : CardsFor(p.What, ctx).Where(c => c.Zone != Zone.Battlefield).ToArray();
                BeginEnteringTogether();
                var attachTo = p.AttachTo is { } host ? CardsFor(host, ctx).FirstOrDefault()?.Id : null;
                if (p.AttachTo is not null && attachTo is null) break; // an Aura returned attached to a creature that is gone stays where it is
                foreach (var card in cards)
                {
                    MoveCard(card.Id, Zone.Battlefield, controller: p.UnderOwnersControl ? card.Owner : ctx.Controller, attachTo: attachTo);
                    if (p.Tapped && !card.Tapped) card.Tapped = true;
                    BecomeAsItEnters(card, p, ctx);
                    if (p.Counters > 0) PutCounters(card, p.CounterKind, p.Counters, ctx.Controller);
                    if (p.AddSubtypes is not null || p.AddKeywords is not null)
                        State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, p.AddKeywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                        {
                            AddSubtypes = p.AddSubtypes, Timestamp = NewTimestamp(),
                        });
                    RecomputeContinuousEffects();
                }
                EndEnteringTogether();
                break;
            }
            case SearchLibrary sl:
                foreach (var searcher in (sl.Who is { } who ? PlayersFor(who, ctx) : new[] { ctx.Controller }).ToList())
                {
                    if (sl.Optional && !await ControllerOf(searcher).ChooseYesNoAsync(ViewFor(searcher), new YesNoRequest("Search your library?", ctx.Source.Id))) continue;
                    var search = sl.MaxManaValueX ? sl with { Filter = sl.Filter with { MaxManaValue = ctx.X } } : sl;
                    if (sl.CountFrom is { } countFrom) search = search with { Count = Eval(countFrom, ctx) };
                    if (search.Count <= 0) continue;
                    ctx.Results.Found.AddRange(await SearchLibraryAsync(searcher, search, ctx.Source));
                }
                break;
            case Sacrifice sac:
            {
                // Each player chooses in turn order, then everything chosen is sacrificed at the same time (rule 101.4).
                var players = PlayersFor(sac.Who, ctx).ToList();
                var choices = new List<(PlayerId Player, IReadOnlyList<CardId> Chosen)>();
                foreach (var player in State.ApnapOrder().Where(players.Contains).ToList())
                    choices.Add((player, await ChooseSacrificesAsync(player, Eval(sac.Count, ctx), sac.Filter, ctx.Source)));
                BeginSimultaneous();
                foreach (var (player, chosen) in choices)
                {
                    foreach (var id in chosen) SacrificePermanent(id);
                    ctx.Results.Sacrificed.AddRange(chosen);
                    if (player == ctx.Controller && chosen.Count > 0) ctx.Results.YouSacrificed = true;
                }
                EndSimultaneous();
                break;
            }
            case PutIntoLibrary pl:
                foreach (var card in (pl.What.Kind == SubjectKind.Self && ctx.Source.Zone == Zone.Graveyard ? new[] { ctx.Source } : CardsFor(pl.What, ctx)).ToList())
                {
                    bool bottom = !pl.Top && (pl.Bottom || await ControllerOf(card.Owner).ChooseYesNoAsync(ViewFor(card.Owner),
                        new YesNoRequest($"Put {card.Name} on the bottom of your library? (No: on top)", card.Id)));
                    MoveCard(card.Id, Zone.Library, toBottom: bottom);
                }
                break;
            case GainControl g:
            {
                PlayerId? chosen = g.NewController is { Kind: SubjectKind.EachOpponent }
                    ? await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who gains control") ?? ctx.Controller
                    : g.NewController is { } who ? PlayersFor(who, ctx).Cast<PlayerId?>().FirstOrDefault() : ctx.Controller;
                // The player who would gain control is an illegal target now: nobody gains control (rule 608.2b).
                if (chosen is not { } newController) break;
                foreach (var card in CardsFor(g.What, ctx).Where(c => c.Controller != newController).ToList())
                {
                    if (g.UntilEndOfTurn) State.TemporaryControl.Add(new TemporaryControlEffect(card.Id, card.Version, card.Controller));
                    card.Controller = newController;
                    card.BaseController = newController;
                    card.ControlledSinceTurnStart = false;
                    State.Combat?.Remove(card.Id);
                    Emit(new ControlChanged(card.Id, newController));
                }
            }
                break;
            case Scry sc:
                await LookAtTopAsync(ctx.Controller, sc.Count, CardChoicePurpose.ScryToBottom, ctx.Source);
                break;
            case Surveil sv:
                await LookAtTopAsync(ctx.Controller, sv.Count, CardChoicePurpose.SurveilToGraveyard, ctx.Source);
                break;
            case Fight f:
            {
                var first = CardsFor(f.First, ctx).FirstOrDefault();
                var second = CardsFor(f.Second, ctx).FirstOrDefault();
                // If either creature is gone (or no longer a creature), no damage is dealt (rule 701.14b).
                if (first is null || second is null || !first.IsCreature || !second.IsCreature) break;
                int firstPower = first.Power, secondPower = second.Power;
                DamageCreature(first, second, firstPower);
                if (first.Id != second.Id) DamageCreature(second, first, secondPower);
                break;
            }
            case Discard d:
                foreach (var player in PlayersFor(d.Who, ctx)) ctx.Results.Discarded.AddRange(await DiscardAsync(player, Eval(d.Count, ctx), ctx.Controller));
                break;
            case IfThen c:
                await ApplyAllAsync(HoldsIn(c.Condition, ctx) ? c.Then : c.Else ?? Array.Empty<Effect>(), ctx);
                break;
            case MayDo m:
                if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(m.Prompt, ctx.Source.Id)))
                    await ApplyAllAsync(m.Effects, ctx);
                break;
            case DealDamage d:
            {
                int amount = Eval(d.Amount, ctx);
                foreach (var card in CardsFor(d.To, ctx).ToList())
                {
                    // Lethal damage is 1 from a deathtouch source (rule 702.2c).
                    int before = card.Damage, lethal = Math.Max(0, card.Toughness - card.Damage);
                    if (ctx.Source.Has(Keyword.Deathtouch) && lethal > 0) lethal = 1;
                    DamageCreature(ctx.Source, card, amount);
                    if (card.IsCreature) ctx.Results.ExcessDamage += Math.Max(0, card.Damage - before - lethal);
                }
                foreach (var player in PlayersFor(d.To, ctx)) DamagePlayer(ctx.Source, player, amount);
            }
                break;
            case DrawCards d:
                foreach (var player in PlayersFor(d.Who, ctx)) Draw(player, Eval(d.Count, ctx));
                break;
            case GainLife g:
                foreach (var player in PlayersFor(g.Who, ctx)) GainLifeFor(player, Eval(g.Amount, ctx));
                break;
            case LoseLife l:
                foreach (var player in PlayersFor(l.Who, ctx))
                {
                    int before = State.GetPlayer(player).Life;
                    ChangeLife(player, -Eval(l.Amount, ctx));
                    ctx.Results.LifeLost += Math.Max(0, before - State.GetPlayer(player).Life);
                }
                break;
            case Destroy d:
                BeginSimultaneous();
                foreach (var card in CardsFor(d.What, ctx).Where(c => !c.Has(Keyword.Indestructible)).ToList())
                {
                    bool creature = card.IsCreature;
                    MoveCard(card.Id, Zone.Graveyard);
                    ctx.Results.Destroyed++;
                    Emit(new PermanentDestroyed(card.Id));
                    if (creature) Emit(new CreatureDied(card.Id));
                }
                EndSimultaneous();
                break;
            case ExileIt x:
            {
                var exiling = x.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc && tc.Version == ti.SubjectVersion
                    ? new[] { tc }
                    : CardsFor(x.What, ctx).ToArray();
                foreach (var card in exiling.Where(c => !(x.ExceptCreatedThisWay && ctx.Results.Created.Contains(c.Id))))
                {
                    MoveCard(card.Id, Zone.Exile);
                    ctx.Results.Exiled.Add(card.Id);
                    if (x.WithCounter is { } kind && card.Zone == Zone.Exile) card.Counters[kind] = card.CounterCount(kind) + 1;
                }
                break;
            }
            case LoseGame:
                if (!Has(ctx.Controller, Replacements.YouCantLose)) Lose(ctx.Controller, $"{ctx.Source.Name}");
                break;
            case ReturnToHand r:
                if (r.What.Kind == SubjectKind.Self && ctx.Source.Zone == Zone.Graveyard) MoveCard(ctx.Source.Id, Zone.Hand); // "return this card"
                foreach (var card in CardsFor(r.What, ctx).ToList())
                {
                    MoveCard(card.Id, Zone.Hand);
                    ctx.Results.Returned.Add(card.Id);
                }
                break;
            case MayPay mp:
            {
                var payer = State.GetPlayer(ctx.Controller);
                bool canPay = (mp.Mana is null || Payable(ctx.Controller, mp.Mana, null)) && CanPayExtra(ctx.Controller, mp.Extra, ctx.Source.Id);
                if (!canPay) break;
                if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(mp.Prompt, ctx.Source.Id))) break;
                if (mp.Mana is { } mana && !await PayManaAsync(payer, ctx.Source.Id, mana, null)) break;
                await PayExtraAsync(ctx.Controller, mp.Extra, ctx.Source.Id);
                await ApplyAllAsync(mp.Effects, ctx);
                break;
            }
            case ReturnFromGraveyard rg:
            {
                var who = ctx.Controller;
                var options = State.GetPlayer(who).Graveyard.Select(State.GetCard)
                    .Where(c => !(rg.ExcludeSacrificed && ctx.Results.Sacrificed.Contains(c.Id)))
                    .Where(c => rg.Filter is null || Matches(rg.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who))
                    .Select(c => ViewBuilder.Card(State, c.Id, who)).ToList();
                if (options.Count == 0) break;
                int max = Math.Min(rg.Count, options.Count);
                var purpose = rg.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand;
                var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who),
                    new CardChoiceRequest($"Choose {(rg.UpTo ? "up to " : "")}{max} from your graveyard", ctx.Source.Id, options, rg.UpTo ? 0 : max, max, purpose));
                Require(chosen.Count <= max && (rg.UpTo || chosen.Count == max) && chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(o => o.Id == id)),
                    "Choose among the listed cards.");
                foreach (var id in chosen) MoveCard(id, rg.To, controller: who);
                break;
            }
            case DiscardHand dh:
                foreach (var player in PlayersFor(dh.Who, ctx).ToList())
                    foreach (var id in State.GetPlayer(player).Hand.ToList())
                    {
                        DiscardCard(player, id, ctx.Controller);
                        ctx.Results.Discarded.Add(id);
                    }
                break;
            case TapIt t:
                foreach (var card in CardsFor(t.What, ctx).Where(c => !c.Tapped)) { card.Tapped = true; Emit(new PermanentTapped(card.Id)); }
                break;
            case UntapIt u:
                foreach (var card in CardsFor(u.What, ctx).Where(c => c.Tapped)) { card.Tapped = false; Emit(new PermanentUntapped(card.Id)); }
                break;
            case Mill m:
                foreach (var player in PlayersFor(m.Who, ctx).ToList())
                {
                    bool again;
                    do
                    {
                        var milled = new List<Card>();
                        foreach (var id in State.GetPlayer(player).Library.Take(Eval(m.Count, ctx)).ToList())
                        {
                            MoveCard(id, Zone.Graveyard);
                            // A card a replacement effect sent elsewhere wasn't put into the graveyard "this way".
                            if (State.GetCard(id).Zone != Zone.Graveyard) continue;
                            ctx.Results.Milled.Add(id);
                            milled.Add(State.GetCard(id));
                        }
                        var nonland = milled.Where(c => !c.Is(CardType.Land)).ToList();
                        again = m.RepeatWhileNonlandShareColor && nonland.Count >= 2
                                && nonland.SelectMany(c => c.Colors).GroupBy(c => c).Any(g => g.Count() >= 2);
                    }
                    while (again && State.GetPlayer(player).Library.Count > 0);
                }
                break;
            case CounterSpell { UnlessPays: { } tax } c:
                if (c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.Card is { } taxed
                    && State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == taxed) is { } taxedItem)
                    await ApplyAsync(new CounterUnlessPays(taxedItem.Id, tax, 0), ctx);
                break;
            case CounterSpell c:
                if (c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.Card is { } spellCard && CanBeCountered(State.GetCard(spellCard)))
                {
                    var countered = State.GetCard(spellCard);
                    bool permanent = countered.Types.IsPermanent() && !countered.Definition.IsToken;
                    CounterSpellOnStack(spellCard);
                    if (c.ExilePermanentPlayable && permanent && countered.Zone != Zone.Exile)
                    {
                        // "Exile it instead …; you may cast that card without paying its mana cost for as long as it remains exiled."
                        MoveCard(spellCard, Zone.Exile);
                        if (countered.Zone == Zone.Exile)
                            State.PlayableFromExile.Add(new PlayableFromExile(spellCard, countered.Version, ctx.Controller, int.MaxValue, WithoutPaying: true));
                    }
                }
                break;
            case PumpUntilEndOfTurn p:
            {
                if (p.WhileSourceRemains && ctx.Source.Zone != Zone.Battlefield) break;
                int power = Eval(p.Power, ctx), toughness = Eval(p.Toughness, ctx);
                foreach (var card in CardsFor(p.What, ctx).Where(c => c.IsCreature || (power == 0 && toughness == 0)))
                    (p.WhileSourceRemains ? State.LastingEffects : State.UntilEndOfTurn).Add(new UntilEndOfTurnEffect(card.Id, card.Version, power, toughness,
                        p.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                    {
                        Timestamp = NewTimestamp(), WhileSource = p.WhileSourceRemains ? (ctx.Source.Id, ctx.Source.Version) : null,
                    });
            }
                break;
            case AddCounters a:
            {
                int count = Eval(a.Count, ctx);
                if (count <= 0) break;
                foreach (var card in CardsFor(a.What, ctx)) PutCounters(card, a.Kind, count, ctx.Controller);
            }
                break;
            case AttachSelf a:
                if (ctx.Source.Zone == Zone.Battlefield)
                    foreach (var card in CardsFor(a.To, ctx))
                    {
                        ctx.Source.AttachedTo = card.Id;
                        ctx.Source.Timestamp = 0; // becoming attached gives it a new timestamp (rule 613.7e)
                    }
                break;
            case Recruit rc:
                foreach (var player in PlayersFor(rc.Who, ctx).ToList())
                {
                    Draw(player);
                    var discarded = await DiscardAsync(player, 1, player);
                    ctx.Results.Discarded.AddRange(discarded);
                    if (!discarded.Any(id => !State.GetCard(id).Is(CardType.Land))) continue;
                    BeginEnteringTogether();
                    for (int i = 0, n = Has(player, Replacements.DoubleTokens) ? 2 : 1; i < n; i++)
                        if (CreateToken(rc.Token, player) is { } soldier) ctx.Results.Created.Add(soldier);
                    EndEnteringTogether();
                }
                break;
            case Attach at:
            {
                var host = CardsFor(at.To, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (host is null) break;
                IEnumerable<Card> attaching = CardsFor(at.What, ctx);
                if (at.What.Kind == SubjectKind.ChooseOne && at.What.Filter is { } choose)
                {
                    var options = State.Battlefield.Select(State.GetCard)
                        .Where(c => c.Id != host.Id && Matches(choose, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0) break;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"Choose what to attach to {host.Name} (or none)", ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(),
                        0, 1, CardChoicePurpose.ToBattlefield));
                    attaching = options.Where(c => pick.Contains(c.Id));
                }
                foreach (var card in attaching.Where(c => c.Zone == Zone.Battlefield && c.Id != host.Id).ToList())
                {
                    // Equipment attaches only to creatures; an Aura only to what it can enchant (rule 701.3b).
                    if (card.HasSubtype("Equipment") && !host.IsCreature) continue;
                    if (card.Definition.EnchantTarget is { } enchant && !MatchesKind(host, enchant.Kind)) continue;
                    if (card.AttachedTo == host.Id) continue;
                    card.AttachedTo = host.Id;
                    card.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                }
                break;
            }
            case TakeMilled tm:
            {
                var who = ctx.Controller;
                var eligible = ctx.Results.Milled.Select(State.GetCard)
                    .Where(c => c.Zone == Zone.Graveyard && (tm.Filter is null || Matches(tm.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, who))).ToList();
                if (eligible.Count == 0) break;
                IReadOnlyList<CardId> taken = eligible.Select(c => c.Id).ToList();
                if (tm.Count >= 0)
                {
                    int max = Math.Min(tm.Count, eligible.Count);
                    taken = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Put up to {max} into your hand", ctx.Source.Id,
                        eligible.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, max, CardChoicePurpose.ToHand));
                    Require(taken.Count <= max && taken.Distinct().Count() == taken.Count && taken.All(id => eligible.Any(c => c.Id == id)), "Choose among the milled cards.");
                }
                foreach (var id in taken) MoveCard(id, Zone.Hand);
                break;
            }
            case RemoveAllCounters rac:
                foreach (var card in CardsFor(rac.What, ctx)) card.Counters.Clear();
                break;
            case Blink bl:
            {
                var blinked = new List<Card>();
                foreach (var card in CardsFor(bl.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    MoveCard(card.Id, Zone.Exile);
                    if (card.Zone == Zone.Exile && !card.Definition.IsToken) blinked.Add(card);
                }
                BeginEnteringTogether();
                foreach (var card in blinked) MoveCard(card.Id, Zone.Battlefield, controller: card.Owner);
                EndEnteringTogether();
                break;
            }
            case ShuffleIntoLibrary sil:
                foreach (var card in (sil.What.Kind == SubjectKind.Self && ctx.Source.Zone != Zone.Library ? new[] { ctx.Source } : CardsFor(sil.What, ctx)).ToList())
                {
                    MoveCard(card.Id, Zone.Library);
                    Shuffle(State.GetPlayer(card.Owner));
                }
                break;
            case AdditionalLandThisTurn:
                State.GetPlayer(ctx.Controller).ExtraLandsThisTurn++;
                break;
            case NoSpellsThisTurn:
                State.SpellsForbiddenTurn = State.TurnNumber;
                break;
            case ExchangeControl ex:
            {
                var first = CardsFor(ex.First, ctx).FirstOrDefault();
                var second = CardsFor(ex.Second, ctx).FirstOrDefault();
                // Both must still be there, or nothing happens (rule 701.12b).
                if (first is null || second is null || first.Controller == second.Controller) break;
                var (a, b) = (first.Controller, second.Controller);
                foreach (var (card, to) in new[] { (first, b), (second, a) })
                {
                    card.Controller = card.BaseController = to;
                    card.ControlledSinceTurnStart = false;
                    State.Combat?.Remove(card.Id);
                    Emit(new ControlChanged(card.Id, to));
                }
                break;
            }
            case AtNextUpkeep nu:
                State.AtNextUpkeep.Add(new DelayedTrigger(ctx.Source.Id, nu.Ability, ctx.Controller, nu.Amount is { } nuAmount ? Eval(nuAmount, ctx) : 0));
                break;
            case ChooseCreatureType:
            {
                var types = CreatureTypeOptions(ctx.Controller);
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose a creature type", ctx.Source.Id, types, OptionKind.CreatureType));
                Require(i >= 0 && i < types.Count, "Choose one of the creature types.");
                ctx.Source.ChosenType = types[i];
                Emit(new ChoiceMade(ctx.Source.Id, types[i]));
                break;
            }
            case RevealTopPutRandom rr:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(rr.Count).ToList();
                if (top.Count == 0) break;
                Emit(new CardsRevealed(ctx.Controller, top));
                var hits = top.Where(id => Matches(rr.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                CardId? picked = hits.Count > 0 ? hits[Rng.Next(hits.Count)] : null;
                if (picked is { } chosen) MoveCard(chosen, rr.To, controller: ctx.Controller);
                var rest = top.Where(id => id != picked).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                break;
            }
            case SearchHandOrLibrary shl:
            {
                var who = ctx.Controller;
                var player = State.GetPlayer(who);
                var filter = shl.Filter with { Controller = ControllerFilter.Any };
                var options = player.Hand.Concat(player.Library).Select(State.GetCard).Where(c => Matches(filter, c, who, ctx.Source, who))
                    .Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList();
                if (options.Count > 0)
                {
                    var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Search your hand and library: choose a card", ctx.Source.Id,
                        options, 0, 1, shl.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand));
                    Require(pick.Count <= 1 && pick.All(id => options.Any(o => o.Id == id)), "Choose one of the matching cards.");
                    foreach (var id in pick) MoveCard(id, shl.To, controller: who);
                }
                Shuffle(player);
                break;
            }
            case AddManaInAnyCombination amc:
                for (int n = 0; n < amc.Count; n++)
                {
                    int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"Choose the color of mana {n + 1} of {amc.Count}", ctx.Source.Id, ColorNames, OptionKind.Color));
                    Require(i >= 0 && i < 5, "Choose a color.");
                    var type = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }[i];
                    var pool = State.GetPlayer(ctx.Controller).ManaPool;
                    if (amc.OnlyFor is { } only) pool.AddSpecial(new ManaUnit(type, ctx.Source.Id, only, false, ManaRider.None));
                    else pool.Add(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            case Behold bh:
            {
                var who = ctx.Controller;
                var filter = bh.Filter with { Controller = ControllerFilter.Any };
                var options = State.PermanentsControlledBy(who).Where(c => Matches(filter, c, who, ctx.Source, who))
                    .Concat(State.GetPlayer(who).Hand.Select(State.GetCard).Where(c => Matches(filter, c, who, ctx.Source, who))).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Behold: choose one you control or reveal one from your hand (or none)", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.Keep));
                if (pick.Count != 1 || options.All(c => c.Id != pick[0])) break;
                if (State.GetCard(pick[0]).Zone == Zone.Hand) Emit(new CardsRevealed(who, pick.ToList()));
                await ApplyAllAsync(bh.Effects, ctx);
                break;
            }
            case CastFromGraveyardNow cg:
            {
                var who = ctx.Controller;
                var player = State.GetPlayer(who);
                var castable = player.Graveyard.Select(State.GetCard)
                    .Where(c => !c.Is(CardType.Land) && Matches(cg.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)
                                && HasLegalTargets(CastingTargets(c.Definition), who, c.Id) && Payable(who, CastingCost(c).WithX(0), null))
                    .ToList();
                if (castable.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("You may cast a spell from your graveyard", ctx.Source.Id,
                    castable.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                if (pick.Count != 1 || castable.All(c => c.Id != pick[0])) break;
                var card = State.GetCard(pick[0]);
                State.PlayableFromGraveyard.Add(new PlayableFromExile(card.Id, card.Version, who, State.TurnNumber));
                await CastSpellAsync(player, card.Id, exileAfter: true);
                break;
            }
            case PreventDamageBy pd:
                if (ctx.Source.Zone != Zone.Battlefield) break;
                foreach (var card in CardsFor(pd.What, ctx))
                    State.DamagePreventions.Add(new DamagePrevention(card.Id, card.Version, ctx.Source.Id, ctx.Source.Version));
                break;
            case Amass am:
                foreach (var player in PlayersFor(am.Who, ctx).ToList())
                    await AmassAsync(player, am, Eval(am.Count, ctx), ctx);
                break;
            case CreateTokens t:
            {
                int count = Eval(t.Count, ctx);
                BeginEnteringTogether();
                foreach (var player in PlayersFor(t.Controller, ctx))
                {
                    int made = count * (Has(player, Replacements.DoubleTokens) ? 2 : 1);
                    for (int i = 0; i < made; i++)
                        if (CreateToken(t.Token, player, t.Tapped) is { } token)
                        {
                            ctx.Results.Created.Add(token);
                            if (t.HasteUntilEndOfTurn)
                                State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(token, State.GetCard(token).Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                        }
                }
                EndEnteringTogether();
            }
                break;
            default:
                throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
        }
    }

    /// <summary>What a card put onto the battlefield becomes as it enters ("They're an artifact", "They are Food artifacts with …").</summary>
    private void BecomeAsItEnters(Card card, PutOntoBattlefield p, EffectContext ctx)
    {
        if (card.Zone != Zone.Battlefield || (p.SetTypes is null && p.SetSubtypes is null && p.AddAbilities is null)) return;
        State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>())
        {
            SetTypes = p.SetTypes, SetSubtypes = p.SetSubtypes ?? (p.SetTypes is not null ? Array.Empty<string>() : null),
            Abilities = p.AddAbilities?.Select(a => BindGranter(a, ctx.Controller)).ToList(), Timestamp = NewTimestamp(),
        });
        RecomputeContinuousEffects();
    }

    /// <summary>Amass (rule 701.47): an Army gets the counters, creating one first if the player has none.</summary>
    private async Task AmassAsync(PlayerId player, Amass amass, int count, EffectContext ctx)
    {
        bool IsArmy(Card c) => c.IsCreature && c.HasSubtype("Army");
        if (!State.PermanentsControlledBy(player).Any(IsArmy))
        {
            BeginEnteringTogether();
            int made = Has(player, Replacements.DoubleTokens) ? 2 : 1;
            for (int i = 0; i < made; i++)
                if (CreateToken(amass.Token, player) is { } token) ctx.Results.Created.Add(token);
            EndEnteringTogether();
        }
        var armies = State.PermanentsControlledBy(player).Where(IsArmy).ToList();
        if (armies.Count == 0) return;
        var army = armies[0];
        if (armies.Count > 1)
        {
            var options = armies.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList();
            var pick = await ControllerOf(player).ChooseCardsAsync(ViewFor(player),
                new CardChoiceRequest($"Amass {amass.Subtype}s {count}: choose an Army", ctx.Source.Id, options, 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && armies.Any(c => c.Id == pick[0]), "Choose one of your Armies.");
            army = State.GetCard(pick[0]);
        }
        PutCounters(army, CounterKind.PlusOnePlusOne, count, ctx.Controller);
        if (!army.HasSubtype(amass.Subtype))
            State.LastingEffects.Add(new UntilEndOfTurnEffect(army.Id, army.Version, 0, 0, Array.Empty<Keyword>())
            {
                AddSubtypes = new[] { amass.Subtype }, Timestamp = NewTimestamp(),
            });
        ctx.Results.Amassed = army.Id;
    }

    /// <summary>Non-combat damage from a spell or ability (rule 120), with deathtouch and lifelink.</summary>
    private void DamageCreature(Card source, Card target, int amount)
    {
        if (target.Zone != Zone.Battlefield || !(target.IsCreature || target.Is(CardType.Planeswalker))) return;
        amount = ModifyDamage(source, target, null, amount, combat: false);
        if (amount <= 0) return;
        if (!target.IsCreature)
        {
            DamagePlaneswalker(source, target, amount, combat: false);
            return;
        }
        target.Damage += amount;
        if (source.Has(Keyword.Deathtouch)) target.DamagedByDeathtouch = true;
        Emit(new DamageDealt(source.Id, target.Id, null, amount));
        if (source.Has(Keyword.Lifelink)) GainLifeFor(source.Controller, amount);
    }

    /// <summary>Damage to a planeswalker removes that many loyalty counters (rule 120.3c).</summary>
    private void DamagePlaneswalker(Card source, Card walker, int amount, bool combat)
    {
        walker.Counters[CounterKind.Loyalty] = Math.Max(0, walker.CounterCount(CounterKind.Loyalty) - amount);
        Emit(new DamageDealt(source.Id, walker.Id, null, amount, combat));
        if (source.Has(Keyword.Lifelink)) GainLifeFor(source.Controller, amount);
    }

    private void DamagePlayer(Card source, PlayerId player, int amount)
    {
        amount = ModifyDamage(source, null, player, amount, combat: false);
        if (amount <= 0) return;
        Emit(new DamageDealt(source.Id, null, player, amount));
        ChangeLife(player, -amount);
        if (source.Has(Keyword.Lifelink)) GainLifeFor(source.Controller, amount);
    }

    private CardId? CreateToken(CardDefinition definition, PlayerId controller, bool tapped = false)
    {
        var token = definition with { IsToken = true };
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        var card = new Card(id, token, controller) { Zone = Zone.Battlefield, Tapped = tapped || token.EntersTapped };
        State.Cards.Add(id, card);
        State.Battlefield.Add(id);
        if (definition.IsCreature() && OpponentsCreaturesEnterTapped(controller)) card.Tapped = true;
        Emit(new TokenCreated(id, controller));
        Emit(new CardMoved(id, controller, Zone.Exile, Zone.Battlefield, controller));
        return id;
    }

    /// <summary>Puts counters on a permanent (doubled by "twice that many counters" effects of its controller).</summary>
    private void PutCounters(Card card, CounterKind kind, int count, PlayerId? placedBy = null)
    {
        if (count <= 0) return;
        if (Has(card.Controller, Replacements.DoubleCounters)) count *= 2;
        card.Counters[kind] = card.CounterCount(kind) + count;
        Emit(new CountersPlaced(card.Id, kind, count, placedBy));
    }

    /// <summary>Whether a player controls a permanent with this replacement effect.</summary>
    private bool Has(PlayerId player, Replacements replacement) =>
        State.PermanentsControlledBy(player).Any(c => (c.Definition.Replaces & replacement) != 0);

    private bool OpponentsCreaturesEnterTapped(PlayerId controller) =>
        State.OpponentsOf(controller).Any(o => Has(o, Replacements.OpponentsCreaturesEnterTapped));

    /// <summary>Damage after replacement and prevention effects (rules 614, 615).</summary>
    private int ModifyDamage(Card source, Card? targetCard, PlayerId? targetPlayer, int amount, bool combat)
    {
        if (amount <= 0) return 0;
        if (State.DamagePreventions.Any(d => d.Card == source.Id && d.Version == source.Version)) return 0; // "prevent all damage that would be dealt by"
        if (targetCard is not null && ProtectedFrom(targetCard, source)) return 0; // 702.16e
        if (combat)
        {
            if ((source.Definition.Replaces & Replacements.PreventCombatDamageToAndBySelf) != 0) return 0;
            if (targetCard is not null && ((targetCard.Definition.Replaces & Replacements.PreventCombatDamageToAndBySelf) != 0
                                           || State.CombatDamagePrevented.Contains((targetCard.Id, targetCard.Version)))) return 0;
        }
        else if (targetCard is not null && targetCard.IsCreature
                 && State.PermanentsControlledBy(targetCard.Controller).Any(c => c.Id != targetCard.Id && (c.Definition.Replaces & Replacements.PreventNoncombatDamageToYourOtherCreatures) != 0))
            return 0;
        var victim = targetPlayer ?? targetCard!.Controller;
        // A source that just left the battlefield (sacrificed to pay for its ability) is used as it last existed there.
        var lki = source.Zone is not (Zone.Battlefield or Zone.Stack) && source.ZoneChangedTurn == State.TurnNumber ? source.LastKnownInfo : null;
        var controller = lki?.Controller ?? source.Controller;
        bool creature = lki is not null ? (lki.Types & CardType.Creature) != 0 : source.IsCreature && source.Zone == Zone.Battlefield;
        if (victim != controller && Has(controller, Replacements.DoubleDamageToOpponents)) amount *= 2;
        if (creature && Has(controller, Replacements.DoubleCreatureDamage)) amount *= 2;
        return amount;
    }

    /// <summary>Scry or surveil: the player looks at the top cards and picks which ones leave the top.</summary>
    private async Task LookAtTopAsync(PlayerId who, int count, CardChoicePurpose purpose, Card source)
    {
        var player = State.GetPlayer(who);
        var top = player.Library.Take(count).ToList();
        if (top.Count == 0) return;
        bool scry = purpose == CardChoicePurpose.ScryToBottom;
        var prompt = scry
            ? $"Scry {count}: choose cards to put on the bottom of your library"
            : $"Surveil {count}: choose cards to put into your graveyard";
        var options = top.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
        var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(prompt, source.Id, options, 0, top.Count, purpose));
        Require(chosen.Distinct().Count() == chosen.Count && chosen.All(top.Contains), "Choose among the cards looked at.");

        var kept = top.Where(id => !chosen.Contains(id)).ToList();
        foreach (var id in top) player.Library.Remove(id);
        // The player orders the cards left on top, and (scry) those put on the bottom (rule 701.22a).
        foreach (var id in (await OrderAsync(who, kept, "Choose the card to put on top next", source)).Reverse()) player.Library.Insert(0, id);
        if (scry)
            foreach (var id in await OrderAsync(who, chosen.ToList(), "Choose the next card for the bottom (first goes deepest)", source)) player.Library.Add(id);
        else
            foreach (var id in chosen)
            {
                player.Library.Insert(0, id); // briefly back on top so it moves from the library
                MoveCard(id, Zone.Graveyard);
            }
        Emit(new LookedAtTop(who, top.Count, chosen.Count, scry));
    }

    private async Task<IReadOnlyList<CardId>> SearchLibraryAsync(PlayerId who, SearchLibrary search, Card source)
    {
        var player = State.GetPlayer(who);
        var filter = search.Filter with { Controller = ControllerFilter.Any };
        var options = player.Library.Select(State.GetCard).Where(c => Matches(filter, c, who, source, who))
            .Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList();
        if (options.Count > 0)
        {
            var purpose = search.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand;
            var where = search.To switch
            {
                Zone.Battlefield => "onto the battlefield" + (search.Tapped ? " tapped" : ""),
                Zone.Graveyard => "into your graveyard",
                Zone.Library => "on top of your library",
                _ => "into your hand",
            };
            var request = new CardChoiceRequest($"Search your library: choose up to {search.Count} to put {where}", source.Id, options, 0,
                Math.Min(search.Count, options.Count), purpose);
            var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), request);
            Require(chosen.Count <= search.Count && chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(o => o.Id == id)),
                "Choose among the matching cards.");
            if (search.Reveal && chosen.Count > 0) Emit(new CardsRevealed(who, chosen.ToList()));
            // "Put one onto the battlefield tapped and the other into your hand."
            CardId? toBattlefield = null;
            if (search.OneToBattlefieldRestToHand && chosen.Count > 0)
            {
                toBattlefield = chosen[0];
                if (chosen.Count > 1)
                {
                    var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Choose the card to put onto the battlefield tapped", source.Id,
                        chosen.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                    Require(pick.Count == 1 && chosen.Contains(pick[0]), "Choose one of the cards found.");
                    toBattlefield = pick[0];
                }
            }
            var onTop = new List<CardId>();
            BeginEnteringTogether();
            foreach (var id in chosen)
            {
                if (search.To == Zone.Library) { onTop.Add(id); continue; }
                var to = toBattlefield is null ? search.To : id == toBattlefield ? Zone.Battlefield : Zone.Hand;
                MoveCard(id, to, controller: who);
                if (to == Zone.Battlefield && (search.Tapped || toBattlefield is not null)) State.GetCard(id).Tapped = true;
            }
            EndEnteringTogether();
            Shuffle(player);
            foreach (var id in onTop) { player.Library.Remove(id); player.Library.Insert(0, id); }
            return chosen;
        }
        Shuffle(player);
        return Array.Empty<CardId>();
    }

    private async Task<IReadOnlyList<CardId>> SacrificeAsync(PlayerId who, int count, ObjectFilter filter, Card source)
    {
        var chosen = await ChooseSacrificesAsync(who, count, filter, source);
        BeginSimultaneous();
        foreach (var id in chosen) SacrificePermanent(id);
        EndSimultaneous();
        return chosen;
    }

    /// <summary>The permanents a player chooses to sacrifice (all of them when there's no real choice).</summary>
    private async Task<IReadOnlyList<CardId>> ChooseSacrificesAsync(PlayerId who, int count, ObjectFilter filter, Card source)
    {
        var any = filter with { Controller = ControllerFilter.Any };
        var candidates = State.PermanentsControlledBy(who).Where(c => Matches(any, c, who, source, who)).ToList();
        count = Math.Min(count, candidates.Count);
        if (count <= 0) return Array.Empty<CardId>();
        IReadOnlyList<CardId> chosen;
        if (count == candidates.Count) chosen = candidates.Select(c => c.Id).ToList();
        else
        {
            var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList();
            chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who),
                new CardChoiceRequest($"Sacrifice {count}", source.Id, options, count, count, CardChoicePurpose.Sacrifice));
            Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(id => candidates.Any(c => c.Id == id)),
                $"Sacrifice exactly {count} of the listed permanents.");
        }
        return chosen;
    }

    private void SacrificePermanent(CardId id)
    {
        var card = State.GetCard(id);
        bool creature = card.IsCreature;
        MoveCard(id, Zone.Graveyard);
        Emit(new PermanentSacrificed(id));
        if (creature) Emit(new CreatureDied(id));
    }

    /// <summary>Replaces "the granter" in a granted ability's effects with the player who granted it.</summary>
    private static AbilityDefinition BindGranter(AbilityDefinition ability, PlayerId granter)
    {
        Subject Bind(Subject s) => s.Kind == SubjectKind.Granter ? new Subject(SubjectKind.FixedPlayer) { Player = granter } : s;
        Effect BindEffect(Effect e) => e switch
        {
            CreateTokens t => t with { Controller = Bind(t.Controller) },
            DrawCards d => d with { Who = Bind(d.Who) },
            GainLife g => g with { Who = Bind(g.Who) },
            LoseLife l => l with { Who = Bind(l.Who) },
            _ => e,
        };
        return ability with { Effects = ability.Effects.Select(BindEffect).ToList() };
    }

    private static string DescribeCost(ExtraCost cost) =>
        cost.Discard > 0 ? $"Discard {cost.Discard} card{(cost.Discard > 1 ? "s" : "")}"
        : cost.Sacrifice is { } s ? $"Sacrifice {(s.Types == CardType.Creature ? "a creature" : "a permanent")}"
        : cost.PayLife > 0 ? $"Pay {cost.PayLife} life" : "Pay";

    /// <summary>Copies a spell on the stack (rule 707.10); the copy's controller may choose new targets.</summary>
    private async Task CopySpellAsync(SpellOnStack original, PlayerId controller)
    {
        var card = State.GetCard(original.Card);
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        // A copy copies the choices made when casting (rule 707.10): modes, X and whether it was kicked.
        var copy = new Card(id, card.Definition with { IsToken = true }, controller) { Zone = Zone.Stack, Controller = controller, Kicked = original.Kicked };
        State.Cards.Add(id, copy);
        var ability = original.Ability ?? CastingTargets(card.Definition);
        IReadOnlyList<ChosenTarget> targets = original.Targets;
        if (ability is { Targets.Count: > 0 }
            && await ControllerOf(controller).ChooseYesNoAsync(ViewFor(controller), new YesNoRequest($"Choose new targets for the copy of {card.Name}?", id)))
            targets = await ChooseTargetsAsync(controller, ability, id, card.Name, canCancel: true) ?? original.Targets;
        PushStack(new SpellOnStack(id, controller, targets) { Ability = original.Ability, X = original.X, Kicked = original.Kicked });
        Emit(new SpellCopied(id, original.Card, controller));
    }

    private bool CanBeCountered(Card spell) =>
        !spell.Definition.CantBeCountered
        && !((spell.Is(CardType.Instant) || spell.Is(CardType.Sorcery)) && Has(spell.Controller, Replacements.YourInstantsAndSorceriesCantBeCountered));

    private async Task LookAtTopTakeAsync(PlayerId who, LookAtTopTake look, Card source)
    {
        var player = State.GetPlayer(who);
        var top = player.Library.Take(look.Count).ToList();
        if (top.Count == 0) return;
        if (look.RevealAll) Emit(new CardsRevealed(who, top));
        var options = top.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
        var eligible = top.Where(id => look.Filter is null || Matches(look.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), who, source, who)).ToList();
        IReadOnlyList<CardId> chosen = Array.Empty<CardId>();
        if (eligible.Count > 0)
        {
            var purpose = look.TakeTo == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand;
            var request = new CardChoiceRequest($"Look at the top {top.Count}: choose up to {look.Take}", source.Id,
                options.Where(o => eligible.Contains(o.Id)).ToList(), 0, Math.Min(look.Take, eligible.Count), purpose);
            chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), request);
            Require(chosen.Count <= look.Take && chosen.Distinct().Count() == chosen.Count && chosen.All(eligible.Contains), "Choose among the matching cards.");
        }
        if (look.Reveal && !look.RevealAll && chosen.Count > 0) Emit(new CardsRevealed(who, chosen.ToList()));
        BeginEnteringTogether();
        foreach (var id in chosen)
        {
            if (look.TakeTo == Zone.Library) continue; // stays on top
            MoveCard(id, look.TakeTo, controller: who);
            if (look.Tapped && State.GetCard(id).Zone == Zone.Battlefield) State.GetCard(id).Tapped = true;
        }
        EndEnteringTogether();
        if (look.RestShuffled)
        {
            Shuffle(player);
            Emit(new LookedAtTop(who, top.Count, chosen.Count, Scry: false));
            return;
        }
        var rest = top.Where(id => !chosen.Contains(id)).ToList();
        if (!look.RestToGraveyard) Rng.Shuffle(rest); // "on the bottom of your library in a random order"
        foreach (var id in rest)
        {
            if (look.RestToGraveyard) MoveCard(id, Zone.Graveyard);
            else
            {
                player.Library.Remove(id);
                player.Library.Add(id);
            }
        }
        Emit(new LookedAtTop(who, top.Count, top.Count - chosen.Count, Scry: !look.RestToGraveyard));
    }

    /// <summary>Removes a spell from the stack to its owner's graveyard (exile if it was cast with flashback).</summary>
    private void CounterSpellOnStack(CardId spellCard)
    {
        var item = State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == spellCard);
        State.Stack.RemoveAll(s => s is SpellOnStack sp && sp.Card == spellCard);
        MoveCard(spellCard, item?.Flashback == true ? Zone.Exile : Zone.Graveyard);
        Emit(new SpellCountered(spellCard));
    }

    /// <summary>Life gain, unless something says players can't gain life.</summary>
    private void GainLifeFor(PlayerId player, int amount)
    {
        if (amount <= 0 || State.Battlefield.Select(State.GetCard).Any(c => c.Definition.PlayersCantGainLife)) return;
        amount += State.PermanentsControlledBy(player).Count(c => (c.Definition.Replaces & Replacements.ExtraLifeGain) != 0);
        ChangeLife(player, amount);
    }

    /// <summary>A card's colors: from its mana cost, or its definition (tokens).</summary>
    internal static IReadOnlyList<string> ColorsOf(Card card) => card.Colors;

    /// <summary>The player puts cards in order, one pick at a time (first picked comes first).</summary>
    private async Task<IReadOnlyList<CardId>> OrderAsync(PlayerId who, List<CardId> cards, string prompt, Card source)
    {
        if (cards.Count < 2) return cards;
        var order = new List<CardId>();
        var left = cards.ToList();
        while (left.Count > 1)
        {
            var options = left.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
            var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(prompt, source.Id, options, 1, 1, CardChoicePurpose.Order));
            var next = pick.Count == 1 && left.Contains(pick[0]) ? pick[0] : left[0];
            order.Add(next);
            left.Remove(next);
        }
        order.Add(left[0]);
        return order;
    }

    /// <summary>"An opponent" (the controller chooses which when there are several).</summary>
    private async Task<PlayerId?> ChooseOpponentAsync(PlayerId controller, Card source, string prompt)
    {
        var opponents = State.OpponentsOf(controller).ToList();
        if (opponents.Count <= 1) return opponents.FirstOrDefault();
        int i = await ControllerOf(controller).ChooseOptionAsync(ViewFor(controller),
            new OptionRequest(prompt, source.Id, opponents.Select(o => State.GetPlayer(o).Name).ToList(), OptionKind.Other));
        Require(i >= 0 && i < opponents.Count, "Choose one of the opponents.");
        return opponents[i];
    }

    /// <summary>Discards a card; a card that says so goes to the battlefield instead when an opponent's spell or ability caused the discard.</summary>
    private void DiscardCard(PlayerId who, CardId card, PlayerId? causedBy)
    {
        if (causedBy is { } cause && cause != who && State.GetCard(card).Definition.OntoBattlefieldIfOpponentMakesYouDiscard)
        {
            MoveCard(card, Zone.Battlefield);
            return;
        }
        MoveCard(card, Zone.Graveyard);
        Emit(new CardDiscarded(who, card));
    }

    private async Task<IReadOnlyList<CardId>> DiscardAsync(PlayerId who, int count, PlayerId? causedBy = null)
    {
        var player = State.GetPlayer(who);
        count = Math.Min(count, player.Hand.Count);
        if (count == 0) return Array.Empty<CardId>();
        var chosen = await ControllerOf(who).ChooseDiscardAsync(ViewFor(who), count);
        Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(player.Hand.Contains),
            $"Must discard exactly {count} distinct cards from hand.");
        foreach (var card in chosen) DiscardCard(who, card, causedBy);
        return chosen;
    }

    // ------------------------------------------------------------------ conditions and filters

    /// <summary>A condition checked while an effect resolves, where targets are known.</summary>
    private bool HoldsIn(Condition condition, EffectContext ctx) => condition switch
    {
        TargetMatches t => ctx.ChosenAt(t.Index)?.Card is { } id && State.GetCard(id) is var card
                           && Matches(t.Filter with { Controller = ControllerFilter.Any }, card, card.Controller, ctx.Source, ctx.Controller),
        Not { Inner: TargetMatches or YouSacrificedThisWay or CreatedThisWay or TargetLifeExactly or XAtLeast or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo or QuantityAtLeast or TargetControlledByYou } n => !HoldsIn(n.Inner, ctx),
        TargetLifeExactly t => ctx.ChosenAt(t.Index)?.Player is { } p && State.GetPlayer(p).Life == t.Life,
        XAtLeast x => ctx.X >= x.AtLeast,
        YouSacrificedThisWay => ctx.Results.YouSacrificed,
        CreatedThisWay => ctx.Results.Created.Count > 0,
        TriggeredWasAttacking => ctx.Trigger?.Subject is { } t && State.GetCard(t).WasAttacking,
        QuantityAtLeast qa => Eval(qa.Quantity, ctx) >= qa.AtLeast,
        TargetAttachedTo a => ctx.ChosenAt(a.Attached)?.Card is { } att && ctx.ChosenAt(a.To)?.Card is { } host && State.GetCard(att).AttachedTo == host,
        TriggeredHasCounters h => ctx.Trigger?.Subject is { } tc && State.GetCard(tc).CounterCount(CounterKind.PlusOnePlusOne) >= h.AtLeast,
        TargetControlledByYou t => ctx.ChosenAt(t.Index)?.Card is { } owned && ControllerOrLastKnown(State.GetCard(owned), ctx.ChosenVersionAt(t.Index)) == ctx.Controller,
        _ => Holds(condition, ctx.Controller, ctx.Source),
    };

    private bool Holds(Condition condition, PlayerId controller, Card? source)
    {
        var player = State.GetPlayer(controller);
        return condition switch
        {
            AttackedThisTurn => player.AttackedThisTurn,
            CreatureDiedThisTurn => State.CreaturesDiedThisTurn > 0,
            GainedLifeThisTurn g => player.LifeGainedThisTurn >= g.AtLeast,
            CardsInGraveyard g => player.Graveyard.Count(id => g.Filter is null
                || Matches(g.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), controller, source, controller)) >= g.AtLeast,
            AttackingCreatures a => (State.Combat?.Attacks.Count(x => State.GetCard(x.Attacker).Controller == controller) ?? 0) >= a.AtLeast,
            YouControl y => State.Battlefield.Select(State.GetCard).Count(c => Matches(y.Filter, c, c.Controller, source, controller)) >= y.AtLeast,
            LifeAtLeast l => player.Life >= l.Amount,
            Not n => !Holds(n.Inner, controller, source),
            WasKicked => source?.Kicked == true,
            OpponentLostLifeThisTurn => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).LifeLostThisTurn > 0),
            YourTurn => State.ActivePlayer == controller,
            SourceUntapped => source is { Zone: Zone.Battlefield, Tapped: false },
            HasEnduringStory => player.HasEnduringStory,
            SourceHasCounters c => source is not null && source.CounterCount(c.Kind) >= c.AtLeast,
            SourceAttacking => source is not null && State.Combat?.FindAttack(source.Id) is not null,
            LifeAboveStarting l => player.Life >= Config.StartingLife + l.AtLeast,
            All a => a.Conditions.All(c => Holds(c, controller, source)),
            TotalPowerAtLeast t => State.PermanentsControlledBy(controller).Where(c => c.IsCreature).Sum(c => c.Power) >= t.Amount,
            TargetMatches or TargetLifeExactly or XAtLeast or YouSacrificedThisWay or CreatedThisWay or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo or QuantityAtLeast or TargetControlledByYou => true, // checked where known (effects)
            WasCastFromGraveyard => source?.CastFromGraveyard == true,
            CardsDrawnThisTurn d => player.CardsDrawnThisTurn >= d.AtLeast,
            AttackingPowerAtLeast ap => State.Combat?.Attacks.Select(a => State.GetCard(a.Attacker)).Where(c => c.Controller == controller).Sum(c => c.Power) >= ap.Amount,
            GiftPromised => source?.GiftPromised == true,
            SourceIs si => source is not null && Matches(si.Filter with { Controller = ControllerFilter.Any }, source, source.Controller, source, controller),
            SourceWasSubtype w => source?.LastKnownInfo?.Subtypes.Contains(w.Subtype, StringComparer.OrdinalIgnoreCase) == true || source?.HasSubtype(w.Subtype) == true && source.Zone == Zone.Battlefield,
            WasCastFromHand => source?.CastFromHand == true,
            WasCast => source?.WasCast == true,
            SourceHadCounters h => (source?.LastKnownInfo?.Counters.GetValueOrDefault(h.Kind) ?? 0) > 0,
            DifferentNames d => State.PermanentsControlledBy(controller).Where(c => Matches(d.Filter, c, c.Controller, source, controller)).Select(c => c.Name).Distinct().Count() >= d.AtLeast,
            ResolvedThisTurn r => source is not null && (r.Exactly ? source.ResolvedThisTurn.Values.DefaultIfEmpty(0).Max() == r.Times : source.ResolvedThisTurn.Values.DefaultIfEmpty(0).Max() >= r.Times),
            _ => throw new NotSupportedException($"Condition {condition.GetType().Name} is not implemented."),
        };
    }

    /// <summary>Whether a permanent with the given subtype (an Aura, an Equipment) is attached to <paramref name="obj"/>.</summary>
    private bool HasAttached(Card obj, string subtype) =>
        State.Battlefield.Select(State.GetCard).Any(c => c.AttachedTo == obj.Id && c.HasSubtype(subtype));

    /// <summary>Mana value; a spell on the stack counts the X chosen for it (rule 202.3e).</summary>
    private int ManaValueOf(Card card)
    {
        int value = card.Definition.ManaCost.ManaValue;
        if (card.Zone == Zone.Stack && card.Definition.ManaCost.XCount > 0 && State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == card.Id) is { } spell)
            value += spell.X * card.Definition.ManaCost.XCount;
        return value;
    }

    /// <summary>
    /// Whether <paramref name="obj"/> (controlled by <paramref name="objController"/>) fits the filter, seen from the ability's side.
    /// With <paramref name="lastKnown"/>, a permanent that left the battlefield is judged as it last existed there (rule 603.10a).
    /// </summary>
    private bool Matches(ObjectFilter filter, Card obj, PlayerId objController, Card? source, PlayerId sourceController, bool lastKnown = false)
    {
        var lk = lastKnown && obj.Zone != Zone.Battlefield ? obj.LastKnownInfo : null;
        var types = lk?.Types ?? obj.Types;
        bool HasSubtype(string s) => lk?.HasSubtype(s) ?? obj.HasSubtype(s);
        bool HasKeyword(Keyword k) => lk?.Keywords.Contains(k) ?? obj.Has(k);
        int power = lk?.Power ?? obj.Power, toughness = lk?.Toughness ?? obj.Toughness;
        var colors = lk?.Colors ?? ColorsOf(obj);
        var supertypes = lk?.Supertypes ?? obj.Definition.Supertypes;
        var attachedTo = lk is null ? obj.AttachedTo : lk.AttachedTo;
        if (filter.Types != 0 && (types & filter.Types) == 0) return false;
        if ((types & filter.ExcludedTypes) != 0) return false;
        if (filter.Subtype is { } subtype && !HasSubtype(subtype)) return false;
        if (filter.Controller == ControllerFilter.You && objController != sourceController) return false;
        if (filter.Controller == ControllerFilter.Opponent && objController == sourceController) return false;
        if (filter.Other && source is not null && obj.Id == source.Id) return false;
        if (filter.MinPower is { } min && power < min) return false;
        if (filter.MaxPower is { } maxPower && power > maxPower) return false;
        if (filter.MinToughness is { } minToughness && toughness < minToughness) return false;
        if (filter.MinManaValue is { } minMv && ManaValueOf(obj) < minMv) return false;
        if (filter.MaxManaValue is { } maxMv && ManaValueOf(obj) > maxMv) return false;
        if (filter.Token is { } token && obj.Definition.IsToken != token) return false;
        if (filter.Colors is { Count: > 0 } wanted && !colors.Any(wanted.Contains)) return false;
        if (filter.Keyword is { } keyword && !HasKeyword(keyword)) return false;
        if (filter.WithoutKeyword is { } without && HasKeyword(without)) return false;
        if (filter.Tapped is { } tapped && (lk?.Tapped ?? obj.Tapped) != tapped) return false;
        if (filter.Supertype != 0 && (supertypes & filter.Supertype) == 0) return false;
        if (filter.ExcludedSupertype != 0 && (supertypes & filter.ExcludedSupertype) != 0) return false;
        if (filter.MaxManaValueLandCount && obj.Definition.ManaCost.ManaValue > State.PermanentsControlledBy(sourceController).Count(c => c.Is(CardType.Land))) return false;
        if (filter.ExcludedSubtype is { } excluded && HasSubtype(excluded)) return false;
        if (filter.Name is { } name && (lk?.Name ?? obj.Name) != name) return false;
        if (filter.OwnedByYou && obj.Owner != sourceController) return false;
        if (filter.PutIntoZoneThisTurn && obj.ZoneChangedTurn != State.TurnNumber) return false;
        if (filter.AttachedToSource && (source is null || source.AttachedTo != obj.Id)) return false;
        if (filter.DamagedBySource && (source is null || !obj.DamagedThisTurnBy.Contains(source.Id))) return false;
        if (filter.Attached is { } attached && (attachedTo is not null) != attached) return false;
        if (filter.MaxManaValueSourcePower && source is not null
            && obj.Definition.ManaCost.ManaValue > (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.Power : source.LastKnownInfo.Power)) return false;
        if (filter.ChosenColor && (source?.ChosenColor is not { } color || !colors.Contains(color))) return false;
        if (filter.ChosenType && (source?.ChosenType is not { } type || !HasSubtype(type))) return false;
        if (filter.AnyOf is { Count: > 0 } anyOf && !anyOf.Any(f => Matches(f with { Controller = ControllerFilter.Any }, obj, objController, source, sourceController, lastKnown))) return false;
        if (filter.HasCounters is { } hasCounters && ((lk is null ? obj.CounterCount(CounterKind.PlusOnePlusOne) : lk.Counters.GetValueOrDefault(CounterKind.PlusOnePlusOne)) > 0) != hasCounters) return false;
        if (filter.Enchanted is { } enchanted && HasAttached(obj, "Aura") != enchanted) return false;
        if (filter.Equipped is { } equipped && HasAttached(obj, "Equipment") != equipped) return false;
        if (filter.Commander is { } commander && obj.IsCommander != commander) return false;
        if (filter.InHand is { } inHand && (obj.Zone == Zone.Hand) != inHand) return false;
        if (filter.FromBattlefieldThisTurn && obj.LeftBattlefieldTurn != State.TurnNumber) return false;
        if (filter.PaidWithTreasure && !obj.PaidWithTreasure) return false;
        if (filter.ChosenParity && (source?.ChosenParity is not { } parity || (ManaValueOf(obj) % 2 == 0 ? "even" : "odd") != parity)) return false;
        if (filter.SharesNameWithYourLegendary
            && !State.PermanentsControlledBy(sourceController).Any(c => (c.Definition.Supertypes & Supertype.Legendary) != 0 && c.Name == obj.Name)) return false;
        if (filter.Multicolored is { } multi && (colors.Count > 1) != multi) return false;
        if (filter.Colorless is { } colorless && (colors.Count == 0) != colorless) return false;
        if (filter.InCombat is not null || filter.Attacking is not null || filter.Blocking is not null)
        {
            bool attacking = lk?.Attacking ?? State.Combat?.FindAttack(obj.Id) is not null;
            bool blocking = lk?.Blocking ?? State.Combat?.IsBlocking(obj.Id) == true;
            if (filter.InCombat is { } wantInCombat && (attacking || blocking) != wantInCombat) return false;
            if (filter.Attacking is { } wantAttacking && attacking != wantAttacking) return false;
            if (filter.Blocking is { } wantBlocking && blocking != wantBlocking) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ continuous effects (rule 611, 613)

    /// <summary>
    /// Recomputes characteristics changed by continuous effects: static abilities of permanents on the battlefield,
    /// then "until end of turn" effects. Only additive P/T changes (layer 7c) and keyword grants (layer 6) exist so
    /// far, so the order within a layer doesn't change the result.
    /// </summary>
    private void RecomputeContinuousEffects()
    {
        var battlefield = State.Battlefield.Select(State.GetCard).ToList();
        var sources = battlefield.Concat(State.Emblems.Select(State.GetCard)).ToList();
        // Timestamps (rule 613.7d–e): permanents that just entered (or became attached), then emblems, in order.
        foreach (var card in sources.Where(c => c.Timestamp == 0)) card.Timestamp = NewTimestamp();
        State.UntilEndOfTurn.RemoveAll(e => State.GetCard(e.Card).Version != e.Version);
        bool SourceGone(CardId id, int version) => State.GetCard(id) is var s && (s.Version != version || s.Zone != Zone.Battlefield);
        State.LastingEffects.RemoveAll(e => State.GetCard(e.Card) is var c && (c.Version != e.Version || c.Zone != Zone.Battlefield)
                                            || (e.WhileSource is { } ws && SourceGone(ws.Card, ws.Version)));
        State.DamagePreventions.RemoveAll(d => SourceGone(d.Card, d.Version) || SourceGone(d.Source, d.SourceVersion));
        // Effects from resolved spells and abilities, oldest first.
        var effects = State.LastingEffects.Concat(State.UntilEndOfTurn)
            .Where(e => State.GetCard(e.Card).Zone == Zone.Battlefield).OrderBy(e => e.Timestamp).ToList();

        foreach (var card in battlefield)
        {
            card.PowerBonus = 0;
            card.ToughnessBonus = 0;
            card.Controller = card.BaseController;
            card.GrantedSubtypes.Clear();
            card.TypesOverride = null;
            card.GrantedTypes = 0;
            card.SubtypesOverride = null;
            card.ColorsOverride = null;
            card.NameOverride = null;
        }

        IEnumerable<Card> AffectedBy(Card source, StaticAbility ability) =>
            Affected(source, ability.Affects, battlefield).Where(a =>
                ability.Filter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, a, a.Controller, source, source.Controller));

        // Static abilities that exist and apply now, oldest source first (an ability that was lost generates no effect).
        List<(Card Source, StaticAbility Ability)> Statics(Func<StaticAbility, bool> relevant) =>
            sources.OrderBy(c => c.Timestamp)
                .SelectMany(source => source.Abilities.OfType<StaticAbility>().Where(relevant)
                    .Where(a => a.While is not { } condition || Holds(condition, source.Controller, source))
                    .Select(a => (source, a)))
                .ToList();

        // Which abilities exist decides which static abilities apply at all, so the ability layer is worked out first
        // with printed characteristics, then again once types and colors are settled (rule 613.8 dependency).
        ApplyAbilityLayer(battlefield, effects, Statics, AffectedBy);

        // Layer 2: control-changing static abilities ("You control enchanted permanent").
        foreach (var (source, ability) in Statics(a => a.GivesControl))
            foreach (var affected in AffectedBy(source, ability).ToList())
                if (affected.Controller != source.Controller)
                {
                    affected.Controller = source.Controller;
                    affected.ControlledSinceTurnStart = false;
                }

        // Layer 1 (names set by copy-like effects), layer 4 (types and subtypes) and layer 5 (colors), in timestamp order.
        var typeChanges = new List<(long Timestamp, Card Card, Action<Card, TypeState> Apply)>();
        foreach (var (source, ability) in Statics(a => a.SetName is not null || a.SetTypes is not null || a.SetSubtypes is not null || a.SetColors is not null
                                                        || a.AddTypes != 0 || a.AddSubtypes is not null || a.AddChosenType))
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                var chosen = ability.AddChosenType ? source.ChosenType : null;
                typeChanges.Add((source.Timestamp, affected, (card, t) =>
                {
                    if (ability.SetName is { } name) t.Name = name;
                    if (ability.SetTypes is { } types) t.Types = types;
                    if (ability.SetSubtypes is { } subtypes) t.Subtypes = subtypes.ToList();
                    if (ability.SetColors is { } colors) t.Colors = colors;
                    t.Types |= ability.AddTypes;
                    if (ability.AddSubtypes is { } add) t.Subtypes.AddRange(add);
                    if (chosen is not null) t.Subtypes.Add(chosen);
                }));
            }
        foreach (var effect in effects.Where(e => e.AddTypes != 0 || e.AddSubtypes is not null || e.SetSubtypes is not null || e.SetTypes is not null))
            typeChanges.Add((effect.Timestamp, State.GetCard(effect.Card), (card, t) =>
            {
                if (effect.SetTypes is { } types) t.Types = types;
                if (effect.SetSubtypes is { } subtypes) t.Subtypes = subtypes.ToList();
                t.Types |= effect.AddTypes;
                if (effect.AddSubtypes is { } add) t.Subtypes.AddRange(add);
            }));
        foreach (var group in typeChanges.GroupBy(x => x.Card))
        {
            var card = group.Key;
            var t = new TypeState(card.Definition.Types, card.Definition.Subtypes.ToList(), card.Definition.ColorList, null);
            foreach (var change in group.OrderBy(x => x.Timestamp)) change.Apply(card, t);
            card.TypesOverride = t.Types;
            card.SubtypesOverride = t.Subtypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!ReferenceEquals(t.Colors, card.Definition.ColorList)) card.ColorsOverride = t.Colors;
            card.NameOverride = t.Name;
        }

        // Layer 6 again with the final types (filters such as "artifact creatures" depend on them).
        ApplyAbilityLayer(battlefield, effects, Statics, AffectedBy);

        // "For each color among permanents you control" mana abilities follow the colors settled above.
        foreach (var card in battlefield.Where(c => c.Definition.ExtraManaOptions.Any(o => o.ColorsAmongYourPermanents)))
            card.ColorsAmongYourPermanents = battlefield.Where(p => p.Controller == card.Controller).SelectMany(p => p.Colors).Distinct()
                .Select(c => Mana.ManaTypeExtensions.TryParse(c[0], out var t) ? t : (ManaType?)null).OfType<ManaType>().OrderBy(t => t).ToList();

        // Layer 7a: characteristic-defining abilities; 7b: effects that set base power/toughness, in timestamp order.
        foreach (var card in battlefield)
        {
            var cda = new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            card.BasePowerOverride = card.Definition.PowerFrom is { } pf ? Eval(pf, cda) : null;
            card.BaseToughnessOverride = card.Definition.ToughnessFrom is { } tf ? Eval(tf, cda) : null;
        }
        var setters = new List<(long Timestamp, Card Card, int? Power, int? Toughness)>();
        foreach (var (source, ability) in Statics(a => a.SetPower is not null || a.SetToughness is not null))
            foreach (var affected in AffectedBy(source, ability).ToList())
                setters.Add((source.Timestamp, affected, ability.SetPower, ability.SetToughness));
        foreach (var effect in effects.Where(e => e.SetPower is not null || e.SetToughness is not null || e.SetPowerFrom is not null || e.SetToughnessFrom is not null))
        {
            var card = State.GetCard(effect.Card);
            var own = new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            setters.Add((effect.Timestamp, card, effect.SetPower ?? (effect.SetPowerFrom is { } pf ? Eval(pf, own) : null),
                effect.SetToughness ?? (effect.SetToughnessFrom is { } tf ? Eval(tf, own) : null)));
        }
        foreach (var (_, card, power, toughness) in setters.OrderBy(x => x.Timestamp))
        {
            if (power is { } p) card.BasePowerOverride = p;
            if (toughness is { } t) card.BaseToughnessOverride = t;
        }

        // Layer 7c: modifications (their order doesn't matter); counters (7d) are counted in Card.Power.
        foreach (var (source, ability) in Statics(a => a.Power != 0 || a.Toughness != 0 || a.PowerBonus is not null || a.ToughnessBonus is not null))
        {
            var ctx = new EffectContext(source.Controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            int power = ability.Power + (ability.PowerBonus is { } pb ? Eval(pb, ctx) : 0);
            int toughness = ability.Toughness + (ability.ToughnessBonus is { } tb ? Eval(tb, ctx) : 0);
            if (power == 0 && toughness == 0) continue;
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                affected.PowerBonus += power;
                affected.ToughnessBonus += toughness;
            }
        }
        foreach (var effect in effects)
        {
            var card = State.GetCard(effect.Card);
            card.PowerBonus += effect.Power;
            card.ToughnessBonus += effect.Toughness;
        }
        // Each hone counter on an Equipment grants +1/+0 to the creature it's attached to.
        foreach (var equipment in battlefield.Where(c => c.CounterCount(CounterKind.Hone) > 0 && c.AttachedTo is not null))
            if (State.GetCard(equipment.AttachedTo!.Value) is { Zone: Zone.Battlefield } equipped) equipped.PowerBonus += equipment.CounterCount(CounterKind.Hone);
        // Mana amounts worked out from characteristics ("X mana, where X is this creature's power") use the final values.
        foreach (var card in battlefield)
            card.ManaAmount = card.Definition.ManaAmountFrom is { } ma
                ? Eval(ma, new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))
                : card.Definition.ManaAmount;

        // Storied: a player controlling a permanent with it and three or more artifacts, legendaries and/or Sagas has an
        // enduring story for the rest of the game; abilities that depend on it apply at once.
        bool gained = false;
        foreach (var player in State.LivingPlayers.Where(p => !p.HasEnduringStory))
        {
            var mine = battlefield.Where(c => c.Controller == player.Id).ToList();
            if (!mine.Any(c => c.Has(Keyword.Storied))) continue;
            if (mine.Count(c => c.Is(CardType.Artifact) || (c.Definition.Supertypes & Supertype.Legendary) != 0 || c.HasSubtype("Saga")) < 3) continue;
            player.HasEnduringStory = gained = true;
            Emit(new EnduringStoryGained(player.Id));
        }
        if (gained) RecomputeContinuousEffects();
    }

    /// <summary>Characteristics worked out in layers 1, 4 and 5.</summary>
    private sealed class TypeState(CardType types, List<string> subtypes, IReadOnlyList<string> colors, string? name)
    {
        public CardType Types { get; set; } = types;
        public List<string> Subtypes { get; set; } = subtypes;
        public IReadOnlyList<string> Colors { get; set; } = colors;
        public string? Name { get; set; } = name;
    }

    /// <summary>
    /// Layer 6 in timestamp order (rule 613.7): granting abilities adds to what the object has; losing all abilities
    /// removes its printed abilities and everything granted by earlier effects, but not by later ones.
    /// </summary>
    private void ApplyAbilityLayer(List<Card> battlefield, List<UntilEndOfTurnEffect> effects,
        Func<Func<StaticAbility, bool>, List<(Card Source, StaticAbility Ability)>> statics, Func<Card, StaticAbility, IEnumerable<Card>> affectedBy)
    {
        // Abilities existing before this pass decide which statics apply; repeat until that is stable.
        for (int pass = 0; pass < 4; pass++)
        {
            var before = battlefield.ToDictionary(c => c.Id, c => (c.LosesAbilities, c.GrantedAbilities.Count, c.GrantedKeywords.Count));
            var changes = new List<(long Timestamp, Card Card, Action<Card> Apply)>();
            foreach (var (source, ability) in statics(a => a.LosesAllAbilities || a.Keywords is { Count: > 0 } || a.GrantedKeywords.Count > 0
                                                           || a.GrantsAbilities is not null || a.GrantsMana is not null || a.GrantsWard is not null
                                                           || a.GrantsGraveyardAbilities is not null))
                foreach (var affected in affectedBy(source, ability).ToList())
                    changes.Add((source.Timestamp, affected, card =>
                    {
                        if (ability.LosesAllAbilities) LoseAllAbilities(card);
                        card.GrantedKeywords.UnionWith(ability.GrantedKeywords);
                        if (ability.GrantsAbilities is { } granted) card.GrantedAbilities.AddRange(granted.Select(g => g with { GrantedBy = source.Id }));
                        if (ability.GrantsMana is { } mana) card.GrantedManaOptions.Add(new ManaOption(mana, ability.GrantsManaAmount));
                        if (ability.GrantsWard is { } ward) card.GrantedWards.Add(ward);
                        // "Has all activated abilities of all [filter] cards in your graveyard."
                        if (ability.GrantsGraveyardAbilities is { } fromGraveyard)
                            card.GrantedAbilities.AddRange(State.GetPlayer(source.Controller).Graveyard.Select(State.GetCard)
                                .Where(c => Matches(fromGraveyard with { Controller = ControllerFilter.Any }, c, c.Owner, source, source.Controller))
                                .SelectMany(c => c.Definition.Abilities.OfType<ActivatedAbility>().Where(a => !a.Cost.FromGraveyard && !a.Cost.FromHand))
                                .Select(a => a with { GrantedBy = source.Id }));
                    }));
            foreach (var effect in effects.Where(e => e.LosesAbilities || e.Keywords.Count > 0 || e.Abilities is not null))
                changes.Add((effect.Timestamp, State.GetCard(effect.Card), card =>
                {
                    if (effect.LosesAbilities) LoseAllAbilities(card);
                    card.GrantedKeywords.UnionWith(effect.Keywords);
                    if (effect.Abilities is { } abilities) card.GrantedAbilities.AddRange(abilities);
                }));

            foreach (var card in battlefield)
            {
                card.LosesAbilities = false;
                card.GrantedKeywords.Clear();
                card.GrantedAbilities.Clear();
                card.GrantedManaOptions.Clear();
                card.GrantedWards.Clear();
            }
            // Keyword counters give their keyword (rule 122.1b).
            foreach (var card in battlefield.Where(c => c.CounterCount(CounterKind.Trample) > 0)) card.GrantedKeywords.Add(Keyword.Trample);
            foreach (var change in changes.OrderBy(x => x.Timestamp)) change.Apply(change.Card);
            if (battlefield.All(c => before[c.Id] == (c.LosesAbilities, c.GrantedAbilities.Count, c.GrantedKeywords.Count))) break;
        }
    }

    private static void LoseAllAbilities(Card card)
    {
        card.LosesAbilities = true;
        card.GrantedWards.Clear();
        card.GrantedKeywords.Clear();
        card.GrantedAbilities.Clear();
        card.GrantedManaOptions.Clear();
    }

    private IEnumerable<Card> Affected(Card source, AffectedFilter filter, IReadOnlyList<Card> battlefield)
    {
        IEnumerable<Card> candidates = filter.Scope switch
        {
            AffectedScope.Self => new[] { source },
            AffectedScope.YourCreatures => battlefield.Where(c => c.IsCreature && c.Controller == source.Controller),
            AffectedScope.OpponentsCreatures => battlefield.Where(c => c.IsCreature && c.Controller != source.Controller),
            AffectedScope.AllCreatures => battlefield.Where(c => c.IsCreature),
            AffectedScope.Enchanted or AffectedScope.Equipped when source.AttachedTo is { } host
                => battlefield.Where(c => c.Id == host),
            AffectedScope.YourPermanents => battlefield.Where(c => c.Controller == source.Controller),
            _ => Array.Empty<Card>(),
        };
        if (filter.Other) candidates = candidates.Where(c => c.Id != source.Id);
        if (filter.Subtype is { } subtype) candidates = candidates.Where(c => c.HasSubtype(subtype));
        return candidates;
    }

    // ------------------------------------------------------------------ triggers (rule 603)

    /// <summary>Notes triggered abilities that trigger on <paramref name="e"/>; they go on the stack before the next priority.</summary>
    private void CollectTriggers(GameEvent e)
    {
        TriggerInfo About(Card card, PlayerId? player = null, int amount = 0) => new(card.Id, card.Version, player, amount);
        // "Whenever a creature card leaves your graveyard" (wherever it goes).
        if (e is CardMoved { From: Zone.Graveyard } left) QueueObservers(TriggerEvent.LeavesGraveyard, State.GetCard(left.Card), left.Owner);
        switch (e)
        {
            case CardMoved { To: Zone.Battlefield } m when _enteringTogether is not null:
                _enteringTogether.Add(m.Card);
                break;
            case CardMoved { To: Zone.Battlefield } m:
                QueueEnterTriggers(m.Card);
                break;
            case CardMoved { From: Zone.Battlefield, To: Zone.Graveyard } m:
                Queue(m.Card, TriggerEvent.PutIntoGraveyard, m.LastController);
                if (!WasCreature(State.GetCard(m.Card))) break;
                Queue(m.Card, TriggerEvent.Dies, m.LastController);
                QueueObservers(TriggerEvent.CreatureDies, State.GetCard(m.Card), m.LastController);
                break;
            case AttackerDeclared a:
            {
                var attacker = State.GetCard(a.Attacker);
                Queue(a.Attacker, TriggerEvent.Attacks, attacker.Controller, About(attacker, a.Defender));
                Queue(a.Attacker, TriggerEvent.AttacksOrBlocks, attacker.Controller);
                QueueObservers(TriggerEvent.CreatureAttacks, attacker, attacker.Controller);
                break;
            }
            case AttacksDeclared a:
                foreach (var card in State.PermanentsControlledBy(a.Player).Concat(State.Emblems.Select(State.GetCard).Where(e => e.Owner == a.Player)).ToList())
                    Queue(card.Id, TriggerEvent.YouAttack, a.Player, new TriggerInfo(Amount: a.Count));
                break;
            case BlockerDeclared b:
                Queue(b.Blocker, TriggerEvent.Blocks, State.GetCard(b.Blocker).Controller, About(State.GetCard(b.Attacker)));
                Queue(b.Blocker, TriggerEvent.AttacksOrBlocks, State.GetCard(b.Blocker).Controller);
                break;
            case LifeChanged l when l.NewLife > l.OldLife:
                foreach (var card in State.PermanentsControlledBy(l.Player).ToList())
                    Queue(card.Id, TriggerEvent.YouGainLife, l.Player, new TriggerInfo(Player: l.Player, Amount: l.NewLife - l.OldLife));
                break;
            case LifeChanged l when l.NewLife < l.OldLife:
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                {
                    if (card.Controller != l.Player) Queue(card.Id, TriggerEvent.OpponentLosesLife, card.Controller, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
                    Queue(card.Id, TriggerEvent.PlayerLosesLife, card.Controller, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
                }
                break;
            case CardDrawn d:
                foreach (var card in State.PermanentsControlledBy(d.Player).ToList())
                    Queue(card.Id, TriggerEvent.YouDrawCard, d.Player, new TriggerInfo(Player: d.Player, Amount: 1));
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != d.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentDrawsCard, card.Controller, new TriggerInfo(Player: d.Player, Amount: 1));
                break;
            case StepBegan { Step: Step.Draw } s:
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                    Queue(card.Id, TriggerEvent.EachDrawStep, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                break;
            case CountersPlaced { Kind: CounterKind.Lore } lore when State.GetCard(lore.Card).Zone == Zone.Battlefield:
            {
                // Chapter abilities trigger as lore counters reach their chapter numbers (rule 714.2b).
                var saga = State.GetCard(lore.Card);
                int now = saga.CounterCount(CounterKind.Lore), before = now - lore.Count;
                foreach (var ability in TriggerAbilitiesOf(saga).Where(a => a.Trigger == TriggerEvent.Chapter))
                    foreach (var chapter in ability.Chapters.Where(c => c > before && c <= now))
                        AddPending(saga.Id, ability, saga.Controller, new TriggerInfo(Amount: chapter));
                break;
            }
            case CountersPlaced cp when State.GetCard(cp.Card).Zone == Zone.Battlefield:
            {
                var target = State.GetCard(cp.Card);
                foreach (var observer in State.Battlefield.Select(State.GetCard).ToList())
                    foreach (var ability in observer.Abilities.OfType<TriggeredAbility>())
                    {
                        if (ability.Trigger != TriggerEvent.CountersPlaced || (ability.CounterKind != cp.Kind && !ability.AnyCounterKind)) continue;
                        if (ability.PlacedByYou && cp.By != observer.Controller) continue;
                        bool hit = ability.OnSelf ? observer.Id == target.Id
                            : Matches(ability.Filter ?? ObjectFilter.YourCreatures, target, target.Controller, observer, observer.Controller);
                        if (hit) AddPending(observer.Id, ability, observer.Controller, About(target, amount: cp.Count));
                    }
                break;
            }
            case PermanentSacrificed ps:
            {
                var gone = State.GetCard(ps.Card);
                var sacrificer = gone.LastKnownInfo?.Controller ?? gone.Owner;
                Queue(ps.Card, TriggerEvent.SelfSacrificed, sacrificer);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == sacrificer))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouSacrifice))
                        if (Matches(ability.Filter ?? ObjectFilter.Anything, gone, sacrificer, observer, observer.Controller, lastKnown: true))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(gone.Id, gone.Version, sacrificer));
                break;
            }
            case AbilityActivated aa:
            {
                var activated = State.GetCard(aa.Source);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == aa.Player))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouActivateAbility))
                        if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, activated, activated.Controller, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(activated.Id, activated.Version, aa.Player));
                break;
            }
            case CardDiscarded cd:
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != cd.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentDiscards, card.Controller, new TriggerInfo(cd.Card, State.GetCard(cd.Card).Version, cd.Player));
                break;
            case PermanentUntapped u when State.GetCard(u.Card).Zone == Zone.Battlefield:
            {
                var untapped = State.GetCard(u.Card);
                foreach (var ability in TriggerAbilitiesOf(untapped).Where(a => a.Trigger == TriggerEvent.BecomesUntapped && a.Filter is null))
                    AddPending(u.Card, ability, untapped.Controller);
                // "Whenever equipped creature becomes untapped": watchers with a filter.
                foreach (var (observer, abilities) in Observers())
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.BecomesUntapped && a.Filter is not null))
                        if (Matches(ability.Filter!, untapped, untapped.Controller, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(untapped.Id, untapped.Version, untapped.Controller));
                break;
            }
            case PermanentTapped t when State.GetCard(t.Card).Zone == Zone.Battlefield:
                Queue(t.Card, TriggerEvent.BecomesTapped, State.GetCard(t.Card).Controller);
                break;
            case SpellCast c:
            {
                var spell = State.GetCard(c.Card);
                // "When you next cast an instant or sorcery spell this turn, copy that spell."
                if ((spell.Is(CardType.Instant) || spell.Is(CardType.Sorcery))
                    && State.CopyNextInstantOrSorcery.RemoveAll(x => x.Player == c.Player && x.Turn == State.TurnNumber) > 0)
                    _pendingTriggers.Add(new PendingTrigger(c.Card, CopyThatSpell, c.Player, new TriggerInfo(spell.Id, spell.Version, c.Player)));
                foreach (var observer in State.Battlefield.Concat(State.Emblems).Select(State.GetCard).ToList())
                {
                    bool mine = observer.Controller == c.Player;
                    if (mine && !spell.IsCreature && observer.Has(Keyword.Prowess)) _pendingTriggers.Add(new PendingTrigger(observer.Id, ProwessTrigger, c.Player));
                    foreach (var ability in observer.Abilities.OfType<TriggeredAbility>())
                    {
                        var filter = (ability.Filter ?? new ObjectFilter()) with { Controller = ControllerFilter.Any };
                        if (ability.TargetsSource && !(State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == c.Card)?.Targets.Any(t => t.Target.Card == observer.Id) ?? false))
                            continue;
                        if ((ability.Trigger == TriggerEvent.YouCastSpell && mine || ability.Trigger == TriggerEvent.OpponentCastsSpell && !mine
                             || ability.Trigger == TriggerEvent.AnyPlayerCastsSpell)
                            && Matches(filter, spell, c.Player, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, About(spell, c.Player, ManaValueOf(spell)));
                    }
                }
                break;
            }
            case StepBegan { Step: Step.BeginCombat } s:
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourBeginCombat, s.ActivePlayer);
                foreach (var card in State.GetPlayer(s.ActivePlayer).Graveyard.Select(State.GetCard).ToList())
                    foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard && a.Trigger == TriggerEvent.YourBeginCombat))
                        AddPending(card.Id, ability, s.ActivePlayer);
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.EachBeginCombat, card.Controller);
                break;
            case DamageDealt { IsCombat: false, TargetPlayer: { } burned } nd when State.GetCard(nd.Source).Controller != burned:
            {
                var source = State.GetCard(nd.Source);
                foreach (var card in State.PermanentsControlledBy(source.Controller).ToList())
                    Queue(card.Id, TriggerEvent.YourSourceDealsNoncombatDamageToOpponent, source.Controller, new TriggerInfo(source.Id, source.Version, burned, nd.Amount));
                break;
            }
            case DamageDealt { IsCombat: true, TargetCard: not null } cd2:
                NoteCombatDamage(State.GetCard(cd2.Source), null, cd2.Amount);
                break;
            case DamageDealt { IsCombat: true, TargetPlayer: { } hurt } d:
            {
                NoteCombatDamage(State.GetCard(d.Source), hurt, d.Amount);
                var source = State.GetCard(d.Source);
                Queue(d.Source, TriggerEvent.DealsCombatDamageToPlayer, source.Controller, About(source, hurt, d.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamageToPlayer, source, source.Controller, About(source, hurt, d.Amount));
                break;
            }
            case StepBegan { Step: Step.PrecombatMain } s:
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourPrecombatMain, s.ActivePlayer);
                break;
            case StepBegan { Step: Step.Upkeep } s:
                // Delayed abilities waiting for "the next upkeep" (rule 603.7).
                foreach (var delayed in State.AtNextUpkeep.ToList())
                {
                    State.AtNextUpkeep.Remove(delayed);
                    _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller, new TriggerInfo(Amount: delayed.Amount)));
                }
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourUpkeep, s.ActivePlayer);
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                    Queue(card.Id, TriggerEvent.EachUpkeep, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                break;
            case StepBegan { Step: Step.End } s:
                foreach (var delayed in State.AtNextEndStep.ToList())
                {
                    State.AtNextEndStep.Remove(delayed);
                    var delayedAbility = !delayed.Return ? SacrificeAtEndStep
                        : delayed.Counters is { } c ? ReturnAtEndStep with { Effects = new Effect[] { new PutOntoBattlefield(Subject.Triggered) { Counters = c.Count, CounterKind = c.Kind } } }
                        : ReturnAtEndStep;
                    _pendingTriggers.Add(new PendingTrigger(delayed.Card, delayedAbility, delayed.Controller, new TriggerInfo(delayed.Card, delayed.Version)));
                }
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourEndStep, s.ActivePlayer);
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.EachEndStep, card.Controller);
                break;
        }
    }

    /// <summary>Triggered abilities of a card: its current ones, or what it had on the battlefield if it just left ("when this dies").</summary>
    private static IEnumerable<TriggeredAbility> TriggerAbilitiesOf(Card card) =>
        (card.Zone == Zone.Battlefield || card.LastKnownInfo is null ? card.Abilities : card.LastKnownInfo.Abilities).OfType<TriggeredAbility>()
        .Where(a => !a.FromGraveyard);

    /// <summary>Permanents (and cards in graveyards with abilities that work from there) watching for events.</summary>
    private IEnumerable<(Card Card, IEnumerable<TriggeredAbility> Abilities)> Observers() =>
        State.Battlefield.Concat(State.Emblems).Select(State.GetCard).Select(c => (c, c.Abilities.OfType<TriggeredAbility>().Where(a => !a.FromGraveyard)))
            .Concat(State.Players.SelectMany(p => p.Graveyard).Select(State.GetCard)
                .Where(c => c.Definition.Abilities.OfType<TriggeredAbility>().Any(a => a.FromGraveyard))
                .Select(c => (c, c.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard))))
            .ToList();

    private void Queue(CardId source, TriggerEvent trigger, PlayerId controller, TriggerInfo? info = null)
    {
        foreach (var ability in TriggerAbilitiesOf(State.GetCard(source)))
            if (ability.Trigger == trigger) AddPending(source, ability, controller, info);
    }

    /// <summary>
    /// Permanents on the battlefield when a group of simultaneous events began (several creatures dying at once):
    /// leaves-the-battlefield triggers look back in time, so those that left in the same event still see each other (rule 603.10a).
    /// </summary>
    private List<CardId>? _lookBack;

    private void BeginSimultaneous() => _lookBack ??= State.Battlefield.ToList();

    private void EndSimultaneous()
    {
        _lookBack = null;
        _batchTriggered.Clear();
    }

    /// <summary>Triggers of permanents watching for an event that happened to <paramref name="subject"/> ("whenever another creature you control enters").</summary>
    private void QueueObservers(TriggerEvent trigger, Card subject, PlayerId subjectController, TriggerInfo? info = null)
    {
        info ??= new TriggerInfo(subject.Id, subject.Version, subjectController);
        // A creature that died is judged as it last existed on the battlefield (rule 603.10a).
        bool lastKnown = trigger == TriggerEvent.CreatureDies;
        foreach (var (observer, abilities) in Observers())
            foreach (var ability in abilities)
                if (ability.Trigger == trigger && Matches(ability.Filter ?? DefaultFilter(trigger), subject, subjectController, observer, observer.Controller, lastKnown))
                    AddPending(observer.Id, ability, observer.Controller, info);
        // Permanents that left the battlefield at the same time still see this one leave (look back in time).
        if (trigger == TriggerEvent.CreatureDies && _lookBack is not null)
            foreach (var id in _lookBack.Where(id => id != subject.Id))
            {
                var gone = State.GetCard(id);
                if (gone.Zone == Zone.Battlefield || gone.LastKnownInfo is not { } lki) continue;
                foreach (var ability in lki.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == trigger && !a.FromGraveyard))
                    if (Matches(ability.Filter ?? DefaultFilter(trigger), subject, subjectController, gone, lki.Controller, lastKnown))
                        AddPending(id, ability, lki.Controller, info);
            }
        // A creature watching for deaths sees its own death too ("whenever this or another creature you control dies").
        if (trigger == TriggerEvent.CreatureDies && subject.Zone == Zone.Graveyard)
            foreach (var ability in (subject.LastKnownInfo?.Abilities ?? subject.Definition.Abilities).OfType<TriggeredAbility>())
                if (ability.Trigger == trigger && ability.Filter is { Other: false } f && Matches(f, subject, subjectController, null, subjectController, lastKnown))
                    AddPending(subject.Id, ability, subjectController, info);
    }

    /// <summary>
    /// A permanent's controller; once it has left the battlefield, its controller as it last existed there (rule 608.2h),
    /// so "its controller creates a token" after destroying a stolen permanent is the player who controlled it.
    /// </summary>
    private static PlayerId ControllerOrLastKnown(Card card, int? chosenVersion) =>
        chosenVersion is { } v && v != card.Version && card.Zone != Zone.Battlefield && card.LastKnownInfo is { } lki ? lki.Controller : card.Controller;

    /// <summary>Spells the ability's controller cast this turn before the spell that triggered it.</summary>
    private IEnumerable<Card> SpellsCastBefore(EffectContext ctx)
    {
        var cast = State.GetPlayer(ctx.Controller).SpellsCastThisTurn;
        int index = ctx.Trigger?.Subject is { } spell ? cast.LastIndexOf(spell) : -1;
        return (index < 0 ? cast : cast.Take(index)).Select(State.GetCard);
    }

    /// <summary>A card that left the battlefield was a creature there (an animated land dies; a creature turned into a land doesn't).</summary>
    private static bool WasCreature(Card card) => card.LastKnownInfo is { } lki ? (lki.Types & CardType.Creature) != 0 : card.IsCreature;

    /// <summary>What an observing trigger watches when its script gives no filter: lands for landfall, otherwise creatures you control.</summary>
    private static ObjectFilter DefaultFilter(TriggerEvent trigger) => trigger switch
    {
        TriggerEvent.LandEnters => new ObjectFilter(CardType.Land),
        TriggerEvent.PermanentEnters or TriggerEvent.LeavesGraveyard => new ObjectFilter(),
        _ => ObjectFilter.YourCreatures,
    };

    /// <summary>
    /// A player's simultaneous triggered abilities go on the stack in the order that player chooses (rule 603.3b); the
    /// last one put on the stack resolves first. Identical abilities of the same source need no choice.
    /// </summary>
    private async Task<List<PendingTrigger>> OrderTriggersAsync(PlayerId player, List<PendingTrigger> triggers)
    {
        var ordered = new List<PendingTrigger>();
        var left = triggers.ToList();
        while (left.Select(t => (t.Source, t.Ability)).Distinct().Count() > 1)
        {
            var labels = left.Select(t => $"{State.GetCard(t.Source).Name}: {t.Ability.Text}").ToList();
            int pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player),
                new OptionRequest("Put which triggered ability on the stack next? (the last one put there resolves first)", left[0].Source, labels, OptionKind.Other));
            var chosen = left[Math.Clamp(pick, 0, left.Count - 1)];
            ordered.Add(chosen);
            left.Remove(chosen);
        }
        ordered.AddRange(left);
        return ordered;
    }

    /// <summary>An ability with an intervening "if" clause triggers only if the condition holds now (rule 603.4).</summary>
    private void AddPending(CardId source, TriggeredAbility ability, PlayerId controller, TriggerInfo? info = null)
    {
        if (ability.Condition is { } condition && !Holds(condition, controller, State.GetCard(source))) return;
        if (ability.TriggerCondition is { } when && !Holds(when, controller, State.GetCard(source))) return;
        if (ability.NthOfTurn is { } nth && NthOfTurnFor(ability, State.GetCard(source), controller, info) != nth) return;
        if (ability.Batched && _lookBack is not null && !_batchTriggered.Add((source, ability))) return; // "one or more": once per simultaneous event
        if (ability.OncePerTurn && !State.GetCard(source).TriggeredThisTurn.Add(ability)) return;
        var pending = new PendingTrigger(source, ability, controller, info) { SourceVersion = State.GetCard(source).Version };
        _pendingTriggers.Add(pending);
        // "If a triggered ability of [this] triggers, that ability triggers an additional time."
        for (int i = ExtraTriggersFor(State.GetCard(source)); i > 0; i--) _pendingTriggers.Add(pending);
    }

    /// <summary>Abilities that trigger an additional time for each static ability saying so that affects their source.</summary>
    private readonly HashSet<(CardId, TriggeredAbility)> _batchTriggered = new();

    private int ExtraTriggersFor(Card source)
    {
        if (source.Zone != Zone.Battlefield) return 0;
        var battlefield = State.Battlefield.Select(State.GetCard).ToList();
        int extra = 0;
        foreach (var permanent in battlefield)
            foreach (var st in permanent.Abilities.OfType<StaticAbility>().Where(a => a.ExtraTriggers))
                if ((st.While is not { } cond || Holds(cond, permanent.Controller, permanent))
                    && Affected(permanent, st.Affects, battlefield).Any(c => c.Id == source.Id
                        && (st.Filter is not { } f || Matches(f with { Controller = ControllerFilter.Any }, c, c.Controller, permanent, permanent.Controller))))
                    extra++;
        return extra;
    }

    private int NthOfTurn(TriggerEvent trigger, PlayerId player) => trigger switch
    {
        TriggerEvent.YouDrawCard => State.GetPlayer(player).CardsDrawnThisTurn,
        TriggerEvent.YouGainLife => State.GetPlayer(player).LifeGainsThisTurn,
        _ => 1,
    };

    private int NthOfTurnFor(TriggeredAbility ability, Card source, PlayerId controller, TriggerInfo? info) => ability.Trigger switch
    {
        TriggerEvent.Attacks => source.AttacksThisTurn,
        // "Whenever an opponent draws their second card each turn".
        TriggerEvent.OpponentDrawsCard when info?.Player is { } drawer => State.GetPlayer(drawer).CardsDrawnThisTurn,
        // "Whenever an opponent casts their first noncreature spell each turn": that player's matching spells this turn, this one included.
        TriggerEvent.OpponentCastsSpell or TriggerEvent.YouCastSpell or TriggerEvent.AnyPlayerCastsSpell when info?.Player is { } caster
            => State.GetPlayer(caster).SpellsCastThisTurn.Select(State.GetCard)
                .Count(c => Matches((ability.Filter ?? new ObjectFilter()) with { Controller = ControllerFilter.Any }, c, caster, source, controller)),
        _ => NthOfTurn(ability.Trigger, controller),
    };

    /// <summary>
    /// Permanents entering the battlefield at the same time (several tokens, "put any number onto the battlefield"):
    /// their enter triggers wait until all of them are there, so they see each other enter (rule 603.6a).
    /// </summary>
    private List<CardId>? _enteringTogether;
    private int _enteringTogetherDepth;

    private void BeginEnteringTogether()
    {
        _enteringTogether ??= new List<CardId>();
        _enteringTogetherDepth++;
    }

    private void EndEnteringTogether()
    {
        if (--_enteringTogetherDepth > 0) return;
        var entered = _enteringTogether;
        _enteringTogether = null;
        foreach (var id in entered ?? new List<CardId>())
            if (State.GetCard(id).Zone == Zone.Battlefield) QueueEnterTriggers(id);
    }

    private void QueueEnterTriggers(CardId id)
    {
        var entering = State.GetCard(id);
        Queue(id, TriggerEvent.EntersBattlefield, entering.Controller);
        if (entering.IsCreature) QueueObservers(TriggerEvent.CreatureEnters, entering, entering.Controller);
        QueueObservers(TriggerEvent.PermanentEnters, entering, entering.Controller);
        if (entering.Is(CardType.Land)) QueueObservers(TriggerEvent.LandEnters, entering, entering.Controller);
    }

    /// <summary>
    /// Combat damage dealt in the current damage step, by source: "whenever this deals combat damage" triggers once per
    /// creature per step, with the total it dealt (rule 510.2: all combat damage is dealt at the same time).
    /// </summary>
    private Dictionary<CardId, (int Amount, PlayerId? Player)>? _combatDamageDealt;

    private void NoteCombatDamage(Card source, PlayerId? player, int amount)
    {
        if (_combatDamageDealt is null)
        {
            QueueCombatDamageTriggers(source, player, amount);
            return;
        }
        var (total, firstPlayer) = _combatDamageDealt.GetValueOrDefault(source.Id);
        _combatDamageDealt[source.Id] = (total + amount, firstPlayer ?? player);
    }

    private void BeginCombatDamage() => _combatDamageDealt = new();

    private void EndCombatDamage()
    {
        var dealt = _combatDamageDealt;
        _combatDamageDealt = null;
        if (dealt is null) return;
        foreach (var (source, (amount, player)) in dealt) QueueCombatDamageTriggers(State.GetCard(source), player, amount);
    }

    private void QueueCombatDamageTriggers(Card source, PlayerId? player, int amount)
    {
        var info = new TriggerInfo(source.Id, source.Version, player, amount);
        Queue(source.Id, TriggerEvent.DealsCombatDamage, source.Controller, info);
        QueueObservers(TriggerEvent.CreatureDealsCombatDamage, source, source.Controller, info);
    }

    /// <summary>A "copy that spell" trigger of <paramref name="source"/> for a spell just cast.</summary>
    private void QueueCopyThatSpell(CardId source, PlayerId controller, Card spell) =>
        _pendingTriggers.Add(new PendingTrigger(source, CopyThatSpell, controller, new TriggerInfo(spell.Id, spell.Version, controller)));

    private static readonly TriggeredAbility CopyThatSpell = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CopySpell(Subject.Triggered, 1) },
        Text = "Copy that spell. You may choose new targets for the copy.",
    };

    private static readonly TriggeredAbility ReturnAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new PutOntoBattlefield(Subject.Triggered) },
        Text = "Return it to the battlefield.",
    };

    private static readonly TriggeredAbility SacrificeAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new SacrificeIt(Subject.Triggered) },
        Text = "Sacrifice it at the beginning of the end step.",
    };

    private static readonly TriggeredAbility SacrificeThisAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new SacrificeIt(Subject.Self) },
        Text = "At the beginning of the end step, sacrifice this token.",
    };

    private static readonly TriggeredAbility ProwessTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new PumpUntilEndOfTurn(1, 1, Subject.Self) },
        Text = "Prowess",
    };

    /// <summary>
    /// Puts waiting triggered abilities on the stack in APNAP order (603.3b), choosing their targets. A trigger
    /// with no legal targets is removed (603.3d). Returns true if anything was put on the stack.
    /// </summary>
    private async Task<bool> PutPendingTriggersOnStackAsync()
    {
        if (_pendingTriggers.Count == 0) return false;
        var pending = _pendingTriggers.ToList();
        _pendingTriggers.Clear();
        bool any = false;
        foreach (var player in State.ApnapOrder().ToList())
        {
            foreach (var trigger in await OrderTriggersAsync(player, pending.Where(t => t.Controller == player).ToList()))
            {
                _triggeredPlayer = trigger.Info?.Player;
                if (!HasLegalTargets(trigger.Ability, player, trigger.Source)) continue;
                var ability = await ChooseModesAsync(player, trigger.Ability, trigger.Source, canCancel: false);
                if (ability is null) continue;
                _triggeredPlayer = trigger.Info?.Player;
                var targets = (await ChooseTargetsAsync(player, ability, trigger.Source, ability.Text, canCancel: false))!;
                Emit(new AbilityTriggered(player, trigger.Source, ability.Text));
                any = true;
                PushStack(new AbilityOnStack(trigger.Source, ability, player, targets)
                {
                    Trigger = trigger.Info, SourceVersion = trigger.SourceVersion >= 0 ? trigger.SourceVersion : State.GetCard(trigger.Source).Version,
                });
            }
        }
        return any;
    }
}
