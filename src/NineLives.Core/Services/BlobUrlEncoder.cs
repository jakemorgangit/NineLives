namespace Blackcat.NineLives.Services;

/// <summary>
/// Encodes blob URLs so that spaces and other characters in path segments are valid in RESTORE FROM URL and HTTP requests.
/// </summary>
public static class BlobUrlEncoder
{
    /// <summary>
    /// Encodes the path portion of a blob URL (e.g. space → %20) so SQL Server and HTTP clients accept it.
    /// </summary>
    public static string Encode(string blobUrl)
    {
        if (string.IsNullOrEmpty(blobUrl)) return blobUrl;
        try
        {
            var uri = new Uri(blobUrl);
            var path = uri.AbsolutePath.TrimStart('/');
            if (string.IsNullOrEmpty(path)) return blobUrl;
            var segments = path.Split('/');
            // Uri.AbsolutePath is ALREADY percent-encoded (a raw space arrives here as %20),
            // so escape the DECODED segment - escaping the encoded form double-encodes
            // (%20 -> %2520) and Azure would look up a blob literally named "%20".
            // Unescape-then-escape is also idempotent for already-encoded input.
            var encodedSegments = segments.Select(s => Uri.EscapeDataString(Uri.UnescapeDataString(s)));
            var encodedPath = string.Join("/", encodedSegments);
            return $"{uri.Scheme}://{uri.Authority}/{encodedPath}";
        }
        catch
        {
            return blobUrl;
        }
    }
}
