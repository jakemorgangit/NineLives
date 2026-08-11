using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The other half of the orchestrator (#300). The CLI could restore, rehearse and judge but
/// not take a backup - and the scriptable case is everywhere: the pre-change safety snapshot
/// ("backup before the deploy; restore if the smoke test fails" - the full loop in one tool),
/// the copy-away before risky maintenance, the nightly extra full to a second container.
///
/// The app's rules travel: COPY_ONLY by default and LOUD when disabled, destinations built by
/// the same BackupDestinationBuilder so what this writes is what every browser and restore
/// can find, generate-only without --execute, a history receipt and webhooks like the other
/// execution verbs.
/// </summary>
internal static class BackupVerb
{
    public static readonly VerbSpec Spec = new(
        "backup",
        "Generate, and with --execute run, a backup of a database",
        "9lives backup (--container NAME | --path DIR) --server NAME --database DB " +
        "[--type full|diff|log] [--stripes N] [--not-copy-only] [--execute]",
        Valued: ["container", "path", "server", "database", "type", "stripes"],
        Switches: ["execute", "not-copy-only"],
        Options:
        [
            ("--container NAME", "A blob container configured in the app, as the destination. " +
                "The blob layout follows the container's own path pattern, so every browser " +
                "and restore finds what this writes."),
            ("--path DIR", "A directory (usually a share) as the destination - one folder " +
                "per database, timestamped names, exactly as the app writes them."),
            ("--server NAME", "The configured server that TAKES the backup. Required."),
            ("--database DB", "The database to back up. Required."),
            ("--type TYPE", "full (the default), diff or log. A log backup requires FULL " +
                "recovery and an existing base; a differential requires a full to be based on."),
            ("--stripes N", "Write N striped files, 1-64. Faster on large databases, and the " +
                "only way past Azure's per-blob size limit."),
            ("--not-copy-only", "Take a PLAIN full instead of the default COPY_ONLY. A plain " +
                "full RESETS THE DIFFERENTIAL BASE on the source: every differential the " +
                "schedule takes afterwards depends on this file. A real choice, said loudly."),
            ("--execute", "Actually run it. Without this, the script prints and nothing is " +
                "touched.")
        ],
        Notes:
        [
            "COPY_ONLY is the default, exactly as it is in the app: a plain full backup " +
            "resets the differential base on the source, silently re-basing every " +
            "differential a schedule takes afterwards onto a file that lives in this " +
            "container and that whoever runs the restore has never heard of. Disabling it " +
            "with --not-copy-only prints the warning to stderr AND into the generated " +
            "script's header, so the person reading the script sees what the person running " +
            "the command was told.",
            "Destinations are built by the same layout rules the app uses - the container's " +
            "path pattern for blob, one folder per database on a share - so what this " +
            "writes is exactly what 9lives list, the app's browser and every restore can " +
            "find and classify, copy-only marked in the file name included.",
            "BACKUP TO URL needs the container's credential on the SOURCE instance; the " +
            "same preflight the app runs asks before the first statement, so the run cannot " +
            "die of Msg 3201 after consent.",
            "An executed run leaves the same receipts as the other execution verbs: a " +
            "history entry the app's History screen lists, and notifications to the " +
            "configured webhooks."
        ],
        ExitCodes:
        [
            "0   backed up - or, without --execute, generated with nothing refused",
            "2   refused by the credential preflight, or the backup itself failed",
            "3   the server could not be reached at all",
            "64  usage",
            "130 cancelled with Ctrl+C - the receipt says Cancelled, the channel was told"
        ],
        Examples:
        [
            ("9lives backup --container backups --server SRV01 --database Sales --execute",
                "a copy-only full into the container, found by every browser afterwards"),
            ("9lives backup --path \\\\nas01\\sql --server SRV01 --database Sales --type log --execute",
                "a log backup onto the share - the pre-change tail of the safety loop"),
            ("9lives backup --container backups --server SRV01 --database Sales --stripes 4 --execute",
                "four stripes, for the database too large for one blob")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors,
        CancellationToken ct = default)
    {
        if (args.Get("server") == null || args.Get("database") == null)
        {
            errors.WriteLine("backup needs --server (the instance that takes it) and --database.");
            return ExitCodes.Usage;
        }

        var containerName = args.Get("container");
        var path = args.Get("path");
        if ((containerName == null) == (path == null))
        {
            errors.WriteLine("Give exactly one destination: --container NAME or --path DIR.");
            return ExitCodes.Usage;
        }

        var type = (args.Get("type") ?? "full").ToLowerInvariant() switch
        {
            "full" => BackupType.Full,
            "diff" or "differential" => BackupType.Differential,
            "log" => BackupType.TransactionLog,
            _ => (BackupType?)null
        };
        if (type == null)
        {
            errors.WriteLine($"Unknown --type '{args.Get("type")}'. One of: full, diff, log.");
            return ExitCodes.Usage;
        }

        var stripes = 1;
        if (args.Get("stripes") is { } stripesText)
        {
            if (!int.TryParse(stripesText, out stripes) || stripes < 1 || stripes > 64)
            {
                errors.WriteLine("--stripes takes a number from 1 to 64 - SQL Server's own " +
                                 "device limit.");
                return ExitCodes.Usage;
            }
        }

        var (server, serverError) = services.FindServer(args.Get("server")!);
        if (server == null)
        {
            errors.WriteLine(serverError);
            return ExitCodes.Usage;
        }

        BlobContainerConfig? container = null;
        if (containerName != null)
        {
            var (found, containerError) = services.FindContainer(containerName);
            if (found == null)
            {
                errors.WriteLine(containerError);
                return ExitCodes.Usage;
            }
            container = found;
        }

        var database = args.Get("database")!;

        // COPY_ONLY is the default and turning it off is LOUD (#300): the consequence lands
        // on the SOURCE's differential schedule, not on this run.
        var copyOnly = !args.Has("not-copy-only");
        if (!copyOnly && type == BackupType.Full)
            errors.WriteLine("WARNING: NOT copy-only. This full RESETS THE DIFFERENTIAL BASE " +
                             $"on {server.ServerName} - every differential the schedule takes " +
                             "afterwards will be based on this file. The script's header says " +
                             "the same.");

        var takenAt = DateTime.Now;
        var destinations = container != null
            ? BackupDestinationBuilder.ForContainer(
                container, server.ServerName, database, type.Value, takenAt, stripes, copyOnly)
            : BackupDestinationBuilder.ForSharedPath(
                path!, database, type.Value, takenAt, stripes, copyOnly);

        var script = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = database,
            Medium = container != null ? BackupMedium.AzureBlob : BackupMedium.SharedPath,
            Destinations = destinations,
            Type = type.Value,
            CopyOnly = copyOnly
        });

        // BACKUP TO URL needs the credential on the SOURCE instance (#284's preflight, the
        // same one the app runs) - asked before the first statement, so the run cannot die
        // of Msg 3201 after consent.
        if (container != null)
        {
            var preflight = await BlobCredentialPreflight.EnsureAsync(
                services.Store, services.Sql, null, container, server, line => errors.WriteLine(line));
            if (!preflight.CanProceed)
            {
                errors.WriteLine($"REFUSED: {preflight.Refusal}");
                if (!args.Has("execute")) output.WriteLine(script);
                return ExitCodes.Failed;
            }
        }

        errors.WriteLine($"Backup: {type.Value} of '{database}' on {server.ServerName}, " +
                         $"{destinations.Count} file(s), " +
                         (copyOnly && type != BackupType.Differential ? "copy-only." : "plain."));

        if (!args.Has("execute"))
        {
            output.WriteLine(script);
            errors.WriteLine("Nothing was executed. Add --execute to run this.");
            return ExitCodes.Ok;
        }

        return await ExecuteAsync(services, server, script, database, type.Value, errors, ct);
    }

    private static async Task<int> ExecuteAsync(
        CliServices services, ServerConnection server, string script, string database,
        BackupType type, TextWriter errors, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var log = new System.Text.StringBuilder();

        void Progress(string message)
        {
            log.AppendLine(message);
            errors.WriteLine(message);
        }

        services.Notifier.Notify(new RunNotification(
            RunPhase.Started, "Backup", database, server.ServerName, $"{type} backup.", null));

        try
        {
            await services.Sql.ExecuteWithProgressAsync(server, script, Progress, ct);

            var completedAt = DateTime.Now;
            services.History.Append(new RestoreHistoryEntry
            {
                Kind = "Backup",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = server.ServerName,
                TargetDatabase = database,
                SourceDatabase = database,
                ChainSummary = $"{type} backup",
                Outcome = RestoreOutcome.Succeeded,
                Script = script,
                Log = log.ToString()
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Succeeded, "Backup", database, server.ServerName,
                $"{type} backup complete.", completedAt - startedAt));

            errors.WriteLine($"Done: {type} backup of '{database}' on {server.ServerName} in " +
                             $"{(completedAt - startedAt).TotalSeconds:0}s.");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            var completedAt = DateTime.Now;
            log.AppendLine("Cancelled from the terminal.");
            services.History.Append(new RestoreHistoryEntry
            {
                Kind = "Backup",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = server.ServerName,
                TargetDatabase = database,
                SourceDatabase = database,
                ChainSummary = $"{type} backup",
                Outcome = RestoreOutcome.Cancelled,
                ErrorMessage = "Cancelled from the terminal.",
                Script = script,
                Log = log.ToString()
            });
            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Backup", database, server.ServerName,
                "Cancelled - any file already written is incomplete and cannot be restored " +
                "from.", completedAt - startedAt));
            await services.Notifier.DrainAsync(NotificationDrain);
            errors.WriteLine("CANCELLED. Any file already written is incomplete and cannot be " +
                             "restored from.");
            return ExitCodes.Interrupted;
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.Now;
            log.AppendLine(ex.Message);
            services.History.Append(new RestoreHistoryEntry
            {
                Kind = "Backup",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = server.ServerName,
                TargetDatabase = database,
                SourceDatabase = database,
                ChainSummary = $"{type} backup",
                Outcome = RestoreOutcome.Failed,
                ErrorMessage = ex.Message,
                Script = script,
                Log = log.ToString()
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Backup", database, server.ServerName,
                ex.Message, completedAt - startedAt));

            errors.WriteLine($"FAILED: {ex.Message}");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Failed;
        }
    }

    /// <summary>Long enough for a slow webhook, short enough that a dead one cannot hold the exit.</summary>
    private static readonly TimeSpan NotificationDrain = TimeSpan.FromSeconds(10);
}
