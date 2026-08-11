using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The validated restore script for a moment, to stdout - and ONLY the script to stdout, so
/// it pipes and redirects cleanly; everything human goes to stderr. The same chain calculation
/// and the same generator the GUI uses, which is the whole reason this emits a script that
/// actually runs rather than a template that nearly does (#63).
///
/// Deliberately generation-only: nothing here touches an instance. Executing arrives in a
/// later step behind its own explicit flags, as the issue's safety defaults demand.
/// </summary>
internal static class ScriptVerb
{
    public static readonly VerbSpec Spec = new(
        "script",
        "Emit the validated restore T-SQL for a moment",
        "9lives script (--container NAME | --server NAME) --database DB [--at \"yyyy-MM-dd HH:mm:ss\"] " +
        "[--target-database NAME] [--with-replace] [--norecovery] [--stop-before-mark NAME | --stop-at-mark NAME] " +
        "[--target NAME [--relocate | --data-path DIR [--log-path DIR]]] [--out FILE]",
        Valued: ["container", "server", "database", "at", "target-database", "target",
                 "stop-before-mark", "stop-at-mark", "data-path", "log-path", "out"],
        Switches: ["with-replace", "norecovery", "relocate"],
        Options:
        [
            ("--container NAME", "A blob container configured in the app."),
            ("--server NAME", "A configured server, read from its msdb history."),
            ("--database DB", "The database whose chain to script. Required."),
            ("--at TIME", "The moment to restore to. Omitted, the newest reachable point is " +
                "scripted. Formats: yyyy-MM-dd HH:mm:ss, yyyy-MM-dd HH:mm, yyyy-MM-dd - " +
                "exact and invariant, nothing culture-shaped."),
            ("--target-database NAME", "Restore AS this name. Defaults to the source name."),
            ("--with-replace", "Adds WITH REPLACE. Off by default, always - overwriting is " +
                "said explicitly here just as it is on the restore verb."),
            ("--norecovery", "Leave the database restoring (NORECOVERY), ready for more log."),
            ("--stop-before-mark NAME", "Stop just BEFORE the named marked transaction - the " +
                "deployment never happened. Marks are planted with BEGIN TRANSACTION ... " +
                "WITH MARK; the app's restore screen finds them in msdb."),
            ("--stop-at-mark NAME", "Stop AT the mark, including the marked transaction."),
            ("--target NAME", "The instance the script will eventually run ON. Only needed for " +
                "relocation, and used only to ask it two questions - nothing is executed."),
            ("--relocate", "MOVE every file to the target's default data and log directories, " +
                "keeping its original name. Needs --target."),
            ("--data-path DIR", "MOVE data files into this directory (log files follow " +
                "--log-path, or the target's default log directory). Implies relocation, " +
                "needs --target."),
            ("--log-path DIR", "MOVE log files into this directory. Implies relocation; data " +
                "files follow --data-path, or the target's default data directory."),
            ("--out FILE", "Write the script to a file instead of stdout.")
        ],
        Notes:
        [
            "Stdout carries ONLY the script - the chain summary and every human word go to " +
            "stderr - so \"> restore.sql\" and pipes stay clean. The script is built by the " +
            "same chain calculation, striped-set grouping and generator the app runs, which " +
            "is why what this emits actually executes.",
            "The moment is matched by RESTORE's own rules. STOPAT can only ride a log chain " +
            "that spans past the moment, so --at picks the log chain covering it and stamps " +
            "STOPAT on every log restore in the chain. A moment between two non-log points " +
            "is a hole no chain reaches: the verb refuses and names the nearest reachable " +
            "points either side, because quietly restoring to somewhere nearby is worse.",
            "A mark and a time are the same mechanism aimed differently, so giving both is " +
            "refused rather than ranked.",
            "Relocation is the one thing here that cannot be done offline (#370). The logical " +
            "file names come from RESTORE FILELISTONLY and the default directories come from " +
            "the instance, so --relocate, --data-path and --log-path need a --target to ask. " +
            "That target is read, never written to. Without one they are refused rather than " +
            "ignored: a script that silently has no MOVE in it is a script that fails in the " +
            "change window it was generated for."
        ],
        ExitCodes:
        [
            "0   script emitted",
            "2   no chain covers the moment asked for, or the source holds no backups for " +
            "this database",
            "3   the source could not be read at all, or the target could not answer the two " +
            "questions relocation needs",
            "64  usage"
        ],
        Examples:
        [
            ("9lives script --container backups --database Sales --at \"2026-08-02 19:00\" --out restore.sql",
                "the validated script for that moment, into a file"),
            ("9lives script --server SRV01 --database Sales --stop-before-mark deploy_v2",
                "undo a marked deployment - the sharpest point-in-time tool SQL Server has"),
            ("9lives script --container backups --database Sales --target SRV02 --relocate --out restore.sql",
                "generate here, run in the change window - with MOVE clauses for a machine that " +
                "does not share the source's drive layout")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        if (args.Get("database") == null)
        {
            errors.WriteLine("script needs --database.");
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
            // The generator would let the mark win, but a pipeline should not learn precedence
            // rules by surprise - two targets on one invocation is a decision nobody made.
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

        var (sets, sourceContainer, error, loadExit) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            // The loader says which kind of failure this was (#370): a malformed
            // invocation exits 64, a source that holds nothing for this database
            // exits 2 - the finding, not a usage error.
            return loadExit;
        }

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);
        var (point, stopAt, chainError) = ChoosePoint(points, at);
        if (point == null)
        {
            errors.WriteLine(chainError);
            return ExitCodes.Failed;
        }

        var chain = new BackupChainBuilder().BuildChainFromRestorePoint(point);

        // Relocation, the one thing this verb cannot answer on its own (#370). Refused without a
        // target rather than dropped, because the workflow this exists for - generate here, hand
        // it over, run it in the change window on a machine with a different drive layout - is
        // exactly the one where a missing MOVE is not discovered until it is expensive.
        List<FileMoveOption>? moves = null;
        if (Relocation.WasAskedFor(args))
        {
            if (args.Get("target") == null)
            {
                errors.WriteLine(Relocation.NeedsATarget);
                return ExitCodes.Usage;
            }

            var (relocateTo, targetError) = services.FindServer(args.Get("target")!);
            if (relocateTo == null)
            {
                errors.WriteLine(targetError);
                return ExitCodes.Usage;
            }

            var (resolved, relocationError) = await Relocation.ResolveAsync(
                args, services, relocateTo, chain, errors.WriteLine, CancellationToken.None);

            if (relocationError != null)
            {
                errors.WriteLine(relocationError);
                return ExitCodes.CouldNotAnswer;
            }

            moves = resolved;
        }

        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = args.Get("target-database") ?? args.Get("database")!,
            WithReplace = args.Has("with-replace"),
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

        if (args.Get("out") is { } path)
        {
            await File.WriteAllTextAsync(path, script);
            errors.WriteLine($"Script written to {path}.");
        }
        else
        {
            output.WriteLine(script);
        }

        errors.WriteLine($"Chain: {point.TypeDisplay} reaching " +
                         $"{(stopAt ?? point.Timestamp):yyyy-MM-dd HH:mm:ss}. Generated only - " +
                         "nothing has been executed.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Which chain covers the asked-for moment, by RESTORE's own rules. STOPAT can only ride a
    /// log chain that spans past the moment; a moment between two non-log points is a hole no
    /// chain reaches, and saying so beats quietly restoring to somewhere nearby. No moment at
    /// all means the newest reachable point.
    /// </summary>
    internal static (RestorePoint? point, DateTime? stopAt, string? error) ChoosePoint(
        List<RestorePoint> points, DateTime? at)
    {
        if (points.Count == 0)
            return (null, null,
                "No restorable point exists: nothing in the source forms a valid chain. " +
                "The validate verb says what is broken.");

        if (at == null)
            return (points.OrderBy(p => p.Timestamp).Last(), null, null);

        var moment = at.Value;

        // A log chain whose end is at-or-past the moment, and whose base full is at-or-before
        // it: STOPAT lands inside what that chain replays.
        var covering = points
            .Where(p => p.Type == BackupType.TransactionLog
                        && p.Timestamp >= moment
                        && p.RequiredFullSet.Timestamp <= moment)
            .OrderBy(p => p.Timestamp)
            .FirstOrDefault();
        if (covering != null)
            return (covering, moment, null);

        var exact = points.FirstOrDefault(p => p.Timestamp == moment);
        if (exact != null)
            return (exact, null, null);

        var before = points.Where(p => p.Timestamp < moment).OrderBy(p => p.Timestamp).LastOrDefault();
        var after = points.Where(p => p.Timestamp > moment).OrderBy(p => p.Timestamp).FirstOrDefault();

        if (before == null)
            return (null, null,
                $"The chains begin at {points.Min(p => p.Timestamp):yyyy-MM-dd HH:mm:ss} - " +
                "nothing reaches back as far as the moment asked for.");

        if (after == null)
            return (null, null,
                $"The chains end at {before.Timestamp:yyyy-MM-dd HH:mm:ss}, before the moment " +
                "asked for. Omit --at to script the newest reachable point.");

        return (null, null,
            $"No chain reaches {moment:yyyy-MM-dd HH:mm:ss}: no log backup spans it. The " +
            $"nearest reachable points are {before.Timestamp:yyyy-MM-dd HH:mm:ss} and " +
            $"{after.Timestamp:yyyy-MM-dd HH:mm:ss}.");
    }
}
