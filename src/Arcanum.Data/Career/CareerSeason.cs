// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>
/// The season: changes to the store's calendar and prices over the career's days, published without a release (the
/// career service serves the current file; the content module has the one it shipped with). Each change applies from
/// its first day (to its last, when it has one), in the order they are listed. Days already played must never change:
/// careers are replayed from their journals with the season, so new changes only start in the future
/// (<see cref="ChangedPast"/> checks a new file against the published one).
/// </summary>
public sealed record CareerSeason
{
    /// <summary>The newest file layout this game understands.</summary>
    public const int Schema = 1;

    public static readonly CareerSeason None = new();

    /// <summary>The file layout; a newer one than <see cref="Schema"/> needs a newer game.</summary>
    public int SchemaVersion { get; init; } = Schema;

    /// <summary>Changes in the order they apply.</summary>
    public IReadOnlyList<SeasonChange> Changes { get; init; } = Array.Empty<SeasonChange>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CareerSeason Parse(string json)
    {
        var season = JsonSerializer.Deserialize<CareerSeason>(json, Options) ?? throw new FormatException("Empty season file.");
        if (season.SchemaVersion > Schema) throw new FormatException($"The season file needs a newer game (layout {season.SchemaVersion}).");
        foreach (var change in season.Changes)
        {
            if (change.From == default) throw new FormatException("Every season change needs its first day (\"from\").");
            if (change.Until is { } until && until < change.From) throw new FormatException($"The change from {change.From:yyyy-MM-dd} ends before it starts.");
            if (change.Events.Any(e => string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.When)))
                throw new FormatException($"An event of the change from {change.From:yyyy-MM-dd} needs an id, a name and when it happens.");
        }
        return season;
    }

    /// <summary>The season as its file (every field written, so it reads back the same).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>What's wrong with the season for a content module's career (sets, booster kinds, decks and events it names).</summary>
    /// <param name="formats">The module's format ids (null: not checked).</param>
    public IReadOnlyList<string> Problems(CareerContent content, IReadOnlyDictionary<string, Limited.SetDefinition> sets, ICollection<string> decks,
        ICollection<string>? formats = null)
    {
        var problems = new List<string>();
        var known = content.Events.Select(e => e.Id).ToHashSet();
        bool Sold(string set) => content.Sets.Contains(set, StringComparer.OrdinalIgnoreCase) && sets.TryGetValue(set, out var s) && s.Booster is not null;
        foreach (var change in Changes)
        {
            string at = $"change from {change.From:yyyy-MM-dd}";
            if (change.Featured is { } featured && !Sold(featured)) problems.Add($"{at}: the store doesn't sell boosters of the featured set '{featured}'.");
            if (change.BoosterPrice is < 0) problems.Add($"{at}: a booster price can't be negative.");
            foreach (var (kind, price) in change.BoosterPrices)
                if (!content.BoosterPrices.ContainsKey(kind) || price < 0) problems.Add($"{at}: the store has no '{kind}' boosters to price (or the price is negative).");
            foreach (var id in change.Cancel.Where(id => !known.Contains(id))) problems.Add($"{at}: there is no event '{id}' to cancel.");
            foreach (var e in change.Events)
            {
                known.Add(e.Id);
                string where = $"{at}, event '{e.Id}'";
                bool date = DateOnly.TryParseExact(e.When, "yyyy-MM-dd", out var day);
                if (!date && !e.When.Equals("lastSunday", StringComparison.OrdinalIgnoreCase) && !e.When.Equals("daily", StringComparison.OrdinalIgnoreCase)
                    && !Enum.TryParse<DayOfWeek>(e.When, true, out _))
                    problems.Add($"{where}: \"when\" must be \"daily\", a day of the week, \"lastSunday\" or a date (2026-10-24).");
                if (date && (day < change.From || change.Until is { } until && day > until)) problems.Add($"{where}: its date is outside the change's days.");
                if (e.Kind is not (CareerRules.Draft or CareerRules.Constructed or CareerRules.Commander)) problems.Add($"{where}: unknown kind '{e.Kind}'.");
                if (e.Kind == CareerRules.Draft && !e.Set.Equals("featured", StringComparison.OrdinalIgnoreCase) && !Sold(e.Set))
                    problems.Add($"{where}: the store doesn't sell boosters of '{e.Set}'.");
                if (!string.IsNullOrEmpty(e.Format))
                {
                    if (e.Kind != CareerRules.Constructed) problems.Add($"{where}: only constructed events take a format.");
                    else if (formats is not null && !formats.Contains(e.Format)) problems.Add($"{where}: the module has no format '{e.Format}'.");
                }
                if (e.Kind == CareerRules.Commander && e.Decks.Count == 0) problems.Add($"{where}: Commander events need the computer players' \"decks\".");
                foreach (var deck in e.Decks.Where(d => !decks.Contains(d))) problems.Add($"{where}: there is no deck '{deck}'.");
                if (e.Players < 2 || e.Rounds < 0 || e.BestOf < 1 || e.Entry < 0) // 0 rounds: Swiss rounds by attendance problems.Add($"{where}: players, rounds, best of or entry make no sense.");
                if (e.Prizes.Count == 0) problems.Add($"{where}: it has no prizes.");
                if (e.Online is { } online)
                {
                    if (online.Starts.Count == 0) problems.Add($"{where}: an online event needs its start times.");
                    foreach (var start in online.Starts.Where(t => !TimeOnly.TryParseExact(t, "HH:mm", out _)))
                        problems.Add($"{where}: start time '{start}' must be like 20:00 (UTC).");
                    if (online.Starts.Distinct().Count() != online.Starts.Count) problems.Add($"{where}: a start time is repeated.");
                    if (online.CheckIn is < 5 or > 240) problems.Add($"{where}: check-in must open between 5 and 240 minutes before the start.");
                    if (online.OfflineScale is < 0 or > 1) problems.Add($"{where}: the offline share of the prizes must be between 0 and 100%.");
                }
            }
        }
        return problems;
    }

    /// <summary>The store's calendar on a day: the content's events with the changes in force.</summary>
    public IReadOnlyList<EventSpec> Events(CareerContent content, DateOnly day)
    {
        var events = content.Events.ToList();
        foreach (var change in On(day))
        {
            events.RemoveAll(e => change.Cancel.Contains(e.Id, StringComparer.Ordinal));
            foreach (var added in change.Events)
            {
                int at = events.FindIndex(e => e.Id == added.Id);
                if (at >= 0) events[at] = added; else events.Add(added);
            }
        }
        return events;
    }

    /// <summary>The week's featured set (the content's rotation, weeks from Monday), unless a change features another.</summary>
    public string FeaturedSet(CareerContent content, DateOnly day) =>
        On(day).LastOrDefault(c => c.Featured is { Length: > 0 })?.Featured ?? content.Featured[day.DayNumber / 7 % content.Featured.Count];

    /// <summary>A booster kind's price on a day (null or empty: play or draft boosters).</summary>
    public int BoosterPrice(CareerContent content, string? kind, DateOnly day)
    {
        int price = string.IsNullOrEmpty(kind) ? content.BoosterPrice : content.BoosterPrices.GetValueOrDefault(kind);
        foreach (var change in On(day))
        {
            if (string.IsNullOrEmpty(kind)) price = change.BoosterPrice ?? price;
            else if (change.BoosterPrices.TryGetValue(kind, out var changed)) price = changed;
        }
        return price;
    }

    /// <summary>The changes in force on a day.</summary>
    public IEnumerable<SeasonChange> On(DateOnly day) => Changes.Where(c => c.From <= day && (c.Until is not { } until || day <= until));

    /// <summary>
    /// Why <paramref name="next"/> can't replace this published season on <paramref name="today"/>: the days before
    /// tomorrow must stay as they were (null when they do).
    /// </summary>
    public string? ChangedPast(CareerSeason next, DateOnly today)
    {
        // Every day up to today must have the same changes in force, in the same order.
        var first = Changes.Concat(next.Changes).Select(c => c.From).DefaultIfEmpty(today).Min();
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var before = JsonSerializer.Serialize(On(day).ToList(), Options);
            var after = JsonSerializer.Serialize(next.On(day).ToList(), Options);
            if (before != after) return $"{day:yyyy-MM-dd} would change (it has already been played); changes must start tomorrow or later.";
        }
        return null;
    }
}

/// <summary>A change to the store from a day on.</summary>
/// <param name="From">The first day it applies (UTC).</param>
/// <param name="Until">The last day it applies (null: from then on).</param>
public sealed record SeasonChange(DateOnly From, DateOnly? Until = null)
{
    /// <summary>Events added to the calendar, or replacing the event with the same id.</summary>
    public IReadOnlyList<EventSpec> Events { get; init; } = Array.Empty<EventSpec>();

    /// <summary>Ids of events taken off the calendar.</summary>
    public IReadOnlyList<string> Cancel { get; init; } = Array.Empty<string>();

    /// <summary>The featured set instead of the weekly rotation.</summary>
    public string? Featured { get; init; }

    /// <summary>The price of play or draft boosters, in cents.</summary>
    public int? BoosterPrice { get; init; }

    /// <summary>Prices of the other booster kinds the store sells ("set", "collector"), in cents.</summary>
    public IReadOnlyDictionary<string, int> BoosterPrices { get; init; } = new Dictionary<string, int>();

    /// <summary>A line for the store's news while it applies.</summary>
    public string? News { get; init; }
}
