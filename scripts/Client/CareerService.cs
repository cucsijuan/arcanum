// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
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
    public const string StoreImage = "res://ui-art/kit/career/store.jpg";

    private CareerSave? _save;
    private string _contentId = "";

    public CareerWorld? World { get; private set; }

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
        var deckNames = module.DeckNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        World = new CareerWorld(content, cards, module.LoadSets(), name => deckNames.Contains(name) ? module.ReadDeck(name) : null);
        _contentId = $"{module.Manifest.Id} {module.Manifest.Version}";
        Load();
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

    /// <summary>Now, for the career: the device's clock, but never before the latest time the career has seen.</summary>
    public DateTime Now => _save is { } save && save.State.LastUtc > DateTime.UtcNow ? save.State.LastUtc : DateTime.UtcNow;

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
        return result;
    }

    /// <summary>Starts a new career with a starting deck, which also becomes the career's first deck.</summary>
    public void Start(string deck, string deckTitle)
    {
        Do(new StartCareer(DateTime.UtcNow, (ulong)Random.Shared.NextInt64(1, long.MaxValue), deck));
        Decks.Save(deckTitle, "casual", World!.Deck(deck));
    }

    /// <summary>Ends the career: the saved file is kept next to it as a backup, and the career's decks are removed.</summary>
    public void Abandon()
    {
        if (File.Exists(SavePath)) File.Move(SavePath, SavePath + ".old", overwrite: true);
        foreach (var deck in Decks.List(null)) Decks.Delete(deck);
        _save = null;
        Problem = null;
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

    /// <summary>The next daily match against the opponent of the day, with the board's table; null when none is left.</summary>
    public MatchSetup? DailyMatch(DeckList deck, string playerName)
    {
        var world = World!;
        var pending = State.Pending ?? throw new InvalidOperationException("No daily match started.");
        var opponent = world.Content.Opponents.First(o => o.Id == pending.OpponentId);
        var cards = App.Instance.Cards!;
        var (mine, _) = deck.Resolve(cards);
        var (theirs, _) = world.Deck(pending.Deck).Resolve(cards);
        return new MatchSetup(new[] { new GameSession.Seat(playerName, mine), new GameSession.Seat(opponent.Name, theirs, IsBot: true) }, 20)
        {
            Event = new EventHook(winner => Do(new FinishDaily(Now, winner == 0)), App.CareerScene, "Daily match") { ReturnText = "Back home" },
            Table = ResourceLoader.Exists(TableImage) ? TableImage : null,
        };
    }
}
