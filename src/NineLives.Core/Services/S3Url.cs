using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The parts of an s3:// container URL (#51): <c>s3://endpoint[:port]/bucket[/base-prefix]</c>.
///
/// The exact string the engine restores from is also the string the app lists with - one URL,
/// two readers - so this is the one place the app's idea of what an s3:// URL means lives. The
/// listing requests themselves go over HTTPS path-style (<c>https://endpoint/bucket</c>): that
/// is the form SQL Server's own S3 connector speaks, and the one every S3-compatible provider
/// accepts regardless of how its bucket DNS is set up.
/// </summary>
/// <param name="Authority">Host, plus the port when the URL named one. Signed as-is into every
/// request's host header, so it must match what the HTTP client sends byte for byte.</param>
/// <param name="Bucket">The first path segment. Path-style requests put it back on the path.</param>
/// <param name="BasePrefix">Anything after the bucket, normalised to end in '/' (or empty).
/// Prepended to every listing prefix and stripped from every returned key, so blob names stay
/// relative to the container URL - the same contract the Azure side has always had.</param>
/// <param name="RegionFromHost">The region the host name itself states, when it does. AWS-style
/// endpoints (<c>s3.eu-west-2.amazonaws.com</c>, and the compatibles that copy the shape) carry
/// it; anything else answers null and the caller falls back to the configured region or the
/// engine's own default of us-east-1.</param>
public sealed record S3Url(string Authority, string Bucket, string BasePrefix, string? RegionFromHost)
{
    public string HttpsBase => $"https://{Authority}";

    /// <summary>A listed key made relative to the container URL, base prefix stripped.</summary>
    public string Relative(string key)
        => BasePrefix.Length > 0 && key.StartsWith(BasePrefix, StringComparison.Ordinal)
            ? key[BasePrefix.Length..]
            : key;

    public static S3Url Parse(string containerUrl)
        => TryParse(containerUrl)
           ?? throw new InvalidOperationException(
               $"'{containerUrl}' is not an s3:// container URL. The shape is " +
               "s3://endpoint[:port]/bucket - the endpoint is the provider's host name, and " +
               "the first path segment is the bucket.");

    public static S3Url? TryParse(string? containerUrl)
    {
        if (string.IsNullOrWhiteSpace(containerUrl)) return null;
        if (!Uri.TryCreate(containerUrl, UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        // AbsolutePath arrives percent-encoded; the prefix has to be the RAW key text because
        // the request signer encodes exactly once. Unescape-then-use, same rule as
        // BlobUrlEncoder, and idempotent for input that was never encoded.
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0) return null;

        var basePrefix = segments.Length > 1 ? string.Join('/', segments.Skip(1)) + "/" : string.Empty;

        // SQL Server's URL shape is s3://endpoint:port, and the port people write is 443.
        // HTTPS elides the default port from the Host header it sends, so signing "host:443"
        // while the wire says "host" would fail every request with SignatureDoesNotMatch -
        // normalise it away here, once, for both the signature and the request URI.
        var authority = uri.Port == 443 ? uri.Host : uri.Authority;

        return new S3Url(authority, segments[0], basePrefix, SniffRegion(uri.Host));
    }

    /// <summary>
    /// AWS regions all look like <c>xx-yyyy-N</c> and every S3-compatible that puts a region in
    /// its host copies the shape, digits at the end included. Requiring the full shape is what
    /// keeps ordinary host labels - "amazonaws", a company name - from being read as one.
    /// </summary>
    private static readonly Regex RegionShape = new(
        "^[a-z]{2,}(?:-[a-z0-9]+)+$", RegexOptions.Compiled);

    private static bool LooksLikeRegion(string label)
        => RegionShape.IsMatch(label) && char.IsAsciiDigit(label[^1]);

    private static string? SniffRegion(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            // s3.eu-west-2.amazonaws.com, s3.dualstack.eu-west-2.amazonaws.com - and the
            // compatibles that follow the pattern (Wasabi, Backblaze B2).
            if (labels[i] == "s3")
            {
                var j = labels[i + 1] == "dualstack" && i + 2 < labels.Length ? i + 2 : i + 1;
                if (LooksLikeRegion(labels[j])) return labels[j];
            }
            // The legacy AWS dash form: s3-eu-west-1.amazonaws.com.
            else if (labels[i].StartsWith("s3-", StringComparison.Ordinal)
                     && LooksLikeRegion(labels[i][3..]))
            {
                return labels[i][3..];
            }
        }
        return null;
    }
}
