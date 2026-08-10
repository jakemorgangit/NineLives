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
        "[--at \"yyyy-MM-dd HH:mm:ss\"] [--execute]",
        Valued: ["container", "server", "database", "at", "target"],
        Switches: ["execute"]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
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

        if (!args.Has("execute"))
        {
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
            await services.Sql.ExecuteWithProgressAsync(target, script, Progress);

            var completedAt = DateTime.Now;
            services.History.Append(new RestoreHistoryEntry
            {
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
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Succeeded, "Rehearsal", sourceDatabase, target.ServerName,
                "Restored, CHECKDB clean, scratch copy dropped. The receipt is in History.",
                completedAt - startedAt));

            errors.WriteLine($"PROVEN: '{sourceDatabase}' restores. Took " +
                             $"{(completedAt - startedAt).TotalSeconds:0}s - that is the " +
                             "measured RTO, and the receipt is in the app's History.");
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
            });

            services.Notifier.Notify(new RunNotification(
                RunPhase.Problem, "Rehearsal", sourceDatabase, target.ServerName,
                ex.Message, completedAt - startedAt));

            errors.WriteLine($"NOT PROVEN: {ex.Message}");
            errors.WriteLine($"The scratch copy '{scratch}' is retained as evidence when the " +
                             "failure happened after its restore began - inspect, then drop it.");
            return ExitCodes.Failed;
        }
    }
}
