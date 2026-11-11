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
    /// <summary>Effects handled by the group-1 part of the effect switch; false when the effect belongs to another group (the switch order is kept).</summary>
    private async Task<bool> ApplyGroup1Async(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case ModeEffects m:
                await ApplyAllAsync(m.Effects, ctx with { TargetOffset = m.TargetOffset });
                break;
            case Encore:
                await EncoreAsync(ctx);
                break;
            case Abilities.Endure endure:
                await EndureAsync(endure, ctx);
                break;
            case Abilities.Mobilize mobilize:
                await MobilizeAsync(mobilize, ctx);
                break;
            case Abilities.Fabricate fabricate:
                await FabricateAsync(fabricate, ctx);
                break;
            case SacrificeSelfAtEndOfCombat:
                if (SourceOnBattlefield(ctx)) State.SacrificeAtEndOfCombat.Add((ctx.Source.Id, ctx.Source.Version));
                break;
            case Abilities.Demonstrate:
                await DemonstrateAsync(ctx);
                break;
            case SuspendFromHand sfh:
                SuspendFromHand(sfh, ctx);
                break;
            case ExilesCreatureAndGraveyard ecg:
                await ExilesCreatureAndGraveyardAsync(ecg, ctx);
                break;
            case SuspendTriggeredSpell sts:
                if (ctx.Trigger?.Subject is { } suspendedSpell && State.GetCard(suspendedSpell) is { Zone: Zone.Stack } suspendedCard && suspendedCard.Version == ctx.Trigger.SubjectVersion
                    && !suspendedCard.Definition.IsToken)
                {
                    await MoveCardAsync(suspendedSpell, Zone.Exile);
                    if (suspendedCard.Zone == Zone.Exile)
                    {
                        suspendedCard.Counters[CounterKind.Time] = suspendedCard.CounterCount(CounterKind.Time) + sts.TimeCounters;
                        suspendedCard.Suspended = true; // "if it doesn't have suspend, it gains suspend"
                        Emit(new ChoiceMade(suspendedCard.Id, $"{suspendedCard.Name} was exiled with {sts.TimeCounters} time counters"));
                    }
                }
                break;
            case CounterPerColorWinOnFive cpw:
                await CounterPerColorWinOnFiveAsync(cpw, ctx);
                break;
            case OpponentShufflesOneCastRestFree:
                await OpponentShufflesOneCastRestFreeAsync(ctx);
                break;
            case OpponentChoosesFromYourGraveyard ocy:
                await OpponentChoosesFromYourGraveyardAsync(ocy, ctx);
                break;
            case GetExperienceCounter:
                State.GetPlayer(ctx.Controller).Experience++;
                Emit(new ChoiceMade(ctx.Source.Id, $"{State.GetPlayer(ctx.Controller).Name} gets an experience counter ({State.GetPlayer(ctx.Controller).Experience})"));
                break;
            case ReturnToBattlefieldOrHandByManaValue rbm:
            {
                int max = Eval(rbm.MaxManaValue, ctx);
                foreach (var card in CardsFor(rbm.What, ctx).Where(c => c.Zone == Zone.Graveyard).ToList())
                    await MoveCardAsync(card.Id, ManaValueOf(card) <= max ? Zone.Battlefield : Zone.Hand, controller: ctx.Controller);
                break;
            }
            case CommanderOntoBattlefieldUntilEndStep:
            {
                var who = ctx.Controller;
                var commanders = State.GetPlayer(who).Command.Select(State.GetCard).Where(c => c.IsCommander && c.Owner == who && c.Types.IsPermanent()).ToList();
                if (commanders.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                    $"{ctx.Source.Name}: you may put a commander you own onto the battlefield", ctx.Source.Id, commanders.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                if (pick.Count != 1 || commanders.All(c => c.Id != pick[0])) break;
                await MoveCardAsync(pick[0], Zone.Battlefield, controller: who);
                var commander = State.GetCard(pick[0]);
                if (commander.Zone != Zone.Battlefield) break;
                State.LastingEffects.Add(new UntilEndOfTurnEffect(commander.Id, commander.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                State.AtNextEndStepEffects.Add((ctx.Source.Id, who, commander.Id, commander.Version, new Effect[] { new ReturnToCommandZone(Subject.Triggered) }, null, null));
                break;
            }
            case ReturnToCommandZone rtc:
                foreach (var card in CardsFor(rtc.What, ctx).Where(c => c.Zone == Zone.Battlefield && c.IsCommander).ToList())
                    await MoveCardAsync(card.Id, Zone.Command);
                break;
            case CastCounteredExiledFree:
                foreach (var (counteredCard, _) in ctx.Results.Countered.ToList())
                {
                    var exiled = State.GetCard(counteredCard);
                    if (exiled.Zone != Zone.Exile || exiled.Definition.IsToken || exiled.Is(CardType.Land)) continue;
                    if (!HasLegalTargets(CastingTargets(exiled.Definition), ctx.Controller, exiled.Id)) continue;
                    if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {exiled.Name} without paying its mana cost?", exiled.Id)))
                        await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), exiled.Id);
                }
                break;
            case PutOneOfBatchOntoBattlefield:
            {
                var candidates = (ctx.Trigger?.Batch ?? new()).Select(b => State.GetCard(b.Card)).Where(c => c.Zone == Zone.Graveyard).ToList();
                if (candidates.Count == 0) break;
                var pick = candidates.Count == 1 ? new[] { candidates[0].Id } : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose one to put onto the battlefield", ctx.Source.Id, candidates.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                Require(pick.Count == 1 && candidates.Any(c => c.Id == pick[0]), "Choose one of them.");
                await MoveCardAsync(pick[0], Zone.Battlefield, controller: ctx.Controller);
                break;
            }
            case AttackRandomOpponentNotAttackedLastCombat:
                AttackRandomOpponentAsTerritorial(ctx);
                break;
            case PlayOpponentsExiledWithCounterFree poe:
                await PlayOpponentsExiledWithCounterFreeAsync(poe, ctx);
                break;
            case AttacksThisCombat atc:
                foreach (var card in CardsFor(atc.What, ctx).Where(c => c.Zone == Zone.Battlefield))
                    State.AttacksThisCombat.Add((card.Id, card.Version, State.CombatSerial));
                break;
            case DoubleTokensThisTurn:
                State.GetPlayer(ctx.Controller).TokenDoublingsThisTurn.Add(State.TurnNumber);
                break;
            case HandBottomExileOfThree:
                await HandBottomExileOfThreeAsync(ctx);
                break;
            case ChooseNumberDestroyPowerAtLeast cnd:
            {
                var options = Enumerable.Range(0, cnd.Max + 1).Select(n => n.ToString()).ToList();
                int chosen = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose a number", ctx.Source.Id, options, OptionKind.Other));
                Require(chosen >= 0 && chosen <= cnd.Max, "Choose one of the numbers.");
                Emit(new ChoiceMade(ctx.Source.Id, chosen.ToString()));
                await ApplyAsync(new Destroy(Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any, MinPower: chosen))), ctx);
                break;
            }
            case EachKeepsTotalPowerAtMost ekp:
                await EachKeepsTotalPowerAtMostAsync(ekp, ctx);
                break;
            case CountersEqualToOwnToughness cet:
            {
                BeginSimultaneous();
                foreach (var card in CardsFor(cet.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    if (card.Toughness > 0) PutCounters(card, CounterKind.PlusOnePlusOne, card.Toughness, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case ExiledControllersCreateToken ect:
                foreach (var id in ctx.Results.Exiled.ToList())
                {
                    var exiled = State.GetCard(id);
                    var controller = exiled.LastKnownInfo?.Controller ?? exiled.Owner;
                    ctx.Results.Created.AddRange((await CreateTokenEventAsync(controller, ect.Token, 1, false, ctx.Source)).Originals);
                }
                break;
            case ExiledCopyAttackingEachOpponent:
                await ExiledCopyAttackingEachOpponentAsync(ctx);
                break;
            case PreventNextFromChosenSource pns:
                await PreventNextFromChosenSourceAsync(pns, ctx);
                break;
            case GrantHarmonize gh:
                foreach (var card in CardsFor(gh.What, ctx).Where(c => c.Zone == Zone.Graveyard).ToList())
                    State.HarmonizeGranted.Add((card.Id, card.Version, State.TurnNumber));
                break;
            case ExileRandomFromGraveyardPlayable:
                await ExileRandomFromGraveyardPlayableAsync(ctx);
                break;
            case SearchOneOfEachColor soc:
                await SearchOneOfEachColorAsync(soc, ctx);
                break;
            case GraveyardToLibraryTop glt:
                await GraveyardToLibraryTopAsync(glt, ctx);
                break;
            case LookTopToHandOrGraveyard lth:
                await LookTopToHandOrGraveyardAsync(lth, ctx);
                break;
            case CastFromGraveyardFree cfg:
                await CastFromGraveyardFreeAsync(cfg, ctx);
                break;
            case PutTriggeredCounters ptc:
            {
                var had = TriggeredCounters(ctx);
                BeginSimultaneous();
                foreach (var card in CardsFor(ptc.To, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    foreach (var (kind, n) in had.Where(kv => kv.Value > 0)) PutCounters(card, kind, n, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case ExileCardsFromHand ech:
            {
                int n = Eval(ech.Count, ctx);
                foreach (var who in PlayersFor(ech.Who, ctx).ToList())
                {
                    var hand = State.GetPlayer(who).Hand.ToList();
                    int take = Math.Min(n, hand.Count);
                    if (take <= 0) continue;
                    var pick = take == hand.Count ? hand : (await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Exile {take} card(s) from your hand", ctx.Source.Id,
                        hand.Select(id => ViewBuilder.Card(State, id, who)).ToList(), take, take, CardChoicePurpose.Discard))).ToList();
                    Require(pick.Count == take && pick.Distinct().Count() == take && pick.All(hand.Contains), "Choose cards from your hand.");
                    foreach (var id in pick) { await MoveCardAsync(id, Zone.Exile); ctx.Results.Exiled.Add(id); }
                }
                break;
            }
            case ExchangeLifeWithToughness ex:
            {
                if (ctx.Source.Zone != Zone.Battlefield || !ctx.Source.IsCreature || PlayersFor(ex.Player, ctx).Cast<PlayerId?>().FirstOrDefault() is not { } swapped) break;
                var player = State.GetPlayer(swapped);
                int life = player.Life, toughness = ctx.Source.Toughness, delta = toughness - life;
                bool cantGain = player.CantGainLifeForRestOfGame || State.Battlefield.Select(State.GetCard).Any(c => c.HasTextAbilities && (c.Definition.PlayersCantGainLife || (c.Definition.OpponentsCantGainLife && c.Controller != swapped)));
                if (player.LifeCantChange || (delta > 0 && cantGain) || (delta < 0 && player.CantLoseLifeNow(State.TurnNumber))) break; // 701.12a
                if (delta > 0) await GainLifeAsync(swapped, delta);
                else if (delta < 0) ChangeLife(swapped, delta);
                State.LastingEffects.Add(new UntilEndOfTurnEffect(ctx.Source.Id, ctx.Source.Version, 0, 0, Array.Empty<Keyword>()) { SetToughness = life, Timestamp = NewTimestamp() });
                RecomputeContinuousEffects();
                break;
            }
            case ChooseColorNow:
            {
                var names = ColorLetters.Select(l => ColorNames[Array.IndexOf(ColorLetters, l)]).ToList();
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose a color", ctx.Source.Id, names, OptionKind.Color));
                Require(i >= 0 && i < names.Count, "Choose one of the colors.");
                ctx.Source.ChosenColor = ColorLetters[i];
                Emit(new ChoiceMade(ctx.Source.Id, names[i]));
                RecomputeContinuousEffects();
                break;
            }
            case GraveyardToLibraryBottom gb:
                foreach (var who in PlayersFor(gb.Who, ctx).ToList())
                {
                    var cards = State.GetPlayer(who).Graveyard.ToList();
                    Rng.Shuffle(cards);
                    foreach (var id in cards) await MoveCardAsync(id, Zone.Library, toBottom: true);
                    PlacedInUnseenOrder(cards, null);
                }
                break;
            case ReturnWithCounter rc:
                if (ctx.Source.Zone == Zone.Graveyard && ctx.SourceVersion is { } persisted && persisted == ctx.Source.Version)
                    await MoveCardAsync(ctx.Source.Id, Zone.Battlefield, controller: ctx.Source.Owner, withCounters: new[] { (rc.Kind, 1) });
                break;
            case ManifestDread md:
                foreach (var player in (md.Who is { } dreader ? PlayersFor(dreader, ctx) : new[] { ctx.Controller }).ToList())
                    for (int i = 0, times = md.Times is { } t ? Eval(t, ctx) : 1; i < times; i++)
                        if (await ManifestDreadAsync(player, ctx.Source.Id) is { } dreaded) ctx.Results.Created.Add(dreaded);
                break;
            case ShardsBecomeCopies sbc:
            {
                var original = CardsFor(sbc.Of, ctx).FirstOrDefault() ?? (ctx.TargetAt(0)?.Card is { } sbt ? State.GetCard(sbt) : null);
                if (original is null) break;
                var copiable = original.Zone == Zone.Battlefield ? original.Definition : original.PrintedDefinition;
                foreach (var shard in State.PermanentsControlledBy(ctx.Controller).Where(c => c.HasSubtype("Shard")).ToList())
                {
                    var previous = shard.CopiedDefinition;
                    shard.CopiedBeforeEndOfTurn = null;
                    shard.CopyEndsAtCleanup = false;
                    shard.CopiedDefinition = copiable;
                    State.AtNextEndStepEffects.Add((ctx.Source.Id, ctx.Controller, shard.Id, shard.Version, new Effect[] { new RevertCopy(previous) }, null, ctx.Source.TransformCount));
                }
                RecomputeContinuousEffects();
                break;
            }
            case RevertCopy rc:
                if (ctx.Trigger is { Subject: { } revertedId } revertInfo && State.GetCard(revertedId) is { Zone: Zone.Battlefield } reverted && reverted.Version == revertInfo.SubjectVersion)
                {
                    reverted.CopiedDefinition = rc.Previous;
                    RecomputeContinuousEffects();
                }
                break;
            case EachPlayerReanimates epr:
            {
                var picks = new List<(PlayerId Player, CardId Card)>();
                foreach (var player in State.ApnapOrder().ToList())
                {
                    var options = State.GetPlayer(player).Graveyard.Select(State.GetCard)
                        .Where(c => Matches(epr.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0) continue;
                    var pick = options.Count == 1 ? new[] { options[0].Id } : await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose a card from your graveyard to put onto the battlefield", ctx.Source.Id,
                        options.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                    Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the listed cards.");
                    picks.Add((player, pick[0]));
                }
                BeginEnteringTogether();
                foreach (var (player, card) in picks) await MoveCardAsync(card, Zone.Battlefield, controller: player);
                EndEnteringTogether();
                break;
            }
            case TurnFaceUpFree tfu:
                foreach (var card in CardsFor(tfu.What, ctx).Where(c => c.Zone == Zone.Battlefield && c.TurnedFaceDown && !CantBeTurnedFaceUp(c)).ToList())
                {
                    card.TurnedFaceDown = false;
                    RecomputeContinuousEffects();
                    Emit(new TurnedFaceUp(card.Id, ctx.Controller));
                    State.GetPlayer(ctx.Controller).TurnedFaceUpTurn = State.TurnNumber;
                }
                break;
            case OpponentsCreaturesMustAttackOthers:
                State.OpponentsCreaturesMustAttackOthers.Add(ctx.Controller);
                break;
            case MillTwoWhileSharingType:
            {
                var player = State.GetPlayer(ctx.Controller);
                while (player.Library.Count > 0)
                {
                    int before = ctx.Results.Milled.Count;
                    await ApplyAsync(new Mill(new Quantity(2), Subject.You), ctx);
                    var two = ctx.Results.Milled.Skip(before).ToList();
                    var types = two.Select(id => State.GetCard(id).Definition.Types).ToList();
                    if (two.Count < 2 || (types[0] & types[1]) == 0) break;
                    await DrawAsync(ctx.Controller);
                }
                break;
            }
            case NthResolutionThisTurn nth:
            {
                var (turn, count) = ctx.Source.Resolutions;
                count = turn == State.TurnNumber ? count + 1 : 1;
                ctx.Source.Resolutions = (State.TurnNumber, count);
                if (count <= nth.Cases.Count) await ApplyAllAsync(nth.Cases[count - 1], ctx);
                else if (nth.Otherwise is { } nthOtherwise) await ApplyAllAsync(nthOtherwise, ctx);
                break;
            }
            case EachCreatureDamagesController ecd:
            {
                var parts = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature).Select(c => new DamagePart(c, null, c.Controller, ecd.Amount, false)).ToList();
                if (parts.Count > 0) await DealDamageEventAsync(parts);
                break;
            }
            case ReanimateRandom rr:
            {
                var pool = State.GetPlayer(ctx.Controller).Graveyard.Where(id => Matches(rr.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                Rng.Shuffle(pool);
                BeginEnteringTogether();
                foreach (var id in pool.Take(rr.Count)) await MoveCardAsync(id, Zone.Battlefield, controller: ctx.Controller);
                EndEnteringTogether();
                break;
            }
            case ManifestFromHand:
            {
                var hand = State.GetPlayer(ctx.Controller).Hand.ToList();
                if (hand.Count == 0) break;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest($"{ctx.Source.Name}: manifest a card from your hand",
                    ctx.Source.Id, hand.Select(id => ViewBuilder.Card(State, id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                Require(pick.Count == 1 && hand.Contains(pick[0]), "Choose a card from your hand.");
                if (await ManifestAsync(ctx.Controller, pick[0]) is { } made) ctx.Results.Created.Add(made);
                break;
            }
            case EachPlayerChoosesYourOpponentsCreatureToDestroy:
            {
                var order = State.ApnapOrder().ToList();
                int start = order.IndexOf(ctx.Controller);
                var chosen = new List<CardId>();
                for (int i = 1; i <= order.Count; i++)
                {
                    var chooser = order[(start + i) % order.Count];
                    var options = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature && c.Controller != ctx.Controller).ToList();
                    if (options.Count == 0) continue;
                    var pick = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest($"{ctx.Source.Name}: choose a creature {State.GetPlayer(ctx.Controller).Name} doesn't control",
                        ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, chooser)).ToList(), 1, 1, CardChoicePurpose.Sacrifice));
                    Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the listed creatures.");
                    chosen.Add(pick[0]);
                    Emit(new ChoiceMade(pick[0], "chosen"));
                }
                ctx.Results.Chosen.AddRange(chosen);
                await ApplyAsync(new Destroy(new Subject(SubjectKind.Chosen)), ctx);
                break;
            }
            case WheneverCreatureDiesThisTurn wcd:
                State.CreatureDiesThisTurnTriggers.Add((ctx.Source.Id, ctx.Controller, wcd.Ability, State.TurnNumber));
                break;
            case AttacksRandomOpponent:
            {
                var opponents = State.OpponentsOf(ctx.Controller).Where(p => !State.GetPlayer(p).HasLost).ToList();
                if (opponents.Count == 0 || ctx.Source.Zone != Zone.Battlefield) break;
                var chosen = opponents[Rng.Next(opponents.Count)];
                Emit(new ChoiceMade(ctx.Source.Id, State.GetPlayer(chosen).Name));
                State.AttackPlayerRequirements.Add((ctx.Source.Id, ctx.Source.Version, chosen, State.TurnNumber));
                break;
            }
            case ExileGraveyardWithTypesReturnOne eg:
            {
                var who = ctx.Controller;
                var yard = State.GetPlayer(who).Graveyard.ToList();
                int TypesOf(IEnumerable<CardId> ids) => new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred,
                    CardType.Land, CardType.Planeswalker, CardType.Sorcery }.Count(t => ids.Any(id => State.GetCard(id).Definition.Is(t)));
                if (TypesOf(yard) < eg.CardTypes) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{ctx.Source.Name}: exile any number of cards with {eg.CardTypes} or more card types among them (or none)",
                    ctx.Source.Id, yard.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 0, yard.Count, CardChoicePurpose.Sacrifice));
                Require(pick.Distinct().Count() == pick.Count && pick.All(yard.Contains), "Choose cards from your graveyard.");
                if (pick.Count == 0 || TypesOf(pick) < eg.CardTypes) break; // fewer types: nothing is exiled
                foreach (var id in pick) await MoveCardAsync(id, Zone.Exile);
                var permanents = pick.Where(id => State.GetCard(id).Zone == Zone.Exile && State.GetCard(id).Definition.Types.IsPermanent()).ToList();
                if (permanents.Count == 0) break;
                var back = permanents.Count == 1 ? permanents : await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{ctx.Source.Name}: put a permanent card from among them onto the battlefield",
                    ctx.Source.Id, permanents.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                Require(back.Count == 1 && permanents.Contains(back[0]), "Choose one of the exiled permanent cards.");
                await MoveCardAsync(back[0], Zone.Battlefield, controller: who, withCounters: new[] { (CounterKind.Finality, 1) });
                break;
            }
            case SacrificeSharingTypeToReturnTarget:
            {
                if (ctx.TargetAt(0)?.Card is not { } chosenId || State.GetCard(chosenId) is not { Zone: Zone.Graveyard } chosen) break;
                var types = chosen.Definition.Types;
                var fodder = State.PermanentsControlledBy(ctx.Controller).Where(c => (c.Types & types & ~CardType.Kindred) != 0).ToList();
                if (fodder.Count == 0) break;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: you may sacrifice a permanent that shares a card type with {chosen.Name}", ctx.Source.Id,
                    fodder.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, 1, CardChoicePurpose.Sacrifice));
                if (pick.Count != 1 || !fodder.Any(c => c.Id == pick[0])) break;
                await SacrificePermanentAsync(pick[0]);
                if (State.GetCard(chosenId).Zone != Zone.Graveyard) break;
                await MoveCardAsync(chosenId, Zone.Battlefield, controller: ctx.Controller);
                if (State.GetCard(chosenId) is { Zone: Zone.Battlefield } returned) State.ExileIfLeaves.Add((returned.Id, returned.Version));
                break;
            }
            case ExileAllCreaturesFractals eaf:
            {
                var creatures = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature).ToList();
                BeginSimultaneous();
                foreach (var c in creatures) await MoveCardAsync(c.Id, Zone.Exile);
                EndSimultaneous();
                // "Total power of creatures they controlled that were exiled this way": as they last existed, those that went to exile.
                var exiledPower = creatures.Where(c => c.Zone == Zone.Exile || c.CeasedToExist).GroupBy(c => c.LastKnownInfo?.Controller ?? c.Owner)
                    .ToDictionary(g => g.Key, g => g.Sum(c => Math.Max(0, c.LastKnownInfo?.Power ?? 0)));
                foreach (var player in State.ApnapOrder().ToList())
                    if (await CreateTokenAsync(eaf.Token, player) is { } fractal)
                    {
                        int n = exiledPower.GetValueOrDefault(player);
                        if (n > 0) PutCounters(State.GetCard(fractal), CounterKind.PlusOnePlusOne, n, player);
                    }
                break;
            }
            case ExileTopCastOnePerType ept:
            {
                var who = ctx.Controller;
                var top = State.GetPlayer(who).Library.Take(ept.Count).ToList();
                foreach (var id in top) await MoveCardAsync(id, Zone.Exile);
                var exiled = top.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
                var lands = exiled.Where(id => State.GetCard(id).Is(CardType.Land)).ToList();
                if (lands.Count > 0)
                {
                    var land = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{ctx.Source.Name}: you may put a land card onto the battlefield",
                        ctx.Source.Id, lands.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                    if (land.Count == 1 && lands.Contains(land[0])) await MoveCardAsync(land[0], Zone.Battlefield, controller: who);
                }
                foreach (var type in new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred, CardType.Planeswalker, CardType.Sorcery })
                {
                    int group = (int)NewTimestamp();
                    foreach (var id in exiled.Where(id => State.GetCard(id) is { Zone: Zone.Exile } c && c.Is(type)))
                        State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, who, State.TurnNumber, WithoutPaying: true)
                            { CastOnly = true, Group = group, GroupLabel = $"Your {type.ToString().ToLowerInvariant()} spell" });
                }
                break;
            }
            case RevealTopCastIfLesser:
            {
                var library = State.GetPlayer(ctx.Controller).Library;
                if (library.Count == 0) break;
                var top = State.GetCard(library[0]);
                Emit(new CardsRevealed(ctx.Controller, new[] { top.Id }));
                int limit = ctx.Trigger?.Amount ?? 0; // the triggering spell's mana value
                bool cast = !top.Is(CardType.Land) && top.ManaValue < limit
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {top.Name} without paying its mana cost?", top.Id))
                    && await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), top.Id);
                if (!cast && top.Zone == Zone.Library) await MoveCardAsync(top.Id, Zone.Hand);
                break;
            }
            case ExiledForCostPlayableThisTurn:
                foreach (var id in ctx.Results.ExiledForCost.Where(id => State.GetCard(id).Zone == Zone.Exile))
                    State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, State.TurnNumber));
                break;
            case RandomGraveyardCardBack:
            {
                var yard = State.GetPlayer(ctx.Controller).Graveyard;
                if (yard.Count == 0) break;
                var picked = State.GetCard(yard[Rng.Next(yard.Count)]);
                Emit(new ChoiceMade(picked.Id, picked.Name));
                await MoveCardAsync(picked.Id, picked.Definition.IsCreature() ? Zone.Battlefield : Zone.Hand, controller: ctx.Controller);
                break;
            }
            case ReorderTopOf rto:
                foreach (var owner in PlayersFor(rto.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(owner).Library;
                    var top = library.Take(rto.Count).ToList();
                    foreach (var id in top) State.GetCard(id).KnownTo.Add(ctx.Controller);
                    if (top.Count < 2) continue;
                    var ordered = (await OrderAsync(ctx.Controller, top, "Choose the card to put on top next (the last one ends on top)", ctx.Source)).ToList();
                    if (ordered.Count != top.Count || !ordered.All(top.Contains)) ordered = top;
                    library.RemoveAll(top.Contains);
                    // The first chosen goes on top first, so the last one chosen ends on top.
                    foreach (var id in ordered) library.Insert(0, id);
                }
                break;
            case ChooseParityNow:
            {
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose odd or even", ctx.Source.Id, new[] { "Odd", "Even" }, OptionKind.Other));
                Require(i is 0 or 1, "Choose odd or even.");
                ctx.Source.ChosenParity = i == 0 ? "odd" : "even";
                Emit(new ChoiceMade(ctx.Source.Id, i == 0 ? "Odd" : "Even"));
                break;
            }
            case HandTopBottom:
            {
                var who = ctx.Controller;
                var player = State.GetPlayer(who);
                var top = player.Library.Take(3).ToList();
                foreach (var id in top) State.GetCard(id).KnownTo.Add(who);
                if (top.Count == 0) break;
                var views = top.Select(id => ViewBuilder.Card(State, id, who)).ToList();
                var toHand = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Put one into your hand", ctx.Source.Id, views, 1, 1, CardChoicePurpose.ToHand));
                Require(toHand.Count == 1 && top.Contains(toHand[0]), "Choose one of the cards.");
                var left = top.Where(id => id != toHand[0]).ToList();
                var onTop = left.Count == 0 ? new List<CardId>() : (await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Put one on top of your library (the other goes to the bottom)",
                    ctx.Source.Id, left.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 1, 1, CardChoicePurpose.Keep))).ToList();
                Require(left.Count == 0 || (onTop.Count == 1 && left.Contains(onTop[0])), "Choose one of the cards.");
                await MoveCardAsync(toHand[0], Zone.Hand);
                foreach (var id in left.Where(id => !onTop.Contains(id))) await MoveCardAsync(id, Zone.Library, toBottom: true);
                break;
            }
            case ControllersMayExile cme:
                foreach (var chosen in CardsFor(cme.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    if (await ControllerOf(chosen.Controller).ChooseYesNoAsync(ViewFor(chosen.Controller), new YesNoRequest($"{ctx.Source.Name}: exile {chosen.Name}?", chosen.Id)))
                    {
                        await MoveCardAsync(chosen.Id, Zone.Exile);
                        ctx.Results.Exiled.Add(chosen.Id);
                    }
                break;
            case UnlessSacrificesTarget ust:
                foreach (var target in CardsFor(ust.Target, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    var owner = target.Controller;
                    bool sacrificed = target.Definition is { CantBeSacrificed: false }
                        && await ControllerOf(owner).ChooseYesNoAsync(ViewFor(owner), new YesNoRequest($"Sacrifice {target.Name}? (Otherwise: {ctx.Source.Name})", target.Id));
                    if (sacrificed) await SacrificePermanentAsync(target.Id);
                    else await ApplyAllAsync(ust.Otherwise, ctx with { Trigger = (ctx.Trigger ?? new TriggerInfo()) with { Player = owner } });
                }
                break;
            case TargetControllerChoosesOne tco:
            {
                var pair = Enumerable.Range(0, Math.Min(2, ctx.Targets.Count)).Where(i => ctx.TargetLegal[i] && ctx.Targets[i].Target.Card is { } id && State.GetCard(id).Zone == Zone.Battlefield).ToList();
                if (pair.Count == 0) break;
                int pick = pair[0];
                if (pair.Count == 2)
                {
                    var chooser = State.GetCard(ctx.Targets[pair[0]].Target.Card!.Value).Controller;
                    var options = pair.Select(i => ctx.Targets[i].Target.Card!.Value).ToList();
                    var chosen = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest($"{ctx.Source.Name}: choose one of these creatures",
                        ctx.Source.Id, options.Select(id => ViewBuilder.Card(State, id, chooser)).ToList(), 1, 1, CardChoicePurpose.Sacrifice));
                    Require(chosen.Count == 1 && options.Contains(chosen[0]), "Choose one of the two creatures.");
                    pick = pair[options.IndexOf(chosen[0])];
                }
                // The chosen one is "target", the other "target2"; a lone legal target is the chosen one.
                var order = new[] { pick, 1 - pick }.Where(i => i < ctx.Targets.Count).ToList();
                await ApplyAllAsync(tco.Effects, ctx with
                {
                    Targets = order.Select(i => ctx.Targets[i]).ToList(),
                    TargetLegal = order.Select(i => ctx.TargetLegal[i] && (pair.Count == 2 || i == pick)).ToArray(),
                });
                break;
            }
            case RevealTopTakeDifferentPowers rtp:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(Math.Max(0, Eval(rtp.Count, ctx))).ToList();
                if (top.Count == 0) break;
                Emit(new CardsRevealed(ctx.Controller, top));
                var eligible = top.Where(id => Matches(rtp.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                var chosen = eligible.Count == 0 ? new List<CardId>() : (await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: put any number with different powers into your hand", ctx.Source.Id,
                    eligible.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, eligible.Count, CardChoicePurpose.ToHand))).ToList();
                Require(chosen.Distinct().Count() == chosen.Count && chosen.All(eligible.Contains), "Choose among the revealed cards.");
                // Different powers: of cards chosen with the same power, the first one chosen is taken.
                chosen = chosen.GroupBy(id => State.GetCard(id).Definition.Power ?? 0).Select(g => g.First()).ToList();
                foreach (var id in chosen) await MoveCardAsync(id, Zone.Hand);
                var rest = top.Where(id => !chosen.Contains(id) && State.GetCard(id).Zone == Zone.Library).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case RevealFaceDown rfd:
                await RevealFaceDownAsync(rfd, ctx);
                break;
            case CantGainLifeForRestOfGame cgl:
                foreach (var player in PlayersFor(cgl.Who, ctx).ToList()) State.GetPlayer(player).CantGainLifeForRestOfGame = true;
                break;
            case UnlockADoor unlock:
                await UnlockADoorAsync(unlock, ctx);
                break;
            case Manifest manifest:
                foreach (var player in PlayersFor(manifest.Who, ctx).ToList())
                    for (int i = 0; i < manifest.Count; i++)
                        if (await ManifestTopAsync(player) is { } made) ctx.Results.Created.Add(made);
                break;
            case Clash clash:
                await ApplyAllAsync(await ClashAsync(ctx) ? clash.IfWin : clash.Else ?? Array.Empty<Effect>(), ctx);
                break;
            case Myriad:
                await MyriadAsync(ctx);
                break;
            case Proliferate:
                await ProliferateAsync(ctx);
                break;
            case DiscoverCards discover:
                await DiscoverAsync(discover, ctx);
                break;
            case ReselectAttack reselect:
                await ReselectAttackAsync(reselect, ctx);
                break;
            case DamageForEachStillExiled de:
            {
                int still = de.Cards.Count(c => State.GetCard(c.Card) is { Zone: Zone.Exile } card && card.Version == c.Version);
                if (still > 0)
                    await DealDamageEventAsync(State.ApnapOrder().Where(p => p != ctx.Controller)
                        .Select(p => new DamagePart(ctx.Source, null, p, de.PerCard * still, false)).ToList());
                break;
            }
            case GivePromisedGift:
                if (ctx.Source.GiftTo is { } promisedTo) await GiveGiftAsync(promisedTo, ctx.Source);
                break;
            case ExileUntilSourceLeaves eu:
                // If the source already left the battlefield, nothing is exiled (it would return at once).
                if (!SourceOnBattlefield(ctx)) break;
                foreach (var card in CardsFor(eu.What, ctx).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    if (!card.Definition.IsToken) State.LinkedExiles.Add(new LinkedExile(ctx.Source.Id, ctx.Source.Version, card.Id, card.Version));
                    if (eu.Castable && card.Zone == Zone.Exile)
                        State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, int.MaxValue) { AnyManaType = true, CastOnly = true });
                }
                break;
            case ExileAndReturnAtEndStep er:
                foreach (var card in CardsFor(er.What, ctx).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    if (!card.Definition.IsToken) State.AtNextEndStep.Add(new DelayedAction(card.Id, card.Version, Return: true, er.UnderYourControl ? ctx.Controller : card.Owner)
                        { Counters = er.ReturnCounter is { } returnKind ? (returnKind, 1) : null });
                }
                break;
            case ExtraTurn et:
                foreach (var player in PlayersFor(et.Who, ctx)) State.ExtraTurns.Add(player);
                break;
            case HeroesCommanderEffect heroesCommander:
                await ApplyHeroesCommanderAsync(heroesCommander, ctx);
                break;
            case HeroesEffect heroes:
                await ApplyHeroesAsync(heroes, ctx);
                break;
            case AcademyEffect academy:
                await ApplyAcademyAsync(academy, ctx);
                break;
            case OriginsEffect origins:
                await ApplyOriginsAsync(origins, ctx);
                break;
            case ReflexiveTrigger reflexive when reflexive.If is null || HoldsIn(reflexive.If, ctx):
                // "When you do, …": a new triggered ability, put on the stack the next time a player would receive
                // priority, with its own targets (rule 603.12).
                var about = reflexive.About is { } aboutSubject ? CardsFor(aboutSubject, ctx).FirstOrDefault() : null;
                var info = about is null && reflexive.Amount is null ? ctx.Trigger
                    : new TriggerInfo(about?.Id ?? ctx.Trigger?.Subject, about?.Version ?? ctx.Trigger?.SubjectVersion ?? 0, ctx.Trigger?.Player,
                        reflexive.Amount is { } reflexiveAmount ? Eval(reflexiveAmount, ctx) : ctx.Trigger?.Amount ?? 0);
                if (reflexive.CarryFound) info = (info ?? new TriggerInfo()) with { Found = ctx.Results.Found.ToList() };
                if (reflexive.CarryExiled) info = (info ?? new TriggerInfo()) with { Found = ctx.Results.Exiled.ToList() };
                _pendingTriggers.Add(new PendingTrigger(ctx.Source.Id, reflexive.Ability, ctx.Controller, info));
                break;
            case ReflexiveTrigger:
                break; // the action didn't happen: no "when you do"
            case CreateTokenCopy tc when tc.Each:
                foreach (var id in CardsFor(tc.Of, ctx).Select(c => c.Id).ToList())
                {
                    var single = ctx with { Results = new EffectResults() };
                    single.Results.Found.Add(id);
                    await ApplyAsync(tc with { Each = false, Of = new Subject(SubjectKind.Found) }, single);
                    ctx.Results.Created.AddRange(single.Results.Created);
                }
                break;
            case CreateTokenCopy tc:
            {
                int count = Eval(tc.Count, ctx);
                // The original may be a permanent, or a card (in exile or a graveyard) for "a copy of it".
                var original = tc.Of.Kind == SubjectKind.Self ? ctx.Source
                    : tc.Of.Kind == SubjectKind.Target && ctx.ChosenAt(tc.Of.Index)?.Card is { } chosenCard ? State.GetCard(chosenCard)
                    : tc.OnlyLastExiled && ctx.Results.Exiled.Count > 0 ? State.GetCard(ctx.Results.Exiled[^1])
                    : CardsFor(tc.Of, ctx).FirstOrDefault();
                // "A copy of that creature" after it left the battlefield: its last known copiable values (rule 608.2h, 707.4).
                var lastKnownCopy = original is null && tc.Of.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } gone } goneInfo
                                    && State.GetCard(gone).LastKnownOf(goneInfo.SubjectVersion) is { CopiableValues: { } values } goneLki
                    ? (values, goneLki.CopiableBackFaceUp)
                    // "A copy of it" once the source left the battlefield (squad, offspring): as it last existed there.
                    : tc.Of.Kind == SubjectKind.Self && ResolvingSourceGone(ctx.Source) is { CopiableValues: { } selfValues } selfLki
                    ? (selfValues, selfLki.CopiableBackFaceUp) : ((CardDefinition, bool)?)null;
                if (original is null && lastKnownCopy is null) break;
                // The exceptions ("except it has haste and 'At the beginning of the end step, sacrifice this token'")
                // become part of the copy's own characteristics (rule 707.9b): copiable, and lost with its abilities. A copy of a
                // double-faced object is a double-faced token, each face with the exceptions (707.8a).
                var (copied, backFaceUp) = lastKnownCopy ?? CopiableForToken(original!);
                CardDefinition Except(CardDefinition face)
                {
                    var copy = face with
                    {
                        IsToken = true,
                        ImageByName = face.ImageByName || (!face.IsToken && face.ImageKey is null),
                        Supertypes = tc.NotLegendary ? face.Supertypes & ~Supertype.Legendary : face.Supertypes,
                        ManaCost = tc.NoManaCost ? ManaCost.Zero : face.ManaCost,
                        Keywords = tc.Haste ? face.Keywords.Append("Haste").ToList() : face.Keywords,
                        Subtypes = tc.AddSubtypes is { } extra ? face.Subtypes.Concat(extra).Distinct().ToList() : face.Subtypes,
                        Abilities = tc.SacrificeAtEndStep ? face.Abilities.Append(SacrificeThisAtEndStep).ToList() : face.Abilities,
                    };
                    return copy with
                    {
                        Power = tc.SetPower ?? copy.Power,
                        Toughness = tc.SetToughness ?? copy.Toughness,
                        PowerFrom = tc.SetPower is not null ? null : copy.PowerFrom,
                        ToughnessFrom = tc.SetToughness is not null ? null : copy.ToughnessFrom,
                        Colors = tc.SetColors ?? (tc.AddColors is { } addColors ? copy.ColorList.Concat(addColors).Distinct().ToList() : copy.Colors),
                        Types = (tc.SetTypes ?? copy.Types) | tc.AddTypes,
                        Subtypes = tc.SetSubtypes is { } setSubtypes
                            ? (tc.SetTypes is not null || !copy.IsCreature() ? setSubtypes : setSubtypes.Concat(copy.Subtypes.Where(s => !IsCreatureType(s))).ToList())
                            : copy.Subtypes,
                        Keywords = tc.AddKeywords is { } addKeywords ? copy.Keywords.Concat(addKeywords).Distinct().ToList() : copy.Keywords,
                        Abilities = tc.AddAbilities is { } addAbilities ? copy.Abilities.Concat(addAbilities).ToList() : copy.Abilities,
                    };
                }
                var tokenCopy = Except(copied) with { BackFace = copied.BackFace is { } copiedBack ? Except(copiedBack) : null };
                PlayerId? creatorOrNone = tc.For is { } creatorSubject ? PlayersFor(creatorSubject, ctx).Cast<PlayerId?>().FirstOrDefault() : ctx.Controller;
                if (creatorOrNone is not { } creator) break;
                foreach (var made in (await CreateTokenEventAsync(creator, tokenCopy, count, tc.Tapped, ctx.Source, backFaceUp)).Originals)
                {
                    ctx.Results.Created.Add(made);
                    if (original is { Zone: not Zone.Battlefield }) State.GetCard(made).CopiedFromCard = (original.Id, original.Version);
                    if (tc.Attacking && State.Combat is { } copyCombat)
                        copyCombat.Attacks.Add(tc.AttackingTriggeredPlayer && ctx.Trigger?.Player is { } attackedPlayer
                            ? new AttackInfo { Attacker = made, Defender = attackedPlayer }
                            : await AttackFor(State.GetCard(made), ctx));
                    if (tc.ExileAtEndOfCombat) State.ExileAtEndOfCombat.Add((made, State.GetCard(made).Version));
                    if (tc.SacrificeAtYourNextEndStep)
                        State.AtPlayersNextEndStep.Add((new DelayedTrigger(ctx.Source.Id, SacrificeThatToken, ctx.Controller, 0) { AboutCard = (made, State.GetCard(made).Version) },
                            ctx.Controller, State.TurnNumber, State.Step == Step.End));
                    if (tc.SacrificeAtNextUpkeep)
                        State.AtNextUpkeep.Add(new DelayedTrigger(ctx.Source.Id, SacrificeThatToken, ctx.Controller, 0) { AboutCard = (made, State.GetCard(made).Version) });
                    // "That token gains haste": an effect on the token, not one of its copiable values (rule 707.9b doesn't apply).
                    if (tc.GainsHaste && State.GetCard(made) is { Zone: Zone.Battlefield } hasty)
                        State.LastingEffects.Add(new UntilEndOfTurnEffect(made, hasty.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                    if (tc.AtNextEndStep is { } later)
                        State.AtNextEndStepEffects.Add((ctx.Source.Id, ctx.Controller, made, State.GetCard(made).Version, later, tc.AtNextEndStepUnless, ctx.Source.TransformCount));
                }
                break;
            }
            case GuessTopCard gt:
            {
                var player = State.GetPlayer(ctx.Controller);
                if (player.Library.Count == 0) break;
                var top = player.Library.Take(gt.Look).ToList();
                Look(ctx.Controller, top);
                // "Put them back in any order".
                var ordered = await OrderAsync(ctx.Controller, top, "Choose the card to put on top next", ctx.Source);
                foreach (var id in top) player.Library.Remove(id);
                player.Library.InsertRange(0, ordered);
                PlacedInUnseenOrder(ordered, ctx.Controller);
                int kind = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose land or nonland", ctx.Source.Id, new[] { "Land", "Nonland" }, OptionKind.Other));
                Require(kind is 0 or 1, "Choose land or nonland.");
                var guesser = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who guesses") ?? State.OpponentsOf(ctx.Controller).First();
                int guess = await ControllerOf(guesser).ChooseOptionAsync(ViewFor(guesser), new OptionRequest(
                    $"{ctx.Source.Name}: {player.Name} chose land or nonland. Guess whether the top card of their library is the chosen kind", ctx.Source.Id,
                    new[] { "It is the chosen kind", "It isn't the chosen kind" }, OptionKind.Other));
                Require(guess is 0 or 1, "Guess one of the options.");
                var revealed = State.GetCard(player.Library[0]);
                Emit(new CardsRevealed(ctx.Controller, new[] { revealed.Id }));
                bool isChosenKind = revealed.Is(CardType.Land) == (kind == 0);
                bool right = (guess == 0) == isChosenKind;
                await ApplyAllAsync(right ? gt.Right : gt.Wrong, ctx);
                break;
            }
            case SacrificeAnyNumber san:
            {
                var who = ctx.Controller;
                var candidates = State.PermanentsControlledBy(who).Where(c => Matches(san.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)).ToList();
                if (candidates.Count == 0) break;
                var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Sacrifice any number", ctx.Source.Id,
                    candidates.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, candidates.Count, CardChoicePurpose.Sacrifice));
                Require(chosen.Distinct().Count() == chosen.Count && chosen.All(id => candidates.Any(c => c.Id == id)), "Sacrifice among the listed permanents.");
                BeginSimultaneous();
                foreach (var id in chosen)
                {
                    await SacrificePermanentAsync(id);
                    ctx.Results.Sacrificed.Add(id);
                    ctx.Results.YouSacrificed = true;
                }
                EndSimultaneous();
                break;
            }
            case RevealTopPutAny rp:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(Eval(rp.Count, ctx)).ToList();
                if (top.Count == 0) break;
                Emit(new CardsRevealed(ctx.Controller, top));
                var eligible = top.Where(id => Matches(rp.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                IReadOnlyList<CardId> chosen = Array.Empty<CardId>();
                if (eligible.Count > 0)
                {
                    chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose any number to put onto the battlefield", ctx.Source.Id,
                        eligible.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, eligible.Count, CardChoicePurpose.ToBattlefield));
                    Require(chosen.Distinct().Count() == chosen.Count && chosen.All(eligible.Contains), "Choose among the revealed cards.");
                }
                // Nonland cards first, then lands tapped (two separate events).
                BeginEnteringTogether();
                foreach (var id in chosen.Where(id => !State.GetCard(id).Is(CardType.Land))) await MoveCardAsync(id, Zone.Battlefield, controller: ctx.Controller);
                EndEnteringTogether();
                BeginEnteringTogether();
                foreach (var id in chosen.Where(id => State.GetCard(id).Is(CardType.Land)))
                {
                    await MoveCardAsync(id, Zone.Battlefield, controller: ctx.Controller, tapped: true);
                }
                EndEnteringTogether();
                var rest = top.Where(id => !chosen.Contains(id)).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case NoteCreatureType:
            {
                var types = CreatureTypeOptions(ctx.Controller).Where(t => !ctx.Source.NotedTypes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (types.Count == 0) break;
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: note a creature type", ctx.Source.Id, types, OptionKind.CreatureType));
                Require(i >= 0 && i < types.Count, "Choose one of the creature types.");
                ctx.Source.NotedTypes.Add(types[i]);
                State.NextCreatureSpellBonus.Add((ctx.Controller, types[i], State.TurnNumber));
                Emit(new ChoiceMade(ctx.Source.Id, types[i]));
                break;
            }
            case SacrificeIt si:
                foreach (var card in (si.What.Kind == SubjectKind.Self ? (SourceOnBattlefield(ctx) && ctx.Source.Controller == ctx.Controller ? new[] { ctx.Source } : Array.Empty<Card>()) : CardsFor(si.What, ctx)).ToList())
                {
                    if (card.Zone != Zone.Battlefield) continue;
                    bool yours = card.Controller == ctx.Controller;
                    await SacrificePermanentAsync(card.Id);
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
                    Count = lt.CountFrom is { } lookCount ? Eval(lookCount, ctx) : lt.CountIsX ? ctx.X : lt.Count,
                    Filter = lt.MaxManaValueX ? (lt.Filter ?? ObjectFilter.Anything) with { MaxManaValue = ctx.X } : lt.Filter,
                    Take = lt.TakeFrom is { } takeCount ? Math.Max(0, Eval(takeCount, ctx)) : lt.Take < 0 ? 99 : lt.Take,
                }, ctx.Source, ctx.Results);
                break;
            case MayPayX mx:
            {
                // "Pay any amount of {R}": only mana of that type.
                ManaCost XCost(int n) => mx.ManaType is { } only ? new ManaCost(0, Enumerable.Repeat(only, n).ToList()) : new ManaCost(n, Array.Empty<ManaType>());
                int max;
                if (mx.ManaType is null) max = MaxAffordableX(ctx.Controller, ManaCost.Parse("{X}"), null);
                else for (max = 0; max < 99 && Payable(ctx.Controller, XCost(max + 1), null); max++) { }
                if (mx.Max is { } cap) max = Math.Min(max, Eval(cap, ctx));
                if (max <= 0) break;
                int x = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller), new NumberRequest(mx.Prompt, ctx.Source.Id, 0, max));
                Require(x >= 0 && x <= max, $"X must be between 0 and {max}.");
                if (x == 0 || !await PayManaAsync(State.GetPlayer(ctx.Controller), ctx.Source.Id, XCost(x), null)) break;
                await ApplyAllAsync(mx.Effects, ctx with { X = x });
                break;
            }
            case NoMaximumHandSizeForever:
                State.GetPlayer(ctx.Controller).NoMaximumHandSize = true;
                break;
            case IgnoreHexproofThisTurn:
                State.GetPlayer(ctx.Controller).IgnoresHexproofTurn = State.TurnNumber;
                break;
            case MayCastFromGraveyardThisTurn mc:
                State.GraveyardCastRights.Add(new GraveyardCastRight(ctx.Controller, State.TurnNumber, mc.Filter) { Extra = mc.Extra, Finality = mc.Finality });
                break;
            case CounterTriggeringUnlessPays ctu:
                if (ctx.Trigger is { } aimedAt) await ApplyAsync(new CounterUnlessPays(aimedAt.Amount, ctu.Mana, 0), ctx); // amount: the stack object
                break;
            case CopyNextInstantOrSorcery cn:
                State.CopyNextInstantOrSorcery.Add((ctx.Controller, State.TurnNumber, cn.PerCommanderCast ? -1 : cn.Copies, cn.AnySpell));
                break;
            case CastFromEachLibraryTopFree:
            {
                var exiled = new List<CardId>();
                foreach (var player in State.ApnapOrder().ToList())
                    if (State.GetPlayer(player).Library.FirstOrDefault() is { } top && State.GetPlayer(player).Library.Count > 0)
                    {
                        await MoveCardAsync(top, Zone.Exile);
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
                    if (!await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), pick[0])) break;
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
                             .Where(c => !c.Is(CardType.Land) && c.ManaValue == ctx.X && ctx.Source.CombatDamagedPlayers.Contains(c.Controller)
                                         && !c.Has(Keyword.Indestructible)).ToList())
                {
                    if (await SurvivesDestructionAsync(card)) continue;
                    await MoveCardAsync(card.Id, Zone.Graveyard);
                    Emit(new PermanentDestroyed(card.Id));
                }
                break;
            case AddManaUntilEndOfTurn mu2:
                foreach (var type in Enumerable.Repeat(mu2.Types, mu2.Times is { } manaTimes ? Math.Max(0, Eval(manaTimes, ctx)) : 1).SelectMany(t => t))
                {
                    State.GetPlayer(ctx.Controller).ManaPool.AddUntilEndOfTurn(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            case DiscardChosenByYou dc:
                foreach (var player in PlayersFor(dc.Who, ctx).ToList())
                {
                    var hand = State.GetPlayer(player).Hand.Select(State.GetCard).ToList();
                    Emit(new HandRevealed(player, hand.Select(c => c.Id).ToList(), Chooser: ctx.Controller));
                    var options = hand.Where(c => dc.Filter is null || Matches(dc.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller))
                        .Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList();
                    int n = Math.Min(dc.Count, options.Count);
                    int least = dc.Optional ? 0 : n;
                    // "You may choose …. If you do, …. If you don't, …": with nothing chosen (or nothing to choose) the "else" happens.
                    var chosen = n == 0 ? Array.Empty<CardId>() : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest($"Choose {(dc.Optional ? "up to " : "")}{n} card(s) for {State.GetPlayer(player).Name} to discard", ctx.Source.Id, options, least, n, CardChoicePurpose.Discard));
                    Require(chosen.Count >= least && chosen.Count <= n && chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(o => o.Id == id)), "Choose among the revealed cards.");
                    if (chosen.Count == 0)
                    {
                        if (dc.Else is { } otherwise) await ApplyAllAsync(otherwise, ctx with { Trigger = new TriggerInfo(Player: player), AffectedPlayer = player });
                        continue;
                    }
                    Emit(new ChosenFromHand(ctx.Controller, player, chosen.ToList()));
                    if (dc.ExileInstead)
                        foreach (var id in chosen)
                        {
                            await MoveCardAsync(id, Zone.Exile);
                            if (State.GetCard(id).Zone == Zone.Exile) { State.GetCard(id).ExiledWith = (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version); ctx.Results.Exiled.Add(id); }
                        }
                    else
                        foreach (var id in chosen) await DiscardCardAsync(player, id, ctx.Controller, byEffect: true);
                }
                break;
            case CreateEmblem ce:
            {
                // "Each player dealt damage this way gets an emblem": one each, owned by that player (rule 114.2).
                var emblemOwners = ce.For is { } forWhom ? PlayersFor(forWhom, ctx).Distinct().ToList() : new List<PlayerId> { ctx.Controller };
                var emblemAbout = ce.About is { } aboutWhat && CardsFor(aboutWhat, ctx).FirstOrDefault() is { } that ? (that.Id, that.Version) : ((CardId, int)?)null;
                foreach (var emblemOwner in emblemOwners)
                {
                    var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                    // A delayed ability of a spell ("until the end of your next turn, whenever you cast a spell, this deals 2 damage …") keeps
                    // that spell's name and colors as the source of what it does.
                    var emblem = new Card(id, new CardDefinition { Name = ce.Name, Abilities = ce.Abilities, IsEmblem = true, IsToken = true, Replaces = ce.Replaces,
                        Colors = ce.UntilEndOfYourNextTurn ? ctx.Source.Colors.ToList() : Array.Empty<string>(),
                        OracleText = ce.EmblemText ?? string.Join("\n", ce.Abilities.Select(a => a.Text)) }, emblemOwner)
                        { Zone = Zone.Command, Remembered = emblemAbout, ChosenType = ctx.Source.ChosenType }; // "choose a creature type; you get an emblem with '… of the chosen type …'"
                    State.Cards.Add(id, emblem);
                    State.Emblems.Add(id);
                    if (ce.UntilEndOfTurn) State.EmblemsUntilEndOfTurn.Add(id);
                    if (ce.UntilYourNextTurn) State.EmblemsUntilOwnersNextTurn.Add(id);
                    if (ce.UntilEndOfYourNextTurn) State.EmblemsUntilEndOfOwnersNextTurn.Add((id, State.TurnNumber));
                    Emit(new EmblemCreated(id, emblemOwner));
                }
                break;
            }
            case ExileTopPlayable ep:
            {
                var owners = ep.From is { } from ? PlayersFor(from, ctx).ToList() : new List<PlayerId> { ctx.Controller };
                if (owners.Count == 0) break; // "target opponent's library": the target is gone
                int howMany = ep.CountFrom is { } countFrom ? Eval(countFrom, ctx) : ep.Count;
                // "The top card of each player's library": every listed library, in turn order.
                var top = ep.UntilTotalManaValue > 0 ? new List<CardId>()
                    : State.ApnapOrder().Where(owners.Contains).SelectMany(o => (ep.FromBottom ? State.GetPlayer(o).Library.TakeLast(howMany) : State.GetPlayer(o).Library.Take(howMany)).ToList()).ToList();
                // "Exiles cards from the top of their library until they have exiled cards with total mana value N or greater this way."
                if (ep.UntilTotalManaValue > 0)
                    foreach (var owner in State.ApnapOrder().Where(owners.Contains).ToList())
                    {
                        int total = 0;
                        while (total < ep.UntilTotalManaValue && State.GetPlayer(owner).Library.Count > 0)
                        {
                            var next = State.GetPlayer(owner).Library[0];
                            await MoveCardAsync(next, Zone.Exile);
                            if (State.GetCard(next).Zone != Zone.Exile) break;
                            ctx.Results.Exiled.Add(next);
                            top.Add(next);
                            total += ManaValueOf(State.GetCard(next));
                        }
                    }
                BeginSimultaneous(); // "exile the top N cards": one event
                foreach (var id in ep.UntilTotalManaValue > 0 ? new List<CardId>() : top)
                {
                    if (ep.FaceDown) Look(ctx.Controller, new[] { id }); // "exile them face down; you may look at them"
                    await MoveCardAsync(id, Zone.Exile, faceDown: ep.FaceDown);
                    if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id); // "the exiled card", "when you exile a card this way"
                }
                EndSimultaneous();
                IReadOnlyList<CardId> playable = top;
                if (ep.ChooseOne && top.Count > 1)
                {
                    var options = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                    playable = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("Choose a card you may play this turn", ctx.Source.Id, options, 1, 1, CardChoicePurpose.ToHand));
                    Require(playable.Count == 1 && top.Contains(playable[0]), "Choose one of the exiled cards.");
                }
                int until = ep.OnceEachTurnThisTurn ? State.TurnNumber
                    : ep.Forever || ep.UntilEndOfNextTurn || ep.UntilYourNextEndStep || ep.WhileYouControlSource || ep.UntilAnotherExiled ? int.MaxValue : State.TurnNumber;
                // One cast each turn among every card exiled with this source that turn: a group shared by all of them.
                int onceGroup = 0;
                if (ep.OnceEachTurnThisTurn)
                {
                    var key = (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version, State.TurnNumber);
                    if (!_onceEachTurnGroups.TryGetValue(key, out onceGroup)) _onceEachTurnGroups[key] = onceGroup = ++_playGroups;
                    if (_usedPlayGroups.Contains(onceGroup)) playable = Array.Empty<CardId>();
                }
                var exiledBy = (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version);
                // "Until you exile another card with [this]": the earlier card's permission ends now.
                if (ep.UntilAnotherExiled && top.Count > 0) State.PlayableFromExile.RemoveAll(p => p.UntilAnotherFrom == exiledBy);
                foreach (var id in playable)
                    State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, until, ep.WithoutPaying)
                    {
                        PayLife = ep.PayLife, While = ep.While, AnyManaType = ep.AnyManaType, AnyColorOnly = ep.AnyColorOnly, CastOnly = ep.CastOnly || ep.PermanentsOnly, PermanentsOnly = ep.PermanentsOnly,
                        UntilEndOfNextTurnOf = ep.UntilEndOfNextTurn ? ctx.Controller : null, MadeOnTurn = State.TurnNumber,
                        UntilNextEndStepOf = ep.UntilYourNextEndStep ? ctx.Controller : null, MadeDuringEndStep = State.Step == Step.End,
                        WhenPlayed = ep.WhenPlayed is { } whenPlayed ? (ctx.Source.Id, whenPlayed) : null,
                        WhileControlling = ep.WhileYouControlSource ? (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version) : null,
                        UntilAnotherFrom = ep.UntilAnotherExiled ? exiledBy : null,
                        Group = onceGroup, MaxManaValue = ep.MaxManaValue,
                    });
                // "At the beginning of your next end step, [this] deals N damage to each opponent for each of those cards that are still exiled."
                if (ep.DamagePerStillExiled > 0)
                {
                    var still = new DamageForEachStillExiled(ep.DamagePerStillExiled, top.Select(id => (id, State.GetCard(id).Version)).ToList());
                    var later = new TriggeredAbility { Trigger = TriggerEvent.NextUpkeep, Effects = new Effect[] { still }, Text = $"{ctx.Source.Name}: damage for each card still exiled" };
                    State.AtPlayersNextEndStep.Add((new DelayedTrigger(ctx.Source.Id, later, ctx.Controller, 0) { SourceTransforms = ctx.Source.TransformCount },
                        ctx.Controller, State.TurnNumber, State.Step == Step.End));
                }
                break;
            }
            case DealDamageDivided dd:
            {
                // The division was announced as it was cast or put on the stack (rule 601.2d); damage assigned to a target
                // that became illegal isn't dealt (rule 608.2b). All of it is dealt at once.
                var division = await DivisionAtResolutionAsync(ctx, dd.TotalFrom is { } totalFrom ? Eval(totalFrom, ctx) : dd.Total, "Damage", dd.TrailingTargets);
                var parts = new List<DamagePart>();
                foreach (var (index, amount) in division)
                {
                    if (ctx.TargetAt(index) is not { } target || amount <= 0) continue;
                    parts.Add(target.Card is { } c ? new DamagePart(ctx.Source, State.GetCard(c), null, amount, false) : new DamagePart(ctx.Source, null, target.Player!.Value, amount, false));
                }
                await DealDamageEventAsync(parts);
                break;
            }
            case DealDamageDividedEvenly de:
            {
                // "Divided evenly, rounded down": no choice is made, so the damage is divided as it resolves, among the targets
                // still legal then.
                var legalTargets = Enumerable.Range(0, ctx.Targets.Count - ctx.TargetOffset).Select(i => ctx.TargetAt(i)).Where(t => t is not null).Select(t => t!.Value).ToList();
                if (legalTargets.Count == 0) break;
                int each = Eval(de.Total, ctx) / legalTargets.Count;
                if (each <= 0) break;
                await DealDamageEventAsync(legalTargets.Select(t => t.Card is { } c
                    ? new DamagePart(ctx.Source, State.GetCard(c), null, each, false)
                    : new DamagePart(ctx.Source, null, t.Player!.Value, each, false)).ToList());
                break;
            }
            case DestroyOneAtRandom dr:
            {
                // "Destroy one of them at random": among the targets still legal (rule 608.2b).
                var among = CardsFor(dr.Among, ctx).Where(c => c.Zone == Zone.Battlefield).ToList();
                if (among.Count == 0) break;
                var picked = among[Rng.Next(among.Count)];
                Emit(new ChoiceMade(ctx.Source.Id, picked.Name));
                await ApplyAsync(new Destroy(new Subject(SubjectKind.ChosenAtRandom)), ctx with { RandomPick = picked.Id });
                break;
            }
            case RedirectNextDamage rd:
                await CreateRedirectShieldAsync(rd, ctx);
                break;
            case KeepOneOfEachType ko:
            {
                // Every choice is made first, in turn order (by each player, or by the one who chooses for everyone); then all the
                // other permanents are sacrificed at the same time (rule 101.4). One permanent may be chosen for several of its types.
                var sacrificing = new List<CardId>();
                foreach (var player in State.ApnapOrder().Where(PlayersFor(ko.Who, ctx).ToList().Contains).ToList())
                {
                    var chooser = ko.Chooser is { } cs ? PlayersFor(cs, ctx).FirstOrDefault() : player;
                    var mine = State.PermanentsControlledBy(player).ToList();
                    var keep = new HashSet<CardId>();
                    foreach (var type in ko.Kinds ?? new[] { CardType.Artifact, CardType.Creature, CardType.Enchantment, CardType.Land, CardType.Planeswalker })
                    {
                        var ofType = mine.Where(c => c.Is(type)).ToList();
                        if (ofType.Count == 0) continue;
                        var options = ofType.Select(c => ViewBuilder.Card(State, c.Id, chooser)).ToList();
                        var pick = ofType.Count == 1 ? new[] { ofType[0].Id } : await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser),
                            new CardChoiceRequest(chooser == player ? $"Choose a {type.ToString().ToLowerInvariant()} to keep"
                                : $"Choose the {type.ToString().ToLowerInvariant()} {State.GetPlayer(player).Name} keeps", ctx.Source.Id, options, 1, 1, CardChoicePurpose.Keep));
                        Require(pick.Count == 1 && ofType.Any(c => c.Id == pick[0]), "Choose one to keep.");
                        keep.Add(pick[0]);
                        Emit(new ChoiceMade(ctx.Source.Id, $"{State.GetPlayer(player).Name} keeps {State.GetCard(pick[0]).Name}"));
                    }
                    sacrificing.AddRange(mine.Where(c => !keep.Contains(c.Id) && !(ko.NonlandOnly && c.Is(CardType.Land))).Select(c => c.Id));
                }
                BeginSimultaneous();
                foreach (var id in sacrificing)
                    if (State.GetCard(id).Zone == Zone.Battlefield) await SacrificePermanentAsync(id);
                EndSimultaneous();
                break;
            }
            default:
                return false;
        }
        return true;
    }
}
