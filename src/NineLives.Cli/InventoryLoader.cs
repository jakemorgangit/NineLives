using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// Turns "--container backups" or "--server SRV01" into the backup sets that source holds -
/// the same discovery the GUI runs: blob listings grouped by the grouping service, or msdb
/// history through <see cref="BackupHistoryInventory"/>. Every read verb starts here, so every
/// read verb agrees about what exists.
/// </summary>
internal static class InventoryLoader
{
    /// <summary>
    /// The sets the chosen source holds, optionally narrowed to one database. Exactly one of
    /// --container or --server must be given: with neither there is nothing to read, and with
    /// both it is ambiguous which catalogue is meant to answer.
    /// </summary>
    public static async Task<(List<BackupSet>? sets, string? error)> LoadAsync(
        CliArguments args, CliServices services)
    {
        var containerName = args.Get("container");
        var serverName = args.Get("server");

        if ((containerName == null) == (serverName == null))
            return (null, "Give exactly one source: --container NAME or --server NAME.");

        List<BackupSet> sets;

        if (containerName != null)
        {
            var (container, error) = services.FindContainer(containerName);
            if (container == null) return (null, error);

            var files = await services.Blobs.ListBackupFilesAsync(container);
            sets = services.Blobs.GroupIntoBackupSets(files, container.BackupServerTimeZoneId);
        }
        else
        {
            var (server, error) = services.FindServer(serverName!);
            if (server == null) return (null, error);

            var history = await services.Sql.ReadBackupHistoryAsync(server, args.Get("database"));
            sets = BackupHistoryInventory.ToSets(history);
        }

        var database = args.Get("database");
        if (database != null)
        {
            sets = sets
                .Where(s => string.Equals(
                    s.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sets.Count == 0)
                return (null, $"The source has no backups for a database called '{database}'. " +
                              "Run the list verb to see what it does hold.");
        }

        return (sets, null);
    }
}
