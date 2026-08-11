using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// One URL, two readers (#51): the engine restores from the s3:// string and the app lists
/// with it, so what the app thinks the string means has to be pinned. The parts that carry
/// consequences: the authority is signed into every request, the bucket goes back on the
/// path, a base prefix must scope the listing AND strip from the keys (or blob names stop
/// being relative to the container), and the region falls back configured -> host -> the
/// engine's own us-east-1 assumption.
/// </summary>
public class S3UrlTests
{
    [Fact]
    public void TheThreePartsComeApart()
    {
        var url = S3Url.Parse("s3://storage.example.com:9000/backups/prod");

        Assert.Equal("storage.example.com:9000", url.Authority);
        Assert.Equal("https://storage.example.com:9000", url.HttpsBase);
        Assert.Equal("backups", url.Bucket);
        Assert.Equal("prod/", url.BasePrefix);
        Assert.Null(url.RegionFromHost);
    }

    [Fact]
    public void ABareBucketHasNoBasePrefix()
    {
        var url = S3Url.Parse("s3://s3.eu-west-2.amazonaws.com/backups");

        Assert.Equal("backups", url.Bucket);
        Assert.Equal(string.Empty, url.BasePrefix);
    }

    [Fact]
    public void Port443VanishesBecauseHttpsWillElideIt()
    {
        // SQL Server's own URL shape is s3://endpoint:port and people write the 443 out.
        // The Host header HTTPS actually sends has no :443 on it, and the signature must
        // sign what the wire says.
        Assert.Equal("storage.example.com", S3Url.Parse("s3://storage.example.com:443/b").Authority);
    }

    [Theory]
    [InlineData("s3://s3.eu-west-2.amazonaws.com/b", "eu-west-2")]
    [InlineData("s3://s3.dualstack.ap-south-1.amazonaws.com/b", "ap-south-1")]
    [InlineData("s3://s3-eu-west-1.amazonaws.com/b", "eu-west-1")]
    [InlineData("s3://s3.us-west-004.backblazeb2.com/b", "us-west-004")]
    [InlineData("s3://s3.eu-central-2.wasabisys.com/b", "eu-central-2")]
    [InlineData("s3://storage.example.com/b", null)]
    [InlineData("s3://accountid.r2.cloudflarestorage.com/b", null)]
    public void TheHostStatesItsRegionOrStaysSilent(string containerUrl, string? region)
    {
        Assert.Equal(region, S3Url.Parse(containerUrl).RegionFromHost);
    }

    [Fact]
    public void AnEncodedBasePrefixComesBackAsRawKeyText()
    {
        // The prefix parameter is signed from the raw text and encoded exactly once by the
        // signer - a pre-encoded prefix would go out double-encoded and match nothing.
        Assert.Equal("my backups/", S3Url.Parse("s3://h.example.com/b/my%20backups").BasePrefix);
    }

    [Fact]
    public void RelativeStripsTheBasePrefixAndOnlyTheBasePrefix()
    {
        var url = S3Url.Parse("s3://h.example.com/bucket/prod");

        Assert.Equal("FULL/f.bak", url.Relative("prod/FULL/f.bak"));
        Assert.Equal("elsewhere/f.bak", url.Relative("elsewhere/f.bak"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://acct.blob.core.windows.net/backups")]
    [InlineData("s3://host-only.example.com")]
    [InlineData("not a url")]
    public void AnythingElseRefusesToParse(string? containerUrl)
    {
        Assert.Null(S3Url.TryParse(containerUrl));
    }

    [Fact]
    public void TheRefusalSpellsTheShapeOut()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => S3Url.Parse("s3://host-only.example.com"));

        Assert.Contains("s3://endpoint[:port]/bucket", ex.Message);
    }
}
