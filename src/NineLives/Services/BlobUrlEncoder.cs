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
            var encodedSegments = segments.Select(s => Uri.EscapeDataString(s));
            var encodedPath = string.Join("/", encodedSegments);
            return $"{uri.Scheme}://{uri.Authority}/{encodedPath}";
        }
        catch
        {
            return blobUrl;
        }
    }
}
