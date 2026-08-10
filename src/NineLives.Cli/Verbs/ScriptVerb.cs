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
        "[--target-database NAME] [--with-replace] [--norecovery] [--stop-before-mark NAME | --stop-at-mark NAME] [--out FILE]",
        Valued: ["container", "server", "database", "at", "target-database",
                 "stop-before-mark", "stop-at-mark", "out"],
        Switches: ["with-replace", "norecovery"]);

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

        var (sets, error) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            return ExitCodes.Usage;
        }

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);
        var (point, stopAt, chainError) = ChoosePoint(points, at);
        if (point == null)
        {
            errors.WriteLine(chainError);
            return ExitCodes.Failed;
        }

        var chain = new BackupChainBuilder().BuildChainFromRestorePoint(point);
        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = args.Get("target-database") ?? args.Get("database")!,
            WithReplace = args.Has("with-replace"),
            RecoveryMode = args.Has("norecovery") ? RecoveryMode.NoRecovery : RecoveryMode.Recovery,
            StopAt = stopAt,
            StopAtMark = mark,
            StopBeforeMark = markAt == null
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
