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
    // ------------------------------------------------------------------ conditions and filters

    /// <summary>A condition checked while an effect resolves, where targets are known.</summary>
    private bool HoldsIn(Condition condition, EffectContext ctx) => condition switch
    {
        Evolves => EvolvesHolds(ctx),
        TurnHappened th => TurnHappenedHolds(th, ctx),
        FullParty => PartySize(ctx.Controller) == 4,
        TriggeredPlayerIsActive => ctx.Trigger?.Player is { } triggeredPlayer && triggeredPlayer == State.ActivePlayer,
        TriggeredBlockedAttackingYourOpponent tb => ctx.Trigger?.Subject is { } attackerId && State.Combat?.FindAttack(attackerId) is { } blockedAttack
            && State.OpponentsOf(ctx.Controller).Contains(blockedAttack.Defender) && blockedAttack.Blockers.Count >= tb.Blockers,
        AttackersMatchChosenNumber => ctx.Source.ChosenNumber is { } n && ctx.Trigger is { Subject: null, Player: { } defending } && State.Combat is { } squallCombat
            && squallCombat.Attacks.Where(x => x.Defender == defending && x.Planeswalker is null).Select(x => State.GetCard(x.Attacker))
                .Any(c => c.Power == n || c.Toughness == n),
        TriggeredPlayerHasMoreLands => ctx.Trigger?.Player is { } that
            && State.PermanentsControlledBy(that).Count(c => c.Is(CardType.Land)) > State.PermanentsControlledBy(ctx.Controller).Count(c => c.Is(CardType.Land)),
        TargetMatches t => ctx.ChosenAt(t.Index)?.Card is { } id && State.GetCard(id) is var card
                           && Matches(t.Filter with { Controller = ControllerFilter.Any }, card, card.Controller, ctx.Source, ctx.Controller),
        Not { Inner: TargetMatches or YouSacrificedThisWay or CreatedThisWay or TargetLifeExactly or XAtLeast or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo or QuantityAtLeast or TargetControlledByYou } n => !HoldsIn(n.Inner, ctx),
        TargetLifeExactly t => ctx.ChosenAt(t.Index)?.Player is { } p && State.GetPlayer(p).Life == t.Life,
        XAtLeast x => ctx.X >= x.AtLeast,
        ActivatedThisTurn a => ctx.ActivatedIndex is { } activated && ctx.Source.ActivationsThisTurn.GetValueOrDefault(activated) >= a.Times,
        YouSacrificedThisWay => ctx.Results.YouSacrificed,
        MilledShareAllTypes => ctx.Results.Milled.Count >= 2 && ctx.Results.Milled.Select(id => State.GetCard(id).Definition.Types).Distinct().Count() < ctx.Results.Milled.Count,
        YouDiscardedThisWay => ctx.Results.Discarded.Any(id => State.GetCard(id).Owner == ctx.Controller),
        CastThisWay => ctx.Results.CastThisWay,
        CreatedThisWay => ctx.Results.Created.Count > 0,
        TriggeredWasAttacking => ctx.Trigger?.Subject is { } t && State.GetCard(t).WasAttacking,
        QuantityAtLeast qa => Eval(qa.Quantity, ctx) >= qa.AtLeast,
        TargetAttachedTo a => ctx.ChosenAt(a.Attached)?.Card is { } att && ctx.ChosenAt(a.To)?.Card is { } host && State.GetCard(att).AttachedTo == host,
        TriggeredHasCounters h => ctx.Trigger?.Subject is { } tc && State.GetCard(tc).CounterCount(CounterKind.PlusOnePlusOne) >= h.AtLeast,
        TriggeredMatches tm => ctx.Trigger?.Subject is { } subject && State.GetCard(subject) is var about
                               && Matches(tm.Filter with { Controller = ControllerFilter.Any }, about, about.Controller, ctx.Source, ctx.Controller, lastKnown: true),
        TargetPlayerLostLife tl => ctx.ChosenAt(tl.Index)?.Player is { } lost && State.GetPlayer(lost).LifeLostThisTurn > 0,
        TargetControlledByYou t => ctx.ChosenAt(t.Index)?.Card is { } owned && ControllerOrLastKnown(State.GetCard(owned), ctx.ChosenVersionAt(t.Index)) == ctx.Controller,
        SacrificedMatches sm => ctx.Results.Sacrificed.Select(State.GetCard)
            .Any(c => Matches(sm.Filter with { Controller = ControllerFilter.Any }, c, c.LastKnownInfo?.Controller ?? c.Owner, ctx.Source, ctx.Controller, lastKnown: true)),
        Not { Inner: SacrificedMatches } n => !HoldsIn(n.Inner, ctx),
        MoreVotes mv => VotesFor(ctx, mv.Option) is var mine && VotesIn(ctx).Select(v => v.Choice).Distinct().Where(c => c != mv.Option.ToString())
            .All(c => VotesIn(ctx).Count(v => v.Choice == c) < mine) && mine > 0,
        TriggeredPlayerHasMostLife => ctx.Trigger?.Player is { } attacked && State.GetPlayer(attacked).Life >= State.LivingPlayers.Max(p => p.Life),
        TriggeredPlayerIsYou => ctx.Trigger?.Player == ctx.Controller,
        FoundHas fh => ctx.Results.Found.Select(State.GetCard).Any(c => Matches(fh.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)),
        TriggeredAbilityCostHasX => ctx.Trigger?.Amount is { } activated && State.Stack.FirstOrDefault(s => s.Id == activated) is AbilityOnStack { Ability: ActivatedAbility withX }
                                    && withX.Cost.Mana?.XCount > 0,
        DefenderHasMoreLifeThanAnotherOpponent => ctx.Trigger?.Defender is { } defender
            && State.OpponentsOf(ctx.Controller).Any(o => o != defender && State.GetPlayer(o).Life < State.GetPlayer(defender).Life),
        TriggeredPlayerFirstLossThisTurn => ctx.Trigger is { Player: { } firstLoser, Amount: var firstLost } && State.GetPlayer(firstLoser).LifeLostThisTurn == firstLost,
        TriggeredPlayerFirstLossOnTheirTurn => ctx.Trigger is { Player: { } loser, Amount: var lost } && loser == State.ActivePlayer
                                               && State.GetPlayer(loser).LifeLostThisTurn == lost,
        TriggeredInGraveyard => ctx.Trigger is { Subject: { } tg } tgi && State.GetCard(tg) is { Zone: Zone.Graveyard } tgc && tgc.Version == tgi.SubjectVersion,
        TriggeredAttacksPlayerAlone => ctx.Trigger?.Subject is { } lone && State.Combat?.FindAttack(lone) is { Planeswalker: null } alone
                                       && State.Combat.Attacks.Count(a => a.Defender == alone.Defender && a.Planeswalker is null) == 1,
        TriggeredCrewedBySource => ctx.Trigger?.Subject is { } vehicle && State.CrewedThisTurn.Any(c => c.Vehicle == vehicle && c.Crewer == ctx.Source.Id && c.Turn == State.TurnNumber),
        Not { Inner: TriggeredPlayerHasMostLife } nm => !HoldsIn(nm.Inner, ctx),
        TriggeredHadCounters => ctx.Trigger?.Subject is { } had && State.GetCard(had).LastKnownInfo is { } hadLki && hadLki.Counters.Values.Any(v => v > 0),
        ReceivedNoVotes => !VotesIn(ctx).Any(v => v.Choice == $"p{(ctx.AffectedPlayer ?? ctx.Controller).Value}"),
        Not { Inner: MoreVotes or ReceivedNoVotes } nv => !HoldsIn(nv.Inner, ctx),
        TriggeredPlayerAttackedYou => ctx.Trigger?.Player is { } watched && State.GetPlayer(watched).PlayersAttackedThisTurn.Contains(ctx.Controller),
        YouAttackedWithCommanderThisTurn => State.GetPlayer(ctx.Controller).AttackedWithCommanderThisTurn,
        BatchHas bh => (ctx.Trigger?.Batch ?? new()).Any(b => State.GetCard(b.Card) is var bc && Matches(bh.Filter, bc, bc.Controller, ctx.Source, ctx.Controller, lastKnown: true)),
        All a => a.Conditions.All(c => HoldsIn(c, ctx)),
        // Any other negation is judged with what the effect knows too ("if that player isn't you").
        Not other => !HoldsIn(other.Inner, ctx),
        SourceDealtDamageThisTurn dd => DamageDealtThisTurnBy(ctx.Source.Id, SourceObjectVersion(ctx.Source, ctx.SourceVersion)) >= dd.AtLeast,
        _ => Holds(condition, ctx.Controller, ctx.Source),
    };

    /// <summary>
    /// A trigger condition checked as the event happens: what the trigger is about is known then ("if you chose a
    /// creature other than this", "if no mana was spent to cast it"); targets are not chosen yet.
    /// </summary>
    private bool HoldsAtTrigger(Condition condition, PlayerId controller, Card source, TriggerInfo? info)
    {
        var ctx = new EffectContext(controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>()) { Trigger = info };
        static bool AboutTrigger(Quantity q) => q.Parts?.Any(AboutTrigger) == true || q.Kind is QuantityKind.ManaSpent or QuantityKind.TriggerAmount or QuantityKind.TriggeredPower or QuantityKind.TriggeredToughness or QuantityKind.TriggeredColors or QuantityKind.TriggeredPlayerAttackers or QuantityKind.TriggeredPlayerAttackersAtYou or QuantityKind.TriggeredPlayerHandSize or QuantityKind.SpellsCastBeforeTriggered;
        return condition switch
        {
            TriggeredAttacksPlayerAlone or TriggeredMatches or TriggeredPlayerHasMostLife or TriggeredHadCounters or TriggeredPlayerIsYou or DefenderHasMoreLifeThanAnotherOpponent or TriggeredAbilityCostHasX or FoundHas or TriggeredPlayerFirstLossThisTurn or TriggeredPlayerFirstLossOnTheirTurn or TriggeredCrewedBySource or Evolves or TriggeredPlayerHasMoreLands or AttackersMatchChosenNumber or TriggeredPlayerIsActive or TriggeredBlockedAttackingYourOpponent or BatchHas => HoldsIn(condition, ctx),
            QuantityAtLeast qa when AboutTrigger(qa.Quantity) => HoldsIn(condition, ctx),
            IncrementHolds => source.Zone == Zone.Battlefield && source.IsCreature && info?.SpellManaSpent is { } spent && (spent > source.Power || spent > source.Toughness),
            // "When this enters, if X is 5 or more": the X it was cast with (107.3m).
            QuantityAtLeast { Quantity.Kind: QuantityKind.X } => HoldsIn(condition, ctx with { X = source.CastX }),
            Not n => !HoldsAtTrigger(n.Inner, controller, source, info),
            All a => a.Conditions.All(c => HoldsAtTrigger(c, controller, source, info)),
            _ => Holds(condition, controller, source),
        };
    }

    /// <summary>A player's Ring-bearer, if it is still the same object on the battlefield under their control.</summary>
    private Card? RingBearerOf(PlayerId player) =>
        State.GetPlayer(player).RingBearer is { } bearer && State.GetCard(bearer.Card) is { Zone: Zone.Battlefield } card && card.Version == bearer.Version
        && card.Controller == player ? card : null;

    private bool Holds(Condition condition, PlayerId controller, Card? source)
    {
        var player = State.GetPlayer(controller);
        return condition switch
        {
            AttackedThisTurn => player.AttackedThisTurn,
            CreatureDiedThisTurn { Filter: { } died } => State.CreatureDeathsThisTurn.Any(id => Matches(died with { Controller = ControllerFilter.Any }, State.GetCard(id), State.GetCard(id).Controller, source, controller, lastKnown: true)),
            CreatureDiedThisTurn => State.CreaturesDiedThisTurn > 0,
            GainedLifeThisTurn g => player.LifeGainedThisTurn >= g.AtLeast,
            CardsInGraveyard g => player.Graveyard.Count(id => g.Filter is null
                || Matches(g.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), controller, source, controller)) >= g.AtLeast,
            AttackingCreatures a => (State.Combat?.Attacks.Count(x => State.GetCard(x.Attacker).Controller == controller) ?? 0) >= a.AtLeast,
            YouControl y => State.Battlefield.Select(State.GetCard).Count(c => Matches(y.Filter, c, c.Controller, source, controller)) >= y.AtLeast,
            SourceEnteredThisTurn => source is { Zone: Zone.Battlefield } && source.ZoneChangedTurn == State.TurnNumber,
            FaceDownActivityThisTurn => State.GetPlayer(controller).FaceDownEnteredTurn == State.TurnNumber || State.GetPlayer(controller).TurnedFaceUpTurn == State.TurnNumber,
            SourcePowerParity sp => source is not null && (Math.Abs(source.Power) % 2 == 1) == sp.Odd,
            Coven => State.PermanentsControlledBy(controller).Where(c => c.IsCreature).Select(c => c.Power).Distinct().Count() >= 3,
            PrimeLandCount => IsPrime(State.PermanentsControlledBy(controller).Count(c => c.Is(CardType.Land))),
            DefendingPlayerControls dpc => source is not null && State.Combat?.FindAttack(source.Id) is { } attacked
                && State.PermanentsControlledBy(attacked.Defender).Any(c => Matches(dpc.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, source, controller)),
            LifeAtLeast l => player.Life >= l.Amount,
            Not n => !Holds(n.Inner, controller, source),
            WasKicked => source?.Kicked == true,
            AdditionalCostWasPaid => source?.AdditionalCostPaid == true,
            WasEvoked => source?.Evoked == true,
            WasForetold => source?.CastForetold == true,
            ClassLevelIs cl => source?.Zone == Zone.Battlefield && source.ClassLevel == cl.Level,
            ClassLevelAtLeast cla => source?.Zone == Zone.Battlefield && source.ClassLevel >= cla.Level,
            TopOfLibrary top => player.Library.Count > 0
                                && Matches(top.Filter with { Controller = ControllerFilter.Any }, State.GetCard(player.Library[0]), controller, source, controller),
            OpponentLostLifeThisTurn => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).LifeLostThisTurn > 0),
            YourTurn => State.ActivePlayer == controller,
            YouAttackedWithCommanderThisTurn => player.AttackedWithCommanderThisTurn,
            // A source that left the battlefield is judged as it last existed there (rules 603.4, 608.2h).
            SourceUntapped => source is not null && SourceUntappedNow(source),
            HasEnduringStory => player.HasEnduringStory,
            HasCitysBlessing => player.HasCitysBlessing,
            OpponentHasMostLife => State.OpponentsOf(controller).Select(o => State.GetPlayer(o).Life).DefaultIfEmpty(int.MinValue).Max() >= player.Life,
            // "Attacks alone": the only creature declared as an attacker, whoever controls the ability (rule 506.5).
            AttackingCreaturesExactly ae => (State.Combat?.Attacks.Count ?? 0) == ae.Count,
            AttackedWithAtLeast { Filter: null } aw => player.AttackersThisTurn >= aw.Count,
            AttackedWithAtLeast aw => State.AttackersThisTurn.Where(a => a.Turn == State.TurnNumber).Select(a => a.Card).Distinct()
                .Count(id => State.GetCard(id) is var c && c.Controller == controller && Matches(aw.Filter! with { Controller = ControllerFilter.Any }, c, controller, source, controller)) >= aw.Count,
            SourceAndOthersAttackedThisCombat sa => SourceAndOthersAttacked(source, sa.Others),
            QuantityAtLeast qa when source is not null && !DependsOnTargets(qa.Quantity) => Eval(qa.Quantity, new EffectContext(controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>())) >= qa.AtLeast,
            SourceHasCounters c => source is not null && source.CounterCount(c.Kind) >= c.AtLeast,
            CastXAtLeast cx => source is not null && source.CastX >= cx.AtLeast,
            ModifiedCreatureDiedThisTurn => player.ModifiedCreatureDiedThisTurn,
            AnyPlayerHandAtMost hand => State.LivingPlayers.Any(p => p.Hand.Count <= hand.Count),
            CardsExiledThisTurn ce => State.CardsPutIntoExileThisTurn >= ce.AtLeast,
            EchoDue => source is { Zone: Zone.Battlefield } && source.Controller == controller && source.ControlStamp > player.PreviousUpkeepStamp,
            SourceHasCountersOfAnyKind any => source is not null && source.Counters.Values.Sum() >= any.AtLeast,
            SourceAttacking => source is not null && State.Combat?.FindAttack(source.Id) is not null,
            CreatureAttackingYou => State.Combat?.Attacks.Any(a => a.Defender == controller && a.Planeswalker is null) == true,
            LifeAboveStarting l => player.Life >= Config.StartingLife + l.AtLeast,
            All a => a.Conditions.All(c => Holds(c, controller, source)),
            TotalPowerAtLeast t => State.PermanentsControlledBy(controller).Where(c => c.IsCreature).Sum(c => c.Power) >= t.Amount,
            MoreVotes or ReceivedNoVotes => true, // checked where known (effects)
            TargetMatches or TargetLifeExactly or XAtLeast or YouSacrificedThisWay or CastThisWay or CreatedThisWay or TriggeredWasAttacking or TriggeredHasCounters or TargetAttachedTo or QuantityAtLeast or TargetControlledByYou or TriggeredMatches or SacrificedMatches => true, // checked where known (effects)
            Abilities.IsRingBearer => source is not null && RingBearerOf(controller)?.Id == source.Id,
            TriggeredPlayerAttackedYou => true, // checked where known (effects)
            EquippedInCombatWith eq => source?.AttachedTo is { } holderId && State.Combat is { } fight && (
                (fight.FindAttack(holderId) is { } attack && attack.Blockers.Select(State.GetCard).Any(b => Matches(eq.Filter with { Controller = ControllerFilter.Any }, b, b.Controller, source, controller)))
                || fight.Attacks.Where(a => a.Blockers.Contains(holderId)).Select(a => State.GetCard(a.Attacker)).Any(a => Matches(eq.Filter with { Controller = ControllerFilter.Any }, a, a.Controller, source, controller))),
            HasRingBearer => RingBearerOf(controller) is not null,
            YourCreaturesDied d => player.CreaturesDiedThisTurn >= d.AtLeast,
            OpponentsCreaturesDied od => State.OpponentsOf(controller).Sum(o => State.GetPlayer(o).CreaturesDiedThisTurn) >= od.AtLeast,
            LifeAtMostHalfStarting => player.Life * 2 <= Config.StartingLife,
            FirstCombatPhase => State.CombatsThisTurn == 1,
            DuringCombat => State.Step is Step.BeginCombat or Step.DeclareAttackers or Step.DeclareBlockers or Step.CombatDamage or Step.EndCombat,
            AddedManaWithThisThisTurn => source is not null && source.DoneThisTurn.Any(e => e is AddManaOfOneColor),
            YourTurnNumberAtMost yt => State.ActivePlayer == controller && player.TurnsTaken <= yt.Turns,
            PlayerDealtCombatDamage pdc => State.Players.Any(p => p.CombatDamageTakenThisTurn >= pdc.AtLeast),
            OpponentDealtCombatDamageByLegendary => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).CombatDamagedByLegendaryThisTurn),
            SacrificedThisTurn st => player.SacrificedThisTurn.Select(State.GetCard)
                .Count(c => Matches(st.Filter with { Controller = ControllerFilter.Any }, c, controller, source, controller, lastKnown: true)) >= st.AtLeast,
            YourPermanentLeftThisTurn => player.PermanentLeftThisTurn,
            SourceAttackedThisTurn => source is not null && source.AttacksThisTurn > 0,
            SourceAttackedOrBlockedThisTurn => source is not null && (source.AttacksThisTurn > 0
                || State.BlocksThisTurn.Any(b => b.Blocker == source.Id && b.BlockerVersion == source.Version)),
            YouControlGreatestPower => State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature).ToList() is { Count: > 0 } all
                                       && all.Where(c => c.Controller == controller).Select(c => c.Power).DefaultIfEmpty(int.MinValue).Max() >= all.Max(c => c.Power),
            WasCastFromGraveyard => source?.CastFromGraveyard == true,
            WasCastFromExile => source?.CastFromExile == true,
            CardsDrawnThisTurn d => player.CardsDrawnThisTurn >= d.AtLeast,
            AttackingPowerAtLeast ap => State.Combat?.Attacks.Select(a => State.GetCard(a.Attacker)).Where(c => c.Controller == controller).Sum(c => c.Power) >= ap.Amount,
            GiftPromised => source?.GiftPromised == true,
            SourceIs si => source is not null && Matches(si.Filter with { Controller = ControllerFilter.Any }, source, source.Controller, source, controller),
            SourceWasSubtype w => source?.LastKnownInfo?.Subtypes.Contains(w.Subtype, StringComparer.OrdinalIgnoreCase) == true || source?.HasSubtype(w.Subtype) == true && source.Zone == Zone.Battlefield,
            WasCastFromHand => source?.CastFromHand == true,
            WasCast => source?.WasCast == true,
            SourceHadCounters { Kind: { } hk } => (source?.LastKnownInfo?.Counters.GetValueOrDefault(hk) ?? 0) > 0,
            SourceHadCounters => source?.LastKnownInfo?.Counters.Values.Any(n => n > 0) == true,
            DifferentNames d => State.PermanentsControlledBy(controller).Where(c => Matches(d.Filter, c, c.Controller, source, controller)).Select(c => c.Name).Distinct().Count() >= d.AtLeast,
            IsMonarch => State.Monarch == controller,
            ChoseOption co => source?.ChosenOption == co.Option,
            DuringStep ds => State.Step == ds.Step,
            YourMainPhase => State.ActivePlayer == controller && State.Step.IsMain(),
            YourEndStep => State.ActivePlayer == controller && State.Step == Step.End,
            ManaSpentToCast ms => source is not null && source.SpentManaTypes.Count(t => t == ms.Type) >= ms.Count,
            CreatureCardExiledWithSource => source is not null && LastCreatureExiledWith(source) is not null,
            CardsExiledWithSource => source is not null && State.Cards.Values.Any(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == source.Id && w.Version == source.Version),
            OpponentsControlTotal ot => State.OpponentsOf(controller).Sum(o => State.PermanentsControlledBy(o)
                .Count(c => Matches(ot.Filter with { Controller = ControllerFilter.Any }, c, o, source, controller))) >= ot.AtLeast,
            OpponentControls oc => State.OpponentsOf(controller).Any(o => State.PermanentsControlledBy(o)
                .Count(c => Matches(oc.Filter with { Controller = ControllerFilter.Any }, c, o, source, controller)) >= oc.AtLeast),
            OpponentDealtDamageThisTurn od => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).DamageTakenThisTurn >= od.AtLeast),
            YouControlGreatestManaValue gm => State.Battlefield.Select(State.GetCard).Where(c => Matches(gm.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, source, controller)).ToList() is { Count: > 0 } among
                && among.Where(c => c.Controller == controller).Select(ManaValueOf).DefaultIfEmpty(int.MinValue).Max() >= among.Max(ManaValueOf),
            YouControlSameName sn => State.PermanentsControlledBy(controller).Where(c => Matches(sn.Filter, c, c.Controller, source, controller))
                .GroupBy(c => c.Name).Any(g => g.Count() >= sn.AtLeast),
            AttachedControllerIsMonarch => source?.AttachedTo is { } host && State.Monarch == State.GetCard(host).Controller,
            SourceHasAnyCounters => source is not null && source.Counters.Values.Any(v => v > 0),
            TriggeredHadCounters => true, // checked where known
            CardTypesInGraveyard ct => new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred, CardType.Land, CardType.Planeswalker, CardType.Sorcery }
                .Count(t => player.Graveyard.Any(id => State.GetCard(id).Definition.Is(t))) >= ct.AtLeast,
            OpponentHasMore om => State.OpponentsOf(controller).Any(o => OpponentMeasure(om.What, o) > OpponentMeasure(om.What, controller)),
            CastDuringYourMainPhase => source?.CastDuringMainPhase == true,
            SourceRenowned => source is not null && SourceRenownedNow(source),
            SourceSolved => source is { Zone: Zone.Battlefield, Solved: true },
            SourcePrepared => source is { Zone: Zone.Battlefield, PreparedCopy: not null },
            SpellsCastLastTurn sl => AnyPlayerCastLastTurn(sl.AtLeast),
            SourceDealtDamageThisTurn dd => source is not null && DamageDealtThisTurnBy(source.Id, SourceObjectVersion(source, null)) >= dd.AtLeast,
            SourceTransformed => source is { Zone: Zone.Battlefield, Transformed: true, IsDoubleFaced: true },
            SourceHasDealtDamage => source?.HasDealtDamage == true,
            SourceHasDealtCombatDamage => source?.HasDealtCombatDamage == true,
            ScriedOrSurveilledThisTurn => player.ScriedOrSurveilledThisTurn,
            ActivatedLoyaltyAbilityThisTurn => player.ActivatedLoyaltyThisTurn,
            LibraryEmpty => player.Library.Count == 0,
            CreatedTokenThisTurn => player.CreatedTokenThisTurn,
            EnchantedPlayersTurn => source?.AttachedPlayer == State.ActivePlayer,
            OpponentDealtNoncombatDamage od => State.OpponentsOf(controller).Select(State.GetPlayer)
                .Any(o => od.LastTurn ? o.NoncombatDamageTakenLastTurn > 0 : o.DamageTakenThisTurn > o.CombatDamageTakenThisTurn),
            SourceFrontFaceUp => source is { Zone: Zone.Battlefield, Transformed: false, IsDoubleFaced: true },
            ExertedThisTurn => source is not null && source.ExertedTurn == State.TurnNumber,
            TriggeredPlayerHasMostLife or TriggeredPlayerIsYou or DefenderHasMoreLifeThanAnotherOpponent or TriggeredAbilityCostHasX or FoundHas or BatchHas or CastThisWay => true, // checked where known
            EnteredThisTurn et => player.EnteredThisTurn.Count(e => State.GetCard(e.Card) is var c
                && (c.Version == e.Version && c.Zone == Zone.Battlefield
                    ? Matches(et.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, source, controller)
                    : c.LastKnownInfo is { } lki && lki.Version == e.Version && Matches(et.Filter with { Controller = ControllerFilter.Any }, c, lki.Controller, source, controller, lastKnown: true))) >= et.AtLeast,
            OpponentsAtLeast oa => State.OpponentsOf(controller).Count() >= oa.Count,
            OpponentCastSpellThisTurn oc => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).SpellsCastThisTurn.Select(State.GetCard)
                .Any(c => Matches(oc.Filter with { Controller = ControllerFilter.Any }, c, o, source, controller, lastKnown: c.Zone != Zone.Stack))),
            OpponentLifeAtMost ol => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).Life <= ol.Life),
            APlayerLifeAtMost al => State.Players.Any(p => !p.HasLost && p.Life <= al.Life),
            NoMonarch => State.Monarch is null,
            ResolvedThisTurn r => source is not null && (r.Exactly ? source.ResolvedThisTurn.Values.DefaultIfEmpty(0).Max() == r.Times : source.ResolvedThisTurn.Values.DefaultIfEmpty(0).Max() >= r.Times),
            FullParty => PartySize(controller) == 4,
            CreatureBlocking => State.Combat is { } blockingCombat && blockingCombat.Attacks.Any(a => a.Blockers.Count > 0),
            SourceAttachedToCreature => source?.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield, IsCreature: true },
            OpponentGraveyardAtLeast og => State.OpponentsOf(controller).Any(o => State.GetPlayer(o).Graveyard.Count >= og.Count),
            TurnHappened th => source is not null && TurnHappenedHolds(th, new EffectContext(controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>())),
            _ => throw new NotSupportedException($"Condition {condition.GetType().Name} is not implemented."),
        };
    }

    /// <summary>Whether two objects share a creature type (as the second last existed, if it left the battlefield).</summary>
    private static bool SharesCreatureType(Card a, Card b)
    {
        var bTypes = b.Zone != Zone.Battlefield && b.LastKnownInfo is { } lk ? lk.Subtypes : b.CurrentSubtypes;
        bool bChangeling = b.Zone != Zone.Battlefield && b.LastKnownInfo is { } lk2 ? lk2.Keywords.Contains(Keyword.Changeling) : b.Has(Keyword.Changeling);
        var aTypes = a.Zone == Zone.Battlefield ? a.CurrentSubtypes : a.Definition.Subtypes;
        bool aChangeling = a.Zone == Zone.Battlefield ? a.Has(Keyword.Changeling) : a.Definition.KeywordAbilities.Contains(Keyword.Changeling);
        if (aChangeling && bChangeling) return true; // both have every creature type
        if (aChangeling && bTypes.Any(Card.IsCreatureType) || bChangeling && aTypes.Any(Card.IsCreatureType)) return true;
        return aTypes.Where(Card.IsCreatureType).Any(t => bTypes.Contains(t, StringComparer.OrdinalIgnoreCase));
    }
}
