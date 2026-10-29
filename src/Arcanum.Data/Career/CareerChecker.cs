// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>A career as the career service keeps it: the journal it checked, the state that gives, and its seal.</summary>
public sealed record CheckedCareer(List<CareerAction> Journal, CareerState State, CareerSeal Seal);

/// <summary>
/// What the career service does with a sync (see <see cref="CareerSyncRequest"/>): it applies the new actions to the
/// state it checked last, with the rules and its own clock, and seals every action up to the first one that doesn't hold.
/// The same code the game runs, so the game and the service can't disagree about the rules. Pure: the caller stores
/// the result.
/// </summary>
public sealed class CareerChecker
{
    /// <summary>How far ahead of the service's clock an action may be (devices' clocks drift).</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The most actions one sync may bring.</summary>
    public const int MaxActions = 20_000;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<string, CareerWorld?> _worldFor;
    private readonly string _game;
    private readonly DateTime _importBefore;
    private readonly ECDsa _key;
    private readonly string _keyId;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, FinishEvent, string?> _onlineResult;
    private readonly string? _previousGame;
    private readonly DateTime _switchedAt;

    /// <param name="worldFor">The career world of a content ("id version"), or null when the service doesn't have it.</param>
    /// <param name="game">The game version whose rules this service runs.</param>
    /// <param name="importBefore">
    /// Careers started before this (when the service began checking careers) are taken as they are when their journal no
    /// longer replays: the rules and content they were played with changed since.
    /// </param>
    /// <param name="previousGame">
    /// The game version this service checked before <paramref name="switchedAt"/>: actions played with it before then that
    /// today's rules don't accept are taken as that version computed them (the state the game kept when it updated).
    /// </param>
    /// <param name="onlineResult">
    /// Checks a player's result of an online table against the result the service confirmed: null when it is that one,
    /// otherwise why not. Without it no online result is accepted.
    /// </param>
    public CareerChecker(Func<string, CareerWorld?> worldFor, string game, DateTime importBefore, ECDsa key, string keyId, Func<DateTime>? clock = null,
        Func<string, FinishEvent, string?>? onlineResult = null, string? previousGame = null, DateTime switchedAt = default)
    {
        _previousGame = previousGame;
        _switchedAt = switchedAt;
        _onlineResult = onlineResult ?? ((_, _) => "Online results can't be checked here.");
        _worldFor = worldFor;
        _game = game;
        _importBefore = importBefore;
        _key = key;
        _keyId = keyId;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Checks a sync of <paramref name="player"/>'s career against what the service has (<paramref name="stored"/>).</summary>
    /// <returns>The answer, and the career to store when it changed (null: keep what there is).</returns>
    public (CareerSyncResponse Response, CheckedCareer? Store) Check(string player, CheckedCareer? stored, CareerSyncRequest request)
    {
        if (request.Game != _game)
            return (new(CareerSyncStatus.Unsupported, stored?.Seal, $"The career service checks careers of game version {_game}; this is {request.Game}."), null);
        if (_worldFor(request.Content) is not { } world)
            return (new(CareerSyncStatus.Unsupported, stored?.Seal, $"The career service doesn't have the content {request.Content} yet."), null);
        if (request.Actions.Count > MaxActions)
            return (new(CareerSyncStatus.Rejected, stored?.Seal, "Too many actions at once."), null);

        // A new career replaces the one the service had: the player ended it.
        bool fresh = request.Base == 0 && request.Actions.FirstOrDefault() is StartCareer start
                     && (stored is null || stored.Journal.FirstOrDefault() is not StartCareer old || old.Seed != start.Seed);
        if (fresh) return First(player, request, world);
        if (stored is null || request.Base != stored.Seal.Count || request.BaseHash != stored.Seal.Hash)
            return (new(CareerSyncStatus.Conflict, stored?.Seal), null);
        if (request.Actions.Count == 0) return (new(CareerSyncStatus.Ok, stored.Seal), null);
        return Continue(player, stored, request.Actions, request.Versions ?? Array.Empty<CareerVersion>(), world);
    }

    /// <summary>A career the service hasn't seen: its whole journal is replayed (or, for an older career, taken as it is).</summary>
    private (CareerSyncResponse, CheckedCareer?) First(string player, CareerSyncRequest request, CareerWorld world)
    {
        var start = (StartCareer)request.Actions[0];
        var versions = request.Versions ?? Array.Empty<CareerVersion>();
        var (state, applied, reason, jumps) = ApplyAll(player, new CareerState(), request.Actions, 0, versions, world);
        if (reason is not null && start.Utc < _importBefore && request.State is { } theirs && request.Actions.All(a => a.Utc <= _clock() + ClockSkew))
        {
            // Played before the service checked careers, with rules and content that have changed since: taken as it is.
            var journal = request.Actions.ToList();
            return Sealed(player, journal, theirs);
        }
        if (applied == 0) return (new(CareerSyncStatus.Rejected, null, reason), null);
        var accepted = request.Actions.Take(applied).ToList();
        if (reason is not null) state = Rerun(new CareerState(), accepted, jumps, world);
        var (response, store) = Sealed(player, accepted, state);
        return reason is null ? (response, store) : (response with { Status = CareerSyncStatus.Rejected, Reason = reason }, store);
    }

    private (CareerSyncResponse, CheckedCareer?) Continue(string player, CheckedCareer stored, IReadOnlyList<CareerAction> actions, IReadOnlyList<CareerVersion> versions,
        CareerWorld world)
    {
        var (state, applied, reason, jumps) = ApplyAll(player, Copy(stored.State), actions, stored.Seal.Count, versions, world);
        if (applied == 0) return (new(CareerSyncStatus.Rejected, stored.Seal, reason), null);
        // The action that failed may have changed the state halfway: apply the good ones again from the stored state.
        if (reason is not null) state = Rerun(Copy(stored.State), actions.Take(applied).ToList(), jumps, world);
        var journal = stored.Journal.Concat(actions.Take(applied)).ToList();
        var hash = actions.Take(applied).Aggregate(stored.Seal.Hash, CareerSeals.Extend);
        var seal = CareerSeals.Sign(player, journal.Count, hash, _keyId, _key);
        var store = new CheckedCareer(journal, state, seal);
        return (new(reason is null ? CareerSyncStatus.Ok : CareerSyncStatus.Rejected, seal, reason), store);
    }

    /// <summary>
    /// Where the state was taken as the previous game version had it: at action <see cref="At"/> (local), the state
    /// becomes <see cref="State"/> and checking goes on from action <see cref="To"/>.
    /// </summary>
    private sealed record Jump(int At, int To, CareerState State);

    /// <summary>
    /// Applies the actions (the first is the journal's action <paramref name="first"/>) in order until one doesn't hold;
    /// returns the state, how many held, why the next didn't, and where the previous version's state was taken instead.
    /// </summary>
    private (CareerState State, int Applied, string? Reason, List<Jump> Jumps) ApplyAll(string player, CareerState state, IReadOnlyList<CareerAction> actions, int first,
        IReadOnlyList<CareerVersion> versions, CareerWorld world)
    {
        var limit = _clock() + ClockSkew;
        var jumps = new List<Jump>();
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action.Utc > limit) return (state, i, $"Action {i + 1} ({Name(action)}) is dated in the future.", jumps);
            // An online result is the one the service confirmed for that table, nothing the player says.
            if (action is FinishEvent { Table: not null } online && _onlineResult(player, online) is { } wrong)
                return (state, i, $"Action {i + 1} (FinishEvent) isn't the table's confirmed result: {wrong}", jumps);
            try
            {
                CareerRules.Apply(state, action, world);
            }
            catch (Exception e) when (e is CareerRuleException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
            {
                // Played with the previous version before the service changed to this one: taken as that version computed
                // it, up to the next version change (whose state the game kept), and checked on from there.
                if (PreviousVersionUntil(first + i, action, versions) is { } next && next.From - first <= actions.Count)
                {
                    int to = next.From - first;
                    state = Copy(next.Before!);
                    jumps.Add(new Jump(i, to, next.Before!));
                    i = to - 1;
                    continue;
                }
                return (state, i, $"Action {i + 1} ({Name(action)}) doesn't hold: {e.Message}", jumps);
            }
        }
        return (state, actions.Count, null, jumps);
    }

    /// <summary>
    /// The version change after the journal's action <paramref name="index"/> when that action was played with the previous
    /// game version before the service changed (and the game kept the state at the change); otherwise null.
    /// </summary>
    private CareerVersion? PreviousVersionUntil(int index, CareerAction action, IReadOnlyList<CareerVersion> versions)
    {
        if (_previousGame is null || action.Utc >= _switchedAt) return null;
        var played = versions.LastOrDefault(v => v.From <= index);
        if (played?.Game != _previousGame) return null;
        return versions.FirstOrDefault(v => v.From > index && v.Before is not null && v.Game != _previousGame);
    }

    /// <summary>Applies the actions that held again from <paramref name="state"/>, taking the previous version's state where it was taken.</summary>
    private static CareerState Rerun(CareerState state, IReadOnlyList<CareerAction> actions, IReadOnlyList<Jump> jumps, CareerWorld world)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            if (jumps.FirstOrDefault(j => j.At == i) is { } jump)
            {
                state = Copy(jump.State);
                i = jump.To - 1;
                continue;
            }
            CareerRules.Apply(state, actions[i], world);
        }
        return state;
    }

    private (CareerSyncResponse, CheckedCareer?) Sealed(string player, List<CareerAction> journal, CareerState state)
    {
        var seal = CareerSeals.Sign(player, journal.Count, CareerSeals.Hash(journal, journal.Count), _keyId, _key);
        return (new(CareerSyncStatus.Ok, seal), new CheckedCareer(journal, state, seal));
    }

    private static string Name(CareerAction action) => action.GetType().Name;

    /// <summary>A deep copy (the rules change the state in place).</summary>
    public static CareerState Copy(CareerState state) =>
        JsonSerializer.Deserialize<CareerState>(JsonSerializer.SerializeToUtf8Bytes(state, Options), Options)!;

    /// <summary>A checked career as stored by the service (compact JSON).</summary>
    public static byte[] Write(CheckedCareer career) => JsonSerializer.SerializeToUtf8Bytes(career, Options);

    public static CheckedCareer Read(byte[] data) => JsonSerializer.Deserialize<CheckedCareer>(data, Options)!;
}
