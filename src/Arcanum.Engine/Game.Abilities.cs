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
    private sealed record PendingTrigger(CardId Source, TriggeredAbility Ability, PlayerId Controller, TriggerInfo? Info = null);

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

    private IEnumerable<Target> LegalTargets(TargetSpec spec, PlayerId controller, CardId source)
    {
        bool ControllerOk(PlayerId owner) => spec.Controller switch
        {
            ControllerFilter.You => owner == controller,
            ControllerFilter.Opponent => owner != controller,
            _ => true,
        };
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
                    if (FilterOk(State.GetCard(spell.Card))) yield return Target.Of(spell.Card);
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
            if (card.Has(Keyword.ProtectionFromEverything)) continue; // 702.16b
            if (card.Controller != controller && (card.Definition.HexproofFromTypes & sourceCard.Types) != 0) continue;
            yield return Target.Of(card.Id);
        }
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
    private async Task<IReadOnlyList<ChosenTarget>?> ChooseTargetsAsync(
        PlayerId player, AbilityDefinition? ability, CardId source, string text, bool canCancel)
    {
        if (ability is null || ability.Targets.Count == 0) return Array.Empty<ChosenTarget>();
        var legal = ability.Targets.Select(spec => (IReadOnlyList<Target>)LegalTargets(spec, player, source).ToList()).ToList();
        var request = new TargetRequest(source, text, ability.Targets, legal, canCancel);

        var chosen = await ControllerOf(player).ChooseTargetsAsync(ViewFor(player), request);
        if (chosen is null)
        {
            Require(canCancel, "These targets must be chosen.");
            return null;
        }
        Require(chosen.Count == legal.Count, $"Choose exactly {legal.Count} target(s).");
        for (int i = 0; i < chosen.Count; i++) Require(legal[i].Contains(chosen[i]), $"Illegal target {chosen[i]}.");
        // The same object can't be chosen twice for one "target" word, e.g. "up to two target creatures" (115.3).
        for (int i = 0; i < chosen.Count; i++)
            for (int j = i + 1; j < chosen.Count; j++)
                Require(chosen[i].IsNone || chosen[i] != chosen[j] || ability.Targets[i] != ability.Targets[j], "Choose different targets.");
        return chosen.Select(t => new ChosenTarget(t, VersionOf(t))).ToList();
    }

    private int VersionOf(Target target) => target.Card is { } c ? State.GetCard(c).Version : 0;

    private bool IsStillLegal(ChosenTarget chosen, TargetSpec spec, PlayerId controller, CardId source)
    {
        if (chosen.Target.IsNone) return false;
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
        var legal = new bool[item.Targets.Count];
        for (int i = 0; i < item.Targets.Count; i++)
            legal[i] = IsStillLegal(item.Targets[i], ability.Targets[i], item.Controller, source.Id);
        // 608.2b: only targets actually chosen count ("up to one" left empty doesn't make it fizzle).
        if (item.Targets.Any(t => !t.Target.IsNone) && !legal.Any(l => l)) return false;

        // An intervening "if" clause is checked again on resolution (rule 603.4).
        if (ability is TriggeredAbility { Condition: { } condition } && !Holds(condition, item.Controller, source)) return true;

        var context = new EffectContext(item.Controller, source, item.Targets, legal, item.X, item.Kicked) { Trigger = (item as AbilityOnStack)?.Trigger };
        context.Results.Sacrificed.AddRange(item.SacrificedForCost);
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

        /// <summary>What earlier effects of this spell or ability did ("this way"): shared by all its effects.</summary>
        public EffectResults Results { get; init; } = new();

        /// <summary>The chosen target an effect calls "target N", if it is still legal.</summary>
        public Target? TargetAt(int index)
        {
            int i = TargetOffset + index;
            return i < Targets.Count && TargetLegal[i] ? Targets[i].Target : null;
        }

        /// <summary>The chosen target even if it became illegal (for "equal to its power" and the like).</summary>
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
    }

    private IEnumerable<Card> CardsFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Card is { } id => new[] { State.GetCard(id) },
        SubjectKind.Self when ctx.Source.Zone == Zone.Battlefield => new[] { ctx.Source },
        SubjectKind.Attached when ctx.Source.Zone == Zone.Battlefield && ctx.Source.AttachedTo is { } host => new[] { State.GetCard(host) },
        SubjectKind.Triggered when ctx.Trigger is { Subject: { } id } info && State.GetCard(id).Version == info.SubjectVersion => new[] { State.GetCard(id) },
        SubjectKind.Each when subject.Filter is { } filter =>
            State.Battlefield.Select(State.GetCard).Where(c => Matches(filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList(),
        _ => Array.Empty<Card>(),
    };

    private IEnumerable<PlayerId> PlayersFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.You => new[] { ctx.Controller },
        SubjectKind.EachOpponent => State.OpponentsOf(ctx.Controller).ToList(),
        SubjectKind.EachPlayer => State.LivingPlayers.Select(p => p.Id).ToList(),
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Player is { } p => new[] { p },
        SubjectKind.TargetController when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { State.GetCard(c).Controller },
        SubjectKind.TargetOwner when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { State.GetCard(c).Owner },
        SubjectKind.TriggeredPlayer when ctx.Trigger?.Player is { } p => new[] { p },
        SubjectKind.Triggered when ctx.Trigger?.Subject is { } c => new[] { State.GetCard(c).Controller },
        _ => Array.Empty<PlayerId>(),
    };

    /// <summary>Works out a quantity as the effect happens.</summary>
    private int Eval(Quantity q, EffectContext ctx)
    {
        Card? TargetCard() => ctx.ChosenAt(q.Index)?.Card is { } id ? State.GetCard(id) : null;
        int value = q.Kind switch
        {
            QuantityKind.Fixed => q.Value,
            QuantityKind.X => ctx.X,
            QuantityKind.PermanentCount => State.Battlefield.Select(State.GetCard)
                .Count(c => Matches(q.Filter ?? ObjectFilter.YourCreatures, c, c.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.GraveyardCount => State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.SourcePower => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null ? ctx.Source.Power : ctx.Source.LastKnownInfo.Power,
            QuantityKind.TargetPower => TargetCard()?.Power ?? 0,
            QuantityKind.TargetToughness => TargetCard()?.Toughness ?? 0,
            QuantityKind.TargetManaValue => TargetCard()?.Definition.ManaCost.ManaValue ?? 0,
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
            case CreateTokenCopy tc:
            {
                int count = Eval(tc.Count, ctx);
                var original = tc.Of.Kind == SubjectKind.Self ? ctx.Source : CardsFor(tc.Of, ctx).FirstOrDefault();
                if (original is null) break;
                var copy = original.Definition with
                {
                    IsToken = true,
                    Keywords = tc.Haste ? original.Definition.Keywords.Append("Haste").ToList() : original.Definition.Keywords,
                };
                for (int i = 0; i < count; i++)
                {
                    var id = CreateToken(copy, ctx.Controller);
                    if (tc.SacrificeAtEndStep && id is { } token)
                        State.AtNextEndStep.Add(new DelayedAction(token, State.GetCard(token).Version, Return: false, ctx.Controller));
                }
                break;
            }
            case SacrificeIt si:
                foreach (var card in (si.What.Kind == SubjectKind.Self ? (ctx.Source.Zone == Zone.Battlefield ? new[] { ctx.Source } : Array.Empty<Card>()) : CardsFor(si.What, ctx)).ToList())
                    if (card.Zone == Zone.Battlefield) SacrificePermanent(card.Id);
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
                    foreach (var id in chosen)
                    {
                        MoveCard(id, Zone.Graveyard);
                        Emit(new CardDiscarded(player, id));
                    }
                }
                break;
            case CreateEmblem ce:
            {
                var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                var emblem = new Card(id, new CardDefinition { Name = ce.Name, Abilities = ce.Abilities, IsEmblem = true, IsToken = true,
                    OracleText = string.Join("\n", ce.Abilities.Select(a => a.Text)) }, ctx.Controller) { Zone = Zone.Command };
                State.Cards.Add(id, emblem);
                State.Emblems.Add(id);
                Emit(new EmblemCreated(id, ctx.Controller));
                break;
            }
            case ExileTopPlayable ep:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(ep.Count).ToList();
                foreach (var id in top) MoveCard(id, Zone.Exile);
                IReadOnlyList<CardId> playable = top;
                if (ep.ChooseOne && top.Count > 1)
                {
                    var options = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                    playable = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("Choose a card you may play this turn", ctx.Source.Id, options, 1, 1, CardChoicePurpose.ToHand));
                    Require(playable.Count == 1 && top.Contains(playable[0]), "Choose one of the exiled cards.");
                }
                int until = State.TurnNumber + (ep.UntilEndOfNextTurn ? State.LivingPlayers.Count() : 0);
                foreach (var id in playable)
                    State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, until, ep.WithoutPaying));
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
                    foreach (var card in mine.Where(c => !keep.Contains(c.Id))) SacrificePermanent(card.Id);
                }
                break;
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
                    PutCounters(chosen[i], CounterKind.PlusOnePlusOne, n);
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
                Emit(new HandRevealed(ctx.Controller, found is { } f ? revealed.Append(f).ToList() : revealed));
                if (found is { } hit) MoveCard(hit, ru.To, controller: ctx.Controller);
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
                    if (pick < payable.Count) await PayExtraAsync(player, payable[pick], ctx.Source.Id);
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
                var opponent = State.OpponentsOf(ctx.Controller).First();
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
            case ReturnExiledWithThis:
                foreach (var card in State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id
                                                                   && (w.Version == ctx.Source.Version || w.Version == ctx.Source.Version - 1)).ToList())
                    MoveCard(card.Id, Zone.Hand);
                break;
            case SearchAndExileWithThis se:
            {
                var player = State.GetPlayer(ctx.Controller);
                var options = player.Library.Select(State.GetCard).Where(c => Matches(se.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller))
                    .Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList();
                if (options.Count > 0)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("Search your library: choose a card to exile", ctx.Source.Id, options, 0, 1, CardChoicePurpose.ToHand));
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
                foreach (var card in CardsFor(b.What, ctx).ToList())
                {
                    if (b.Permanent)
                    {
                        if (b.SetSubtypes is { } replaced) { card.PermanentSubtypesOverride = replaced; card.PermanentSubtypes.Clear(); }
                        if (b.AddSubtypes is { } subtypes) card.PermanentSubtypes.UnionWith(subtypes);
                        if (b.Keywords is { } keywords) card.PermanentKeywords.UnionWith(keywords);
                        if (b.Abilities is { } abilities) card.PermanentAbilities.AddRange(abilities);
                        if (b.Power is { } bp) card.PermanentBasePower = bp;
                        if (b.Toughness is { } bt) card.PermanentBaseToughness = bt;
                        card.GrantedTypes |= b.AddTypes;
                        continue;
                    }
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, b.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                    {
                        AddTypes = b.AddTypes, SetPower = b.Power, SetToughness = b.Toughness, AddSubtypes = b.AddSubtypes, Abilities = b.Abilities,
                    });
                }
                break;
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
                    foreach (var (kind, have) in card.Counters.Where(kv => kv.Value > 0).ToList())
                        PutCounters(card, kind, have);
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
                foreach (var player in PlayersFor(ra.Who, ctx).ToList())
                    foreach (var card in State.GetPlayer(player).Graveyard.Select(State.GetCard)
                                 .Where(c => Matches(ra.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller)).ToList())
                        MoveCard(card.Id, Zone.Battlefield, controller: ctx.Controller);
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
                var cards = p.What.Kind == SubjectKind.Self
                    ? (ctx.Source.Zone is Zone.Graveyard or Zone.Hand or Zone.Exile ? new[] { ctx.Source } : Array.Empty<Card>())
                    : p.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc
                      && tc.Version == ti.SubjectVersion && tc.Zone != Zone.Battlefield ? new[] { tc }
                    : CardsFor(p.What, ctx).Where(c => c.Zone != Zone.Battlefield).ToArray();
                foreach (var card in cards)
                {
                    MoveCard(card.Id, Zone.Battlefield, controller: p.UnderOwnersControl ? card.Owner : ctx.Controller);
                    if (p.Tapped && !card.Tapped) card.Tapped = true;
                    if (p.Counters > 0) PutCounters(card, p.CounterKind, p.Counters);
                    if (p.AddSubtypes is { } subtypes) card.PermanentSubtypes.UnionWith(subtypes);
                    if (p.AddKeywords is { } keywords) card.PermanentKeywords.UnionWith(keywords);
                    RecomputeContinuousEffects();
                }
                break;
            }
            case SearchLibrary sl:
                foreach (var searcher in (sl.Who is { } who ? PlayersFor(who, ctx) : new[] { ctx.Controller }).ToList())
                {
                    if (sl.Optional && !await ControllerOf(searcher).ChooseYesNoAsync(ViewFor(searcher), new YesNoRequest("Search your library?", ctx.Source.Id))) continue;
                    await SearchLibraryAsync(searcher, sl, ctx.Source);
                }
                break;
            case Sacrifice sac:
                foreach (var player in PlayersFor(sac.Who, ctx).ToList())
                {
                    var gone = await SacrificeAsync(player, Eval(sac.Count, ctx), sac.Filter, ctx.Source);
                    ctx.Results.Sacrificed.AddRange(gone);
                    if (player == ctx.Controller && gone.Count > 0) ctx.Results.YouSacrificed = true;
                }
                break;
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
                var newController = g.NewController is { } who ? PlayersFor(who, ctx).FirstOrDefault() : ctx.Controller;
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
                foreach (var player in PlayersFor(d.Who, ctx)) await DiscardAsync(player, Eval(d.Count, ctx), ctx.Controller);
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
                    int before = card.Damage, lethal = Math.Max(0, card.Toughness - card.Damage);
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
                foreach (var card in CardsFor(d.What, ctx).Where(c => !c.Has(Keyword.Indestructible)).ToList())
                {
                    bool creature = card.IsCreature;
                    MoveCard(card.Id, Zone.Graveyard);
                    ctx.Results.Destroyed++;
                    Emit(new PermanentDestroyed(card.Id));
                    if (creature) Emit(new CreatureDied(card.Id));
                }
                break;
            case ExileIt x:
            {
                var exiling = x.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc && tc.Version == ti.SubjectVersion
                    ? new[] { tc }
                    : CardsFor(x.What, ctx).ToArray();
                foreach (var card in exiling)
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
                foreach (var card in CardsFor(r.What, ctx).ToList()) MoveCard(card.Id, Zone.Hand);
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
                        MoveCard(id, Zone.Graveyard);
                        Emit(new CardDiscarded(player, id));
                    }
                break;
            case TapIt t:
                foreach (var card in CardsFor(t.What, ctx).Where(c => !c.Tapped)) { card.Tapped = true; Emit(new PermanentTapped(card.Id)); }
                break;
            case UntapIt u:
                foreach (var card in CardsFor(u.What, ctx).Where(c => c.Tapped)) { card.Tapped = false; Emit(new PermanentUntapped(card.Id)); }
                break;
            case Mill m:
                foreach (var player in PlayersFor(m.Who, ctx))
                    foreach (var id in State.GetPlayer(player).Library.Take(Eval(m.Count, ctx)).ToList())
                    {
                        MoveCard(id, Zone.Graveyard);
                        ctx.Results.Milled.Add(id);
                    }
                break;
            case CounterSpell c:
                if (c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.Card is { } spellCard && CanBeCountered(State.GetCard(spellCard)))
                    CounterSpellOnStack(spellCard);
                break;
            case PumpUntilEndOfTurn p:
            {
                int power = Eval(p.Power, ctx), toughness = Eval(p.Toughness, ctx);
                foreach (var card in CardsFor(p.What, ctx).Where(c => c.IsCreature || (power == 0 && toughness == 0)))
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, power, toughness,
                        p.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>()));
            }
                break;
            case AddCounters a:
            {
                int count = Eval(a.Count, ctx);
                if (count <= 0) break;
                foreach (var card in CardsFor(a.What, ctx)) PutCounters(card, a.Kind, count);
            }
                break;
            case AttachSelf a:
                if (ctx.Source.Zone == Zone.Battlefield)
                    foreach (var card in CardsFor(a.To, ctx)) ctx.Source.AttachedTo = card.Id;
                break;
            case CreateTokens t:
            {
                int count = Eval(t.Count, ctx);
                foreach (var player in PlayersFor(t.Controller, ctx))
                {
                    int made = count * (Has(player, Replacements.DoubleTokens) ? 2 : 1);
                    for (int i = 0; i < made; i++)
                        if (CreateToken(t.Token, player, t.Tapped) is { } token && t.HasteUntilEndOfTurn)
                            State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(token, State.GetCard(token).Version, 0, 0, new[] { Keyword.Haste }));
                }
            }
                break;
            default:
                throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
        }
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
    private void PutCounters(Card card, CounterKind kind, int count)
    {
        if (count <= 0) return;
        if (Has(card.Controller, Replacements.DoubleCounters)) count *= 2;
        card.Counters[kind] = card.CounterCount(kind) + count;
        Emit(new CountersPlaced(card.Id, kind, count));
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
        if (targetCard is not null && targetCard.Has(Keyword.ProtectionFromEverything)) return 0; // 702.16e
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
        if (victim != source.Controller && Has(source.Controller, Replacements.DoubleDamageToOpponents)) amount *= 2;
        if (source.IsCreature && source.Zone == Zone.Battlefield && Has(source.Controller, Replacements.DoubleCreatureDamage)) amount *= 2;
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

        foreach (var id in chosen)
        {
            if (scry)
            {
                player.Library.Remove(id);
                player.Library.Add(id);
            }
            else MoveCard(id, Zone.Graveyard);
        }
        Emit(new LookedAtTop(who, top.Count, chosen.Count, scry));
    }

    private async Task SearchLibraryAsync(PlayerId who, SearchLibrary search, Card source)
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
            var onTop = new List<CardId>();
            foreach (var id in chosen)
            {
                if (search.To == Zone.Library) { onTop.Add(id); continue; }
                MoveCard(id, search.To, controller: who);
                if (search.To == Zone.Battlefield && search.Tapped) State.GetCard(id).Tapped = true;
            }
            Shuffle(player);
            foreach (var id in onTop) { player.Library.Remove(id); player.Library.Insert(0, id); }
            return;
        }
        Shuffle(player);
    }

    private async Task<IReadOnlyList<CardId>> SacrificeAsync(PlayerId who, int count, ObjectFilter filter, Card source)
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
        foreach (var id in chosen) SacrificePermanent(id);
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

    private static string DescribeCost(ExtraCost cost) =>
        cost.Discard > 0 ? $"Discard {cost.Discard} card{(cost.Discard > 1 ? "s" : "")}"
        : cost.Sacrifice is { } s ? $"Sacrifice {(s.Types == CardType.Creature ? "a creature" : "a permanent")}"
        : cost.PayLife > 0 ? $"Pay {cost.PayLife} life" : "Pay";

    /// <summary>Copies a spell on the stack (rule 707.10); the copy's controller may choose new targets.</summary>
    private async Task CopySpellAsync(SpellOnStack original, PlayerId controller)
    {
        var card = State.GetCard(original.Card);
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        var copy = new Card(id, card.Definition with { IsToken = true }, controller) { Zone = Zone.Stack, Controller = controller };
        State.Cards.Add(id, copy);
        var ability = original.Ability ?? CastingTargets(card.Definition);
        IReadOnlyList<ChosenTarget> targets = original.Targets;
        if (ability is { Targets.Count: > 0 }
            && await ControllerOf(controller).ChooseYesNoAsync(ViewFor(controller), new YesNoRequest($"Choose new targets for the copy of {card.Name}?", id)))
            targets = await ChooseTargetsAsync(controller, ability, id, card.Name, canCancel: true) ?? original.Targets;
        State.Stack.Add(new SpellOnStack(id, controller, targets) { Ability = original.Ability, X = original.X, Kicked = original.Kicked });
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
        foreach (var id in chosen)
        {
            if (look.TakeTo == Zone.Library) continue; // stays on top
            MoveCard(id, look.TakeTo, controller: who);
        }
        foreach (var id in top.Where(id => !chosen.Contains(id)))
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

    private async Task DiscardAsync(PlayerId who, int count, PlayerId? causedBy = null)
    {
        var player = State.GetPlayer(who);
        count = Math.Min(count, player.Hand.Count);
        if (count == 0) return;
        var chosen = await ControllerOf(who).ChooseDiscardAsync(ViewFor(who), count);
        Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(player.Hand.Contains),
            $"Must discard exactly {count} distinct cards from hand.");
        foreach (var card in chosen)
        {
            if (causedBy is { } cause && cause != who && State.GetCard(card).Definition.OntoBattlefieldIfOpponentMakesYouDiscard)
            {
                MoveCard(card, Zone.Battlefield);
                continue;
            }
            MoveCard(card, Zone.Graveyard);
            Emit(new CardDiscarded(who, card));
        }
    }

    // ------------------------------------------------------------------ conditions and filters

    /// <summary>A condition checked while an effect resolves, where targets are known.</summary>
    private bool HoldsIn(Condition condition, EffectContext ctx) => condition switch
    {
        TargetMatches t => ctx.ChosenAt(t.Index)?.Card is { } id && State.GetCard(id) is var card
                           && Matches(t.Filter with { Controller = ControllerFilter.Any }, card, card.Controller, ctx.Source, ctx.Controller),
        Not { Inner: TargetMatches or YouSacrificedThisWay or TargetLifeExactly or XAtLeast or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo } n => !HoldsIn(n.Inner, ctx),
        TargetLifeExactly t => ctx.ChosenAt(t.Index)?.Player is { } p && State.GetPlayer(p).Life == t.Life,
        XAtLeast x => ctx.X >= x.AtLeast,
        YouSacrificedThisWay => ctx.Results.YouSacrificed,
        TriggeredWasAttacking => ctx.Trigger?.Subject is { } t && State.GetCard(t).WasAttacking,
        TargetAttachedTo a => ctx.ChosenAt(a.Attached)?.Card is { } att && ctx.ChosenAt(a.To)?.Card is { } host && State.GetCard(att).AttachedTo == host,
        TriggeredHasCounters h => ctx.Trigger?.Subject is { } tc && State.GetCard(tc).CounterCount(CounterKind.PlusOnePlusOne) >= h.AtLeast,
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
            SourceHasCounters c => source is not null && source.CounterCount(c.Kind) >= c.AtLeast,
            SourceAttacking => source is not null && State.Combat?.FindAttack(source.Id) is not null,
            LifeAboveStarting l => player.Life >= Config.StartingLife + l.AtLeast,
            All a => a.Conditions.All(c => Holds(c, controller, source)),
            TotalPowerAtLeast t => State.PermanentsControlledBy(controller).Where(c => c.IsCreature).Sum(c => c.Power) >= t.Amount,
            TargetMatches or TargetLifeExactly or XAtLeast or YouSacrificedThisWay or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo => true, // checked where known (effects)
            SourceIs si => source is not null && Matches(si.Filter with { Controller = ControllerFilter.Any }, source, source.Controller, source, controller),
            SourceWasSubtype w => source?.LastKnownInfo?.Subtypes.Contains(w.Subtype, StringComparer.OrdinalIgnoreCase) == true || source?.HasSubtype(w.Subtype) == true && source.Zone == Zone.Battlefield,
            WasCastFromHand => source?.CastFromHand == true,
            SourceHadCounters h => (source?.LastKnownInfo?.Counters.GetValueOrDefault(h.Kind) ?? 0) > 0,
            DifferentNames d => State.PermanentsControlledBy(controller).Where(c => Matches(d.Filter, c, c.Controller, source, controller)).Select(c => c.Name).Distinct().Count() >= d.AtLeast,
            ResolvedThisTurn r => source is not null && source.ResolvedThisTurn.Values.DefaultIfEmpty(0).Max() >= r.Times,
            _ => throw new NotSupportedException($"Condition {condition.GetType().Name} is not implemented."),
        };
    }

    /// <summary>Whether <paramref name="obj"/> (controlled by <paramref name="objController"/>) fits the filter, seen from the ability's side.</summary>
    private bool Matches(ObjectFilter filter, Card obj, PlayerId objController, Card? source, PlayerId sourceController)
    {
        if (filter.Types != 0 && (obj.Types & filter.Types) == 0) return false;
        if ((obj.Types & filter.ExcludedTypes) != 0) return false;
        if (filter.Subtype is { } subtype && !obj.HasSubtype(subtype)) return false;
        if (filter.Controller == ControllerFilter.You && objController != sourceController) return false;
        if (filter.Controller == ControllerFilter.Opponent && objController == sourceController) return false;
        if (filter.Other && source is not null && obj.Id == source.Id) return false;
        if (filter.MinPower is { } min && obj.Power < min) return false;
        if (filter.MaxPower is { } maxPower && obj.Power > maxPower) return false;
        if (filter.MinToughness is { } minToughness && obj.Toughness < minToughness) return false;
        if (filter.MinManaValue is { } minMv && obj.Definition.ManaCost.ManaValue < minMv) return false;
        if (filter.MaxManaValue is { } maxMv && obj.Definition.ManaCost.ManaValue > maxMv) return false;
        if (filter.Token is { } token && obj.Definition.IsToken != token) return false;
        if (filter.Colors is { Count: > 0 } colors && !ColorsOf(obj).Any(colors.Contains)) return false;
        if (filter.Keyword is { } keyword && !obj.Has(keyword)) return false;
        if (filter.WithoutKeyword is { } without && obj.Has(without)) return false;
        if (filter.Tapped is { } tapped && obj.Tapped != tapped) return false;
        if (filter.Supertype != 0 && (obj.Definition.Supertypes & filter.Supertype) == 0) return false;
        if (filter.ExcludedSupertype != 0 && (obj.Definition.Supertypes & filter.ExcludedSupertype) != 0) return false;
        if (filter.MaxManaValueLandCount && obj.Definition.ManaCost.ManaValue > State.PermanentsControlledBy(sourceController).Count(c => c.Is(CardType.Land))) return false;
        if (filter.ExcludedSubtype is { } excluded && obj.HasSubtype(excluded)) return false;
        if (filter.Name is { } name && obj.Name != name) return false;
        if (filter.AttachedToSource && (source is null || source.AttachedTo != obj.Id)) return false;
        if (filter.DamagedBySource && (source is null || !obj.DamagedThisTurnBy.Contains(source.Id))) return false;
        if (filter.Attached is { } attached && (obj.AttachedTo is not null) != attached) return false;
        if (filter.MaxManaValueSourcePower && source is not null
            && obj.Definition.ManaCost.ManaValue > (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.Power : source.LastKnownInfo.Power)) return false;
        if (filter.ChosenColor && (source?.ChosenColor is not { } color || !ColorsOf(obj).Contains(color))) return false;
        if (filter.ChosenType && (source?.ChosenType is not { } type || !obj.HasSubtype(type))) return false;
        if (filter.AnyOf is { Count: > 0 } anyOf && !anyOf.Any(f => Matches(f with { Controller = ControllerFilter.Any }, obj, objController, source, sourceController))) return false;
        if (filter.HasCounters is { } hasCounters && (obj.CounterCount(CounterKind.PlusOnePlusOne) > 0) != hasCounters) return false;
        if (filter.InCombat is { } inCombat || filter.Attacking is not null)
        {
            bool attacking = State.Combat?.FindAttack(obj.Id) is not null;
            bool blocking = State.Combat?.IsBlocking(obj.Id) == true;
            if (filter.InCombat is { } wantInCombat && (attacking || blocking) != wantInCombat) return false;
            if (filter.Attacking is { } wantAttacking && attacking != wantAttacking) return false;
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
        foreach (var card in battlefield)
        {
            card.PowerBonus = 0;
            card.ToughnessBonus = 0;
            card.GrantedKeywords.Clear();
            card.GrantedSubtypes.Clear();
            card.GrantedAbilities.Clear();
            card.TypesOverride = null;
            card.GrantedTypes = 0;
            card.SubtypesOverride = null;
            card.ColorsOverride = null;
            card.NameOverride = null;
            card.LosesAbilities = false;
            card.ManaTypesOverride = null;
            card.Controller = card.BaseController;
            card.GrantedKeywords.UnionWith(card.PermanentKeywords);
            card.GrantedSubtypes.UnionWith(card.PermanentSubtypes);
            card.SubtypesOverride = card.PermanentSubtypesOverride;
            card.GrantedAbilities.AddRange(card.PermanentAbilities);
            // Characteristic-defining abilities set base power/toughness (layer 7a).
            var cda = new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            card.BasePowerOverride = card.Definition.PowerFrom is { } pf ? Eval(pf, cda) : null;
            card.BaseToughnessOverride = card.Definition.ToughnessFrom is { } tf ? Eval(tf, cda) : null;
            card.ManaAmount = card.Definition.ManaAmountFrom is { } ma ? Eval(ma, cda) : card.Definition.ManaAmount;
            if (card.PermanentBasePower is { } pbp) card.BasePowerOverride = pbp;
            if (card.PermanentBaseToughness is { } pbt) card.BaseToughnessOverride = pbt;
        }
        State.UntilEndOfTurn.RemoveAll(e => State.GetCard(e.Card).Version != e.Version);
        var sources = battlefield.Concat(State.Emblems.Select(State.GetCard)).ToList();

        IEnumerable<Card> AffectedBy(Card source, StaticAbility ability) =>
            Affected(source, ability.Affects, battlefield).Where(a =>
                ability.Filter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, a, a.Controller, source, source.Controller));

        // Layer 2: control-changing static abilities ("You control enchanted permanent").
        foreach (var source in sources)
            foreach (var ability in source.Definition.Abilities.OfType<StaticAbility>().Where(a => a.GivesControl))
                foreach (var affected in AffectedBy(source, ability).ToList())
                    if (affected.Controller != source.Controller)
                    {
                        affected.Controller = source.Controller;
                        affected.ControlledSinceTurnStart = false;
                    }

        // Layer 6 first for "loses all abilities", so the abilities it removes don't apply below.
        foreach (var source in sources)
            foreach (var ability in source.Definition.Abilities.OfType<StaticAbility>().Where(a => a.LosesAllAbilities))
                foreach (var affected in AffectedBy(source, ability).ToList())
                    affected.LosesAbilities = true;

        // Layers 1–6: name, types, colors, abilities.
        foreach (var source in sources)
        {
            foreach (var ability in source.Abilities.OfType<StaticAbility>())
            {
                if (ability.While is { } condition && !Holds(condition, source.Controller, source)) continue;
                foreach (var affected in AffectedBy(source, ability).ToList())
                {
                    if (ability.SetName is { } name) affected.NameOverride = name;
                    if (ability.SetTypes is { } types) affected.TypesOverride = types;
                    if (ability.SetSubtypes is { } subtypes) affected.SubtypesOverride = subtypes;
                    if (ability.SetColors is { } colors) affected.ColorsOverride = colors;
                    affected.GrantedTypes |= ability.AddTypes;
                    if (ability.AddSubtypes is { } add) affected.GrantedSubtypes.UnionWith(add);
                    if (ability.AddChosenType && source.ChosenType is { } chosen) affected.GrantedSubtypes.Add(chosen);
                    affected.GrantedKeywords.UnionWith(ability.GrantedKeywords);
                    if (ability.GrantsAbilities is { } granted) affected.GrantedAbilities.AddRange(granted);
                    if (ability.GrantsMana is { } mana)
                    {
                        affected.ManaTypesOverride = mana;
                        affected.ManaAmount = ability.GrantsManaAmount;
                    }
                }
            }
        }
        foreach (var effect in State.UntilEndOfTurn)
        {
            var card = State.GetCard(effect.Card);
            if (card.Zone != Zone.Battlefield) continue;
            card.GrantedTypes |= effect.AddTypes;
            if (effect.AddSubtypes is { } subtypes) card.GrantedSubtypes.UnionWith(subtypes);
            card.GrantedKeywords.UnionWith(effect.Keywords);
            if (effect.Abilities is { } abilities) card.GrantedAbilities.AddRange(abilities);
        }

        // Layer 7b: set base power/toughness (statics, then effects in timestamp order).
        foreach (var source in sources)
            foreach (var ability in source.Abilities.OfType<StaticAbility>().Where(a => a.SetPower is not null || a.SetToughness is not null))
            {
                if (ability.While is { } condition && !Holds(condition, source.Controller, source)) continue;
                foreach (var affected in AffectedBy(source, ability).ToList())
                {
                    if (ability.SetPower is { } p) affected.BasePowerOverride = p;
                    if (ability.SetToughness is { } t) affected.BaseToughnessOverride = t;
                }
            }
        foreach (var effect in State.UntilEndOfTurn)
        {
            var card = State.GetCard(effect.Card);
            if (card.Zone != Zone.Battlefield) continue;
            if (effect.SetPower is { } p) card.BasePowerOverride = p;
            if (effect.SetToughness is { } t) card.BaseToughnessOverride = t;
        }

        // Layer 7c: modifications (filters may depend on the types and keywords settled above).
        foreach (var source in sources)
        {
            var ctx = new EffectContext(source.Controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            foreach (var ability in source.Abilities.OfType<StaticAbility>())
            {
                if (ability.While is { } condition && !Holds(condition, source.Controller, source)) continue;
                int power = ability.Power + (ability.PowerBonus is { } pb ? Eval(pb, ctx) : 0);
                int toughness = ability.Toughness + (ability.ToughnessBonus is { } tb ? Eval(tb, ctx) : 0);
                if (power == 0 && toughness == 0) continue;
                foreach (var affected in AffectedBy(source, ability).ToList())
                {
                    affected.PowerBonus += power;
                    affected.ToughnessBonus += toughness;
                }
            }
        }
        foreach (var effect in State.UntilEndOfTurn)
        {
            var card = State.GetCard(effect.Card);
            if (card.Zone != Zone.Battlefield) continue;
            card.PowerBonus += effect.Power;
            card.ToughnessBonus += effect.Toughness;
        }
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
        switch (e)
        {
            case CardMoved { To: Zone.Battlefield } m:
            {
                var entering = State.GetCard(m.Card);
                Queue(m.Card, TriggerEvent.EntersBattlefield, entering.Controller);
                if (entering.IsCreature) QueueObservers(TriggerEvent.CreatureEnters, entering, entering.Controller);
                if (entering.Is(CardType.Land)) QueueObservers(TriggerEvent.LandEnters, entering, entering.Controller);
                break;
            }
            case CardMoved { From: Zone.Battlefield, To: Zone.Graveyard } m when State.GetCard(m.Card).IsCreature:
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
                foreach (var card in State.PermanentsControlledBy(a.Player).ToList())
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
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != l.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentLosesLife, card.Controller, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
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
            case CountersPlaced cp when State.GetCard(cp.Card).Zone == Zone.Battlefield:
            {
                var target = State.GetCard(cp.Card);
                foreach (var observer in State.Battlefield.Select(State.GetCard).ToList())
                    foreach (var ability in observer.Abilities.OfType<TriggeredAbility>())
                    {
                        if (ability.Trigger != TriggerEvent.CountersPlaced || ability.CounterKind != cp.Kind) continue;
                        bool hit = ability.OnSelf ? observer.Id == target.Id
                            : Matches(ability.Filter ?? ObjectFilter.YourCreatures, target, target.Controller, observer, observer.Controller);
                        if (hit) AddPending(observer.Id, ability, observer.Controller, About(target, amount: cp.Count));
                    }
                break;
            }
            case PermanentSacrificed ps:
            {
                var gone = State.GetCard(ps.Card);
                Queue(ps.Card, TriggerEvent.SelfSacrificed, gone.LastKnownInfo?.Controller ?? gone.Owner);
                break;
            }
            case CardDiscarded cd:
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != cd.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentDiscards, card.Controller, new TriggerInfo(cd.Card, State.GetCard(cd.Card).Version, cd.Player));
                break;
            case PermanentUntapped u when State.GetCard(u.Card).Zone == Zone.Battlefield:
                Queue(u.Card, TriggerEvent.BecomesUntapped, State.GetCard(u.Card).Controller);
                break;
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
                            AddPending(observer.Id, ability, observer.Controller, About(spell, c.Player, spell.Definition.ManaCost.ManaValue));
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
            {
                var source = State.GetCard(cd2.Source);
                Queue(cd2.Source, TriggerEvent.DealsCombatDamage, source.Controller, About(source, amount: cd2.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamage, source, source.Controller, About(source, amount: cd2.Amount));
                break;
            }
            case DamageDealt { IsCombat: true, TargetPlayer: { } hurt } d:
            {
                var combatSource = State.GetCard(d.Source);
                Queue(d.Source, TriggerEvent.DealsCombatDamage, combatSource.Controller, About(combatSource, hurt, d.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamage, combatSource, combatSource.Controller, About(combatSource, hurt, d.Amount));
                var source = State.GetCard(d.Source);
                Queue(d.Source, TriggerEvent.DealsCombatDamageToPlayer, source.Controller, About(source, hurt, d.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamageToPlayer, source, source.Controller, About(source, hurt, d.Amount));
                break;
            }
            case StepBegan { Step: Step.Upkeep } s:
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

    /// <summary>Triggers of permanents watching for an event that happened to <paramref name="subject"/> ("whenever another creature you control enters").</summary>
    private void QueueObservers(TriggerEvent trigger, Card subject, PlayerId subjectController, TriggerInfo? info = null)
    {
        info ??= new TriggerInfo(subject.Id, subject.Version, subjectController);
        foreach (var (observer, abilities) in Observers())
            foreach (var ability in abilities)
                if (ability.Trigger == trigger && Matches(ability.Filter ?? ObjectFilter.YourCreatures, subject, subjectController, observer, observer.Controller))
                    AddPending(observer.Id, ability, observer.Controller, info);
        // A creature watching for deaths sees its own death too ("whenever this or another creature you control dies").
        if (trigger == TriggerEvent.CreatureDies && subject.Zone == Zone.Graveyard)
            foreach (var ability in (subject.LastKnownInfo?.Abilities ?? subject.Definition.Abilities).OfType<TriggeredAbility>())
                if (ability.Trigger == trigger && ability.Filter is { Other: false } f && Matches(f, subject, subjectController, null, subjectController))
                    AddPending(subject.Id, ability, subjectController, info);
    }

    /// <summary>An ability with an intervening "if" clause triggers only if the condition holds now (rule 603.4).</summary>
    private void AddPending(CardId source, TriggeredAbility ability, PlayerId controller, TriggerInfo? info = null)
    {
        if (ability.Condition is { } condition && !Holds(condition, controller, State.GetCard(source))) return;
        if (ability.NthOfTurn is { } nth && NthOfTurnFor(ability, State.GetCard(source), controller) != nth) return;
        if (ability.OncePerTurn && !State.GetCard(source).TriggeredThisTurn.Add(ability)) return;
        _pendingTriggers.Add(new PendingTrigger(source, ability, controller, info));
    }

    private int NthOfTurn(TriggerEvent trigger, PlayerId player) => trigger switch
    {
        TriggerEvent.YouDrawCard => State.GetPlayer(player).CardsDrawnThisTurn,
        TriggerEvent.YouGainLife => State.GetPlayer(player).LifeGainsThisTurn,
        _ => 1,
    };

    private int NthOfTurnFor(TriggeredAbility ability, Card source, PlayerId controller) =>
        ability.Trigger is TriggerEvent.Attacks ? source.AttacksThisTurn : NthOfTurn(ability.Trigger, controller);

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
            foreach (var trigger in pending.Where(t => t.Controller == player))
            {
                if (!HasLegalTargets(trigger.Ability, player, trigger.Source)) continue;
                var ability = await ChooseModesAsync(player, trigger.Ability, trigger.Source, canCancel: false);
                if (ability is null) continue;
                var targets = (await ChooseTargetsAsync(player, ability, trigger.Source, ability.Text, canCancel: false))!;
                Emit(new AbilityTriggered(player, trigger.Source, ability.Text));
                any = true;
                // Ward: the ability is countered unless its controller pays (702.21).
                var (wardMana, wardLife) = WardCost(player, targets);
                if (wardMana.ManaValue > 0 || wardLife > 0)
                {
                    var payer = State.GetPlayer(player);
                    bool paid = payer.Life >= wardLife && (wardMana.ManaValue == 0 || (Payable(player, wardMana, null) && await PayManaAsync(payer, trigger.Source, wardMana, null)));
                    if (!paid) continue;
                    if (wardLife > 0) ChangeLife(player, -wardLife);
                }
                State.Stack.Add(new AbilityOnStack(trigger.Source, ability, player, targets) { Trigger = trigger.Info });
            }
        }
        return any;
    }
}
