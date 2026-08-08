namespace Blackcat.NineLives.Models;

/// <summary>
/// How much of the app to show (#176).
///
/// The problem this solves is growth. Nine Lives started as "restore a database from a blob
/// container" and is now a backup and restore orchestrator across two media, with a
/// copy-between-servers action, a header audit, chain validation, point-in-time recovery, MOVE,
/// tail-log handling and a credential panel - all of it on screen for everybody, all the time.
///
/// Somebody whose entire job is "restore last night's full onto the test box" should not have to
/// read past four collapsible steps and seven sidebar entries to do it. Growth made the app more
/// capable and less approachable, and those do not have to be a trade.
///
/// A mode rather than an "advanced options" toggle, deliberately: a collapsed section still tells
/// you there is something you are not using. A mode says the app is smaller today and means it.
/// </summary>
public enum AppMode
{
    /// <summary>
    /// Restore a database from a blob container. What the app originally did, and what most people
    /// want most of the time.
    /// </summary>
    Basic = 0,

    /// <summary>Adds the second medium, taking backups, point-in-time recovery and file relocation.</summary>
    Standard = 1,

    /// <summary>Everything.</summary>
    Pro = 2
}

/// <summary>
/// What each mode turns on.
///
/// One place that answers it, rather than a switch at every call site - a feature that appears in
/// Basic's navigation and then finds its screen half-hidden is worse than either.
///
/// The honest test for each line below is "does somebody who has never heard of this need to see it
/// to do their job?". Where the answer was unclear the feature went in the LOWER tier, because a
/// mode that hides something people need teaches them to switch to Pro and never look back - which
/// would leave the whole idea doing nothing.
/// </summary>
public static class AppModeCapabilities
{
    // ── screens ─────────────────────────────────────────────────────────────────

    /// <summary>Taking a backup at all.</summary>
    public static bool CanBackUp(AppMode mode) => mode >= AppMode.Standard;

    /// <summary>Copying a database between servers - two servers, one of them overwritten.</summary>
    public static bool CanCopyBetweenServers(AppMode mode) => mode >= AppMode.Pro;

    /// <summary>Browsing a container without restoring from it.</summary>
    public static bool CanBrowseBackups(AppMode mode) => mode >= AppMode.Standard;

    // ── the restore screen ──────────────────────────────────────────────────────

    /// <summary>
    /// Restoring from a path both servers can see.
    ///
    /// Standard rather than Pro: an estate that backs up to a file share is not an advanced user,
    /// it is a different ordinary arrangement - and in Basic there is nothing to choose, so the
    /// medium selector does not appear at all.
    /// </summary>
    public static bool CanUseSharedPath(AppMode mode) => mode >= AppMode.Standard;

    /// <summary>
    /// Restoring to a moment rather than to a backup.
    ///
    /// Standard, and it was the hardest call here. Point-in-time is close to the heart of what this
    /// app is for, but it is also the thing somebody restoring last night's full will never touch -
    /// and STOPAT with nothing selected is a checkbox that does nothing, which is its own kind of
    /// clutter.
    /// </summary>
    public static bool CanRestoreToAPointInTime(AppMode mode) => mode >= AppMode.Standard;

    /// <summary>Relocating the database files. Needed whenever the target's layout differs.</summary>
    public static bool CanRelocateFiles(AppMode mode) => mode >= AppMode.Standard;

    /// <summary>
    /// The chain checks, the VERIFYONLY pass and the header audit.
    ///
    /// Pro: every one of them answers a question somebody has to know to ask. They are also the
    /// features that cost minutes and reach across the network, and offering those to somebody who
    /// has not asked for them is how an app earns a reputation for being slow.
    /// </summary>
    public static bool CanVerifyAndAudit(AppMode mode) => mode >= AppMode.Pro;

    /// <summary>
    /// The server-side credential panel.
    ///
    /// Pro, because Basic and Standard can present connecting as one step rather than four things
    /// that each need it. The credential still has to exist for a restore to work - what changes is
    /// whether the app makes that somebody's problem before they have hit it.
    /// </summary>
    public static bool CanManageServerCredentials(AppMode mode) => mode >= AppMode.Pro;

    /// <summary>Handing a restore over as an Agent job.</summary>
    public static bool CanScriptAsAgentJob(AppMode mode) => mode >= AppMode.Pro;

    /// <summary>The less common RESTORE options - KEEP_REPLICATION, the broker flags, CHECKSUM.</summary>
    public static bool CanUseAdvancedRestoreOptions(AppMode mode) => mode >= AppMode.Pro;

    // ── what a mode is, in words ────────────────────────────────────────────────

    public static string Title(AppMode mode) => mode switch
    {
        AppMode.Basic => "Basic",
        AppMode.Standard => "Standard",
        _ => "Pro"
    };

    public static string Tagline(AppMode mode) => mode switch
    {
        AppMode.Basic => "Restore a database from a blob container.",
        AppMode.Standard => "Back up and restore, to blob or a file share, with point-in-time recovery.",
        _ => "Everything, including copying between servers and auditing backups."
    };

    /// <summary>What it turns on, for the card. Short lines, because a card nobody reads is a gate.</summary>
    public static IReadOnlyList<string> Highlights(AppMode mode) => mode switch
    {
        AppMode.Basic =>
        [
            "Pick a container, pick a restore point, restore",
            "The full restore chain worked out for you",
            "Nothing else on screen"
        ],

        AppMode.Standard =>
        [
            "Everything in Basic",
            "Back up a database, to blob or a file share",
            "Restore from a path both servers can see",
            "Restore to a moment in time, and relocate files"
        ],

        _ =>
        [
            "Everything in Standard",
            "Copy a database onto another server in one action",
            "Verify and audit backups against their own headers",
            "Manage server-side credentials, and script as an Agent job"
        ]
    };

    /// <summary>
    /// Who it is for. Named rather than described, because somebody choosing between three cards on
    /// first run is asking "which one am I?" rather than "what does each contain?".
    /// </summary>
    public static string WhoFor(AppMode mode) => mode switch
    {
        AppMode.Basic => "You need last night's backup on another server, and nothing more.",
        AppMode.Standard => "You look after the backups as well as the restores.",
        _ => "You want every check the tool can make, and are happy to be asked."
    };
}
