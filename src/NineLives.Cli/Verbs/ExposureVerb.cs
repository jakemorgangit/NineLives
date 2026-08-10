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
        "9lives exposure [--server NAME] [--json]",
        Valued: ["server"],
        Switches: ["json"]);

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
        return worst switch
        {
            ExposureLevel.Alarm => ExitCodes.Failed,
            ExposureLevel.Warning => ExitCodes.Warnings,
            _ => ExitCodes.Ok
        };
    }
}
