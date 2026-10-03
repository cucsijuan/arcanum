// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Bots.Evaluation;

namespace Arcanum.Bots;

/// <summary>
/// A computer opponent with simple, readable heuristics: develop the board on curve, use removal on the biggest
/// threat, attack when it's safe or lethal, block to survive and trade well, and use tricks in combat.
/// It decides only from its own <see cref="GameView"/> plus the printed rules of cards it can see.
/// </summary>
public sealed class BotController : IPlayerController
{
    private readonly PlayerId _me;

    /// <summary>Turn number when each opponent last attacked us: recent attackers are treated as bigger threats.</summary>
    private readonly Dictionary<PlayerId, int> _lastAttackedUs = new();
    private Func<CardId, CardDefinition?> _definitions = _ => null;

    /// <summary>Keep any opening hand (prepared scenarios and the sandbox, where hands are set up on purpose).</summary>
    public bool AlwaysKeep { get; set; }

    /// <summary>Optional pause before visible actions so a person can follow what the bot does.</summary>
    public Func<Task>? Pace { get; set; }

    public BotController(PlayerId me) => _me = me;

    /// <summary>
    /// How to read the printed rules of a card by id. The bot only asks about cards visible in its view
    /// (its hand, the battlefield, the stack), so it never learns hidden information.
    /// </summary>
    public void UseCardRules(Func<CardId, CardDefinition?> definitions) => _definitions = definitions;

    private CardDefinition? Rules(GameView view, CardId id) =>
        view.FindCard(id) is { IsHidden: false } ? _definitions(id) : null;

    private async Task PaceAsync()
    {
        if (Pace is not null) await Pace();
    }

    // ---------------------------------------------------------------- opening hand

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken)
    {
        var hand = view.Self.Hand;
        int lands = hand.Count(c => (c.Types & CardType.Land) != 0);
        bool keep = AlwaysKeep || mulligansTaken >= 2 || (lands >= 2 && lands <= hand.Count - 2);
        return Task.FromResult(keep);
    }

    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
        Task.FromResult<IReadOnlyList<CardId>>(LeastUseful(view, count));

    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) =>
        Task.FromResult<IReadOnlyList<CardId>>(LeastUseful(view, count));

    /// <summary>Modes picked for each source's latest modal spell or ability, to choose its targets accordingly.</summary>
    private readonly Dictionary<CardId, IReadOnlyList<int>> _lastModes = new();

    public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request)
    {
        var rules = Rules(view, request.Source) ?? _definitions(request.Source);
        var ability = rules?.Spell?.Modes is not null ? rules.Spell : rules?.Abilities.FirstOrDefault(a => a.Modes is not null);
        var ranked = request.Possible
            .Where(i => !request.Modes[i].Contains("lose the game", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => ability?.Modes is { } modes ? SpellScore(view, modes[i].AsAbility()) : 0)
            .ToList();
        if (ranked.Count < request.Min) ranked = request.Possible.ToList();
        var chosen = ranked.Take(Math.Max(request.Min, Math.Min(request.Max, ranked.Count))).OrderBy(i => i).ToList();
        _lastModes[request.Source] = chosen;
        return Task.FromResult<IReadOnlyList<int>?>(chosen);
    }

    /// <summary>The color or creature type most common among the bot's own visible cards.</summary>
    public Task<int> ChooseOptionAsync(GameView view, OptionRequest request)
    {
        var mine = view.Battlefield.Where(c => c.Controller == _me).Concat(view.Self.Hand).Where(c => !c.IsHidden).ToList();
        int best = 0, bestScore = -1;
        for (int i = 0; i < request.Options.Count; i++)
        {
            var option = request.Options[i];
            int score = request.Kind switch
            {
                OptionKind.Color => mine.Count(c => c.ManaCost?.Contains("{" + ColorLetter(option) + "}") == true || c.Colors.Contains(ColorLetter(option))),
                OptionKind.CreatureType => mine.Count(c => _definitions(c.Id)?.Subtypes.Contains(option) == true),
                _ => 0,
            };
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return Task.FromResult(best);
    }

    private static string ColorLetter(string color) => color switch { "Blue" => "U", _ => color[..1] };

    /// <summary>Use everything available for X.</summary>
    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => Task.FromResult(request.Max);

    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request)
    {
        int lands = view.Battlefield.Count(c => c.Controller == _me && (c.Types & CardType.Land) != 0)
                    + view.Self.Hand.Count(c => (c.Types & CardType.Land) != 0);
        // Lands are dead draws once there are enough; spells far above the mana available are slow.
        bool Unwanted(CardView c) => (c.Types & CardType.Land) != 0 ? lands >= 5 : ManaValue(c.ManaCost) > lands + 2;
        double Value(CardView c) => (c.Types & CardType.Creature) != 0 ? CreatureValue(c) : 1 + ManaValue(c.ManaCost);

        IEnumerable<CardView> picks = request.Purpose switch
        {
            CardChoicePurpose.ScryToBottom or CardChoicePurpose.SurveilToGraveyard => request.Options.Where(Unwanted),
            CardChoicePurpose.ToHand or CardChoicePurpose.ToBattlefield => request.Options.OrderByDescending(Value).Take(request.Max),
            _ => request.Options.OrderBy(Value).Take(request.Min),
        };
        var chosen = picks.Take(request.Max).Select(c => c.Id).ToList();
        foreach (var extra in request.Options.OrderBy(Value).Where(c => !chosen.Contains(c.Id)))
        {
            if (chosen.Count >= request.Min) break;
            chosen.Add(extra.Id);
        }
        return Task.FromResult<IReadOnlyList<CardId>>(chosen);
    }

    /// <summary>Extra lands when flooded, otherwise the most expensive spells.</summary>
    private static List<CardId> LeastUseful(GameView view, int count)
    {
        var hand = view.Self.Hand;
        int lands = hand.Count(c => (c.Types & CardType.Land) != 0);
        bool flooded = lands > hand.Count / 2;
        return hand
            .OrderByDescending(c => (c.Types & CardType.Land) != 0 ? (flooded ? 100 : -1) : ManaValue(c.ManaCost))
            .Take(count)
            .Select(c => c.Id)
            .ToList();
    }

    // ---------------------------------------------------------------- priority

    public async Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        var action = Decide(view, legal);
        if (action is not PassPriority) await PaceAsync();
        return action;
    }

    private void RememberAttacks(GameView view)
    {
        foreach (var attack in view.Attacks.Where(a => a.Defender == _me))
            if (view.FindCard(attack.Attacker) is { } attacker) _lastAttackedUs[attacker.Controller] = view.TurnNumber;
    }

    /// <summary>
    /// How dangerous an opponent is to us: their board, cards in hand, whether they attacked us recently and the
    /// commander damage they've dealt us. Low life slightly lowers it (they're closer to being out anyway).
    /// </summary>
    private double Threat(GameView view, PlayerId opponent)
    {
        var p = view.Players[opponent.Value];
        double board = view.Battlefield.Where(c => c.Controller == opponent && (c.Types & CardType.Creature) != 0).Sum(CreatureValue);
        double threat = board + 0.7 * p.Hand.Count;
        if (_lastAttackedUs.TryGetValue(opponent, out int turn) && view.TurnNumber - turn <= 4) threat += 6;
        threat += view.Self.CommanderDamage.Where(kv => view.FindCard(kv.Key)?.Controller == opponent).Sum(kv => kv.Value) * 0.5;
        threat += Math.Min(p.Life, 40) * 0.05;
        return threat;
    }

    /// <summary>Total power an opponent could swing at us next turn with their creatures.</summary>
    private static int PotentialAttack(GameView view, PlayerId opponent) =>
        view.Battlefield.Where(c => c.Controller == opponent && (c.Types & CardType.Creature) != 0 && !Has(c, "Defender"))
            .Sum(c => c.Power ?? 0);

    private PlayerAction Decide(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        RememberAttacks(view);
        bool myTurn = view.ActivePlayer == _me;
        bool emptyStack = view.Stack.Count == 0;

        // Respond to an opponent's spell: counter it if it matters.
        if (!emptyStack && view.Stack[^1].Controller != _me)
            return CounterIfWorthIt(view, legal) ?? PassPriority.Instance;

        if (myTurn && view.Step.IsMain() && emptyStack)
        {
            if (legal.OfType<PlayLand>().FirstOrDefault() is { } land) return land;
            if (BestSpell(view, legal, mainPhase: true) is { } spell) return spell;
            if (LoyaltyAbility(view, legal) is { } loyalty) return loyalty;
            if (view.Step == Step.PrecombatMain && Equip(view, legal) is { } equip) return equip;
            if (view.Step == Step.PostcombatMain && UsefulActivation(view, legal) is { } ping) return ping;
            return PassPriority.Instance;
        }

        // Combat tricks once blocks are known.
        if (view.Step == Step.DeclareBlockers && CombatTrick(view, legal) is { } trick) return trick;

        // End of the opponent's turn: use instant-speed removal and pingers we held back.
        if (!myTurn && view.Step == Step.End && emptyStack)
            return BestSpell(view, legal, mainPhase: false) ?? UsefulActivation(view, legal) ?? (PlayerAction)PassPriority.Instance;

        return PassPriority.Instance;
    }

    private PlayerAction? CounterIfWorthIt(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        var top = view.Stack[^1];
        bool threat = top.AbilityText is null && (ManaValue(top.Card.ManaCost) >= 3 || top.Targets.Any(t => t.Card is { } c && view.FindCard(c)?.Controller == _me));
        if (!threat) return null;
        foreach (var cast in legal.OfType<CastSpell>())
        {
            var spell = Rules(view, cast.Card)?.Spell;
            if (spell is not null && spell.Effects.Any(e => e is CounterSpell)) return cast;
        }
        return null;
    }

    /// <summary>The spell worth casting most right now, or null. Creatures and permanents in main phases; removal when it has a good target.</summary>
    private CastSpell? BestSpell(GameView view, IReadOnlyList<PlayerAction> legal, bool mainPhase)
    {
        CastSpell? best = null;
        double bestScore = 0.5;
        foreach (var cast in legal.OfType<CastSpell>())
        {
            var rules = Rules(view, cast.Card);
            var card = view.FindCard(cast.Card);
            if (rules is null || card is null) continue;
            double score = 0;
            if (rules.Spell is { } spell)
            {
                score = SpellScore(view, spell);
                // Tricks (pump our own creature) are for combat, not the main phase.
                if (spell.Effects.All(e => !IsHarmful(e)) && spell.Targets.Count > 0) score = 0;
            }
            else if (mainPhase && rules.Types.IsPermanent())
            {
                score = 1 + ManaValue(card.ManaCost) + (rules.Is(CardType.Creature) ? 1 : 0);
                if (rules.EnchantTarget is not null) score = AuraScore(view, rules, ManaValue(card.ManaCost));
            }
            if (score > bestScore) { bestScore = score; best = cast; }
        }
        return best;
    }

    /// <summary>Net value of an effect hitting every permanent matching the filter: theirs minus ours.</summary>
    private double EachValue(GameView view, ObjectFilter filter)
    {
        bool Hit(CardView c) => (filter.Types == 0 || (c.Types & filter.Types) != 0)
                                && (filter.Controller == ControllerFilter.Any || (filter.Controller == ControllerFilter.Opponent) == (c.Controller != _me));
        var hit = view.Battlefield.Where(Hit).ToList();
        return hit.Where(c => c.Controller != _me).Sum(CreatureValue) - hit.Where(c => c.Controller == _me).Sum(CreatureValue) - 1;
    }

    /// <summary>Value of casting a non-permanent spell now: removal against the best target, card draw, face damage.</summary>
    private double SpellScore(GameView view, AbilityDefinition spell)
    {
        if (spell.Modes is { } modes) return modes.Max(m => SpellScore(view, m.AsAbility()));
        if (spell.Targets.Count == 0)
        {
            double value = 0;
            foreach (var effect in spell.Effects)
            {
                value += effect switch
                {
                    DrawCards d when d.Who.Kind == SubjectKind.You => 1.5 * d.Count.Estimate,
                    CreateTokens t => 2 * t.Count.Estimate,
                    DealDamage { To.Kind: SubjectKind.EachOpponent } d => d.Amount.Estimate,
                    GainLife g when g.Who.Kind == SubjectKind.You => 0.3 * g.Amount.Estimate,
                    LoseLife { Who.Kind: SubjectKind.You } => -1,
                    SearchLibrary => 1.5,
                    Destroy { What.Kind: SubjectKind.Each } d => EachValue(view, d.What.Filter!),
                    DealDamage { To.Kind: SubjectKind.Each } d => EachValue(view, d.To.Filter! with { MaxPower = null }),
                    PumpUntilEndOfTurn { What.Kind: SubjectKind.Each } => 0.5,
                    _ => 0.2,
                };
            }
            return value;
        }
        if (!TargetIsHarmed(spell, 0)) return spell.Effects.OfType<DrawCards>().Sum(d => 1.5 * d.Count.Estimate);
        var target = BestHarmTarget(view, spell, 0, LegalHarmTargets(view, spell.Targets[0]));
        if (target is null) return 0;
        if (target.Value.Player is { } facePlayer)
        {
            int damage = DamageTo(spell, 0);
            return damage >= view.Players[facePlayer.Value].Life ? 100 : 0; // burn to the face only to finish a player
        }
        return view.FindCard(target.Value.Card!.Value) is { } victim ? 1 + CreatureValue(victim) : 0;
    }

    private double AuraScore(GameView view, CardDefinition aura, int manaValue)
    {
        var st = aura.Abilities.OfType<StaticAbility>().FirstOrDefault();
        bool beneficial = st is null || st.Power + st.Toughness >= 0;
        var candidates = view.Battlefield.Where(c => (c.Types & CardType.Creature) != 0 && (beneficial ? c.Controller == _me : c.Controller != _me));
        return candidates.Any() ? 1 + manaValue : 0;
    }

    private PlayerAction? Equip(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        foreach (var activate in legal.OfType<ActivateAbility>())
        {
            var source = view.FindCard(activate.Source);
            var ability = Rules(view, activate.Source)?.Abilities.ElementAtOrDefault(activate.Index);
            if (source is null || ability is null || !ability.Effects.Any(e => e is AttachSelf)) continue;
            if (source.AttachedTo is null) return activate; // targets: our best creature (see ChooseTargets)
        }
        return null;
    }

    /// <summary>Tap/sacrifice abilities that harm something worthwhile, or gain value with no target.</summary>
    /// <summary>
    /// A planeswalker ability: the ultimate when affordable, otherwise a minus ability worth its loyalty when it
    /// leaves the planeswalker alive and has a good effect, otherwise the plus ability.
    /// </summary>
    private PlayerAction? LoyaltyAbility(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        var options = legal.OfType<ActivateAbility>()
            .Select(a => (Action: a, Ability: Rules(view, a.Source)?.Abilities.ElementAtOrDefault(a.Index) as ActivatedAbility, Card: view.FindCard(a.Source)))
            .Where(o => o.Ability?.Cost.Loyalty is not null && o.Card is not null)
            .ToList();
        if (options.Count == 0) return null;
        foreach (var group in options.GroupBy(o => o.Action.Source))
        {
            int loyalty = group.First().Card!.Loyalty;
            var ultimate = group.Where(o => o.Ability!.Cost.Loyalty! < 0).OrderBy(o => o.Ability!.Cost.Loyalty).FirstOrDefault();
            if (ultimate.Ability is not null && -ultimate.Ability.Cost.Loyalty! >= 6) return ultimate.Action;
            var minus = group.Where(o => o.Ability!.Cost.Loyalty! < 0 && loyalty + o.Ability.Cost.Loyalty! > 0)
                .OrderByDescending(o => SpellScore(view, o.Ability!)).FirstOrDefault();
            if (minus.Ability is not null && SpellScore(view, minus.Ability) >= 4) return minus.Action;
            var plus = group.Where(o => o.Ability!.Cost.Loyalty! >= 0).OrderByDescending(o => o.Ability!.Cost.Loyalty).FirstOrDefault();
            if (plus.Ability is not null) return plus.Action;
        }
        return null;
    }

    private PlayerAction? UsefulActivation(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        foreach (var activate in legal.OfType<ActivateAbility>())
        {
            var ability = Rules(view, activate.Source)?.Abilities.ElementAtOrDefault(activate.Index) as ActivatedAbility;
            if (ability is null || ability.Effects.Any(e => e is AttachSelf) || ability.Cost.Loyalty is not null) continue;
            if (ability.Cost.SacrificeSelf) continue; // keep sacrifice outlets for emergencies
            if (ability.Targets.Count == 0)
            {
                if (ability.Effects.Any(e => e is DrawCards or CreateTokens)) return activate;
                continue;
            }
            if (!TargetIsHarmed(ability, 0)) continue;
            var target = BestHarmTarget(view, ability, 0, LegalHarmTargets(view, ability.Targets[0]));
            if (target is { Card: { } c } && view.FindCard(c) is { } victim && DamageTo(ability, 0) >= RemainingToughness(victim)) return activate;
            if (target is { Player: not null } && DamageTo(ability, 0) > 0) return activate; // chip damage at the right moment
        }
        return null;
    }

    /// <summary>A pump spell that wins a combat for one of our creatures.</summary>
    private PlayerAction? CombatTrick(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        foreach (var cast in legal.OfType<CastSpell>())
        {
            var spell = Rules(view, cast.Card)?.Spell;
            var pump = spell?.Effects.OfType<PumpUntilEndOfTurn>().FirstOrDefault(p => p.Power.Estimate + p.Toughness.Estimate > 0);
            if (spell is null || pump is null || spell.Targets.Count != 1) continue;
            if (FightToWin(view, pump) is not null) return cast;
        }
        return null;
    }

    /// <summary>Our creature in a combat that the pump turns from losing into surviving/killing.</summary>
    private CardView? FightToWin(GameView view, PumpUntilEndOfTurn pump)
    {
        foreach (var attack in view.Attacks.Where(a => a.IsBlocked))
        {
            var attacker = view.FindCard(attack.Attacker);
            var blockers = attack.Blockers.Select(view.FindCard).OfType<CardView>().ToList();
            if (attacker is null || blockers.Count == 0) continue;
            if (attacker.Controller == _me)
            {
                int incoming = blockers.Sum(b => b.Power ?? 0);
                bool diesNow = incoming >= RemainingToughness(attacker);
                bool survivesPumped = incoming < RemainingToughness(attacker) + pump.Toughness.Estimate;
                if (diesNow && survivesPumped) return attacker;
            }
            else
            {
                foreach (var blocker in blockers.Where(b => b.Controller == _me))
                {
                    bool diesNow = (attacker.Power ?? 0) >= RemainingToughness(blocker);
                    bool survivesPumped = (attacker.Power ?? 0) < RemainingToughness(blocker) + pump.Toughness.Estimate;
                    if (diesNow && survivesPumped) return blocker;
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- targets

    public Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request)
    {
        var ability = FindAbility(view, request);
        var chosen = new List<Target>();
        for (int i = 0; i < request.Specs.Count; i++)
        {
            var legal = request.Legal[i].Where(t => !chosen.Contains(t) && !t.IsNone).ToList();
            if (legal.Count == 0) legal = request.Legal[i].ToList();
            Target pick;
            if (ability is not null && TargetIsHarmed(ability, i))
            {
                pick = BestHarmTarget(view, ability, i, legal) ?? legal[0];
            }
            else
            {
                var pump = ability?.Effects.OfType<PumpUntilEndOfTurn>().FirstOrDefault(p => RefersToTarget(p, i));
                pick = (pump is not null ? FightToWin(view, pump) is { } fighter ? Target.Of(fighter.Id) : (Target?)null : null)
                       ?? BestHelpTarget(view, legal) ?? legal[0];
            }
            chosen.Add(pick);
        }
        return Task.FromResult<IReadOnlyList<Target>?>(chosen);
    }

    private AbilityDefinition? FindAbility(GameView view, TargetRequest request)
    {
        var rules = Rules(view, request.Source) ?? _definitions(request.Source); // trigger sources may have left play
        if (rules is null) return null;
        if (rules.Spell is { } spell && request.Text == rules.Name)
            return spell.Modes is not null && _lastModes.TryGetValue(request.Source, out var modes) ? spell.WithModes(modes) : spell;
        if (_lastModes.TryGetValue(request.Source, out var chosenModes)
            && rules.Abilities.FirstOrDefault(a => a.Modes is not null && a.Targets.Count == 0) is { } modal)
            return modal.WithModes(chosenModes);
        if (rules.EnchantTarget is { } enchant && request.Text == rules.Name)
            return rules.Abilities.OfType<StaticAbility>().FirstOrDefault() is { } st ? st with { Targets = new[] { enchant } } : null;
        return rules.Abilities.FirstOrDefault(a => a.Text == request.Text && a.Targets.Count == request.Specs.Count)
               ?? rules.Abilities.FirstOrDefault(a => a.Targets.Count == request.Specs.Count);
    }

    private List<Target> LegalHarmTargets(GameView view, TargetSpec spec)
    {
        var targets = new List<Target>();
        if (spec.Kind is TargetKind.Any or TargetKind.Player or TargetKind.PlayerOrPlaneswalker)
            targets.AddRange(view.Players.Where(p => p.Id != _me && !p.HasLost).Select(p => Target.Of(p.Id)));
        if (spec.Kind != TargetKind.Player)
            targets.AddRange(view.Battlefield.Where(c => c.Controller != _me && !Has(c, "Hexproof") && !Has(c, "Shroud") && Matches(c, spec.Kind)).Select(c => Target.Of(c.Id)));
        return targets;
    }

    private static bool Matches(CardView c, TargetKind kind) => kind switch
    {
        TargetKind.Any or TargetKind.Creature => (c.Types & CardType.Creature) != 0,
        TargetKind.CreatureOrPlaneswalker => (c.Types & (CardType.Creature | CardType.Planeswalker)) != 0,
        TargetKind.Planeswalker or TargetKind.PlayerOrPlaneswalker => (c.Types & CardType.Planeswalker) != 0,
        TargetKind.Artifact => (c.Types & CardType.Artifact) != 0,
        TargetKind.Enchantment => (c.Types & CardType.Enchantment) != 0,
        TargetKind.Land => (c.Types & CardType.Land) != 0,
        TargetKind.Permanent => true,
        _ => false,
    };

    /// <summary>
    /// Best opposing target for a harmful effect: a valuable creature it actually kills, else the most valuable
    /// creature, else an opponent. Damage that can't kill anything goes to the opponent when allowed.
    /// </summary>
    private Target? BestHarmTarget(GameView view, AbilityDefinition ability, int index, IEnumerable<Target> legal)
    {
        var options = legal.ToList();
        int damage = DamageTo(ability, index);
        var creatures = options
            .Where(t => t.Card is { } c && view.FindCard(c) is { } card && card.Controller != _me)
            .Select(t => (Target: t, Card: view.FindCard(t.Card!.Value)!))
            .ToList();
        // In multiplayer, a creature matters more when its controller is the biggest threat to us.
        double Weight(CardView c) => CreatureValue(c) * (view.Players.Count(p => !p.HasLost) > 2 ? 1 + Threat(view, c.Controller) / 30 : 1);
        if (damage > 0)
        {
            var killable = creatures.Where(x => damage >= RemainingToughness(x.Card) && !Has(x.Card, "Indestructible"))
                .OrderByDescending(x => Weight(x.Card)).FirstOrDefault();
            if (killable.Card is not null && CreatureValue(killable.Card) >= 2) return killable.Target;
            // To the face: a player it finishes off first, otherwise the biggest threat.
            var face = options.Where(t => t.Player is { } p && p != _me)
                .OrderBy(t => view.Players[t.Player!.Value.Value].Life <= damage ? 0 : 1)
                .ThenByDescending(t => Threat(view, t.Player!.Value))
                .FirstOrDefault();
            if (face.Player is not null) return face;
            return killable.Card is not null ? killable.Target : null;
        }
        var best = creatures.OrderByDescending(x => Weight(x.Card)).FirstOrDefault();
        if (best.Card is not null) return best.Target;
        var opponent = options.FirstOrDefault(t => t.Player is { } pl && pl != _me);
        return opponent.Player is not null ? opponent : null;
    }

    /// <summary>Best friendly target for a helpful effect: our most valuable creature, else ourselves.</summary>
    private Target? BestHelpTarget(GameView view, IEnumerable<Target> legal)
    {
        var options = legal.ToList();
        var mine = options
            .Where(t => t.Card is { } c && view.FindCard(c) is { } card && card.Controller == _me && (card.Types & CardType.Creature) != 0)
            .OrderByDescending(t => CreatureValue(view.FindCard(t.Card!.Value)!))
            .FirstOrDefault();
        if (mine.Card is not null) return mine;
        var self = options.FirstOrDefault(t => t.Player == _me);
        return self.Player is not null ? self : null;
    }

    // ---------------------------------------------------------------- combat

    public async Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders)
    {
        var attackers = possibleAttackers.Select(view.FindCard).OfType<CardView>().ToList();
        List<CardView> BlockersOf(PlayerId p) =>
            view.Battlefield.Where(c => c.Controller == p && (c.Types & CardType.Creature) != 0 && !c.Tapped && !Has(c, "Can't block")).ToList();

        // Alpha strike at an opponent our whole attack can finish (power beyond what their blockers can stop).
        foreach (var defender in defenders.OrderBy(d => view.Players[d.Value].Life))
        {
            var blockers = BlockersOf(defender);
            int totalPower = attackers.Sum(a => a.Power ?? 0);
            int stopped = attackers.OrderByDescending(a => a.Power ?? 0).Take(blockers.Count).Sum(a => a.Power ?? 0);
            if (totalPower - stopped >= view.Players[defender.Value].Life)
            {
                await PaceAsync();
                return attackers.Where(a => (a.Power ?? 0) > 0).Select(a => new AttackDeclaration(a.Id, defender)).ToList();
            }
        }

        // Otherwise pressure the biggest threat rather than the weakest player: hitting someone who is no danger
        // spends creatures we may need against the strong ones. Creatures that only defend well stay home.
        var candidates = attackers.Where(a => !IsDefensive(a)).ToList();
        if (defenders.Count > 1)
        {
            // Keep blockers back against whoever could hit us hardest next turn: creatures that block their attackers
            // well (survive or trade up), or anything at all if their swing would be lethal.
            var dangerous = defenders.OrderByDescending(d => PotentialAttack(view, d)).First();
            int danger = PotentialAttack(view, dangerous);
            var theirAttackers = view.Battlefield.Where(c => c.Controller == dangerous && (c.Types & CardType.Creature) != 0 && !Has(c, "Defender")).ToList();
            bool BlocksWell(CardView guard) => theirAttackers.Any(a =>
                CanBlock(guard, a) && ((guard.Toughness ?? 0) > (a.Power ?? 0) || (guard.Power ?? 0) >= RemainingToughness(a) || Has(guard, "Deathtouch")));
            var guards = danger >= view.Self.Life
                ? candidates.OrderByDescending(c => c.Toughness ?? 0).Take(2).ToList()
                : danger >= view.Self.Life / 3
                    ? candidates.Where(BlocksWell).OrderByDescending(c => c.Toughness ?? 0).Take(1).ToList()
                    : new List<CardView>();
            foreach (var guard in guards) candidates.Remove(guard);
        }
        var byThreat = defenders.OrderByDescending(d => Threat(view, d)).ToList();
        var declarations = new List<AttackDeclaration>();
        // Opposing planeswalkers: attack one with enough safe power to finish it.
        foreach (var walker in view.Battlefield.Where(c => (c.Types & CardType.Planeswalker) != 0 && defenders.Contains(c.Controller)).OrderByDescending(c => c.Loyalty))
        {
            var safe = candidates.Where(a => SafeToAttack(a, BlockersOf(walker.Controller))).OrderByDescending(a => a.Power ?? 0).ToList();
            var sent = new List<CardView>();
            foreach (var a in safe)
            {
                if (sent.Sum(x => x.Power ?? 0) >= walker.Loyalty) break;
                sent.Add(a);
            }
            if (sent.Sum(x => x.Power ?? 0) < walker.Loyalty) continue;
            foreach (var a in sent)
            {
                declarations.Add(new AttackDeclaration(a.Id, walker.Controller, walker.Id));
                candidates.Remove(a);
            }
        }
        foreach (var attacker in candidates)
        {
            foreach (var defender in byThreat)
            {
                if (!SafeToAttack(attacker, BlockersOf(defender))) continue;
                declarations.Add(new AttackDeclaration(attacker.Id, defender));
                break;
            }
        }
        if (declarations.Count > 0) await PaceAsync();
        return declarations;
    }

    /// <summary>Better kept back as a blocker than sent in: no power, or a wall-like body.</summary>
    private static bool IsDefensive(CardView c)
    {
        int power = c.Power ?? 0, toughness = c.Toughness ?? 0;
        return power <= 0 || (power <= 2 && toughness >= 2 * Math.Max(1, power) && toughness >= 4);
    }

    /// <summary>No blocker can kill it without dying in return, or nothing can block it at all.</summary>
    private static bool SafeToAttack(CardView attacker, List<CardView> blockers)
    {
        var able = blockers.Where(b => CanBlock(b, attacker)).ToList();
        if (able.Count == 0 || Has(attacker, "Can't be blocked")) return true;
        if (Has(attacker, "Menace") && able.Count < 2) return true;
        foreach (var b in able)
        {
            bool killsAttacker = (b.Power ?? 0) >= RemainingToughness(attacker) || Has(b, "Deathtouch");
            bool dies = (attacker.Power ?? 0) >= RemainingToughness(b) || Has(attacker, "Deathtouch");
            if (killsAttacker && !dies) return false;                                         // bad trade for us
            if (killsAttacker && dies && CreatureValue(attacker) > CreatureValue(b) + 1) return false; // we'd lose more
        }
        return true;
    }

    private static bool CanBlock(CardView blocker, CardView attacker) =>
        !Has(blocker, "Can't block") && (!Has(attacker, "Flying") || Has(blocker, "Flying") || Has(blocker, "Reach"));

    public async Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request)
    {
        RememberAttacks(view);
        var attackers = request.Attackers.Select(view.FindCard).OfType<CardView>().OrderByDescending(a => a.Power ?? 0).ToList();
        var free = request.Blockers.Select(view.FindCard).OfType<CardView>().ToList();
        int incoming = attackers.Where(a => !request.MinimumBlockers.ContainsKey(a.Id)).Sum(a => a.Power ?? 0)
                       + attackers.Where(a => request.MinimumBlockers.ContainsKey(a.Id)).Sum(a => a.Power ?? 0);
        int life = view.Self.Life;
        var blocks = new List<BlockDeclaration>();

        foreach (var attacker in attackers)
        {
            if (request.MinimumBlockers.TryGetValue(attacker.Id, out int min) && min > 1) continue; // menace: leave it
            var able = free.Where(b => request.CanBlock.TryGetValue(b.Id, out var list) && list.Contains(attacker.Id)).ToList();
            if (able.Count == 0) continue;
            bool BlockerKills(CardView b) => (b.Power ?? 0) >= RemainingToughness(attacker) || Has(b, "Deathtouch");
            bool BlockerDies(CardView b) => (attacker.Power ?? 0) >= RemainingToughness(b) || Has(attacker, "Deathtouch");

            CardView? pick =
                able.Where(b => BlockerKills(b) && !BlockerDies(b)).OrderBy(CreatureValue).FirstOrDefault()          // free kill
                ?? able.Where(b => !BlockerDies(b)).OrderBy(CreatureValue).FirstOrDefault(b => (attacker.Power ?? 0) >= 2) // safe wall
                ?? able.Where(b => BlockerKills(b) && CreatureValue(b) <= CreatureValue(attacker)).OrderBy(CreatureValue).FirstOrDefault(); // good trade
            if (pick is null && incoming >= life) pick = able.OrderBy(CreatureValue).First(); // chump to survive
            if (pick is null) continue;

            blocks.Add(new BlockDeclaration(pick.Id, attacker.Id));
            free.Remove(pick);
            incoming -= attacker.Power ?? 0;
        }
        if (!request.IsLegal(blocks, out _)) blocks.Clear();
        if (blocks.Count > 0) await PaceAsync();
        return blocks;
    }

    /// <summary>Always takes the commander back to the command zone; no other yes/no choices exist yet.</summary>
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Task.FromResult(true);

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Task.FromResult(request.Suggested);

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        Task.FromResult<IReadOnlyList<ManaTap>?>(request.SuggestedTaps);
}
