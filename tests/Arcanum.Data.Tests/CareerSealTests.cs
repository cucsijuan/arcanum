// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Arcanum.Data.Career;

namespace Arcanum.Data.Tests;

public partial class CareerTests
{
    private static readonly ECDsa SealKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly Dictionary<string, string> SealKeys = new() { ["t1"] = Convert.ToBase64String(SealKey.ExportSubjectPublicKeyInfo()) };
    private const string Content = "test 1.0.0";
    private const string Game = "9.9.9";

    private static CareerChecker Checker(DateTime? now = null, DateTime? importBefore = null) =>
        new(content => content == Content ? World() : null, Game, importBefore ?? DateTime.MinValue, SealKey, "t1", () => now ?? T0.AddDays(2));

    private static List<CareerAction> PlayedJournal() => new()
    {
        new StartCareer(T0, 42, "starter"),
        new StartDaily(T0.AddMinutes(1), "cash"),
        new FinishMatch(T0.AddMinutes(20), true),
        new OpenBooster(T0.AddMinutes(21), "abc"),
    };

    private static CareerSyncRequest Sync(IReadOnlyList<CareerAction> actions, int @base = 0, string? baseHash = null, CareerState? state = null) =>
        new(Content, Game, @base, baseHash ?? CareerSeals.Hash(Array.Empty<CareerAction>(), 0), actions, state);

    [Fact]
    public void ASealVouchesForTheStartOfAJournalAndNothingElse()
    {
        var journal = PlayedJournal();
        var seal = CareerSeals.Sign("player", 3, CareerSeals.Hash(journal, 3), "t1", SealKey);
        Assert.True(CareerSeals.Covers(seal, journal, SealKeys));
        Assert.Equal(CareerSeals.Hash(journal, 3), journal.Take(3).Aggregate(CareerSeals.Hash(journal, 0), CareerSeals.Extend));

        var changed = journal.ToList();
        changed[1] = new StartDaily(T0.AddMinutes(1), "booster");
        Assert.False(CareerSeals.Covers(seal, changed, SealKeys));
        Assert.False(CareerSeals.IsGenuine(seal with { Count = 4 }, SealKeys));
        Assert.False(CareerSeals.IsGenuine(seal with { Player = "someone else" }, SealKeys));
        Assert.False(CareerSeals.IsGenuine(seal, new Dictionary<string, string> { ["t1"] = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()) }));
        Assert.False(CareerSeals.IsGenuine(seal with { KeyId = "unknown" }, SealKeys));
    }

    [Fact]
    public void TheServiceReplaysANewCareerAndSealsIt()
    {
        var journal = PlayedJournal();
        var (response, stored) = Checker().Check("player", null, Sync(journal));
        Assert.Equal(CareerSyncStatus.Ok, response.Status);
        Assert.NotNull(stored);
        Assert.True(CareerSeals.Covers(response.Seal!, journal, SealKeys));
        Assert.Equal(4, response.Seal!.Count);
        Assert.Equal(CareerRules.Replay(journal, World()).Cash, stored!.State.Cash);
        Assert.Equal("player", response.Seal.Player);
    }

    [Fact]
    public void LaterSyncsBringOnlyTheNewActionsOnTopOfTheSeal()
    {
        var journal = PlayedJournal();
        var checker = Checker();
        var (_, stored) = checker.Check("player", null, Sync(journal.Take(2).ToList()));
        var (response, next) = checker.Check("player", stored, Sync(journal.Skip(2).ToList(), 2, stored!.Seal.Hash));
        Assert.Equal(CareerSyncStatus.Ok, response.Status);
        Assert.True(CareerSeals.Covers(response.Seal!, journal, SealKeys));
        Assert.Equal(4, next!.Journal.Count);
        Assert.Equal(CareerRules.Replay(journal, World()).Cash, next.State.Cash);
        Assert.Equal(stored.State.Sealed["abc"] - 1, next.State.Sealed["abc"]); // the booster opened in the new actions

        // A sync built on another base: the service says what it has.
        var (conflict, none) = checker.Check("player", next, Sync(new CareerAction[] { new StartDaily(T0.AddHours(1), "cash") }, 2, stored.Seal.Hash));
        Assert.Equal(CareerSyncStatus.Conflict, conflict.Status);
        Assert.Equal(next.Seal, conflict.Seal);
        Assert.Null(none);
    }

    [Fact]
    public void AnActionThatDoesntHoldIsRefusedWithEverythingAfterIt()
    {
        var journal = PlayedJournal();
        journal.Add(new OpenBooster(T0.AddMinutes(22), "abc"));
        journal.Add(new OpenBooster(T0.AddMinutes(23), "abc"));
        journal.Add(new OpenBooster(T0.AddMinutes(24), "abc")); // a fourth: the career had three
        journal.Add(new StartDaily(T0.AddMinutes(25), "cash"));
        var (response, stored) = Checker().Check("player", null, Sync(journal));
        Assert.Equal(CareerSyncStatus.Rejected, response.Status);
        Assert.Contains("Action 7 (OpenBooster)", response.Reason);
        Assert.Equal(6, response.Seal!.Count);
        Assert.Equal(6, stored!.Journal.Count);
        Assert.Equal(CareerRules.Replay(journal.Take(6), World()).Collection, stored.State.Collection);
    }

    [Fact]
    public void ActionsDatedAfterTheServicesClockAreRefused()
    {
        var journal = PlayedJournal();
        var (response, stored) = Checker(now: T0.AddMinutes(10)).Check("player", null, Sync(journal));
        Assert.Equal(CareerSyncStatus.Rejected, response.Status);
        Assert.Contains("future", response.Reason);
        Assert.Equal(2, stored!.Journal.Count);
    }

    [Fact]
    public void ACareerFromBeforeTheServiceIsTakenAsItIsWhenItNoLongerReplays()
    {
        var journal = PlayedJournal();
        journal.Add(new OpenBooster(T0.AddMinutes(30), "old-set")); // content that no longer exists
        var theirs = CareerRules.Replay(journal.Take(4), World());
        theirs.Cash = 1234;
        var (imported, stored) = Checker(importBefore: T0.AddDays(1)).Check("player", null, Sync(journal, state: theirs));
        Assert.Equal(CareerSyncStatus.Ok, imported.Status);
        Assert.Equal(5, imported.Seal!.Count);
        Assert.Equal(1234, stored!.State.Cash);

        // A career started after the service began checking is never taken on its word.
        var (refused, _) = Checker(importBefore: T0).Check("player", null, Sync(journal, state: theirs));
        Assert.Equal(CareerSyncStatus.Rejected, refused.Status);
        Assert.Equal(4, refused.Seal!.Count);
    }

    [Fact]
    public void ANewCareerReplacesTheOldOneAndOtherVersionsAreNotChecked()
    {
        var checker = Checker();
        var (_, stored) = checker.Check("player", null, Sync(PlayedJournal()));
        var fresh = new List<CareerAction> { new StartCareer(T0.AddHours(2), 7, "starter") };
        var (response, next) = checker.Check("player", stored, Sync(fresh));
        Assert.Equal(CareerSyncStatus.Ok, response.Status);
        Assert.Single(next!.Journal);

        Assert.Equal(CareerSyncStatus.Unsupported, checker.Check("player", null, Sync(fresh) with { Game = "1.0.0" }).Response.Status);
        Assert.Equal(CareerSyncStatus.Unsupported, checker.Check("player", null, Sync(fresh) with { Content = "other 1.0.0" }).Response.Status);
    }

    [Fact]
    public void ASyncKeepsItsHashThroughTheWire()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var journal = PlayedJournal();
        var sent = JsonSerializer.Deserialize<CareerSyncRequest>(JsonSerializer.Serialize(Sync(journal), web), web)!;
        Assert.Equal(CareerSeals.Hash(journal, 4), CareerSeals.Hash(sent.Actions, 4));
        var stored = Checker().Check("player", null, sent).Store!;
        var back = CareerChecker.Read(CareerChecker.Write(stored));
        Assert.Equal(stored.Seal, back.Seal);
        Assert.Equal(CareerSeals.Hash(stored.Journal, 4), CareerSeals.Hash(back.Journal, 4));
        Assert.Equal(stored.State.Cash, back.State.Cash);
    }
}
