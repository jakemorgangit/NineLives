using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// Is the chain intact - asked on a schedule, answered in an exit code. The chain builder
/// already refuses to offer what SQL Server would reject (a differential with no base, a log
/// run that does not connect); this verb makes the refusals VISIBLE, because a set silently
/// absent from the points list looks fine right up until the restore that needed it.
///
/// Chain logic only, deliberately: nothing here opens a file or a connection beyond reading
/// the inventory, so it is cheap enough to run every night. Whether each file is actually
/// readable is the app's audit; whether every byte restores is the rehearsal's proof.
/// </summary>
internal static class ValidateVerb
{
    public static readonly VerbSpec Spec = new(
        "validate",
        "Check every chain is intact; the exit code is the answer",
        "9lives validate (--container NAME | --server NAME) [--database DB] [--json]",
        Valued: ["container", "server", "database"],
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

        var builder = new BackupChainBuilder();
        var results = sets
            .Where(s => !string.IsNullOrEmpty(s.DatabaseName))
            .GroupBy(s => s.DatabaseName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => Judge(g.Key, [.. g], builder))
            .ToList();

        if (args.Has("json"))
        {
            output.WriteLine(JsonSerializer.Serialize(results, JsonOut.Options));
        }
        else
        {
            TableWriter.Write(output,
                ["DATABASE", "VERDICT", "POINTS", "REACHES", "STRANDED"],
                results.Select(r => new[]
                {
                    r.Database,
                    r.Intact ? "intact" : "BROKEN",
                    r.Points.ToString(),
                    r.Reaches?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    r.Stranded.Count.ToString()
                }).ToList());

            foreach (var r in results.Where(x => x.Stranded.Count > 0))
                foreach (var reason in r.Stranded)
                    errors.WriteLine($"{r.Database}: {reason}");
        }

        return results.All(r => r.Intact) ? ExitCodes.Ok : ExitCodes.Failed;
    }

    internal sealed record Verdict(
        string Database, bool Intact, int Points, DateTime? Reaches, List<string> Stranded);

    /// <summary>
    /// A database's chains, judged. Broken means a set exists that no restore point can use:
    /// the builder left it out because SQL Server would reject the chain it implies.
    /// </summary>
    private static Verdict Judge(string database, List<BackupSet> sets, BackupChainBuilder builder)
    {
        var points = builder.ComputeRestorePoints(sets);

        var used = new HashSet<BackupSet>(
            points.SelectMany(p => p.AllRequiredSets));

        var stranded = new List<string>();
        foreach (var set in sets.Where(s => !used.Contains(s)).OrderBy(s => s.Timestamp))
        {
            stranded.Add(set.Type switch
            {
                BackupType.Differential =>
                    $"differential at {set.Timestamp:yyyy-MM-dd HH:mm:ss} has no base full in " +
                    "hand - restoring it would fail with error 3136",
                BackupType.TransactionLog =>
                    $"log at {set.Timestamp:yyyy-MM-dd HH:mm:ss} does not connect to any chain " +
                    "- the run of logs is broken before it",
                _ =>
                    $"{set.Type} at {set.Timestamp:yyyy-MM-dd HH:mm:ss} forms no restore point",
            });
        }

        // No points at all is broken by definition - backups that reach nowhere - EXCEPT when
        // there are also no sets, which the caller's filters make impossible today; belt and
        // braces for tomorrow.
        var intact = stranded.Count == 0 && points.Count > 0;

        return new Verdict(
            database, intact, points.Count,
            points.Count == 0 ? null : points.Max(p => p.Timestamp), stranded);
    }
}
