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
