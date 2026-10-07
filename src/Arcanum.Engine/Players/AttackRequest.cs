// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>What an attack requirement asks of a creature (rule 508.1d).</summary>
public enum AttackRequirementKind
{
    /// <summary>"Attacks each combat if able": any attack obeys it (a planeswalker too).</summary>
    Attacks,
    /// <summary>"Attacks [that player] if able": only attacking that player obeys it.</summary>
    AttacksPlayer,
    /// <summary>A goaded creature "attacks a player other than [the goading player] if able" (rule 701.15b).</summary>
    AttacksPlayerOtherThan,
}

/// <summary>One requirement on one creature; <paramref name="Player"/> for the kinds about a player.</summary>
public sealed record AttackRequirement(CardId Attacker, AttackRequirementKind Kind, PlayerId? Player = null);

/// <summary>A planeswalker that can be attacked, and the defending player who controls it.</summary>
public sealed record AttackablePlaneswalker(CardId Planeswalker, PlayerId Controller);

/// <summary>A creature that can't attack a given player; with <paramref name="Planeswalkers"/>, not their planeswalkers either.</summary>
public sealed record AttackForbidden(CardId Attacker, PlayerId Defender, bool Planeswalkers = false);

/// <summary>
/// Everything needed to declare attackers legally (rule 508.1): who may attack whom, the restrictions ("can't attack
/// [player]", "can't attack unless defending player controls an Island", "can't attack alone") and the requirements, which
/// must be obeyed as far as possible without breaking a restriction (508.1d). A requirement that would need a cost to be paid
/// (an attack tax, 508.1h) doesn't have to be obeyed.
/// </summary>
public sealed record AttackRequest(IReadOnlyList<CardId> Attackers, IReadOnlyList<PlayerId> Defenders)
{
    public IReadOnlyList<AttackablePlaneswalker> Planeswalkers { get; init; } = Array.Empty<AttackablePlaneswalker>();

    public IReadOnlyList<AttackForbidden> Forbidden { get; init; } = Array.Empty<AttackForbidden>();

    /// <summary>Creatures that "can't attack alone" (rule 506.5).</summary>
    public IReadOnlyList<CardId> CantAttackAlone { get; init; } = Array.Empty<CardId>();

    public IReadOnlyList<AttackRequirement> Requirements { get; init; } = Array.Empty<AttackRequirement>();

    /// <summary>Defending players that cost something to attack (their planeswalkers too).</summary>
    public IReadOnlyList<PlayerId> Taxed { get; init; } = Array.Empty<PlayerId>();

    private int? _maxRequirements;

    /// <summary>The same declaration with every defender that costs something to attack ruled out.</summary>
    public AttackRequest WithoutTaxedDefenders() => new(Attackers, Defenders)
    {
        Planeswalkers = Planeswalkers,
        Forbidden = Forbidden.Concat(Attackers.SelectMany(a => Taxed.Select(d => new AttackForbidden(a, d, true)))).ToList(),
        CantAttackAlone = CantAttackAlone,
        Requirements = Requirements,
    };

    /// <summary>The most requirements a legal declaration obeys without paying any cost.</summary>
    public int MaxRequirements => _maxRequirements ??= Search(Array.Empty<AttackDeclaration>()).Requirements;

    /// <summary>Whether the creature may attack that player (or, with a planeswalker, that planeswalker).</summary>
    public bool MayAttack(CardId attacker, PlayerId defender, CardId? planeswalker = null) =>
        Attackers.Contains(attacker) && Defenders.Contains(defender)
        && (planeswalker is not { } pw ? !Forbidden.Any(f => f.Attacker == attacker && f.Defender == defender)
            : Planeswalkers.Any(p => p.Planeswalker == pw && p.Controller == defender)
              && !Forbidden.Any(f => f.Attacker == attacker && f.Defender == defender && f.Planeswalkers));

    private static bool Obeys(AttackRequirement r, AttackDeclaration? d) => d is not null && r.Kind switch
    {
        AttackRequirementKind.Attacks => true,
        AttackRequirementKind.AttacksPlayer => d.Planeswalker is null && d.Defender == r.Player,
        _ => d.Planeswalker is null && d.Defender != r.Player,
    };

    /// <summary>Requirements a declaration obeys (paying costs or not).</summary>
    public int ObeyedRequirements(IReadOnlyList<AttackDeclaration> declared) =>
        Requirements.Count(r => Obeys(r, declared.FirstOrDefault(d => d.Attacker == r.Attacker)));

    public bool IsLegal(IReadOnlyList<AttackDeclaration> declared, out string? reason)
    {
        reason = null;
        if (declared.Select(d => d.Attacker).Distinct().Count() != declared.Count) { reason = "A creature can attack only once."; return false; }
        foreach (var d in declared)
            if (!MayAttack(d.Attacker, d.Defender, d.Planeswalker))
            {
                reason = "That creature can't attack that player.";
                return false;
            }
        if (declared.Count == 1 && CantAttackAlone.Contains(declared[0].Attacker))
        {
            reason = "That creature can't attack alone.";
            return false;
        }
        if (Requirements.Count > 0 && ObeyedRequirements(declared) < MaxRequirements)
        {
            reason = "Some creatures must attack as required.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// A legal declaration obeying as many requirements as possible, keeping as much of <paramref name="wanted"/> as it can:
    /// required attackers are added (at the player the first wanted attack goes to when they may), and attackers that can't
    /// attack as wanted are left home rather than sent elsewhere.
    /// </summary>
    public IReadOnlyList<AttackDeclaration> Complete(IReadOnlyList<AttackDeclaration> wanted) => Search(wanted).Declared;

    private (int Requirements, IReadOnlyList<AttackDeclaration> Declared) Search(IReadOnlyList<AttackDeclaration> wanted)
    {
        var attackers = Attackers.Distinct().ToList();
        var preferred = wanted.FirstOrDefault()?.Defender is { } first && Defenders.Contains(first) ? first : Defenders.FirstOrDefault();
        var options = new List<(AttackDeclaration? Choice, int Free, int Kept)>[attackers.Count];
        for (int i = 0; i < attackers.Count; i++)
        {
            var c = attackers[i];
            var want = wanted.FirstOrDefault(w => w.Attacker == c);
            var choices = new List<AttackDeclaration?> { null };
            if (want is not null) choices.Add(want);
            foreach (var d in Defenders.OrderBy(d => d == preferred ? 0 : 1))
            {
                choices.Add(new AttackDeclaration(c, d));
                foreach (var pw in Planeswalkers.Where(p => p.Controller == d)) choices.Add(new AttackDeclaration(c, d, pw.Planeswalker));
            }
            var mine = Requirements.Where(r => r.Attacker == c).ToList();
            options[i] = choices.Distinct()
                .Where(o => o is null || MayAttack(c, o.Defender, o.Planeswalker))
                .Select(o => (o,
                    // Obeying a requirement by attacking a player who costs something to attack is never required.
                    o is not null && Taxed.Contains(o.Defender) ? 0 : mine.Count(r => Obeys(r, o)),
                    o == want ? (want is null ? 0 : 2) : o is null ? 0 : -3))
                .ToList();
        }

        // State: how many creatures attack (0, 1, 2+) and whether one that can't attack alone does (rule 506.5).
        var memo = new Dictionary<(int, int, bool), ((int Req, int Kept) Score, int Choice)>();
        (int Req, int Kept) Best(int index, int count, bool alone)
        {
            if (index == attackers.Count) return alone && count < 2 ? (int.MinValue, int.MinValue) : (0, 0);
            if (memo.TryGetValue((index, count, alone), out var known)) return known.Score;
            (int Req, int Kept) best = (int.MinValue, int.MinValue);
            int choice = -1;
            for (int o = 0; o < options[index].Count; o++)
            {
                var (option, free, kept) = options[index][o];
                bool attacks = option is not null;
                var rest = Best(index + 1, Math.Min(2, count + (attacks ? 1 : 0)), alone || (attacks && CantAttackAlone.Contains(attackers[index])));
                if (rest.Req == int.MinValue) continue;
                var score = (rest.Req + free, rest.Kept + kept);
                if (choice < 0 || score.Item1 > best.Req || (score.Item1 == best.Req && score.Item2 > best.Kept)) { best = score; choice = o; }
            }
            memo[(index, count, alone)] = (best, choice);
            return best;
        }

        var top = Best(0, 0, false);
        var result = new List<AttackDeclaration>();
        int n = 0;
        bool lone = false;
        for (int i = 0; i < attackers.Count; i++)
        {
            var option = options[i][memo[(i, n, lone)].Choice].Choice;
            if (option is null) continue;
            result.Add(option);
            n = Math.Min(2, n + 1);
            lone |= CantAttackAlone.Contains(attackers[i]);
        }
        return (top.Req, result);
    }
}
