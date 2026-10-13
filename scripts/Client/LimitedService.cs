// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots.Limited;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Godot;

namespace Arcanum.Client;

/// <summary>Something limited events can be played with: a set with boosters, or a cube list.</summary>
public sealed record LimitedSource(string Id, string Name, bool IsCube, int BoostersPerPlayer, int SealedBoosters);

/// <summary>
/// Runs the current draft or sealed event for the client: creates it, makes the computer players pick and build
/// decks, pairs the rounds, plays out the matches between computer players and saves everything under
/// user://limited so the event can be resumed.
/// </summary>
public sealed class LimitedService : ILimitedSession
{
    private readonly string _savePath, _returnScene;
    private readonly string? _table;

    /// <param name="savePath">Where the event is saved (the career keeps its events apart from the Play screen's).</param>
    /// <param name="returnScene">Where a game of the event goes back to.</param>
    /// <param name="table">A table picture for the event's games (see <see cref="MatchSetup.Table"/>).</param>
    /// <param name="ratingsForPlayer">Whether the player may use the set's draft statistics (shown as grades, and in "Build for me").</param>
    public LimitedService(string savePath = "user://limited/current.json", string returnScene = App.LimitedScene, string? table = null, bool ratingsForPlayer = false)
    {
        _savePath = savePath;
        _returnScene = returnScene;
        _table = table;
        RatingsForPlayer = ratingsForPlayer;
    }

    /// <summary>Whether the player may use the set's draft statistics; the computer players always do.</summary>
    public bool RatingsForPlayer { get; }

    /// <summary>The draft statistics of the current event's set, when the module has them.</summary>
    public DraftRatings? Ratings => RatingsFor(Current?.Source);

    private static readonly Dictionary<string, DraftRatings?> RatingsCache = new();

    /// <summary>The draft statistics of an event source's set (a cube has none), read once.</summary>
    public static DraftRatings? RatingsFor(string? source)
    {
        if (source is null || !source.StartsWith("set:") || App.Instance.Module is not { } module) return null;
        string set = source[4..];
        if (!RatingsCache.TryGetValue(set, out var ratings)) RatingsCache[set] = ratings = module.LoadDraftRatings(set);
        return ratings;
    }

    private string SavePath => ProjectSettings.GlobalizePath(_savePath);
    private static string UserCubes => ProjectSettings.GlobalizePath("user://cubes");

    private DraftSession? _draft;
    private Random _random = new();

    public LimitedEvent? Current { get; private set; }

    private static CardDatabase Cards => App.Instance.Cards!;

    /// <summary>The draft in progress (restored from the saved event when needed).</summary>
    public DraftSession? Draft
    {
        get
        {
            if (Current is not { Stage: EventStage.Drafting, Draft: { } snapshot }) return null;
            return _draft ??= DraftSession.Restore(snapshot, SourceFor(Current, opened: snapshot), _random);
        }
    }

    // ---------------------------------------------------------------- the screen's view of the event

    public int Seat => Current?.HumanSeat ?? 0;

    public DraftView? DraftState => Draft is { } d && Current is { } ev
        ? new DraftView(d.Round, d.Rounds, d.PickInRound, d.PassesLeft, d.PackFor(ev.HumanSeat), d.Picks[ev.HumanSeat], false)
        : null;

    public bool IsOnline => false;
    public int SecondsLeft => -1;
    public string? Status => null;
    public bool GameInProgress => false;
    public bool WaitingForOpponent => false;

    public event Action? Changed { add { } remove { } }

    public void Pick(int index) => HumanPick(index);

    public void DeckEdited() => Save();

    public void FinishBuilding() => StartPlaying();

    public void PlayNext()
    {
        App.Instance.PendingMatch = NextGame();
        App.Instance.GoTo(App.GameBoardScene);
    }

    public void ReturnToGame() { }

    // ---------------------------------------------------------------- sources

    /// <summary>Sets of the module whose boosters can be opened with the card data at hand, then cubes.</summary>
    public List<LimitedSource> Sources()
    {
        var list = new List<LimitedSource>();
        if (App.Instance.Module is not { } module || App.Instance.Cards is null) return list;
        foreach (var set in module.LoadSets().Where(s => s.Booster is not null))
        {
            var generator = new BoosterGenerator(set, Cards);
            if (generator.EmptySheets.Any()) continue; // the card data doesn't know this set's printings
            list.Add(new LimitedSource("set:" + set.Code, generator.Name, false, set.BoostersPerPlayer, set.SealedBoosters));
        }
        foreach (var name in module.CubeNames()) list.Add(new LimitedSource("cube:module:" + name, $"Cube: {Pretty(name)}", true, 3, 6));
        if (Directory.Exists(UserCubes))
            foreach (var file in Directory.EnumerateFiles(UserCubes, "*.txt").Order())
                list.Add(new LimitedSource("cube:user:" + System.IO.Path.GetFileNameWithoutExtension(file), $"Cube: {Pretty(System.IO.Path.GetFileNameWithoutExtension(file))}", true, 3, 6));
        return list;
    }

    /// <summary>Saves a pasted cube list under user://cubes.</summary>
    public string SaveUserCube(string name, string list)
    {
        Directory.CreateDirectory(UserCubes);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')).Trim();
        if (safe.Length == 0) safe = "cube";
        File.WriteAllText(System.IO.Path.Combine(UserCubes, safe + ".txt"), list);
        return "cube:user:" + safe;
    }

    /// <summary>A source's name to show, without building its boosters as <see cref="Sources"/> does.</summary>
    public static string SourceName(string sourceId)
    {
        if (sourceId.StartsWith("set:") && App.Instance.Module?.LoadSets().FirstOrDefault(s => "set:" + s.Code == sourceId) is { } set) return set.Name;
        var parts = sourceId.Split(':', 3);
        return parts.Length == 3 && parts[0] == "cube" ? $"Cube: {Pretty(parts[2])}" : sourceId;
    }

    private static string Pretty(string name) => string.Join(' ', name.Split('-', '_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));

    private static List<PoolCard> CubeCards(string sourceId, List<string>? unknown = null)
    {
        var parts = sourceId.Split(':', 3);
        string text = parts[1] == "module"
            ? App.Instance.Module!.ReadCube(parts[2])
            : File.ReadAllText(System.IO.Path.Combine(UserCubes, parts[2] + ".txt"));
        return CubeBoosters.CardsFrom(DeckList.Parse(text), Cards, unknown);
    }

    /// <summary>
    /// Starts an event made elsewhere (the career's calendar): a draft begins drafting, sealed opens the pools, and
    /// seats that all have their decks already (a constructed event) go straight to the first round.
    /// </summary>
    public void Begin(LimitedEvent ev, Random random)
    {
        _random = random;
        Current = ev;
        _draft = null;
        if (ev.Seats.All(s => s.Deck is not null)) StartPlaying();
        else if (ev.Mode == LimitedMode.Draft)
        {
            ev.Stage = EventStage.Drafting;
            _draft = new DraftSession(SourceFor(ev), ev.Seats.Count, ev.BoostersPerPlayer, _random);
            BotPicks();
        }
        else
        {
            var boosters = SourceFor(ev);
            foreach (var seat in ev.Seats)
                for (int i = 0; i < ev.BoostersPerPlayer; i++) seat.Pool.AddRange(boosters.Open(_random));
            FinishPools();
        }
        Save();
    }

    /// <summary>Opens boosters for an event; a cube leaves out the cards already opened (<paramref name="opened"/>).</summary>
    private IBoosterSource SourceFor(LimitedEvent ev, DraftSnapshot? opened = null) => BoosterSource(ev, opened, _random);

    /// <summary>Opens boosters for an event; a cube leaves out the cards already opened (<paramref name="opened"/>).</summary>
    public static IBoosterSource BoosterSource(LimitedEvent ev, DraftSnapshot? opened, Random random)
    {
        if (ev.Source.StartsWith("set:"))
        {
            var set = App.Instance.Module!.LoadSets().First(s => s.Code == ev.Source[4..]);
            return new BoosterGenerator(set, Cards);
        }
        var remaining = ev.CubeCards.ToList();
        if (opened is not null)
            foreach (var card in opened.Packs.Concat(opened.Picks).SelectMany(c => c))
                remaining.Remove(card);
        return new CubeBoosters(ev.Source, remaining, ev.CubeBoosterSize, random);
    }

    // ---------------------------------------------------------------- event lifecycle

    public void Load()
    {
        if (Current is not null || !File.Exists(SavePath)) return;
        try
        {
            Current = LimitedEvent.FromJson(File.ReadAllText(SavePath));
            _random = new Random(Current.Seed + Current.Rounds.Count * 7919 + (Current.Draft?.PickInRound ?? 0));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            GD.PushWarning($"Could not read the saved event: {e.Message}");
        }
    }

    public void Save()
    {
        if (Current is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SavePath)!);
        if (_draft is not null && Current.Stage == EventStage.Drafting) Current.Draft = _draft.Snapshot();
        File.WriteAllText(SavePath, Current.ToJson());
    }

    public void Abandon()
    {
        Current = null;
        _draft = null;
        if (File.Exists(SavePath)) File.Delete(SavePath);
    }

    /// <summary>A new event (not started) for these seats; null with <paramref name="error"/> if it can't be made.</summary>
    public static LimitedEvent? NewEvent(LimitedMode mode, LimitedSource source, IReadOnlyList<EventSeat> seats, int bestOf, int seed, out string? error)
    {
        error = null;
        var cube = new List<PoolCard>();
        if (source.IsCube)
        {
            var unknown = new List<string>();
            cube = CubeCards(source.Id, unknown);
            int needed = (mode == LimitedMode.Draft ? seats.Count * source.BoostersPerPlayer : seats.Count * source.SealedBoosters) * 15;
            if (cube.Count < needed)
            {
                error = $"This cube has {cube.Count} cards; {(mode == LimitedMode.Draft ? "a draft" : "sealed")} for {seats.Count} needs {needed}.";
                return null;
            }
            if (unknown.Count > 0) GD.PushWarning($"Cube: unknown cards {string.Join(", ", unknown.Take(10))}");
        }
        return new LimitedEvent
        {
            Mode = mode, Source = source.Id, CubeCards = cube, BestOf = bestOf, Seed = seed,
            BoostersPerPlayer = mode == LimitedMode.Draft ? source.BoostersPerPlayer : source.SealedBoosters,
            RoundsTotal = Math.Min(3, seats.Count - 1),
            Seats = seats.ToList(),
        };
    }

    /// <summary>Starts a new event; returns an error message if it can't.</summary>
    public string? Start(LimitedMode mode, LimitedSource source, int seats, int bestOf, string playerName)
    {
        int seed = System.Environment.TickCount;
        _random = new Random(seed);
        var ev = NewEvent(mode, source,
            Enumerable.Range(0, seats).Select(i => new EventSeat { Name = i == 0 ? playerName : $"Computer {i}", IsHuman = i == 0 }).ToList(),
            bestOf, seed, out var error);
        if (ev is null) return error;
        Current = ev;
        _draft = null;
        var boosters = SourceFor(ev);
        if (mode == LimitedMode.Draft)
        {
            ev.Stage = EventStage.Drafting;
            _draft = new DraftSession(boosters, seats, ev.BoostersPerPlayer, _random);
            BotPicks();
        }
        else
        {
            foreach (var seat in ev.Seats)
                for (int i = 0; i < ev.BoostersPerPlayer; i++) seat.Pool.AddRange(boosters.Open(_random));
            FinishPools();
        }
        Save();
        return null;
    }

    public static DraftOption Option(PoolCard card) => Option(card, null);

    /// <summary>A card as the computer players judge it, with what the set's statistics say of it.</summary>
    public static DraftOption Option(PoolCard card, DraftRatings? ratings)
    {
        var definition = DeckList.TryResolve(Cards, card.ToEntry(), out var d) ? d : Cards.Find(card.Name)!.Definition;
        return ratings?.Score(card.Name) is var (score, confidence)
            ? new DraftOption(definition, card.Rarity, score, confidence)
            : new DraftOption(definition, card.Rarity);
    }

    private List<DraftOption> Options(IEnumerable<PoolCard> cards)
    {
        var ratings = Ratings;
        return cards.Select(c => Option(c, ratings)).ToList();
    }

    /// <summary>The person takes a card; the computer players pick at the same time, then the boosters move on.</summary>
    public void HumanPick(int index)
    {
        var draft = Draft ?? throw new InvalidOperationException("No draft in progress.");
        int human = Current!.HumanSeat;
        draft.Pick(human, index);
        if (draft.IsComplete) FinishPools();
        else BotPicks();
        Save();
    }

    /// <summary>The card a computer player would take from the person's booster (automatic play).</summary>
    public int AutoPickIndex()
    {
        var draft = Draft!;
        int human = Current!.HumanSeat;
        return DraftPicker.Pick(Options(draft.PackFor(human)), Options(draft.Picks[human]), _random, EventSeat.DefaultSkill);
    }

    private void BotPicks()
    {
        var draft = _draft!;
        for (int seat = 0; seat < draft.Seats; seat++)
        {
            if (Current!.Seats[seat].IsHuman || draft.HasPicked(seat)) continue;
            draft.Pick(seat, DraftPicker.Pick(Options(draft.PackFor(seat)), Options(draft.Picks[seat]), _random, Current.Seats[seat].Skill));
            if (draft.IsComplete) { FinishPools(); return; }
        }
    }

    /// <summary>Pools are complete: the computer players build their decks.</summary>
    private void FinishPools()
    {
        var ev = Current!;
        if (_draft is not null)
        {
            for (int s = 0; s < ev.Seats.Count; s++)
            {
                ev.Seats[s].Pool.Clear();
                ev.Seats[s].Pool.AddRange(_draft.Picks[s]);
            }
            _draft = null;
            ev.Draft = null;
        }
        foreach (var seat in ev.Seats.Where(s => !s.IsHuman)) seat.Deck = AutoBuild(seat.Pool, ev.Source, seat.Skill);
        ev.Stage = EventStage.Building;
    }

    // ---------------------------------------------------------------- decks

    /// <summary>The limited format of the module (or a built-in one).</summary>
    public static FormatRules Format => App.Instance.Formats.FirstOrDefault(f => f.Limited)
                                        ?? new FormatRules { Id = "limited", Name = "Limited", MinDeckSize = 40, MaxCopies = 99, Limited = true };

    /// <summary>The player's deck built from their pool ("Build for me"): with the set's statistics only when the player may use them.</summary>
    public DeckList AutoBuild(IReadOnlyList<PoolCard> pool) => AutoBuild(pool, Current?.Source, RatingsForPlayer ? 1 : 0);

    /// <summary>
    /// A 40-card deck built from a pool by a builder of this skill (see the draft bots), with basic lands of the event's
    /// set (<paramref name="source"/>).
    /// </summary>
    public static DeckList AutoBuild(IReadOnlyList<PoolCard> pool, string? source, double skill = EventSeat.DefaultSkill)
    {
        var ratings = RatingsFor(source);
        var choice = LimitedDeckBuilder.Build(pool.Select(c => Option(c, ratings)).ToList(), skill);
        var deck = new DeckList();
        foreach (var index in choice.PoolCards)
        {
            var entry = pool[index].ToEntry();
            DeckList.Adjust(deck.Main, entry.Name, 1, entry.Set, entry.Number);
        }
        foreach (var (color, count) in choice.BasicLands.Where(kv => kv.Value > 0))
        {
            var basic = BasicLand(color, source);
            DeckList.Adjust(deck.Main, basic.Name, count, basic.Set, basic.Number);
        }
        foreach (var (card, index) in pool.Select((c, i) => (c, i)).Where(x => !choice.PoolCards.Contains(x.i)))
        {
            var entry = card.ToEntry();
            DeckList.Adjust(deck.Sideboard, entry.Name, 1, entry.Set, entry.Number);
        }
        return deck;
    }

    private static readonly Dictionary<ManaType, string> BasicNames = new()
    {
        [ManaType.White] = "Plains", [ManaType.Blue] = "Island", [ManaType.Black] = "Swamp", [ManaType.Red] = "Mountain", [ManaType.Green] = "Forest",
    };

    public static IReadOnlyDictionary<ManaType, string> Basics => BasicNames;

    /// <summary>A basic land for the deck, in the event set's printing when the set has one.</summary>
    public DeckEntry BasicLand(ManaType color) => BasicLand(color, Current?.Source);

    public static DeckEntry BasicLand(ManaType color, string? source)
    {
        var name = BasicNames[color];
        if (source is not null && source.StartsWith("set:") && Cards.Find(name)?.Record.Printings
                .Where(p => p.Set.Equals(source[4..], StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.CollectorNumber, Comparer<string>.Create(OracleJsonl.CompareNumbers)).FirstOrDefault() is { } printing)
            return new DeckEntry(1, name, printing.Set.ToUpperInvariant(), printing.CollectorNumber);
        return new DeckEntry(1, name);
    }

    public List<DeckIssue> Validate(EventSeat seat) => ValidateSeat(seat);

    public static List<DeckIssue> ValidateSeat(EventSeat seat) =>
        seat.Deck is null ? new List<DeckIssue> { new(IssueSeverity.Error, "No deck yet.") }
            : DeckValidator.ValidateLimited(seat.Deck, Format, seat.Pool, Cards);

    // ---------------------------------------------------------------- rounds

    /// <summary>The person's deck is ready: the first round is paired.</summary>
    public void StartPlaying()
    {
        var ev = Current!;
        ev.Stage = EventStage.Playing;
        NextRound();
        Save();
    }

    private void NextRound()
    {
        var ev = Current!;
        if (ev.Rounds.Count >= ev.RoundsTotal)
        {
            ev.Stage = EventStage.Finished;
            return;
        }
        var round = ev.PairNextRound(_random);
        // Matches between computer players are played out from their decks' strength.
        foreach (var match in round.Where(m => m.SeatB is not null && !m.Involves(ev.HumanSeat)))
            while (!match.Done)
            {
                double a = DeckStrength(ev.Seats[match.SeatA]), b = DeckStrength(ev.Seats[match.SeatB!.Value]);
                ev.RecordGame(match, _random.NextDouble() < a / (a + b) ? match.SeatA : match.SeatB);
            }
        // The person got a bye: on to the next round.
        if (ev.CurrentMatch is null) NextRound();
    }

    private double DeckStrength(EventSeat seat) => DeckStrength(seat.Deck);

    /// <summary>How strong a deck is (to settle matches between computer players without playing them).</summary>
    public static double DeckStrength(DeckList? deck)
    {
        var spells = (deck?.Main ?? new List<DeckEntry>()).Where(e => Cards.Find(e.Name) is { } c && !c.Definition.Is(CardType.Land));
        double total = spells.Sum(e => CardRating.Rate(Cards.Find(e.Name)!.Definition) * e.Count);
        return Math.Max(1, total);
    }

    /// <summary>The next game of the person's current match, ready for the game board.</summary>
    public MatchSetup? NextGame()
    {
        var ev = Current;
        if (ev?.CurrentMatch is not { } match) return null;
        int human = ev.HumanSeat, opponent = match.Opponent(human)!.Value;
        var seats = new[] { human, opponent }.Select(s =>
        {
            var (deck, unknown) = ev.Seats[s].Deck!.Resolve(Cards);
            if (unknown.Count > 0) GD.PushWarning($"{ev.Seats[s].Name}'s deck: unknown cards {string.Join(", ", unknown)}");
            return new GameSession.Seat(ev.Seats[s].Name, deck, IsBot: !ev.Seats[s].IsHuman);
        }).ToList();
        int game = match.WinsA + match.WinsB + match.Draws + 1;
        return new MatchSetup(seats, 20)
        {
            Event = new EventHook(winner => RecordGame(match, winner is null ? null : winner == 0 ? human : opponent),
                _returnScene, $"Round {ev.Rounds.Count} · game {game}"),
            Table = _table,
        };
    }

    private void RecordGame(EventMatch match, int? winnerSeat)
    {
        var ev = Current!;
        ev.RecordGame(match, winnerSeat);
        if (match.Done) NextRound();
        Save();
    }
}
