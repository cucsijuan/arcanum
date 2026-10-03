// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Client;

/// <summary>
/// Records every answer given for the local seats, in order. Because the engine is deterministic, replaying
/// these answers into a fresh game with the same seed rebuilds the exact same position, which is how undo works.
/// </summary>
public sealed class DecisionLog
{
    public readonly record struct Entry(object? Answer, bool Manual);

    private readonly Queue<Entry> _replay;

    public List<Entry> Entries { get; } = new();

    public DecisionLog(IEnumerable<Entry>? replay = null) => _replay = new Queue<Entry>(replay ?? Array.Empty<Entry>());

    public bool IsReplaying => _replay.Count > 0;

    /// <param name="live">Produces the answer when not replaying, plus whether a person made it (vs. auto-pass).</param>
    public async Task<T> Record<T>(Func<Task<(T Answer, bool Manual)>> live)
    {
        if (_replay.TryDequeue(out var recorded))
        {
            Entries.Add(recorded);
            return (T)recorded.Answer!;
        }
        var (answer, manual) = await live();
        Entries.Add(new Entry(answer, manual));
        return answer;
    }
}
