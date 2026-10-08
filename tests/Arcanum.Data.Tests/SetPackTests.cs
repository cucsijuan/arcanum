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
}
