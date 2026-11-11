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
    /// <summary>"Each other creature you control enters with additional +1/+1 counters equal to …" of the entering creature's controller's permanents.</summary>
    private int ExtraEnterCounters(Card entering) =>
        State.PermanentsControlledBy(entering.Controller).Where(c => c.Id != entering.Id && c.Definition.OthersEnterWithCounters is not null && c.HasTextAbilities
                && (c.Definition.OthersEnterWithCountersFilter is not { } only || Matches(only with { Controller = ControllerFilter.Any }, entering, entering.Controller, c, c.Controller)))
            .Sum(c => Math.Max(0, Eval(c.Definition.OthersEnterWithCounters!, new EffectContext(c.Controller, c, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))))
        + State.GetPlayer(entering.Controller).Graveyard.Select(State.GetCard)
            .Count(g => g.Definition.GraveyardEnterBonus is { } bonus && Matches(bonus with { Controller = ControllerFilter.Any }, entering, entering.Controller, g, entering.Controller));

    /// <summary>Puts counters on a permanent (doubled by "twice that many counters" effects of its controller).</summary>
    /// <summary>A counter kind as players read it ("first strike", "+1/+1").</summary>
    private static string CounterName(CounterKind kind) => kind switch
    {
        CounterKind.PlusOnePlusOne => "+1/+1",
        CounterKind.MinusOneMinusOne => "-1/-1",
        CounterKind.MinusZeroMinusOne => "-0/-1",
        _ => System.Text.RegularExpressions.Regex.Replace(kind.ToString(), "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant(),
    };

    /// <summary>Keyword counters and the keyword each gives (rule 122.1b).</summary>
    private static readonly (CounterKind Counter, Keyword Keyword)[] KeywordCounters =
    {
        (CounterKind.Trample, Keyword.Trample), (CounterKind.Indestructible, Keyword.Indestructible), (CounterKind.Lifelink, Keyword.Lifelink),
        (CounterKind.Shadow, Keyword.Shadow), (CounterKind.FirstStrike, Keyword.FirstStrike), (CounterKind.DoubleStrike, Keyword.DoubleStrike),
        (CounterKind.Deathtouch, Keyword.Deathtouch), (CounterKind.Flying, Keyword.Flying), (CounterKind.Haste, Keyword.Haste),
        (CounterKind.Hexproof, Keyword.Hexproof), (CounterKind.Menace, Keyword.Menace), (CounterKind.Reach, Keyword.Reach),
        (CounterKind.Vigilance, Keyword.Vigilance), (CounterKind.Decayed, Keyword.Decayed),
    };

    /// <summary>Whether an effect keeps counters from being put on this permanent, or given to this player, while its source remains.</summary>
    private bool CountersBannedOn(Card? card, PlayerId? player)
    {
        State.CounterBans.RemoveAll(b => State.GetCard(b.Source) is var source && (source.Version != b.SourceVersion || source.Zone != Zone.Battlefield));
        return State.CounterBans.Any(b => card is not null && b.Card == card.Id && b.CardVersion == card.Version || player is not null && b.Player == player);
    }

    /// <summary>The subject names players (a chosen player, "each opponent"), not objects.</summary>
    private static bool IsPlayerSubject(Subject subject) =>
        subject.Kind is SubjectKind.You or SubjectKind.TriggeredController or SubjectKind.EachOpponent or SubjectKind.EachPlayer or SubjectKind.Target
            or SubjectKind.TriggeredPlayer or SubjectKind.OtherOpponents or SubjectKind.AttackedBySource
            or SubjectKind.SourceOwner or SubjectKind.PlayersOtherThanSourceOwner;

    private void PutCounters(Card card, CounterKind kind, int count, PlayerId? placedBy = null)
    {
        if (CountersAfterReplacements(card, kind, count, placedBy) is { } amount) PlaceCounters(card, kind, amount, placedBy);
    }

    /// <summary>
    /// How many counters are put on after the replacement effects that modify them ("that many plus one", "twice that many"); null
    /// when both kinds apply, since the permanent's controller then chooses their order (rule 616.1) as soon as the engine can ask
    /// (right after the current effect): the counters wait in <see cref="_pendingCounters"/>. Counters a permanent enters with
    /// are ordered as it enters instead (<see cref="OrderEnterCountersAsync"/>), so they never wait.
    /// </summary>
    private int? CountersAfterReplacements(Card card, CounterKind kind, int count, PlayerId? placedBy)
    {
        if (count <= 0 || CountersBannedOn(card, null) || card.Has(Keyword.CantHaveCounters)) return null;
        // "Creatures you control can't have -1/-1 counters put on them."
        if (kind == CounterKind.MinusOneMinusOne && card.Zone == Zone.Battlefield && card.IsCreature
            && State.PermanentsControlledBy(card.Controller).Any(p => p.HasTextAbilities && p.Definition.YourCreaturesCantHaveMinusCounters)) return null;
        int plusOnes = ExtraCounterInstances(card, kind), doublings = CounterDoublings(card, kind, placedBy);
        if (plusOnes > 0 && doublings > 0)
        {
            _pendingCounters.Add((card.Id, card.Version, kind, count, placedBy));
            return null;
        }
        return (count + plusOnes) << doublings;
    }

    /// <summary>
    /// Puts on a permanent the counters it enters with, before it is announced as entered: it enters with them (rules 614.1c,
    /// 122.6), so its enters triggers and their intervening "if" conditions (rule 603.4) see them. Counter replacement effects
    /// still apply. Returns what was placed, to be announced afterwards with <see cref="AnnounceEnterCounters"/>. Counters whose
    /// replacement effects need their controller to choose an order have it asked first (<see cref="OrderEnterCountersAsync"/>,
    /// the result passed as <paramref name="ordered"/>); without it (a move that can't ask) they are put right after the
    /// current effect instead (see <see cref="PutCounters"/>).
    /// </summary>
    private List<(CounterKind Kind, int Count)> PlaceEnterCounters(Card card, IReadOnlyList<(CounterKind Kind, int Count)> counters, IReadOnlyDictionary<int, int>? ordered = null)
    {
        var placed = new List<(CounterKind, int)>();
        for (int i = 0; i < counters.Count; i++)
        {
            var (kind, count) = counters[i];
            int amount;
            if (ordered is not null && ordered.TryGetValue(i, out var orderedAmount)) amount = orderedAmount;
            else if (CountersAfterReplacements(card, kind, count, card.Controller) is { } plain) amount = plain;
            else continue;
            card.Counters[kind] = card.CounterCount(kind) + amount;
            placed.Add((kind, amount));
        }
        return placed;
    }

    /// <summary>
    /// For a permanent entering with counters, has its controller order the "plus one" and "twice that many" replacement effects
    /// where both apply (rule 616.1), as it enters and before its counters are put on. Returns the resulting amounts, by index.
    /// </summary>
    private async Task<Dictionary<int, int>> OrderEnterCountersAsync(Card card, IReadOnlyList<(CounterKind Kind, int Count)> counters)
    {
        var ordered = new Dictionary<int, int>();
        for (int i = 0; i < counters.Count; i++)
        {
            var (kind, count) = counters[i];
            if (count > 0 && ExtraCounterInstances(card, kind) > 0 && CounterDoublings(card, kind, card.Controller) > 0)
                ordered[i] = await OrderedCounterCountAsync(card, kind, count, card.Controller);
        }
        return ordered;
    }

    /// <summary>
    /// The number of counters after every "that many plus one" and "twice that many" effect applied once, in the order the
    /// permanent's controller chooses (rules 614.5 and 616.1).
    /// </summary>
    private async Task<int> OrderedCounterCountAsync(Card card, CounterKind kind, int count, PlayerId? placedBy)
    {
        int plusOnes = ExtraCounterInstances(card, kind), doublings = CounterDoublings(card, kind, placedBy);
        while (plusOnes > 0 || doublings > 0)
        {
            bool plusFirst = doublings == 0 || (plusOnes > 0 && await ControllerOf(card.Controller).ChooseOptionAsync(ViewFor(card.Controller),
                new OptionRequest($"{count} {CounterName(kind)} counter{(count == 1 ? "" : "s")} on {card.Name}: which replacement applies next?", card.Id,
                    new[] { "That many plus one", "Twice that many" }, OptionKind.Other)) == 0);
            if (plusFirst) { count += 1; plusOnes--; }
            else { count *= 2; doublings--; }
        }
        return count;
    }

    /// <summary>Announces counters placed by <see cref="PlaceEnterCounters"/>.</summary>
    private void AnnounceEnterCounters(Card card, List<(CounterKind Kind, int Count)> placed)
    {
        if (placed.Count == 0) return;
        // Every counter it enters with is put on at once ("one or more counters" triggers once, learning each kind).
        _counterPlacements++;
        BeginSimultaneous();
        foreach (var (kind, amount) in placed) Emit(new CountersPlaced(card.Id, kind, amount, card.Controller));
        EndSimultaneous();
    }

    private void PlaceCounters(Card card, CounterKind kind, int count, PlayerId? placedBy)
    {
        if (kind == CounterKind.Lore && count > 0) State.LoreCounterTurns.Add((card.Id, card.Version, State.TurnNumber));
        card.Counters[kind] = card.CounterCount(kind) + count;
        _counterPlacements++;
        Emit(new CountersPlaced(card.Id, kind, count, placedBy));
    }

    /// <summary>Counter placements so far: the kinds a single placement puts on a permanent count as one event for it.</summary>
    private int _counterPlacements;
    private (Core.CardId Card, int Turn, int Placement) _lastCounterPlacement;

    /// <summary>"Twice that many" effects that apply to these counters on this permanent (each instance once).</summary>
    private int CounterDoublings(Card card, CounterKind kind, PlayerId? placedBy) =>
        Instances(card.Controller, Replacements.DoubleCounters) + (placedBy is { } by ? CountersYouPutDoublings(by) : 0)
        + (kind == CounterKind.PlusOnePlusOne && card.IsCreature ? Instances(card.Controller, Replacements.DoubleCreaturePlusOneCounters) : 0);

    /// <summary>"If you would put one or more counters on a permanent or player, put twice that many of each of those kinds of counters on that permanent or player instead."</summary>
    private int CountersYouPutDoublings(PlayerId player) =>
        State.PermanentsControlledBy(player).Count(c => c.HasTextAbilities && c.Definition.DoublesCountersYouPut);

    /// <summary>"That many plus one +1/+1 counters are put on it instead" effects that apply (Armies, Goblins and Orcs you control).</summary>
    private int ExtraCounterInstances(Card card, CounterKind kind) =>
        (kind == CounterKind.PlusOnePlusOne && card.IsCreature && (card.HasSubtype("Army") || card.HasSubtype("Goblin") || card.HasSubtype("Orc"))
            ? Instances(card.Controller, Replacements.ExtraCounterOnArmiesGoblinsOrcs) : 0)
        // "If one or more +1/+1 counters would be put on a creature you control, that many plus one +1/+1 counters are put on it instead."
        + (kind == CounterKind.PlusOnePlusOne && card.IsCreature
            ? State.PermanentsControlledBy(card.Controller).Count(c => c.HasTextAbilities && c.Definition.CreaturePlusOneCountersPlusOne) : 0)
        // The same on other kinds of permanents ("another creature you control", "a permanent you control", "an artifact or creature you control").
        + (kind == CounterKind.PlusOnePlusOne
            ? State.PermanentsControlledBy(card.Controller).Count(c => c.HasTextAbilities && c.Definition.PlusOneCountersPlusOneOn is { } on && Matches(on, card, card.Controller, c, c.Controller)) : 0);

    /// <summary>Counters waiting for their controller to order the replacement effects that modify them.</summary>
    private readonly List<(CardId Card, int Version, CounterKind Kind, int Count, PlayerId? PlacedBy)> _pendingCounters = new();

    /// <summary>Puts the waiting counters, each replacement effect applied once in the order the permanent's controller chooses.</summary>
    private async Task ResolvePendingCountersAsync()
    {
        await GainPendingLifeAsync();
        while (_pendingCounters.Count > 0)
        {
            var (id, version, kind, count, placedBy) = _pendingCounters[0];
            _pendingCounters.RemoveAt(0);
            var card = State.GetCard(id);
            if (card.Zone != Zone.Battlefield || card.Version != version) continue;
            PlaceCounters(card, kind, await OrderedCounterCountAsync(card, kind, count, placedBy), placedBy);
        }
    }

    /// <summary>How many permanents with this replacement effect a player controls (each one applies, rule 616).</summary>
    private int Instances(PlayerId player, Replacements replacement) =>
        State.PermanentsControlledBy(player).Count(c => (c.Replaces & replacement) != 0);

    /// <summary>Whether a player controls a permanent with this replacement effect.</summary>
    private bool Has(PlayerId player, Replacements replacement) =>
        State.PermanentsControlledBy(player).Any(c => (c.Replaces & replacement) != 0)
        || State.Emblems.Select(State.GetCard).Any(e => e.Owner == player && (e.Replaces & replacement) != 0);

    private bool OpponentsCreaturesEnterTapped(PlayerId controller) =>
        State.OpponentsOf(controller).Any(o => Has(o, Replacements.OpponentsCreaturesEnterTapped));
}
