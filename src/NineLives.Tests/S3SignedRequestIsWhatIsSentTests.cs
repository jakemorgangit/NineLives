using System.Net;
using System.Net.Http;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What was signed is what goes on the wire.
///
/// SigV4's characteristic failure is not a wrong algorithm - the signer is already pinned stage by
/// stage against AWS's published test vectors - but a mismatch between the canonical string the
/// signature was computed over and the bytes that actually arrive. The server re-derives the
/// canonical form from the request it receives; if anything re-escapes, unescapes or reorders the
/// query in between, the signatures differ and the provider answers SignatureDoesNotMatch, which
/// reads as "your key is wrong" rather than "your URL was rewritten".
///
/// The gap this closes is real: the client hands a STRING to HttpRequestMessage, which builds a
/// System.Uri from it, and Uri normalises. The AWS vectors cannot catch that - they end at the
/// canonical string. These cases are the ones a base path actually contains: spaces, brackets
/// round a region name, a plus, an already-percent-encoded sequence, an accent.
/// </summary>
[Collection(WpfCollection.Name)]
public class S3SignedRequestIsWhatIsSentTests
{
    [Theory]
    [InlineData("FULL/SRV01/Sales/")]
    [InlineData("SQL Backups/SRV01/")]
    [InlineData("Prod (EU)/SRV01/")]
    [InlineData("a+b/SRV01/")]
    [InlineData("~arch*ive/SRV01/")]
    [InlineData("50%25/SRV01/")]
    [InlineData("sauvegardes-é/SRV01/")]
    public async Task TheQueryOnTheWireIsTheQueryThatWasSigned(string prefix)
    {
        string? sent = null;

        S3ListingClient.SenderForTests = (req, _) =>
        {
            sent = req.RequestUri!.Query.TrimStart('?');
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>")
            });
        };

        try
        {
            var url = S3Url.TryParse("s3://s3.eu-west-2.amazonaws.com/dr-bucket")!;

            await S3ListingClient.ListPageAsync(
                url, "AKIAEXAMPLE", "secret", "eu-west-2",
                prefix: prefix, delimiter: "/", maxKeys: null, continuationToken: null);

            var signed = S3RequestSigner.CanonicalQuery(
            [
                ("list-type", "2"),
                ("delimiter", "/"),
                ("prefix", prefix)
            ]);

            Assert.Equal(signed, sent);
        }
        finally
        {
            S3ListingClient.SenderForTests = null;
        }
    }

    /// <summary>
    /// And the bucket in the path, for the same reason - a bucket name is more constrained than a
    /// prefix, but the path is signed too and goes through the same construction.
    /// </summary>
    [Fact]
    public async Task TheBucketPathSurvivesTheTripToo()
    {
        string? path = null;

        S3ListingClient.SenderForTests = (req, _) =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>")
            });
        };

        try
        {
            var url = S3Url.TryParse("s3://s3.eu-west-2.amazonaws.com/dr-bucket")!;

            await S3ListingClient.ListPageAsync(
                url, "AKIAEXAMPLE", "secret", "eu-west-2",
                prefix: null, delimiter: "/", maxKeys: null, continuationToken: null);

            Assert.Equal("/" + S3RequestSigner.UriEncode("dr-bucket", keepSlash: true), path);
        }
        finally
        {
            S3ListingClient.SenderForTests = null;
        }
    }
}
