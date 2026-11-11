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
    // ------------------------------------------------------------------ resolution (rule 608)

    /// <summary>Carries out a resolving spell's or ability's effects. Returns false if every target became illegal.</summary>
    private async Task<bool> ApplyResolutionAsync(StackItem item, AbilityDefinition ability, Card source)
    {
        _triggeredPlayer = (item as AbilityOnStack)?.Trigger?.Player;
        _triggeredAmount = (item as AbilityOnStack)?.Trigger?.Amount ?? 0;
        _triggeredSubject = (item as AbilityOnStack)?.Trigger?.Subject;
        _triggeredSubjectVersion = (item as AbilityOnStack)?.Trigger?.SubjectVersion ?? 0;
        _triggeredDefender = (item as AbilityOnStack)?.Trigger?.Defender;
        _triggeredFound = (item as AbilityOnStack)?.Trigger?.Found;
        _announcedX = item.X;
        var savedSource = _resolvingSource;
        _resolvingSource = item is AbilityOnStack { SourceVersion: { } resolvingVersion }
            ? (source.Id, resolvingVersion, LeftBattlefieldVersion(ability, source, resolvingVersion)) : null;
        try { return await ApplyResolutionCoreAsync(item, ability, source); }
        finally { _resolvingSource = savedSource; }
    }

    private async Task<bool> ApplyResolutionCoreAsync(StackItem item, AbilityDefinition ability, Card source)
    {
        var legal = new bool[item.Targets.Count];
        for (int i = 0; i < item.Targets.Count; i++)
            legal[i] = IsStillLegal(item.Targets[i], SpecAt(ability, i), item.Controller, source.Id);
        // 608.2b: only targets actually chosen count ("up to one" left empty doesn't make it fizzle).
        if (item.Targets.Any(t => !t.Target.IsNone) && !legal.Any(l => l)) return false;

        // An intervening "if" clause is checked again on resolution (rule 603.4).
        if (ability is TriggeredAbility { Condition: { } condition } && !HoldsAtTrigger(condition, item.Controller, source, (item as AbilityOnStack)?.Trigger)) return true;

        // An enters ability that refers to X uses the X chosen when the permanent was cast (rule 107.3m).
        int x = item.X != 0 || ability is not TriggeredAbility { Trigger: TriggerEvent.EntersBattlefield } ? item.X : source.CastX;
        var context = new EffectContext(item.Controller, source, item.Targets, legal, x, item.Kicked)
        {
            Trigger = (item as AbilityOnStack)?.Trigger, GrantedBy = ability.GrantedBy, Ability = item is AbilityOnStack ? ability : null, Resolving = ability, SourceVersion = (item as AbilityOnStack)?.SourceVersion,
            SourceLeftVersion = (item as AbilityOnStack)?.SourceVersion is { } sv ? LeftBattlefieldVersion(ability, source, sv) : null,
            SourceTransforms = (item as AbilityOnStack)?.SourceTransforms,
            Division = item.Division, ActivatedIndex = (item as AbilityOnStack)?.AbilityIndex, Ninja = (item as AbilityOnStack)?.NinjutsuAttack,
        };
        context.Results.Sacrificed.AddRange(item.SacrificedForCost);
        context.Results.Discarded.AddRange(item.DiscardedForCost);
        context.Results.ExiledForCost.AddRange(item.ExiledForCost);
        if ((item as AbilityOnStack)?.Trigger?.Found is { } carried) context.Results.Found.AddRange(carried);
        // A promised gift is given before the spell's other effects (rule 702.174b).
        if (item is SpellOnStack { GiftTo: { } giftee } && (source.Is(CardType.Instant) || source.Is(CardType.Sorcery))) await GiveGiftAsync(giftee, source);
        if (item is AbilityOnStack { Ability: var counted }) source.ResolvedThisTurn[counted] = source.ResolvedThisTurn.GetValueOrDefault(counted) + 1;
        await ApplyAllAsync(ability.Effects, context);
        _announcedX = -1;
        RecomputeContinuousEffects();
        // "Whenever the final chapter ability of a Saga you control resolves."
        if (ability is TriggeredAbility { Trigger: TriggerEvent.Chapter } && item is AbilityOnStack { Trigger.Amount: var chapter } && chapter == source.Definition.FinalChapter)
            foreach (var card in State.PermanentsControlledBy(item.Controller).ToList()) Queue(card.Id, TriggerEvent.FinalChapterResolved, item.Controller);
        return true;
    }

    /// <summary>
    /// The source object (card and version) of the ability resolving now: conditions about "this" use it as it last existed.
    /// <c>LeftVersion</c>: the object that left the battlefield as the ability triggered or was activated (<see cref="LeftBattlefieldVersion"/>).
    /// </summary>
    private (CardId Card, int Version, int? LeftVersion)? _resolvingSource;

    /// <summary>"If this is untapped": as the resolving ability's source last existed on the battlefield if it left (rules 603.4, 608.2h).</summary>
    private bool SourceUntappedNow(Card source)
    {
        if (ResolvingVersionOf(source) is not { } version) return source.Zone == Zone.Battlefield && !source.Tapped;
        if (source.Zone == Zone.Battlefield && source.Version == version) return !source.Tapped;
        return ResolvingSourceLastKnown(source, version) is { } lki && !lki.Tapped;
    }

    private int? ResolvingVersionOf(Card card) => _resolvingSource is { } r && r.Card == card.Id ? r.Version : null;

    private async Task ApplyAllAsync(IEnumerable<Effect> effects, EffectContext ctx)
    {
        foreach (var effect in effects)
        {
            if (State.IsGameOver) return;
            bool wasSourceObject = IsSourceObject(ctx);
            int versionBefore = ctx.Source.Version;
            await ApplyAsync(effect, ctx);
            FollowMovedSource(ctx, wasSourceObject, versionBefore);
            await ResolvePendingCountersAsync();
            RecomputeContinuousEffects(); // later effects see earlier ones ("then it fights")
        }
    }

    private sealed record EffectContext(PlayerId Controller, Card Source, IReadOnlyList<ChosenTarget> Targets, bool[] TargetLegal,
        int X = 0, bool Kicked = false, int TargetOffset = 0)
    {
        /// <summary>How damage or counters were divided among the targets as it was cast or put on the stack (by target index).</summary>
        public IReadOnlyList<int>? Division { get; init; }

        /// <summary>The ability resolving ("this ability").</summary>
        public AbilityDefinition? Ability { get; init; }
        /// <summary>The spell or ability resolving, for "repeat this process" (its effects happen again from the start).</summary>
        public AbilityDefinition? Resolving { get; init; }

        /// <summary>For an activated ability: its index among its source's abilities.</summary>
        public int? ActivatedIndex { get; init; }

        /// <summary>The object picked at random by the effect being applied ("destroy one of them at random").</summary>
        public CardId? RandomPick { get; init; }

        public TriggerInfo? Trigger { get; init; }

        /// <summary>The player an effect is being applied to right now ("each opponent loses 1 life for each … in that player's graveyard").</summary>
        public PlayerId? AffectedPlayer { get; init; }

        /// <summary>The object a static ability is being applied to right now ("+1/+1 for each other creature that shares a creature type with it").</summary>
        public Card? AffectedCard { get; init; }

        /// <summary>For an ability: its source's object version when it triggered or was activated.</summary>
        public int? SourceVersion { get; init; }

        /// <summary>
        /// For an ability its source's leaving the battlefield triggered, or that sacrificed its source as a cost: the object
        /// (version) that left, which "this" means for what it was like (rule 608.2h).
        /// </summary>
        public int? SourceLeftVersion { get; init; }

        /// <summary>For ninjutsu: what the returned attacker was attacking.</summary>
        public (PlayerId Defender, CardId? Planeswalker)? Ninja { get; init; }

        /// <summary>For an ability: how many times its source had transformed when it was put on the stack (rule 701.27f).</summary>
        public int? SourceTransforms { get; init; }

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
        /// <summary>A spell was cast by this effect ("if you do, you can't cast additional spells this turn").</summary>
        public bool CastThisWay { get; set; }
        public int LifeLost { get; set; }
        public List<CardId> Milled { get; } = new();
        public int Destroyed { get; set; }
        /// <summary>The cards this effect destroyed that went to a graveyard ("a creature card put into your graveyard this way").</summary>
        public List<CardId> DestroyedCards { get; } = new();
        public int ExcessDamage { get; set; }
        public List<CardId> Exiled { get; } = new();
        public List<CardId> Created { get; } = new();
        public CardId? Amassed { get; set; }
        public List<CardId> Discarded { get; } = new();
        public List<CardId> ExiledForCost { get; } = new();
        public List<PlayerId> HandDiscarders { get; } = new();
        public List<CardId> Found { get; } = new();

        /// <summary>Cards put onto the battlefield by this effect, and permanents it attached.</summary>
        public List<CardId> Entered { get; } = new();
        public List<CardId> AttachedCards { get; } = new();
        public int CountersRemoved { get; set; }
        public int Rolled { get; set; }

        /// <summary>Players who drew at least one card in a draw effect of this spell or ability ("for each opponent who drew a card this way").</summary>
        public List<PlayerId> PlayersWhoDrew { get; } = new();

        /// <summary>Parley: the nonland cards revealed this way.</summary>
        public int NonlandRevealed { get; set; }
        /// <summary>Cards drawn by the draw effects so far ("the number of cards drawn this way").</summary>
        public int Drawn { get; set; }
        /// <summary>Cards a surveil of this spell or ability put into the graveyard.</summary>
        public List<CardId> Surveilled { get; } = new();

        /// <summary>The card discovered (or found by an exile-until effect) and its mana value as it was exiled, and the N it was discovered for.</summary>
        public (CardId Card, int ManaValue)? Discovered { get; set; }
        public int DiscoverTarget { get; set; }

        /// <summary>Power and toughness of revealed cards as they were revealed ("draw cards equal to its power").</summary>
        public Dictionary<CardId, (int Power, int Toughness)> FoundStats { get; } = new();
        public List<CardId> Returned { get; } = new();
        public int Tapped { get; set; }
        public List<CardId> Damaged { get; } = new();
        public int Attached { get; set; }
        public List<(PlayerId Voter, string Choice)> Votes { get; } = new();
        public List<CardId> Chosen { get; } = new();
        /// <summary>Cards chosen outside the battlefield ("one of them" in your graveyard), as the objects they were then.</summary>
        public List<(CardId Card, int Version)> ChosenElsewhere { get; } = new();
        public PlayerId? ChosenPlayer { get; set; }
        public List<CardId> ControlGained { get; } = new();
        /// <summary>Spells countered by this spell or ability, as the objects they were on the stack ("the spell countered this way").</summary>
        public List<(CardId Card, int Version)> Countered { get; } = new();
        public int AbilitiesCountered { get; set; }

        /// <summary>The object version of the source after an effect of this ability moved it (the ability follows it, rule 400.7).</summary>
        public int? FollowedSourceVersion { get; set; }
        public List<PlayerId> DamagedPlayers { get; } = new();
    }

    private IEnumerable<Card> CardsFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Card is { } id => new[] { State.GetCard(id) },
        SubjectKind.Self when SourceOnBattlefield(ctx) => new[] { ctx.Source },
        SubjectKind.ChosenAtRandom when ctx.RandomPick is { } picked => new[] { State.GetCard(picked) },
        SubjectKind.AttackedBySource when State.Combat?.FindAttack(ctx.Source.Id) is { Planeswalker: { } attackedWalker } => new[] { State.GetCard(attackedWalker) },
        SubjectKind.EachTarget => Enumerable.Range(subject.Index, Math.Max(0, ctx.Targets.Count - ctx.TargetOffset - subject.Index))
            .Select(i => ctx.TargetAt(i)?.Card).Where(c => c is not null).Select(c => State.GetCard(c!.Value))
            // "Each of those creatures you don't control": the targets that match.
            .Where(c => subject.Filter is not { } only || Matches(only, c, c.Controller, ctx.Source, ctx.Controller)).ToList(),
        SubjectKind.GranterPermanent when ctx.GrantedBy is { } granter && State.GetCard(granter).Zone == Zone.Battlefield => new[] { State.GetCard(granter) },
        SubjectKind.Attached when SourceOnBattlefield(ctx) && ctx.Source.AttachedTo is { } host => new[] { State.GetCard(host) },
        SubjectKind.Amassed when ctx.Results.Amassed is { } army && State.GetCard(army).Zone == Zone.Battlefield => new[] { State.GetCard(army) },
        SubjectKind.DamagedThisWay => ctx.Results.Damaged.Distinct().Select(State.GetCard)
            .Where(c => c.Zone == Zone.Battlefield && (subject.Filter is null || Matches(subject.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))).ToList(),
        SubjectKind.RingBearer when RingBearerOf(ctx.Controller) is { } bearer => new[] { bearer },
        SubjectKind.ControlGainedThisWay => ctx.Results.ControlGained.Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList(),
        SubjectKind.ExiledThisWay => ctx.Results.Exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile).ToList(),
        SubjectKind.ExiledWithSource => State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith == (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version)
                && (subject.Filter is null || Matches(subject.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)))
            .OrderBy(c => c.ExiledWithOrder).ToList(),
        SubjectKind.Chosen => ctx.Results.Chosen.Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield
                                  || ctx.Results.ChosenElsewhere.Contains((c.Id, c.Version))).ToList(),
        SubjectKind.AttackersOfTriggered when State.Combat is { } fight && ctx.Trigger?.Player is { } defender
            => fight.Attacks.Where(x => x.Defender == defender && x.Planeswalker == ctx.Trigger.Subject).Select(x => State.GetCard(x.Attacker)).ToList(),
        SubjectKind.Attackers when State.Combat is { } battle => battle.Attacks.Select(x => State.GetCard(x.Attacker)).ToList(),
        SubjectKind.Batch => (ctx.Trigger?.Batch ?? new()).Select(b => State.GetCard(b.Card)).Where((c, i) => c.Version == ctx.Trigger!.Batch![i].Version).ToList(),
        SubjectKind.Convokers => ctx.Source.Convokers.Select(x => State.GetCard(x.Card)).Where(c => c.Zone == Zone.Battlefield && ctx.Source.Convokers.Contains((c.Id, c.Version))).ToList(),
        SubjectKind.Created => ctx.Results.Created.Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList(),
        SubjectKind.Found => ctx.Results.Found.Select(State.GetCard).ToList(),
        SubjectKind.Milled => ctx.Results.Milled.Select(State.GetCard)
            .Where(c => c.Zone == Zone.Graveyard && (subject.Filter is null || Matches(subject.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller))).ToList(),
        SubjectKind.EnteredThisWay => ctx.Results.Entered.Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList(),
        SubjectKind.Discarded => ctx.Results.Discarded.Select(State.GetCard)
            .Where(c => c.Zone == Zone.Graveyard && (subject.Filter is null || Matches(subject.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller))).ToList(),
        SubjectKind.Triggered when ctx.Trigger is { Subject: { } id } info && State.GetCard(id).Version == info.SubjectVersion => new[] { State.GetCard(id) },
        SubjectKind.Each when subject.Filter is { } filter =>
            State.Battlefield.Select(State.GetCard).Where(c => Matches(filter, c, c.Controller, ctx.Source, ctx.Controller)
                                                               && (!subject.ControlledByChosenPlayer || ctx.Results.ChosenPlayer == c.Controller)
                                                               && (!subject.ControlledByTarget || (ctx.TargetAt(subject.Index)?.Player ?? (ctx.ChosenAt(subject.Index)?.Card is { } held ? ControllerOrLastKnown(State.GetCard(held), ctx.ChosenVersionAt(subject.Index)) : (PlayerId?)null)) == c.Controller)
                                                               && (!subject.ExceptTargets || ctx.Targets.All(t => t.Target.Card != c.Id))
                                                               && (!subject.ExceptChosen || !ctx.Results.Chosen.Contains(c.Id))
                                                               && (!subject.AttachedToTarget || (ctx.ChosenAt(subject.Index)?.Card is { } host && c.AttachedTo == host))).ToList(),
        _ => Array.Empty<Card>(),
    };

    private IEnumerable<PlayerId> PlayersFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.You => new[] { ctx.Controller },
        SubjectKind.TriggeredController when ctx.Trigger is { Subject: { } tc } ti => new[] { ControllerOrLastKnown(State.GetCard(tc), ti.SubjectVersion) },
        SubjectKind.OpponentsWhoDiscardedHand => ctx.Results.HandDiscarders.Distinct().Where(p => State.OpponentsOf(ctx.Controller).Contains(p)).ToList(),
        SubjectKind.SourceController => new[] { ctx.Source.Zone == Zone.Battlefield ? ctx.Source.Controller : ctx.Source.LastKnownInfo?.Controller ?? ctx.Source.Owner },
        // "Each player" / "each opponent" act one after another in APNAP order, starting with the active player (rule 101.4).
        SubjectKind.EachOpponent => State.ApnapOrder().Where(p => p != ctx.Controller).ToList(),
        SubjectKind.SourceOwner => new[] { ctx.Source.Owner },
        SubjectKind.ExiledWithSourceOwners => CardsFor(new Subject(SubjectKind.ExiledWithSource), ctx).Select(c => c.Owner).Distinct().ToList(),
        SubjectKind.PlayersOtherThanSourceOwner => State.ApnapOrder().Where(p => p != ctx.Source.Owner).ToList(),
        SubjectKind.OtherOpponents => State.ApnapOrder().Where(p => p != ctx.Controller && p != ctx.Trigger?.Player).ToList(),
        SubjectKind.AttackedBySource when State.Combat?.FindAttack(ctx.Source.Id) is { Planeswalker: null } attackedPlayer => new[] { attackedPlayer.Defender },
        SubjectKind.DefendingPlayer when State.Combat?.FindAttack(ctx.Trigger?.Subject ?? ctx.Source.Id) is { } attack => new[] { attack.Defender },
        SubjectKind.DefendingPlayer when ctx.Trigger?.Defender is { } defender => new[] { defender },
        SubjectKind.EachPlayer => State.ApnapOrder().ToList(),
        SubjectKind.Target when ctx.TargetAt(subject.Index)?.Player is { } p => new[] { p },
        SubjectKind.TargetController when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { ControllerOrLastKnown(State.GetCard(c), ctx.ChosenVersionAt(subject.Index)) },
        SubjectKind.TargetOwner when ctx.ChosenAt(subject.Index)?.Card is { } c => new[] { State.GetCard(c).Owner },
        SubjectKind.TriggeredPlayer when ctx.Trigger?.Player is { } p => new[] { p },
        SubjectKind.FixedPlayer when subject.Player is { } fixedPlayer => new[] { fixedPlayer },
        SubjectKind.EachTarget => Enumerable.Range(subject.Index, Math.Max(0, ctx.Targets.Count - ctx.TargetOffset - subject.Index))
            .Select(i => ctx.TargetAt(i)?.Player).Where(p => p is not null).Select(p => p!.Value).ToList(),
        SubjectKind.Triggered when ctx.Trigger?.Subject is { } c => new[] { State.GetCard(c).Controller },
        SubjectKind.ChosenPlayer when ctx.Results.ChosenPlayer is { } picked => new[] { picked },
        SubjectKind.PlayersDamagedThisWay => State.ApnapOrder().Where(ctx.Results.DamagedPlayers.Contains).ToList(),
        SubjectKind.PlayersWhoDrewThisWay => State.ApnapOrder().Where(ctx.Results.PlayersWhoDrew.Contains).ToList(),
        SubjectKind.EnchantedPlayer => (ctx.Source.Zone == Zone.Battlefield ? ctx.Source.AttachedPlayer : null) is { } enchantedPlayer ? new[] { enchantedPlayer } : Array.Empty<PlayerId>(),
        SubjectKind.OpponentsDamagedBySameName => State.ApnapOrder().Where(o => o != ctx.Controller).Where(o => State.GetPlayer(o).CombatDamagedByNames.Contains(ctx.Source.Name)).ToList(),
        SubjectKind.YouAndChosenPlayer => ctx.Results.ChosenPlayer is { } picked2 ? new[] { ctx.Controller, picked2 } : new[] { ctx.Controller },
        SubjectKind.OpponentsWhoVotedWithYou => OpponentsByVote(ctx, agreeing: true),
        SubjectKind.Sacrificers => State.ApnapOrder().Where(p => ctx.Results.Sacrificed.Any(id => (State.GetCard(id).LastKnownInfo?.Controller ?? State.GetCard(id).Owner) == p)).ToList(),
        SubjectKind.PlayerToYourRight => Enumerable.Range(1, State.Players.Count - 1).Select(i => State.Players[(ctx.Controller.Value - i + State.Players.Count) % State.Players.Count])
            .Where(p => !p.HasLost).Select(p => p.Id).Take(1).ToList(),
        SubjectKind.YouAndOpponentsWhoVotedWithYou => OpponentsByVote(ctx, agreeing: true).Prepend(ctx.Controller).ToList(),
        _ => Array.Empty<PlayerId>(),
    };

    private async Task ApplyAsync(Effect effect, EffectContext ctx)
    {
        if (await ApplyGroup1Async(effect, ctx)) return;
        if (await ApplyGroup2Async(effect, ctx)) return;
        if (await ApplyGroup3Async(effect, ctx)) return;
        if (await ApplyGroup4Async(effect, ctx)) return;
        throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
    }
}
