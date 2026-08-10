using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The verb that touches an instance (#63 step 3), and therefore the verb built out of
/// refusals. Without --execute nothing runs: the script, the plan and every preflight verdict
/// print, and the exit code already says whether an --execute would have been allowed - so a
/// pipeline can rehearse its own restore step for free. WITH REPLACE is consented to by its
/// own flag, never by --force: --force overrides what the EVIDENCE says (version, TDE,
/// readability), not what the OPERATOR must say.
///
/// An executed run lands in the same history the app's History screen lists, and the same
/// webhooks hear about it - a restore at 3am from a runbook step is exactly the restore
/// somebody wants a Teams message about.
/// </summary>
internal static class RestoreVerb
{
    public static readonly VerbSpec Spec = new(
        "restore",
        "Generate, and with --execute run, a restore against a target server",
        "9lives restore (--container NAME | --server NAME) --database DB --target SERVER " +
        "[--at \"yyyy-MM-dd HH:mm:ss\"] [--target-database NAME] [--with-replace] [--norecovery] " +
        "[--stop-before-mark NAME | --stop-at-mark NAME] [--execute] [--force]",
        Valued: ["container", "server", "database", "at", "target", "target-database",
                 "stop-before-mark", "stop-at-mark"],
        Switches: ["execute", "with-replace", "norecovery", "force"],
        Options:
        [
            ("--container NAME", "A blob container configured in the app, as the source."),
            ("--server NAME", "A configured server's msdb history, as the source."),
            ("--database DB", "The database whose chain to restore. Required."),
            ("--target SERVER", "The configured server that RUNS the RESTORE. Required, " +
                "always explicit - a destructive act aims at nothing by default."),
            ("--at TIME", "The moment to restore to, by the same windowing rules the script " +
                "verb documents. Omitted, the newest reachable point."),
            ("--target-database NAME", "Restore AS this name. Defaults to the source name."),
            ("--with-replace", "Consent to overwrite an existing database. Never inherited " +
                "from anywhere, never implied by --force - this flag is the only way to " +
                "mean it."),
            ("--norecovery", "Leave the database restoring, ready for more log."),
            ("--stop-before-mark NAME", "Stop just before the named marked transaction."),
            ("--stop-at-mark NAME", "Stop at the mark, inclusive."),
            ("--execute", "Actually run it. Without this, the script and every preflight " +
                "verdict print and nothing is touched."),
            ("--force", "Override what the EVIDENCE says - version direction, missing TDE " +
                "certificate, unreadable files - loudly, as warnings. It cannot stand in " +
                "for --with-replace: evidence is overridable, consent is not.")
        ],
        Notes:
        [
            "Built out of refusals, in a ladder. First, nothing runs without --execute: the " +
            "bare invocation prints the script, the plan and every preflight verdict, and " +
            "its exit code already says whether an --execute would have been allowed - so a " +
            "pipeline can rehearse its own DR step nightly without touching anything.",
            "Second, the preflights - the same safety nets the app fires before WITH " +
            "REPLACE drops anything, asked of the target before the run: does the target " +
            "database already exist without --with-replace; can the target's service " +
            "account actually read every disk file; was the backup taken on a NEWER major " +
            "version than the target (error 3169, the one-directional law of RESTORE); and " +
            "is the TDE or backup-encryption certificate on the target, found by " +
            "thumbprint (error 33111 otherwise, named here before the worst morning finds " +
            "it). No verdict comes from silence: a header that cannot be read refuses " +
            "nothing, because refusing on a guess blocks legal restores.",
            "Executed runs leave the receipts the app leaves: an entry in the same History " +
            "the app's History screen lists - script, console log, outcome, duration - and " +
            "notifications to the same webhooks. A 3am restore from a runbook step looks " +
            "exactly like a clicked one afterwards.",
            "Files restore to the paths recorded in the backup. Relocation flags are not " +
            "built yet - the rehearse verb relocates automatically, and the app's restore " +
            "screen has full WITH MOVE control."
        ],
        ExitCodes:
        [
            "0   restored - or, without --execute, generated with nothing refused",
            "2   refused by a preflight, or the restore itself failed",
            "3   the source or target could not be reached at all",
            "64  usage"
        ],
        Examples:
        [
            ("9lives restore --container backups --database Sales --target SRV02",
                "generate only: script plus preflight verdicts, nothing touched"),
            ("9lives restore --container backups --database Sales --target SRV02 --with-replace --execute",
                "the real thing, consented to explicitly"),
            ("9lives restore --server SRV01 --database Sales --target SRV02 --at \"2026-08-02 19:00\" --execute",
                "a moment mid-log, STOPAT stamped on every log restore")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        if (args.Get("database") == null || args.Get("target") == null)
        {
            errors.WriteLine("restore needs --database and --target (the server that runs the " +
                             "RESTORE - a configured server name).");
            return ExitCodes.Usage;
        }

        var markBefore = args.Get("stop-before-mark");
        var markAt = args.Get("stop-at-mark");
        if (markBefore != null && markAt != null)
        {
            errors.WriteLine("Give one of --stop-before-mark and --stop-at-mark, not both.");
            return ExitCodes.Usage;
        }

        var mark = markBefore ?? markAt;
        if (mark != null && args.Get("at") != null)
        {
            errors.WriteLine("Give a mark or a time, not both - they are the same mechanism " +
                             "aimed differently.");
            return ExitCodes.Usage;
        }

        DateTime? at = null;
        if (args.Get("at") is { } atText)
        {
            at = CliArguments.ParseTime(atText);
            if (at == null)
            {
                errors.WriteLine($"Could not read '{atText}' as a time. Formats: " +
                                 "yyyy-MM-dd HH:mm:ss, yyyy-MM-dd HH:mm, yyyy-MM-dd.");
                return ExitCodes.Usage;
            }
        }

        var (target, targetError) = services.FindServer(args.Get("target")!);
        if (target == null)
        {
            errors.WriteLine(targetError);
            return ExitCodes.Usage;
        }

        var (sets, error) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            return ExitCodes.Usage;
        }

        var builder = new BackupChainBuilder();
        var points = builder.ComputeRestorePoints(sets);
        var (point, stopAt, chainError) = ScriptVerb.ChoosePoint(points, at);
        if (point == null)
        {
            errors.WriteLine(chainError);
            return ExitCodes.Failed;
        }

        var chain = builder.BuildChainFromRestorePoint(point);
        var sourceDatabase = args.Get("database")!;
        var targetDatabase = args.Get("target-database") ?? sourceDatabase;
        var withReplace = args.Has("with-replace");

        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = targetDatabase,
            WithReplace = withReplace,
            RecoveryMode = args.Has("norecovery") ? RecoveryMode.NoRecovery : RecoveryMode.Recovery,
            StopAt = stopAt,
            StopAtMark = mark,
            StopBeforeMark = markAt == null
        });

        // The preflights run whether or not this is an --execute: a generate-only invocation
        // that says "this WOULD be refused" is the pipeline's cheap rehearsal of its own DR step.
        var preflight = await Preflights.RunAsync(
            services, target, chain, targetDatabase, withReplace, args.Has("force"));

        errors.WriteLine($"Chain: {point.TypeDisplay} reaching " +
                         $"{(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss}, restoring " +
                         $"'{sourceDatabase}' as '{targetDatabase}' on {target.ServerName}.");
        foreach (var warning in preflight.Warnings)
            errors.WriteLine($"WARNING: {warning}");
        foreach (var refusal in preflight.Refusals)
            errors.WriteLine($"REFUSED: {refusal}");

        if (!args.Has("execute"))
        {
            output.WriteLine(script);
            errors.WriteLine(preflight.Refusals.Count > 0
                ? "Nothing was executed - and --execute would be refused for the reasons above."
                : "Nothing was executed. Add --execute to run this.");
            return preflight.Refusals.Count > 0 ? ExitCodes.Failed : ExitCodes.Ok;
        }

        if (preflight.Refusals.Count > 0)
        {
            errors.WriteLine("Not run. Every refusal above must be resolved - or, for the " +
                             "evidence-based ones, deliberately overridden with --force.");
            return ExitCodes.Failed;
        }

        return await ExecuteAsync(
            services, target, script, sourceDatabase, targetDatabase, point, stopAt,
            chain, args.Get("container"), errors);
    }

    private static async Task<int> ExecuteAsync(
        CliServices services, ServerConnection target, string script, string sourceDatabase,
        string targetDatabase, RestorePoint point, DateTime? stopAt, BackupChain chain,
        string? containerName, TextWriter errors)
    {
        var startedAt = DateTime.Now;
        var log = new System.Text.StringBuilder();

        void Progress(string message)
        {
            log.AppendLine(message);
            errors.WriteLine(message);
        }

        services.Notifier.Notify(new RunNotification(
            RunPhase.Started, "Restore", sourceDatabase, target.ServerName,
            $"Restoring as '{targetDatabase}' to " +
            $"{(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss}.", null));

        try
        {
            await services.Sql.ExecuteWithProgressAsync(target, script, Progress);

            var completedAt = DateTime.Now;
            services.History.Append(new RestoreHistoryEntry
            {
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = target.ServerName,
                TargetDatabase = targetDatabase,
                SourceDatabase = sourceDatabase,
                ContainerName = containerName,
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Outcome = RestoreOutcome.Succeeded,
                Script = script,
                Log = log.ToString()
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Succeeded, "Restore", sourceDatabase, target.ServerName,
                $"'{targetDatabase}' is restored and online.", completedAt - startedAt));

            errors.WriteLine($"Done: '{targetDatabase}' restored on {target.ServerName} in " +
                             $"{(completedAt - startedAt).TotalSeconds:0}s.");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.Now;
            log.AppendLine(ex.Message);
            services.History.Append(new RestoreHistoryEntry
            {
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = target.ServerName,
                TargetDatabase = targetDatabase,
                SourceDatabase = sourceDatabase,
                ContainerName = containerName,
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Outcome = RestoreOutcome.Failed,
                ErrorMessage = ex.Message,
                Script = script,
                Log = log.ToString()
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Restore", sourceDatabase, target.ServerName,
                ex.Message, completedAt - startedAt));

            errors.WriteLine($"FAILED: {ex.Message}");
            errors.WriteLine("The history entry records how far it got - check the target " +
                             "database's state before retrying.");
            return ExitCodes.Failed;
        }
    }
}
