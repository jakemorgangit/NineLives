using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// What a source holds, one line per database: how many sets, of what kinds, and how fresh.
/// The first question anyone asks of a container or an instance's history, and the answer the
/// other verbs' --database option is chosen from.
/// </summary>
internal static class ListVerb
{
    public static readonly VerbSpec Spec = new(
        "list",
        "The databases a source holds backups for, with counts and freshness",
        "9lives list (--container NAME | --server NAME) [--json]",
        Valued: ["container", "server"],
        Switches: ["json"]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        var (sets, error) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            return ExitCodes.Usage;
        }

        var byDatabase = sets
            .Where(s => !string.IsNullOrEmpty(s.DatabaseName))
            .GroupBy(s => s.DatabaseName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Database = g.Key,
                Fulls = g.Count(s => s.Type == BackupType.Full),
                Diffs = g.Count(s => s.Type == BackupType.Differential),
                Logs = g.Count(s => s.Type == BackupType.TransactionLog),
                Latest = g.Max(s => s.Timestamp)
            })
            .ToList();

        if (args.Has("json"))
        {
            output.WriteLine(JsonSerializer.Serialize(byDatabase, JsonOut.Options));
            return ExitCodes.Ok;
        }

        if (byDatabase.Count == 0)
        {
            errors.WriteLine("The source holds no recognisable backups.");
            return ExitCodes.Failed;
        }

        TableWriter.Write(output,
            ["DATABASE", "FULL", "DIFF", "LOG", "LATEST BACKUP"],
            byDatabase.Select(d => new[]
            {
                d.Database,
                d.Fulls.ToString(),
                d.Diffs.ToString(),
                d.Logs.ToString(),
                d.Latest.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToList());

        return ExitCodes.Ok;
    }
}
