using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class BlobUrlEncoderTests
{
    [Fact]
    public void Encode_SpacesInSegments_PercentEncoded()
    {
        var result = BlobUrlEncoder.Encode("https://acct.blob.core.windows.net/backups/my db/full backup.bak");
        Assert.Equal("https://acct.blob.core.windows.net/backups/my%20db/full%20backup.bak", result);
    }

    [Fact]
    public void Encode_CleanUrl_Unchanged()
    {
        const string url = "https://acct.blob.core.windows.net/backups/FULL/SRV01/Db/20260110_220000.bak";
        Assert.Equal(url, BlobUrlEncoder.Encode(url));
    }

    [Fact]
    public void Encode_SingleQuote_PercentEncoded()
    {
        var result = BlobUrlEncoder.Encode("https://acct.blob.core.windows.net/backups/o'brien.bak");
        Assert.Equal("https://acct.blob.core.windows.net/backups/o%27brien.bak", result);
    }

    [Fact]
    public void Encode_SlashesBetweenSegments_Preserved()
    {
        var result = BlobUrlEncoder.Encode("https://h/a/b/c.bak");
        Assert.Equal("https://h/a/b/c.bak", result);
    }

    [Fact]
    public void Encode_AlreadyEncodedInput_IsIdempotent()
    {
        // %20 must stay %20, not double-encode to %2520 - Azure would otherwise look up
        // a blob literally named "%20".
        var result = BlobUrlEncoder.Encode("https://h/c/my%20file.bak");
        Assert.Equal("https://h/c/my%20file.bak", result);
    }

    [Fact]
    public void Encode_QueryString_IsDropped()
    {
        // Characterization: only scheme://authority/path survives - a SAS query string is
        // stripped. Generated scripts rely on a server-side credential, never a URL token.
        var result = BlobUrlEncoder.Encode("https://h/c/file.bak?sv=2026&sig=abc");
        Assert.Equal("https://h/c/file.bak", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Encode_NullOrEmpty_ReturnedAsIs(string? input)
    {
        Assert.Equal(input, BlobUrlEncoder.Encode(input!));
    }

    [Fact]
    public void Encode_UnparseableInput_ReturnedAsIs()
    {
        const string garbage = "not a url at all";
        Assert.Equal(garbage, BlobUrlEncoder.Encode(garbage));
    }

    [Fact]
    public void Encode_HostOnlyUrl_ReturnedAsIs()
    {
        // Empty path branch: nothing to encode.
        const string url = "https://acct.blob.core.windows.net";
        Assert.Equal(url, BlobUrlEncoder.Encode(url));
    }
}
