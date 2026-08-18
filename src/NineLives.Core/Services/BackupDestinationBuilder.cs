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
        //
        // Never on a differential, matching the statement exactly (#441). The generator has the
        // same condition, because there IS no copy-only differential: the keyword protects the
        // differential base, and a differential does not move it. So the file name was claiming a
        // property of a statement that never carried it - noise at best, and at worst read as
        // meaningful by the listing, which uses this marker to classify what it finds. It also
        // read oddly in an audit, where the receipt correctly said NOT copy-only beside a file
        // called _COPY_ONLY_.
        //
        // Backups already on disk keep the old naming, so anything parsing the marker back out
        // still has to tolerate it on a _DIFF_ file. This changes what gets written from here on.
        var copy = copyOnly && type != BackupType.Differential ? "_COPY_ONLY" : string.Empty;

        return $"{Sanitise(databaseName)}_{FolderFor(type)}{copy}_{takenAt:yyyyMMdd_HHmmss}{suffix}" +
               ExtensionFor(type);
    }

    /// <summary>
    /// Where a file belongs INSIDE the container, by the container's own pattern (#491).
    ///
    /// Split out of <see cref="ForContainer"/> because writing a new backup is not the only thing
    /// that has to land in the right place. A backup that was taken to a local disk and is being
    /// copied in afterwards keeps the name it already has - but it still has to arrive where the
    /// listing looks, or the app cannot see it at all.
    ///
    /// The listing reads the database and server back OUT of this path: the pattern's tokens are
    /// how a blob is attributed. A file dropped at the container root has no path to read, so it
    /// belongs to no database, and every question asked per database steps straight over it.
    /// </summary>
    public static string PathFor(
        BlobContainerConfig container,
        string serverName,
        string databaseName,
        BackupType type,
        string fileName)
    {
        var (host, instance) = ServerIdentity.Split(serverName);

        var path = container.PathPattern
            .Replace("{BackupType}", FolderFor(type), StringComparison.OrdinalIgnoreCase)
            .Replace("{ServerName}", Sanitise(host), StringComparison.OrdinalIgnoreCase)
            .Replace("{InstanceName}", Sanitise(instance), StringComparison.OrdinalIgnoreCase)
            .Replace("{DatabaseName}", Sanitise(databaseName), StringComparison.OrdinalIgnoreCase)
            .Replace("{FileName}", fileName, StringComparison.OrdinalIgnoreCase);

        // A pattern with a token this app cannot fill leaves an empty segment behind, which would
        // put the backup one folder shallower than the listing expects to find it.
        return string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries));
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

                return $"{root}/{PathFor(container, serverName, databaseName, type, name)}";
            })
            .ToList();
    }

    /// <summary>
    /// How long a backup device URL may be before SQL Server refuses it (#346).
    ///
    /// The engine's own limit, not ours. Past it BACKUP TO URL fails on the server, with a
    /// message about the device rather than about lengths - and by then the run is under way.
    /// </summary>
    public const int MaxDeviceUrlLength = 259;

    /// <summary>
    /// Why these destinations cannot be written, or null when they can.
    ///
    /// Checked where it can still be fixed. Every part that makes a URL long is a setting
    /// somebody chose - the endpoint, the base prefix, the path pattern - so the refusal names
    /// the longest offender and its length rather than saying "too long" and leaving the search
    /// to whoever is standing there. Striping is the multiplier worth knowing about: every
    /// stripe carries the same prefix, so one that does not fit means none of them do.
    /// </summary>
    public static string? DescribeTooLong(IReadOnlyList<string> destinations)
    {
        var worst = destinations
            .Where(d => d.Length > MaxDeviceUrlLength)
            .OrderByDescending(d => d.Length)
            .FirstOrDefault();

        if (worst == null) return null;

        return $"This backup's destination is {worst.Length} characters and SQL Server refuses a " +
               $"backup URL longer than {MaxDeviceUrlLength}:{Environment.NewLine}{Environment.NewLine}" +
               $"{worst}{Environment.NewLine}{Environment.NewLine}" +
               "Shorten the container's path pattern, or its base path, before running this - the " +
               "server would refuse it mid-backup otherwise.";
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
