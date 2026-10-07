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

    /// <summary>What casting it means: the card, or its Adventure.</summary>
    private CardDefinition? SpellRules(GameView view, CastSpell cast) =>
        Rules(view, cast.Card) is { } rules
            ? cast.Half is { } half && rules.SplitHalves is { } halves ? halves[half]
            : cast.Adventure ? rules.Adventure : rules.Adventure is not null ? rules with { Adventure = null } : rules
            : null;

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
            CardChoicePurpose.ToHand or CardChoicePurpose.ToBattlefield or CardChoicePurpose.Keep => request.Options.OrderByDescending(Value).Take(request.Max),
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
        // Asked again with nothing changed: the last action came to nothing (the bot backed out of a target choice it had no
        // good answer for), so it isn't tried again from this same position. Only a safety net against looping: an action
        // that fails without the bot backing out is recorded by the engine (Game.FailedActions) and reported as an error.
        var signature = (view.TurnNumber, view.Step, view.Stack.Count, view.Self.Hand.Count, view.Self.ManaPoolTotal,
            view.Battlefield.Count, view.Battlefield.Count(c => c.Tapped), view.Players.Sum(p => p.Life));
        if (!signature.Equals(_lastSignature)) _backedOut.Clear();
        else if (_lastAction is { } previous && previous is not PassPriority) _backedOut.Add(previous);
        _lastSignature = signature;
        if (_backedOut.Count > 0) legal = legal.Where(a => !_backedOut.Contains(a)).ToList();
        var action = Decide(view, legal);
        _lastAction = action;
        if (action is not PassPriority) await PaceAsync();
        return action;
    }

    private object? _lastSignature;
    private PlayerAction? _lastAction;
    private readonly HashSet<PlayerAction> _backedOut = new();

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
            var spell = SpellRules(view, cast)?.Spell;
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
            var rules = SpellRules(view, cast);
            var card = view.FindCard(cast.Card);
            if (rules is null || card is null) continue;
            double score = 0;
            if (rules.Spell is { } spell)
            {
                score = SpellScore(view, spell);
                // Tricks (pump our own creature) are for combat, not the main phase.
                if (spell.Effects.All(e => !IsHarmful(e)) && spell.Targets.Count > 0) score = 0;
                // An Adventure is worth a little more: the card can still be cast from exile afterwards.
                if (cast.Adventure && score > 0) score += 1;
                else if (cast.Adventure && mainPhase && spell.Targets.Count == 0) score = Math.Max(score, 1);
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
                    PumpUntilEndOfTurn { What.Kind: SubjectKind.Each } p when IsHarmful(p) => EachValue(view, p.What.Filter!),
                    PumpUntilEndOfTurn { What.Kind: SubjectKind.Each } => 0.5,
                    _ => 0.2,
                };
            }
            return value;
        }
        if (spell.Targets[0].Kind == TargetKind.GraveyardCard && spell.Effects.OfType<PutOntoBattlefield>().Any(p => p.What is { Kind: SubjectKind.Target, Index: 0 }))
        {
            // Reanimation: worth the best creature card in a graveyard, less the life it may cost.
            var best = view.Players.SelectMany(p => p.Graveyard).Where(c => (c.Types & CardType.Creature) != 0).Select(c => (Value: CreatureValue(c), c.ManaCost)).DefaultIfEmpty().MaxBy(c => c.Value);
            double cost = spell.Effects.OfType<LoseLife>().Any(l => l.Who.Kind == SubjectKind.You) ? ManaValue(best.ManaCost) * 0.4 : 0;
            return best.Value - cost;
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
        bool beneficial = !AuraIsHarmful(aura);
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
            var spell = SpellRules(view, cast)?.Spell;
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
        int specIndex(int i) => Math.Min(i, request.Specs.Count - 1);
        for (int i = 0; ; i++)
        {
            bool extra = i >= request.Specs.Count; // more targets for an "any number" requirement
            if (extra && !request.LastIsAnyNumber) break;
            var allowed = request.LegalAt(i).Where(t => request.IsAllowed(i, t, chosen)).ToList();
            var real = allowed.Where(t => !t.IsNone).ToList();
            if (extra)
            {
                // Helpful "any number" effects take everything useful; harmful ones spread over a few of the best targets.
                bool harmful = ability is not null && TargetIsHarmed(ability, specIndex(i));
                if (real.Count == 0 || (harmful && chosen.Count >= 3)) break;
                var next = harmful ? BestHarmTarget(view, ability!, specIndex(i), real) : BestHelpTarget(view, real) ?? real[0];
                if (next is not { } n || (harmful && n.Player is not null)) break;
                chosen.Add(n);
                continue;
            }
            if (real.Count == 0)
            {
                if (allowed.Count == 0) return Task.FromResult<IReadOnlyList<Target>?>(request.CanCancel ? null : chosen.Append(request.LegalAt(i)[0]).ToList());
                chosen.Add(Target.None);
                continue;
            }
            Target pick;
            if (ability is not null && TargetIsHarmed(ability, specIndex(i)))
            {
                pick = BestHarmTarget(view, ability, specIndex(i), real) ?? real[0];
            }
            else
            {
                var pump = ability?.Effects.OfType<PumpUntilEndOfTurn>().FirstOrDefault(p => RefersToTarget(p, specIndex(i)));
                pick = (pump is not null ? FightToWin(view, pump) is { } fighter && real.Contains(Target.Of(fighter.Id)) ? Target.Of(fighter.Id) : (Target?)null : null)
                       ?? BestHelpTarget(view, real) ?? real[0];
            }
            if (!request.IsAllowed(i, pick, chosen)) pick = real[0];
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
        {
            // The Aura's target is judged by the Aura as a whole: a harmful one goes on an opponent's creature.
            var statics = rules.Abilities.OfType<StaticAbility>().ToList();
            var st = AuraIsHarmful(rules)
                ? statics.FirstOrDefault(StaticIsHarmful) ?? new StaticAbility(new AffectedFilter(AffectedScope.Enchanted)) { LosesAllAbilities = true }
                : statics.FirstOrDefault(s => !StaticIsHarmful(s)) ?? new StaticAbility(new AffectedFilter(AffectedScope.Enchanted));
            return st with { Targets = new[] { enchant } };
        }
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
        var declared = (await ChooseAttackersAsync(view, possibleAttackers, defenders))
            // A creature that can't attack the chosen player attacks another it may attack, or stays home.
            .Select(d => view.MayAttack(d.Attacker, d.Defender) || d.Planeswalker is not null ? d
                : defenders.FirstOrDefault(p => view.MayAttack(d.Attacker, p)) is var other && view.MayAttack(d.Attacker, other) ? d with { Defender = other } : null)
            .OfType<AttackDeclaration>().ToList();
        // Requirements and restrictions ("attacks each combat if able", goad, "can't attack alone"): made legal, keeping the plan.
        if (view.AttackRequest is { } rules && !rules.IsLegal(declared, out _)) declared = rules.Complete(declared).ToList();
        if (view.AttackTaxes.Count == 0) return declared;
        // Only as many attackers as the attack taxes can be paid for, the strongest first; taxes add up across defenders.
        var budget = view.AttackTaxes.ToDictionary(t => t.Defender, t => t.Affordable);
        int affordable = view.AttackTaxes.Select(t => t.Affordable).DefaultIfEmpty(int.MaxValue).Min();
        var kept = new List<AttackDeclaration>();
        int taxedSoFar = 0;
        foreach (var d in declared.OrderByDescending(d => view.FindCard(d.Attacker)?.Power ?? 0))
        {
            if (!budget.ContainsKey(d.Defender)) { kept.Add(d); continue; }
            if (taxedSoFar >= affordable || taxedSoFar >= budget[d.Defender]) continue;
            kept.Add(d);
            taxedSoFar++;
        }
        // Leaving taxed attackers home may leave a creature that can't attack alone on its own: keep the rest legal.
        if (view.AttackRequest is { } rest && !rest.IsLegal(kept, out _)) kept = rest.WithoutTaxedDefenders().Complete(kept).ToList();
        return kept;
    }

    private async Task<IReadOnlyList<AttackDeclaration>> ChooseAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders)
    {
        var attackers = possibleAttackers.Select(view.FindCard).OfType<CardView>().ToList();
        List<CardView> BlockersOf(PlayerId p) =>
            view.Battlefield.Where(c => c.Controller == p && (c.Types & CardType.Creature) != 0 && !c.Tapped && !Has(c, "Can't block")).ToList();

        // Alpha strike at an opponent our whole attack finishes even after their best blocks (chumps included).
        foreach (var defender in defenders.OrderBy(d => view.Players[d.Value].Life))
        {
            var swing = attackers.Where(a => (a.Power ?? 0) > 0).ToList();
            if (swing.Count == 0) break;
            int life = view.Players[defender.Value].Life;
            var predicted = AssignBlocks(swing, BlockersOf(defender), life, (b, a) => CanBlock(b, a) && !Has(a, "Can't be blocked"), a => Has(a, "Menace") ? 2 : 1);
            if (swing.Sum(a => Through(a, predicted[a.Id])) >= life)
            {
                await PaceAsync();
                return swing.Select(a => new AttackDeclaration(a.Id, defender)).ToList();
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
        // Creatures that attack each combat if able always go (at the biggest threat).
        foreach (var forced in attackers.Where(a => a.AttacksEachCombat && declarations.All(d => d.Attacker != a.Id)))
            declarations.Add(new AttackDeclaration(forced.Id, byThreat[0]));
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
        if (!Has(attacker, "Menace"))
            foreach (var b in able)
            {
                bool killsAttacker = Kills(attacker, new[] { b });
                bool dies = Dies(b, attacker);
                if (killsAttacker && !dies) return false;                                         // bad trade for us
                if (killsAttacker && dies && CreatureValue(attacker) > CreatureValue(b) + 1) return false; // we'd lose more
            }
        // Two blockers that kill it while it takes down at most one of them, worth clearly less.
        for (int i = 0; i < able.Count; i++)
            for (int j = i + 1; j < able.Count; j++)
            {
                var pair = new[] { able[i], able[j] };
                if (!Kills(attacker, pair)) continue;
                double lost = pair.Where(b => Dies(b, attacker)).Select(CreatureValue).DefaultIfEmpty(0).Max();
                if (lost + 1 < CreatureValue(attacker)) return false;
            }
        return true;
    }

    private static bool CanBlock(CardView blocker, CardView attacker) =>
        !Has(blocker, "Can't block") && (!Has(attacker, "Flying") || Has(blocker, "Flying") || Has(blocker, "Reach"));

    public async Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request)
    {
        RememberAttacks(view);
        var blocks = PlanBlocks(view, request);
        // Requirements (lures, "must be blocked") and restrictions: the closest legal declaration to the plan (rule 509.1c).
        if (!request.IsLegal(blocks, out _)) blocks = request.Complete(blocks).ToList();
        if (blocks.Count > 0) await PaceAsync();
        return blocks;
    }

    /// <summary>
    /// Blocks in three steps: first survive if the attack is lethal (chump the biggest threats, two blockers on a
    /// menace attacker); if nothing saves us, use every blocker to take as many attackers down as possible; otherwise
    /// make good blocks (free kills, safe walls, fair trades) and double blocks that kill a big attacker.
    /// </summary>
    public static List<BlockDeclaration> PlanBlocks(GameView view, BlockRequest request)
    {
        var attackers = request.Attackers.Select(view.FindCard).OfType<CardView>().ToList();
        var blockers = request.Blockers.Select(view.FindCard).OfType<CardView>().ToList();
        var assigned = AssignBlocks(attackers, blockers, view.Self.Life,
            (b, a) => request.CanBlock.TryGetValue(b.Id, out var list) && list.Contains(a.Id),
            a => Math.Max(1, request.MinimumBlockers.GetValueOrDefault(a.Id, 1)));
        return assigned.SelectMany(kv => kv.Value.Select(b => new BlockDeclaration(b.Id, kv.Key))).ToList();
    }

    /// <summary>Damage that still gets through from an attacker with these blockers (trample goes past lethal damage).</summary>
    private static int Through(CardView a, IReadOnlyList<CardView> by) =>
        by.Count == 0 ? a.Power ?? 0
        : Has(a, "Trample") ? Math.Max(0, (a.Power ?? 0) - by.Sum(b => Has(a, "Deathtouch") ? 1 : Math.Max(0, RemainingToughness(b)))) : 0;

    /// <summary>Whether these blockers together destroy the attacker (a first striker kills what it can before they hit back).</summary>
    private static bool Kills(CardView a, IReadOnlyList<CardView> by)
    {
        var hitting = by.ToList();
        if (Has(a, "First strike") || Has(a, "Double strike"))
        {
            int strike = a.Power ?? 0;
            foreach (var b in by.Where(b => !Has(b, "First strike") && !Has(b, "Double strike")).OrderBy(RemainingToughness))
            {
                int need = Has(a, "Deathtouch") ? 1 : RemainingToughness(b);
                if (strike < need) break;
                strike -= need;
                hitting.Remove(b);
            }
        }
        if (Has(a, "Indestructible")) return false;
        return hitting.Any(b => Has(b, "Deathtouch") && (b.Power ?? 0) > 0) || hitting.Sum(b => b.Power ?? 0) >= RemainingToughness(a);
    }

    /// <summary>Whether a blocker dies to the attacker's damage.</summary>
    private static bool Dies(CardView b, CardView a) =>
        !Has(b, "Indestructible") && ((a.Power ?? 0) >= RemainingToughness(b) || (Has(a, "Deathtouch") && (a.Power ?? 0) > 0));

    /// <summary>
    /// The blocking plan for a defender at <paramref name="life"/>: which blockers go on which attacker. Used for
    /// our own blocks and to predict an opponent's blocks when deciding how to attack.
    /// </summary>
    public static Dictionary<CardId, List<CardView>> AssignBlocks(IReadOnlyList<CardView> attackers, IReadOnlyList<CardView> allBlockers, int life,
        Func<CardView, CardView, bool> Able, Func<CardView, int> Needed)
    {
        var assigned = attackers.ToDictionary(a => a.Id, _ => new List<CardView>());
        var free = allBlockers.ToList();
        int Incoming() => attackers.Sum(a => Through(a, assigned[a.Id]));
        void Assign(CardView a, IEnumerable<CardView> by)
        {
            foreach (var b in by.ToList()) { assigned[a.Id].Add(b); free.Remove(b); }
        }

        // The cheapest group of free blockers that can legally block this attacker (with the most damage stopped).
        List<CardView>? CheapestGroup(CardView a, Func<List<CardView>, bool>? goal = null)
        {
            var able = free.Where(b => Able(b, a)).OrderBy(CreatureValue).ToList();
            int need = Needed(a) - assigned[a.Id].Count;
            if (able.Count < Math.Max(0, need)) return null;
            var group = able.Take(Math.Max(0, need)).ToList();
            if (goal is null) return group;
            foreach (var extra in able.Skip(group.Count))
            {
                if (goal(assigned[a.Id].Concat(group).ToList())) return group;
                group.Add(extra);
            }
            return goal(assigned[a.Id].Concat(group).ToList()) ? group : null;
        }

        // 1. Survive: stop the most damage per blocker spent until the attack isn't lethal.
        while (Incoming() >= life)
        {
            var best = attackers
                .Where(a => assigned[a.Id].Count == 0)
                .Select(a => (Attacker: a, Group: CheapestGroup(a)))
                .Where(x => x.Group is { Count: > 0 })
                .Select(x => (x.Attacker, Group: x.Group!, Saved: (x.Attacker.Power ?? 0) - Through(x.Attacker, x.Group!)))
                .Where(x => x.Saved > 0)
                .OrderByDescending(x => (double)x.Saved / x.Group.Count).ThenBy(x => x.Group.Sum(CreatureValue))
                .FirstOrDefault();
            if (best.Attacker is null) break;
            Assign(best.Attacker, best.Group);
        }

        if (Incoming() >= life)
        {
            // 2. We lose anyway: throw everything in to destroy as many attackers as we can, most valuable first.
            foreach (var a in attackers) assigned[a.Id].Clear();
            free = allBlockers.ToList();
            foreach (var a in attackers.OrderByDescending(CreatureValue))
                if (CheapestGroup(a, by => Kills(a, by)) is { } group) Assign(a, group);
            // Whatever is left still blocks (it can't make things worse): pile on blocked attackers that would survive,
            // then on unblocked ones it can block alone.
            foreach (var b in free.ToList())
            {
                var target = attackers.Where(a => Able(b, a) && assigned[a.Id].Count > 0 && !Kills(a, assigned[a.Id])).OrderByDescending(CreatureValue).FirstOrDefault()
                             ?? attackers.Where(a => Able(b, a) && assigned[a.Id].Count == 0 && Needed(a) == 1).OrderByDescending(a => a.Power ?? 0).FirstOrDefault();
                if (target is not null) Assign(target, new[] { b });
            }
        }
        else
        {
            // 3. Not lethal: good single blocks, then double blocks that kill a big attacker.
            foreach (var a in attackers.Where(a => assigned[a.Id].Count == 0 && Needed(a) == 1).OrderByDescending(a => a.Power ?? 0))
            {
                var able = free.Where(b => Able(b, a)).ToList();
                CardView? pick =
                    able.Where(b => Kills(a, new[] { b }) && !Dies(b, a)).OrderBy(CreatureValue).FirstOrDefault()                       // free kill
                    ?? able.Where(b => !Dies(b, a)).OrderBy(CreatureValue).FirstOrDefault(b => (a.Power ?? 0) >= 2)                      // safe wall
                    ?? able.Where(b => Kills(a, new[] { b }) && CreatureValue(b) <= CreatureValue(a)).OrderBy(CreatureValue).FirstOrDefault(); // fair trade
                if (pick is not null) Assign(a, new[] { pick });
            }
            foreach (var a in attackers.Where(a => assigned[a.Id].Count == 0).OrderByDescending(CreatureValue))
            {
                var able = free.Where(b => Able(b, a)).OrderByDescending(b => b.Power ?? 0).ToList();
                (CardView, CardView)? bestPair = null;
                double bestCost = double.MaxValue;
                for (int i = 0; i < able.Count; i++)
                    for (int j = i + 1; j < able.Count; j++)
                    {
                        var pair = new[] { able[i], able[j] };
                        if (!Kills(a, pair)) continue;
                        // The attacker kills at most one of them (it assigns its damage); count the one we'd lose.
                        double lost = pair.Where(b => Dies(b, a)).Select(CreatureValue).DefaultIfEmpty(0).Max();
                        if (lost < CreatureValue(a) && lost < bestCost) { bestCost = lost; bestPair = (able[i], able[j]); }
                    }
                if (bestPair is { } p2) Assign(a, new[] { p2.Item1, p2.Item2 });
            }
        }
        return assigned;
    }

    /// <summary>Always takes the commander back to the command zone; no other yes/no choices exist yet.</summary>
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Task.FromResult(true);

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Task.FromResult(request.Suggested);

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        Task.FromResult<IReadOnlyList<ManaTap>?>(request.SuggestedTaps);
}
