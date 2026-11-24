// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots.Simulation;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Bots;

public sealed partial class BotController
{
    /// <summary>
    /// When set, the bot tries its main decisions (what to do first in its main phases, how to attack and block, its instants
    /// and abilities when answering the stack, after blocks and at an opponent's end step) by playing the game
    /// on from each option (<see cref="Simulator"/>) instead of trusting its rules of thumb alone.
    /// </summary>
    public Simulator? Search { get; set; }

    /// <summary>How much better (in average score) another option must do to replace the rules of thumb's choice.</summary>
    public double SearchMargin { get; set; } = 0.03;

    /// <summary>Searched decisions across all bots and the time they took (for measuring).</summary>
    public static int SearchedDecisions;
    public static long SearchTicks;
    public static long LongestSearchTicks;
    public static int GaveUp;

    private async Task<double[]?> TimedScoreAsync<T>(IReadOnlyList<T> options, Func<T, IPlayerController, IPlayerController> answer, int seed)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var scores = await Search!.ScoreAsync(options, answer, seed);
        // A device too slow to finish one round in time gains nothing from searching, and rebuilding the game only takes
        // longer as it goes on: after a few such decisions in a row the bot stops searching for the rest of the game.
        if (scores is not null) _outOfTimeInARow = 0;
        else if (++_outOfTimeInARow >= GiveUpAfter)
        {
            Search = null;
            Interlocked.Increment(ref GaveUp);
        }
        long took = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref SearchedDecisions);
        Interlocked.Add(ref SearchTicks, took);
        long longest;
        while (took > (longest = Interlocked.Read(ref LongestSearchTicks)) && Interlocked.CompareExchange(ref LongestSearchTicks, took, longest) != longest) { }
        return scores;
    }

    private int _outOfTimeInARow;

    /// <summary>Decisions out of time in a row after which the bot stops searching.</summary>
    private const int GiveUpAfter = 3;

    /// <summary>Decisions searched so far (each one gets its own deals).</summary>
    private int _searches;

    /// <summary>The most options tried for one decision.</summary>
    private const int MaxOptions = 6;

    private async Task<PlayerAction> SearchActionAsync(GameView view, IReadOnlyList<PlayerAction> legal, PlayerAction planned)
    {
        bool ownMain = view.ActivePlayer == _me && view.Step.IsMain() && view.Stack.Count == 0;
        // Spells and abilities that do something (mana abilities only make mana for them).
        bool Acts(PlayerAction a) => a is CastSpell
            || a is ActivateAbility act && !(Rules(view, act.Source)?.Abilities.ElementAtOrDefault(act.Index) is Arcanum.Engine.Abilities.ActivatedAbility ability
                                              && Arcanum.Engine.Game.IsManaAbilityOf(ability));
        // Elsewhere, instants and abilities at the moments they matter most: answering what is on the stack, once blocks
        // are declared, and at the end of an opponent's turn. Not at every stop (each search takes seconds on a small device).
        if (!ownMain && !(legal.Any(Acts)
                          && (view.Stack.Count > 0 || view.Step == Step.DeclareBlockers || (view.ActivePlayer != _me && view.Step == Step.End))))
            return planned;
        // The planned action, passing, then each other land and spell, and abilities that aren't mana abilities.
        var options = new List<PlayerAction> { planned };
        void Add(PlayerAction a) { if (options.Count < MaxOptions && !options.Contains(a)) options.Add(a); }
        Add(PassPriority.Instance);
        foreach (var land in legal.OfType<PlayLand>().GroupBy(l => Rules(view, l.Card)?.Name).Select(g => g.First())) Add(land);
        foreach (var cast in legal.OfType<CastSpell>().GroupBy(c => (Rules(view, c.Card)?.Name, c.Adventure, c.Back)).Select(g => g.First())) Add(cast);
        foreach (var other in legal.Where(a => a is ActivateAbility ? Acts(a) : a is UnlockDoor or TurnFaceUp)) Add(other);
        if (options.Count < 2) return planned;
        var scores = await TimedScoreAsync(options, (option, rollout) => FirstAnswer.Action(option, rollout), ++_searches + view.TurnNumber * 100);
        return Pick(options, scores);
    }

    private async Task<IReadOnlyList<AttackDeclaration>> SearchAttacksAsync(GameView view, IReadOnlyList<CardId> possibleAttackers,
        IReadOnlyList<PlayerId> defenders, IReadOnlyList<AttackDeclaration> planned)
    {
        if (possibleAttackers.Count == 0 || view.AttackerTaxes.Count > 0 || view.AttackTaxes.Count > 0) return planned;
        var rules = view.AttackRequest;
        var options = new List<IReadOnlyList<AttackDeclaration>> { planned };
        void Add(List<AttackDeclaration> attack)
        {
            if (rules is not null && !rules.IsLegal(attack, out _)) attack = rules.Complete(attack).ToList();
            if (!options.Any(o => o.Count == attack.Count && o.All(attack.Contains))) options.Add(attack);
        }
        Add(new List<AttackDeclaration>());
        // Everything at the player the plan attacks (or the first one), and each planned attacker held back in turn.
        var target = planned.FirstOrDefault()?.Defender ?? defenders[0];
        Add(possibleAttackers.Where(a => (view.FindCard(a)?.Power ?? 0) > 0 && defenders.Any(d => view.MayAttack(a, d)))
            .Select(a => new AttackDeclaration(a, view.MayAttack(a, target) ? target : defenders.FirstOrDefault(d => view.MayAttack(a, d)))).ToList());
        foreach (var held in planned.Take(MaxOptions - options.Count)) Add(planned.Where(d => d != held).ToList());
        if (options.Count < 2) return planned;
        var scores = await TimedScoreAsync(options, (option, rollout) => FirstAnswer.Attacks(option, rollout), ++_searches + view.TurnNumber * 100);
        return Pick(options, scores);
    }

    private async Task<IReadOnlyList<BlockDeclaration>> SearchBlocksAsync(GameView view, BlockRequest request, IReadOnlyList<BlockDeclaration> planned)
    {
        // The planned blocks, no blocks, and the plan with each of its blockers held back in turn (each made legal).
        var options = new List<IReadOnlyList<BlockDeclaration>> { planned };
        void Add(List<BlockDeclaration> blocks)
        {
            if (!request.IsLegal(blocks, out _)) blocks = request.Complete(blocks).ToList();
            if (options.Count < MaxOptions && !options.Any(o => o.Count == blocks.Count && o.All(blocks.Contains))) options.Add(blocks);
        }
        Add(new List<BlockDeclaration>());
        foreach (var held in planned.Select(b => b.Blocker).Distinct().ToList()) Add(planned.Where(b => b.Blocker != held).ToList());
        if (options.Count < 2) return planned;
        var scores = await TimedScoreAsync(options, (option, rollout) => FirstAnswer.Blocks(option, rollout), ++_searches + view.TurnNumber * 100);
        return Pick(options, scores);
    }

    /// <summary>The best scoring option; the first (the rules of thumb's) unless another beats it by the margin.</summary>
    private T Pick<T>(IReadOnlyList<T> options, double[]? scores)
    {
        if (scores is null) return options[0]; // out of time before one round: the rules of thumb's choice
        int best = 0;
        for (int i = 1; i < options.Count; i++)
            if (scores[i] > scores[best] + (best == 0 ? SearchMargin : 0)) best = i;
        return options[best];
    }
}
