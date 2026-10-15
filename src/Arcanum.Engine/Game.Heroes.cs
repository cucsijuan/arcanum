// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Effects and rules first needed by a set of hero, summon and job cards (see "Heroes, summons and jobs" in docs/card-scripts.md).
/// </summary>
public sealed partial class Game
{
    private async Task ApplyHeroesAsync(HeroesEffect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case FlipCoin flip:
            {
                var player = State.GetPlayer(ctx.Controller);
                bool first = player.CoinFlipTurn != State.TurnNumber;
                player.CoinFlipTurn = State.TurnNumber;
                bool won = (first && Has(ctx.Controller, Replacements.FirstCoinFlipEachTurnWins)) || Rng.Next(2) == 0;
                Emit(new ChoiceMade(ctx.Source.Id, won ? $"{player.Name} won the coin flip" : $"{player.Name} lost the coin flip"));
                if (won)
                    foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == ctx.Controller))
                        foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouWinCoinFlip))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(Player: ctx.Controller));
                await ApplyAllAsync(won ? flip.Won : flip.Lost, ctx);
                break;
            }
            case CopyAbility copy:
            {
                if (copy.What.Kind != SubjectKind.Target || ctx.TargetAt(copy.What.Index)?.StackObject is not { } id
                    || State.Stack.FirstOrDefault(s => s.Id == id) is not AbilityOnStack original || original.Ability.CantBeCopied) break;
                int copies = Eval(copy.Count, ctx);
                for (int i = 0; i < copies; i++)
                {
                    IReadOnlyList<ChosenTarget> targets = original.Targets;
                    if (original.Ability.Targets.Count > 0 && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                            new YesNoRequest($"Choose new targets for copy {i + 1} of {original.Ability.Text}?", ctx.Source.Id)))
                        targets = await ChooseTargetsAsync(ctx.Controller, original.Ability, original.Source, original.Ability.Text, canCancel: true, exactCount: original.Targets.Count)
                                  ?? original.Targets;
                    PushStack(original with { Controller = ctx.Controller, Targets = targets, Id = 0 });
                    Emit(new AbilityTriggered(ctx.Controller, original.Source, $"Copy: {original.Ability.Text}"));
                }
                break;
            }
            case AdditionalEndStep extra:
                if (!extra.OnlyIfFirst || (State.Step == Step.End && _endStepsThisTurn == 1)) _extraEndSteps++;
                break;
            case UnattachWhenControlLost uw:
                foreach (var card in CardsFor(uw.What, ctx).ToList())
                    State.UnattachWhenControlLost.Add((card.Id, card.Version, ctx.Controller, State.TurnNumber, ctx.Source.Id));
                break;
            case UnattachIfAttachedToYours ua:
                foreach (var card in CardsFor(ua.What, ctx).ToList())
                    if (card.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield, IsCreature: true } h && h.Controller == ctx.Controller)
                    {
                        card.AttachedTo = null;
                        Emit(new ChoiceMade(card.Id, "unattached"));
                        RecomputeContinuousEffects();
                    }
                break;
            case RevealAndAlternate ra:
                await RevealAndAlternateAsync(ra, ctx);
                break;
            case RememberChosen:
                if (!SourceOnBattlefield(ctx)) break;
                if (ctx.Results.Chosen.Count > 0)
                    ctx.Source.ChosenCreature = (ctx.Results.Chosen[0], State.GetCard(ctx.Results.Chosen[0]).Version);
                if (ctx.Results.ChosenPlayer is { } chosenPlayer) ctx.Source.ChosenPlayer = chosenPlayer;
                break;
            case ExileTop et:
                foreach (var player in PlayersFor(et.Who, ctx).ToList())
                    foreach (var id in State.GetPlayer(player).Library.Take(Math.Max(0, Eval(et.Count, ctx with { AffectedPlayer = player }))).ToList())
                    {
                        await MoveCardAsync(id, Zone.Exile);
                        if (State.GetCard(id).Zone != Zone.Exile) continue;
                        ctx.Results.Exiled.Add(id);
                        if (et.PlayableDuringLoreTurns)
                            State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, int.MaxValue)
                            {
                                LoreSource = (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version),
                            });
                    }
                break;
            case DoubleDamageToPlayerUntilYourNextTurn dd:
                foreach (var player in PlayersFor(dd.Who, ctx).ToList()) State.DamageDoubledAgainst.Add((player, ctx.Controller, ctx.Source.Id));
                break;
            case EachDealsDamageEqualToPower each:
            {
                var to = CardsFor(each.To, ctx).FirstOrDefault();
                var toPlayer = to is null ? PlayersFor(each.To, ctx).Cast<PlayerId?>().FirstOrDefault() : null;
                if (to is null && toPlayer is null) break;
                var parts = CardsFor(each.Sources, ctx).Where(c => c.IsCreature && c.Power > 0)
                    .Select(c => new DamagePart(c, to, toPlayer, c.Power, false) { Report = ctx.Results }).ToList();
                if (parts.Count > 0) await DealDamageEventAsync(parts);
                break;
            }
            case MultiplyPower mp:
                foreach (var card in CardsFor(mp.What, ctx).Where(c => c.IsCreature).ToList())
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, card.Power * (mp.Factor - 1), mp.Toughness ? card.Toughness * (mp.Factor - 1) : 0,
                        Array.Empty<Keyword>()) { Timestamp = NewTimestamp() });
                break;
            case EntersWithAdditionalCounters ewc:
            {
                int n = Eval(ewc.Count, ctx);
                if (n <= 0) break;
                foreach (var spell in (ewc.What.Kind == SubjectKind.Triggered && ctx.Trigger?.Subject is { } tid ? new[] { State.GetCard(tid) } : CardsFor(ewc.What, ctx))
                             .Where(c => c.Zone == Zone.Stack).ToList())
                    State.ExtraCountersOnEnter[(spell.Id, spell.Version)] = State.ExtraCountersOnEnter.GetValueOrDefault((spell.Id, spell.Version)) + n;
                break;
            }
            case ThisSpellOntoBattlefieldTransformed sb:
            {
                if (ctx.Source.Zone != Zone.Stack) break;
                await MoveCardAsync(ctx.Source.Id, Zone.Exile);
                if (ctx.Source.Zone != Zone.Exile) break;
                var counters = sb.WithCounter is { } kind ? new List<(CounterKind, int)> { (kind, 1) } : null;
                await MoveCardAsync(ctx.Source.Id, Zone.Battlefield, controller: ctx.Source.Owner, transformed: true, withCounters: counters);
                break;
            }
            case RemoveCounterFromAnyNumber rc:
            {
                var options = State.PermanentsControlledBy(ctx.Controller)
                    .Where(c => c.CounterCount(rc.Kind) > 0 && Matches(rc.Filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                if (options.Count == 0) break;
                var picked = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: remove a {CounterName(rc.Kind)} counter from any number of them", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, options.Count, CardChoicePurpose.Keep));
                Require(picked.Distinct().Count() == picked.Count && picked.All(id => options.Any(c => c.Id == id)), "Choose among the listed permanents.");
                foreach (var id in picked)
                {
                    RemoveCountersFrom(State.GetCard(id), rc.Kind, 1);
                    ctx.Results.CountersRemoved++;
                }
                break;
            }
            case ExileRandomPermanentCardCopyTapped:
            {
                var graveyard = State.GetPlayer(ctx.Controller).Graveyard;
                while (true)
                {
                    var permanents = graveyard.Where(id => State.GetCard(id).Types.IsPermanent()).ToList();
                    if (permanents.Count == 0) break;
                    var picked = State.GetCard(permanents[Rng.Next(permanents.Count)]);
                    await MoveCardAsync(picked.Id, Zone.Exile);
                    if (picked.Zone != Zone.Exile) break;
                    ctx.Results.Exiled.Add(picked.Id);
                    await ApplyAsync(new CreateTokenCopy(new Subject(SubjectKind.ExiledThisWay), 1) { Tapped = true, OnlyLastExiled = true }, ctx);
                    if (!picked.Is(CardType.Land)) break;
                }
                break;
            }
            case CountersUpTo cu:
                foreach (var card in CardsFor(cu.What, ctx).ToList())
                {
                    int n = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                        new NumberRequest($"{card.Name}: put how many {CounterName(cu.Kind)} counters on it?", card.Id, 0, cu.Max));
                    Require(n >= 0 && n <= cu.Max, $"Choose from 0 to {cu.Max}.");
                    if (n > 0) PutCounters(card, cu.Kind, n, ctx.Controller);
                }
                break;
            case AttachAnyNumber aa:
            {
                var host = CardsFor(aa.To, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (host is null) break;
                var options = State.PermanentsControlledBy(ctx.Controller)
                    .Where(c => c.Id != host.Id && c.AttachedTo != host.Id && Matches(aa.Filter, c, c.Controller, ctx.Source, ctx.Controller)
                                && (!c.HasSubtype("Equipment") || host.IsCreature)).ToList();
                if (options.Count == 0) break;
                var picked = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"Attach any number of them to {host.Name}", ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, options.Count, CardChoicePurpose.Keep));
                Require(picked.Distinct().Count() == picked.Count && picked.All(id => options.Any(c => c.Id == id)), "Choose among the listed permanents.");
                foreach (var id in picked)
                {
                    var card = State.GetCard(id);
                    card.AttachedTo = host.Id;
                    card.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                    ctx.Results.Attached++;
                    ctx.Results.AttachedCards.Add(id);
                }
                break;
            }
            case ShuffleFromGraveyard sg:
            {
                var player = State.GetPlayer(ctx.Controller);
                foreach (var id in player.Graveyard.Where(id => Matches(sg.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList())
                {
                    await MoveCardAsync(id, Zone.Library);
                    if (State.GetCard(id).Zone == Zone.Library) ctx.Results.Returned.Add(id);
                }
                Shuffle(player);
                break;
            }
            case LookHandLandsGraveyard lh:
                await LookHandLandsGraveyardAsync(Eval(lh.Count, ctx), ctx);
                break;
            case Meld:
            {
                if (!SourceOnBattlefield(ctx) || ctx.Source.Owner != ctx.Controller || ctx.Source.Controller != ctx.Controller
                    || ctx.Source.PrintedDefinition is not { MeldsWith: { } partnerName, MeldResult: not null }) break;
                var partner = State.PermanentsControlledBy(ctx.Controller)
                    .FirstOrDefault(c => c.Owner == ctx.Controller && c.IsCreature && c.Name == partnerName && c.MeldPartner is null);
                if (partner is null) break;
                var main = ctx.Source;
                BeginSimultaneous();
                await MoveCardAsync(main.Id, Zone.Exile);
                await MoveCardAsync(partner.Id, Zone.Exile);
                EndSimultaneous();
                // If either wasn't exiled, they stay where they are (rule 701.42c).
                if (main.Zone != Zone.Exile || partner.Zone != Zone.Exile) break;
                _pendingMeld[(main.Id, main.Version)] = partner;
                await MoveCardAsync(main.Id, Zone.Battlefield, controller: ctx.Controller);
                break;
            }
            case NinjutsuEnter:
            {
                if (ctx.Source.Zone is not (Zone.Hand or Zone.Command) || !IsSourceObject(ctx) || ctx.Ninja is not { } attack) break;
                await MoveCardAsync(ctx.Source.Id, Zone.Battlefield, controller: ctx.Controller, tapped: true);
                if (ctx.Source.Zone == Zone.Battlefield && State.Combat is { } combat)
                    combat.Attacks.Add(new AttackInfo { Attacker = ctx.Source.Id, Defender = attack.Defender, Planeswalker = attack.Planeswalker });
                break;
            }
            case HandToTop ht:
            {
                var who = ctx.Controller;
                var hand = State.GetPlayer(who).Hand.ToList();
                int n = Math.Min(ht.Count, hand.Count);
                if (n == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Put {n} card(s) from your hand on top of your library", ctx.Source.Id,
                    hand.Select(id => ViewBuilder.Card(State, id, who)).ToList(), n, n, CardChoicePurpose.Keep));
                Require(pick.Count == n && pick.Distinct().Count() == n && pick.All(hand.Contains), "Choose cards from your hand.");
                // In any order: the player orders them, the first chosen ending on top.
                foreach (var id in (await OrderAsync(who, pick.ToList(), "Choose the card to put on top next (the last one ends on top)", ctx.Source)))
                    await MoveCardAsync(id, Zone.Library);
                break;
            }
            case CommanderToHand:
            {
                var command = State.GetPlayer(ctx.Controller).Command.Select(State.GetCard).Where(c => c.IsCommander && c.Owner == ctx.Controller).ToList();
                if (command.Count == 0) break;
                var pick = command[0];
                if (command.Count > 1)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose the commander to put into your hand",
                        ctx.Source.Id, command.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.ToHand));
                    Require(chosen.Count == 1 && command.Any(c => c.Id == chosen[0]), "Choose one of your commanders.");
                    pick = State.GetCard(chosen[0]);
                }
                await MoveCardAsync(pick.Id, Zone.Hand);
                break;
            }
            case DamageRandomOpponent dr:
            {
                var opponents = State.ApnapOrder().Where(o => State.OpponentsOf(ctx.Controller).Contains(o)).ToList();
                if (opponents.Count == 0) break;
                var victim = opponents[Rng.Next(opponents.Count)];
                Emit(new ChoiceMade(ctx.Source.Id, $"{State.GetPlayer(victim).Name} chosen at random"));
                int amount = Eval(dr.Amount, ctx);
                if (amount <= 0) break;
                var walkers = State.PermanentsControlledBy(victim).Where(c => c.Is(CardType.Planeswalker)).ToList();
                Card? walker = null;
                if (walkers.Count > 0)
                {
                    var labels = walkers.Select(w => w.Name).Prepend(State.GetPlayer(victim).Name).ToList();
                    int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(
                        $"{ctx.Source.Name} deals {amount} damage to that player or a planeswalker they control", ctx.Source.Id, labels, OptionKind.Other));
                    Require(pick >= 0 && pick < labels.Count, "Choose who is dealt the damage.");
                    walker = pick == 0 ? null : walkers[pick - 1];
                }
                await DealDamageEventAsync(new List<DamagePart> { new(ctx.Source, walker, walker is null ? victim : null, amount, false) { Report = ctx.Results } });
                break;
            }
            case AddManaOfOneColor am:
            {
                int n = Eval(am.Count, ctx);
                if (n <= 0) break;
                if (am.Optional && !await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Add {n} mana of any one color?", ctx.Source.Id))) break;
                var colors = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green };
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                    new OptionRequest("Choose the color", ctx.Source.Id, new[] { "White", "Blue", "Black", "Red", "Green" }, OptionKind.Color));
                Require(i >= 0 && i < 5, "Choose a color.");
                for (int k = 0; k < n; k++)
                {
                    State.GetPlayer(ctx.Controller).ManaPool.Add(colors[i]);
                    Emit(new ManaAdded(ctx.Controller, colors[i], ctx.Source.Id));
                }
                ctx.Source.DoneThisTurn.Add(am);
                break;
            }
            case RollDie die:
            {
                int result = Rng.Next(die.Sides) + 1;
                ctx.Results.Rolled = result;
                Emit(new ChoiceMade(ctx.Source.Id, $"rolled {result} on a d{die.Sides}"));
                break;
            }
            case ExileUntilLesserCastFree eu:
                await ExileUntilLesserCastFreeAsync(eu, ctx);
                break;
            case RevealOnePerCardType rt:
                await RevealOnePerCardTypeAsync(rt.Count, ctx);
                break;
            case NextCreatureSpell { Type: { } type }:
                State.NextCreatureSpellBonus.Add((ctx.Controller, type, State.TurnNumber));
                break;
            case NextCreatureSpell next:
                State.NextCreatureSpell.Add((ctx.Controller, State.TurnNumber, next.Haste, next.Counters));
                break;
            case EachExilesTopPlayLesser:
            {
                var exiled = new List<Card>();
                foreach (var player in State.ApnapOrder().ToList())
                    if (State.GetPlayer(player).Library.FirstOrDefault() is var top && State.GetPlayer(player).Library.Count > 0)
                    {
                        await MoveCardAsync(top, Zone.Exile);
                        if (State.GetCard(top).Zone == Zone.Exile) exiled.Add(State.GetCard(top));
                    }
                if (exiled.FirstOrDefault(c => c.Owner == ctx.Controller) is not { } mine) break;
                foreach (var card in exiled.Where(c => c.Id == mine.Id || ManaValueOf(c) < ManaValueOf(mine)))
                    State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber, WithoutPaying: true));
                break;
            }
            case UnattachAtNextEndStep:
                foreach (var id in ctx.Results.AttachedCards)
                    State.AtNextEndStepEffects.Add((ctx.Source.Id, ctx.Controller, id, State.GetCard(id).Version,
                        new Effect[] { new Unattach(Subject.Triggered) }, null, ctx.Source.TransformCount));
                break;
            case Unattach un:
                foreach (var card in CardsFor(un.What, ctx).Where(c => c.AttachedTo is not null).ToList())
                {
                    card.AttachedTo = null;
                    Emit(new ChoiceMade(card.Id, "unattached"));
                    RecomputeContinuousEffects();
                }
                break;
            case CastExiledNow ce:
            {
                var player = State.GetPlayer(ctx.Controller);
                bool cast = false;
                foreach (var card in (ce.What.Kind == SubjectKind.ExiledThisWay ? ctx.Results.Exiled.Select(State.GetCard) : CardsFor(ce.What, ctx))
                             .Where(c => c.Zone == Zone.Exile && !c.Is(CardType.Land)).ToList())
                {
                    var permission = new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber) { CastOnly = true };
                    State.PlayableFromExile.Add(permission);
                    try
                    {
                        if (CanBeCast(card, ctx.Controller, flashExtra: false)
                            && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {card.Name}?", card.Id)))
                            cast = await CastSpellAsync(player, card.Id);
                    }
                    finally { State.PlayableFromExile.Remove(permission); }
                }
                if (!cast) await ApplyAllAsync(ce.Else, ctx);
                break;
            }
            case PlayableThisTurn pt:
                foreach (var card in (pt.What.Kind == SubjectKind.ExiledThisWay ? ctx.Results.Exiled.Select(State.GetCard) : CardsFor(pt.What, ctx)).Where(c => c.Zone == Zone.Exile).ToList())
                    State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber) { AnyManaType = pt.AnyMana });
                break;
            case SearchLibraryOrGraveyard s:
                await SearchLibraryOrGraveyardAsync(s, ctx);
                break;
            case PutSourceCountersOn p:
            {
                var lki = ctx.SourceLeftVersion is { } left ? ctx.Source.LastKnownOf(left) : null;
                var counters = lki?.Counters ?? (SourceOnBattlefield(ctx) ? ctx.Source.Counters : null);
                if (counters is null) break;
                var to = CardsFor(p.To, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (to is null) break;
                BeginSimultaneous();
                foreach (var (kind, count) in counters.Where(kv => kv.Value > 0).ToList()) PutCounters(to, kind, count, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case AttachFormerlyAttached a:
            {
                int version = ctx.SourceLeftVersion ?? ctx.Source.Version;
                var host = CardsFor(a.To, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (host is null) break;
                var options = State.Battlefield.Select(State.GetCard)
                    .Where(c => c.FormerHost is { } former && former.Card == ctx.Source.Id && former.Version == version
                                && (a.Filter is null || Matches(a.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller))
                                && (!c.HasSubtype("Equipment") || host.IsCreature) && c.Id != host.Id)
                    .ToList();
                if (options.Count == 0) break;
                var pick = options[0];
                if (options.Count > 1)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"Choose what to attach to {host.Name}", ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(chosen.Count == 1 && options.Any(c => c.Id == chosen[0]), "Choose one of the listed permanents.");
                    pick = State.GetCard(chosen[0]);
                }
                pick.AttachedTo = host.Id;
                pick.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                ctx.Results.Attached++;
                break;
            }
            default:
                throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
        }
    }

    private async Task SearchLibraryOrGraveyardAsync(SearchLibraryOrGraveyard s, EffectContext ctx)
    {
        var player = State.GetPlayer(ctx.Controller);
        var filter = s.Filter with { Controller = ControllerFilter.Any };
        bool Fits(CardId id) => Matches(filter, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller);
        var inGraveyard = player.Graveyard.Where(Fits).ToList();
        bool searchLibrary = player.Library.Count > 0 && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
            new YesNoRequest(inGraveyard.Count > 0 ? "Search your library too (not only your graveyard)?" : "Search your library?", ctx.Source.Id));
        var options = inGraveyard.Concat(searchLibrary ? player.Library.Where(Fits) : Array.Empty<CardId>()).ToList();
        if (searchLibrary) Look(ctx.Controller, player.Library); // searching is looking through the whole library (rule 701.23a)
        if (options.Count > 0)
        {
            // A card in the graveyard (a public zone) must be found if there is one (rule 701.19b covers hidden zones only).
            var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                $"{ctx.Source.Name}: choose a card to put into your hand", ctx.Source.Id, options.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(),
                inGraveyard.Count > 0 ? 1 : 0, 1, CardChoicePurpose.ToHand));
            Require(chosen.Count <= 1 && chosen.All(options.Contains) && (chosen.Count == 1 || inGraveyard.Count == 0), "Choose one of the listed cards.");
            foreach (var id in chosen)
            {
                Emit(new CardsRevealed(ctx.Controller, new[] { id }));
                await MoveCardAsync(id, Zone.Hand);
                ctx.Results.Found.Add(id);
            }
        }
        if (searchLibrary) Shuffle(player);
    }

    private async Task RevealAndAlternateAsync(RevealAndAlternate ra, EffectContext ctx)
    {
        var you = State.GetPlayer(ctx.Controller);
        var revealed = you.Library.Take(ra.Count).ToList();
        if (revealed.Count == 0) return;
        Emit(new CardsRevealed(ctx.Controller, revealed));
        RevealToAll(revealed);
        PlayerId? opponent = null;
        bool yourPick = true;
        while (revealed.Count > 0)
        {
            if (revealed.Count == 1 || yourPick)
            {
                var pick = revealed[0];
                if (revealed.Count > 1)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Put one of them into your hand",
                        ctx.Source.Id, revealed.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 1, 1, CardChoicePurpose.ToHand));
                    Require(chosen.Count == 1 && revealed.Contains(chosen[0]), "Choose one of the revealed cards.");
                    pick = chosen[0];
                }
                revealed.Remove(pick);
                await MoveCardAsync(pick, Zone.Hand);
                // "Then choose an opponent" after your first pick.
                opponent ??= await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose an opponent to put cards on the bottom of your library");
                if (opponent is null) yourPick = true; // no opponent: the rest come to you one by one
                else yourPick = false;
            }
            else
            {
                var chooser = opponent!.Value;
                var chosen = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest($"Put one of them on the bottom of {you.Name}'s library",
                    ctx.Source.Id, revealed.Select(id => ViewBuilder.Card(State, id, chooser, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(chosen.Count == 1 && revealed.Contains(chosen[0]), "Choose one of the revealed cards.");
                revealed.Remove(chosen[0]);
                you.Library.Remove(chosen[0]);
                you.Library.Add(chosen[0]);
                Emit(new ChoiceMade(chosen[0], "put on the bottom of the library"));
                yourPick = true;
            }
        }
    }

    private async Task LookHandLandsGraveyardAsync(int count, EffectContext ctx)
    {
        var player = State.GetPlayer(ctx.Controller);
        var top = player.Library.Take(count).ToList();
        if (top.Count == 0) return;
        Look(ctx.Controller, top);
        var views = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
        var toHand = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
            "You may put one of them into your hand", ctx.Source.Id, views, 0, 1, CardChoicePurpose.ToHand));
        Require(toHand.Count <= 1 && toHand.All(top.Contains), "Choose one of the cards.");
        var rest = top.Except(toHand).ToList();
        var lands = rest.Where(id => State.GetCard(id).Is(CardType.Land)).ToList();
        IReadOnlyList<CardId> toBattlefield = Array.Empty<CardId>();
        if (lands.Count > 0)
        {
            toBattlefield = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                "Put any number of land cards onto the battlefield tapped", ctx.Source.Id, lands.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(),
                0, lands.Count, CardChoicePurpose.ToBattlefield));
            Require(toBattlefield.Distinct().Count() == toBattlefield.Count && toBattlefield.All(lands.Contains), "Choose among the land cards.");
        }
        foreach (var id in toHand) await MoveCardAsync(id, Zone.Hand);
        BeginEnteringTogether();
        foreach (var id in toBattlefield) await MoveCardAsync(id, Zone.Battlefield, controller: ctx.Controller, tapped: true);
        EndEnteringTogether();
        BeginSimultaneous();
        foreach (var id in rest.Except(toBattlefield).ToList()) await MoveCardAsync(id, Zone.Graveyard);
        EndSimultaneous();
    }

    private async Task ExileUntilLesserCastFreeAsync(ExileUntilLesserCastFree eu, EffectContext ctx)
    {
        var player = State.GetPlayer(ctx.Controller);
        int below = ctx.Trigger?.Amount ?? 0; // the triggering spell's mana value
        var exiled = new List<CardId>();
        Card? hit = null;
        while (player.Library.Count > 0)
        {
            var top = State.GetCard(player.Library[0]);
            await MoveCardAsync(top.Id, Zone.Exile);
            if (top.Zone != Zone.Exile) break;
            exiled.Add(top.Id);
            if (!top.Is(CardType.Land) && ManaValueOf(top) < below && Matches(eu.Filter with { Controller = ControllerFilter.Any }, top, ctx.Controller, ctx.Source, ctx.Controller))
            {
                hit = top;
                break;
            }
        }
        Emit(new CardsRevealed(ctx.Controller, exiled));
        if (hit is not null && HasLegalTargets(CastingTargets(hit.Definition), ctx.Controller, hit.Id)
            && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {hit.Name} without paying its mana cost?", hit.Id)))
            await CastNowWithoutPayingAsync(player, hit.Id);
        var rest = exiled.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
        Rng.Shuffle(rest);
        foreach (var id in rest) await MoveCardAsync(id, Zone.Library, toBottom: true);
    }

    private async Task RevealOnePerCardTypeAsync(int count, EffectContext ctx)
    {
        var player = State.GetPlayer(ctx.Controller);
        var top = player.Library.Take(count).ToList();
        if (top.Count == 0) return;
        Emit(new CardsRevealed(ctx.Controller, top));
        RevealToAll(top);
        var types = new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Land, CardType.Planeswalker, CardType.Sorcery };
        var taken = new List<CardId>();
        foreach (var type in types)
        {
            var options = top.Where(id => !taken.Contains(id) && State.GetCard(id).Is(type)).ToList();
            if (options.Count == 0) continue;
            var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest($"You may put a {type.ToString().ToLowerInvariant()} card into your hand",
                ctx.Source.Id, options.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToHand));
            Require(pick.Count <= 1 && pick.All(options.Contains), "Choose one of the revealed cards.");
            taken.AddRange(pick);
        }
        foreach (var id in taken) await MoveCardAsync(id, Zone.Hand);
        var rest = top.Except(taken).ToList();
        Rng.Shuffle(rest);
        foreach (var id in rest)
        {
            player.Library.Remove(id);
            player.Library.Add(id);
        }
        PlacedInUnseenOrder(rest, ctx.Controller);
    }

    /// <summary>
    /// A permanent changed control: "whenever an opponent gains control of a permanent from you", and "when you lose control of that
    /// Equipment this turn, if it's attached to a creature you control, unattach it".
    /// </summary>
    private void CollectControlChangeTriggers(ControlChanged changed, PlayerId previous)
    {
        var card = State.GetCard(changed.Card);
        if (State.OpponentsOf(previous).Contains(changed.NewController))
            foreach (var watcher in PermanentsAndEmblemsOf(previous).ToList())
                Queue(watcher.Id, TriggerEvent.OpponentGainsControlFromYou, previous, new TriggerInfo(card.Id, card.Version, changed.NewController));
        foreach (var lost in State.UnattachWhenControlLost.Where(u => u.Card == card.Id && u.Version == card.Version && u.Player == previous && u.Turn == State.TurnNumber).ToList())
        {
            State.UnattachWhenControlLost.Remove(lost);
            if (card.AttachedTo is { } host && State.GetCard(host) is { IsCreature: true } h && h.Controller == previous)
                _pendingTriggers.Add(new PendingTrigger(lost.Source, UnattachIfYours, previous, new TriggerInfo(card.Id, card.Version, previous)));
        }
    }

    /// <summary>
    /// "If a land or Bird you control entering the battlefield causes a triggered ability of a permanent you control to trigger, that
    /// ability triggers an additional time": one more for each such permanent its controller controls.
    /// </summary>
    private int ExtraTriggersFromLandsAndBirds(Card source, TriggeredAbility ability, TriggerInfo? info)
    {
        if (source.Zone != Zone.Battlefield) return 0;
        var entering = ability.Trigger switch
        {
            TriggerEvent.EntersBattlefield => source,
            TriggerEvent.CreatureEnters or TriggerEvent.PermanentEnters or TriggerEvent.LandEnters when info?.Subject is { } subject => State.GetCard(subject),
            _ => null,
        };
        if (entering is null || entering.Zone != Zone.Battlefield || entering.Controller != source.Controller) return 0;
        if (!entering.Is(CardType.Land) && !entering.HasSubtype("Bird")) return 0;
        return Instances(source.Controller, Replacements.ExtraTriggersFromLandsAndBirdsEntering);
    }

    /// <summary>"You have protection from each of your opponents": nothing they control can target or damage that player (rule 702.16j).</summary>
    private bool PlayerProtectedFrom(PlayerId player, PlayerId other) =>
        player != other && State.OpponentsOf(player).Contains(other) && Has(player, Replacements.YouHaveProtectionFromOpponents);

    /// <summary>
    /// "If a creature dying causes a triggered ability of this creature or an emblem you own to trigger, that ability triggers an additional
    /// time" (a creature with it, as it last existed if it is the one dying).
    /// </summary>
    private int ExtraTriggersFromDying(Card source, TriggeredAbility ability, TriggerInfo? info)
    {
        var dying = ability.Trigger switch
        {
            TriggerEvent.Dies => source,
            TriggerEvent.CreatureDies or TriggerEvent.PermanentDies when info?.Subject is { } subject => State.GetCard(subject),
            _ => null,
        };
        if (dying is null || !WasCreature(dying)) return 0;
        // Its own "when this dies": as it last existed (the value the layers last gave it on the battlefield).
        if (source.Zone == Zone.Battlefield || dying == source) return source.DyingTriggerBonus;
        if (State.Emblems.Contains(source.Id)) return State.PermanentsControlledBy(source.Owner).Sum(c => c.DyingTriggerBonus);
        return 0;
    }

    /// <summary>Cards about to enter melded, by object (card, version): the other card of the pair.</summary>
    private readonly Dictionary<(CardId Card, int Version), Card> _pendingMeld = new();

    /// <summary>The other card of a melded permanent that left the battlefield goes to the same kind of zone, its owner's (rule 712.4c).</summary>
    private void MovePartnerWithMeld(Card partner, Zone to, bool toBottom)
    {
        partner.MeldedInto = null;
        partner.ResetStatus();
        partner.Zone = to;
        var owner = State.GetPlayer(partner.Owner);
        switch (to)
        {
            case Zone.Library:
                if (toBottom) owner.Library.Add(partner.Id);
                else owner.Library.Insert(0, partner.Id);
                break;
            case Zone.Battlefield or Zone.Stack:
                owner.Graveyard.Add(partner.Id); // can't happen (a melded permanent never moves there); keep the card somewhere
                partner.Zone = Zone.Graveyard;
                break;
            default:
                owner.GetZone(to).Add(partner.Id);
                break;
        }
    }

    /// <summary>"If a creature attacking causes a triggered ability of a permanent you control to trigger, that ability triggers an additional time."</summary>
    private int ExtraTriggersFromAttack(Card source, TriggeredAbility ability, TriggerInfo? info)
    {
        if (source.Zone != Zone.Battlefield) return 0;
        bool fromAttack = ability.Trigger is TriggerEvent.Attacks or TriggerEvent.CreatureAttacks or TriggerEvent.YouAttack or TriggerEvent.YouAttackPlayer
                              or TriggerEvent.CreaturesAttackOpponent or TriggerEvent.PlayerAttacks or TriggerEvent.EquippedAttacks
                          || (ability.Trigger == TriggerEvent.AttacksOrBlocks && info?.Defender is not null);
        if (!fromAttack) return 0;
        return State.PermanentsControlledBy(source.Controller).Count(c => c.HasTextAbilities && c.Definition.ExtraTriggersFromAttacking);
    }

    /// <summary>A permanent of the caster lets it cast this top card of its library by paying life equal to its mana value.</summary>
    private bool LibraryTopLifePermission(Card card, PlayerId caster) =>
        State.GetPlayer(caster).Library.FirstOrDefault() == card.Id
        && State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Definition.LibraryTopSpellsForLife && p.Definition.LibraryTopPlay is { } plays
            && (p.Definition.LibraryTopPlayIf is not { } playIf || Holds(playIf, caster, p)) && Matches(plays with { Controller = ControllerFilter.Any }, card, caster, p, caster));

    /// <summary>Another permanent of the caster lets it cast this top card of its library paying its costs as usual.</summary>
    private bool LibraryTopManaPermission(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && !p.Definition.LibraryTopSpellsForLife && p.Definition.LibraryTopPlay is { } plays
            && (p.Definition.LibraryTopPlayIf is not { } playIf || Holds(playIf, caster, p)) && Matches(plays with { Controller = ControllerFilter.Any }, card, caster, p, caster));

    /// <summary>Unblocked attackers the player controls (once blockers are declared, rule 509.1h).</summary>
    private List<AttackInfo> UnblockedAttackers(PlayerId player) =>
        State.Combat is { } combat && State.Step is Step.DeclareBlockers or Step.CombatDamage or Step.EndCombat
            ? combat.Attacks.Where(a => !a.IsBlocked && State.GetCard(a.Attacker).Controller == player).ToList()
            : new List<AttackInfo>();

    /// <summary>The 1/1 green Frog creature token "those tokens plus a 1/1 green Frog creature token" adds.</summary>
    private static readonly CardDefinition FrogToken = new()
    {
        Name = "Frog", Types = CardType.Creature, Subtypes = new[] { "Frog" }, Power = 1, Toughness = 1, Colors = new[] { "G" }, IsToken = true,
    };

    private static readonly TriggeredAbility SacrificeThatToken = new()
    {
        Trigger = TriggerEvent.NextUpkeep,
        Effects = new Effect[] { new SacrificeIt(Subject.Triggered) },
        Text = "Sacrifice that token.",
    };

    private static readonly TriggeredAbility UnattachIfYours = new()
    {
        Trigger = TriggerEvent.Reflexive,
        Effects = new Effect[] { new UnattachIfAttachedToYours(Subject.Triggered) },
        Text = "When you lose control of that Equipment this turn, if it's attached to a creature you control, unattach it.",
    };

    /// <summary>Life a replacement effect gives as part of a zone change ("instead exile it and you gain 2 life"), gained right after the move.</summary>
    private readonly List<(PlayerId Player, int Amount)> _pendingLifeGains = new();

    private async Task GainPendingLifeAsync()
    {
        while (_pendingLifeGains.Count > 0)
        {
            var (player, amount) = _pendingLifeGains[0];
            _pendingLifeGains.RemoveAt(0);
            await GainLifeAsync(player, amount);
        }
    }

    // ------------------------------------------------------------------ quantities

    private int EvalHeroes(Quantity q, EffectContext ctx) => q.Kind switch
    {
        QuantityKind.ManaValueSum => State.Battlefield.Select(State.GetCard)
            .Where(c => Matches(q.Filter ?? ObjectFilter.Anything, c, c.Controller, ctx.Source, ctx.Controller)).Sum(ManaValueOf),
        QuantityKind.ControlGainedThisWay => ctx.Results.ControlGained.Select(State.GetCard).Count(c => c.Zone == Zone.Battlefield),
        QuantityKind.FoundManaValue => ctx.Results.Found.Count > 0 ? ManaValueOf(State.GetCard(ctx.Results.Found[^1])) : 0,
        QuantityKind.DiscardedManaValue => ctx.Results.Discarded.Select(State.GetCard).Sum(ManaValueOf),
        QuantityKind.CardTypesAmongDiscarded => new[] { CardType.Artifact, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Land, CardType.Planeswalker,
            CardType.Sorcery, CardType.Battle, CardType.Kindred }.Count(t => ctx.Results.Discarded.Any(id => State.GetCard(id).Is(t))),
        QuantityKind.CreaturesDiedThisTurn => State.CreaturesDiedThisTurn,
        QuantityKind.Rolled => ctx.Results.Rolled,
        QuantityKind.OpponentsDealtCombatDamage => State.OpponentsOf(ctx.Controller).Count(o => State.GetPlayer(o).CombatDamageTakenThisTurn > 0),
        QuantityKind.CountersRemovedThisWay => ctx.Results.CountersRemoved,
        QuantityKind.FoundMatching => ctx.Results.Found.Select(State.GetCard)
            .Count(c => q.Filter is null || Matches(q.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)),
        _ => throw new NotSupportedException($"Quantity {q.Kind} is not implemented."),
    };
}
