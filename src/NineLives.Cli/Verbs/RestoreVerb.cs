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
        "[--relocate | --data-path DIR [--log-path DIR]] " +
        "[--stop-before-mark NAME | --stop-at-mark NAME] [--execute] [--force] [--json]",
        Valued: ["container", "server", "database", "at", "target", "target-database",
                 "stop-before-mark", "stop-at-mark", "data-path", "log-path"],
        Switches: ["execute", "with-replace", "norecovery", "force", "relocate", "json"],
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
            ("--relocate", "MOVE every file to the target's default data and log " +
                "directories, keeping the original file names. The freshly provisioned VM " +
                "rarely has the source server's drive layout."),
            ("--data-path DIR", "MOVE data files into this directory (log files follow " +
                "--log-path, or the target's default log directory). Implies relocation."),
            ("--log-path DIR", "MOVE log files into this directory. Implies relocation; " +
                "data files follow --data-path, or the target's default data directory."),
            ("--stop-before-mark NAME", "Stop just before the named marked transaction."),
            ("--stop-at-mark NAME", "Stop at the mark, inclusive."),
            ("--execute", "Actually run it. Without this, the script and every preflight " +
                "verdict print and nothing is touched."),
            ("--force", "Override what the EVIDENCE says - version direction, missing TDE " +
                "certificate, unreadable files - loudly, as warnings. It cannot stand in " +
                "for --with-replace: evidence is overridable, consent is not."),
            ("--json", "The ending as data on stdout - outcome, chain, point, duration, " +
                "warnings, refusals, history id - in place of the script or beside the " +
                "prose, which stays on stderr. The run's result as an artefact.")
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
            "Files restore to the paths recorded in the backup unless relocation is " +
            "asked for. --relocate moves every file to the target's default data and log " +
            "directories keeping its original name; --data-path and --log-path place them " +
            "explicitly, mirroring the app's WITH MOVE control. Either way the disk-space " +
            "preflight judges the volumes the files actually LAND on - and a freshly " +
            "provisioned VM whose drives differ from the source server's is exactly where " +
            "the recorded paths would have failed mid-run."
        ],
        ExitCodes:
        [
            "0   restored - or, without --execute, generated with nothing refused",
            "2   refused by a preflight, or the restore itself failed",
            "3   the source or target could not be reached at all",
            "64  usage",
            "130 cancelled with Ctrl+C - the receipt says Cancelled, the channel was told"
        ],
        Examples:
        [
            ("9lives restore --container backups --database Sales --target SRV02",
                "generate only: script plus preflight verdicts, nothing touched"),
            ("9lives restore --container backups --database Sales --target SRV02 --with-replace --execute",
                "the real thing, consented to explicitly"),
            ("9lives restore --server SRV01 --database Sales --target SRV02 --at \"2026-08-02 19:00\" --execute",
                "a moment mid-log, STOPAT stamped on every log restore"),
            ("9lives restore --container backups --database Sales --target SRV02 --relocate --with-replace --execute",
                "the provisioning shape: files land in the new VM's own data and log " +
                "directories, whatever its drives are")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors,
        CancellationToken ct = default)
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

        var (sets, sourceContainer, error, loadExit) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            // The loader says which kind of failure this was (#370): a malformed
            // invocation exits 64, a source that holds nothing for this database
            // exits 2 - the finding, not a usage error.
            return loadExit;
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

        // Relocation (#299): the Terraform case. A freshly provisioned VM rarely has the
        // source server's drive layout, and the recorded paths fail mid-run with
        // directory-not-found - after WITH REPLACE has already dropped the target. The
        // rehearse verb has always relocated; the restore verb, the one the template
        // actually ends with, now can. Explicit directories win; either side not given
        // falls back to the target's own defaults.
        List<FileMoveOption>? moves = null;
        if (args.Has("relocate") || args.Get("data-path") != null || args.Get("log-path") != null)
        {
            var moveDevices = chain.FullSet.Files
                .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
                .ToList();

            List<FileMoveOption> logicalFiles;
            try
            {
                logicalFiles = await services.Sql.RestoreFileListOnlyAsync(target, moveDevices, ct);
            }
            catch (Exception ex)
            {
                errors.WriteLine($"Could not read the backup's file list from " +
                                 $"{target.ServerName}, which relocation needs: {ex.Message}");
                return ExitCodes.CouldNotAnswer;
            }

            var dataDir = args.Get("data-path");
            var logDir = args.Get("log-path");
            if (dataDir == null || logDir == null)
            {
                var (defaultData, defaultLog) = await services.Sql.GetDefaultPathsAsync(target, ct);
                dataDir ??= defaultData;
                logDir ??= defaultLog;
            }

            moves = RestoreRelocation.ToDirectories(logicalFiles, dataDir, logDir);
            errors.WriteLine($"Relocating {moves.Count} file(s): data to {dataDir}, log to {logDir}.");
        }

        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = targetDatabase,
            WithReplace = withReplace,
            RecoveryMode = args.Has("norecovery") ? RecoveryMode.NoRecovery : RecoveryMode.Recovery,
            StopAt = stopAt,
            StopAtMark = mark,
            StopBeforeMark = markAt == null,
            FileMoves = moves ?? [],
            // The bucket's region has to reach the statement, not just the listing (#361).
            // Null for a --server source, which found its backups through an instance's own
            // history and has no container to ask.
            S3Region = sourceContainer?.S3Region
        });

        // The preflights run whether or not this is an --execute: a generate-only invocation
        // that says "this WOULD be refused" is the pipeline's cheap rehearsal of its own DR step.
        // With relocation, the space check judges the volumes the files actually LAND on.
        var preflight = await Preflights.RunAsync(
            services, target, chain, targetDatabase, withReplace, args.Has("force"), moves,
            sourceContainer, line => errors.WriteLine(line));

        errors.WriteLine($"Chain: {point.TypeDisplay} reaching " +
                         $"{(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss}, restoring " +
                         $"'{sourceDatabase}' as '{targetDatabase}' on {target.ServerName}.");
        foreach (var warning in preflight.Warnings)
            errors.WriteLine($"WARNING: {warning}");
        foreach (var refusal in preflight.Refusals)
            errors.WriteLine($"REFUSED: {refusal}");

        var json = args.Has("json");

        if (!args.Has("execute"))
        {
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "restore",
                    Outcome = preflight.Refusals.Count > 0 ? "Refused" : "Generated",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    TargetDatabase = targetDatabase,
                    Chain = point.TypeDisplay,
                    RestoreTo = stopAt ?? point.Timestamp,
                    Warnings = preflight.Warnings,
                    Refusals = preflight.Refusals,
                    Script = script
                });
            else
                output.WriteLine(script);

            errors.WriteLine(preflight.Refusals.Count > 0
                ? "Nothing was executed - and --execute would be refused for the reasons above."
                : "Nothing was executed. Add --execute to run this.");
            return preflight.Refusals.Count > 0 ? ExitCodes.Failed : ExitCodes.Ok;
        }

        if (preflight.Refusals.Count > 0)
        {
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "restore",
                    Outcome = "Refused",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    TargetDatabase = targetDatabase,
                    Warnings = preflight.Warnings,
                    Refusals = preflight.Refusals
                });
            errors.WriteLine("Not run. Every refusal above must be resolved - or, for the " +
                             "evidence-based ones, deliberately overridden with --force.");
            return ExitCodes.Failed;
        }

        return await ExecuteAsync(
            services, target, script, sourceDatabase, targetDatabase, point, stopAt,
            chain, args.Get("container"), json ? output : null, errors, ct);
    }

    private static async Task<int> ExecuteAsync(
        CliServices services, ServerConnection target, string script, string sourceDatabase,
        string targetDatabase, RestorePoint point, DateTime? stopAt, BackupChain chain,
        string? containerName, TextWriter? jsonOut, TextWriter errors, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var log = new System.Text.StringBuilder();

        // The run's ending as data (#303), when asked for - stdout carries only this.
        void EmitResult(string outcome, DateTime completedAt, string historyId, string? error = null)
        {
            if (jsonOut == null) return;
            CliRunResult.Write(jsonOut, new
            {
                Verb = "restore",
                Outcome = outcome,
                Server = target.ServerName,
                Database = sourceDatabase,
                TargetDatabase = targetDatabase,
                Chain = point.TypeDisplay,
                RestoredTo = stopAt ?? point.Timestamp,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationSeconds = Math.Round((completedAt - startedAt).TotalSeconds, 1),
                HistoryId = historyId,
                Error = error
            });
        }

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
            await services.Sql.ExecuteWithProgressAsync(target, script, Progress, ct);

            var completedAt = DateTime.Now;
            var receipt = new RestoreHistoryEntry
            {
                Origin = "CLI",
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
            };
            services.History.Append(receipt);
            EmitResult("Succeeded", completedAt, receipt.Id);

            services.Notifier.Notify(new RunNotification(
                RunPhase.Succeeded, "Restore", sourceDatabase, target.ServerName,
                $"'{targetDatabase}' is restored and online.", completedAt - startedAt));

            errors.WriteLine($"Done: '{targetDatabase}' restored on {target.ServerName} in " +
                             $"{(completedAt - startedAt).TotalSeconds:0}s.");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C mid-restore (#296): the process is ending, but the story must not - the
            // receipt says Cancelled, the channel hears about it, and the exit code is the
            // conventional 128+SIGINT so a wrapping script can tell interruption from failure.
            var completedAt = DateTime.Now;
            log.AppendLine("Cancelled from the terminal.");
            var receipt = new RestoreHistoryEntry
            {
                Origin = "CLI",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = target.ServerName,
                TargetDatabase = targetDatabase,
                SourceDatabase = sourceDatabase,
                ContainerName = containerName,
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Outcome = RestoreOutcome.Cancelled,
                ErrorMessage = "Cancelled from the terminal.",
                Script = script,
                Log = log.ToString()
            };
            services.History.Append(receipt);
            EmitResult("Cancelled", completedAt, receipt.Id, "Cancelled from the terminal.");
            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Restore", sourceDatabase, target.ServerName,
                "Cancelled from the terminal - check the target database's state before " +
                "retrying.", completedAt - startedAt));
            await services.Notifier.DrainAsync(NotificationDrain);
            errors.WriteLine("CANCELLED. The history entry records how far it got - check " +
                             "the target database's state before retrying.");
            return ExitCodes.Interrupted;
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.Now;
            log.AppendLine(ex.Message);
            var receipt = new RestoreHistoryEntry
            {
                Origin = "CLI",
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
            };
            services.History.Append(receipt);
            EmitResult("Failed", completedAt, receipt.Id, ex.Message);

            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Restore", sourceDatabase, target.ServerName,
                ex.Message, completedAt - startedAt));

            errors.WriteLine($"FAILED: {ex.Message}");
            errors.WriteLine("The history entry records how far it got - check the target " +
                             "database's state before retrying.");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Failed;
        }
    }

    /// <summary>Long enough for a slow webhook, short enough that a dead one cannot hold the exit.</summary>
    private static readonly TimeSpan NotificationDrain = TimeSpan.FromSeconds(10);
}
