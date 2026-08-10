namespace Blackcat.NineLives.Services;

/// <summary>
/// Narrows a blob listing to one server and/or database, so the filter can be pushed down to Azure
/// as a prefix rather than applied after everything has been downloaded (#28).
///
/// A hint, never a contract. Passing a scope the container's layout cannot honour is safe - the
/// listing falls back to a full scan and returns the same files it always did. What must never
/// happen is the reverse: a scope that quietly excludes backups that do exist, because on the
/// restore screen a missing backup is a missing restore point.
/// </summary>
/// <param name="ServerName">Server as it appears in the path, or null for all.</param>
/// <param name="DatabaseName">Database as it appears in the path, or null for all.</param>
public sealed record BlobListingScope(string? ServerName = null, string? DatabaseName = null)
{
    public bool HasAnything =>
        !string.IsNullOrWhiteSpace(ServerName) || !string.IsNullOrWhiteSpace(DatabaseName);
}
