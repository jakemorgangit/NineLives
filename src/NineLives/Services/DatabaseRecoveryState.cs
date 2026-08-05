namespace Blackcat.NineLives.Services;

/// <summary>
/// What a restore left behind, and what to do about it (#14).
///
/// When a chain fails partway through, the target database is still mid-restore - and if
/// "Disconnect sessions" was on it is also stuck in SINGLE_USER, because the closing
/// ALTER DATABASE ... SET MULTI_USER never ran. Both states block other connections.
///
/// This is the worst possible moment to leave someone guessing: a restore has just failed during
/// an incident, and the database is now in a state that keeps everyone else out. A DBA who knows
/// the remedy will type it into SSMS from memory. The people this tool is aimed at may not, and
/// the app is already holding the connection that could fix it.
/// </summary>
/// <param name="Exists">False when the database is not on the server at all.</param>
/// <param name="StateDescription">sys.databases.state_desc, e.g. ONLINE, RESTORING, RECOVERY_PENDING.</param>
/// <param name="UserAccessDescription">sys.databases.user_access_desc, e.g. MULTI_USER, SINGLE_USER.</param>
public sealed record DatabaseRecoveryState(
    bool Exists,
    string? StateDescription,
    string? UserAccessDescription)
{
    public static DatabaseRecoveryState Missing => new(false, null, null);

    /// <summary>Mid-restore: the database is not usable until it is recovered or restored further.</summary>
    public bool IsRestoring =>
        string.Equals(StateDescription, "RESTORING", StringComparison.OrdinalIgnoreCase);

    /// <summary>Restore ended badly enough that SQL Server could not bring the database up.</summary>
    public bool IsRecoveryPending =>
        string.Equals(StateDescription, "RECOVERY_PENDING", StringComparison.OrdinalIgnoreCase);

    /// <summary>Locked to one connection - usually a leftover from Disconnect sessions.</summary>
    public bool IsSingleUser =>
        string.Equals(UserAccessDescription, "SINGLE_USER", StringComparison.OrdinalIgnoreCase);

    public bool NeedsAttention => Exists && (IsRestoring || IsRecoveryPending || IsSingleUser);

    /// <summary>
    /// Plain-language account of where the database is, written for someone reading it just after
    /// a restore failed rather than someone browsing documentation.
    /// </summary>
    public string Explain(string databaseName)
    {
        if (!Exists)
            return $"[{databaseName}] is not on the server. The restore failed before it was created, " +
                   "so nothing has been left behind.";

        var lines = new List<string>();

        if (IsRestoring)
            lines.Add(
                $"[{databaseName}] is in RESTORING state. That is what a database looks like " +
                "part-way through a chain: it is waiting for more log or differential backups and " +
                "cannot be used until it is either restored further or brought online as-is.");

        if (IsRecoveryPending)
            lines.Add(
                $"[{databaseName}] is in RECOVERY_PENDING. SQL Server could not finish recovery - " +
                "usually the restore was interrupted. It will need attention before the database " +
                "can be used.");

        if (IsSingleUser)
            lines.Add(
                $"[{databaseName}] is in SINGLE_USER. \"Disconnect sessions\" set that before the " +
                "restore, and the statement that puts it back never ran because the chain stopped " +
                "first. Only one connection can reach the database until it is changed back.");

        if (lines.Count == 0)
            lines.Add($"[{databaseName}] is {StateDescription} / {UserAccessDescription}.");

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// The statements that would put this right, in the order they should be run. Empty when there
    /// is nothing to do.
    ///
    /// WITH RECOVERY is offered rather than performed silently: it ends the restore sequence, so
    /// any log backup not yet applied can never be applied afterwards. Whether that is the right
    /// move depends on what the user was trying to reach, and only they know that.
    /// </summary>
    public IReadOnlyList<RecoveryAction> SuggestedActions(string databaseName)
    {
        var actions = new List<RecoveryAction>();
        if (!Exists) return actions;

        if (IsRestoring || IsRecoveryPending)
            actions.Add(new RecoveryAction(
                "Bring the database online",
                $"RESTORE DATABASE {TSql.QuoteName(databaseName)} WITH RECOVERY",
                "Finishes the restore with what has already been applied. Any remaining log " +
                "backups cannot be applied after this - if you meant to reach a later point in " +
                "time, fix the chain and restore again instead."));

        if (IsSingleUser)
            actions.Add(new RecoveryAction(
                "Allow other connections again",
                $"ALTER DATABASE {TSql.QuoteName(databaseName)} SET MULTI_USER",
                "Undoes the SINGLE_USER that Disconnect sessions applied. Safe at any point."));

        return actions;
    }
}

/// <summary>One remediation the user can read, copy, or run.</summary>
/// <param name="Title">Short label for the button.</param>
/// <param name="Sql">The exact statement - shown before it is run, never hidden.</param>
/// <param name="Caution">What running it commits them to.</param>
public sealed record RecoveryAction(string Title, string Sql, string Caution);
