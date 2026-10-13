// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

public class DraftRatingsTests
{
    private const string File = """
        {
          "credit": "Card ratings: sample data", "set": "tst", "format": "Draft", "games": 1000000, "mean": 0.55, "sd": 0.03,
          "cards": {
            "Glade Cub": {"gih": 0.616, "games": 20000, "ata": 2.4, "picks": 50000},
            "Ember Bolt": {"gih": 0.55, "games": 9000, "ata": 5.1, "picks": 30000},
            "Mire Crawler": {"gih": 0.49, "games": 4000},
            "Rare Wonder": {"gih": 0.70, "games": 120, "ata": 1.2, "picks": 400},
            "Field Guide": {"ata": 9.5, "picks": 1000},
            "Stone Gate": {"gih": 0.58, "games": 1500}
          }
        }
        """;

    [Fact]
    public void ReadsTheSetsStatistics()
    {
        var ratings = DraftRatings.Parse(File);
        Assert.Equal("Card ratings: sample data", ratings.Credit);
        Assert.Equal("tst", ratings.Set);
        Assert.Equal(0.55, ratings.Mean);
        Assert.Equal(new CardDraftStats(0.616, 20000, 2.4, 50000), ratings.Stats("glade cub"));
        Assert.Equal(new CardDraftStats(null, 0, 9.5, 1000), ratings.Stats("Field Guide"));
        Assert.Null(ratings.Stats("Unknown Card"));
        // A two-faced card is listed by its front face.
        Assert.Equal(ratings.Stats("Stone Gate"), ratings.Stats("Stone Gate // Open Gate"));
    }

    [Fact]
    public void GradesGoByHowFarAboveTheAverageACardIs()
    {
        var ratings = DraftRatings.Parse(File);
        Assert.Equal("A+", ratings.Grade("Glade Cub"));  // 2.2 spreads above
        Assert.Equal("C+", ratings.Grade("Ember Bolt")); // the average
        Assert.Equal("F", ratings.Grade("Mire Crawler")); // 2 spreads below
        Assert.Equal("B+", ratings.Grade("Stone Gate"));  // 1 spread above
        // Too few games, or no games at all: no grade.
        Assert.Null(ratings.Grade("Rare Wonder"));
        Assert.Null(ratings.Grade("Field Guide"));
    }

    [Fact]
    public void ScoresAreOnTheBotsScaleWithTrustFromTheGames()
    {
        var ratings = DraftRatings.Parse(File);
        var (score, confidence) = ratings.Score("Ember Bolt")!.Value;
        Assert.Equal(5, score, 6);
        Assert.Equal(9000 / 9500.0, confidence, 6);
        Assert.Equal(6.5, ratings.Score("Stone Gate")!.Value.Score, 6); // one spread above: 5 + 1.5
        Assert.Equal(10, ratings.Score("Rare Wonder")!.Value.Score); // capped
        Assert.True(ratings.Score("Rare Wonder")!.Value.Confidence < 0.2);
        Assert.Null(ratings.Score("Field Guide"));
    }
}
