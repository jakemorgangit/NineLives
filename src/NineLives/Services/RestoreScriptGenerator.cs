using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class RestoreScriptGenerator
{
    public string Generate(BackupChain chain, RestoreOptions options)
    {
        var sb = new StringBuilder();
        var dbName = EscapeName(options.TargetDatabaseName);
        var hasDiffs = chain.DiffSets.Count > 0;
        var hasLogs = chain.LogSets.Count > 0;
        var usePit = options.StopAt.HasValue && hasLogs;

        AppendHeader(sb, options, chain);
        // Credential is not included in script; it must exist on the server (see Restore options).

        if (options.DisconnectSessions)
            AppendDisconnectSessions(sb, dbName);

        AppendFullRestore(sb, chain.FullSet, options, hasDiffs || hasLogs);

        for (int i = 0; i < chain.DiffSets.Count; i++)
        {
            bool moreDiffs = i < chain.DiffSets.Count - 1;
            AppendDiffRestore(sb, chain.DiffSets[i], options, moreDiffs || hasLogs);
        }

        for (int i = 0; i < chain.LogSets.Count; i++)
        {
            bool isLast = i == chain.LogSets.Count - 1;
            AppendLogRestore(sb, chain.LogSets[i], options, isLast, usePit);
        }

        if (options.DisconnectSessions && options.RecoveryMode == RecoveryMode.Recovery)
            AppendReconnectSessions(sb, dbName);

        AppendFooter(sb);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, RestoreOptions options, BackupChain chain)
    {
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Nine Lives - Generated Restore Script (Blackcat Data Solutions)");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- Target Database: {options.TargetDatabaseName}");
        sb.AppendLine($"-- Restore Chain: {chain.Summary}");
        if (options.StopAt.HasValue)
            sb.AppendLine($"-- Point-in-Time: {options.StopAt.Value:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();
    }

    private static void AppendDisconnectSessions(StringBuilder sb, string dbName)
    {
        sb.AppendLine("-- Disconnect all active sessions");
        sb.AppendLine($"IF DB_ID('{dbName.Replace("[", "").Replace("]", "")}') IS NOT NULL");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    ALTER DATABASE {dbName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendFullRestore(
        StringBuilder sb, BackupSet fullSet, RestoreOptions options, bool moreToFollow)
    {
        var dbName = EscapeName(options.TargetDatabaseName);
        var recoveryClause = moreToFollow ? "NORECOVERY" : GetRecoveryClause(options);

        sb.AppendLine($"-- Restore FULL backup ({fullSet.FileCount} file(s)): {fullSet.SetId}");
        sb.AppendLine($"RESTORE DATABASE {dbName}");
        AppendFromUrls(sb, fullSet, options);

        if (options.WithReplace)
            sb.AppendLine("         REPLACE,");

        AppendFileMoves(sb, options);

        sb.AppendLine($"         {recoveryClause},");
        AppendCommonOptions(sb, options);
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendDiffRestore(
        StringBuilder sb, BackupSet diffSet, RestoreOptions options, bool moreToFollow)
    {
        var dbName = EscapeName(options.TargetDatabaseName);
        var recoveryClause = moreToFollow ? "NORECOVERY" : GetRecoveryClause(options);

        sb.AppendLine($"-- Restore DIFFERENTIAL backup ({diffSet.FileCount} file(s)): {diffSet.SetId}");
        sb.AppendLine($"RESTORE DATABASE {dbName}");
        AppendFromUrls(sb, diffSet, options);
        sb.AppendLine($"         {recoveryClause},");
        AppendCommonOptions(sb, options);
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendLogRestore(
        StringBuilder sb, BackupSet logSet, RestoreOptions options, bool isLast, bool usePit)
    {
        var dbName = EscapeName(options.TargetDatabaseName);
        var recoveryClause = isLast ? GetRecoveryClause(options) : "NORECOVERY";

        sb.AppendLine($"-- Restore LOG backup ({logSet.FileCount} file(s)): {logSet.SetId}");
        sb.AppendLine($"RESTORE LOG {dbName}");
        AppendFromUrls(sb, logSet, options);

        if (isLast && usePit && options.StopAt.HasValue)
            sb.AppendLine($"         STOPAT = '{options.StopAt.Value:yyyy-MM-ddTHH:mm:ss}',");

        sb.AppendLine($"         {recoveryClause},");
        AppendCommonOptions(sb, options);
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendFromUrls(StringBuilder sb, BackupSet set, RestoreOptions options)
    {
        for (int i = 0; i < set.Files.Count; i++)
        {
            var file = set.Files[i];
            var url = BlobUrlEncoder.Encode(file.BlobUrl);
            var prefix = i == 0 ? "    FROM" : "        ";
            var suffix = i < set.Files.Count - 1 ? "," : "";
            sb.AppendLine($"{prefix} URL = N'{url.Replace("'", "''")}'{suffix}");
        }

        sb.AppendLine("    WITH");
    }

    private static void AppendFileMoves(StringBuilder sb, RestoreOptions options)
    {
        foreach (var move in options.FileMoves)
        {
            if (!string.IsNullOrWhiteSpace(move.NewPhysicalName))
            {
                sb.AppendLine($"         MOVE N'{move.LogicalName}' TO N'{move.NewPhysicalName}',");
            }
        }
    }

    private static void AppendCommonOptions(StringBuilder sb, RestoreOptions options)
    {
        if (options.KeepReplication)
            sb.AppendLine("         KEEP_REPLICATION,");
        if (options.EnableBroker)
            sb.AppendLine("         ENABLE_BROKER,");
        if (options.NewBroker)
            sb.AppendLine("         NEW_BROKER,");
        sb.AppendLine($"         STATS = {options.StatsPercent};");
    }

    private static void AppendReconnectSessions(StringBuilder sb, string dbName)
    {
        sb.AppendLine("-- Return database to multi-user mode");
        sb.AppendLine($"ALTER DATABASE {dbName} SET MULTI_USER;");
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Restore script complete.");
        sb.AppendLine("-- ============================================================");
    }

    private static string GetRecoveryClause(RestoreOptions options)
    {
        return options.RecoveryMode switch
        {
            RecoveryMode.Recovery => "RECOVERY",
            RecoveryMode.NoRecovery => "NORECOVERY",
            RecoveryMode.Standby => $"STANDBY = '{options.StandbyFilePath}'",
            _ => "RECOVERY"
        };
    }

    private static string EscapeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "[DatabaseName]";
        if (name.StartsWith('[')) return name;
        return $"[{name}]";
    }
}
