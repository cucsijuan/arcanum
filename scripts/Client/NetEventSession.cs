// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Mana;
using Arcanum.Net.Events;
using Arcanum.Net.Protocol;

namespace Arcanum.Client;

/// <summary>
/// This player's seat in an online limited event run by a host. The event is rebuilt from each update the host sends;
/// the deck being edited stays on this device (and is sent to the host after every change, so the host uses the latest
/// one if time runs out).
/// </summary>
public sealed class NetEventSession : ILimitedSession
{
    private EventClient _client;
    private EventInfo? _info;
    private DateTime _receivedAt;
    private DeckList? _deck;
    private string? _sentDeck;

    public NetEventSession(EventClient client)
    {
        _client = client;
        Wire(client);
    }

    public EventClient Client => _client;

    /// <summary>Continues on a new connection to the event (after a drop).</summary>
    public void Attach(EventClient client)
    {
        _client.Changed -= OnUpdate;
        _client = client;
        Wire(client);
    }

    private void Wire(EventClient client)
    {
        client.Changed += OnUpdate;
        if (client.Event is { } info) OnUpdate(info);
    }

    public EventInfo? Info => _info;

    private void OnUpdate(EventInfo info)
    {
        var previousStage = _info?.Stage;
        _info = info;
        _receivedAt = DateTime.UtcNow;
        // The host's copy of the deck is the one that counts once building is over (or when nothing was edited here).
        if (info.Deck is { } list && (_deck is null || info.Stage != previousStage) && list != _sentDeck)
        {
            try { _deck = DeckList.Parse(list); } catch (FormatException) { }
        }
        Current = Build(info);
        Changed?.Invoke();
    }

    private LimitedEvent Build(EventInfo info) => new()
    {
        Mode = info.Mode, Source = info.Source, BestOf = info.BestOf, RoundsTotal = info.RoundsTotal, Stage = info.Stage,
        Seats = info.Seats.Select((s, i) => new EventSeat
        {
            Name = s.Name,
            IsHuman = i == info.You,
            Pool = i == info.You ? info.Pool.ToList() : new List<PoolCard>(),
            Deck = i == info.You ? _deck : null,
        }).ToList(),
        Rounds = info.Rounds.Select(r => r.Select(m => new EventMatch
        {
            SeatA = m.A, SeatB = m.B, WinsA = m.WinsA, WinsB = m.WinsB, Draws = m.Draws, Done = m.Done,
        }).ToList()).ToList(),
    };

    public LimitedEvent? Current { get; private set; }

    public int Seat => _info?.You ?? 0;

    public DraftView? DraftState => _info?.Draft is { } d
        ? new DraftView(d.Round, d.Rounds, d.Pick, d.PassesLeft, d.Pack, d.Picks, d.Picked)
        : null;

    public bool IsOnline => true;

    public int SecondsLeft => _info is { SecondsLeft: >= 0 } info
        ? Math.Max(0, info.SecondsLeft - (int)(DateTime.UtcNow - _receivedAt).TotalSeconds)
        : -1;

    public string? Status => _info switch
    {
        null => "Waiting for the host…",
        { DeckProblem: { } problem } => $"The host can't accept your deck: {problem}",
        { Stage: EventStage.Drafting, Draft.Picked: true } => "Waiting for the other players to pick…",
        { Stage: EventStage.Building, Ready: true } => "Deck sent. Waiting for the other players to finish theirs…",
        _ => null,
    };

    public bool GameInProgress => _info?.GameToken is not null;

    public bool WaitingForOpponent => _info is { Stage: EventStage.Playing, Ready: true, GameToken: null };

    public event Action? Changed;

    public void Pick(int index) => _client.Pick(index);

    public void DeckEdited()
    {
        _deck = Current?.Seats[Seat].Deck;
        if (_deck is null) return;
        _sentDeck = _deck.Export();
        _client.SubmitDeck("Event deck", _sentDeck);
    }

    public void FinishBuilding()
    {
        DeckEdited();
        _client.Ready();
    }

    public void PlayNext() => _client.Ready();

    public void ReturnToGame() => App.Instance.Online.ReturnToGame();

    public void Abandon() => App.Instance.Online.Leave();

    public DeckList AutoBuild(IReadOnlyList<PoolCard> pool) => LimitedService.AutoBuild(pool, Current?.Source);

    public DeckEntry BasicLand(ManaType color) => LimitedService.BasicLand(color, Current?.Source);

    public List<DeckIssue> Validate(EventSeat seat) => LimitedService.ValidateSeat(seat);
}
