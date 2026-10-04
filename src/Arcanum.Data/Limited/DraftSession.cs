// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.Limited;

/// <summary>
/// A booster draft: every seat opens a booster, takes one card and passes the rest, until the boosters are empty;
/// then the next round of boosters is opened. Boosters go to the left in the first and third rounds and to the
/// right in the second (seats are numbered clockwise, so "left" is the next seat).
/// </summary>
public sealed class DraftSession
{
    private readonly IBoosterSource _source;
    private readonly Random _random;
    private List<PoolCard>[] _packs;
    private readonly int?[] _pending;

    public int Seats { get; }
    public int Rounds { get; }

    /// <summary>The current round, from 0; equals <see cref="Rounds"/> once the draft is over.</summary>
    public int Round { get; private set; }

    /// <summary>Picks made so far in this round (the same for every seat).</summary>
    public int PickInRound { get; private set; }

    /// <summary>Each seat's drafted cards, in pick order.</summary>
    public IReadOnlyList<List<PoolCard>> Picks { get; }

    public bool IsComplete => Round >= Rounds;

    /// <summary>Boosters move to the next seat in rounds 1 and 3, to the previous one in round 2.</summary>
    public bool PassesLeft => Round % 2 == 0;

    public DraftSession(IBoosterSource source, int seats, int rounds, Random random)
    {
        if (seats < 2) throw new ArgumentOutOfRangeException(nameof(seats), "A draft needs at least two seats.");
        _source = source;
        _random = random;
        Seats = seats;
        Rounds = rounds;
        _pending = new int?[seats];
        Picks = Enumerable.Range(0, seats).Select(_ => new List<PoolCard>()).ToList();
        _packs = OpenRound();
    }

    private DraftSession(IBoosterSource source, Random random, int seats, int rounds, int round, int pick, List<PoolCard>[] packs, List<List<PoolCard>> picks)
    {
        _source = source;
        _random = random;
        Seats = seats;
        Rounds = rounds;
        Round = round;
        PickInRound = pick;
        _packs = packs;
        _pending = new int?[seats];
        Picks = picks;
    }

    private List<PoolCard>[] OpenRound() => Enumerable.Range(0, Seats).Select(_ => _source.Open(_random)).ToArray();

    /// <summary>The booster in front of a seat now.</summary>
    public IReadOnlyList<PoolCard> PackFor(int seat) => IsComplete ? Array.Empty<PoolCard>() : _packs[seat];

    /// <summary>Whether a seat has already chosen its card for this pick.</summary>
    public bool HasPicked(int seat) => _pending[seat] is not null;

    /// <summary>
    /// A seat takes the card at <paramref name="index"/> of its booster. When every seat has picked, the cards are
    /// taken at the same time and the boosters are passed (or the next round is opened).
    /// </summary>
    public void Pick(int seat, int index)
    {
        if (IsComplete) throw new InvalidOperationException("The draft is over.");
        if (index < 0 || index >= _packs[seat].Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_pending[seat] is not null) throw new InvalidOperationException("This seat already picked.");
        _pending[seat] = index;
        if (_pending.Any(p => p is null)) return;

        for (int s = 0; s < Seats; s++)
        {
            var card = _packs[s][_pending[s]!.Value];
            _packs[s].RemoveAt(_pending[s]!.Value);
            Picks[s].Add(card);
            _pending[s] = null;
        }
        PickInRound++;
        if (_packs.All(p => p.Count == 0))
        {
            Round++;
            PickInRound = 0;
            if (!IsComplete) _packs = OpenRound();
            return;
        }
        // Seat s receives the booster from the seat on its right when passing left (s - 1), from its left otherwise.
        var passed = new List<PoolCard>[Seats];
        for (int s = 0; s < Seats; s++)
            passed[s] = _packs[PassesLeft ? (s - 1 + Seats) % Seats : (s + 1) % Seats];
        _packs = passed;
    }

    /// <summary>Everything needed to resume the draft later.</summary>
    public DraftSnapshot Snapshot() => new(Seats, Rounds, Round, PickInRound,
        _packs.Select(p => p.ToList()).ToList(), Picks.Select(p => p.ToList()).ToList());

    /// <summary>Resumes a draft; <paramref name="source"/> opens the boosters of later rounds.</summary>
    public static DraftSession Restore(DraftSnapshot snapshot, IBoosterSource source, Random random) =>
        new(source, random, snapshot.Seats, snapshot.Rounds, snapshot.Round, snapshot.PickInRound,
            snapshot.Packs.Select(p => p.ToList()).ToArray(), snapshot.Picks.Select(p => p.ToList()).ToList());
}

/// <summary>A draft in progress, as saved with the event.</summary>
public sealed record DraftSnapshot(int Seats, int Rounds, int Round, int PickInRound, IReadOnlyList<List<PoolCard>> Packs, IReadOnlyList<List<PoolCard>> Picks);
