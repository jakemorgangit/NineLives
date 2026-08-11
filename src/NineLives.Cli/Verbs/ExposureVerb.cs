using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The exposure dashboard for machines: every configured server swept, every database judged
/// by the same <see cref="ExposureAdvisor"/> the GUI uses, worst first - and the worst level
/// IS the exit code, so a scheduled task turns "log backups quietly stopped an hour ago" into
/// a red pipeline without anyone watching a screen. The issue's list of verbs predates this
/// screen; a monitoring-shaped question is exactly what a CLI is for.
/// </summary>
internal static class ExposureVerb
{
    public static readonly VerbSpec Spec = new(
        "exposure",
        "Sweep every server: if it died now, what is lost? Exit code = worst level",
        "9lives exposure [--server NAME] [--json] [--notify | --notify-always]",
        Valued: ["server"],
        Switches: ["json", "notify", "notify-always"],
        Options:
        [
            ("--server NAME", "Sweep one configured server. Omitted, every configured server " +
                "is swept in parallel - the estate, not a sample."),
            ("--json", "Rows as JSON on stdout: server, database, level, recoveryModel, " +
                "lastFull, lastDifferential, lastLog, recoverableTo, verdict."),
            ("--notify", "Push the sweep's verdict through the configured webhooks - one " +
                "message per sweep, worst offenders first, into the same channel the runs " +
                "report to. Speaks only at warning level and above."),
            ("--notify-always", "Like --notify, but a clean sweep also sends its all-clear - " +
                "for the channel that wants the heartbeat, not just the bad news.")
        ],
        Notes:
        [
            "Every user database judged by the same advisor as the app's dashboard, worst " +
            "first: never backed up is an alarm; FULL recovery with no log backups is the " +
            "double alarm (the model promises point-in-time and the estate cannot deliver " +
            "it); log silence beyond 1 hour warns and beyond 24 hours alarms; SIMPLE " +
            "recovery warns past 26 hours since a full or diff and alarms past 7 days. " +
            "Warnings and alarms come from EVIDENCE of staleness, never from mere absence " +
            "of information.",
            "A server that will not answer becomes an ALARM ROW, not a dead verb: its " +
            "databases' exposure is unknown, which is not the same as fine - and on the " +
            "worst morning the servers that do answer still get judged.",
            "The worst level found IS the exit code, which makes this the scheduled-task " +
            "verb: wire it to a scheduler and a failing exit turns quiet log-backup silence " +
            "into a red pipeline while attention is elsewhere - and --notify closes the " +
            "loop with zero extra infrastructure, pushing the same verdict through the " +
            "configured webhooks. One message per sweep, the worst offenders named worst " +
            "first, never one per row: a channel flooded by forty rows is a channel " +
            "somebody mutes. Endpoint subscriptions apply - a problem message honours " +
            "Problems, the --notify-always all-clear honours Finished. Task Scheduler plus " +
            "this flag is backup monitoring for an estate with no agent and no dashboard."
        ],
        ExitCodes:
        [
            "0   every database fine",
            "1   warnings - amber, worth a look today",
            "2   alarms - including any unreachable server",
            "3   the sweep itself could not run",
            "64  usage"
        ],
        Examples:
        [
            ("9lives exposure",
                "the whole estate, one exit code"),
            ("9lives exposure --server SRV01 --json",
                "one server's rows for a dashboard or jq"),
            ("9lives exposure --notify",
                "the scheduled shape: exit code for the scheduler, worst rows to the channel")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        List<ServerConnection> servers;
        if (args.Get("server") is { } name)
        {
            var (server, error) = services.FindServer(name);
            if (server == null)
            {
                errors.WriteLine(error);
                return ExitCodes.Usage;
            }

            servers = [server];
        }
        else
        {
            servers = services.Config.Servers.ToList();
            if (servers.Count == 0)
            {
                errors.WriteLine("No servers are configured - add them in the app first.");
                return ExitCodes.Usage;
            }
        }

        var now = DateTime.Now;
        var rows = new List<ExposureRow>();

        // Parallel like the GUI's sweep: forty servers at thirty seconds each is the difference
        // between a verb and a lunch break. An unreachable server becomes an alarm ROW rather
        // than a dead verb - on the worst morning, the servers that answer still get judged,
        // and the one that will not answer is exactly the one to worry about.
        var sweeps = servers.Select(async server =>
        {
            try
            {
                var swept = await services.Sql.GetBackupExposureAsync(server);
                foreach (var row in swept) ExposureAdvisor.Judge(row, now);
                return swept;
            }
            catch (Exception ex)
            {
                var reason = ex.Message.Split('\n')[0].Trim();
                return
                [
                    new ExposureRow
                    {
                        ServerName = server.ServerName,
                        DatabaseName = "(every database on it)",
                        RecoveryModel = "?",
                        StateDescription = "UNREACHABLE",
                        Level = ExposureLevel.Alarm,
                        // The word rides in the verdict because the verdict is what the table
                        // and the JSON both carry - a state column nobody prints is a state
                        // nobody sees.
                        Verdict = $"UNREACHABLE - the server did not answer: {reason} Its " +
                                  "databases' exposure is unknown, which is not the same as fine."
                    }
                ];
            }
        });

        foreach (var swept in await Task.WhenAll(sweeps))
            rows.AddRange(swept);

        rows = rows
            .OrderByDescending(r => r.Level)
            .ThenBy(r => r.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (args.Has("json"))
        {
            var shaped = rows.Select(r => new
            {
                Server = r.ServerName,
                Database = r.DatabaseName,
                Level = r.Level.ToString(),
                r.RecoveryModel,
                r.LastFull,
                r.LastDifferential,
                r.LastLog,
                r.RecoverableTo,
                r.Verdict
            });
            output.WriteLine(JsonSerializer.Serialize(shaped, JsonOut.Options));
        }
        else
        {
            TableWriter.Write(output,
                ["", "SERVER", "DATABASE", "MODEL", "RECOVERABLE TO", "VERDICT"],
                rows.Select(r => new[]
                {
                    r.Level switch
                    {
                        ExposureLevel.Alarm => "!!",
                        ExposureLevel.Warning => "!",
                        _ => ""
                    },
                    r.ServerName,
                    r.DatabaseName,
                    r.RecoveryModel,
                    r.RecoverableTo?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    r.Verdict
                }).ToList());
        }

        var worst = rows.Count == 0 ? ExposureLevel.Ok : rows.Max(r => r.Level);

        // --notify closes the loop (#301): the same verdict, through the configured
        // webhooks. One message per sweep - a channel flooded by forty rows is a channel
        // somebody mutes - speaking only at warning level and above unless --notify-always
        // asks for the heartbeat. The exit code is unaffected either way.
        if (args.Has("notify") || args.Has("notify-always"))
        {
            if (worst != ExposureLevel.Ok)
            {
                var alarms = rows.Count(r => r.Level == ExposureLevel.Alarm);
                var warnings = rows.Count(r => r.Level == ExposureLevel.Warning);
                var offenders = rows
                    .Where(r => r.Level != ExposureLevel.Ok)
                    .Take(5)
                    .Select(r => $"{r.ServerName}/{r.DatabaseName} - {r.Verdict}");
                var named = string.Join("  |  ", offenders);
                var overflow = alarms + warnings > 5
                    ? $"  |  ...and {alarms + warnings - 5} more"
                    : string.Empty;

                services.Notifier.Notify(new RunNotification(
                    RunPhase.Problem, "Exposure",
                    $"{alarms} alarm(s), {warnings} warning(s)",
                    $"{servers.Count} server(s)",
                    named + overflow));
                await services.Notifier.DrainAsync(NotificationDrain);
            }
            else if (args.Has("notify-always"))
            {
                services.Notifier.Notify(new RunNotification(
                    RunPhase.Succeeded, "Exposure",
                    $"all {rows.Count} database(s) inside their windows",
                    $"{servers.Count} server(s)"));
                await services.Notifier.DrainAsync(NotificationDrain);
            }
        }

        return worst switch
        {
            ExposureLevel.Alarm => ExitCodes.Failed,
            ExposureLevel.Warning => ExitCodes.Warnings,
            _ => ExitCodes.Ok
        };
    }

    /// <summary>Long enough for a slow webhook, short enough that a dead one cannot hold the exit.</summary>
    private static readonly TimeSpan NotificationDrain = TimeSpan.FromSeconds(10);
}
