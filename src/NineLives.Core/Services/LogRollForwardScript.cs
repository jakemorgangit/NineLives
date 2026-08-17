using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Rolls a database that is already sitting in RESTORING forward with more log backups (#451).
///
/// The cutover shape. A copy takes a full now and restores it; if it is left in NORECOVERY the
/// target stays restorable, and the source's later log backups can be applied at the moment of
/// switching over. The long part - the full - happens in advance, and the downtime is only the
/// logs that accumulated since.
///
/// Separate from <see cref="RestoreScriptGenerator"/> rather than a mode of it, because that one
/// always begins with a full: it describes restoring a chain from nothing. This describes adding
/// to a restore already under way, which is a different statement sequence with a different
/// precondition - the database must already be in RESTORING, and if it is not, every statement
/// here fails with 3101 rather than doing something unintended.
/// </summary>
public static class LogRollForwardScript
{
    /// <summary>
    /// The logs in the order they must be applied, against a database left in NORECOVERY.
    ///
    /// <paramref name="bringOnline"/> decides whether the last statement recovers. Left false, the
    /// target stays restorable and another batch can follow - which is what a rehearsal of the
    /// cutover wants, and what somebody applying logs hourly until the switch wants too.
    /// </summary>
    public static string Build(
        string targetDatabase,
        IReadOnlyList<BackupHistoryEntry> logs,
        bool bringOnline = true)
    {
        var ordered = logs
            .Where(l => l.Type == BackupType.TransactionLog && l.HasFiles)
            .OrderBy(l => l.StartedAt)
            .ToList();

        var sb = new StringBuilder();
        var db = TSql.QuoteName(targetDatabase);

        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Nine Lives - roll the copy forward");
        sb.AppendLine($"-- Target: {TSql.CommentText(targetDatabase)}");
        sb.AppendLine($"-- {ordered.Count} log backup(s)");
        if (ordered.Count > 0)
            sb.AppendLine($"-- Covering {ordered[0].StartedAt:yyyy-MM-dd HH:mm} to {ordered[^1].StartedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        if (ordered.Count == 0)
        {
            sb.AppendLine("-- No log backups to apply. The source has taken none since the copy,");
            sb.AppendLine("-- so there is nothing between the copy and now to roll forward.");
            return sb.ToString();
        }

        // Said, not assumed. Run against a database that is already online, every statement below
        // fails with 3101 - which is safe but reads as a broken script rather than a precondition
        // nobody met.
        sb.AppendLine($"-- {targetDatabase} must still be in RESTORING for these to apply. If the copy");
        sb.AppendLine("-- brought it online, it cannot take more logs and has to be redone with");
        sb.AppendLine("-- \"Leave the target ready for more log backups\" ticked.");
        sb.AppendLine();

        for (int i = 0; i < ordered.Count; i++)
        {
            var log = ordered[i];
            var isLast = i == ordered.Count - 1;
            var recovery = isLast && bringOnline ? "RECOVERY" : "NORECOVERY";

            sb.AppendLine($"-- {log.StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"RESTORE LOG {db}");

            // Every file of the set, in stripe order, in ONE statement - a striped log is one media
            // set and naming half of it fails with 3132.
            for (int f = 0; f < log.Files.Count; f++)
            {
                var prefix = f == 0 ? "    FROM" : "        ";
                var suffix = f < log.Files.Count - 1 ? "," : "";
                sb.AppendLine($"{prefix} {BackupDevice.Clause(log.Files[f])}{suffix}");
            }

            sb.AppendLine("    WITH");
            sb.AppendLine($"         {recovery},");
            sb.AppendLine("         STATS = 10;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        if (!bringOnline)
        {
            sb.AppendLine("-- Still in RESTORING, ready for the next batch. To finish:");
            sb.AppendLine($"-- RESTORE DATABASE {db} WITH RECOVERY;");
        }

        return sb.ToString();
    }
}
