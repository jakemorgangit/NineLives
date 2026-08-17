namespace Blackcat.NineLives.Models;

/// <summary>What one instance's service account could do with a folder.</summary>
public enum FolderAccess
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>The folder is there and the account could do what was asked of it.</summary>
    Ok,

    /// <summary>
    /// The account cannot see the folder at all.
    ///
    /// The one this check exists for. A SQL Server running as a local account, or as
    /// NT SERVICE\MSSQLSERVER, has no identity on the network and so cannot reach ANY share -
    /// which from the outside is indistinguishable from a path that does not exist, and is the
    /// commonest way a copy through a shared folder fails.
    /// </summary>
    NotVisible,

    /// <summary>Visible, and the account cannot create in it.</summary>
    CannotWrite,

    /// <summary>
    /// The instance never answered.
    ///
    /// A UNC path whose host does not resolve does not come back as "not found": the statement
    /// hangs while Windows looks for the machine, and the command timeout expires first.
    /// </summary>
    Unreachable,

    /// <summary>Something else. The server's own words are in the message.</summary>
    Other
}

/// <summary>
/// Whether one instance can use a folder, asked of that instance (#452).
///
/// The distinction that makes it worth asking at all: a copy through a shared folder needs the
/// SOURCE to write there as its own service account and the TARGET to read there as its own - two
/// different accounts, routinely not the same one, and neither of them this app. A check from here
/// would answer for the wrong identity entirely.
///
/// Asked BEFORE the backup, which is the whole point. The readability check that already exists
/// runs on a file, so it cannot run until a file is there - by which time a full backup has been
/// written and the wait is over. This one costs a round trip and answers the common failure in a
/// second.
/// </summary>
/// <param name="Folder">The folder as it was asked about.</param>
/// <param name="Access">What the account could do.</param>
/// <param name="ServerMessage">What SQL Server said, verbatim, whatever the classification.</param>
public sealed record FolderAccessCheck(
    string Folder, FolderAccess Access, string? ServerMessage = null)
{
    public bool IsOk => Access == FolderAccess.Ok;

    public static FolderAccessCheck Ok(string folder) => new(folder, FolderAccess.Ok);

    /// <summary>
    /// Turns what SQL Server said into which failure it was.
    ///
    /// The message is kept whatever the classification: the numbered error is what somebody
    /// searches for, and a wrong guess here must not hide the server's own words.
    /// </summary>
    public static FolderAccessCheck FromError(string folder, string message)
    {
        var m = message ?? string.Empty;

        var access =
            Contains(m, "timeout") || Contains(m, "network path was not found")
                ? FolderAccess.Unreachable
            : Contains(m, "access is denied") || Contains(m, "permission")
                ? FolderAccess.CannotWrite
            : Contains(m, "cannot find") || Contains(m, "does not exist")
                ? FolderAccess.NotVisible
                : FolderAccess.Other;

        return new FolderAccessCheck(folder, access, m);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to tell somebody, naming the instance whose account could not do it.
    ///
    /// Names the ACCOUNT rather than just the failure, because the fix is a permission grant to a
    /// specific identity and "access denied" on its own sends people to check the share from their
    /// own logged-in session, where it works.
    /// </summary>
    public string Explain(string serverName, bool wasWriteCheck) => Access switch
    {
        FolderAccess.Ok => wasWriteCheck
            ? $"{serverName} can write here as its own service account."
            : $"{serverName} can see this folder as its own service account.",

        FolderAccess.NotVisible =>
            $"{serverName} cannot see {Folder} at all. Its SQL Server service account is the one " +
            "that has to reach it, not your login - an instance running as a local account or as " +
            "NT SERVICE\\MSSQLSERVER has no identity on the network and cannot open any share.",

        FolderAccess.CannotWrite =>
            $"{serverName} can see {Folder} but its service account cannot create files there. " +
            "The backup would fail at the point of writing.",

        FolderAccess.Unreachable =>
            $"{serverName} never answered about {Folder}. A UNC path whose host does not resolve " +
            "hangs rather than reporting a missing folder, so check the machine name first.",

        _ => $"{serverName} could not use {Folder}: {ServerMessage}"
    };
}
