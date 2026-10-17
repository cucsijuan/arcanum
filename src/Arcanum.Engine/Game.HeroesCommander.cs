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
/// Keywords, keyword actions and rules first needed by the heroes set's commander decks (see "Heroes commander decks" in
/// docs/card-scripts.md).
/// </summary>
public sealed partial class Game
{
    /// <summary>
    /// Evolve (rule 702.100a): "Whenever a creature you control enters, if that creature's power is greater than this creature's
    /// power and/or that creature's toughness is greater than this creature's toughness, put a +1/+1 counter on this creature."
    /// </summary>
    private static readonly TriggeredAbility EvolveTrigger = new()
    {
        Trigger = TriggerEvent.CreatureEnters,
        Condition = new Evolves(),
        Effects = new Effect[] { new AddCounters(1, Subject.Self) },
        Text = "Evolve: put a +1/+1 counter on this creature.",
    };

    /// <summary>Evolve triggers of the creatures already under the entering creature's controller's control (and those entering with it).</summary>
    private void QueueEvolve(Card entering)
    {
        foreach (var evolver in State.PermanentsControlledBy(entering.Controller).Where(c => c.Id != entering.Id && c.IsCreature && c.Has(Keyword.Evolve)).ToList())
            AddPending(evolver.Id, EvolveTrigger, evolver.Controller, new TriggerInfo(entering.Id, entering.Version, entering.Controller));
    }

    private bool EvolvesHolds(EffectContext ctx)
    {
        if (ctx.Trigger is not { Subject: { } id } about) return false;
        var entered = State.GetCard(id);
        int version = ctx.SourceVersion ?? ctx.Source.Version;
        return TriggeredPowerOf(entered, about.SubjectVersion) > TriggeredPowerOf(ctx.Source, version)
            || TriggeredToughnessOf(entered, about.SubjectVersion) > TriggeredToughnessOf(ctx.Source, version);
    }

    /// <summary>Improvise (702.126): printed on the spell, or given to it by a permanent its caster controls ("nonartifact spells you cast have improvise").</summary>
    private bool HasImprovise(Card card, PlayerId caster) =>
        card.Definition.Improvise
        || State.PermanentsControlledBy(caster).Any(p => p.HasTextAbilities && p.Abilities.OfType<SpellsHaveImprovise>().Any(g =>
            Matches(g.Spells with { Controller = ControllerFilter.Any }, card, caster, p, caster)));

    /// <summary>"At the beginning of the end step, sacrifice this creature." (granted to a creature cast with a free-cast effect).</summary>
    private static readonly TriggeredAbility SacrificeThisCreatureAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new SacrificeIt(Subject.Self) },
        Text = "At the beginning of the end step, sacrifice this creature.",
    };

    private async Task ApplyHeroesCommanderAsync(HeroesCommanderEffect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case Adapt adapt:
                foreach (var card in CardsFor(adapt.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    if (card.CounterCount(CounterKind.PlusOnePlusOne) == 0) PutCounters(card, CounterKind.PlusOnePlusOne, Eval(adapt.Count, ctx), ctx.Controller);
                break;
            case Monstrosity monstrosity:
                foreach (var card in CardsFor(monstrosity.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    if (card.Monstrous) continue;
                    PutCounters(card, CounterKind.PlusOnePlusOne, Eval(monstrosity.Count, ctx), ctx.Controller);
                    card.Monstrous = true;
                    Emit(new ChoiceMade(card.Id, "became monstrous"));
                }
                break;
            case Explore explore:
                foreach (var (card, controller) in StillActing(explore.What, ctx))
                    await ExploreAsync(card, controller, ctx);
                break;
            case Connive connive:
            {
                // 701.50e: in turn order, each player has the permanents they control connive one at a time, in the order they choose.
                var conniving = StillActing(connive.What, ctx);
                foreach (var player in State.ApnapOrder())
                {
                    var theirs = conniving.Where(x => x.Controller == player).Select(x => x.Card).ToList();
                    while (theirs.Count > 0)
                    {
                        var next = theirs[0];
                        if (theirs.Count > 1)
                        {
                            var picked = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                                $"{ctx.Source.Name}: choose the next creature to connive", ctx.Source.Id,
                                theirs.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 1, 1, CardChoicePurpose.Order));
                            Require(picked.Count == 1 && theirs.Any(c => c.Id == picked[0]), "Choose one of the listed creatures.");
                            next = theirs.First(c => c.Id == picked[0]);
                        }
                        theirs.Remove(next);
                        await ConniveAsync(next, player, ctx);
                    }
                }
                break;
            }
            case SacrificeAllBut sab:
            {
                var doomed = new List<Card>();
                var kept = new List<Card>();
                foreach (var player in State.ApnapOrder().Where(PlayersFor(sab.Who, ctx).ToList().Contains).ToList())
                {
                    var theirs = State.PermanentsControlledBy(player)
                        .Where(c => Matches(sab.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (theirs.Count == 0) continue;
                    int most = Math.Min(sab.Keep, theirs.Count);
                    int least = sab.Exactly ? most : 0;
                    var keep = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                        sab.Exactly ? $"{ctx.Source.Name}: choose {most} to keep; you sacrifice the rest" : $"{ctx.Source.Name}: choose up to {sab.Keep} to keep; you sacrifice the rest", ctx.Source.Id,
                        theirs.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), least, most, CardChoicePurpose.Keep));
                    Require(keep.Count >= least && keep.Count <= most && keep.Distinct().Count() == keep.Count && keep.All(id => theirs.Any(c => c.Id == id)), "Choose permanents you control.");
                    doomed.AddRange(theirs.Where(c => !keep.Contains(c.Id)));
                    kept.AddRange(theirs.Where(c => keep.Contains(c.Id)));
                }
                if (sab.PutCounter is { } keptCounter)
                {
                    long stamp = NewTimestamp();
                    foreach (var card in kept.Where(c => c.Zone == Zone.Battlefield))
                    {
                        PutCounters(card, keptCounter, 1, ctx.Controller);
                        if (sab.KeptCantAttackYouWhileCounter)
                            State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>())
                                { CantAttackPlayerAndPlaneswalkers = ctx.Controller, WhileCounter = keptCounter, Timestamp = stamp });
                    }
                    RecomputeContinuousEffects();
                }
                BeginSimultaneous();
                foreach (var card in doomed.Where(c => c.Zone == Zone.Battlefield))
                {
                    ctx.Results.Sacrificed.Add(card.Id);
                    await SacrificePermanentAsync(card.Id);
                }
                EndSimultaneous();
                break;
            }
            case OpponentSplitsExiled:
            {
                var exiled = ctx.Results.ExiledForCost.Select(State.GetCard).Where(c => c.Zone == Zone.Exile).ToList();
                if (exiled.Count == 0) break;
                var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, $"{ctx.Source.Name}: choose an opponent to choose one of the exiled cards");
                if (opponent is not { } chooser) break;
                var picked = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose the card that goes to the bottom of {State.GetPlayer(ctx.Controller).Name}'s library (the other returns to the battlefield)",
                    ctx.Source.Id, exiled.Select(c => ViewBuilder.Card(State, c.Id, chooser)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(picked.Count == 1 && exiled.Any(c => c.Id == picked[0]), "Choose one of the exiled cards.");
                Emit(new ChoiceMade(picked[0], $"chosen by {State.GetPlayer(chooser).Name}: to the bottom of the library"));
                await MoveCardAsync(picked[0], Zone.Library, toBottom: true);
                BeginEnteringTogether();
                foreach (var other in exiled.Where(c => c.Id != picked[0]))
                {
                    await MoveCardAsync(other.Id, Zone.Battlefield, controller: ctx.Controller, tapped: true);
                    if (other.Zone == Zone.Battlefield) ctx.Results.Entered.Add(other.Id);
                }
                EndEnteringTogether();
                break;
            }
            case DistributeSourceCounters dsc:
            {
                var source = ctx.Source;
                while (SourceOnBattlefield(ctx) && source.CounterCount(dsc.Kind) > 0)
                {
                    var others = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature && c.Id != source.Id).ToList();
                    if (others.Count == 0) break;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{source.Name}: choose a creature to move {CounterName(dsc.Kind)} counters onto (or none to stop)", source.Id,
                        others.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    if (pick.Count == 0) break;
                    Require(pick.Count == 1 && others.Any(c => c.Id == pick[0]), "Choose one of the creatures.");
                    int n = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller), new NumberRequest(
                        $"Move how many {CounterName(dsc.Kind)} counters onto {State.GetCard(pick[0]).Name}?", source.Id, 1, source.CounterCount(dsc.Kind)));
                    Require(n >= 1 && n <= source.CounterCount(dsc.Kind), "Choose how many counters to move.");
                    RemoveCountersFrom(source, dsc.Kind, n);
                    PutCounters(State.GetCard(pick[0]), dsc.Kind, n, ctx.Controller);
                    await ResolvePendingCountersAsync();
                }
                break;
            }
            case AttackTaxUntilYourNextTurn tax:
                State.AttackTaxesUntilNextTurn.Add((ctx.Controller, tax.Tax));
                Emit(new ChoiceMade(ctx.Source.Id, $"until {State.GetPlayer(ctx.Controller).Name}'s next turn, attacking them costs {tax.Tax} for each creature"));
                break;
            case WhenDiesThisTurn wd:
                foreach (var card in CardsFor(wd.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.WhenDiesThisTurn.Add((card.Id, card.Version, State.TurnNumber, ctx.Source.Id, ctx.Controller,
                        new TriggeredAbility { Trigger = TriggerEvent.Dies, Effects = wd.Effects, Text = wd.Text }));
                break;
            case PutExiledWithThis pew:
            {
                var exiled = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id
                        && w.Version == (ctx.SourceVersion ?? ctx.Source.Version)
                        && Matches(pew.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)).ToList();
                if (exiled.Count == 0) break;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: you may put one of these onto the battlefield", ctx.Source.Id,
                    exiled.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                Require(pick.Count <= 1 && pick.All(id => exiled.Any(c => c.Id == id)), "Choose one of the exiled cards.");
                if (pick.Count == 0) break;
                await MoveCardAsync(pick[0], Zone.Battlefield, controller: ctx.Controller, withCounters: pew.CounterKinds.Select(k => (k, 1)).ToList());
                if (State.GetCard(pick[0]).Zone == Zone.Battlefield) ctx.Results.Entered.Add(pick[0]);
                break;
            }
            case ExileWithSameNameTokens ex:
            {
                var first = CardsFor(ex.What, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (first is null) break;
                var all = State.PermanentsControlledBy(first.Controller).Where(c => c.Id == first.Id || (c.Definition.IsToken && c.Name == first.Name)).ToList();
                BeginSimultaneous();
                foreach (var card in all)
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    if (card.Zone == Zone.Exile) ctx.Results.Exiled.Add(card.Id);
                }
                EndSimultaneous();
                break;
            }
            case ChooseOneOf one:
            {
                var among = CardsFor(one.Among, ctx).Distinct().ToList();
                ctx.Results.Found.Clear();
                if (among.Count == 0) break;
                var pick = among.Count == 1 ? new[] { among[0].Id } : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose one of them", ctx.Source.Id, among.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(pick.Count == 1 && among.Any(c => c.Id == pick[0]), "Choose one of them.");
                ctx.Results.Found.Add(pick[0]);
                break;
            }
            case OpponentsChooseFromYourGraveyard ocg:
            {
                var chosen = new List<CardId>();
                foreach (var opponent in State.ApnapOrder().Where(State.OpponentsOf(ctx.Controller).Contains).ToList())
                {
                    var options = State.GetPlayer(ctx.Controller).Graveyard.Where(id => !chosen.Contains(id))
                        .Where(id => Matches(ocg.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0) break;
                    var pick = await ControllerOf(opponent).ChooseCardsAsync(ViewFor(opponent), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose a card in {State.GetPlayer(ctx.Controller).Name}'s graveyard (it returns to the battlefield under their control)", ctx.Source.Id,
                        options.Select(id => ViewBuilder.Card(State, id, opponent)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(pick.Count == 1 && options.Contains(pick[0]), "Choose one of the listed cards.");
                    chosen.Add(pick[0]);
                    Emit(new ChoiceMade(pick[0], $"chosen by {State.GetPlayer(opponent).Name}"));
                }
                BeginEnteringTogether();
                foreach (var id in chosen.Where(id => State.GetCard(id).Zone == Zone.Graveyard))
                {
                    await MoveCardAsync(id, Zone.Battlefield, controller: ctx.Controller);
                    if (State.GetCard(id).Zone == Zone.Battlefield) ctx.Results.Entered.Add(id);
                }
                EndEnteringTogether();
                break;
            }
            case ExileRandomFromOpponentsGraveyards:
                foreach (var opponent in State.ApnapOrder().Where(State.OpponentsOf(ctx.Controller).Contains).ToList())
                {
                    var graveyard = State.GetPlayer(opponent).Graveyard;
                    if (graveyard.Count == 0) continue;
                    var random = graveyard[Rng.Next(graveyard.Count)];
                    await MoveCardAsync(random, Zone.Exile);
                    if (State.GetCard(random).Zone == Zone.Exile) ctx.Results.Exiled.Add(random);
                }
                break;
            case CastExiledFreeAnyNumber cef:
            {
                var cast = new List<(Card Card, PlayerId Owner, int ManaValue)>();
                while (true)
                {
                    var castable = ctx.Results.Exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && !c.Is(CardType.Land) && cast.All(x => x.Card != c)
                        && HasLegalTargets(CastingTargets(c.Definition), ctx.Controller, c.Id)).ToList();
                    if (castable.Count == 0) break;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: you may cast one of these without paying its mana cost (or none to stop)", ctx.Source.Id,
                        castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    if (pick.Count == 0) break;
                    Require(castable.Any(c => c.Id == pick[0]), "Choose one of the exiled cards.");
                    var card = State.GetCard(pick[0]);
                    int value = ManaValueOf(card);
                    if (await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), card.Id)) cast.Add((card, card.Owner, value));
                    else break;
                }
                // "Then each player who owns a spell you cast this way loses life equal to its mana value."
                if (cef.OwnersLoseLife)
                    foreach (var (_, owner, manaValue) in cast)
                        await ApplyAsync(new LoseLife(manaValue, new Subject(SubjectKind.FixedPlayer) { Player = owner }), ctx);
                break;
            }
            case CastReboundedFree:
            {
                // The card rebound exiled, still that object (rule 400.7), may be cast without paying its mana cost.
                var card = ctx.Source;
                if (card.Zone != Zone.Exile || ctx.Trigger?.Subject != card.Id || ctx.Trigger.SubjectVersion != card.Version) break;
                var owner = State.GetPlayer(card.Owner);
                if (!HasLegalTargets(CastingTargets(card.Definition), owner.Id, card.Id)
                    || !await ControllerOf(owner.Id).ChooseYesNoAsync(ViewFor(owner.Id), new YesNoRequest($"Rebound: cast {card.Name} without paying its mana cost?", card.Id))) break;
                await CastNowWithoutPayingAsync(owner, card.Id);
                break;
            }
            case BecomeCopy becomeCopy:
            {
                var target = CardsFor(becomeCopy.Target, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (target is null) break;
                var copiable = target.Definition;
                foreach (var card in CardsFor(becomeCopy.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    card.CopiedDefinition = copiable with { Name = becomeCopy.ExceptName ?? copiable.Name };
                    card.CopyEndsAtCleanup = true;
                }
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown effect {effect.GetType().Name}");
        }
    }

    /// <summary>
    /// The permanents an effect tells to do something that they still do after leaving the battlefield (explore, connive), with the
    /// player who does it: their controller, or the one they last had (rules 701.44c, 701.50c).
    /// </summary>
    private List<(Card Card, PlayerId Controller)> StillActing(Subject what, EffectContext ctx)
    {
        var acting = CardsFor(what, ctx).Select(c => (c, c.Zone == Zone.Battlefield ? c.Controller : c.LastKnownInfo?.Controller ?? c.Owner)).ToList();
        // "It explores" about a creature that has left since its ability triggered: still the object it was.
        if (acting.Count == 0 && what.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } id } about && State.GetCard(id) is var gone
            && gone.Version != about.SubjectVersion && gone.LastKnownOf(about.SubjectVersion) is { } was)
            acting.Add((gone, was.Controller));
        return acting;
    }

    /// <summary>The exploring permanent still being the object that was told to explore, on the battlefield (it gets the counter).</summary>
    private static bool StillThere(Card card, PlayerId controller) => card.Zone == Zone.Battlefield && card.Controller == controller;

    private async Task ExploreAsync(Card explorer, PlayerId controller, EffectContext ctx)
    {
        int version = explorer.Version;
        var player = State.GetPlayer(controller);
        Card? top = player.Library.Count > 0 ? State.GetCard(player.Library[0]) : null;
        if (top is not null) Emit(new CardsRevealed(controller, new[] { top.Id }));
        if (top is not null && top.Is(CardType.Land))
        {
            await MoveCardAsync(top.Id, Zone.Hand);
            Emit(new ChoiceMade(explorer.Id, $"explored: {top.Name} into the hand"));
            return;
        }
        if (explorer.Version == version && StillThere(explorer, controller)) PutCounters(explorer, CounterKind.PlusOnePlusOne, 1, controller);
        Emit(new ChoiceMade(explorer.Id, top is null ? "explored: no card revealed" : $"explored: revealed {top.Name}"));
        if (top is not null && top.Zone == Zone.Library
            && await ControllerOf(controller).ChooseYesNoAsync(ViewFor(controller), new YesNoRequest($"Explore: put {top.Name} into your graveyard?", top.Id)))
            await MoveCardAsync(top.Id, Zone.Graveyard);
    }

    private async Task ConniveAsync(Card conniver, PlayerId controller, EffectContext ctx)
    {
        int version = conniver.Version;
        await DrawAsync(controller, 1);
        var discarded = await DiscardAsync(controller, 1, controller);
        bool nonland = discarded.Any(id => !State.GetCard(id).Is(CardType.Land));
        if (nonland && conniver.Version == version && StillThere(conniver, controller)) PutCounters(conniver, CounterKind.PlusOnePlusOne, 1, controller);
        Emit(new ChoiceMade(conniver.Id, "connived"));
    }

    // ------------------------------------------------------------------ convoke (702.51)

    /// <summary>Untapped creatures the caster controls that can be tapped for convoke, those without mana abilities first.</summary>
    private List<Card> ConvokeCandidates(Card spell, PlayerId caster) =>
        State.PermanentsControlledBy(caster).Where(c => c.IsCreature && !c.Tapped && c.Id != spell.Id).OrderBy(c => c.ManaOptions.Count > 0 ? 1 : 0).ToList();

    /// <summary>
    /// What tapping <paramref name="creatures"/> for convoke pays of <paramref name="cost"/> (702.51a): each creature pays for one colored
    /// symbol of one of its colors (a Phyrexian or hybrid one too) or for {1}. Colored symbols are paid first (as many as the creatures'
    /// colors allow, a maximum matching), then generic mana. Returns the rest of the cost and the creatures that paid for something;
    /// creatures that can pay for nothing more are left out.
    /// </summary>
    private static (ManaCost Remaining, List<Card> Used) ConvokePays(ManaCost cost, IReadOnlyList<Card> creatures)
    {
        // Colored symbols: (list 0 = Phyrexian, 1 = plain, 2 = hybrid; index; colors that pay it). Phyrexian first (saves life), hybrid last.
        var units = new List<(int List, int Index, string[] Colors)>();
        for (int i = 0; i < cost.Phyrexian.Count; i++)
            if (cost.Phyrexian[i] != ManaType.Colorless) units.Add((0, i, new[] { cost.Phyrexian[i].ToSymbol().ToString() }));
        for (int i = 0; i < cost.Pips.Count; i++)
            if (cost.Pips[i] != ManaType.Colorless) units.Add((1, i, new[] { cost.Pips[i].ToSymbol().ToString() }));
        for (int i = 0; i < cost.Hybrid.Count; i++)
            units.Add((2, i, new[] { cost.Hybrid[i].First, cost.Hybrid[i].Second }.Where(t => t != ManaType.Colorless).Select(t => t.ToSymbol().ToString()).ToArray()));
        var owner = Enumerable.Repeat(-1, units.Count).ToArray();
        var paysUnit = Enumerable.Repeat(-1, creatures.Count).ToArray();
        bool Augment(int creature, bool[] seen)
        {
            for (int u = 0; u < units.Count; u++)
            {
                if (seen[u] || !units[u].Colors.Intersect(creatures[creature].Colors).Any()) continue;
                seen[u] = true;
                if (owner[u] < 0 || Augment(owner[u], seen))
                {
                    owner[u] = creature;
                    paysUnit[creature] = u;
                    return true;
                }
            }
            return false;
        }
        for (int c = 0; c < creatures.Count; c++) Augment(c, new bool[units.Count]);
        var used = new List<Card>();
        int generic = cost.Generic;
        for (int c = 0; c < creatures.Count; c++)
        {
            if (paysUnit[c] >= 0) used.Add(creatures[c]);
            else if (generic > 0) { generic--; used.Add(creatures[c]); }
        }
        bool Paid(int list, int index) => units.Select((x, u) => (x, u)).Any(p => p.x.List == list && p.x.Index == index && owner[p.u] >= 0);
        var rest = new ManaCost(generic,
            cost.Pips.Where((_, i) => !Paid(1, i)).ToList(),
            cost.Hybrid.Where((_, i) => !Paid(2, i)).ToList(), cost.XCount)
        {
            Phyrexian = cost.Phyrexian.Where((_, i) => !Paid(0, i)).ToList(),
        };
        // Keep the creatures in the order given (their colored symbol first is irrelevant to the player).
        return (rest, creatures.Where(used.Contains).ToList());
    }

    /// <summary>
    /// Whether the cost can be paid with convoke helping (702.51): every creature without a mana ability is tapped if it pays for
    /// something; creatures with mana abilities are added one at a time when the rest still can't be paid.
    /// </summary>
    private bool CanPayWithConvoke(Card card, PlayerId caster, CastingChoices c, ManaCost total)
    {
        if (CanPayCastingMana(card, caster, c, total)) return true;
        var candidates = ConvokeCandidates(card, caster);
        int free = candidates.Count(x => x.ManaOptions.Count == 0);
        for (int n = free; n <= candidates.Count; n++)
        {
            var (rest, used) = ConvokePays(total, candidates.Take(n).ToList());
            if (CanPayCastingMana(card, caster, c with { Convoked = used.Select(u => u.Id).ToList() }, rest)) return true;
        }
        return false;
    }

    /// <summary>
    /// Convoke while paying (702.51a): the player chooses creatures to tap, each paying for a colored symbol of its color or {1}. A choice
    /// that taps a creature paying for nothing, or that leaves the rest unpayable, is asked again (after a few tries, the engine's own
    /// choice is used). Returns the rest of the cost; the tapped creatures are remembered on the spell (702.51c).
    /// </summary>
    private async Task<(ManaCost Remaining, CastingChoices Choices)> ConvokeAsync(PlayerId player, Card card, CastingChoices choices, ManaCost cost)
    {
        var candidates = ConvokeCandidates(card, player).Where(c => ConvokePays(cost, new[] { c }).Used.Count == 1).ToList();
        if (candidates.Count == 0) return (cost, choices);
        int most = Math.Min(cost.ManaValue, candidates.Count);
        bool needed = !CanPayCastingMana(card, player, choices, cost);
        List<Card> picked;
        for (int attempt = 0; ; attempt++)
        {
            var ids = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                attempt == 0 ? $"Convoke: tap up to {most} creatures (each pays for {{1}} or one mana of its color)" : $"Convoke: that doesn't pay for the spell; choose again (up to {most})",
                card.Id, candidates.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 0, most, CardChoicePurpose.Sacrifice));
            Require(ids.Count <= most && ids.Distinct().Count() == ids.Count && ids.All(id => candidates.Any(c => c.Id == id)), "Choose untapped creatures you control.");
            picked = ids.Select(State.GetCard).ToList();
            var (rest, used) = ConvokePays(cost, picked);
            if (used.Count == picked.Count && CanPayCastingMana(card, player, choices with { Convoked = ids.ToList() }, rest)) break;
            if (attempt >= 2)
            {
                // The engine's own choice: the creatures without mana abilities, then the others as needed.
                picked = new();
                if (needed)
                    for (int n = candidates.Count(c => c.ManaOptions.Count == 0); n <= candidates.Count; n++)
                    {
                        var (r, u) = ConvokePays(cost, candidates.Take(n).ToList());
                        if (!CanPayCastingMana(card, player, choices with { Convoked = u.Select(x => x.Id).ToList() }, r)) continue;
                        picked = u;
                        break;
                    }
                break;
            }
        }
        if (picked.Count == 0) return (cost, choices);
        var paid = ConvokePays(cost, picked);
        foreach (var creature in paid.Used)
        {
            Tap(creature);
            card.Convokers.Add((creature.Id, creature.Version));
        }
        Emit(new ChoiceMade(card.Id, $"convoked with {string.Join(", ", paid.Used.Select(c => c.Name))}"));
        return (paid.Remaining, choices with { Convoked = paid.Used.Select(c => c.Id).ToList() });
    }

    // ------------------------------------------------------------------ rebound (702.88)

    private static readonly TriggeredAbility ReboundUpkeep = new()
    {
        Trigger = TriggerEvent.YourUpkeep,
        Effects = new Effect[] { new CastReboundedFree() },
        Text = "Rebound: you may cast this card from exile without paying its mana cost.",
    };

    /// <summary>
    /// Rebound (702.88a), as a spell cast from its controller's hand resolves: it is exiled instead of going to the graveyard, and at the
    /// beginning of that player's next upkeep they may cast it from exile without paying its mana cost. False when it doesn't apply.
    /// </summary>
    private async Task<bool> ReboundAsync(Card spell)
    {
        if (!spell.CastFromHand || !spell.Definition.Keywords.Contains("Rebound", StringComparer.OrdinalIgnoreCase)) return false;
        var caster = spell.Controller;
        await MoveCardAsync(spell.Id, Zone.Exile);
        if (spell.Zone == Zone.Exile)
        {
            State.AtNextUpkeep.Add(new DelayedTrigger(spell.Id, ReboundUpkeep, caster, 0) { OnlyAtUpkeepOf = caster, AboutCard = (spell.Id, spell.Version) });
            Emit(new ChoiceMade(spell.Id, "rebound: exiled until its caster's next upkeep"));
        }
        Emit(new SpellResolved(spell.Id));
        return true;
    }

    // ------------------------------------------------------------------ blitz (702.152)

    private static readonly TriggeredAbility BlitzDraw = new()
    {
        Trigger = TriggerEvent.Dies,
        Effects = new Effect[] { new DrawCards(1, Subject.You) },
        Text = "Blitz: this was put into a graveyard from the battlefield — draw a card.",
    };

    /// <summary>"You may cast this card from your graveyard using its blitz ability."</summary>
    private static bool BlitzFromGraveyard(Card card) =>
        card.Zone == Zone.Graveyard && card.Definition.BlitzFromGraveyard && card.Definition.Blitz is not null && !card.Is(CardType.Land);

    /// <summary>Another rule lets the card be cast from its owner's graveyard (paying its mana cost or another cost).</summary>
    private bool GraveyardCastByOtherPermission(Card card) =>
        State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version) || HasGraveyardCastRight(card) || HadesPermission(card.Owner)
        || Has(card.Owner, Replacements.PermanentsFromGraveyard) || card.Definition.GraveyardCastCost is not null
        || (card.Definition.GraveyardCastIf is { } castIf && Holds(castIf, card.Owner, card)) || CastWithGraveyardArtifactsPermission(card);

    private static string BlitzCostText(Card card)
    {
        var text = card.Definition.Blitz?.ToString() ?? "";
        if (card.Definition.BlitzExtra is { Discard: > 0 } extra) text += extra.Discard == 1 ? ", discard a card" : $", discard {extra.Discard} cards";
        return text;
    }

    /// <summary>
    /// A permanent whose blitz cost was paid (702.152a): as long as it is that object it has haste and "when this is put into a graveyard
    /// from the battlefield, draw a card"; it is sacrificed at the beginning of the next end step.
    /// </summary>
    private void Blitzed(Card card, PlayerId controller)
    {
        card.Blitzed = true;
        State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
        State.AtNextEndStepEffects.Add((card.Id, controller, card.Id, card.Version, new Effect[] { new SacrificeIt(Subject.Triggered) }, null, null));
        Emit(new ChoiceMade(card.Id, "blitzed: haste, draws a card when it dies, sacrificed at the next end step"));
    }

    // ------------------------------------------------------------------ foretell (702.143)

    /// <summary>A card its owner foretold on an earlier turn, still that object in exile: it can be cast for its foretell cost.</summary>
    private bool Foretold(Card card) =>
        card.Zone == Zone.Exile && card.ForetoldTurn >= 0 && card.ForetoldTurn < State.TurnNumber && card.Definition.Foretell is not null && !card.Is(CardType.Land);

    /// <summary>Foretelling (702.143a, a special action): the card goes from its owner's hand into exile face down; only its owner may look at it.</summary>
    private async Task ForetellAsync(PlayerId player, Card card)
    {
        await MoveCardAsync(card.Id, Zone.Exile, faceDown: true);
        if (card.Zone != Zone.Exile) return;
        card.KnownTo.Add(card.Owner);
        card.ForetoldTurn = State.TurnNumber;
        Emit(new ChoiceMade(card.Id, $"{State.GetPlayer(player).Name} foretold a card"));
    }

    /// <summary>"Whenever one or more creatures an opponent controls attack you and aren't blocked", after blockers are declared (rule 509.4).</summary>
    private void QueueUnblockedByOpponent(CombatState combat)
    {
        var unblocked = combat.Attacks.Where(a => a.Planeswalker is null && !a.IsBlocked)
            .Select(a => (a.Defender, Attacker: State.GetCard(a.Attacker).Controller)).Distinct().ToList();
        foreach (var (defender, attacker) in unblocked)
            foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == defender))
                foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.AttackedUnblockedByOpponent))
                    AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(Player: attacker));
    }

    private int EvalHeroesCommander(Quantity q, EffectContext ctx) => q.Kind switch
    {
        QuantityKind.Difference => Eval(q.Parts![0], ctx) - Eval(q.Parts![1], ctx),
        QuantityKind.ColorsAmongYourPermanents => State.PermanentsControlledBy(ctx.Controller).SelectMany(c => c.Colors).Distinct().Count(),
        QuantityKind.CounterKindsOnSource => ctx.Source.Counters.Count(kv => kv.Value > 0),
        QuantityKind.ExiledManaValue => ctx.Results.Exiled.Select(State.GetCard).Sum(ManaValueOf),
        QuantityKind.PlayersWhoLostLifeThisTurn => State.Players.Count(p => p.LifeLostThisTurn > 0),
        QuantityKind.GreatestPowerAsCast => ctx.Source.GreatestPowerAsCast,
        QuantityKind.EnteredThisTurnCount => State.GetPlayer(ctx.Controller).EnteredThisTurn.Select(e => (Card: State.GetCard(e.Card), e.Version))
            .Count(e => e.Card.Version == e.Version && e.Card.Zone == Zone.Battlefield
                        && Matches((q.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, e.Card, e.Card.Controller, ctx.Source, ctx.Controller)),
        QuantityKind.OpponentsControlling => State.OpponentsOf(ctx.Controller).Count(o => State.PermanentsControlledBy(o)
            .Any(c => Matches((q.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, c, o, ctx.Source, ctx.Controller))),
        QuantityKind.SacrificedManaValue => ctx.Results.Sacrificed.Select(State.GetCard).Sum(c => c.LastKnownInfo?.ManaValue ?? ManaValueOf(c)),
        QuantityKind.TriggeredCounterCount => ctx.Trigger is { Subject: { } about } info
            ? (State.GetCard(about) is var c && c.Version == info.SubjectVersion && c.Zone == Zone.Battlefield ? c.Counters.Values.Sum()
                : (c.LastKnownOf(info.SubjectVersion) ?? c.LastKnownInfo)?.Counters.Values.Sum() ?? 0) : 0,
        QuantityKind.OpponentsCombatDamagedBySourceOrDragon => State.OpponentsOf(ctx.Controller).Select(State.GetPlayer)
            .Count(p => p.CombatDamagedByDragonThisTurn || p.CombatDamageSourcesThisTurn.Contains((ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version))),
        _ => throw new InvalidOperationException($"Unknown quantity {q.Kind}"),
    };

    /// <summary>"As long as this Equipment is attached to a creature, your opponents can't cast spells during your turn."</summary>
    private bool ForbiddenDuringEquippersTurn(PlayerId player) =>
        State.Battlefield.Select(State.GetCard).Any(p => p.Definition.OpponentsCantCastDuringYourTurnWhileAttached && p.HasTextAbilities
            && p.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield, IsCreature: true }
            && State.ActivePlayer == p.Controller && State.OpponentsOf(p.Controller).Contains(player));

    // ------------------------------------------------------------------ "once during each of your turns, you may cast … from your graveyard"

    /// <summary>The permanents whose unused once-per-turn permission lets <paramref name="caster"/> cast this graveyard card now.</summary>
    private List<Card> OncePerTurnGraveyardPermissions(Card card, PlayerId caster)
    {
        if (card.Zone != Zone.Graveyard || card.Owner != caster || State.ActivePlayer != caster || card.Is(CardType.Land)) return new();
        return State.PermanentsControlledBy(caster).Where(p => p.HasTextAbilities && p.Definition.GraveyardCastOncePerTurn is { } permission
                && !State.GraveyardCastsUsed.Contains((p.Id, p.Version, State.TurnNumber))
                && Matches(permission.Filter with { Controller = ControllerFilter.Any }, card, caster, p, caster)
                && (!permission.NotFromBattlefieldThisTurn || (card.ZoneChangedTurn == State.TurnNumber && card.EnteredFrom != Zone.Battlefield)))
            .ToList();
    }

    /// <summary>
    /// Casting a card from the graveyard that only a once-per-turn permission allows: the permission it uses (the player chooses when more than
    /// one applies with different riders). Null when the card is cast some other way.
    /// </summary>
    private async Task<Card?> ChooseOncePerTurnGraveyardPermissionAsync(PlayerId caster, Card card)
    {
        if (card.Zone != Zone.Graveyard || card.Definition.Flashback is not null || IsFlashbackCast(card) || GraveyardCastByOtherPermission(card)) return null;
        var permissions = OncePerTurnGraveyardPermissions(card, caster);
        if (permissions.Count == 0) return null;
        var distinct = permissions.GroupBy(p => p.Definition.GraveyardCastOncePerTurn!.EntersTapped).Select(g => g.First()).ToList();
        if (distinct.Count == 1) return distinct[0];
        int pick = await ControllerOf(caster).ChooseOptionAsync(ViewFor(caster), new OptionRequest($"Cast {card.Name} from your graveyard with which permission?", card.Id,
            distinct.Select(p => p.Definition.GraveyardCastOncePerTurn!.EntersTapped ? $"{p.Name} (it enters tapped)" : p.Name).ToList(), OptionKind.Other));
        Require(pick >= 0 && pick < distinct.Count, "Choose one of the permissions.");
        return distinct[pick];
    }

    private bool TurnHappenedHolds(TurnHappened th, EffectContext ctx) => th.Event switch
    {
        TurnEvent.YouPutCounterOnCreature => State.GetPlayer(ctx.Controller).PutCounterOnCreatureThisTurn,
        TurnEvent.CounterPutOnSource => ctx.Source.Zone == Zone.Battlefield && ctx.Source.CounterPutTurn == State.TurnNumber,
        TurnEvent.CardLeftYourGraveyard => State.GetPlayer(ctx.Controller).CardLeftGraveyardThisTurn,
        TurnEvent.YouLostLife => State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).LifeLostThisTurn >= th.AtLeast,
        TurnEvent.PlayerLostLife => State.Players.Any(p => p.LifeLostThisTurn >= th.AtLeast),
        _ => false,
    };

    private static readonly string[] PartyRoles = { "Cleric", "Rogue", "Warrior", "Wizard" };

    /// <summary>
    /// The number of creatures in a player's party (rule 700.8): up to one each of Cleric, Rogue, Warrior and Wizard, each creature filling one
    /// role (the largest such assignment; a changeling can fill any).
    /// </summary>
    private int PartySize(PlayerId player)
    {
        var creatures = State.PermanentsControlledBy(player).Where(c => c.IsCreature).ToList();
        var holder = new int[PartyRoles.Length];
        Array.Fill(holder, -1);
        bool Fill(int creature, bool[] seen)
        {
            for (int r = 0; r < PartyRoles.Length; r++)
            {
                if (seen[r] || !(creatures[creature].HasSubtype(PartyRoles[r]) || creatures[creature].Has(Keyword.Changeling))) continue;
                seen[r] = true;
                if (holder[r] < 0 || Fill(holder[r], seen)) { holder[r] = creature; return true; }
            }
            return false;
        }
        int size = 0;
        for (int c = 0; c < creatures.Count && size < PartyRoles.Length; c++)
            if (Fill(c, new bool[PartyRoles.Length])) size++;
        return size;
    }

    /// <summary>Training (702.149a): "Whenever this creature attacks with another creature with greater power, put a +1/+1 counter on this creature."</summary>
    private static readonly TriggeredAbility TrainingTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[] { new AddCounters(1, Subject.Self) },
        Text = "Training: put a +1/+1 counter on this creature.",
    };

    /// <summary>Modified (rule 700.9): it has one or more counters, is equipped, or is enchanted by an Aura its controller controls.</summary>
    private bool IsModified(Card card) =>
        card.Counters.Values.Any(v => v > 0)
        || State.Battlefield.Select(State.GetCard).Any(a => a.AttachedTo == card.Id
            && (a.HasSubtype("Equipment") || (a.HasSubtype("Aura") && a.Controller == card.Controller)));

    /// <summary>"As this enters, remove all counters from any number of [filter]. It enters with twice that many +1/+1 counters" (after it entered, like devour).</summary>
    private async Task EnterRemovingCountersAsync(Card card, ObjectFilter filter, PlayerId who)
    {
        var holders = State.Battlefield.Select(State.GetCard)
            .Where(c => c.Id != card.Id && c.Counters.Values.Any(v => v > 0) && Matches(filter with { Controller = ControllerFilter.Any }, c, c.Controller, card, who)).ToList();
        if (holders.Count == 0) return;
        var picked = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{card.Name}: remove all counters from any number of these", card.Id,
            holders.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, holders.Count, CardChoicePurpose.Keep));
        Require(picked.Distinct().Count() == picked.Count && picked.All(id => holders.Any(c => c.Id == id)), "Choose among the listed permanents.");
        int removed = 0;
        foreach (var holder in picked.Select(State.GetCard))
            foreach (var (kind, n) in holder.Counters.Where(kv => kv.Value > 0).ToList())
            {
                RemoveCountersFrom(holder, kind, n);
                removed += n;
            }
        if (removed > 0) PutCounters(card, CounterKind.PlusOnePlusOne, 2 * removed, who);
        await ResolvePendingCountersAsync();
    }
}
