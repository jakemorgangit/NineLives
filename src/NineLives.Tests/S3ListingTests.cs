using System.Net;
using System.Net.Http;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The listing choreography (#51): what actually goes on the wire for an s3:// container, and
/// what comes back into the app. The signature's bytes are pinned in S3SignerTests against
/// AWS's published vectors; here the whole client runs against a scripted endpoint, so the
/// pins are about behaviour - paging follows the continuation token, prefixes push down, a
/// base prefix scopes and strips, folder markers are not backups, keys flow through the same
/// inference Azure listings do, and a refusal surfaces the provider's own sentence.
/// </summary>
public class S3ListingTests : IDisposable
{
    public void Dispose() => S3ListingClient.SenderForTests = null;

    // ── the scripted endpoint ───────────────────────────────────────────────────

    /// <summary>What the client sent, captured before the request is disposed.</summary>
    private sealed record Sent(string Uri, string? Authorization, string? AmzDate, string? ContentSha);

    private static (List<Sent> sent, BlobStorageService blobs, BlobContainerConfig config) Stage(
        string containerUrl,
        Queue<(HttpStatusCode Status, string Body)> responses,
        string pair = "AKIDEXAMPLE:test-secret",
        string? region = null,
        string pathPattern = BlobContainerConfig.DefaultPathPattern)
    {
        var config = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "s3",
            ContainerUrl = containerUrl,
            S3Region = region,
            PathPattern = pathPattern
        };

        var store = new FakeCredentialStore();
        if (pair.Length > 0) store.SaveSasToken(config, pair);

        var sent = new List<Sent>();
        S3ListingClient.SenderForTests = (request, _) =>
        {
            sent.Add(new Sent(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Authorization", out var auth) ? auth.First() : null,
                request.Headers.TryGetValues("x-amz-date", out var date) ? date.First() : null,
                request.Headers.TryGetValues("x-amz-content-sha256", out var sha) ? sha.First() : null));

            var (status, body) = responses.Count > 0
                ? responses.Dequeue()
                : (HttpStatusCode.OK, EmptyPage);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        };

        return (sent, new BlobStorageService(store), config);
    }

    private const string EmptyPage =
        """<?xml version="1.0"?><ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>""";

    private static string Page(string? nextToken, params (string Key, long Size, string Modified)[] objects)
    {
        var contents = string.Join("", objects.Select(o =>
            $"<Contents><Key>{o.Key}</Key><LastModified>{o.Modified}</LastModified>" +
            $"<ETag>\"etag-{o.Size}\"</ETag><Size>{o.Size}</Size></Contents>"));
        var token = nextToken == null
            ? "<IsTruncated>false</IsTruncated>"
            : $"<IsTruncated>true</IsTruncated><NextContinuationToken>{nextToken}</NextContinuationToken>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
               $"<Name>backups</Name>{token}{contents}</ListBucketResult>";
    }

    // ── listing behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task PagesFollowTheContinuationTokenAndKeysFlowThroughTheInference()
    {
        var (sent, blobs, config) = Stage("s3://s3.eu-west-2.amazonaws.com/backups",
            new Queue<(HttpStatusCode, string)>(
            [
                (HttpStatusCode.OK, Page("token-2",
                    ("FULL/SRV01/SalesDb/SalesDb_backup_2026_01_05_010000.bak", 1048576, "2026-01-05T01:10:00Z"),
                    ("LOG/SRV01/SalesDb/SalesDb_backup_2026_01_05_020000.trn", 2048, "2026-01-05T02:10:00Z"))),
                (HttpStatusCode.OK, Page(null,
                    ("FULL/", 0, "2026-01-01T00:00:00Z"),
                    ("DIFF/SRV01/SalesDb/SalesDb_backup_2026_01_06_010000.bak", 4096, "2026-01-06T01:10:00Z")))
            ]));

        var files = await blobs.ListBackupFilesAsync(config);

        // The zero-byte folder marker is not a backup; everything else inferred exactly as an
        // Azure listing would have.
        Assert.Equal(3, files.Count);
        Assert.Equal(2, sent.Count);
        Assert.Contains("continuation-token=token-2", sent[1].Uri);

        var full = files.Single(f => f.Type == BackupType.Full);
        Assert.Equal("SRV01", full.InferredServerName);
        Assert.Equal("SalesDb", full.InferredDatabaseName);
        Assert.Equal(
            "s3://s3.eu-west-2.amazonaws.com/backups/FULL/SRV01/SalesDb/SalesDb_backup_2026_01_05_010000.bak",
            full.BlobUrl);
        Assert.Equal("\"etag-1048576\"", full.ETag);

        // Ordered by LastModified, same as ever.
        Assert.Equal(
            files.OrderBy(f => f.LastModified).Select(f => f.BlobName).ToList(),
            files.Select(f => f.BlobName).ToList());
    }

    [Fact]
    public async Task TheRequestIsSignedPathStyleForTheHostsRegion()
    {
        var (sent, blobs, config) = Stage("s3://s3.eu-west-2.amazonaws.com/backups",
            new Queue<(HttpStatusCode, string)>());

        await blobs.ListBackupFilesAsync(config);

        var request = Assert.Single(sent);
        Assert.StartsWith("https://s3.eu-west-2.amazonaws.com/backups?list-type=2", request.Uri);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", request.Authorization);
        Assert.Contains("/eu-west-2/s3/aws4_request", request.Authorization);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", request.Authorization);
        Assert.Equal(S3RequestSigner.EmptyPayloadHash, request.ContentSha);
        Assert.NotNull(request.AmzDate);
    }

    [Fact]
    public async Task TheConfiguredRegionOutranksTheHostAndSilenceMeansUsEast1()
    {
        var (sentConfigured, blobsConfigured, configConfigured) = Stage(
            "s3://s3.eu-west-2.amazonaws.com/backups",
            new Queue<(HttpStatusCode, string)>(), region: "us-gov-east-1");
        await blobsConfigured.ListBackupFilesAsync(configConfigured);
        Assert.Contains("/us-gov-east-1/s3/", Assert.Single(sentConfigured).Authorization);

        var (sentPlain, blobsPlain, configPlain) = Stage(
            "s3://storage.example.com/backups", new Queue<(HttpStatusCode, string)>());
        await blobsPlain.ListBackupFilesAsync(configPlain);
        Assert.Contains("/us-east-1/s3/", Assert.Single(sentPlain).Authorization);
    }

    [Fact]
    public async Task AScopePushesThePrefixDown()
    {
        var (sent, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>(),
            pathPattern: "{ServerName}/{DatabaseName}/{FileName}");

        await blobs.ListBackupFilesAsync(
            config, new BlobListingScope("SRV01", "SalesDb"), progress: null);

        Assert.Contains("prefix=SRV01%2FSalesDb", Assert.Single(sent).Uri);
    }

    [Fact]
    public async Task ABasePrefixScopesTheListingAndStripsFromTheKeys()
    {
        var (sent, blobs, config) = Stage("s3://storage.example.com/backups/prod",
            new Queue<(HttpStatusCode, string)>(
            [
                (HttpStatusCode.OK, Page(null,
                    ("prod/FULL/SRV01/SalesDb/SalesDb_backup_2026_01_05_010000.bak", 100, "2026-01-05T01:10:00Z")))
            ]));

        var files = await blobs.ListBackupFilesAsync(config);

        Assert.Contains("prefix=prod%2F", Assert.Single(sent).Uri);

        var file = Assert.Single(files);
        // Relative to the container URL, so the path inference still reads FULL/... - and the
        // BlobUrl recomposes to the full engine-ready device string.
        Assert.Equal("FULL/SRV01/SalesDb/SalesDb_backup_2026_01_05_010000.bak", file.BlobName);
        Assert.Equal(BackupType.Full, file.Type);
        Assert.Equal(
            "s3://storage.example.com/backups/prod/FULL/SRV01/SalesDb/SalesDb_backup_2026_01_05_010000.bak",
            file.BlobUrl);
    }

    [Fact]
    public async Task VerifyConnectionProbesTheSmallestPage()
    {
        var (sent, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>());

        Assert.True(await blobs.VerifyConnectionAsync(config));
        Assert.Contains("max-keys=1", Assert.Single(sent).Uri);
    }

    [Fact]
    public async Task TopLevelFoldersComeFromCommonPrefixes()
    {
        var (sent, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>(
            [
                (HttpStatusCode.OK,
                    "<?xml version=\"1.0\"?><ListBucketResult>" +
                    "<IsTruncated>false</IsTruncated>" +
                    "<CommonPrefixes><Prefix>FULL/</Prefix></CommonPrefixes>" +
                    "<CommonPrefixes><Prefix>LOG/</Prefix></CommonPrefixes>" +
                    "</ListBucketResult>")
            ]));

        var folders = await blobs.ListTopLevelFoldersAsync(config);

        Assert.Equal(["FULL", "LOG"], folders);
        Assert.Contains("delimiter=%2F", Assert.Single(sent).Uri);
    }

    // ── refusals ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusalSurfacesTheProvidersOwnSentence()
    {
        var (_, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>(
            [
                (HttpStatusCode.Forbidden,
                    "<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code>" +
                    "<Message>Access Denied</Message></Error>")
            ]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blobs.VerifyConnectionAsync(config));

        Assert.Contains("AccessDenied", ex.Message);
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("Access Denied", ex.Message);
    }

    [Fact]
    public async Task ARefusalWithoutXmlStillNamesTheStatus()
    {
        var (_, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>(
                [(HttpStatusCode.InternalServerError, "<html>proxy says no</html>")]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blobs.VerifyConnectionAsync(config));

        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task AMissingPairSaysWhereItLives()
    {
        var (sent, blobs, config) = Stage("s3://storage.example.com/backups",
            new Queue<(HttpStatusCode, string)>(), pair: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => blobs.ListBackupFilesAsync(config));

        Assert.Contains("AccessKeyId:SecretKey", ex.Message);
        Assert.Empty(sent);
    }

    // ── the parse on its own ────────────────────────────────────────────────────

    [Fact]
    public void TheParseAcceptsTheNamespacedAndTheBare()
    {
        // AWS stamps the 2006-03-01 namespace; some compatible providers answer without one.
        // "S3-compatible" is the promise, so both shapes must read identically.
        var namespaced = S3ListingClient.ParsePage(Page("next-token",
            ("FULL/a.bak", 42, "2026-02-01T00:00:00Z")));
        var bare = S3ListingClient.ParsePage(
            "<?xml version=\"1.0\"?><ListBucketResult>" +
            "<Contents><Key>FULL/a.bak</Key><LastModified>2026-02-01T00:00:00Z</LastModified>" +
            "<Size>42</Size></Contents></ListBucketResult>");

        foreach (var page in new[] { namespaced, bare })
        {
            var obj = Assert.Single(page.Objects);
            Assert.Equal("FULL/a.bak", obj.Key);
            Assert.Equal(42, obj.SizeBytes);
            Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), obj.LastModified);
        }

        Assert.Equal("next-token", namespaced.NextContinuationToken);
        Assert.Null(bare.NextContinuationToken);
    }
}
