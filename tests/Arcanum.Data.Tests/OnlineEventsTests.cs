// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Career;

namespace Arcanum.Data.Tests;

public partial class CareerTests
{
    private const string OnlineSeason = """
        { "changes": [ { "from": "2026-10-12", "events": [
            { "id": "draft", "name": "Draft night", "when": "Wednesday", "kind": "draft", "players": 8, "rounds": 3, "entry": 1500,
              "prizes": [{ "wins": 3, "boosters": 6, "reputation": 10 }, { "wins": 0, "reputation": 2 }],
              "online": { "starts": ["21:00", "18:00"], "checkIn": 30, "offlineScale": 0.5 } },
            { "id": "pvp", "name": "Online cup", "when": "Wednesday", "kind": "draft", "entry": 1000, "prizes": [{ "place": 1, "boosters": 8 }],
              "online": { "starts": ["20:00"], "pvpOnly": true } } ] } ] }
        """;

    private static readonly DateOnly Wednesday = new(2026, 10, 14);

    private static CheckIn Person(string id, int minute) => new(id, id.ToUpperInvariant(), Wednesday.ToDateTime(new TimeOnly(17, minute), DateTimeKind.Utc));

    [Fact]
    public void CheckInOpensBeforeEachStartUntilItBegins()
    {
        var draft = World(season: OnlineSeason).Event("draft", Wednesday)!;
        DateTime At(int h, int m) => Wednesday.ToDateTime(new TimeOnly(h, m), DateTimeKind.Utc);
        Assert.Equal(new[] { At(18, 0), At(21, 0) }, OnlineEvents.Starts(draft, Wednesday));
        Assert.Null(OnlineEvents.CheckInOpen(draft, Wednesday, At(17, 29)));
        Assert.Equal(At(18, 0), OnlineEvents.CheckInOpen(draft, Wednesday, At(17, 30)));
        Assert.Equal(At(18, 0), OnlineEvents.CheckInOpen(draft, Wednesday, At(17, 59)));
        Assert.Null(OnlineEvents.CheckInOpen(draft, Wednesday, At(18, 0)));
        Assert.Equal(At(21, 0), OnlineEvents.NextStart(draft, Wednesday, At(18, 0)));
    }

    [Fact]
    public void PeopleAreSeatedEvenlyAtAsFewTablesAsHoldThem()
    {
        var draft = World(season: OnlineSeason).Event("draft", Wednesday)!;
        var start = Wednesday.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc);
        var people = Enumerable.Range(0, 10).Select(i => Person($"p{i}", 50 - i)).ToList(); // p9 checked in first
        var tables = OnlineEvents.Seat(draft, Wednesday, start, people, 8, 99);
        Assert.Equal(new[] { 5, 5 }, tables.Select(t => t.People.Count));
        Assert.Equal("p9", tables[0].Host);
        Assert.Equal(10, tables.SelectMany(t => t.People).Select(p => p.Player).Distinct().Count());
        Assert.NotEqual(tables[0].Seed, tables[1].Seed);
        static IEnumerable<string> Seating(IEnumerable<OnlineTable> ts) => ts.SelectMany(t => t.People.Select(p => $"{t.Id}:{t.Seed}:{p.Player}"));
        Assert.Equal(Seating(tables), Seating(OnlineEvents.Seat(draft, Wednesday, start, people.AsEnumerable().Reverse().ToList(), 8, 99)));
        Assert.Single(OnlineEvents.Seat(draft, Wednesday, start, people.Take(8).ToList(), 8, 99));
        var twins = OnlineEvents.Seat(draft, Wednesday, start, new[] { Person("a", 1) with { Name = "Ana" }, Person("b", 2) with { Name = "ana" } }, 8, 99);
        Assert.Equal(new[] { "Ana", "ana 2" }, twins[0].People.Select(p => p.Name));

        // A host who doesn't open the lobby in time hands it to the next person.
        var table = tables[0];
        Assert.Equal(table.People[0].Player, OnlineEvents.HostAt(table, start.AddMinutes(9)));
        Assert.Equal(table.People[1].Player, OnlineEvents.HostAt(table, start.AddMinutes(11)));
        Assert.Equal(table.People[0].Player, OnlineEvents.HostAt(table with { Code = "ABCDEF" }, start.AddMinutes(30)));
    }

    [Fact]
    public void AResultIsConfirmedWhenTheHostAndThePlayerAgree()
    {
        var start = Wednesday.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc);
        var table = new OnlineTable("t", "draft", Wednesday, start, 8, 1, new[] { Person("host", 1), Person("guest", 2) }, "host");
        var report = new TableReport(new[] { new TableResult(0, 3, 0, 0, 1), new TableResult(1, 2, 1, 0, 2) },
            new[] { new TableMatch(3, 0, 1, 2, 1) });
        var own = new Dictionary<string, OwnResult>();
        Assert.Null(OnlineEvents.Confirmed(table, report, own, "guest"));
        Assert.Null(OnlineEvents.Confirmed(table, report, own, "host"));
        own["guest"] = new OwnResult(2, 1, 0, 3); // disagrees
        Assert.Null(OnlineEvents.Confirmed(table, report, own, "guest"));
        own["guest"] = new OwnResult(2, 1, 0, 2);
        Assert.Equal(report.Results[1], OnlineEvents.Confirmed(table, report, own, "guest"));
        Assert.Equal(report.Results[0], OnlineEvents.Confirmed(table, report, own, "host"));
        Assert.Null(OnlineEvents.Confirmed(table, report, own, "stranger"));

        var finish = new FinishEvent(start.AddHours(3), 2, 1, 0, 2, Table: "t", Players: 8, Pvp: true);
        Assert.Null(OnlineEvents.Check(table, report.Results[1], finish));
        Assert.NotNull(OnlineEvents.Check(table, report.Results[1], finish with { Wins = 3, Losses = 0 }));
        Assert.NotNull(OnlineEvents.Check(table, report.Results[1], finish with { Pvp = false }));
        Assert.NotNull(OnlineEvents.Check(table, null, finish));

        var tables = new[] { (table, report, (IReadOnlyDictionary<string, OwnResult>)own) };
        var ranks = OnlineEvents.Ranks(tables);
        Assert.Equal(OnlineEvents.StartRank + OnlineEvents.RankStep / 2, ranks["host"], 3);
        Assert.Equal(OnlineEvents.StartRank - OnlineEvents.RankStep / 2, ranks["guest"], 3);
        Assert.Equal(9, OnlineEvents.Points(tables)["host"]);
        Assert.Equal(6, OnlineEvents.Points(tables)["guest"]);
    }

    [Fact]
    public void AnOnlineEntryFinishesWithItsTablesResultAndOnlinePrizes()
    {
        var world = World(season: OnlineSeason);
        var at = Wednesday.ToDateTime(new TimeOnly(17, 40), DateTimeKind.Utc);
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "starter") };
        var state = new CareerState();
        CareerRules.Apply(state, journal[0], world);
        state.Cash += 5000;
        Assert.Contains("online only", Assert.Throws<CareerRuleException>(() => CareerRules.Apply(state, new EnterEvent(at, "pvp", CareerRules.PayCash), world)).Message);
        CareerRules.Apply(state, new EnterEvent(at, "pvp", CareerRules.PayCash, Online: true), world);
        Assert.True(state.Event!.Online);
        int boosters = state.Sealed.Values.Sum();
        CareerRules.Apply(state, new FinishEvent(at.AddHours(3), 3, 0, 0, 1, Table: "t", Players: 4, Pvp: true), world);
        Assert.Equal(boosters + 8, state.Sealed.Values.Sum());
        Assert.Contains("(online)", state.Notes[^1].Text);
    }

    [Fact]
    public void TheServiceAcceptsOnlyTheConfirmedResultOfAnOnlineTable()
    {
        var at = Wednesday.ToDateTime(new TimeOnly(17, 40), DateTimeKind.Utc);
        // The starting cash (1000) pays the online cup's entry.
        var journal = new List<CareerAction> { new StartCareer(T0, 42, "starter"), new EnterEvent(at, "pvp", CareerRules.PayCash, Online: true) };
        var honest = new FinishEvent(at.AddHours(3), 1, 2, 0, 3, Table: "t", Players: 8, Pvp: true);
        string? Confirm(string player, FinishEvent f) => f == honest ? null : "the record isn't the confirmed one";
        CareerChecker Check() => new(c => c == Content ? World(season: OnlineSeason) : null, Game, DateTime.MinValue, SealKey, "t1", () => at.AddDays(1), Confirm);

        var (ok, _) = Check().Check("player", null, Sync(journal.Append(honest).ToList()));
        Assert.Equal(CareerSyncStatus.Ok, ok.Status);
        var (bad, stored) = Check().Check("player", null, Sync(journal.Append(honest with { Wins = 3, Losses = 0, Place = 1 }).ToList()));
        Assert.Equal(CareerSyncStatus.Rejected, bad.Status);
        Assert.Contains("confirmed result", bad.Reason);
        Assert.Equal(2, stored!.Journal.Count);
    }
}
