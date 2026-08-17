using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>What a restored database looks like, read back from sys.databases (#205).</summary>
/// <param name="CompatibilityLevel">e.g. 150. Travels with the backup, not the target.</param>
/// <param name="RecoveryModelDesc">FULL, SIMPLE or BULK_LOGGED, as the restore left it.</param>
/// <param name="Owner">Who owns it now - the login that ran the restore, not the original owner.</param>
public sealed record DatabaseOverview(int CompatibilityLevel, string RecoveryModelDesc, string? Owner)
{
    /// <summary>
    /// One sentence stating what arrived. Stated, not altered - none of these are wrong, they are
    /// simply facts about the source that people forget travel with a backup.
    /// </summary>
    public string Describe(string database) =>
        $"[{database}] arrived with compatibility level {CompatibilityLevel}, " +
        $"recovery model {RecoveryModelDesc}" +
        (Owner == null ? "." : $", owned by {Owner}.");
}

/// <summary>
/// A database user whose SID matches no login on this server (#205).
///
/// The single most common post-restore fault. SQL-auth users carry their SID inside the database,
/// and on a different server the same-named login has a different SID - so the app connects, the
/// login succeeds, and access to the database fails with "The server principal is not able to
/// access the database under the current security context."
/// </summary>
/// <param name="Name">The user, as named inside the database.</param>
/// <param name="HasSameNamedLogin">Whether a login of the same name exists to map onto.</param>
public sealed record OrphanedUser(string Name, bool HasSameNamedLogin);

/// <summary>
/// What finishes a restore, offered rather than performed (#205).
///
/// The recovery panel (#14) handles databases left in a BAD state. This is its counterpart for the
/// database that restored fine, because "restored" is not the end of the job: nobody has verified
/// the data, and on a different server every SQL-auth user is orphaned. Same shape as recovery -
/// the statement shown in full, run or copy, caution text - because that shape is the whole
/// contract: nothing runs that was not read first.
/// </summary>
public static class PostRestoreAdvice
{
    /// <summary>
    /// The verification everyone means to run and defers. Offered on every success, because the
    /// restore is the cheapest moment to find corruption - it proves the backup, not just the copy.
    /// </summary>
    public static RecoveryAction CheckDb(string database) => new(
        "Check the restored database (DBCC CHECKDB)",
        $"DBCC CHECKDB ({TSql.QuoteName(database)}) WITH NO_INFOMSGS",
        "Reads every allocation and page. Duration scales with the size of the database - minutes " +
        "on most, longer on very large ones - and it holds schema locks briefly as it goes. " +
        "No output means it found nothing wrong: the backup is proven, not just restored.");

    /// <summary>
    /// The fix for an orphaned user with a same-named login: remap the SID. This is the modern
    /// form of sp_change_users_login, which is deprecated and SQL-auth only.
    /// </summary>
    // The user names came FROM the restored database (#294) - quoted exactly, or a user
    // genuinely named [admin] would be re-attached as a different principal.
    public static RecoveryAction FixOrphan(string database, OrphanedUser user) => new(
        $"Re-attach user {user.Name} to the login of the same name",
        $"USE {TSql.QuoteName(database)}; " +
        $"ALTER USER {TSql.QuoteNameExact(user.Name)} WITH LOGIN = {TSql.QuoteNameExact(user.Name)}",
        "Points the database user at this server's login by rewriting its SID. The user keeps " +
        "every permission it had inside the database.");

    /// <summary>
    /// An orphan with nothing to map onto. No statement is offered, because inventing one means
    /// inventing a password - said plainly instead, with the statement to run once a login exists.
    /// </summary>
    public static RecoveryAction ExplainUnmappableOrphan(string database, OrphanedUser user) => new(
        $"User {user.Name} has no matching login on this server",
        $"-- After creating the login:\n" +
        $"-- CREATE LOGIN {TSql.CommentText(TSql.QuoteNameExact(user.Name))} WITH PASSWORD = '...';\n" +
        $"USE {TSql.QuoteName(database)}; " +
        $"ALTER USER {TSql.QuoteNameExact(user.Name)} WITH LOGIN = {TSql.QuoteNameExact(user.Name)}",
        "This server has no login of that name, so there is nothing to re-attach the user to. " +
        "Create the login first - with a password set by whoever owns that account, not invented " +
        "here - then the ALTER USER line maps the user onto it.",
        Runnable: false);

    /// <summary>
    /// The script that carries a login across from the instance the backup came from (#459).
    ///
    /// The honest gap in ExplainUnmappableOrphan: it says create the login first, with a password
    /// set by whoever owns the account, and leaves somebody holding a database whose users cannot
    /// log in. Inventing a password here would be wrong, but there is a third option it did not
    /// offer - go and fetch the real one.
    ///
    /// Run on the SOURCE, this emits a CREATE LOGIN carrying the original password HASH and the
    /// original SID. Both matter, and for different reasons:
    ///
    ///   - the hash means applications authenticate with the password they already have, and
    ///     nobody has to be told a new one
    ///   - the SID means the restored user is not orphaned AT ALL. Its SID already matches, so
    ///     there is no ALTER USER to run and no window where permissions are wrong
    ///
    /// Only SQL logins. A Windows login has no password here to carry and its SID comes from
    /// Active Directory, so the target recreates it with CREATE LOGIN ... FROM WINDOWS and the
    /// SIDs match by construction - the script says so rather than silently finding nothing.
    ///
    /// Nothing is executed by this app: it is read on one server and its OUTPUT is run on another,
    /// which is also why the result carries a real password hash and this app never sees it.
    /// </summary>
    public static RecoveryAction CaptureLoginFromSource(string loginName) => new(
        $"Copy the login {loginName} from the source instance",
        BuildLoginCaptureScript(loginName),
        "Run this on the instance the backup came FROM. It prints a CREATE LOGIN statement " +
        "carrying that login's real password hash and its original SID - run that output on this " +
        "server. Because the SID matches, the restored user stops being orphaned without an " +
        "ALTER USER, and because the hash matches, applications keep the password they already " +
        "have. Only SQL logins: a Windows login is recreated with CREATE LOGIN ... FROM WINDOWS, " +
        "which carries its own SID from Active Directory.",
        Runnable: false);

    /// <summary>
    /// The name is a LITERAL here, not an identifier - it is being compared against sys.sql_logins
    /// rather than naming an object - so it is escaped as one. QUOTENAME inside the script does
    /// the identifier quoting for the statement it emits, on the far side.
    /// </summary>
    internal static string BuildLoginCaptureScript(string loginName) =>
        $"""
        -- Run this on the instance the backup came FROM.
        -- It prints a CREATE LOGIN statement to run on the target; it changes nothing here.
        USE [master];
        GO

        DECLARE @LoginName sysname = N'{TSql.EscapeLiteral(loginName)}';
        DECLARE @Command nvarchar(max);

        IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = @LoginName)
        BEGIN
            THROW 50000, 'No SQL login of that name on this instance. A Windows login carries no password and takes its SID from Active Directory, so the target recreates it with CREATE LOGIN ... FROM WINDOWS instead.', 1;
        END;

        SELECT @Command =
               N'CREATE LOGIN ' + QUOTENAME(sl.name)
             + N' WITH PASSWORD = '
             + CONVERT(nvarchar(max),
                       CONVERT(varbinary(256),
                               LOGINPROPERTY(sl.name, 'PasswordHash')), 1)
             + N' HASHED'
             + N', SID = ' + CONVERT(nvarchar(max), sl.sid, 1)
             + N', DEFAULT_DATABASE = '
             + QUOTENAME(COALESCE(sl.default_database_name, N'master'))
             + N', DEFAULT_LANGUAGE = '
             + QUOTENAME(COALESCE(sl.default_language_name, N'us_english'))
             + N', CHECK_POLICY = '
             + CASE sl.is_policy_checked WHEN 1 THEN N'ON' ELSE N'OFF' END
             + N', CHECK_EXPIRATION = '
             + CASE sl.is_expiration_checked WHEN 1 THEN N'ON' ELSE N'OFF' END
             + N';'
             + CASE
                   WHEN sl.is_disabled = 1
                   THEN CHAR(13) + CHAR(10)
                      + N'ALTER LOGIN ' + QUOTENAME(sl.name) + N' DISABLE;'
                   ELSE N''
               END
        FROM sys.sql_logins AS sl
        WHERE sl.name = @LoginName;

        SELECT @Command AS [RunThisOnTheTarget];
        GO
        """;

    /// <summary>What the orphan scan concluded, in one line.</summary>
    public static string DescribeOrphans(IReadOnlyList<OrphanedUser> orphans) => orphans.Count switch
    {
        0 => "No orphaned users - every database user maps to a login on this server.",
        1 => "1 database user is orphaned - its SID matches no login on this server. " +
             "It will fail to access the database until it is re-attached.",
        var n => $"{n} database users are orphaned - their SIDs match no login on this server. " +
                 "They will fail to access the database until they are re-attached."
    };
}
