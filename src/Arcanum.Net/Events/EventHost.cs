// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcanum.Bots.Limited;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Net.Host;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Events;

/// <summary>What an event needs from the card content (the host's card data).</summary>
public sealed class EventRules
{
    /// <summary>Opens boosters; for a draft being resumed, gets its snapshot (a cube leaves out the cards already opened).</summary>
    public required Func<DraftSnapshot?, IBoosterSource> Boosters { get; init; }

    /// <summary>A card as the computer players judge it.</summary>
    public required Func<PoolCard, DraftOption> Option { get; init; }

    /// <summary>A deck built from a pool the way the computer players build theirs.</summary>
    public required Func<IReadOnlyList<PoolCard>, DeckList> AutoBuild { get; init; }

    /// <summary>Why a deck can't be played with this pool, or null.</summary>
    public required Func<DeckList, IReadOnlyList<PoolCard>, string?> CheckDeck { get; init; }

    /// <summary>
    /// A constructed event: why a deck changed between games can't be played with the cards registered (main deck and
    /// sideboard), or null. Without it, constructed decks can't be changed.
    /// </summary>
    public Func<DeckList, DeckList, string?>? CheckSideboarding { get; init; }

    /// <summary>The cards of a deck, for a game.</summary>
    public required Func<DeckList, IReadOnlyList<CardDefinition>> Cards { get; init; }

    /// <summary>How strong a deck is, to settle matches between computer players without playing them.</summary>
    public Func<DeckList, double> Strength { get; init; } = _ => 1;
}

public sealed record EventOptions
{
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Seconds to pick from a booster of this many cards.</summary>
    public Func<int, int> PickSeconds { get; init; } = cards => Math.Clamp(10 + 5 * cards, 15, 80);

    /// <summary>
    /// Extra time for the first pick of each round, while everyone opens their new booster on screen (it opens by
    /// itself after a few seconds, so this covers players who leave it alone too).
    /// </summary>
    public TimeSpan OpeningTime { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan BuildTime { get; init; } = TimeSpan.FromMinutes(12);

    /// <summary>Time between the games of a match (sideboarding).</summary>
    public TimeSpan SideboardTime { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Options of each game (grace period, computer pace, versions).</summary>
    public HostOptions Games { get; init; } = new();

    /// <summary>
    /// An event of two days: after this many rounds the day is over (the cut by record applied) and no round is paired
    /// until the event is resumed on <see cref="NextDay"/> (see <see cref="EventHost.DayEnded"/>). 0: one day.
    /// </summary>
    public int DayEndsAfter { get; init; }

    /// <summary>When the next day's rounds begin (UTC), told to the players.</summary>
    public DateTime? NextDay { get; init; }
}

/// <summary>
/// Runs a draft, sealed or constructed event for players connected to this host: the draft (everyone picks at once;
/// when a pick's time runs out, the computer picks for that player), deck building from each pool (timed; an unfinished
/// deck is built automatically), then Swiss rounds whose games run side by side, each in its own <see cref="GameHost"/>,
/// and the playoff of a top cut. A constructed event, every seat with its deck, starts at the first round. Swiss rounds
/// with a time limit end on time (<see cref="LimitedEvent.RoundMinutes"/>). Players only ever see their own boosters,
/// pool and deck. Everything happens on the thread that calls <see cref="Poll"/>.
/// </summary>
public sealed class EventHost
{
    private sealed class ActiveGame
    {
        public required EventMatch Match { get; init; }
        public required GameHost Host { get; init; }
        public required ulong Seed { get; init; }
        /// <summary>Game tokens of seat A and seat B (null for the computer).</summary>
        public required string?[] Tokens { get; init; }
    }

    private readonly LimitedEvent _ev;
    private readonly string?[] _tokens;
    private readonly EventRules _rules;
    private readonly EventOptions _options;
    private readonly Peer?[] _peers;
    private readonly bool[] _ready;
    private readonly string?[] _problems;
    private readonly List<IConnectionListener> _listeners = new();
    private readonly List<Peer> _greeting = new();
    private readonly List<ActiveGame> _games = new();
    private readonly Dictionary<EventMatch, DateTime> _between = new(); // matches waiting for their next game
    private readonly Random _random;
    private DraftSession? _draft;
    private DateTime _deadline;
    private bool _dirty = true;

    /// <param name="tokens">Each seat's token; null for the computer's seats.</param>
    public EventHost(LimitedEvent ev, IReadOnlyList<string?> tokens, EventRules rules, EventOptions? options = null)
    {
        _ev = ev;
        _tokens = tokens.ToArray();
        _rules = rules;
        _options = options ?? new EventOptions();
        _peers = new Peer?[ev.Seats.Count];
        _ready = new bool[ev.Seats.Count];
        _problems = new string?[ev.Seats.Count];
        _random = new Random(ev.Seed);
    }

    public LimitedEvent Event => _ev;

    private DateTime Now => _options.Clock();

    private bool IsPerson(int seat) => _ev.Seats[seat].IsHuman;

    /// <summary>The people of a match who aren't at the table (not connected to the event).</summary>
    private List<int> Away(EventMatch match) =>
        new[] { match.SeatA, match.SeatB ?? -1 }.Where(s => s >= 0 && IsPerson(s) && _peers[s] is null).ToList();

    /// <summary>The event changed (to save it, or redraw the host's screen).</summary>
    public event Action? Changed;

    /// <summary>The day's rounds are over (<see cref="EventOptions.DayEndsAfter"/>): save the event to resume it the next day.</summary>
    public event Action? DayEnded;

    /// <summary>The day's rounds are over and the event waits for the next day.</summary>
    public bool DayOver { get; private set; }

    public void AddListener(IConnectionListener listener) => _listeners.Add(listener);

    /// <summary>Opens the boosters: the draft begins, or (sealed) everyone gets their pool.</summary>
    public void Start()
    {
        if (_ev.Seats.All(s => s.Deck is not null))
        {
            // Constructed: everyone brought a deck.
            _ev.Stage = EventStage.Playing;
            NextRound();
        }
        else if (_ev.Mode == LimitedMode.Draft)
        {
            _ev.Stage = EventStage.Drafting;
            _draft = new DraftSession(_rules.Boosters(null), _ev.Seats.Count, _ev.BoostersPerPlayer, _random);
            NewPick();
        }
        else
        {
            var boosters = _rules.Boosters(null);
            foreach (var seat in _ev.Seats)
                for (int i = 0; i < _ev.BoostersPerPlayer; i++) seat.Pool.AddRange(boosters.Open(_random));
            FinishPools();
        }
        MarkChanged();
    }

    /// <summary>A player's connection, already identified (handed over by the lobby).</summary>
    public void Attach(int seat, IConnection connection, IEnumerable<NetMessage>? received = null)
    {
        _peers[seat]?.Close();
        var peer = new Peer(connection, new WireFormat(), _options.Clock);
        if (received is not null) peer.Hold(received);
        _peers[seat] = peer;
        MarkChanged();
    }

    public void Accept(IConnection connection) => _greeting.Add(new Peer(connection, new WireFormat(), _options.Clock));

    private void MarkChanged()
    {
        _dirty = true;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ every frame

    public void Poll()
    {
        foreach (var listener in _listeners)
            while (listener.TryAccept(out var connection)) Accept(connection);
        Greet();

        for (int seat = 0; seat < _peers.Length; seat++)
        {
            if (_peers[seat] is not { } peer) continue;
            foreach (var message in peer.Receive()) Handle(seat, message);
            if (!peer.IsOpen)
            {
                _peers[seat] = null;
                MarkChanged();
            }
        }

        switch (_ev.Stage)
        {
            case EventStage.Drafting: PollDraft(); break;
            case EventStage.Building: PollBuilding(); break;
            case EventStage.Playing: PollRounds(); break;
        }

        if (_dirty) SendUpdates();
    }

    private void Greet()
    {
        foreach (var peer in _greeting.ToList())
        {
            var messages = peer.Receive();
            if (messages.Count == 0)
            {
                if (!peer.IsOpen) _greeting.Remove(peer);
                continue;
            }
            _greeting.Remove(peer);
            if (messages[0] is not Hello hello)
            {
                peer.Close();
                continue;
            }
            // A player coming back to the event, or joining their game.
            int seat = Array.FindIndex(_tokens, t => t is not null && Tokens.Same(t, hello.Token));
            if (seat >= 0)
            {
                Attach(seat, peer.Connection, messages.Skip(1));
                continue;
            }
            var game = _games.FirstOrDefault(g => g.Host.HasToken(hello.Token));
            if (game is not null)
            {
                game.Host.Accept(peer.Connection, messages);
                continue;
            }
            peer.Send(new Rejected("No seat in this event for you."));
            peer.Close();
        }
    }

    private void Handle(int seat, NetMessage message)
    {
        switch (message)
        {
            case DraftPick pick when _draft is { } draft && pick.Round == draft.Round && pick.Pick == draft.PickInRound
                                     && !draft.HasPicked(seat) && pick.Index >= 0 && pick.Index < draft.PackFor(seat).Count:
                PickFor(seat, pick.Index);
                break;
            case SubmitDeck deck when _ev.Stage is EventStage.Building or EventStage.Playing && !InGame(seat):
                try
                {
                    var list = DeckList.Parse(deck.List);
                    if (_ev.Seats[seat].Registered is { } registered)
                    {
                        // Sideboarding a constructed deck: only with the cards registered.
                        _problems[seat] = _rules.CheckSideboarding is { } check ? check(list, registered) : "Decks can't be changed in this event.";
                        if (_problems[seat] is null) _ev.Seats[seat].Deck = DeckValidator.WithSideboardFrom(list, registered);
                    }
                    else
                    {
                        _problems[seat] = _rules.CheckDeck(list, _ev.Seats[seat].Pool);
                        if (_problems[seat] is null || _ev.Stage == EventStage.Building) _ev.Seats[seat].Deck = list;
                    }
                }
                catch (FormatException e)
                {
                    _problems[seat] = e.Message;
                }
                _ready[seat] = false;
                MarkChanged();
                break;
            case EventReady when _ev.Stage is EventStage.Building or EventStage.Playing:
                if (_ev.Seats[seat].Deck is not null && _problems[seat] is null)
                {
                    _ready[seat] = true;
                    MarkChanged();
                }
                break;
        }
    }

    private bool InGame(int seat) => _games.Any(g => g.Match.Involves(seat));

    // ------------------------------------------------------------------ draft

    private void NewPick()
    {
        _deadline = Now + TimeSpan.FromSeconds(_options.PickSeconds(_draft!.PackFor(0).Count))
                    + (_draft.PickInRound == 0 ? _options.OpeningTime : TimeSpan.Zero);
    }

    private void PickFor(int seat, int index)
    {
        var draft = _draft!;
        int pickBefore = draft.PickInRound, roundBefore = draft.Round;
        draft.Pick(seat, index);
        if (draft.IsComplete) FinishPools();
        else if (draft.PickInRound != pickBefore || draft.Round != roundBefore) NewPick();
        MarkChanged();
    }

    private int ComputerPick(int seat) =>
        DraftPicker.Pick(_draft!.PackFor(seat).Select(_rules.Option).ToList(), _draft.Picks[seat].Select(_rules.Option).ToList(), _random, _ev.Seats[seat].Skill);

    private void PollDraft()
    {
        var draft = _draft!;
        bool timeUp = Now >= _deadline;
        // Computer players pick at once; people when they choose, or the computer for them when time is up.
        for (int seat = 0; seat < _ev.Seats.Count && _ev.Stage == EventStage.Drafting; seat++)
            if (!draft.HasPicked(seat) && (!IsPerson(seat) || timeUp))
                PickFor(seat, ComputerPick(seat));
    }

    private void FinishPools()
    {
        if (_draft is { } draft)
        {
            for (int s = 0; s < _ev.Seats.Count; s++)
            {
                _ev.Seats[s].Pool.Clear();
                _ev.Seats[s].Pool.AddRange(draft.Picks[s]);
            }
            _draft = null;
            _ev.Draft = null;
        }
        for (int s = 0; s < _ev.Seats.Count; s++)
        {
            _ready[s] = !IsPerson(s);
            if (!IsPerson(s)) _ev.Seats[s].Deck = _rules.AutoBuild(_ev.Seats[s].Pool);
        }
        _ev.Stage = EventStage.Building;
        _deadline = Now + _options.BuildTime;
    }

    // ------------------------------------------------------------------ deck building

    private void PollBuilding()
    {
        if (!_ready.All(r => r) && Now < _deadline) return;
        // Time's up: an unfinished or unplayable deck is built automatically.
        for (int s = 0; s < _ev.Seats.Count; s++)
        {
            var seat = _ev.Seats[s];
            if (seat.Deck is null || _rules.CheckDeck(seat.Deck, seat.Pool) is not null) seat.Deck = _rules.AutoBuild(seat.Pool);
            _problems[s] = null;
            _ready[s] = false;
        }
        _ev.Stage = EventStage.Playing;
        NextRound();
        MarkChanged();
    }

    // ------------------------------------------------------------------ rounds

    private void NextRound()
    {
        while (true)
        {
            // The Swiss rounds, then the playoff of a top cut.
            if (_ev.Complete)
            {
                _ev.Stage = EventStage.Finished;
                return;
            }
            var round = _ev.PairNextRound(_random);
            foreach (var match in round.Where(m => !m.Done))
            {
                if (IsPerson(match.SeatA) || IsPerson(match.SeatB!.Value))
                {
                    // A person who isn't at the table yet: the match waits for them (see LimitedEvent.SeatCall).
                    if (Away(match).Count > 0)
                    {
                        match.Waiting = true;
                        match.Due = Now;
                        match.Started ??= Now; // the round's clock runs all the same
                    }
                    else StartGame(match);
                }
                else
                    while (!match.Done) // two computer players: settled by their decks' strength
                    {
                        double a = _rules.Strength(_ev.Seats[match.SeatA].Deck!), b = _rules.Strength(_ev.Seats[match.SeatB!.Value].Deck!);
                        _ev.RecordGame(match, _random.NextDouble() < a / (a + b) ? match.SeatA : match.SeatB);
                    }
            }
            if (round.Any(m => !m.Done)) return;
        }
    }

    /// <param name="timeCalled">A game being resumed whose round's time had been called (at that turn).</param>
    private void StartGame(EventMatch match, ulong? seed = null, string?[]? tokens = null, IReadOnlyList<RecordedAnswer>? replay = null, int? timeCalled = null)
    {
        var order = new[] { match.SeatA, match.SeatB!.Value };
        tokens ??= order.Select(s => IsPerson(s) ? Tokens.New() : null).ToArray();
        var seats = order.Select((s, i) => new HostSeat(_ev.Seats[s].Name, _rules.Cards(_ev.Seats[s].Deck!), null, !IsPerson(s), tokens[i],
            _rules.Cards(_ev.Seats[s].SideboardFromPool()))).ToList();
        seed ??= (ulong)_random.NextInt64();
        var host = new GameHost(new GameConfig { Seed = seed.Value, CardNames = _options.Games.CardNames, NonbasicLandNames = _options.Games.NonbasicLandNames, CreatureCardNames = _options.Games.CreatureCardNames }, seats, _options.Games with { Clock = _options.Clock, Replay = replay });
        _games.Add(new ActiveGame { Match = match, Host = host, Seed = seed.Value, Tokens = tokens });
        _between.Remove(match);
        foreach (var s in order) _ready[s] = false;
        // The round's clock runs from the match's first game.
        match.Started ??= Now;
        // Before the game runs, so a resumed game ends where it would have.
        if (timeCalled is { } turn) host.Game.CallTime(turn);
        host.Start();
        MarkChanged();
    }

    private void PollRounds()
    {
        foreach (var game in _games.ToList())
        {
            // The round's time is up: the end-of-match procedure begins (the turn in progress, then the additional turns).
            if (game.Host.Game.TimeCalledAt is null && _ev.TimeUp(game.Match, Now)) game.Host.Game.CallTime();
            game.Host.Poll();
            if (game.Host.Running is not { IsCompleted: true } run) continue;
            var match = game.Match;
            int? winner = run.IsCompletedSuccessfully && game.Host.Game.State.Winner is { } w ? (w.Value == 0 ? match.SeatA : match.SeatB) : null;
            game.Host.Poll(); // last views out
            game.Host.Close();
            _games.Remove(game);
            _ev.RecordGame(match, winner, _ev.TimeUp(match, Now));
            if (!match.Done)
            {
                _between[match] = Now + _options.SideboardTime;
                foreach (var s in new[] { match.SeatA, match.SeatB!.Value }) _ready[s] = !IsPerson(s);
            }
            MarkChanged();
        }

        // Matches waiting for a late player: a game loss after the seat call, a match loss when they don't show up.
        foreach (var match in _ev.Rounds.LastOrDefault()?.Where(m => m.Waiting).ToList() ?? new())
        {
            var away = Away(match);
            if (away.Count == 0)
            {
                match.Waiting = false;
                if (!match.Done) StartGame(match);
                MarkChanged();
            }
            else if (Now >= match.Due!.Value + LimitedEvent.NoShowAfter)
            {
                _ev.NoShow(match, away);
                MarkChanged();
            }
            else if (Now >= match.Due!.Value + LimitedEvent.SeatCall && match.Late.Count == 0 && !match.Done)
            {
                _ev.GiveGameLoss(match, away);
                MarkChanged();
            }
        }

        // The next game of a match starts when both players are ready (or when sideboarding time is up); none once the
        // round's time is up: the match ends with the games played.
        foreach (var (match, until) in _between.ToList())
            if (_ev.TimeUp(match, Now))
            {
                _ev.EndOnTime(match);
                _between.Remove(match);
                MarkChanged();
            }
            else if ((_ready[match.SeatA] && _ready[match.SeatB!.Value]) || Now >= until)
                StartGame(match);

        if (_games.Count == 0 && _between.Count == 0 && _ev.Rounds.LastOrDefault()?.All(m => m.Done && !m.Waiting) != false)
        {
            if (_options.DayEndsAfter > 0 && _ev.Rounds.Count == _options.DayEndsAfter && !_ev.IsPlayoff(_ev.Rounds.Count - 1))
            {
                // The end of the day: the cut is known now, the rounds go on the next day.
                if (DayOver) return;
                _ev.ApplyCut();
                // Nobody left to play the next day (every person was cut): the computer players' rounds are settled now.
                if (Enumerable.Range(0, _ev.Seats.Count).All(s => !IsPerson(s) || _ev.Dropped.Contains(s)))
                {
                    NextRound();
                    MarkChanged();
                    return;
                }
                DayOver = true;
                MarkChanged();
                DayEnded?.Invoke();
                return;
            }
            NextRound();
            MarkChanged();
        }
    }

    // ------------------------------------------------------------------ what each player sees

    private void SendUpdates()
    {
        _dirty = false;
        for (int seat = 0; seat < _peers.Length; seat++) _peers[seat]?.Send(new EventUpdate(InfoFor(seat)));
    }

    public EventInfo InfoFor(int seat)
    {
        var me = _ev.Seats[seat];
        DraftInfo? draft = _draft is { } d
            ? new DraftInfo(d.Round, d.Rounds, d.PickInRound, d.PassesLeft, d.PackFor(seat).ToList(), d.Picks[seat].ToList(), d.HasPicked(seat))
            : null;
        var game = _games.FirstOrDefault(g => g.Match.Involves(seat));
        string? token = game is null ? null : game.Tokens[game.Match.SeatA == seat ? 0 : 1];
        DateTime? until = _ev.Stage switch
        {
            EventStage.Drafting when draft is { Picked: false } => _deadline,
            EventStage.Building when !_ready[seat] => _deadline,
            EventStage.Playing => _between.FirstOrDefault(kv => kv.Key.Involves(seat)) is { Key: not null } kv && !_ready[seat] ? kv.Value : null,
            _ => null,
        };
        int seconds = until is { } u ? Math.Max(0, (int)Math.Ceiling((u - Now).TotalSeconds)) : -1;
        bool Ready(int s) => _ev.Stage == EventStage.Drafting ? _draft?.HasPicked(s) == true : _ready[s];
        return new EventInfo(seat, Title, _ev.Mode, _ev.Source, _ev.BestOf, _ev.RoundsTotal, _ev.Stage,
            _ev.Seats.Select((s, i) => new EventSeatInfo(s.Name, !s.IsHuman, !s.IsHuman || _peers[i] is not null, Ready(i))).ToList(),
            _ev.Rounds.Select(r => (IReadOnlyList<MatchInfo>)r.Select(m => new MatchInfo(m.SeatA, m.SeatB, m.WinsA, m.WinsB, m.Draws, m.Done,
                _games.Any(g => g.Match == m), m.Started, m.Waiting, m.Due, m.Late.Count > 0 ? m.Late.ToList() : null, m.BothLost)).ToList()).ToList(),
            draft, _ev.Stage == EventStage.Drafting ? Array.Empty<PoolCard>() : me.Pool.ToList(), me.Deck?.Export(), _problems[seat], _ready[seat],
            seconds, token, _ev.TopCut, _ev.CutAfter, _ev.CutPoints, _ev.RoundMinutes, _ev.Dropped.ToList(), me.Registered?.Export(),
            DayOver ? _options.NextDay : null);
    }

    /// <summary>Shown to players: "Draft · Foundations" and so on.</summary>
    public string Title { get; init; } = "";

    // ------------------------------------------------------------------ saving

    /// <summary>Everything needed to resume the event on this host later (also the games being played).</summary>
    public string Save()
    {
        if (_draft is not null) _ev.Draft = _draft.Snapshot();
        var root = new JsonObject
        {
            ["event"] = JsonNode.Parse(_ev.ToJson()),
            ["title"] = Title,
            ["tokens"] = new JsonArray(_tokens.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["games"] = new JsonArray(_games.Select(g => (JsonNode?)new JsonObject
            {
                ["round"] = _ev.Rounds.Count - 1,
                ["match"] = _ev.Rounds[^1].IndexOf(g.Match),
                ["seed"] = g.Seed,
                ["tokens"] = new JsonArray(g.Tokens.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["answers"] = JsonSerializer.SerializeToNode(g.Host.Answers),
                ["timeCalled"] = g.Host.Game.TimeCalledAt,
            }).ToArray()),
        };
        return root.ToJsonString();
    }

    /// <summary>The seat a token belongs to, or -1.</summary>
    public int SeatOf(string token) => Array.FindIndex(_tokens, t => t is not null && Tokens.Same(t, token));

    /// <summary>Resumes a saved event: the draft, deck building or rounds go on; games in progress are replayed.</summary>
    public static EventHost Restore(string json, EventRules rules, EventOptions? options = null)
    {
        var root = JsonNode.Parse(json)!;
        var ev = LimitedEvent.FromJson(root["event"]!.ToJsonString());
        var tokens = root["tokens"]!.AsArray().Select(t => t?.GetValue<string>()).ToList();
        var host = new EventHost(ev, tokens, rules, options) { Title = root["title"]?.GetValue<string>() ?? "" };
        switch (ev.Stage)
        {
            case EventStage.Drafting:
                host._draft = DraftSession.Restore(ev.Draft!, rules.Boosters(ev.Draft), host._random);
                host.NewPick();
                break;
            case EventStage.Building:
                for (int s = 0; s < ev.Seats.Count; s++) host._ready[s] = !ev.Seats[s].IsHuman;
                host._deadline = host.Now + host._options.BuildTime;
                break;
            case EventStage.Playing:
                var resumed = new HashSet<EventMatch>();
                foreach (var g in root["games"]!.AsArray())
                {
                    var match = ev.Rounds[g!["round"]!.GetValue<int>()][g["match"]!.GetValue<int>()];
                    var gameTokens = g["tokens"]!.AsArray().Select(t => t?.GetValue<string>()).ToArray();
                    var answers = g["answers"].Deserialize<List<RecordedAnswer>>() ?? new();
                    host.StartGame(match, g["seed"]!.GetValue<ulong>(), gameTokens, answers, g["timeCalled"]?.GetValue<int>());
                    resumed.Add(match);
                }
                // Matches of this round between games: the next game starts after the usual wait.
                foreach (var match in ev.Rounds.LastOrDefault() ?? new List<EventMatch>())
                    if (!match.Done && !match.Waiting && !resumed.Contains(match)) host._between[match] = host.Now + host._options.SideboardTime;
                break;
        }
        return host;
    }
}
