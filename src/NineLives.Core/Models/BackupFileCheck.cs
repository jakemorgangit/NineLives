namespace Blackcat.NineLives.Models;

/// <summary>Why the target instance could not read a backup file.</summary>
public enum BackupFileProblem
{
    None,

    /// <summary>The path is not there as far as the target is concerned.</summary>
    NotFound,

    /// <summary>
    /// The path exists but the SQL Server service account cannot read it.
    ///
    /// The one this check exists for. A SQL Server running as a local account, or as
    /// NT SERVICE\MSSQLSERVER, has no identity on the network and so cannot read ANY share - which
    /// looks identical to a missing file from the outside, and is the most common way a restore
    /// from a shared location fails.
    /// </summary>
    AccessDenied,

    /// <summary>Reached and readable, but not a backup SQL Server can restore from.</summary>
    NotAValidBackup,

    /// <summary>
    /// The target never answered about this path.
    ///
    /// Found by probing a real instance: a UNC path whose HOST does not resolve does not come back
    /// as "not found" - the statement hangs while Windows looks for the machine, and the command
    /// timeout expires first. Without this it surfaced as a bare "Execution Timeout Expired" after
    /// a 30-second wait, which says nothing about what is wrong.
    /// </summary>
    Unreachable,

    /// <summary>Something else. The server's own words are in the message.</summary>
    Other
}

/// <summary>
/// What the TARGET instance made of one backup file (#149).
///
/// The distinction that makes this worth doing at all: the app's own process can see a share the
/// SQL Server service account cannot, and the restore runs as that account on the target host. A
/// File.Exists from here would say yes and then the restore would fail with
/// "Operating system error 5(Access is denied)" - so the question has to be asked ON the target,
/// by the account that will do the work.
/// </summary>
/// <param name="Path">The file as it was asked about.</param>
/// <param name="Problem">What stopped it, or None.</param>
/// <param name="ServerMessage">What SQL Server actually said, kept verbatim.</param>
public sealed record BackupFileCheck(string Path, BackupFileProblem Problem, string? ServerMessage = null)
{
    public bool CanBeRestored => Problem == BackupFileProblem.None;

    public static BackupFileCheck Ok(string path) => new(path, BackupFileProblem.None);

    /// <summary>
    /// Turns what SQL Server said into which of the failures it was.
    ///
    /// The message is kept whatever the classification, because the numbered error is what somebody
    /// searches for - and because a wrong guess here must not hide the server's own words.
    /// </summary>
    public static BackupFileCheck From(string path, string message)
    {
        var problem = Classify(message);
        return new BackupFileCheck(path, problem, message);
    }

    private static BackupFileProblem Classify(string message)
    {
        // Checked first: a timeout is SQL Server's own words about the COMMAND, not about the file,
        // so none of the operating system tests below would match it.
        if (message.Contains("Timeout Expired", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout period elapsed", StringComparison.OrdinalIgnoreCase))
            return BackupFileProblem.Unreachable;

        // Operating system error 5 is access denied; 2 and 3 are file and path not found. SQL Server
        // reports all of them inside a 3201 "Cannot open backup device", so the operating system
        // number is the part that tells them apart.
        if (message.Contains("error 5(", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return BackupFileProblem.AccessDenied;

        if (message.Contains("error 2(", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("error 3(", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The system cannot find the file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The system cannot find the path", StringComparison.OrdinalIgnoreCase))
            return BackupFileProblem.NotFound;

        // 3241: the media family is wrong or the file is not a backup at all.
        if (message.Contains("3241", StringComparison.Ordinal) ||
            message.Contains("media family", StringComparison.OrdinalIgnoreCase))
            return BackupFileProblem.NotAValidBackup;

        return BackupFileProblem.Other;
    }

    /// <summary>
    /// What to tell somebody, in terms of what they can do about it.
    ///
    /// Access denied gets the explanation rather than the error, because the error sends people to
    /// check the file - and the file is almost never the problem.
    /// </summary>
    public string Explain(string targetServerName) => Problem switch
    {
        BackupFileProblem.None => $"{Path} is readable.",

        BackupFileProblem.AccessDenied =>
            $"{targetServerName} can see {Path} but is not allowed to read it. The RESTORE runs as " +
            "the SQL Server service account, not as you - and an instance running as a local " +
            "account or as NT SERVICE\\MSSQLSERVER has no identity on the network, so it cannot " +
            "read any share. Grant that account read access, or run the service as a domain account.",

        BackupFileProblem.NotFound =>
            $"{targetServerName} cannot find {Path}. It is reachable from here but not from there, " +
            "which usually means the share is not mapped on that host, or the path is local to a " +
            "different machine.",

        BackupFileProblem.NotAValidBackup =>
            $"{Path} is readable but is not a backup {targetServerName} can restore from.",

        BackupFileProblem.Unreachable =>
            $"{targetServerName} did not answer about {Path} in time. A path whose HOST cannot be " +
            "reached from that machine behaves this way - Windows keeps looking for the server " +
            "rather than reporting a missing file. Check the machine name in the path, and that " +
            $"{targetServerName} can reach it.",

        _ => $"{targetServerName} could not read {Path}: {ServerMessage}"
    };
}
