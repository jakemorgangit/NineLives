using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

namespace Blackcat.NineLives.Services;

/// <summary>One listed object, exactly what a page of ListObjectsV2 says about it.</summary>
public sealed record S3Object(string Key, long SizeBytes, DateTimeOffset LastModified, string? ETag);

/// <summary>
/// One page of results. NextContinuationToken is null on the last page - it is the loop
/// condition, straight from the response.
/// </summary>
public sealed record S3ListPage(
    IReadOnlyList<S3Object> Objects,
    IReadOnlyList<string> CommonPrefixes,
    string? NextContinuationToken);

/// <summary>
/// ListObjectsV2 against any S3-compatible endpoint, signed with SigV4 and nothing else (#51).
///
/// This is the whole of the app's S3 wire surface: the engine does the restores itself, so the
/// only thing the app ever needs from the provider is "what is in the bucket" - one GET,
/// repeated per page. That is why this is a client and not an SDK reference: the AWS SDK would
/// be the biggest package in the application, brought in to make one request whose every byte
/// is pinned in tests anyway.
///
/// Requests are path-style (https://endpoint/bucket?list-type=2...) because that is what SQL
/// Server's own S3 connector speaks and the one form every compatible provider accepts without
/// per-bucket DNS. TLS is assumed for the same reason: the engine itself refuses s3:// over
/// plain HTTP, so a provider the engine can restore from is a provider this can list.
/// </summary>
public static class S3ListingClient
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Test seam. A signature can be pinned offline (S3SignerTests) but the request choreography
    /// - paging, prefixes, what a 403 turns into - needs the whole client to run, and it must
    /// run without a bucket. Checked on every send so a test observes exactly what would have
    /// gone on the wire, Authorization header included.
    /// </summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SenderForTests;

    /// <summary>
    /// One page of listing. The caller owns the paging loop and the prefix strategy; this owns
    /// the request shape, the signature and the parse.
    /// </summary>
    /// <param name="prefix">The raw key prefix, unencoded - encoding is the signer's job.</param>
    /// <param name="delimiter">"/" to read folders (CommonPrefixes) instead of walking keys.</param>
    /// <param name="maxKeys">Smaller first page for a connection probe; null takes the provider default.</param>
    public static async Task<S3ListPage> ListPageAsync(
        S3Url url,
        string keyId,
        string secret,
        string region,
        string? prefix,
        string? delimiter,
        int? maxKeys,
        string? continuationToken,
        CancellationToken ct = default)
    {
        // Raw values; CanonicalQuery encodes and orders them, and the URI carries that exact
        // canonical string - the server re-derives it from what arrives, so the two must match.
        var query = new List<(string Name, string Value)> { ("list-type", "2") };
        if (!string.IsNullOrEmpty(continuationToken)) query.Add(("continuation-token", continuationToken));
        if (!string.IsNullOrEmpty(delimiter)) query.Add(("delimiter", delimiter));
        if (maxKeys is { } mk) query.Add(("max-keys", mk.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(prefix)) query.Add(("prefix", prefix));

        var encodedPath = "/" + S3RequestSigner.UriEncode(url.Bucket, keepSlash: true);
        var canonicalQuery = S3RequestSigner.CanonicalQuery(query);
        var amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var headers = new List<(string Name, string Value)>
        {
            ("host", url.Authority),
            ("x-amz-content-sha256", S3RequestSigner.EmptyPayloadHash),
            ("x-amz-date", amzDate)
        };

        var authorization = S3RequestSigner.Authorization(
            "GET", encodedPath, query, headers, S3RequestSigner.EmptyPayloadHash,
            keyId, secret, region, "s3", amzDate);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{url.HttpsBase}{encodedPath}?{canonicalQuery}");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", S3RequestSigner.EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var send = SenderForTests ?? ((r, c) => Http.SendAsync(r, c));
        using var response = await send(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw Refusal((int)response.StatusCode, body);

        return ParsePage(body);
    }

    /// <summary>
    /// An S3 refusal comes back as XML with a Code and a Message, and the Message is usually
    /// the single most useful sentence available - AccessDenied, RequestTimeTooSkewed and
    /// SignatureDoesNotMatch all explain themselves. Passed through rather than translated;
    /// when the body is not the expected XML (a proxy's error page, an empty 403) the status
    /// line still stands on its own. The code rides on the exception for the failure
    /// explainer, which can turn it into the next move.
    /// </summary>
    private static S3RequestFailedException Refusal(int status, string body)
    {
        string? code = null, message = null;
        try
        {
            var root = XDocument.Parse(body).Root;
            code = Element(root, "Code");
            message = Element(root, "Message");
        }
        catch
        {
            // Not XML - the status alone will have to say it.
        }

        var head = string.IsNullOrEmpty(code)
            ? $"The S3 endpoint refused the listing (HTTP {status})."
            : $"The S3 endpoint refused the listing ({code}, HTTP {status}).";
        return new S3RequestFailedException(
            string.IsNullOrEmpty(message) ? head : $"{head} {message}", code, status);
    }

    /// <summary>
    /// Reads a ListBucketResult. By local name, ignoring the namespace: AWS stamps
    /// http://s3.amazonaws.com/doc/2006-03-01/ on the document, and some compatible providers
    /// omit it - the same parse has to accept both, because "S3-compatible" is the product
    /// promise here.
    /// </summary>
    internal static S3ListPage ParsePage(string xml)
    {
        var root = XDocument.Parse(xml).Root
            ?? throw new InvalidOperationException("The S3 listing response was empty.");

        var objects = new List<S3Object>();
        var prefixes = new List<string>();

        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "Contents":
                    var key = Element(element, "Key");
                    if (string.IsNullOrEmpty(key)) break;

                    long.TryParse(Element(element, "Size"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var size);

                    // ISO-8601 with a Z. Invariant and pinned to UTC for the same reason as
                    // the SAS expiry parse (#21): the ambient culture has no business here.
                    DateTimeOffset.TryParse(Element(element, "LastModified"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var modified);

                    objects.Add(new S3Object(key, size, modified, Element(element, "ETag")));
                    break;

                case "CommonPrefixes":
                    var prefix = Element(element, "Prefix");
                    if (!string.IsNullOrEmpty(prefix)) prefixes.Add(prefix);
                    break;
            }
        }

        return new S3ListPage(objects, prefixes, Element(root, "NextContinuationToken"));
    }

    private static string? Element(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
