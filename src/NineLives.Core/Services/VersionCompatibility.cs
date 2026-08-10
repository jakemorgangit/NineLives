namespace Blackcat.NineLives.Services;

/// <summary>How a backup's version relates to the target's (#210).</summary>
public enum VersionVerdict
{
    /// <summary>One side or the other did not say. No verdict, no warning - evidence only.</summary>
    Unknown,

    /// <summary>Same major version. Nothing to say.</summary>
    Same,

    /// <summary>
    /// Older backup onto a newer target - legal, and the database is upgraded on the way through.
    /// Worth a sentence, not a stop.
    /// </summary>
    UpgradeInPassing,

    /// <summary>
    /// Newer backup onto an older target. SQL Server can never do this - error 3169, no
    /// exceptions - so the restore must not start.
    /// </summary>
    Refuse
}

/// <summary>
/// The one-directional law of RESTORE (#210): a backup taken on a newer major version can never be
/// restored onto an older one. Not with compatibility levels, not between adjacent releases, not
/// ever - error 3169. And without this check it arrives from the server mid-restore, after WITH
/// REPLACE has already dropped the database being restored over.
///
/// Same failure shape as the credential preflight (#32), the readability preflight (#149) and the
/// disk-space check (#182), and the cheapest of the lot: the header carries the version, and the
/// target's is one SERVERPROPERTY away.
/// </summary>
public static class VersionCompatibility
{
    /// <summary>Major version to marketing year, for messages people can act on.</summary>
    public static string Describe(int major) => major switch
    {
        17 => "SQL Server 2025 (17.x)",
        16 => "SQL Server 2022 (16.x)",
        15 => "SQL Server 2019 (15.x)",
        14 => "SQL Server 2017 (14.x)",
        13 => "SQL Server 2016 (13.x)",
        12 => "SQL Server 2014 (12.x)",
        11 => "SQL Server 2012 (11.x)",
        10 => "SQL Server 2008 (10.x)",
        9 => "SQL Server 2005 (9.x)",
        _ => $"SQL Server (major version {major})"
    };

    public static VersionVerdict Check(int? backupMajor, int? targetMajor)
    {
        // No verdict from silence. A header that did not say, or an edition that does not report,
        // is not evidence of a mismatch - and refusing on a guess would block legal restores.
        if (backupMajor is not int backup || targetMajor is not int target) return VersionVerdict.Unknown;
        if (backup == target) return VersionVerdict.Same;

        return backup < target ? VersionVerdict.UpgradeInPassing : VersionVerdict.Refuse;
    }

    /// <summary>The refusal, naming both sides and the way out.</summary>
    public static string ExplainRefusal(int backupMajor, int targetMajor, string targetName) =>
        $"This backup was taken on {Describe(backupMajor)}. {targetName} is " +
        $"{Describe(targetMajor)}, and SQL Server can never restore a newer version's backup " +
        "onto an older one - error 3169, with no exceptions. Restore it onto a " +
        $"{Describe(backupMajor)} or later instance instead. Nothing has been changed on the " +
        "server or the database.";

    /// <summary>The upgrade note - one sentence, because the restore is legal and will work.</summary>
    public static string ExplainUpgrade(int backupMajor, int targetMajor) =>
        $"This backup was taken on {Describe(backupMajor)} and is being restored onto " +
        $"{Describe(targetMajor)}. That is fine - the database is upgraded as it is restored - " +
        "but it can never go back to the older version afterwards, and its compatibility level " +
        "will stay at the source's until raised.";
}
