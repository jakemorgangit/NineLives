namespace Blackcat.NineLives.Models;

/// <summary>
/// How the target reaches the files the source wrote (#149).
///
/// msdb records paths as the SOURCE saw them, which is the one thing about this workflow that
/// catches people out. A job that backed up to <c>E:\SQLBackups\MyDb.bak</c> recorded a path that
/// means something entirely different on the target machine - if it resolves there at all, it
/// resolves to the target's own E: drive, which is worse than failing.
///
/// So the shared location this issue is about is often not the path in msdb: the source wrote to a
/// local path that happens to be a share, and the target reaches it by its UNC name. Stating that
/// substitution explicitly is what makes "both hosts can see it" a checkable claim rather than an
/// assumption.
///
/// When the source backed up to a UNC path already - the usual arrangement with a central backup
/// share - no mapping is needed and this does nothing.
/// </summary>
/// <param name="SourcePrefix">The path as the source wrote it, e.g. <c>E:\SQLBackups</c>.</param>
/// <param name="TargetPrefix">How the target reaches the same place, e.g. <c>\\SRV01\SQLBackups</c>.</param>
public sealed record BackupPathMapping(string SourcePrefix, string TargetPrefix)
{
    /// <summary>No substitution: the paths mean the same thing on both machines.</summary>
    public static readonly BackupPathMapping None = new(string.Empty, string.Empty);

    public bool IsInUse => !string.IsNullOrWhiteSpace(SourcePrefix) && !string.IsNullOrWhiteSpace(TargetPrefix);

    /// <summary>
    /// The path as the target should be asked about it.
    ///
    /// Case-insensitive, because Windows paths are - a mapping typed as <c>e:\backups</c> must
    /// still match a path msdb recorded as <c>E:\SQLBackups</c>'s drive. Anything that does not
    /// start with the source prefix is returned untouched rather than mangled: a chain can hold
    /// files from more than one location, and rewriting one that was already reachable would break
    /// a restore that would otherwise have worked.
    /// </summary>
    public string Apply(string path)
    {
        if (!IsInUse || string.IsNullOrWhiteSpace(path)) return path;

        if (!path.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase)) return path;

        var remainder = path[SourcePrefix.Length..];

        // Neither side is trusted to have a trailing separator, or to lack one.
        return $"{TargetPrefix.TrimEnd('\\', '/')}\\{remainder.TrimStart('\\', '/')}";
    }

    /// <summary>
    /// True when this mapping would change the path - so the screen can say whether the check it is
    /// about to run is asking about the same file the source wrote or a different name for it.
    /// </summary>
    public bool Changes(string path) => !string.Equals(path, Apply(path), StringComparison.Ordinal);

    /// <summary>
    /// Whether a path is one the target could plausibly reach at all.
    ///
    /// A local path on the source is the case worth catching early: it is not that the target
    /// cannot find it, but that it may find its OWN drive of that letter and read something else
    /// entirely. That is the only failure here that can produce a successful restore of the wrong
    /// backup, so it is worth saying before the check rather than after it.
    /// </summary>
    public static bool LooksLocalToTheSource(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        path.Length > 1 && path[1] == ':';
}
