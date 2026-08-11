using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The proof loop from a terminal (#63 step 3): restore the chain to a scratch database on the
/// target, prove the data with DBCC CHECKDB, drop the scratch copy - and leave the receipt in
/// the same history the app reads, so the exposure dashboard's Proven column and measured RTO
/// fill in from a scheduled task nobody watches. "Nightly DR verification" was this issue's
/// founding example, and this is that verb.
///
/// Safety by construction, same as the app's rehearsal (#238): a generated scratch name, never
/// WITH REPLACE, every file relocated, and the cleanup runs LAST so a failure retains the
/// evidence. The one database it can drop is the one it just created.
/// </summary>
internal static class RehearseVerb
{
    public static readonly VerbSpec Spec = new(
        "rehearse",
        "Prove a database restores: scratch restore + CHECKDB + drop, receipt in History",
        "9lives rehearse (--container NAME | --server NAME) --database DB --target SERVER " +
        "[--at \"yyyy-MM-dd HH:mm:ss\"] [--execute] [--json]",
        Valued: ["container", "server", "database", "at", "target"],
        Switches: ["json", "execute"],
        Options:
        [
            ("--container NAME", "A blob container configured in the app, as the source."),
            ("--server NAME", "A configured server's msdb history, as the source."),
            ("--database DB", "The database to PROVE. Required. The scratch copy is named " +
                "after it: MyDb becomes MyDb_rehearsal_20260810_0930."),
            ("--target SERVER", "The configured server the scratch restore runs on. " +
                "Required. Its default data and log paths receive the relocated files."),
            ("--at TIME", "Prove the chain to this moment. Omitted, the newest reachable " +
                "point - which is the honest answer to \"could I restore what I have NOW?\""),
            ("--execute", "Actually run it. Without this, the full rehearsal script prints " +
                "and nothing is touched."),
            ("--json", "The proof as data on stdout: outcome, chain, point, the measured " +
                "duration - the RTO number - and the history id. The artefact a pipeline " +
                "archives.")
        ],
        Notes:
        [
            "\"The only tested backup is a restored backup.\" Everyone repeats it; this verb " +
            "produces the evidence: restore the chain to a scratch database, prove the data " +
            "with DBCC CHECKDB, drop the scratch copy. The elapsed time is a MEASURED " +
            "restore time - the number RTO conversations are otherwise made up from.",
            "Safety by construction, same as the app's rehearsal: the scratch name is " +
            "generated and honest about what it is, the restore refuses if that name " +
            "somehow exists, WITH REPLACE never appears, every file is relocated to the " +
            "target's defaults so nothing collides with the real database, and the cleanup " +
            "runs LAST - a failed rehearsal retains the scratch copy as evidence to " +
            "inspect, then drop.",
            "The receipt lands in the same History the app reads, as a Rehearsal entry: " +
            "the exposure dashboard's Proven column and measured RTO fill in from it, and " +
            "webhook notifications name the database being proven, not the scratch copy. " +
            "Scheduled - Task Scheduler, a CI job, anything that can run an exe nightly - " +
            "this is proof that renews itself, with no SQL Agent required.",
            "The app can also wrap this same loop as a disabled SQL Agent job (Rehearsal " +
            "as Agent job, on the restore screen) for estates that prefer receipts in the " +
            "job history."
        ],
        ExitCodes:
        [
            "0   PROVEN - restored, CHECKDB clean, scratch dropped (or, without --execute, printed)",
            "2   NOT PROVEN - the failure is recorded, the scratch copy retained as evidence",
            "3   the source or target could not be reached, or FILELISTONLY answered nothing",
            "64  usage",
            "130 cancelled with Ctrl+C - recorded, told to the channel, scratch retained if begun"
        ],
        Examples:
        [
            ("9lives rehearse --container backups --database Sales --target SRV02 --execute",
                "prove Sales restores, right now, receipt in History"),
            ("9lives rehearse --server SRV01 --database Sales --target SRV02",
                "print the rehearsal script without running it")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors,
        CancellationToken ct = default)
    {
        if (args.Get("database") == null || args.Get("target") == null)
        {
            errors.WriteLine("rehearse needs --database and --target (the server the scratch " +
                             "restore runs on - a configured server name).");
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
        var scratch = RehearsalPlanner.ScratchName(sourceDatabase, DateTime.Now);

        // Relocation is not optional here: the scratch copy must never collide with the real
        // database's files, so every file moves to the target's defaults under the scratch name.
        var devices = chain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();
        var files = await services.Sql.RestoreFileListOnlyAsync(target, devices);
        if (files.Count == 0)
        {
            errors.WriteLine("FILELISTONLY returned nothing - the rehearsal cannot relocate " +
                             "files it cannot list.");
            return ExitCodes.CouldNotAnswer;
        }

        var (dataPath, logPath) = await services.Sql.GetDefaultPathsAsync(target);
        var moves = RehearsalPlanner.ScratchMoves(files, scratch, dataPath, logPath);

        var restoreScript = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = scratch,
            WithReplace = false,
            RecoveryMode = RecoveryMode.Recovery,
            StopAt = stopAt,
            FileMoves = moves
        });

        var script = RehearsalPlanner.BuildScript(restoreScript, scratch);

        errors.WriteLine($"Rehearsal: {point.TypeDisplay} reaching " +
                         $"{(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss}, proving " +
                         $"'{sourceDatabase}' as scratch '{scratch}' on {target.ServerName}.");

        var json = args.Has("json");

        if (!args.Has("execute"))
        {
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "rehearse",
                    Outcome = "Generated",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    Scratch = scratch,
                    Chain = point.TypeDisplay,
                    ProveTo = stopAt ?? point.Timestamp,
                    Script = script
                });
            else
                output.WriteLine(script);
            errors.WriteLine("Nothing was executed. Add --execute to run the rehearsal.");
            return ExitCodes.Ok;
        }

        var startedAt = DateTime.Now;
        var log = new System.Text.StringBuilder();

        void Progress(string message)
        {
            log.AppendLine(message);
            errors.WriteLine(message);
        }

        // The subject is the database being PROVEN, not the scratch copy it was proven on -
        // "Rehearsal MyDb_rehearsal_20260810" answers a question nobody asked.
        services.Notifier.Notify(new RunNotification(
            RunPhase.Started, "Rehearsal", sourceDatabase, target.ServerName,
            $"Proving the chain to {(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss} on a " +
            "scratch copy.", null));

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
                TargetDatabase = scratch,
                SourceDatabase = sourceDatabase,
                ContainerName = args.Get("container"),
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Kind = "Rehearsal",
                Outcome = RestoreOutcome.Succeeded,
                Script = script,
                Log = log.ToString()
            };
            services.History.Append(receipt);
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "rehearse",
                    Outcome = "Proven",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    Chain = point.TypeDisplay,
                    ProvenTo = stopAt ?? point.Timestamp,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    DurationSeconds = Math.Round((completedAt - startedAt).TotalSeconds, 1),
                    HistoryId = receipt.Id
                });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Succeeded, "Rehearsal", sourceDatabase, target.ServerName,
                "Restored, CHECKDB clean, scratch copy dropped. The receipt is in History.",
                completedAt - startedAt));

            errors.WriteLine($"PROVEN: '{sourceDatabase}' restores. Took " +
                             $"{(completedAt - startedAt).TotalSeconds:0}s - that is the " +
                             "measured RTO, and the receipt is in the app's History.");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C mid-rehearsal (#296): recorded as Cancelled, told to the channel, and
            // exited with 128+SIGINT - and the scratch copy note still applies, because the
            // interruption may have landed after its restore began.
            var completedAt = DateTime.Now;
            log.AppendLine("Cancelled from the terminal.");
            var receipt = new RestoreHistoryEntry
            {
                Origin = "CLI",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ServerName = target.ServerName,
                TargetDatabase = scratch,
                SourceDatabase = sourceDatabase,
                ContainerName = args.Get("container"),
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Kind = "Rehearsal",
                Outcome = RestoreOutcome.Cancelled,
                ErrorMessage = "Cancelled from the terminal.",
                Script = script,
                Log = log.ToString()
            };
            services.History.Append(receipt);
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "rehearse",
                    Outcome = "Cancelled",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    Scratch = scratch,
                    ScratchRetained = true,
                    HistoryId = receipt.Id,
                    Error = "Cancelled from the terminal."
                });
            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Rehearsal", sourceDatabase, target.ServerName,
                "Cancelled from the terminal.", completedAt - startedAt));
            await services.Notifier.DrainAsync(NotificationDrain);
            errors.WriteLine($"CANCELLED. If the scratch restore had begun, '{scratch}' is " +
                             "retained - inspect, then drop it.");
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
                TargetDatabase = scratch,
                SourceDatabase = sourceDatabase,
                ContainerName = args.Get("container"),
                RestorePointTimestamp = stopAt ?? point.Timestamp,
                ChainSummary = point.TypeDisplay,
                Kind = "Rehearsal",
                Outcome = RestoreOutcome.Failed,
                ErrorMessage = ex.Message,
                Script = script,
                Log = log.ToString()
            };
            services.History.Append(receipt);
            if (json)
                CliRunResult.Write(output, new
                {
                    Verb = "rehearse",
                    Outcome = "NotProven",
                    Server = target.ServerName,
                    Database = sourceDatabase,
                    Scratch = scratch,
                    ScratchRetained = true,
                    HistoryId = receipt.Id,
                    Error = ex.Message
                });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Rehearsal", sourceDatabase, target.ServerName,
                ex.Message, completedAt - startedAt));

            errors.WriteLine($"NOT PROVEN: {ex.Message}");
            errors.WriteLine($"The scratch copy '{scratch}' is retained as evidence when the " +
                             "failure happened after its restore began - inspect, then drop it.");
            await services.Notifier.DrainAsync(NotificationDrain);
            return ExitCodes.Failed;
        }
    }

    /// <summary>Long enough for a slow webhook, short enough that a dead one cannot hold the exit.</summary>
    private static readonly TimeSpan NotificationDrain = TimeSpan.FromSeconds(10);
}
