using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Where a backup this app takes should be written (#165).
///
/// The property that matters is a round trip: a backup this app writes must be one this app can
/// then FIND. The blob path already infers a backup's type, server and database from where it sits
/// in the container, using the container's own configured pattern - so a backup written anywhere
/// else lands in the container and is invisible to the screen that would restore it.
///
/// That is why this builds the destination from the SAME pattern the listing parses rather than
/// picking a layout of its own. A second convention here would be a backup that exists and cannot
/// be found, which is the worst shape a backup can take.
/// </summary>
public static class BackupDestinationBuilder
{
    /// <summary>
    /// The folder names the listing maps back to a type. These are what it parses, so these are
    /// what gets written.
    /// </summary>
    public static string FolderFor(BackupType type) => type switch
    {
        BackupType.Full => "FULL",
        BackupType.Differential => "DIFF",
        BackupType.TransactionLog => "LOG",
        _ => "FULL"
    };

    public static string ExtensionFor(BackupType type) =>
        type == BackupType.TransactionLog ? ".trn" : ".bak";

    /// <summary>
    /// The file name, in the shape the timestamp parser reads: <c>Db_TYPE_yyyyMMdd_HHmmss.bak</c>.
    ///
    /// The timestamp is not decoration. It is what groups stripes into one set and orders sets on
    /// the timeline, and the parser looks for exactly <c>yyyyMMdd_HHmmss</c> - so this format is a
    /// requirement rather than a preference.
    /// </summary>
    /// <param name="stripe">1-based stripe number, or null when the backup is not striped.</param>
    public static string FileName(
        string databaseName, BackupType type, DateTime takenAt, int? stripe = null, bool copyOnly = false)
    {
        var suffix = stripe.HasValue ? $"_{stripe.Value}" : string.Empty;

        // COPY_ONLY in the name as well as in the header. The listing reads it from the name (#49),
        // and a copy-only full that is not recognised as one becomes a differential's base - which
        // SQL Server then rejects with 3136.
        var copy = copyOnly ? "_COPY_ONLY" : string.Empty;

        return $"{Sanitise(databaseName)}_{FolderFor(type)}{copy}_{takenAt:yyyyMMdd_HHmmss}{suffix}" +
               ExtensionFor(type);
    }

    /// <summary>
    /// The blob URLs to write, in stripe order, laid out by the container's own pattern.
    /// </summary>
    public static List<string> ForContainer(
        BlobContainerConfig container,
        string serverName,
        string databaseName,
        BackupType type,
        DateTime takenAt,
        int stripes = 1,
        bool copyOnly = true)
    {
        var (host, instance) = ServerIdentity.Split(serverName);
        var root = container.ContainerUrl.TrimEnd('/');

        return Enumerable.Range(1, Math.Max(1, stripes))
            .Select(i =>
            {
                var name = FileName(databaseName, type, takenAt, stripes > 1 ? i : null, copyOnly);

                var path = container.PathPattern
                    .Replace("{BackupType}", FolderFor(type), StringComparison.OrdinalIgnoreCase)
                    .Replace("{ServerName}", Sanitise(host), StringComparison.OrdinalIgnoreCase)
                    .Replace("{InstanceName}", Sanitise(instance), StringComparison.OrdinalIgnoreCase)
                    .Replace("{DatabaseName}", Sanitise(databaseName), StringComparison.OrdinalIgnoreCase)
                    .Replace("{FileName}", name, StringComparison.OrdinalIgnoreCase);

                // A pattern with a token this app cannot fill leaves an empty segment behind, which
                // would put the backup one folder shallower than the listing expects to find it.
                path = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries));

                return $"{root}/{path}";
            })
            .ToList();
    }

    /// <summary>
    /// The file paths to write on a share.
    ///
    /// A share has no configured pattern to follow, and it does not need one: backups written here
    /// are discovered through the source instance's msdb, which records the path it wrote and
    /// everything else besides. So the layout only has to be sane for a person looking at the
    /// folder - one directory per database, which is what backup jobs do anyway.
    /// </summary>
    public static List<string> ForSharedPath(
        string root,
        string databaseName,
        BackupType type,
        DateTime takenAt,
        int stripes = 1,
        bool copyOnly = true)
    {
        var trimmed = root.TrimEnd('\\', '/');

        return Enumerable.Range(1, Math.Max(1, stripes))
            .Select(i => $@"{trimmed}\{Sanitise(databaseName)}\" +
                         FileName(databaseName, type, takenAt, stripes > 1 ? i : null, copyOnly))
            .ToList();
    }

    /// <summary>
    /// Strips what cannot appear in a file or blob name.
    ///
    /// A database name is far freer than a filename: <c>My/Db</c> is legal on SQL Server and would
    /// silently become a FOLDER in a blob path, putting the backup somewhere the listing would read
    /// as a different database entirely.
    /// </summary>
    private static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = value;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\']).Distinct())
            cleaned = cleaned.Replace(c, '_');

        return cleaned;
    }
}
