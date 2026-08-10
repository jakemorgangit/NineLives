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
    /// Restoring to a moment rather than to a backup. Every mode: the modes narrow which SCREENS
    /// exist, not which restore options do - a Basic user restoring to just-before-the-mistake is
    /// the app's founding scenario, not an advanced one.
    /// </summary>
    public static bool CanRestoreToAPointInTime(AppMode mode) => true;

    /// <summary>
    /// Relocating the database files (WITH MOVE). Every mode, decisively: it is needed whenever
    /// the target's layout differs from the source's, which is most restores onto a DIFFERENT
    /// server - the Basic scenario itself. Hiding it made Basic restores fail with directory
    /// errors that Standard would not have hit.
    /// </summary>
    public static bool CanRelocateFiles(AppMode mode) => true;

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

    /// <summary>
    /// The less common RESTORE options - KEEP_REPLICATION, the broker flags, CHECKSUM. Every
    /// mode, same ruling as MOVE: restore OPTIONS are the trade of the restore screen, and the
    /// modes gate screens and machinery, not the statement somebody can write.
    /// </summary>
    public static bool CanUseAdvancedRestoreOptions(AppMode mode) => true;

    // ── what a mode is, in words ────────────────────────────────────────────────

    /// <summary>
    /// Named for the WORK, not for a rank.
    ///
    /// Basic/Standard/Pro reads as a price list, which is the wrong idea twice over: nothing here
    /// is bought, and nothing is withheld. The enum keeps those names because they are already in
    /// everybody's config.json and renaming them would reset the setting - what changes is what
    /// anybody is shown (#191).
    /// </summary>
    public static string Title(AppMode mode) => mode switch
    {
        AppMode.Basic => "Restore only",
        AppMode.Standard => "Back up and restore",
        _ => "Everything"
    };

    /// <summary>One line on what is on SCREEN - not on what the mode is worth.</summary>
    public static string Tagline(AppMode mode) => mode switch
    {
        AppMode.Basic => "The restore screen, and nothing else.",
        AppMode.Standard => "Restoring, plus taking the backups yourself.",
        _ => "Every screen, including the checks."
    };

    /// <summary>
    /// What is on screen. Short lines, because a card nobody reads is a gate.
    ///
    /// The wider modes say what they ADD rather than "everything in Basic, plus" - the ladder
    /// phrasing is what made three views of one app read as three tiers of a product.
    /// </summary>
    public static IReadOnlyList<string> Highlights(AppMode mode) => mode switch
    {
        AppMode.Basic =>
        [
            "Pick a container, pick a restore point, restore",
            "Every restore option - a moment in time, relocated files, the lot",
            "Nothing else on screen"
        ],

        AppMode.Standard =>
        [
            "Taking a backup, to blob or a file share",
            "Restoring from a path both servers can see",
            "Browsing backups, and the exposure dashboard"
        ],

        _ =>
        [
            "Copying a database onto another server",
            "Verifying and auditing backups against their headers",
            "Server-side credentials, and handing a restore to an Agent job"
        ]
    };

    /// <summary>
    /// What the list underneath is. Said once, above it, rather than "Adds:" on every line - which
    /// is what the phrasing had degenerated into once the "everything in Basic" ladder came out.
    /// </summary>
    public static string HighlightsLabel(AppMode mode) =>
        mode == AppMode.Basic ? "On screen" : "Adds";

    /// <summary>
    /// Which one to pick, in terms of the job rather than the person.
    ///
    /// It used to say "you want every check the tool can make", which quietly makes the narrow
    /// modes the choice of somebody who wants less - and nobody picks that about themselves.
    /// </summary>
    public static string WhoFor(AppMode mode) => mode switch
    {
        AppMode.Basic => "Pick this if someone else looks after the backups.",
        AppMode.Standard => "Pick this if the backups are yours too.",
        _ => "Pick this if you would rather have everything to hand."
    };
}
