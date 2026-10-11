// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Career;

namespace Arcanum.Data.Tests;

public class CareerCloudTests
{
    private static readonly byte[] Key = CareerSave.DefaultKey;
    private static readonly DateTime T0 = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private static CareerBundle Bundle(DateTime saved, params CareerAction[] journal)
    {
        var bundle = new CareerBundle { SavedUtc = saved };
        bundle.Files[CareerBundle.SaveName] = new CareerSave { Journal = journal.ToList() }.Write(Key);
        bundle.Files["decks/Burn.txt"] = "4 Card 1"u8.ToArray();
        return bundle;
    }

    private static readonly CareerAction Start = new StartCareer(T0, 42, "starter");
    private static readonly CareerAction Buy = new BuyBooster(T0.AddMinutes(5), "abc");
    private static readonly CareerAction Sell = new SellCard(T0.AddMinutes(6), "Card 1|ABC|1", 1);

    [Fact]
    public void ABundleReadsBackTheSame()
    {
        var bundle = Bundle(T0, Start, Buy);
        var back = CareerBundle.Unpack(bundle.Pack());
        Assert.Equal(T0, back.SavedUtc);
        Assert.Equal(bundle.Files.Keys.Order(), back.Files.Keys.Order());
        Assert.Equal(CloudSync.Same, CareerCloud.Compare(bundle, back, Key));
    }

    [Fact]
    public void TheCareerThatWentFurtherWinsAndCareersThatWentApartAreAConflict()
    {
        var cloud = Bundle(T0.AddHours(1), Start, Buy);
        Assert.Equal(CloudSync.Upload, CareerCloud.Compare(Bundle(T0, Start, Buy, Sell), cloud, Key));     // went on here (even if older files)
        Assert.Equal(CloudSync.Download, CareerCloud.Compare(Bundle(T0.AddHours(2), Start), cloud, Key)); // the cloud went on
        Assert.Equal(CloudSync.Conflict, CareerCloud.Compare(Bundle(T0, Start, Sell), cloud, Key));
        Assert.Equal(CloudSync.Download, CareerCloud.Compare(null, cloud, Key));                          // a new device
        Assert.Equal(CloudSync.Upload, CareerCloud.Compare(cloud, null, Key));                            // nothing in the cloud yet
    }

    [Fact]
    public void WithTheSameJournalTheNewerFilesWinAndAnEndedCareerCounts()
    {
        var cloud = Bundle(T0, Start, Buy);
        var edited = Bundle(T0.AddHours(1), Start, Buy);
        edited.Files["decks/Burn.txt"] = "4 Card 2"u8.ToArray();
        Assert.Equal(CloudSync.Upload, CareerCloud.Compare(edited, cloud, Key));
        Assert.Equal(CloudSync.Download, CareerCloud.Compare(cloud, edited, Key));
        var ended = new CareerBundle { SavedUtc = T0.AddHours(2) };
        Assert.Equal(CloudSync.Download, CareerCloud.Compare(edited, ended, Key));
    }
}
