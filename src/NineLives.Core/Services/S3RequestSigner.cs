using System.Security.Cryptography;
using System.Text;

namespace Blackcat.NineLives.Services;

/// <summary>
/// AWS Signature Version 4 - the hundred lines of it a signed GET needs, and nothing else.
///
/// Hand-rolled deliberately (#51): the alternative is the AWS SDK, which would be the largest
/// dependency in the application in exchange for one HMAC chain and some canonicalisation
/// rules. The risk of hand-rolling is quiet divergence from the specification, and the answer
/// to that is not reading the code harder - it is S3SignerTests, which pins every stage
/// (canonical request, derived key, final signature, whole header) against the worked example
/// in AWS's own documentation and its published test suite. A refactor that changes any byte
/// of the output fails those pins.
///
/// Everything here is pure string-and-bytes work: no clock, no network, no state. The caller
/// supplies the timestamp so that requests sign what they send and tests sign what the vectors
/// signed.
/// </summary>
internal static class S3RequestSigner
{
    /// <summary>
    /// SHA-256 of an empty body. Every request the listing client makes is a GET, so this is
    /// the only payload hash it ever sends - but S3 still requires it said explicitly, both as
    /// the x-amz-content-sha256 header and as the canonical request's last line.
    /// </summary>
    internal const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// RFC 3986 percent-encoding, the strict form SigV4 canonicalisation requires: unreserved
    /// characters bare, everything else %XX with uppercase hex, UTF-8 bytes underneath.
    /// Deliberately not Uri.EscapeDataString - .NET's set of "safe" characters has shifted
    /// between releases, and a signature is a place where the encoding drifting means every
    /// request starts failing with SignatureDoesNotMatch.
    /// </summary>
    /// <param name="keepSlash">True when encoding a URI path, where '/' separates segments
    /// and stays bare. Query names and values encode it.</param>
    internal static string UriEncode(string value, bool keepSlash = false)
    {
        var sb = new StringBuilder(value.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~'
                || (keepSlash && c == '/'))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(((int)b).ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The canonical query string: every name and value encoded, then sorted by encoded name
    /// (ordinal, which is the byte order the specification asks for). This exact string is
    /// also what goes on the request URI - the server re-derives the canonical form from what
    /// arrives, so sending anything that canonicalises differently breaks the signature.
    /// </summary>
    internal static string CanonicalQuery(IEnumerable<(string Name, string Value)> query)
        => string.Join('&',
            query
                .Select(q => (Name: UriEncode(q.Name), Value: UriEncode(q.Value)))
                .OrderBy(q => q.Name, StringComparer.Ordinal)
                .ThenBy(q => q.Value, StringComparer.Ordinal)
                .Select(q => $"{q.Name}={q.Value}"));

    /// <summary>
    /// The canonical request. Header names lower-case and sorted; the signed-headers line is
    /// derived from the same list, so the two can never disagree about what was signed.
    /// </summary>
    internal static string CanonicalRequest(
        string method,
        string encodedPath,
        string canonicalQuery,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadHash)
    {
        var canonical = headers
            .Select(h => (Name: h.Name.ToLowerInvariant(), Value: h.Value.Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(method).Append('\n');
        sb.Append(encodedPath.Length == 0 ? "/" : encodedPath).Append('\n');
        sb.Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in canonical)
            sb.Append(name).Append(':').Append(value).Append('\n');
        sb.Append('\n');
        sb.Append(string.Join(';', canonical.Select(h => h.Name))).Append('\n');
        sb.Append(payloadHash);
        return sb.ToString();
    }

    /// <summary>
    /// The signing key: HMAC chained through date, region and service from "AWS4" + secret.
    /// Derived per request rather than cached - the chain is four HMACs, and a cache keyed on
    /// day/region/service is complexity with nothing measurable to show for it.
    /// </summary>
    internal static byte[] SigningKey(string secret, string dateStamp, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>
    /// The complete Authorization header value for a request whose parts are given. The
    /// timestamp arrives as the already-formatted x-amz-date value (yyyyMMddTHHmmssZ) so that
    /// the header on the wire and the one signed here are the same string by construction.
    /// </summary>
    internal static string Authorization(
        string method,
        string encodedPath,
        IEnumerable<(string Name, string Value)> query,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadHash,
        string keyId,
        string secret,
        string region,
        string service,
        string amzDate)
    {
        var dateStamp = amzDate[..8];
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";

        var canonicalRequest = CanonicalRequest(
            method, encodedPath, CanonicalQuery(query), headers, payloadHash);

        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{HexSha256(canonicalRequest)}";

        var signature = Convert.ToHexStringLower(
            Hmac(SigningKey(secret, dateStamp, region, service), stringToSign));

        var signedHeaders = string.Join(';', headers
            .Select(h => h.Name.ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal));

        return $"AWS4-HMAC-SHA256 Credential={keyId}/{scope}, " +
               $"SignedHeaders={signedHeaders}, Signature={signature}";
    }

    internal static string HexSha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] Hmac(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
