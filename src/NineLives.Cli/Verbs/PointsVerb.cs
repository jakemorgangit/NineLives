using System.Text.Json;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The timeline as text: every discrete moment a valid chain can reach for one database,
/// computed by the same <see cref="BackupChainBuilder"/> the GUI's timeline is drawn from.
/// If a moment is not in this list, no chain in the source reaches it - which is exactly the
/// thing a DR pipeline wants to assert before it needs it.
/// </summary>
internal static class PointsVerb
{
    public static readonly VerbSpec Spec = new(
        "points",
        "The restore points a database's chains can reach",
        "9lives points (--container NAME | --server NAME) --database DB [--json]",
        Valued: ["container", "server", "database"],
        Switches: ["json"]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        if (args.Get("database") == null)
        {
            errors.WriteLine("points needs --database. Run the list verb to see what the source holds.");
            return ExitCodes.Usage;
        }

        var (sets, error) = await InventoryLoader.LoadAsync(args, services);
        if (sets == null)
        {
            errors.WriteLine(error);
            return ExitCodes.Usage;
        }

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);

        if (args.Has("json"))
        {
            var shaped = points.Select(p => new
            {
                Timestamp = p.Timestamp,
                Type = p.Type.ToString(),
                Chain = p.TypeDisplay,
                Files = p.AllRequiredSets.Sum(s => s.Files.Count),
                Approximate = p.IsTimestampApproximate
            });
            output.WriteLine(JsonSerializer.Serialize(shaped, JsonOut.Options));
            return points.Count == 0 ? ExitCodes.Failed : ExitCodes.Ok;
        }

        if (points.Count == 0)
        {
            errors.WriteLine("No restorable point exists: nothing in the source forms a valid " +
                             "chain for this database. The validate verb says what is broken.");
            return ExitCodes.Failed;
        }

        TableWriter.Write(output,
            ["RESTORE POINT", "TYPE", "CHAIN", "FILES"],
            points.Select(p => new[]
            {
                p.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + (p.IsTimestampApproximate ? " ~" : ""),
                p.Type.ToString(),
                p.TypeDisplay,
                p.AllRequiredSets.Sum(s => s.Files.Count).ToString()
            }).ToList());

        if (points.Any(p => p.IsTimestampApproximate))
            errors.WriteLine("~ = timestamp inferred from the blob name or write time, not read " +
                             "from a header.");

        return ExitCodes.Ok;
    }
}
