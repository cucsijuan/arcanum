// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;
using Arcanum.Data.Modules;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// The career on this device: its world (the content module's career settings), the saved career (journal and state
/// in user://career/career.sav) and the career's own decks (user://career/decks, built from the collection).
/// Every change goes through <see cref="Do"/>, which checks it with <see cref="CareerRules"/> and saves.
/// </summary>
public sealed class CareerService
{
    /// <summary>Where the career lives: user://career, or ARCANUM_CAREER_DIR (test runs that must not touch the player's career).</summary>
    private static string Folder => OS.GetEnvironment("ARCANUM_CAREER_DIR") is { Length: > 0 } dir ? dir : "user://career";

    private static string SavePath => ProjectSettings.GlobalizePath(Folder + "/career.sav");

    public const string TableImage = "res://ui-art/kit/career/table.jpg";

    /// <summary>The store's tables (the art that's there): a career match is played at one of them, an event at the day's.</summary>
    private static readonly string[] Tables = { TableImage, "res://ui-art/kit/career/table2.jpg" };

    /// <summary>Testing: ARCANUM_CAREER_TABLE=&lt;n&gt; plays every match at the store's n-th table (from 1).</summary>
    private static string? TestTable => int.TryParse(OS.GetEnvironment("ARCANUM_CAREER_TABLE"), out int n) && n >= 1 && n <= Tables.Length ? Tables[n - 1] : null;

    public static string? AnyTable(Random random) => TestTable ?? (Tables.Where(t => ResourceLoader.Exists(t)).ToList() is { Count: > 0 } tables ? tables[random.Next(tables.Count)] : null);

    /// <summary>The table the store's events are played at on a day.</summary>
    public static string? TableFor(DateOnly day) => TestTable ?? (Tables.Where(t => ResourceLoader.Exists(t)).ToList() is { Count: > 0 } tables ? tables[day.DayNumber % tables.Count] : null);
    public const string StoreImage = "res://ui-art/kit/career/store.jpg";

    private CareerSave? _save;
    private string _contentId = "";
    private ContentModule? _module;
    private CardDatabase? _cards;
    private TimeSpan _clockOffset;

    public CareerWorld? World { get; private set; }

    /// <summary>A new season arrived: the calendar and prices may have changed.</summary>
    public event Action? SeasonChanged;

    /// <summary>The career was replaced by the cloud's, or the cloud's state changed (<see cref="CloudStatus"/>).</summary>
    public event Action? CloudChanged;

    /// <summary>The career's decks: only cards of the collection, no starter decks.</summary>
    public DeckStore Decks { get; } = new(Folder + "/decks", starters: false);

    /// <summary>Why the content has no career, or why the saved career couldn't be read.</summary>
    public string? Problem { get; private set; }

    public bool HasCareer => _save is not null;

    public CareerState State => _save?.State ?? throw new InvalidOperationException("No career.");

    public void Init(ContentModule module, CardDatabase cards)
    {
        var errors = new List<string>();
        var content = module.LoadCareer(errors);
        foreach (var error in errors) GD.PushWarning($"Career: {error}");
        if (content is null)
        {
            Problem = errors.Count > 0 ? "The content's career settings couldn't be read." : "The installed content has no career.";
            return;
        }
        _module = module;
        _cards = cards;
        var seasonErrors = new List<string>();
        MakeWorld(content, ReadSeason() ?? module.LoadSeason(seasonErrors));
        foreach (var error in seasonErrors) GD.PushWarning($"Career: {error}");
        _contentId = $"{module.Manifest.Id} {module.Manifest.Version}";
        Load();
        UpdateSeason();
    }

    private void MakeWorld(CareerContent content, CareerSeason? season)
    {
        var module = _module!;
        var deckNames = module.DeckNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        World = new CareerWorld(content, _cards!, module.LoadSets(), name => deckNames.Contains(name) ? module.ReadDeck(name) : null, module.LoadFormats(), season);
    }

    // ---------------------------------------------------------------- the season and the clock

    /// <summary>The season last downloaded for this content (the module's own is used until there is one).</summary>
    private string SeasonPath => ProjectSettings.GlobalizePath($"{Folder}/season.{_module!.Manifest.Id}.json");

    private CareerSeason? ReadSeason()
    {
        try { return File.Exists(SeasonPath) ? CareerSeason.Parse(File.ReadAllText(SeasonPath)) : null; }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException) { return null; }
    }

    /// <summary>
    /// Downloads the current season from the career service when it changed (and learns the service's clock from the
    /// answer); offline, the last one downloaded stays. ARCANUM_CAREER_OFFLINE=1 skips it (test runs).
    /// </summary>
    private async void UpdateSeason()
    {
        if (OS.GetEnvironment("ARCANUM_CAREER_OFFLINE") == "1") return;
        try
        {
            using var http = new System.Net.Http.HttpClient { BaseAddress = Account.ServiceUrl, Timeout = TimeSpan.FromSeconds(15) };
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"career/season/{_module!.Manifest.Id}.json");
            if (File.Exists(SeasonPath)) request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(SeasonPath);
            using var response = await http.SendAsync(request);
            if (response.Headers.Date is { } serverNow) _clockOffset = serverNow.UtcDateTime - DateTime.UtcNow;
            if (response.StatusCode != System.Net.HttpStatusCode.OK) return; // not changed, or no season for this content
            var text = await response.Content.ReadAsStringAsync();
            var season = CareerSeason.Parse(text); // a newer layout than this game understands throws: keep the old one
            File.WriteAllText(SeasonPath, text);
            if (response.Content.Headers.LastModified is { } modified) File.SetLastWriteTimeUtc(SeasonPath, modified.UtcDateTime);
            if (_module!.LoadCareer() is { } content)
            {
                MakeWorld(content, season);
                SeasonChanged?.Invoke();
            }
        }
        catch (Exception e) { GD.Print($"Career: the season couldn't be updated ({e.GetBaseException().Message})."); }
    }

    private void Load()
    {
        if (!File.Exists(SavePath)) return;
        try
        {
            _save = CareerSave.Read(File.ReadAllBytes(SavePath), CareerSave.DefaultKey);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Problem = e is InvalidDataException ? e.Message : "The saved career couldn't be read.";
            GD.PushWarning($"Career: {e.Message}");
        }
    }

    /// <summary>
    /// Now, for the career: the career service's clock when it was reached (the device's otherwise), but never before
    /// the latest time the career has seen.
    /// </summary>
    public DateTime Now
    {
        get
        {
            var now = DateTime.UtcNow + _clockOffset;
            return _save is { } save && save.State.LastUtc > now ? save.State.LastUtc : now;
        }
    }

    /// <summary>Checks and applies an action, adds it to the journal and saves; throws <see cref="CareerRuleException"/> when it isn't allowed.</summary>
    public CareerResult Do(CareerAction action)
    {
        var world = World ?? throw new CareerRuleException("The installed content has no career.");
        var save = _save ?? (action is StartCareer ? new CareerSave() : throw new CareerRuleException("No career."));
        var result = CareerRules.Apply(save.State, action, world);
        save.Journal.Add(action);
        save.Content = _contentId;
        _save = save;
        Write();
        SyncSoon();
        return result;
    }

    /// <summary>Starts a new career with a starting deck, which also becomes the career's first deck.</summary>
    public void Start(string deck, string deckTitle)
    {
        Do(new StartCareer(Now, (ulong)Random.Shared.NextInt64(1, long.MaxValue), deck));
        Decks.Save(deckTitle, "casual", World!.Deck(deck));
    }

    /// <summary>Ends the career: the saved file is kept next to it as a backup, and the career's decks are removed.</summary>
    public void Abandon()
    {
        if (File.Exists(SavePath)) File.Move(SavePath, SavePath + ".old", overwrite: true);
        foreach (var deck in Decks.List(null)) Decks.Delete(deck);
        File.WriteAllText(EndedPath, DateTime.UtcNow.ToString("O")); // the cloud's copy ends too
        _save = null;
        Problem = null;
        SyncSoon();
    }

    // ---------------------------------------------------------------- the cloud

    /// <summary>The career's file in the cloud (ARCANUM_CAREER_CLOUD changes it, for test runs that must not touch the player's).</summary>
    private static string CloudFile => OS.GetEnvironment("ARCANUM_CAREER_CLOUD") is { Length: > 0 } name ? name : "career.zip";
    private string FolderPath => ProjectSettings.GlobalizePath(Folder);
    private string EndedPath => Path.Combine(FolderPath, "ended");

    /// <summary>Where the career stands with the cloud, for the player.</summary>
    public string CloudStatus { get; private set; } = "";

    /// <summary>
    /// Asked when the career here and the cloud's went on separately: true keeps this device's, false takes the cloud's,
    /// null decides later. The screens set it.
    /// </summary>
    public Func<CareerSave, CareerSave, Task<bool?>>? AskConflict { get; set; }

    private bool _syncing, _syncAgain;
    private int _syncSoon;

    /// <summary>Sends the career to the cloud a few seconds after it changes (changes close together go once).</summary>
    public async void SyncSoon()
    {
        int mine = ++_syncSoon;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (mine == _syncSoon) await SyncAsync();
    }

    /// <summary>
    /// Brings the career here and the cloud's together: the one ahead replaces the other (the replaced files are kept
    /// as a backup next to the career), and when both went on the player chooses. Without a Discord sign-in (or with
    /// ARCANUM_CAREER_OFFLINE=1) the career stays on this device only.
    /// </summary>
    public async Task SyncAsync()
    {
        if (_syncing) { _syncAgain = true; return; }
        if (OS.GetEnvironment("ARCANUM_CAREER_OFFLINE") == "1" || World is null) return;
        _syncing = true;
        try
        {
            do
            {
                _syncAgain = false;
                await SyncOnce();
            } while (_syncAgain);
        }
        catch (Exception e)
        {
            SetCloud($"Not saved to the cloud: {e.GetBaseException().Message}");
            GD.Print($"Career: cloud sync failed: {e}");
        }
        finally { _syncing = false; }
    }

    private async Task SyncOnce()
    {
        if (await App.Instance.Online.CloudAsync() is not { } cloud)
        {
            SetCloud(Account.HasDiscord ? $"Not saved to the cloud. {App.Instance.Online.InternetStatus}" : "Kept on this computer only: sign in with Discord to keep it in the cloud.");
            return;
        }
        var remoteBytes = await cloud.ReadAsync(CloudFile);
        var remote = remoteBytes is null ? null : CareerBundle.Unpack(remoteBytes);
        var local = LocalBundle();
        var key = CareerSave.DefaultKey;
        switch (CareerCloud.Compare(local, remote, key))
        {
            case CloudSync.Same:
                break;
            case CloudSync.Upload:
                await cloud.WriteAsync(CloudFile, local!.Pack());
                break;
            case CloudSync.Download:
                TakeBundle(remote!, local);
                break;
            case CloudSync.Conflict:
                bool? keepMine = AskConflict is { } ask ? await ask(CareerCloud.Read(local!, key)!, CareerCloud.Read(remote!, key)!) : null;
                if (keepMine is null)
                {
                    SetCloud("Not saved to the cloud: this computer's career and the cloud's went on separately.");
                    return;
                }
                if (keepMine == true)
                {
                    Backup(remote!, "cloud");
                    await cloud.WriteAsync(CloudFile, local!.Pack());
                }
                else TakeBundle(remote!, local);
                break;
        }
        SetCloud($"Saved to the cloud ({DateTime.Now:HH:mm}).");
    }

    private void SetCloud(string status)
    {
        CloudStatus = status;
        CloudChanged?.Invoke();
    }

    /// <summary>The career's files here (null: there has never been a career on this device).</summary>
    private CareerBundle? LocalBundle()
    {
        var bundle = new CareerBundle();
        var times = new List<DateTime>();
        void Add(string path, string name)
        {
            if (!File.Exists(path)) return;
            bundle.Files[name] = File.ReadAllBytes(path);
            times.Add(File.GetLastWriteTimeUtc(path));
        }
        Add(SavePath, CareerBundle.SaveName);
        Add(Path.Combine(FolderPath, "event.json"), "event.json");
        var decks = Path.Combine(FolderPath, "decks");
        if (Directory.Exists(decks))
            foreach (var file in Directory.GetFiles(decks)) Add(file, "decks/" + Path.GetFileName(file));
        if (bundle.Save is null && File.Exists(EndedPath)) times.Add(File.GetLastWriteTimeUtc(EndedPath));
        if (times.Count == 0) return null;
        bundle.SavedUtc = times.Max();
        return bundle;
    }

    /// <summary>Replaces the career here with the cloud's (this device's files are kept in a backup zip).</summary>
    private void TakeBundle(CareerBundle remote, CareerBundle? local)
    {
        if (local is not null) Backup(local, "computer");
        Directory.CreateDirectory(FolderPath);
        if (File.Exists(SavePath)) File.Delete(SavePath);
        var decks = Path.Combine(FolderPath, "decks");
        if (Directory.Exists(decks)) foreach (var file in Directory.GetFiles(decks)) File.Delete(file);
        File.Delete(Path.Combine(FolderPath, "event.json"));
        foreach (var (name, data) in remote.Files)
        {
            var path = Path.Combine(FolderPath, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, remote.SavedUtc);
        }
        if (remote.Save is null)
        {
            File.WriteAllText(EndedPath, remote.SavedUtc.ToString("O"));
            File.SetLastWriteTimeUtc(EndedPath, remote.SavedUtc);
        }
        else if (File.Exists(EndedPath)) File.Delete(EndedPath);
        _save = null;
        _events = null;
        Problem = null;
        Load();
        CloudChanged?.Invoke();
    }

    private void Backup(CareerBundle bundle, string from)
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllBytes(Path.Combine(FolderPath, $"backup-{from}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"), bundle.Pack());
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
        var temp = SavePath + ".tmp";
        File.WriteAllBytes(temp, _save!.Write(CareerSave.DefaultKey));
        File.Move(temp, SavePath, overwrite: true);
    }

    /// <summary>A deck's cards the collection doesn't have enough of.</summary>
    public List<MissingCard> Missing(DeckList deck) => CareerRules.Missing(deck, State, World!);

    /// <summary>
    /// The board for the match just started (daily, practice or ante), on the store's table. In an ante match each
    /// anted card is out of its deck for the game.
    /// </summary>
    public MatchSetup Match(DeckList deck, string playerName)
    {
        var world = World!;
        var pending = State.Pending ?? throw new InvalidOperationException("No match started.");
        var opponent = world.Content.Opponents.First(o => o.Id == pending.OpponentId);
        var cards = App.Instance.Cards!;
        var (mine, _) = deck.Resolve(cards);
        var theirList = CareerRules.OpponentDeck(State, world, pending.OpponentId, pending.Deck);
        var (theirs, _) = theirList.Resolve(cards);
        if (pending.Kind == CareerRules.Ante)
        {
            // The player's stake is out of the collection now: out of the deck too when the deck needed that copy.
            var staked = CardKey.Parse(pending.PlayerAnte).Name;
            if (State.OwnedByName(staked) < deck.Main.Where(e => e.Name.Equals(staked, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count))
                RemoveOne(mine, staked);
            RemoveOne(theirs, CardKey.Parse(pending.OpponentAnte).Name);
        }
        var title = pending.Kind switch { CareerRules.Practice => "Practice match", CareerRules.Ante => "Ante match", _ => "Daily match" };
        // Testing: ARCANUM_SANDBOX=1 starts the match from the sandbox board.
        return new MatchSetup(new[] { new GameSession.Seat(playerName, mine, Sideboard: deck.ResolveSideboard(cards)),
                new GameSession.Seat(opponent.Name, theirs, IsBot: true, Sideboard: theirList.ResolveSideboard(cards)) }, 20,
            Sandbox: OS.GetEnvironment("ARCANUM_SANDBOX") == "1")
        {
            Event = new EventHook(winner => Finish(winner == 0), App.CareerScene, title) { ReturnText = "Back home" },
            Table = AnyTable(Random.Shared),
            // Testing: ARCANUM_CAREER_MATS=<mine>,<theirs> shows those playmats instead ("none" for none).
            TableMats = OS.GetEnvironment("ARCANUM_CAREER_MATS").Split(',') is [var testMine, var testTheirs]
                ? new[] { testMine, testTheirs }.Select(id => id == "none" ? null : id).ToList()
                : new[] { PlayerMat, CareerRules.OpponentPlaymat(world, opponent.Name) },
        };
    }

    // ---------------------------------------------------------------- the calendar's events

    private LimitedService? _events;

    /// <summary>
    /// Whether career players see the set's draft statistics (grades on the draft table and the desk, and "Build for me"
    /// using them). Set to false to take them away from players; the computer players keep using them.
    /// </summary>
    public static readonly bool DraftRatingsForPlayers = true;

    /// <summary>The event entered, run like the Play screen's limited events but kept with the career and played on the store's table.</summary>
    public LimitedService Events => _events ??= new LimitedService(Folder + "/event.json", App.EventScene, () => EventTable(EnteredEvent), DraftRatingsForPlayers,
        (name, human) => human ? PlayerMat : CareerRules.OpponentPlaymat(World!, name),
        () => EnteredEvent is { TournamentMat: true } e ? e.Name : null,
        () => EnteredEvent is { RoundMinutes: > 0 } or { TournamentMat: true } ? EnteredEvent!.Name : null,
        () => EnteredEvent?.Name);

    /// <summary>The event entered, if any (without loading or setting up its running event).</summary>
    private EventSpec? EnteredEvent => HasCareer && State.Event is { } entry ? World!.Event(entry.Id, entry.Day) : null;

    /// <summary>The big tournaments' table, where events played on a tournament mat are played.</summary>
    public const string TournamentTable = "res://ui-art/kit/career/regional-table.jpg";

    /// <summary>The table an event is played at: the big tournaments' for one on a tournament mat, otherwise the day's.</summary>
    public string? EventTable(EventSpec? spec) =>
        spec is { TournamentMat: true } && TestTable is null && ResourceLoader.Exists(TournamentTable) ? TournamentTable : TableFor(CareerRules.DayOf(Now));

    /// <summary>The playmat the player puts on the table, or null for none.</summary>
    private string? PlayerMat => _save?.State.Playmat is { Length: > 0 } id ? id : null;

    /// <summary>The calendar event entered, if any, with its running event (rebuilt from its seed when its file is gone).</summary>
    /// <summary>The calendar event entered, if any: a draft or constructed one with its running event (rebuilt from its seed when its file is gone), a Commander one without (see <see cref="Pod"/>).</summary>
    public (EventSpec Spec, EventEntry Entry, LimitedEvent? Running)? CurrentEvent()
    {
        if (!HasCareer || State.Event is not { } entry || World!.Event(entry.Id, entry.Day) is not { } spec) return null;
        if (spec.Kind == CareerRules.Commander) return (spec, entry, null);
        Events.Load();
        if (Events.Current is null) Begin(spec, entry);
        // An event of several days is over when its last day is: the rounds left unplayed are lost (the player is dropped).
        if (spec.Days > 1 && CareerRules.DayOf(Now) > CareerRules.LastDay(spec, entry.Start) && Events.Current!.Stage == EventStage.Playing)
            Events.DropOut();
        return (spec, entry, Events.Current!);
    }

    /// <summary>Enters today's event (paying as chosen) and sets it up: the seats, and the draft or the decks.</summary>
    public void Enter(EventSpec spec, string pay, DeckList? deck)
    {
        Events.Abandon(); // any leftover of an earlier event
        if (File.Exists(PodPath)) File.Delete(PodPath);
        _pod = null;
        Do(new EnterEvent(Now, spec.Id, pay, deck?.Export()));
        if (spec.Kind != CareerRules.Commander) Begin(spec, State.Event!);
    }

    /// <summary>How well visitors draft: casual players.</summary>
    private const double VisitorSkill = 0.25;

    /// <summary>How well a regular drafts: better the higher the reputation tier it takes to meet them (0.4 to 0.9), rivals a bit more.</summary>
    private double DraftSkill(OpponentSpec regular)
    {
        int tiers = World!.Content.Tiers.Count;
        double skill = 0.4 + 0.5 * regular.Tier / Math.Max(1, tiers - 1);
        return Math.Min(1, skill + (regular.Rival ? 0.1 : 0));
    }

    private void Begin(EventSpec spec, EventEntry entry)
    {
        var world = World!;
        var random = new Random((int)(entry.Seed & 0x7FFFFFFF));
        // The regulars come first (in a random order), then visitors.
        var regulars = world.Content.Opponents.OrderBy(_ => random.Next()).ToList();
        var visitors = world.Content.Visitors.OrderBy(_ => random.Next()).ToList();
        // A big event's crowd: more visitors, the first names of some with the family names of others.
        var names = world.Content.Visitors.Select(v => v.Split(' ', 2)).Where(p => p.Length == 2).ToList();
        visitors.AddRange(names.SelectMany(a => names.Where(b => b != a).Select(b => $"{a[0]} {b[1]}"))
            .Except(world.Content.Visitors).Distinct().OrderBy(_ => random.Next()).ToList());
        var seats = new List<EventSeat> { new() { Name = Settings.PlayerName, IsHuman = true, Deck = spec.Kind == CareerRules.Constructed ? DeckList.Parse(entry.Deck) : null } };
        // The computer players bring decks legal in the event's format (any of theirs when none is).
        var format = spec.Kind == CareerRules.Constructed ? world.FormatOf(spec) : null;
        List<string> Legal(IReadOnlyList<string> names)
        {
            if (format is null || string.IsNullOrEmpty(spec.Format)) return names.ToList();
            var legal = names.Where(n => world.Deck(n) is { } d && CareerRules.LegalIn(d, format, world)).ToList();
            return legal.Count > 0 ? legal : names.ToList();
        }
        var preconDecks = Legal(world.Content.Precons.Select(p => p.Deck).ToList());
        int players = CareerRules.Attendance(spec, entry.Seed);
        for (int i = 1; i < players; i++)
        {
            DeckList? deck = null;
            string name;
            double skill = VisitorSkill;
            if (i - 1 < regulars.Count)
            {
                var regular = regulars[i - 1];
                name = regular.Name;
                skill = DraftSkill(regular);
                if (spec.Kind == CareerRules.Constructed)
                {
                    var theirs = Legal(regular.Decks);
                    var name0 = theirs[random.Next(theirs.Count)];
                    deck = CareerRules.OpponentDeck(State, world, regular.Id, name0);
                    // A rival's improved deck must still be legal; otherwise the deck as it was.
                    if (format is not null && !string.IsNullOrEmpty(spec.Format) && !CareerRules.LegalIn(deck, format, world) && world.Deck(name0) is { } plain) deck = plain;
                }
            }
            else
            {
                int v = i - 1 - regulars.Count;
                name = v < visitors.Count ? visitors[v] : $"Visitor {v + 1}";
                if (spec.Kind == CareerRules.Constructed && preconDecks.Count > 0)
                    deck = world.Deck(preconDecks[random.Next(preconDecks.Count)]);
            }
            seats.Add(new EventSeat { Name = name, Deck = deck, Skill = skill });
        }
        var ev = new LimitedEvent
        {
            Mode = spec.Kind == CareerRules.Draft ? LimitedMode.Draft : LimitedMode.Sealed,
            Source = spec.Kind == CareerRules.Draft ? "set:" + entry.Set : "constructed:" + spec.Id,
            BestOf = spec.BestOf, Seed = (int)(entry.Seed & 0x7FFFFFFF), RoundsTotal = CareerRules.SwissRounds(spec, players), Seats = seats,
            TopCut = CareerRules.PlayoffRounds(spec, players) > 0 ? spec.TopCut : 0, CutAfter = spec.CutAfter, CutPoints = spec.CutPoints, RoundMinutes = spec.RoundMinutes,
            BoostersPerPlayer = spec.Kind == CareerRules.Draft ? world.Sets[entry.Set].BoostersPerPlayer : 0,
        };
        Events.Begin(ev, random);
    }

    /// <summary>The player's record in the running event: wins, losses, draws (byes are wins).</summary>
    public static (int Wins, int Losses, int Draws) Record(LimitedEvent ev)
    {
        int me = ev.HumanSeat, wins = 0, losses = 0, draws = 0;
        foreach (var m in ev.Rounds.SelectMany(r => r).Where(m => m.Done && m.Involves(me)))
        {
            if (m.SeatB is null) { wins++; continue; }
            int mine = m.WinsOf(me), theirs = m.WinsOf(m.Opponent(me)!.Value);
            if (mine > theirs) wins++;
            else if (mine < theirs) losses++;
            else draws++;
        }
        return (wins, losses, draws);
    }

    /// <summary>
    /// Ends the event: its result (dropping out early ends it with the record so far, in last place), the drafted cards
    /// kept, the prizes; returns what the career's rules gave.
    /// </summary>
    public CareerResult FinishEvent(bool drop = false)
    {
        var (spec, _, running) = CurrentEvent() ?? throw new CareerRuleException("You aren't in an event.");
        if (running is not { } ev)
        {
            var pod = Pod()!;
            int mine = pod.Winners.Count(w => w == 0), even = pod.Winners.Count(w => w < 0);
            int podPlace = drop ? spec.Players : 1 + Enumerable.Range(1, pod.Names.Count - 1).Count(s => pod.Winners.Count(w => w == s) > mine);
            var podResult = Do(new FinishEvent(Now, mine, pod.Winners.Count - mine - even, even, podPlace));
            if (File.Exists(PodPath)) File.Delete(PodPath);
            _pod = null;
            return podResult;
        }
        // Dropping concedes the match in progress; the others play the event out and the player keeps their place.
        if (drop) Events.DropOut();
        var (wins, losses, draws) = Record(ev);
        int place = ev.Stage != EventStage.Finished ? ev.Seats.Count : ev.Standings().IndexOf(ev.HumanSeat) + 1;
        var pool = spec.Kind == CareerRules.Draft
            ? (ev.Stage == EventStage.Drafting ? Events.Draft?.Picks[ev.HumanSeat] ?? new List<Arcanum.Data.Limited.PoolCard>() : ev.Seats[ev.HumanSeat].Pool)
                .Select(c => CardKey.Of(c).ToString()).ToList()
            : null;
        var result = Do(new FinishEvent(Now, wins, losses, draws, place, pool));
        Events.Abandon();
        return result;
    }

    // ---------------------------------------------------------------- Commander pods

    /// <summary>A Commander event's pod: its players (the person first), the decks of the others, each game's winner (-1: a draw).</summary>
    public sealed class CommanderPod
    {
        public List<string> Names { get; set; } = new();
        public List<string> Decks { get; set; } = new();
        public List<int> Winners { get; set; } = new();
    }

    private CommanderPod? _pod;
    private static string PodPath => ProjectSettings.GlobalizePath(Folder + "/pod.json");

    /// <summary>The pod of the Commander event entered (made from the event's seed the first time).</summary>
    public CommanderPod? Pod()
    {
        if (CurrentEvent() is not { Running: null } current) return null;
        var (spec, entry) = (current.Spec, current.Entry);
        if (_pod is not null) return _pod;
        if (File.Exists(PodPath))
            try { return _pod = System.Text.Json.JsonSerializer.Deserialize<CommanderPod>(File.ReadAllText(PodPath)); }
            catch (System.Text.Json.JsonException) { }
        var random = new Random((int)(entry.Seed & 0x7FFFFFFF));
        var regulars = World!.Content.Opponents.OrderBy(_ => random.Next()).ToList();
        var decks = spec.Decks.OrderBy(_ => random.Next()).ToList();
        _pod = new CommanderPod();
        _pod.Names.Add(Settings.PlayerName);
        _pod.Decks.Add("");
        int players = CareerRules.Attendance(spec, entry.Seed);
        for (int i = 1; i < players; i++)
        {
            _pod.Names.Add(i - 1 < regulars.Count ? regulars[i - 1].Name : $"Visitor {i}");
            _pod.Decks.Add(decks.Count == 0 ? "" : decks[(i - 1) % decks.Count]);
        }
        SavePod();
        return _pod;
    }

    private void SavePod()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PodPath)!);
        File.WriteAllText(PodPath, System.Text.Json.JsonSerializer.Serialize(_pod));
    }

    /// <summary>The pod's next game: everyone's Commander deck, the format's starting life; the winner is kept and the game goes back to the event.</summary>
    public MatchSetup PodGame()
    {
        var (spec, entry, _) = CurrentEvent()!.Value;
        var pod = Pod()!;
        var cards = App.Instance.Cards!;
        var seats = new List<GameSession.Seat>();
        for (int i = 0; i < pod.Names.Count; i++)
        {
            var deck = i == 0 ? DeckList.Parse(entry.Deck) : World!.Deck(pod.Decks[i]);
            var (main, _) = deck.Resolve(cards);
            seats.Add(new GameSession.Seat(pod.Names[i], main, IsBot: i > 0, deck.ResolveCommanders(cards), deck.ResolveCompanions(cards)));
        }
        return new MatchSetup(seats, World!.CommanderFormat.StartingLife, Commander: true)
        {
            Label = $"{spec.Name} · game {pod.Winners.Count + 1} of {spec.Rounds}",
            Event = new EventHook(winner =>
            {
                pod.Winners.Add(winner ?? -1);
                SavePod();
            }, App.EventScene, $"{spec.Name} · game {pod.Winners.Count + 1}") { ReturnText = "Back to the event" },
        };
    }

    /// <summary>The card an ante match was just won or lost for, shown when the player is back home (then cleared).</summary>
    public (CardKey Card, bool Won, string Opponent)? AnteResult { get; set; }

    /// <summary>Ends the match in progress; an ante match leaves its result to show at home.</summary>
    public CareerResult Finish(bool won)
    {
        var pending = State.Pending;
        var result = Do(new FinishMatch(Now, won));
        if (pending is { Kind: CareerRules.Ante })
            AnteResult = (CardKey.Parse(won ? pending.OpponentAnte : pending.PlayerAnte), won, pending.OpponentId);
        return result;
    }

    private static void RemoveOne(List<Arcanum.Engine.Cards.CardDefinition> deck, string name)
    {
        int i = deck.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) deck.RemoveAt(i);
    }
}
