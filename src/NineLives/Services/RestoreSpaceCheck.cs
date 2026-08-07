using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>What one volume the restore writes to has, and needs.</summary>
/// <param name="Volume">The mount point as SQL Server reports it, e.g. <c>D:\</c>.</param>
/// <param name="RequiredBytes">The size of the database files landing there.</param>
/// <param name="FreeBytes">What the volume has left, as the SQL Server service account sees it.</param>
public sealed record VolumeSpace(string Volume, long RequiredBytes, long FreeBytes)
{
    public bool Fits => FreeBytes >= RequiredBytes;

    public long ShortfallBytes => Math.Max(0, RequiredBytes - FreeBytes);

    /// <summary>
    /// A property, not a method: a binding cannot call one, and WPF renders nothing and says
    /// nothing when asked to - which shipped as an empty panel once already this week (#130).
    /// </summary>
    public string Describe =>
        Fits
            ? $"{Volume} needs {ByteSize.Format(RequiredBytes)} and has {ByteSize.Format(FreeBytes)} free."
            : $"{Volume} needs {ByteSize.Format(RequiredBytes)} and has only " +
              $"{ByteSize.Format(FreeBytes)} free - short by {ByteSize.Format(ShortfallBytes)}.";
}

/// <summary>
/// Whether a restore can physically fit before it is started (#32).
///
/// A restore that runs out of disk fails part-way, and by then WITH REPLACE has already dropped the
/// database it was replacing - so the target is gone and the thing that was going to replace it is
/// incomplete. That is the worst outcome this app can produce, and it is entirely predictable:
/// RESTORE FILELISTONLY already reports how big each file will be, and SQL Server already knows how
/// much room each volume has.
///
/// Both numbers come from the TARGET instance rather than from here, for the same reason the
/// shared-path readability check does: the app's process may not even be able to see those volumes,
/// and where it can, what it sees is not necessarily what the service account can write to.
/// </summary>
public static class RestoreSpaceCheck
{
    /// <summary>
    /// What each volume needs, given where the files are actually going.
    ///
    /// Grouped by volume rather than reported per file, because the question is whether the volume
    /// fits the total - four files of 10 GB on one drive is 40 GB, and checking them individually
    /// would pass each one while the restore still fails.
    /// </summary>
    /// <param name="files">
    /// The logical files, with the paths the restore will actually write to - which is
    /// <c>NewPhysicalName</c>, not the source's path, or this checks the wrong drive entirely.
    /// </param>
    /// <param name="freeByVolume">What each volume has left, as the target reported it.</param>
    public static List<VolumeSpace> Check(
        IEnumerable<FileMoveOption> files, IReadOnlyDictionary<string, long> freeByVolume)
    {
        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var volume = VolumeOf(file.NewPhysicalName);
            if (volume == null) continue;

            required[volume] = required.GetValueOrDefault(volume) + Math.Max(0, file.SizeBytes);
        }

        return required
            .Select(pair => new VolumeSpace(
                pair.Key,
                pair.Value,
                // A volume the target did not report on is treated as having nothing, so it is
                // reported rather than silently passing. Not knowing is not the same as fitting.
                MatchVolume(freeByVolume, pair.Key)))
            .OrderBy(v => v.Volume, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The volume a path lands on: <c>D:\Data\MyDb.mdf</c> to <c>D:\</c>.
    ///
    /// A UNC path has no drive letter and comes back null rather than being guessed at - SQL Server
    /// can restore to a share, and sys.dm_os_volume_stats does not describe one, so the honest
    /// answer is that this check does not cover it.
    /// </summary>
    public static string? VolumeOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        if (path.Length < 2 || path[1] != ':') return null;

        return path[..2].ToUpperInvariant() + @"\";
    }

    private static long MatchVolume(IReadOnlyDictionary<string, long> freeByVolume, string volume)
    {
        if (freeByVolume.TryGetValue(volume, out var free)) return free;

        // sys.dm_os_volume_stats reports a mount point that may or may not carry the trailing
        // separator depending on how the volume was mounted, so a miss on the exact string is not
        // yet a miss.
        var trimmed = volume.TrimEnd('\\');
        foreach (var (key, value) in freeByVolume)
            if (string.Equals(key.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase))
                return value;

        return 0;
    }

    /// <summary>
    /// What to say before starting, or empty when everything fits.
    ///
    /// Deliberately not a refusal. SQL Server can grow a file onto space that frees up, a volume can
    /// be extended while the restore runs, and this app is not the authority on somebody else's
    /// storage - but a restore that is 200 GB short is worth stopping to look at.
    /// </summary>
    public static string Warn(IReadOnlyList<VolumeSpace> volumes)
    {
        var tight = volumes.Where(v => !v.Fits).ToList();
        if (tight.Count == 0) return string.Empty;

        return "This restore may not fit. " + string.Join(" ", tight.Select(v => v.Describe)) +
               " The restore would fail part-way, and with WITH REPLACE the database being " +
               "restored over is dropped before that happens - so it would be gone and the " +
               "replacement incomplete.";
    }
}
