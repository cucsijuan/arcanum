// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Bots;
using Arcanum.Engine;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Host;

/// <summary>
/// One seat of a hosted game, as the engine sees it: decisions go to the connected player, or to the computer while
/// the player is away (after the grace period) or if the seat was the computer's from the start. Answers from the
/// network are checked here, with the engine's own request, so a wrong or hostile answer is asked again instead of
/// reaching the game.
/// </summary>
internal sealed class Seat : IPlayerController
{
    private readonly GameHost _host;
    private Game _game = null!;
    private BotController? _computer;
    private Func<CardId, Engine.Cards.CardDefinition?> _rules = _ => null;
    private Pending? _pending;
    private int _nextAsk;
    private int _invalidInARow;
    private DateTime? _disconnectedAt;
    private string? _lastView;

    public PlayerId Id { get; }
    public bool IsComputerSeat { get; }
    public string Token { get; }

    public Peer? Peer { get; private set; }
    public CardAliases Aliases { get; private set; } = new();
    public bool ViewDirty { get; set; } = true;

    /// <summary>The computer plays this seat for now (the person dropped, or the host replaced them).</summary>
    public bool ComputerPlays { get; private set; }

    /// <summary>Priority stops of the person (sent by their client); used to pass priority for them.</summary>
    public AutoPassPolicy Policy { get; } = new();

    public Seat(GameHost host, PlayerId id, bool isComputer, string? token)
    {
        _host = host;
        Id = id;
        IsComputerSeat = isComputer;
        Token = token ?? Tokens.New();
        if (!isComputer) _disconnectedAt = host.Options.Clock();
    }

    public void UseCardRules(Game game)
    {
        _game = game;
        _rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
    }

    private bool ComputerDecides => IsComputerSeat || ComputerPlays;

    private BotController Computer
    {
        get
        {
            if (_computer is not null) return _computer;
            _computer = new BotController(Id) { Pace = _host.Options.ComputerPace };
            _computer.UseCardRules(_rules);
            return _computer;
        }
    }

    public SeatState State => ComputerDecides ? SeatState.Computer : Peer is null ? SeatState.Disconnected : SeatState.Connected;

    public SeatStatus Status()
    {
        int left = 0;
        if (State == SeatState.Disconnected)
            left = _host.Options.Grace is { } grace && _disconnectedAt is { } since
                ? Math.Max(0, (int)Math.Ceiling((since + grace - _host.Options.Clock()).TotalSeconds))
                : -1;
        return new SeatStatus(Id, State, left);
    }

    // ------------------------------------------------------------------ connection

    public void Connect(IConnection connection, IEnumerable<NetMessage>? early = null)
    {
        Peer?.Close();
        Aliases = new CardAliases(); // a new connection never learns ids given to an earlier one
        Peer = new Peer(connection, new WireFormat(Aliases), _host.Options.Clock);
        if (early is not null) Peer.Hold(early);
        _disconnectedAt = null;
        _lastView = null;
        _invalidInARow = 0;
        ViewDirty = true;
        if (!IsComputerSeat) ComputerPlays = false; // back in charge from the next decision
        _host.SendWelcome(this);
        PushView();
        if (_pending is not null)
        {
            _pending.Deadline = null; // the full time again after reconnecting
            SendAsk(_pending);
        }
        _host.NotifySeatChanged(this);
    }

    public void Poll()
    {
        if (Peer is not null)
        {
            foreach (var message in Peer.Receive())
            {
                switch (message)
                {
                    case Answer answer: OnAnswer(answer); break;
                    case Stops stops: ApplyStops(stops); break;
                }
                if (Peer is null) return;
            }
            if (!Peer.IsOpen)
            {
                Peer = null;
                _disconnectedAt = _host.Options.Clock();
                _host.NotifySeatChanged(this);
            }
            else if (_pending is { Deadline: { } deadline } && _host.Options.Clock() >= deadline) Expire();
            return;
        }
        if (!ComputerDecides && _disconnectedAt is { } since && _host.Options.Grace is { } grace && _host.Options.Clock() - since >= grace)
            TakeOver(onlyIfDisconnected: true);
    }

    /// <summary>The computer plays this seat from now on (until the person reconnects).</summary>
    public bool TakeOver(bool onlyIfDisconnected)
    {
        if (ComputerDecides || (onlyIfDisconnected && Peer is not null)) return false;
        ComputerPlays = true;
        _host.NotifySeatChanged(this);
        var pending = _pending;
        _pending = null;
        pending?.Abandon(); // the computer answers the question the person left open
        return true;
    }

    public void PushView()
    {
        if (Peer is null || !ViewDirty) return;
        ViewDirty = false;
        var json = Peer.Format.Write(new ViewUpdate(_game.ViewFor(Id)));
        if (json == _lastView) return;
        _lastView = json;
        Peer.SendRaw(json);
    }

    private void ApplyStops(Stops stops)
    {
        Policy.OwnTurnStops.Clear();
        Policy.OwnTurnStops.UnionWith(stops.OwnTurn);
        Policy.OpponentTurnStops.Clear();
        Policy.OpponentTurnStops.UnionWith(stops.OpponentTurn);
        Policy.FullControl = stops.FullControl;
        if (stops.PassingTurn is { } turn) Policy.PassTurn(turn);
        else Policy.CancelPassTurn();
    }

    // ------------------------------------------------------------------ questions

    /// <summary>A question waiting for the person's answer.</summary>
    private sealed class Pending(int id, Question question, Func<JsonElement, WireFormat, string?> check, Action complete, Action abandon)
    {
        public int Id { get; } = id;
        public Question Question { get; } = question;
        public string? Error { get; set; }

        /// <summary>When the player's time to answer runs out (null: no limit, or not sent to them yet).</summary>
        public DateTime? Deadline { get; set; }

        /// <summary>Reads and checks an answer; null when it's acceptable (then call <see cref="Complete"/>).</summary>
        public string? Check(JsonElement value, WireFormat format) => check(value, format);

        public void Complete() => complete();

        public void Abandon() => abandon();
    }

    private void SendAsk(Pending pending)
    {
        if (Peer is null) return;
        PushView();
        var now = _host.Options.Clock();
        if (_host.Options.DecisionTime is { } limit) pending.Deadline ??= now + limit;
        int seconds = pending.Deadline is { } d ? Math.Max(0, (int)Math.Ceiling((d - now).TotalSeconds)) : -1;
        Peer.Send(new Ask(pending.Id, pending.Question, pending.Error, seconds));
    }

    /// <summary>The player took too long: the computer makes this one decision for them.</summary>
    private void Expire()
    {
        var pending = _pending!;
        _pending = null;
        Peer?.Send(new AskExpired(pending.Id));
        pending.Abandon();
    }

    private void OnAnswer(Answer answer)
    {
        var pending = _pending;
        if (pending is null || pending.Id != answer.Ask || Peer is null) return; // stale (already answered or taken over)
        var error = pending.Check(answer.Value, Peer.Format);
        if (error is null)
        {
            _invalidInARow = 0;
            _pending = null;
            _host.MarkViewsDirty();
            pending.Complete(); // the game goes on from here, and may ask this seat again
            return;
        }
        if (++_invalidInARow >= _host.Options.MaxInvalidAnswers)
        {
            Peer.Close();
            Peer = null;
            _disconnectedAt = _host.Options.Clock();
            TakeOver(onlyIfDisconnected: false);
            return;
        }
        pending.Error = error;
        SendAsk(pending);
    }

    /// <summary>Replayed (resuming a game), or decided now; recorded either way.</summary>
    private async Task<T> Decide<T>(Func<Task<T>> decide)
    {
        if (!_host.TryReplay(Id, out T answer)) answer = await decide();
        _host.Record(Id, answer);
        return answer;
    }

    /// <summary>Asks the person (checking their answer), or the computer when it plays the seat.</summary>
    private Task<T> Ask<T>(Question question, Func<T, string?> check, Func<BotController, Task<T>> computer) =>
        Decide(async () =>
        {
            if (!ComputerDecides)
            {
                var (answered, value) = await AskPerson(question, check);
                if (answered) return value;
            }
            return await computer(Computer);
        });

    private Task<(bool Answered, T Value)> AskPerson<T>(Question question, Func<T, string?> check)
    {
        var result = new TaskCompletionSource<(bool, T)>();
        T parsed = default!;
        var pending = new Pending(++_nextAsk, question,
            (element, format) =>
            {
                try
                {
                    parsed = format.FromElement<T>(element)!;
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
                {
                    return "That choice is no longer possible.";
                }
                return check(parsed);
            },
            () => result.TrySetResult((true, parsed)),
            () => result.TrySetResult((false, default!)));
        _pending = pending;
        SendAsk(pending);
        return result.Task;
    }

    private GameState S => _game.State;

    private static string? Distinct(IReadOnlyList<CardId>? ids) =>
        ids is not null && ids.Distinct().Count() != ids.Count ? "The same card was chosen twice." : null;

    private string? FromHand(IReadOnlyList<CardId>? chosen, int count) =>
        chosen is null || chosen.Count != count ? $"Choose exactly {count} card{(count == 1 ? "" : "s")}."
        : Distinct(chosen) ?? (chosen.All(S.GetPlayer(Id).Hand.Contains) ? null : "Choose cards from your hand.");

    // ------------------------------------------------------------------ IPlayerController

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) =>
        Ask(new KeepHandQuestion(mulligansTaken), (bool _) => null, c => c.KeepHandAsync(view, mulligansTaken));

    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
        Ask(new BottomQuestion(count), (IReadOnlyList<CardId> c) => FromHand(c, count), c => c.ChooseCardsToBottomAsync(view, count));

    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) =>
        Decide(async () =>
        {
            // Passing by the person's own stops is decided here, not over the network each time.
            if (!ComputerDecides && Policy.ShouldAutoPass(view, legalActions)) return PassPriority.Instance;
            if (!ComputerDecides)
            {
                var (answered, value) = await AskPerson(new ActionQuestion(legalActions),
                    (PlayerAction a) => a is not null && legalActions.Contains(a) ? null : "That action isn't possible now.");
                if (answered) return value;
            }
            return await Computer.ChooseActionAsync(view, legalActions);
        });

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        Ask(new ManaQuestion(request), (IReadOnlyList<ManaTap>? taps) => CheckPayment(request, taps), c => c.ChooseManaPaymentAsync(view, request));

    private string? CheckPayment(ManaPaymentRequest request, IReadOnlyList<ManaTap>? taps)
    {
        if (taps is null) return null; // cancel
        if (taps.Select(t => t.Source).Distinct().Count() != taps.Count) return "Each source can be tapped only once.";
        if (!taps.All(t => request.Sources.Any(s => s.Source == t.Source && s.Option == t.Option && s.Types.Contains(t.Type))))
            return "That source can't pay for this.";
        var (owed, excess) = ManaPayment.Apply(request.RemainingAfterPool, ManaPayment.Produced(S, taps).ToList());
        if (owed.ManaValue != 0) return $"Payment is short by {owed}.";
        if (excess != 0 && !taps.Any(t => ManaPayment.AmountOf(S.GetCard(t.Source), t.Option) > 1)) return "That taps more mana than the cost.";
        return null;
    }

    public Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request)
    {
        // Rules between targets stay on the host (they need the game); the player's choice is checked against them.
        var sent = request with
        {
            Specs = request.Specs.Select(s => s with { Filter = null, Text = s.Describe() }).ToList(),
            Allowed = null,
        };
        return Ask(new TargetsQuestion(sent), (IReadOnlyList<Target>? chosen) =>
        {
            if (chosen is null) return request.CanCancel ? null : "These targets must be chosen.";
            if (!request.IsComplete(chosen.Count) || (!request.LastIsAnyNumber && chosen.Count != request.Legal.Count))
                return $"Choose {request.Legal.Count} target{(request.Legal.Count == 1 ? "" : "s")}.";
            for (int i = 0; i < chosen.Count; i++)
                if (!request.IsAllowed(i, chosen[i], chosen.Take(i).ToList())) return "Those targets can't be chosen together.";
            return null;
        }, c => c.ChooseTargetsAsync(view, request));
    }

    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        Ask(new AttackQuestion(possibleAttackers, defenders), (IReadOnlyList<AttackDeclaration> declared) =>
        {
            if (declared is null) return "Declare attackers.";
            if (declared.Select(d => d.Attacker).Distinct().Count() != declared.Count) return "A creature can attack only once.";
            if (!declared.All(d => possibleAttackers.Contains(d.Attacker) && defenders.Contains(d.Defender))) return "That creature can't attack that player.";
            if (!declared.All(d => d.Planeswalker is not { } pw
                                   || S.Cards.TryGetValue(pw, out var w) && w.Zone == Zone.Battlefield && w.Is(Engine.Cards.CardType.Planeswalker) && w.Controller == d.Defender))
                return "A planeswalker can only be attacked through its controller.";
            return null;
        }, c => c.DeclareAttackersAsync(view, possibleAttackers, defenders));

    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) =>
        Ask(new BlockQuestion(request), (IReadOnlyList<BlockDeclaration> blocks) =>
            blocks is null ? "Declare blockers." : request.IsLegal(blocks, out var reason) ? null : reason ?? "Illegal blocks.",
            c => c.DeclareBlockersAsync(view, request));

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Ask(new DamageQuestion(request), (DamageAssignment a) =>
        {
            if (a?.ToBlockers is null) return "Assign the damage.";
            if (!a.ToBlockers.Keys.All(request.Lethal.ContainsKey)) return "Damage can only be assigned to blocking creatures.";
            if (a.ToBlockers.Values.Any(v => v < 0) || a.ToPlayer < 0) return "Damage amounts can't be negative.";
            if (a.Total != request.Power) return $"Assign exactly {request.Power} damage.";
            if (a.ToPlayer != 0 && !request.Trample) return "Only trample can assign damage to the player.";
            if (a.ToPlayer != 0 && !request.Lethal.All(kv => a.ToBlockers.GetValueOrDefault(kv.Key) >= kv.Value))
                return "Assign lethal damage to every blocker before the player.";
            return null;
        }, c => c.AssignCombatDamageAsync(view, request));

    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) =>
        Ask(new YesNoQuestion(request), (bool _) => null, c => c.ChooseYesNoAsync(view, request));

    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) =>
        Ask(new DiscardQuestion(count), (IReadOnlyList<CardId> c) => FromHand(c, count), c => c.ChooseDiscardAsync(view, count));

    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) =>
        Ask(new CardsQuestion(request), (IReadOnlyList<CardId> chosen) =>
        {
            if (chosen is null || chosen.Count < request.Min || chosen.Count > request.Max)
                return request.Min == request.Max ? $"Choose {request.Min}." : $"Choose from {request.Min} to {request.Max}.";
            return Distinct(chosen) ?? (chosen.All(id => request.Options.Any(o => o.Id == id)) ? null : "Choose among the cards shown.");
        }, c => c.ChooseCardsAsync(view, request));

    public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) =>
        Ask(new ModesQuestion(request), (IReadOnlyList<int>? modes) =>
        {
            if (modes is null) return request.CanCancel ? null : "Modes must be chosen.";
            return modes.Count >= request.Min && modes.Count <= request.Max && modes.Distinct().Count() == modes.Count && modes.All(request.Possible.Contains)
                ? null : "Choose among the possible modes.";
        }, c => c.ChooseModesAsync(view, request));

    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) =>
        Ask(new NumberQuestion(request), (int n) => n >= request.Min && n <= request.Max ? null : $"Choose a number from {request.Min} to {request.Max}.",
            c => c.ChooseNumberAsync(view, request));

    public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) =>
        Ask(new OptionQuestion(request), (int i) => i >= 0 && i < request.Options.Count ? null : "Choose one of the options.",
            c => c.ChooseOptionAsync(view, request));
}
