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
        if (request.Suggested is { } suggested) return Task.FromResult(suggested);
        if (request.Kind == OptionKind.CardName) return Task.FromResult(ChooseCardName(view, request));
        if (request.Kind == OptionKind.Other && CostChoice(view, request) is { } way) return Task.FromResult(way);
        // A card type to have protection from: creatures are what attacks and blocks.
        if (request.Prompt.Contains("card type", StringComparison.OrdinalIgnoreCase)
            && request.Options.ToList().FindIndex(o => o.Equals("Creature", StringComparison.OrdinalIgnoreCase)) is var creature and >= 0)
            return Task.FromResult(creature);
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

    /// <summary>
    /// A card name: for its own permanent ("activated abilities of sources with the chosen name …") the name of an opponent's
    /// visible permanent with the most abilities; asked by an opponent's effect ("that player chooses a card name"), the name
    /// of its own best creature card it can see, which is what the opponent would most like to find.
    /// </summary>
    private int ChooseCardName(GameView view, OptionRequest request)
    {
        bool ours = request.Source is { } source && view.FindCard(source) is { } src && src.Controller == _me;
        var candidates = ours
            ? view.Battlefield.Where(c => c.Controller != _me && !c.IsHidden).OrderByDescending(c => c.AbilityTexts.Count).ThenByDescending(c => ManaValue(c.ManaCost))
            : view.Self.Hand.Concat(view.Self.Graveyard).Concat(view.Battlefield.Where(c => c.Owner == _me))
                .Where(c => !c.IsHidden && (c.Types & CardType.Creature) != 0 && !c.IsToken).OrderByDescending(CreatureValue);
        foreach (var card in candidates)
        {
            int index = request.Options.ToList().IndexOf(card.Name ?? "");
            if (index >= 0) return index;
        }
        return 0;
    }

    private static string ColorLetter(string color) => color switch { "Blue" => "U", _ => color[..1] };

    /// <summary>
    /// A choice among ways to pay ("choose the additional cost", "you may sacrifice a Food or pay {2}{W}"): mana rather than
    /// giving up a permanent or a card, and an optional payment declined when what it gives isn't worth it. Null: not a cost.
    /// </summary>
    private int? CostChoice(GameView view, OptionRequest request)
    {
        bool optional = request.Options.Count > 0 && request.Options[^1] == "Don't pay";
        if (!optional && !request.Prompt.EndsWith(": choose the additional cost", StringComparison.Ordinal)) return null;
        int mana = request.Options.ToList().FindIndex(o => (o.StartsWith('{') || o.StartsWith("Pay {", StringComparison.Ordinal)) && !o.Contains(" and ", StringComparison.Ordinal));
        if (mana >= 0) return mana;
        if (optional && request.Source is { } source && FindMayPay(view, source, request.Prompt) is { Options: { } ways } pay
            && !ways.Any(w => PaymentWorthIt(view, source, pay, w.Extra))) return request.Options.Count - 1;
        return 0;
    }

    /// <summary>The "you may pay … If you do, …" of a source with this prompt, if any.</summary>
    private MayPay? FindMayPay(GameView view, CardId source, string prompt)
    {
        var rules = Rules(view, source) ?? _definitions(source);
        if (rules is null) return null;
        var abilities = rules.Spell is { } spell ? rules.Abilities.Append(spell) : rules.Abilities;
        return abilities.SelectMany(a => AllEffects(a.Effects)).OfType<MayPay>().FirstOrDefault(m => m.Prompt == prompt);
    }

    private static IEnumerable<Effect> AllEffects(IEnumerable<Effect> effects)
    {
        foreach (var effect in effects)
        {
            yield return effect;
            var inner = effect switch
            {
                MayDo m => m.Effects, MayPay m => m.Else is { } e ? m.Effects.Concat(e) : m.Effects, MayPayX m => m.Effects,
                IfThen i => i.Else is { } e ? i.Then.Concat(e) : i.Then,
                _ => null,
            };
            if (inner is not null) foreach (var e in AllEffects(inner)) yield return e;
        }
    }

    /// <summary>
    /// Whether to sacrifice for an optional payoff: never the commander, and only what is worth less than the payoff, or, for
    /// a look at the top of the library (surveil, scry), something cheap when there are no spells left in hand to cast.
    /// Paying mana or life is left as it was (yes).
    /// </summary>
    private bool PaymentWorthIt(GameView view, CardId source, MayPay pay, ExtraCost? extra)
    {
        if (extra is not { Sacrifice: { } filter }) return true;
        var given = SacrificeCandidates(view, filter, source).OrderBy(KeepValue).Take(extra.SacrificeCount).ToList();
        if (given.Count < extra.SacrificeCount || given.Any(c => c.IsCommander)) return false;
        double lost = given.Sum(KeepValue), gain = PayoffValue(pay.Effects);
        if (gain >= lost) return true;
        bool selection = Unwrapped(pay.Effects).All(e => e is Scry or Surveil or DrawCards);
        return selection && NeedsCards(view) && given.All(c => KeepValue(c) <= CheapPermanent);
    }

    /// <summary>Roughly what effects done for us are worth, on the scale of <see cref="KeepValue"/>.</summary>
    private static double PayoffValue(IEnumerable<Effect> effects) => Unwrapped(effects).Sum(effect => effect switch
    {
        DrawCards d when d.Who.Kind == SubjectKind.You => 1.5 * d.Count.Estimate,
        CreateTokens t => 2 * t.Count.Estimate,
        Scry s => 0.4 * s.Count,
        Surveil s => 0.4 * s.Count,
        DealDamage d => d.Amount.Estimate,
        GainLife g => 0.3 * g.Amount.Estimate,
        AddCounters { Kind: CounterKind.PlusOnePlusOne } a => 1.5 * a.Count.Estimate,
        _ => 1.0,
    });

    /// <summary>
    /// Use everything available for X; a loyalty cost of −X keeps the planeswalker alive; X paid in life is the X planned when
    /// the spell was chosen, and never all our life.
    /// </summary>
    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request)
    {
        int chosen = request.Prompt.Contains("loyalty counters", StringComparison.Ordinal) ? Math.Max(request.Min, request.Max - 1) : request.Max;
        if (request.Prompt.Contains("pay X life", StringComparison.Ordinal))
        {
            int planned = request.Source is { } planFor && _plannedLifeX.Remove(planFor, out int x) ? x : 0;
            chosen = Math.Max(request.Min, Math.Min(Math.Min(planned, request.Max), view.Self.Life - 1));
        }
        if (request.Source is { } source) _announcedX[source] = chosen; // X is announced before targets (rule 601.2b)
        return Task.FromResult(chosen);
    }

    /// <summary>The X the spell or ability being judged or targeted has, when known (null: planned as a rough 2).</summary>
    private int? _x;

    /// <summary>The last number each source announced (its X), for choosing its targets.</summary>
    private readonly Dictionary<CardId, int> _announcedX = new();

    /// <summary>The X planned for a spell whose X is paid in life ("pay X life"), from when it was chosen to cast.</summary>
    private readonly Dictionary<CardId, int> _plannedLifeX = new();

    /// <summary>
    /// The best X to pay in life for a spell, and its score less the life: the smallest X that does the most good, keeping at
    /// least a quarter of our life (and 6) in hand. 0 when no X is worth it.
    /// </summary>
    private (double Score, int X) BestLifeX(GameView view, AbilityDefinition spell)
    {
        int life = view.Self.Life;
        int most = life - Math.Max(6, life / 4);
        (double Score, int X) best = (0, 0);
        for (int x = 1; x <= most; x++)
        {
            _x = x;
            double score = SpellScore(view, spell) - 0.2 * x;
            if (score > best.Score) best = (score, x);
        }
        _x = null;
        return best.Score >= 2 ? best : (0, 0);
    }

    /// <summary>The largest X a spell with this cost could have now: our untapped lands and floating mana, less the rest of the cost.</summary>
    private int AffordableX(GameView view, string cost)
    {
        int xs = 0;
        for (int i = cost.IndexOf("{X}", StringComparison.Ordinal); i >= 0; i = cost.IndexOf("{X}", i + 3, StringComparison.Ordinal)) xs++;
        return Math.Max(0, (AvailableMana(view) - (ManaValue(cost) - xs)) / Math.Max(1, xs)); // ManaValue counts each {X} as 1
    }

    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request)
    {
        int lands = view.Battlefield.Count(c => c.Controller == _me && (c.Types & CardType.Land) != 0)
                    + view.Self.Hand.Count(c => (c.Types & CardType.Land) != 0);
        // Lands are dead draws once there are enough; spells far above the mana available are slow.
        bool Unwanted(CardView c) => (c.Types & CardType.Land) != 0 ? lands >= 5 : ManaValue(c.ManaCost) > lands + 2;
        double Value(CardView c) => request.Purpose == CardChoicePurpose.Sacrifice ? KeepValue(c)
            : (c.Types & CardType.Creature) != 0 ? CreatureValue(c) : 1 + ManaValue(c.ManaCost);

        IEnumerable<CardView> picks = request.Purpose switch
        {
            CardChoicePurpose.ScryToBottom or CardChoicePurpose.SurveilToGraveyard => request.Options.Where(Unwanted),
            CardChoicePurpose.ToHand or CardChoicePurpose.ToBattlefield or CardChoicePurpose.Keep => request.Options.OrderByDescending(Value).Take(request.Max),
            // "A source of your choice": the opponents' most dangerous creature.
            // -1/-1 counters: on the least valuable creature that survives them, else on the least valuable one.
            CardChoicePurpose.Weaken => request.Options.OrderByDescending(c => c.Toughness is { } t && t > request.Amount).ThenBy(Value).Take(request.Min),
            CardChoicePurpose.DamageSource => request.Options.OrderByDescending(c => c.Controller != _me).ThenByDescending(c => c.Power ?? 0).ThenByDescending(Value).Take(request.Min),
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
            // A face-down creature it can turn face up: before combat, so it attacks as what it really is.
            if (view.Step == Step.PrecombatMain && legal.OfType<TurnFaceUp>().FirstOrDefault() is { } faceUp) return faceUp;
            if (BestSpell(view, legal, mainPhase: true) is { } spell) return spell;
            if (LoyaltyAbility(view, legal) is { } loyalty) return loyalty;
            if (view.Step == Step.PrecombatMain && Equip(view, legal) is { } equip) return equip;
            if (view.Step == Step.PostcombatMain && UsefulActivation(view, legal) is { } ping) return ping;
            // Mana left over with nothing better to do: the companion comes into the hand.
            if (legal.OfType<PutCompanionIntoHand>().FirstOrDefault() is { } companion) return companion;
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
            var rules = SpellRules(view, cast);
            if (rules?.Spell is { } spell && spell.Effects.Any(e => e is CounterSpell) && CounterCostWorthIt(view, cast, rules, top)) return cast;
        }
        return null;
    }

    /// <summary>
    /// A counterspell whose cost sacrifices (when no way of paying it with mana alone is affordable) is cast only when what it
    /// gives up is worth less than what it saves, and never to save the very permanent it would sacrifice.
    /// </summary>
    private bool CounterCostWorthIt(GameView view, CastSpell cast, CardDefinition rules, StackItemView top)
    {
        int mana = AvailableMana(view) - ManaValue(view.FindCard(cast.Card)?.ManaCost);
        var sacrifices = new List<ExtraCost>();
        if (rules.AdditionalCost is { Sacrifice: not null } always) sacrifices.Add(always);
        if (rules.AdditionalCostOptions is { Count: > 0 } options
            && !options.Any(o => o.Extra is not { Sacrifice: not null } && (o.Mana?.ManaValue ?? 0) <= mana))
            sacrifices.AddRange(options.Select(o => o.Extra).OfType<ExtraCost>().Where(e => e.Sacrifice is not null).Take(1));
        if (sacrifices.Count == 0) return true;
        var saved = top.Targets.Select(t => t.Card).OfType<CardId>().Select(view.FindCard).OfType<CardView>()
            .Where(c => c.Controller == _me && c.Zone == Zone.Battlefield).ToList();
        double worth = saved.Count > 0 ? saved.Sum(PermanentValue) : ManaValue(top.Card.ManaCost);
        foreach (var cost in sacrifices)
        {
            var given = SacrificeCandidates(view, cost.Sacrifice!, cast.Card).OrderBy(KeepValue).Take(cost.SacrificeCount).ToList();
            if (given.Count < cost.SacrificeCount || given.Any(g => saved.Any(s => s.Id == g.Id)) || given.Sum(KeepValue) >= worth) return false;
        }
        return true;
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
            if (rules is { Spell: { } lifeSpell, PayXLife: true })
            {
                (score, int x) = BestLifeX(view, lifeSpell);
                _plannedLifeX[card.Id] = x;
            }
            else if (rules.Spell is { } spell)
            {
                _x = card.ManaCost?.Contains("{X}") == true ? AffordableX(view, card.ManaCost) : null;
                score = SpellScore(view, spell);
                _x = null;
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
        var hit = view.Battlefield.Where(c => Hits(filter, c)).ToList();
        return hit.Where(c => c.Controller != _me).Sum(CreatureValue) - hit.Where(c => c.Controller == _me).Sum(CreatureValue) - 1;
    }

    private bool Hits(ObjectFilter filter, CardView c) =>
        (filter.Types == 0 || (c.Types & filter.Types) != 0)
        && (filter.Controller == ControllerFilter.Any || (filter.Controller == ControllerFilter.Opponent) == (c.Controller != _me));

    /// <summary>
    /// Net value of a sweeper that kills the creatures matching the filter for which <paramref name="dies"/> holds: the
    /// opponents' creatures it kills less ours, which weigh more (and our commander most), so only a lopsided sweep pays.
    /// </summary>
    private double EachKillValue(GameView view, ObjectFilter filter, Func<CardView, bool> dies)
    {
        var killed = view.Battlefield.Where(c => (c.Types & CardType.Creature) != 0 && Hits(filter, c) && dies(c)).ToList();
        return killed.Where(c => c.Controller != _me).Sum(CreatureValue)
               - killed.Where(c => c.Controller == _me).Sum(c => 1.5 * CreatureValue(c) + (c.IsCommander ? 6 : 0)) - 1;
    }

    /// <summary>A quantity's value with the X being judged.</summary>
    private int Amount(Quantity q) => q.Kind == QuantityKind.X && _x is { } x ? x * q.Multiplier : q.Estimate;

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
                    DealDamage { To.Kind: SubjectKind.Each } d => EachKillValue(view, d.To.Filter!,
                        c => !Has(c, "Indestructible") && Amount(d.Amount) >= RemainingToughness(c)),
                    PumpUntilEndOfTurn { What.Kind: SubjectKind.Each } p when IsHarmful(p) => EachKillValue(view, p.What.Filter!,
                        c => (c.Toughness ?? 0) + Amount(p.Toughness) is var t && (t <= 0 || t <= c.Damage && !Has(c, "Indestructible"))),
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
        if (DamagesTarget(spell, 0) && DamageTo(spell, 0, _x) <= 0) return 0; // an X of 0 does nothing
        var target = BestHarmTarget(view, spell, 0, LegalHarmTargets(view, spell.Targets[0]));
        if (target is null) return 0;
        if (target.Value.Player is { } facePlayer)
        {
            int damage = DamageTo(spell, 0, _x);
            return damage >= view.Players[facePlayer.Value].Life ? 100 : 0; // burn to the face only to finish a player
        }
        // A noncreature by what it does (a Food or Treasure token is barely worth a card).
        return view.FindCard(target.Value.Card!.Value) is { } victim ? victim.Power is null ? PermanentValue(victim) : 1 + CreatureValue(victim) : 0;
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
            .Where(o => (o.Ability?.Cost.Loyalty is not null || o.Ability?.Cost.LoyaltyX == true) && o.Card is not null)
            .ToList();
        if (options.Count == 0) return null;
        foreach (var group in options.GroupBy(o => o.Action.Source))
        {
            int loyalty = group.First().Card!.Loyalty;
            var ultimate = group.Where(o => o.Ability!.Cost.Loyalty! < 0).OrderBy(o => o.Ability!.Cost.Loyalty).FirstOrDefault();
            if (ultimate.Ability is not null && -ultimate.Ability.Cost.Loyalty! >= 6) return ultimate.Action;
            // −X: worth it when X (all but one loyalty) does something good.
            var minusX = group.FirstOrDefault(o => o.Ability!.Cost.LoyaltyX);
            if (minusX.Ability is not null && loyalty > 1 && SpellScore(view, minusX.Ability) >= 4) return minusX.Action;
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
            if (ability is null || ability.Effects.Any(e => e is AttachSelf) || ability.Cost.Loyalty is not null || ability.Cost.LoyaltyX) continue;
            if (ability.Cost.SacrificeSelf) continue; // keep sacrifice outlets for emergencies
            // A card for two artifacts, a draw for a discard: only worth it out of cards, with cheap things to give up.
            if (SpendsCards(ability.Cost) && !(ability.Effects.Any(e => e is DrawCards) && NeedsCards(view) && CheapSacrifice(view, ability.Cost, activate.Source))) continue;
            if (ability.Targets.Count == 0)
            {
                // A Class's next level: its new section is the point of the card.
                if (ability.Effects.Any(e => e is DrawCards or CreateTokens or BecomeClassLevel)) return activate;
                continue;
            }
            if (!TargetIsHarmed(ability, 0)) continue;
            var target = BestHarmTarget(view, ability, 0, LegalHarmTargets(view, ability.Targets[0]));
            if (target is { Card: { } c } && view.FindCard(c) is { } victim && DamageTo(ability, 0) >= RemainingToughness(victim)) return activate;
            if (target is { Player: not null } && DamageTo(ability, 0) > 0) return activate; // chip damage at the right moment
        }
        return null;
    }

    /// <summary>
    /// Costs that give up other cards or permanents (sacrifice another permanent, discard, return or exile one):
    /// paying them just to draw or make a token loses board for nothing.
    /// </summary>
    private static bool SpendsCards(AbilityCost cost) =>
        cost.ExileSelf || cost.ReturnSelfToHand || cost.DiscardX
        || cost.Extra is { } extra && (extra.Sacrifice is not null || extra.Discard > 0 || extra.ReturnToHand is not null);

    /// <summary>Out of things to do: no spells left in hand (only lands, or nothing).</summary>
    private static bool NeedsCards(GameView view) => view.Self.Hand.All(c => (c.Types & CardType.Land) != 0);

    /// <summary>
    /// A cost that only sacrifices permanents (the least valuable ones, as <see cref="ChooseCardsAsync"/> picks them) and those
    /// are worth little: tokens, small artifacts. Never the commander, never cards from the hand.
    /// </summary>
    private bool CheapSacrifice(GameView view, AbilityCost cost, CardId source)
    {
        if (cost.ExileSelf || cost.ReturnSelfToHand || cost.DiscardX || cost.Extra is not { Sacrifice: { } filter } extra
            || extra.Discard > 0 || extra.ReturnToHand is not null) return false;
        var picks = SacrificeCandidates(view, filter, source).OrderBy(KeepValue).Take(extra.SacrificeCount).ToList();
        return picks.Count == extra.SacrificeCount && picks.All(c => !c.IsCommander && KeepValue(c) <= CheapPermanent);
    }

    /// <summary>Our permanents a sacrifice cost could take (roughly: its types, supertype, subtype and token-ness).</summary>
    private IEnumerable<CardView> SacrificeCandidates(GameView view, ObjectFilter filter, CardId source) =>
        view.Battlefield.Where(c => c.Controller == _me && !(filter.Other && c.Id == source) && SacrificeMatches(filter, c));

    private static bool SacrificeMatches(ObjectFilter f, CardView c) =>
        (f.AnyOf is not { Count: > 0 } any || any.Any(a => SacrificeMatches(a, c)))
        && (f.Types == 0 || (c.Types & f.Types) != 0) && (f.ExcludedTypes & c.Types) == 0
        && (f.Supertype == 0 || (c.Supertypes & f.Supertype) != 0)
        && (f.Subtype is null || c.Subtypes.Contains(f.Subtype, StringComparer.OrdinalIgnoreCase))
        && (f.Token is null || c.IsToken == f.Token);

    /// <summary>Our untapped lands and floating mana: about what we could still pay.</summary>
    private int AvailableMana(GameView view) =>
        view.Battlefield.Count(c => c.Controller == _me && !c.Tapped && (c.Types & CardType.Land) != 0) + view.Self.ManaPoolTotal;

    /// <summary>The most a permanent may be worth to be sacrificed for a card (a 1/1 flying token, a small artifact).</summary>
    private const double CheapPermanent = 4.5;

    /// <summary>How much we'd rather keep a permanent of ours: creatures by their stats, mana sources higher, the commander most.</summary>
    private static double KeepValue(CardView c)
    {
        double value = (c.Types & CardType.Creature) != 0 ? CreatureValue(c) : 1 + ManaValue(c.ManaCost) + c.AbilityTexts.Count;
        if (c.AbilityTexts.Any(t => t.Contains("Add {", StringComparison.Ordinal))) value += 2;
        if ((c.Types & CardType.Land) != 0) value += 4;
        if (c.IsCommander) value += 100;
        return value;
    }

    /// <summary>A pump spell that wins a combat for one of our creatures, or a "gets -N/-N" that turns a combat our way.</summary>
    private PlayerAction? CombatTrick(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        foreach (var cast in legal.OfType<CastSpell>())
        {
            var spell = SpellRules(view, cast)?.Spell;
            if (spell is null || spell.Targets.Count != 1) continue;
            var pump = spell.Effects.OfType<PumpUntilEndOfTurn>().FirstOrDefault(p => p.Power.Estimate + p.Toughness.Estimate > 0);
            if (pump is not null && FightToWin(view, pump) is not null) return cast;
            if (OnlyLethalHarm(spell, 0) && ShrinkToWin(view, spell) is { } victim)
            {
                _plannedHarm[cast.Card] = victim.Id;
                return cast;
            }
        }
        return null;
    }

    /// <summary>
    /// An opposing creature in combat that a "gets -N/-N" spell is worth a card on, once blocks are known: one it kills, an
    /// attacker or blocker our creatures then kill, one that then no longer kills our creature, or an unblocked attacker
    /// whose damage would otherwise finish us.
    /// </summary>
    private CardView? ShrinkToWin(GameView view, AbilityDefinition spell)
    {
        int damage = DamageTo(spell, 0), shrink = ShrinkOf(spell, 0);
        int lost = Unwrapped(spell.Effects).OfType<PumpUntilEndOfTurn>().Where(p => RefersToTarget(p, 0)).Sum(p => -p.Power.Estimate);
        static int PowerOf(CardView c) => Math.Max(0, c.Power ?? 0);
        bool Targetable(CardView c) => c.Controller != _me && !Has(c, "Hexproof") && !Has(c, "Shroud");
        int unblocked = view.Attacks.Where(a => a.Defender == _me && a.Planeswalker is null && !a.IsBlocked)
            .Select(a => view.FindCard(a.Attacker)).OfType<CardView>().Sum(PowerOf);

        CardView? best = null;
        double bestGain = 1.5; // worth about a card
        void Consider(CardView c, double gain)
        {
            if (Targetable(c) && gain > bestGain) { bestGain = gain; best = c; }
        }
        foreach (var attack in view.Attacks)
        {
            var attacker = view.FindCard(attack.Attacker);
            if (attacker is null) continue;
            var blockers = attack.Blockers.Select(view.FindCard).OfType<CardView>().ToList();
            if (attacker.Controller != _me)
            {
                if (attack.Defender != _me && blockers.All(b => b.Controller != _me)) continue;
                int incoming = blockers.Sum(PowerOf);
                bool deathtouch = blockers.Any(b => Has(b, "Deathtouch"));
                double gain = DiesFrom(attacker, damage + incoming, shrink, deathtouch) && !DiesFrom(attacker, incoming, 0, deathtouch) ? CreatureValue(attacker) : 0;
                // Our blockers it no longer kills (each judged as if it took all the attacker's damage).
                int power = PowerOf(attacker), weaker = Math.Max(0, power - lost);
                gain += blockers.Where(b => b.Controller == _me && DiesFrom(b, power, 0, Has(attacker, "Deathtouch")) && !DiesFrom(b, weaker, 0, Has(attacker, "Deathtouch") && weaker > 0))
                    .Sum(CreatureValue);
                // Unblocked: damage that would finish us, but not once it's smaller.
                if (!attack.IsBlocked && attack.Defender == _me && attack.Planeswalker is null && unblocked >= view.Self.Life && unblocked - Math.Min(lost, power) < view.Self.Life)
                    gain += 100;
                Consider(attacker, gain);
            }
            else
            {
                // Our attacker blocked: a blocker it then kills, or that no longer kills it.
                int incoming = blockers.Sum(PowerOf);
                foreach (var blocker in blockers)
                {
                    double gain = DiesFrom(blocker, damage + PowerOf(attacker), shrink, Has(attacker, "Deathtouch")) && !DiesFrom(blocker, PowerOf(attacker), 0, Has(attacker, "Deathtouch"))
                        ? CreatureValue(blocker) : 0;
                    int after = incoming - Math.Min(lost, PowerOf(blocker));
                    bool deathtouch = blockers.Any(b => Has(b, "Deathtouch"));
                    if (DiesFrom(attacker, incoming, 0, deathtouch) && !DiesFrom(attacker, after, 0, deathtouch && after > 0)) gain += CreatureValue(attacker);
                    Consider(blocker, gain);
                }
            }
        }
        return best;
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
        _x = _announcedX.TryGetValue(request.Source, out var announced) ? announced : null;
        try { return Task.FromResult(ChooseTargets(view, request, ability)); }
        finally { _x = null; }
    }

    private IReadOnlyList<Target>? ChooseTargets(GameView view, TargetRequest request, AbilityDefinition? ability)
    {
        var chosen = new List<Target>();
        int specIndex(int i) => Math.Min(i, request.Specs.Count - 1);
        for (int i = 0; ; i++)
        {
            // An "any number" requirement (the last one) takes zero or more targets, within the request's cap (Fireball pays per target).
            bool extra = i >= request.Specs.Count - (request.LastIsAnyNumber ? 1 : 0);
            if (extra && !request.CanAddMore(i)) break;
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
                if (allowed.Count == 0) return request.CanCancel ? null : chosen.Append(request.LegalAt(i)[0]).ToList();
                chosen.Add(Target.None);
                continue;
            }
            Target pick;
            if (ability is not null && TargetIsHarmed(ability, specIndex(i)))
            {
                // What a combat trick was cast for, else the best opposing target, else any opposing one. With only our own
                // things to aim it at, a spell being cast is called off; anything else hits the one we'd miss least (a "you may"
                // effect is then declined).
                var planned = _plannedHarm.Remove(request.Source, out var p) && real.Contains(Target.Of(p)) ? Target.Of(p) : (Target?)null;
                var opposing = real.Where(t => !Ours(view, t))
                    .OrderByDescending(t => t.Card is { } c && view.FindCard(c) is { Zone: Zone.Battlefield } card ? PermanentValue(card) : 0).ToList();
                var harm = planned ?? BestHarmTarget(view, ability, specIndex(i), real) ?? (opposing.Count > 0 ? opposing[0] : null);
                if (harm is null && request.CanCancel) return null;
                pick = harm ?? real.OrderBy(t => t.Card is { } c && view.FindCard(c) is { } own ? PermanentValue(own) : double.MaxValue).First();
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
        if (ability is not null) _lastTargets[request.Source] = (ability, chosen);
        return chosen;
    }

    /// <summary>The creature each combat trick that shrinks was cast for (by the spell's card), for its target.</summary>
    private readonly Dictionary<CardId, CardId> _plannedHarm = new();

    /// <summary>Whether a target is us or something we control.</summary>
    private bool Ours(GameView view, Target t) => t.Player == _me || t.Card is { } c && view.FindCard(c)?.Controller == _me;

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
        int damage = DamageTo(ability, index, _x);
        var creatures = options
            .Where(t => t.Card is { } c && view.FindCard(c) is { } card && card.Controller != _me)
            .Select(t => (Target: t, Card: view.FindCard(t.Card!.Value)!))
            .ToList();
        // In multiplayer, a creature matters more when its controller is the biggest threat to us.
        double Weight(CardView c) => CreatureValue(c) * (view.Players.Count(p => !p.HasLost) > 2 ? 1 + Threat(view, c.Controller) / 30 : 1);
        if (OnlyLethalHarm(ability, index))
        {
            // "Gets -2/-2" (with damage or not) only matters to a creature it kills; in a combat it changes, CombatTrick aims it.
            int shrink = ShrinkOf(ability, index, _x);
            var dies = creatures.Where(x => x.Card.Power is not null && DiesFrom(x.Card, damage, shrink))
                .OrderByDescending(x => Weight(x.Card)).FirstOrDefault();
            return dies.Card is not null ? dies.Target : null;
        }
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
        var best = creatures.OrderByDescending(x => x.Card.Power is null ? PermanentValue(x.Card) : Weight(x.Card)).FirstOrDefault();
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
        // Two blockers that kill it while losing clearly less than it is worth.
        for (int i = 0; i < able.Count; i++)
            for (int j = i + 1; j < able.Count; j++)
            {
                var pair = new[] { able[i], able[j] };
                if (!Kills(attacker, pair)) continue;
                if (MostLost(attacker, pair) + 1 < CreatureValue(attacker)) return false;
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
        // A block tax: only as many blocking creatures as can be paid for, kept in the plan's order (then made legal again).
        if (request.TaxPerBlocker is not null && blocks.Select(b => b.Blocker).Distinct().Count() > request.AffordableBlockers)
        {
            var paid = blocks.Select(b => b.Blocker).Distinct().Take(request.AffordableBlockers).ToHashSet();
            blocks = request.Complete(blocks.Where(b => paid.Contains(b.Blocker)).ToList()).ToList();
        }
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

    /// <summary>
    /// Whether these blockers together destroy the attacker. A first striker kills blockers before they hit back, and it
    /// picks which ones: the blockers kill it only if every way it can spend its first-strike damage leaves enough of them.
    /// </summary>
    private static bool Kills(CardView a, IReadOnlyList<CardView> by)
    {
        if (Has(a, "Indestructible")) return false;
        bool HitBack(IEnumerable<CardView> hitting) =>
            hitting.Any(b => Has(b, "Deathtouch") && (b.Power ?? 0) > 0) || hitting.Sum(b => b.Power ?? 0) >= RemainingToughness(a);
        if (!Has(a, "First strike") && !Has(a, "Double strike")) return HitBack(by);
        var exposed = by.Where(b => !Has(b, "First strike") && !Has(b, "Double strike")).ToList();
        return KillableSets(a, exposed, a.Power ?? 0).All(killed => HitBack(by.Except(killed)));
    }

    /// <summary>Damage the attacker must assign to a blocker to destroy it (none can if it's indestructible).</summary>
    private static int LethalNeed(CardView a, CardView b) =>
        Has(b, "Indestructible") ? int.MaxValue : Has(a, "Deathtouch") && (a.Power ?? 0) > 0 ? 1 : Math.Max(0, RemainingToughness(b));

    /// <summary>Every set of these blockers that <paramref name="damage"/> points of the attacker's damage can destroy (the empty set too).</summary>
    private static IEnumerable<List<CardView>> KillableSets(CardView a, IReadOnlyList<CardView> blockers, int damage)
    {
        var list = blockers.Take(10).ToList();
        for (int mask = 0; mask < 1 << list.Count; mask++)
        {
            var set = list.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            if (set.Sum(b => (long)LethalNeed(a, b)) <= damage) yield return set;
        }
    }

    /// <summary>
    /// What a group of blockers loses: the most valuable set the attacker can destroy with its damage (twice its power
    /// with double strike); nothing if first-striking blockers destroy it before it deals damage.
    /// </summary>
    private static double MostLost(CardView a, IReadOnlyList<CardView> by)
    {
        bool attackerFirst = Has(a, "First strike") || Has(a, "Double strike");
        var firstStrikers = by.Where(b => Has(b, "First strike") || Has(b, "Double strike")).ToList();
        if (!attackerFirst && !Has(a, "Indestructible") && firstStrikers.Count > 0
            && (firstStrikers.Any(b => Has(b, "Deathtouch") && (b.Power ?? 0) > 0) || firstStrikers.Sum(b => b.Power ?? 0) >= RemainingToughness(a)))
            return 0;
        int damage = (a.Power ?? 0) * (Has(a, "Double strike") ? 2 : 1);
        return KillableSets(a, by, damage).Select(set => set.Sum(CreatureValue)).DefaultIfEmpty(0).Max();
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
                        // The attacker spends its damage on what it can destroy; count the most we'd lose.
                        double lost = MostLost(a, pair);
                        if (lost < CreatureValue(a) && lost < bestCost) { bestCost = lost; bestPair = (able[i], able[j]); }
                    }
                if (bestPair is { } p2) Assign(a, new[] { p2.Item1, p2.Item2 });
            }
        }
        return assigned;
    }

    /// <summary>
    /// Says yes (the commander goes back to the command zone, optional effects are done), except to a "you may" effect
    /// that would hurt our own permanent or us: one we had to target because nothing else was legal; and to an optional
    /// sacrifice that isn't worth its payoff (see <see cref="PaymentWorthIt"/>).
    /// </summary>
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => request.Suggested is { } suggested ? Task.FromResult(suggested) :
        // Asked only when the full cost can be paid too: a card with morph is cast face up then (face down only when that's all it can afford),
        // and a card with evoke is cast to stay rather than evoked.
        Task.FromResult(!EndsALoop(view, request) && !request.Prompt.Contains(" face down as a 2/2", StringComparison.Ordinal)
                        && !request.Prompt.Contains(" for its evoke cost ", StringComparison.Ordinal)
                        && !(request.Card is { } source && HurtsOwnTarget(view, source, request.Prompt))
                        && !(request.Card is { } payer && FindMayPay(view, payer, request.Prompt) is { Options: null } pay && !PaymentWorthIt(view, payer, pay, pay.Extra)));

    /// <summary>How many times this turn the same "you may" of the same source was asked.</summary>
    private readonly Dictionary<(CardId? Source, string Prompt), (int Turn, int Count)> _askedThisTurn = new();

    /// <summary>The most times in one turn the bot says yes to the same optional action of the same source.</summary>
    private const int LoopLimit = 40;

    /// <summary>
    /// A loop that only goes on while this player keeps taking an optional action (rule 732.2a: that player chooses how many times):
    /// past <see cref="LoopLimit"/> repetitions in one turn the bot stops it.
    /// </summary>
    private bool EndsALoop(GameView view, YesNoRequest request)
    {
        var key = (request.Card, request.Prompt);
        var (turn, count) = _askedThisTurn.GetValueOrDefault(key);
        count = turn == view.TurnNumber ? count + 1 : 1;
        _askedThisTurn[key] = (view.TurnNumber, count);
        return count > LoopLimit;
    }

    /// <summary>The targets last chosen for each source's ability, to judge its "you may" choice as it resolves.</summary>
    private readonly Dictionary<CardId, (AbilityDefinition Ability, IReadOnlyList<Target> Targets)> _lastTargets = new();

    private bool HurtsOwnTarget(GameView view, CardId source, string prompt)
    {
        if (!_lastTargets.TryGetValue(source, out var last)) return false;
        var may = MayEffects(last.Ability.Effects).FirstOrDefault(m => m.Prompt == prompt);
        if (may is null) return false;
        for (int i = 0; i < last.Targets.Count; i++)
        {
            var t = last.Targets[i];
            bool ours = t.Player == _me || (t.Card is { } c && view.FindCard(c) is { } card && card.Controller == _me);
            if (ours && Unwrapped(may.Effects).Any(e => RefersToTarget(e, i) && IsHarmful(e))) return true;
        }
        return false;
    }

    private static IEnumerable<MayDo> MayEffects(IEnumerable<Effect> effects)
    {
        foreach (var effect in effects)
        {
            if (effect is MayDo m) { yield return m; foreach (var inner in MayEffects(m.Effects)) yield return inner; }
            else if (effect is IfThen i) foreach (var inner in MayEffects(i.Else is { } e ? i.Then.Concat(e) : i.Then)) yield return inner;
        }
    }

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Task.FromResult(request.Suggested);

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        Task.FromResult<IReadOnlyList<ManaTap>?>(request.SuggestedTaps);
}
