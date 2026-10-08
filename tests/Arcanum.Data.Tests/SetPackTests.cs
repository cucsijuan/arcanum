// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

public class SetPackTests
{
    [Fact]
    public void APackPictureIsReadWithItsSeals()
    {
        var set = SetDefinition.Parse("""{"code":"abc","name":"Sample","pack":{"image":"https://example.invalid/pack.jpg","seals":[0.09,0.08]}}""");

        Assert.Equal("https://example.invalid/pack.jpg", set.PackImage);
        Assert.Equal((0.09, 0.08), set.PackImageSeals);
    }

    [Fact]
    public void ThePackPictureIsOptionalAndSealsHaveDefaults()
    {
        var plain = SetDefinition.Parse("""{"code":"abc","name":"Sample"}""");
        var noSeals = SetDefinition.Parse("""{"code":"abc","name":"Sample","pack":{"image":"https://example.invalid/pack.jpg"}}""");

        Assert.Null(plain.PackImage);
        Assert.Equal((0.07, 0.06), plain.PackImageSeals);
        Assert.Equal((0.07, 0.06), noSeals.PackImageSeals);
    }

    [Theory]
    [InlineData("""{"code":"abc","name":"Sample","pack":"https://example.invalid/pack.jpg"}""")]
    [InlineData("""{"code":"abc","name":"Sample","pack":{"image":42,"seals":0.1}}""")]
    [InlineData("""{"code":"abc","name":"Sample","pack":{"image":"x","seals":["a","b"]}}""")]
    [InlineData("""{"code":"abc","name":"Sample","pack":{"image":"x","seals":[0.1]}}""")]
    public void AMalformedPackIsIgnoredNotFatal(string json)
    {
        var set = SetDefinition.Parse(json);

        Assert.Equal("abc", set.Code);
        Assert.Equal((0.07, 0.06), set.PackImageSeals);
    }

    [Fact]
    public void SealsAreKeptInRange()
    {
        var set = SetDefinition.Parse("""{"code":"abc","name":"Sample","pack":{"image":"x","seals":[1.2,-0.5]}}""");

        Assert.Equal((0.3, 0.0), set.PackImageSeals);
    }
}
