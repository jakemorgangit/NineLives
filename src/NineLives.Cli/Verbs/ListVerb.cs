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
        Switches: ["json"],
        Options:
        [
            ("--container NAME",
                "A blob container configured in the app, by its configured name. The whole " +
                "container is listed and grouped into backup sets, exactly as the app's " +
                "browser groups it - striped sets count once, not per file."),
            ("--server NAME",
                "A server configured in the app. What ITS msdb recorded backing up - which " +
                "covers everything that instance wrote, wherever it wrote it."),
            ("--json",
                "The same rows as JSON on stdout: database, fulls, diffs, logs, latest. " +
                "camelCase names, ISO 8601 dates.")
        ],
        Notes:
        [
            "One line per database: how many fulls, differentials and logs the source holds, " +
            "and when the newest of any kind was taken. This is the discovery step - the " +
            "answer to \"what can I even ask about?\" - and the value every other verb's " +
            "--database option is chosen from.",
            "Give exactly one source. A container and a server are different catalogues " +
            "answering the same question, and which one is meant matters: the container " +
            "knows what is IN it, the server knows what it WROTE."
        ],
        ExitCodes:
        [
            "0   listed",
            "2   the source holds no recognisable backups",
            "3   the source could not be read at all",
            "64  usage"
        ],
        Examples:
        [
            ("9lives list --container backups",
                "every database the container holds, with freshness"),
            ("9lives list --server SRV01 --json",
                "what SRV01's msdb recorded, for jq and friends")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        var (sets, _, error) = await InventoryLoader.LoadAsync(args, services);
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
