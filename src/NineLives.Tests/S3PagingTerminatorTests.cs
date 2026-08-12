using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// When an S3 listing stops (#416).
///
/// The paging loops terminate on `token != null`, and the token was taken straight off the XML
/// element. `XElement.Value` on a PRESENT BUT EMPTY element is "", not null - so a provider that
/// emits `&lt;NextContinuationToken/&gt;` on the last page instead of omitting it left a non-null
/// token, the loop asked for another page with an empty continuation-token (which means "from the
/// start"), got page one back, and went round again. An infinite loop accumulating duplicates
/// until the process ran out of memory.
///
/// AWS omits the element, so this never fires against S3 proper. The feature exists for the
/// S3-COMPATIBLE providers - Wasabi, B2, R2, MinIO, storage appliances - which is exactly where
/// empty-versus-omitted differs, and none of them have been run against.
///
/// `IsTruncated` is the authoritative flag and was not consulted at all.
/// </summary>
public class S3PagingTerminatorTests
{
    private const string OneObject = """
        <Contents>
          <Key>FULL/SRV01/Sales/20260801_220000.bak</Key>
          <Size>1024</Size>
          <LastModified>2026-08-01T22:00:00.000Z</LastModified>
          <ETag>"abc"</ETag>
        </Contents>
        """;

    private static string Page(string truncated, string? tokenElement) => $"""
        <ListBucketResult>
          {OneObject}
          <IsTruncated>{truncated}</IsTruncated>
          {tokenElement}
        </ListBucketResult>
        """;

    // ── the last page, in every shape a provider writes it ──────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("<NextContinuationToken></NextContinuationToken>")]
    [InlineData("<NextContinuationToken/>")]
    [InlineData("<NextContinuationToken>   </NextContinuationToken>")]
    public void TheLastPageEndsTheLoopHoweverTheProviderWritesIt(string? tokenElement)
    {
        var page = S3ListingClient.ParsePage(Page("false", tokenElement));

        Assert.Null(page.NextContinuationToken);
        Assert.Single(page.Objects);
    }

    /// <summary>
    /// IsTruncated is the authoritative flag: a stale token alongside "not truncated" must not
    /// start another round.
    /// </summary>
    [Fact]
    public void NotTruncatedEndsTheLoopEvenWithATokenPresent()
    {
        var page = S3ListingClient.ParsePage(
            Page("false", "<NextContinuationToken>abc123</NextContinuationToken>"));

        Assert.Null(page.NextContinuationToken);
    }

    // ── and a real next page still pages ────────────────────────────────────────

    [Fact]
    public void ATruncatedPageWithATokenCarriesOn()
    {
        var page = S3ListingClient.ParsePage(
            Page("true", "<NextContinuationToken>abc123</NextContinuationToken>"));

        Assert.Equal("abc123", page.NextContinuationToken);
        Assert.Single(page.Objects);
    }

    /// <summary>
    /// Truncated but with no usable token is the one genuinely broken response. Stopping is the
    /// only safe reading: continuing would send an empty token and restart the listing.
    /// </summary>
    [Fact]
    public void TruncatedWithNoTokenStopsRatherThanRestarting()
    {
        var page = S3ListingClient.ParsePage(Page("true", null));

        Assert.Null(page.NextContinuationToken);
    }
}
