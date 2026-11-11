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
    /// <summary>Works out a quantity as the effect happens.</summary>
    private int Eval(Quantity q, EffectContext ctx)
    {
        Card? TargetCard() => ctx.ChosenAt(q.Index)?.Card is { } id ? State.GetCard(id) : null;
        // A target that left the battlefield during resolution ("its toughness" after it was moved) is used as it
        // last existed there (rule 608.2h).
        LastKnown? TargetLastKnown() => TargetCard() is { } t && ctx.ChosenVersionAt(q.Index) is { } v && v != t.Version
                                        && t.Zone != Zone.Battlefield ? t.LastKnownOf(v) : null;
        int value = q.Kind switch
        {
            QuantityKind.Fixed => q.Value,
            QuantityKind.X => ctx.X,
            QuantityKind.PermanentCount when q.ControlledByTriggeredPlayer => ctx.Trigger?.Player is { } who
                ? State.PermanentsControlledBy(who).Count(c => Matches((q.Filter ?? ObjectFilter.YourCreatures) with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))
                : 0,
            QuantityKind.PermanentCount when q.ControlledByAttachedController => ctx.Source.AttachedTo is { } host
                ? State.PermanentsControlledBy(State.GetCard(host).Controller).Count(c => Matches((q.Filter ?? ObjectFilter.YourCreatures) with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))
                : 0,
            QuantityKind.PermanentCount when q.ControlledByTarget => ctx.TargetAt(q.Index)?.Player is { } owner
                ? State.PermanentsControlledBy(owner).Count(c => Matches((q.Filter ?? ObjectFilter.YourCreatures) with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))
                : 0,
            QuantityKind.YourUnlockedDoors => UnlockedDoorsOf(ctx.Controller).Doors,
            QuantityKind.YourUnlockedDoorNames => UnlockedDoorsOf(ctx.Controller).Names,
            QuantityKind.ColorsSpentToCast => ctx.Source.SpentManaTypes.Where(t => t is Mana.ManaType.White or Mana.ManaType.Blue or Mana.ManaType.Black or Mana.ManaType.Red or Mana.ManaType.Green).Distinct().Count(),
            QuantityKind.ChosenPlayerExtraCardsInHand => ctx.Results.ChosenPlayer is { } chosenOne
                ? Math.Max(0, State.GetPlayer(chosenOne).Hand.Count - State.GetPlayer(ctx.Controller).Hand.Count) : 0,
            QuantityKind.GreatestPowerInYourGraveyard => State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard)
                .Where(c => Matches(q.Filter! with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)).Select(c => c.Definition.Power ?? 0).DefaultIfEmpty(0).Max(),
            QuantityKind.GreatestPowerAmong => State.Battlefield.Select(State.GetCard)
                .Where(c => Matches(q.Filter!, c, c.Controller, ctx.Source, ctx.Controller)).Select(c => c.Power).DefaultIfEmpty(0).Max(),
            QuantityKind.PermanentCount => State.Battlefield.Select(State.GetCard)
                .Count(c => Matches(q.Filter ?? ObjectFilter.YourCreatures, c, c.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.DiscardedThisWay => ctx.Results.Discarded.Count(id => q.Filter is null
                || Matches(q.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), State.GetCard(id).Owner, ctx.Source, ctx.Controller)),
            QuantityKind.GraveyardsWithAtLeast => State.LivingPlayers.Count(p => p.Graveyard.Count >= q.Value),
            QuantityKind.ManaSpent => ctx.Trigger?.SpellManaSpent ?? (ctx.Trigger?.Subject is { } spent ? State.GetCard(spent).ManaSpent : 0),
            QuantityKind.ReturnedThisWay => ctx.Results.Returned.Count,
            QuantityKind.AttachedPower => ctx.Source.Zone == Zone.Battlefield && ctx.Source.AttachedTo is { } host ? State.GetCard(host).Power : 0,
            QuantityKind.GreatestPower => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature).Select(c => c.Power).DefaultIfEmpty(0).Max(),
            QuantityKind.AttackersOfAffectedPlayer => State.Combat?.Attacks.Count(a => a.Defender == (ctx.AffectedPlayer ?? ctx.Controller) && a.Planeswalker is null) ?? 0,
            QuantityKind.TriggeredSpellTargets => ctx.Trigger?.Subject is { } aimingSpell
                ? State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == aimingSpell)?.Targets.Count(t => !t.Target.IsNone) ?? 0 : 0,
            QuantityKind.TriggeredSpellPermanentOrPlayerTargets => ctx.Trigger?.Subject is { } aimer
                ? State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == aimer)?.Targets
                    .Count(t => t.Target.Player is not null || (t.Target.Card is { } tc && State.GetCard(tc).Zone == Zone.Battlefield)) ?? 0 : 0,
            QuantityKind.GreatestOtherToughness => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Id != ctx.Source.Id).Select(c => c.Toughness).DefaultIfEmpty(0).Max(),
            QuantityKind.ExperienceCounters => State.GetPlayer(ctx.Controller).Experience,
            QuantityKind.DestroyedToughness => ctx.Results.DestroyedCards.Sum(id => State.GetCard(id).LastKnownInfo?.Toughness ?? 0),
            QuantityKind.TotalToughness => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature).Sum(c => c.Toughness),
            QuantityKind.LifeLostThisTurn => State.GetPlayer(ctx.Controller).LifeLostThisTurn,
            QuantityKind.CountersOnSource => ctx.Source.Counters.Values.Sum(),
            QuantityKind.TokensCreatedThisTurn => State.GetPlayer(ctx.Controller).TokensCreatedThisTurn,
            QuantityKind.NontokenCreaturesDiedThisTurn => State.NontokenCreaturesDiedThisTurn,
            QuantityKind.CommanderCasts => State.GetPlayer(ctx.Controller).CommanderCasts.Values.Sum(),
            QuantityKind.TriggeredPlayerAttackersAtYou => State.Combat is { } atYou && ctx.Trigger?.Player is { } attackingPlayer
                ? atYou.Attacks.Count(x => x.Defender == ctx.Controller && State.GetCard(x.Attacker).Controller == attackingPlayer) : 0,
            QuantityKind.YourAurasOnTriggeredAtDeath => ctx.Trigger is { Subject: { } deadOne } deadInfo
                && State.AurasAtDeath.TryGetValue((deadOne, deadInfo.SubjectVersion), out var auraHolders) ? auraHolders.Count(h => h == ctx.Controller) : 0,
            QuantityKind.TriggeredCommanderCasts => ctx.Trigger?.Subject is { } commanderCard ? State.GetPlayer(State.GetCard(commanderCard).Owner).CommanderCasts.GetValueOrDefault(commanderCard) : 0,
            QuantityKind.TargetCommanderCasts => ctx.ChosenAt(0)?.Player is { } castingPlayer ? State.GetPlayer(castingPlayer).CommanderCasts.Values.Sum() : 0,
            QuantityKind.CounterKindsAmongYourCreatures => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature)
                .SelectMany(c => c.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key)).Distinct().Count(),
            QuantityKind.CardsToGraveyardFromHandOrLibraryThisTurn => State.GetPlayer(ctx.Controller).CardsToGraveyardFromHandOrLibraryThisTurn,
            QuantityKind.AffectedHalfLifeRoundedUp => (Math.Max(0, State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).Life) + 1) / 2,
            QuantityKind.CardsInAllGraveyards => State.Players.Sum(p => p.Graveyard.Count),
            QuantityKind.GreatestToughness => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature).Select(c => c.Toughness).DefaultIfEmpty(0).Max(),
            QuantityKind.GreatestAmongOpponents => State.OpponentsOf(ctx.Controller).Select(o => State.PermanentsControlledBy(o)
                .Count(c => Matches((q.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))).DefaultIfEmpty(0).Max(),
            QuantityKind.CountersAmong => State.Battlefield.Select(State.GetCard)
                .Where(c => Matches(q.Filter ?? ObjectFilter.Anything, c, c.Controller, ctx.Source, ctx.Controller))
                .Sum(c => q.AnyCounterKind ? c.Counters.Values.Sum() : c.CounterCount(q.Counter)),
            QuantityKind.ManaValueOfOtherSpellsThisTurn => State.GetPlayer(ctx.Controller).SpellsCastThisTurn.Where(id => id != ctx.Source.Id).Sum(id => State.GetCard(id).ManaValue),
            QuantityKind.MilledManaValue => ctx.Results.Milled.Sum(id => State.GetCard(id).ManaValue),
            QuantityKind.TappedThisWay => ctx.Results.Tapped,
            QuantityKind.NonlandRevealedThisWay => ctx.Results.NonlandRevealed,
            QuantityKind.RingLevel => State.GetPlayer(ctx.Controller).RingLevel,
            QuantityKind.AttachedThisWay => ctx.Results.Attached,
            QuantityKind.SourceToughness => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null ? ctx.Source.Toughness : ctx.Source.LastKnownInfo.Toughness,
            QuantityKind.GraveyardCount => (q.OfEveryone ? State.Players.Select(p => p.Id)
                    : q.OfOpponents ? State.OpponentsOf(ctx.Controller)
                    : q.OfSourceController ? PlayersFor(new Subject(SubjectKind.SourceController), ctx)
                    : new[] { q.OfAffectedPlayer && ctx.AffectedPlayer is { } affected ? affected
                        : q.ControlledByTarget && ctx.TargetAt(q.Index)?.Player is { } graveOwner ? graveOwner : ctx.Controller })
                .SelectMany(p => State.GetPlayer(p).Graveyard).Select(State.GetCard)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.SourcePower => ctx.Source.Zone == Zone.Battlefield || ctx.Source.LastKnownInfo is null ? ctx.Source.Power : ctx.Source.LastKnownInfo.Power,
            QuantityKind.TargetPower => TargetLastKnown()?.Power ?? TargetCard()?.Power ?? 0,
            QuantityKind.TargetToughness => TargetLastKnown()?.Toughness ?? TargetCard()?.Toughness ?? 0,
            // As the chosen object last existed (rule 608.2h): a spell that left the stack keeps its X.
            QuantityKind.TargetManaValue => TargetCard() is { } tmv
                ? ctx.ChosenVersionAt(q.Index) is { } chosenVersion && chosenVersion != tmv.Version
                    ? TargetLastKnown()?.ManaValue ?? (tmv.LastOnStack is { } stack && stack.Version == chosenVersion ? stack.ManaValue : ManaValueOf(tmv))
                    : ManaValueOf(tmv)
                : 0,
            QuantityKind.LifeGainedThisTurn => State.GetPlayer(ctx.Controller).LifeGainedThisTurn,
            QuantityKind.YourLife => State.GetPlayer(ctx.Controller).Life,
            QuantityKind.HandSize => State.GetPlayer(ctx.Controller).Hand.Count,
            QuantityKind.DefendingPlayerHandSize => PlayersFor(new Subject(SubjectKind.DefendingPlayer), ctx).Select(p => State.GetPlayer(p).Hand.Count).FirstOrDefault(),
            QuantityKind.VotesFor => VotesFor(ctx, q.Value),
            QuantityKind.TimesKicked => ctx.Source.TimesKicked,
            QuantityKind.AttackingPower => State.Combat?.Attacks.Select(a => State.GetCard(a.Attacker))
                .Where(c => c.Controller == ctx.Controller && (q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller))).Sum(c => c.Power) ?? 0,
            QuantityKind.OpponentCount => State.OpponentsOf(ctx.Controller).Count(),
            QuantityKind.CardsInAllHands => State.LivingPlayers.Sum(p => p.Hand.Count),
            QuantityKind.OtherCreaturesSharingTypeWithAffected => ctx.AffectedCard is { } affectedCard
                ? State.Battlefield.Select(State.GetCard).Count(c => c.IsCreature && c.Id != affectedCard.Id && SharesCreatureType(c, affectedCard)) : 0,
            QuantityKind.HalfLibrary => State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).Library.Count / 2,
            QuantityKind.DamageTakenThisTurn => ctx.ChosenAt(q.Index)?.Player is { } hurt ? State.GetPlayer(hurt).DamageTakenThisTurn : 0,
            QuantityKind.TargetPlayersGreatestPower => ctx.TargetAt(q.Index)?.Player is { } owner
                ? State.PermanentsControlledBy(owner).Where(c => c.IsCreature).Select(c => c.Power).DefaultIfEmpty(0).Max() : 0,
            QuantityKind.LastExiledCreaturePower => LastCreatureExiledWith(ctx.Source)?.Power ?? 0,
            QuantityKind.LastExiledCreatureToughness => LastCreatureExiledWith(ctx.Source)?.Toughness ?? 0,
            QuantityKind.DiscoverShortfall => ctx.Results.Discovered is { } discovered ? Math.Max(0, ctx.Results.DiscoverTarget - discovered.ManaValue) : 0,
            QuantityKind.TriggeredPlayerAttackers => ctx.Trigger?.Player is { } attackingPlayer && State.Combat is { } attackCombat
                ? attackCombat.Attacks.Count(a => State.GetCard(a.Attacker).Controller == attackingPlayer) : 0,
            QuantityKind.OpponentsAttackedThisCombat => State.Combat is { } meleeCombat
                ? meleeCombat.PlayersAttacked.Count(p => p.Attacker == ctx.Controller && p.Defender != ctx.Controller) : 0,
            QuantityKind.GreatestCommanderManaValueAnywhere => State.Cards.Values.Where(c => c.IsCommander && c.Owner == ctx.Controller)
                .Select(c => c.ManaValue).DefaultIfEmpty(0).Max(),
            QuantityKind.GreatestCommanderManaValue => State.Cards.Values.Where(c => c.IsCommander && c.Owner == ctx.Controller && c.Zone is Zone.Battlefield or Zone.Command)
                .Select(c => c.ManaValue).DefaultIfEmpty(0).Max(),
            QuantityKind.OtherAttackersSharingTypeWithTriggered => ctx.Trigger?.Subject is { } attackerId && State.GetCard(attackerId) is var trig && State.Combat is { } fight
                ? fight.Attacks.Select(a => State.GetCard(a.Attacker)).Count(o => o.Id != trig.Id && o.CurrentSubtypes.Where(Card.IsCreatureType).Any(trig.HasSubtype)
                    || (o.Id != trig.Id && (o.Has(Keyword.Changeling) && trig.CurrentSubtypes.Any(Card.IsCreatureType) || trig.Has(Keyword.Changeling) && o.CurrentSubtypes.Any(Card.IsCreatureType))))
                : 0,
            QuantityKind.SquadPaid => ctx.Source.SquadPaid,
            QuantityKind.OffspringPaid => (ResolvingSourceGone(ctx.Source) is { } offspringGone ? offspringGone.OffspringPaid : ctx.Source.OffspringPaid) ? 1 : 0,
            QuantityKind.VotesReceived => VotesIn(ctx).Count(v => v.Choice == $"p{(ctx.AffectedPlayer ?? ctx.Controller).Value}"),
            QuantityKind.OpponentsVotedOtherwise => OpponentsByVote(ctx, agreeing: false).Count(),
            QuantityKind.AffectedHandSize => State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).Hand.Count,
            QuantityKind.OpponentsCreaturesExiledThisTurn => State.OpponentsOf(ctx.Controller).Sum(o => State.GetPlayer(o).CreaturesExiledThisTurn),
            QuantityKind.YourCreaturesDiedThisTurn => State.GetPlayer(ctx.Controller).CreaturesDiedThisTurn,
            QuantityKind.CreaturesAttackedThisTurn => State.AttackersThisTurn.Count(a => a.Turn == State.TurnNumber),
            QuantityKind.OwnedInExile => State.Cards.Values.Count(c => c.Zone == Zone.Exile && c.Owner == ctx.Controller
                && (q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller))),
            QuantityKind.CardTypesExiledWithSource => Enum.GetValues<CardType>().Count(t => State.Cards.Values.Any(c => c.Zone == Zone.Exile
                && c.ExiledWith is { } w && w.Source == ctx.Source.Id && w.Version == ctx.Source.Version && (c.Definition.Types & t) != 0)),
            QuantityKind.DifferentlyNamedTokens => State.PermanentsControlledBy(ctx.Controller).Where(c => c.Definition.IsToken).Select(c => c.Name).Distinct(StringComparer.Ordinal).Count(),
            QuantityKind.CardsExiledWithSource => State.Cards.Values.Count(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id && w.Version == ctx.Source.Version),
            QuantityKind.TriggeredPlayerHandSize => ctx.Trigger?.Player is { } handOf ? State.GetPlayer(handOf).Hand.Count : 0,
            QuantityKind.OpponentsWithHandAtMost => State.OpponentsOf(ctx.Controller).Count(o => State.GetPlayer(o).Hand.Count <= q.Value),
            QuantityKind.GreatestOpponentPoison => State.OpponentsOf(ctx.Controller).Select(o => State.GetPlayer(o).Poison).DefaultIfEmpty(0).Max(),
            QuantityKind.AffectedLifeLostThisTurn => State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).LifeLostThisTurn,
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
                .Select(c => c.ManaValue).Distinct().Count(),
            QuantityKind.FoundThisWay => ctx.Results.Found.Count,
            QuantityKind.GreatestManaValue => State.PermanentsControlledBy(ctx.Controller)
                .Where(c => q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller))
                .Select(c => c.ManaValue).DefaultIfEmpty(0).Max(),
            QuantityKind.SpellsCastThisTurn => State.GetPlayer(ctx.Controller).SpellsCastThisTurn.Select(State.GetCard)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.GreatestManaValueSpellsCast => State.GetPlayer(ctx.Controller) is var caster
                ? caster.SpellsCastThisTurn.Select((id, i) => (Card: State.GetCard(id), Value: caster.SpellManaValuesThisTurn[i]))
                    .Where(s => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, s.Card, ctx.Controller, ctx.Source, ctx.Controller))
                    .Select(s => s.Value).DefaultIfEmpty(0).Max() : 0,
            QuantityKind.SpellsCastBeforeTriggered => SpellsCastBefore(ctx)
                .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)),
            QuantityKind.TriggeredColors => ctx.Trigger?.Subject is { } colored ? ColorsOf(State.GetCard(colored)).Count : 0,
            QuantityKind.SourceCounters => SourceCounterCount(ctx, q.Counter),
            QuantityKind.SourceCountersAll => SourceCounterCount(ctx, null),
            QuantityKind.TargetsOnBattlefield => CardsFor(new Subject(SubjectKind.EachTarget), ctx).Count(c => c.Zone == Zone.Battlefield),
            QuantityKind.DestroyedManaValue => ctx.Results.DestroyedCards.Select(State.GetCard).Sum(c => c.LastKnownInfo?.ManaValue ?? c.ManaValue),
            QuantityKind.CardTypesAmongBatch => new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred, CardType.Land, CardType.Planeswalker, CardType.Sorcery }
                .Count(t => (ctx.Trigger?.Batch ?? new List<(CardId, int)>()).Select(b => State.GetCard(b.Card)).Concat(ctx.Trigger?.Subject is { } one && ctx.Trigger.Batch is null ? new[] { State.GetCard(one) } : Array.Empty<Card>())
                    .Any(c => c.Definition.Is(t))),
            QuantityKind.OpponentsLifeLostThisTurn => State.OpponentsOf(ctx.Controller).Sum(o => State.GetPlayer(o).LifeLostThisTurn),
            QuantityKind.SourceSpellX => State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == ctx.Source.Id)?.X ?? 0,
            QuantityKind.BeheldPower => ctx.Source.Beheld.Count > 0 ? BeheldPower(State.GetCard(ctx.Source.Beheld[0].Card), ctx.Source.Beheld[0].Version) : 0,
            QuantityKind.OpponentsGraveyardCount => State.OpponentsOf(ctx.Controller).Sum(o => State.GetPlayer(o).Graveyard.Count),
            QuantityKind.GreatestOtherPower => State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Id != ctx.Source.Id)
                .Select(c => c.Power).DefaultIfEmpty(0).Max(),
            QuantityKind.TriggeredPower => ctx.Trigger is { Subject: { } t } about ? TriggeredPowerOf(State.GetCard(t), about.SubjectVersion) : 0,
            QuantityKind.TriggeredPowerToughnessDifference => Math.Abs(Eval(new Quantity(0, QuantityKind.TriggeredPower), ctx) - Eval(new Quantity(0, QuantityKind.TriggeredToughness), ctx)),
            QuantityKind.ChosenPowerDifference => ctx.Results.Chosen.Count == 2 ? Math.Abs(State.GetCard(ctx.Results.Chosen[0]).Power - State.GetCard(ctx.Results.Chosen[1]).Power) : 0,
            QuantityKind.TriggeredToughness => ctx.Trigger is { Subject: { } tt } aboutT ? TriggeredToughnessOf(State.GetCard(tt), aboutT.SubjectVersion) : 0,
            QuantityKind.RingBearerPower => RingBearerOf(ctx.Controller) is { } bearer ? bearer.Power : 0,
            QuantityKind.Sum => q.Parts?.Sum(part => Eval(part, ctx)) ?? 0,
            QuantityKind.TargetLoyalty or QuantityKind.DistinctPowersAmongYourCreatures or QuantityKind.GreatestManaValueInYourGraveyard
                or QuantityKind.TargetPlayerMilledThisTurn or QuantityKind.DrawnThisWay or QuantityKind.Domain or QuantityKind.DiscardedThisTurn
                or QuantityKind.PlaneswalkerTypesAmongYours or QuantityKind.OpponentsWhoDiscardedFewer or QuantityKind.ColorsAmongYourMatching
                or QuantityKind.UnspentMana => EvalFracture(q, ctx),
            QuantityKind.CardsYouDrewThisTurn or QuantityKind.TriggeredSpellX or QuantityKind.ManaSpentOnSource or QuantityKind.ManaSpentOnTarget
                or QuantityKind.TwoToThe or QuantityKind.AffectedHalfLifeRoundedDown or QuantityKind.AffectedHalfHandRoundedDown
                or QuantityKind.AffectedHalfPermanentsRoundedDown or QuantityKind.TriggeredSpellColorsSpent or QuantityKind.TargetHandSize
                or QuantityKind.DifferentlyNamedYouControl or QuantityKind.PermanentsPutIntoGraveyardThisTurn
                or QuantityKind.YourNontokenCreaturesDestroyedThisWay => EvalAcademy(q, ctx),
            QuantityKind.Difference or QuantityKind.Per or QuantityKind.CardTypesInAllGraveyards or QuantityKind.CardTypesInYourGraveyard or QuantityKind.ColorsAmongYourPermanents or QuantityKind.CounterKindsOnSource or QuantityKind.ExiledManaValue
                or QuantityKind.PlayersWhoLostLifeThisTurn or QuantityKind.OpponentsWhoLostLifeThisTurn or QuantityKind.OpponentsCombatDamagedBySourceOrDragon or QuantityKind.GreatestPowerAsCast or QuantityKind.TriggeredCounterCount or QuantityKind.SacrificedManaValue
                or QuantityKind.EnteredThisTurnCount or QuantityKind.EnteredThisTurnEver or QuantityKind.OpponentsControlling or QuantityKind.TriggeredPlusOneCounters
                => EvalHeroesCommander(q, ctx),
            QuantityKind.HalfRoundedUp => (Math.Max(0, Eval(q.Parts![0], ctx)) + 1) / 2, // rule 107.1a
            QuantityKind.HalfRoundedDown => Math.Max(0, Eval(q.Parts![0], ctx)) / 2,
            QuantityKind.Devotion => DevotionTo(ctx.Controller, q.Filter?.Colors ?? Array.Empty<string>()),
            QuantityKind.PlayersWhoDrewThisWay => ctx.Results.PlayersWhoDrew.Count(p => q.Filter?.Controller != ControllerFilter.Opponent || p != ctx.Controller),
            QuantityKind.OpponentsWithMore => State.OpponentsOf(ctx.Controller).Count(o => OpponentMeasure(q.Measure!, o) > OpponentMeasure(q.Measure!, ctx.Controller)),
            QuantityKind.AffectedManaValue or QuantityKind.FoundPower or QuantityKind.FoundToughness => EvalOrigins(q, ctx),
            QuantityKind.ManaValueSum or QuantityKind.ControlGainedThisWay or QuantityKind.FoundManaValue or QuantityKind.DiscardedManaValue or QuantityKind.FoundMatching
                or QuantityKind.CardTypesAmongDiscarded or QuantityKind.CreaturesDiedThisTurn or QuantityKind.CountersRemovedThisWay
                or QuantityKind.SpellsAndAbilitiesCounteredThisWay or QuantityKind.Rolled or QuantityKind.OpponentsDealtCombatDamage => EvalHeroes(q, ctx),
            QuantityKind.PermanentsSacrificedThisTurn => State.PermanentsSacrificedThisTurn,
            QuantityKind.SacrificedThisWay => ctx.Results.Sacrificed.Count,
            QuantityKind.AmassedPower => ctx.Results.Amassed is { } army ? PowerOrLastKnown(State.GetCard(army)) : 0,
            QuantityKind.AttackingCount => State.Combat?.Attacks.Select(a => State.GetCard(a.Attacker))
                .Count(c => q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller)) ?? 0,
            _ => throw new NotSupportedException($"Quantity {q.Kind} is not implemented."),
        };
        return value * q.Multiplier + q.Offset;
    }

    private int OpponentMeasure(string what, PlayerId player) => what switch
    {
        "life" => State.GetPlayer(player).Life,
        "creatures" => State.PermanentsControlledBy(player).Count(c => c.IsCreature),
        "lands" => State.PermanentsControlledBy(player).Count(c => c.Is(CardType.Land)),
        "cards" => State.GetPlayer(player).Hand.Count,
        _ => throw new NotSupportedException($"Unknown comparison '{what}'."),
    };

    /// <summary>Quantities that need a resolving spell's targets or results (checked only while it resolves).</summary>
    /// <summary>A quantity worked out for each object a static ability affects.</summary>
    private static bool DependsOnAffected(Quantity? q) =>
        q is not null && (q.Kind == QuantityKind.OtherCreaturesSharingTypeWithAffected || (q.Parts?.Any(DependsOnAffected) ?? false));

    private static bool DependsOnTargets(Quantity q) => q.Kind is QuantityKind.TargetPower or QuantityKind.TargetToughness or QuantityKind.TargetManaValue
        or QuantityKind.X or QuantityKind.TriggerAmount or QuantityKind.TriggeredPower or QuantityKind.TriggeredToughness or QuantityKind.SacrificedPower or QuantityKind.SacrificedToughness
        or QuantityKind.LifeLostThisWay or QuantityKind.MilledThisWay or QuantityKind.DestroyedThisWay or QuantityKind.ExcessDamage or QuantityKind.ExiledThisWay
        or QuantityKind.DiscardedThisWay or QuantityKind.ReturnedThisWay or QuantityKind.MilledManaValue or QuantityKind.TappedThisWay or QuantityKind.ManaSpent
        || q.ControlledByTarget || q.ControlledByTriggeredPlayer;

    /// <summary>The amount a pending trigger is about, while its targets are chosen and checked ("with equal or lesser mana value than that spell").</summary>
    private int _triggeredAmount;
}
