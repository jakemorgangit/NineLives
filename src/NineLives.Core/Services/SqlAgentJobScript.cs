using System.Text;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Wraps a generated script as a SQL Server Agent job (#32).
///
/// For the people who cannot run a restore interactively: a maintenance window at 3am, a change
/// process that takes submitted scripts rather than someone at a keyboard, an operator who will run
/// what a DBA hands them. The job is the deliverable, and it is reviewable before it exists.
///
/// The job is created DISABLED and with no schedule. Handing somebody a script that quietly
/// schedules a restore of a production database would be a remarkable thing to do by accident, and
/// nothing about pasting a script into SSMS suggests it might. Enabling it and giving it a schedule
/// are two deliberate acts, both left to whoever reviews it.
/// </summary>
public static class SqlAgentJobScript
{
    /// <summary>
    /// The T-SQL that creates the job, or empty when there is nothing to wrap.
    /// </summary>
    /// <param name="script">The script to run, GO batches and all.</param>
    /// <param name="jobName">What the job is called. Quoted as data, not concatenated.</param>
    /// <param name="description">What it is for, so somebody finding it in a month knows.</param>
    public static string Wrap(string? script, string jobName, string? description = null)
    {
        var steps = SplitBatches(script);
        if (steps.Count == 0 || string.IsNullOrWhiteSpace(jobName)) return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Nine Lives - Generated SQL Server Agent Job (Blackcat Data Solutions)");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- Job:       {jobName}");
        sb.AppendLine($"-- Steps:     {steps.Count}");
        sb.AppendLine("--");
        sb.AppendLine("-- The job is created DISABLED and with NO SCHEDULE. Enable it, or run it by");
        sb.AppendLine("-- hand, when you actually want the restore to happen. Nothing here schedules");
        sb.AppendLine("-- anything: a script that quietly arranged to restore over a database at some");
        sb.AppendLine("-- future point would be a surprising thing to paste into SSMS.");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        sb.AppendLine("USE [msdb];");
        sb.AppendLine("GO");
        sb.AppendLine();

        var nameLiteral = TSql.EscapeLiteral(jobName);

        // Refuses rather than replaces. A job of this name may be somebody else's, and dropping it
        // to make room is not a decision a generated script gets to take.
        sb.AppendLine($"IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'{nameLiteral}')");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    RAISERROR(N'A job named ''{nameLiteral}'' already exists. Rename it, or drop that job first.', 16, 1);");
        sb.AppendLine("    RETURN;");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine();

        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine();
        sb.AppendLine("DECLARE @jobId BINARY(16);");
        sb.AppendLine();
        sb.AppendLine("EXEC msdb.dbo.sp_add_job");
        sb.AppendLine($"    @job_name = N'{nameLiteral}',");
        sb.AppendLine("    @enabled = 0,");
        sb.AppendLine($"    @description = N'{TSql.EscapeLiteral(description ?? "Created by Nine Lives.")}',");

        // Stops on failure by default, which is what sp_add_jobstep does anyway - stated here
        // because a restore chain that carries on after a failed step is a database left in a
        // state nobody asked for.
        sb.AppendLine("    @notify_level_eventlog = 2,");
        sb.AppendLine("    @job_id = @jobId OUTPUT;");
        sb.AppendLine();

        for (var i = 0; i < steps.Count; i++)
        {
            var isLast = i == steps.Count - 1;

            sb.AppendLine("EXEC msdb.dbo.sp_add_jobstep");
            sb.AppendLine("    @job_id = @jobId,");
            sb.AppendLine($"    @step_name = N'Step {i + 1} of {steps.Count}',");
            sb.AppendLine($"    @step_id = {i + 1},");
            sb.AppendLine("    @subsystem = N'TSQL',");
            sb.AppendLine("    @database_name = N'master',");

            // Any failure stops the job. The alternative - carrying on to the next RESTORE - would
            // apply a differential or a log to a database that never got its full.
            sb.AppendLine("    @on_success_action = " + (isLast ? "1," : "3,"));
            sb.AppendLine("    @on_fail_action = 2,");
            sb.AppendLine($"    @command = N'{TSql.EscapeLiteral(steps[i])}';");
            sb.AppendLine();
        }

        sb.AppendLine("EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId;");
        sb.AppendLine();
        sb.AppendLine("COMMIT TRANSACTION;");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine($"-- To run it now:      EXEC msdb.dbo.sp_start_job @job_name = N'{nameLiteral}';");
        sb.AppendLine($"-- To enable it:       EXEC msdb.dbo.sp_update_job @job_name = N'{nameLiteral}', @enabled = 1;");
        sb.AppendLine($"-- To remove it again: EXEC msdb.dbo.sp_delete_job @job_name = N'{nameLiteral}';");

        return sb.ToString();
    }

    /// <summary>
    /// A name for the job that says what it does and when it was made.
    ///
    /// Timestamped because these accumulate. Two refreshes of the same test database a week apart
    /// are two different jobs, and a name collision would otherwise refuse the second one.
    /// </summary>
    public static string SuggestName(string verb, string? targetDatabase, DateTime at) =>
        $"NineLives {verb} {targetDatabase ?? "database"} {at:yyyy-MM-dd HHmm}";

    /// <summary>
    /// The script split on GO, because an Agent step cannot contain one.
    ///
    /// GO is a batch separator understood by SSMS and sqlcmd, not by SQL Server - so a job step
    /// containing one fails with a syntax error at the moment the job runs, which for a restore
    /// handed over for a maintenance window is the worst possible moment to find out. One step per
    /// batch keeps the boundaries the script already declared, and makes a failure point at the
    /// statement that caused it rather than at the whole thing.
    /// </summary>
    public static List<string> SplitBatches(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return [];

        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            current.AppendLine(line.TrimEnd('\r'));
        }

        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            if (batch.Length > 0) batches.Add(batch);
            current.Clear();
        }
    }
}
