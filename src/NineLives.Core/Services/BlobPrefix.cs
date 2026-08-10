namespace Blackcat.NineLives.Services;

/// <summary>
/// Works out which Azure blob prefixes can serve a given filter, so listing does not have to walk
/// the whole container and throw most of it away client-side (#28).
///
/// Measured against a real 4,440-blob container: an unprefixed listing takes about 1,075 ms, while
/// listing one database across the three backup-type folders takes about 233 ms. The ratio is not
/// the interesting part - the scaling is. A full scan grows with every server, database and day of
/// retention; a scoped listing grows only with the one database being restored.
///
/// Everything here is pure string work so it can be tested without touching Azure. Getting it
/// wrong silently returns FEWER backups than exist, which on this screen means a restore point
/// quietly disappearing - so the rule throughout is to return null (meaning "scan everything")
/// whenever a safe prefix cannot be proven.
/// </summary>
public static class BlobPrefix
{
    /// <summary>
    /// The prefixes to list for a filter, or null when no safe prefix exists and the caller must
    /// scan the whole container.
    /// </summary>
    /// <param name="pathPattern">The container's configured pattern, e.g. {BackupType}/{ServerName}/{DatabaseName}/{FileName}.</param>
    /// <param name="serverName">Selected server, or null for all.</param>
    /// <param name="databaseName">Selected database, or null for all.</param>
    /// <param name="backupTypeFolders">
    /// Folder names standing in for {BackupType} - normally the container's top-level folders. When
    /// the pattern needs {BackupType} and this is empty, no prefix can be derived.
    /// </param>
    public static IReadOnlyList<string>? Derive(
        string? pathPattern,
        string? serverName,
        string? databaseName,
        IReadOnlyCollection<string>? backupTypeFolders = null)
    {
        if (string.IsNullOrWhiteSpace(pathPattern)) return null;

        var segments = pathPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // One prefix under construction, or several once {BackupType} fans out.
        var prefixes = new List<string> { string.Empty };
        var anythingKnown = false;

        foreach (var segment in segments)
        {
            // {FileName} is the leaf. Everything after it is meaningless as a prefix, and the leaf
            // itself is never known in advance.
            if (Is(segment, "{FileName}")) break;

            if (!IsToken(segment))
            {
                // A literal path element - always known.
                prefixes = Append(prefixes, segment);
                anythingKnown = true;
                continue;
            }

            if (Is(segment, "{BackupType}"))
            {
                if (backupTypeFolders is not { Count: > 0 }) break;

                // Fan out: one prefix per backup-type folder. This is what makes the default
                // pattern usable at all, since {BackupType} leads it and would otherwise stop the
                // derivation on the very first segment.
                prefixes = backupTypeFolders
                    .SelectMany(folder => Append(prefixes, folder))
                    .ToList();
                anythingKnown = true;
                continue;
            }

            if (Is(segment, "{ServerName}") || Is(segment, "{ClusterName$AgName}") || Is(segment, "{ClusterName_AgName}"))
            {
                if (string.IsNullOrWhiteSpace(serverName)) break;
                prefixes = Append(prefixes, serverName);
                anythingKnown = true;
                continue;
            }

            if (Is(segment, "{DatabaseName}"))
            {
                if (string.IsNullOrWhiteSpace(databaseName)) break;
                prefixes = Append(prefixes, databaseName);
                anythingKnown = true;
                continue;
            }

            // Any other token - {InstanceName}, {AgName}, something new - has no value to hand, so
            // the path stops being predictable here.
            break;
        }

        if (!anythingKnown) return null;

        // A single empty prefix is the same as no prefix; say so plainly rather than issuing a
        // listing that pretends to be scoped.
        var result = prefixes.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// True when a container's layout can be prefix-scoped at all. Flat Ola AG naming puts every
    /// file at the container root with the structure encoded in the filename, so there is nothing
    /// to prefix on.
    /// </summary>
    public static bool SupportsPrefixes(Models.BackupSourceType sourceType) =>
        sourceType == Models.BackupSourceType.Standalone;

    private static List<string> Append(List<string> prefixes, string segment)
        => prefixes.Select(p => $"{p}{segment}/").ToList();

    private static bool IsToken(string segment)
        => segment.StartsWith('{') && segment.EndsWith('}');

    private static bool Is(string segment, string token)
        => segment.Equals(token, StringComparison.OrdinalIgnoreCase);
}
