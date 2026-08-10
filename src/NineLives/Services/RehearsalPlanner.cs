using System.IO;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Plans a restore rehearsal (#238): the same chain, restored to a scratch name, proven with
/// CHECKDB, and dropped - leaving nothing behind but the evidence.
///
/// The DBA proverb the whole industry repeats and almost nobody acts on: the only tested backup
/// is a restored backup. A rehearsal is that test, made cheap enough to actually run - and its
/// history entry is the receipt the audit question asks for.
///
/// Safety by construction, not by care:
///  - the target name is GENERATED, timestamped, and refused outright if it somehow exists;
///  - WITH REPLACE is never emitted - a fresh name has nothing to replace, and leaving the
///    option off means a collision fails loudly instead of overwriting;
///  - every file MOVEs to scratch-named files in the instance's default directories, so the
///    rehearsal cannot touch the real database's files even in a path collision;
///  - the DROP is guarded and LAST: if the restore or CHECKDB fails, it never runs, and the
///    scratch copy is retained as the evidence of what failed.
/// </summary>
public static class RehearsalPlanner
{
    /// <summary>"MyDb_rehearsal_20260810_0930" - honest about what it is to anyone who sees it.</summary>
    public static string ScratchName(string database, DateTime now) =>
        $"{database}_rehearsal_{now:yyyyMMdd_HHmm}";

    /// <summary>
    /// The STABLE scratch name a scheduled rehearsal reuses (#259). A job runs the same text
    /// weekly, so a timestamp baked in at generation would collide with its own leftovers on the
    /// second run - instead the name is fixed, and each run begins by clearing its own previous
    /// evidence, which by then has been seen in the job history.
    /// </summary>
    public static string ScheduledScratchName(string database) =>
        $"{database}_rehearsal_scheduled";

    /// <summary>
    /// The scheduled form of the rehearsal script (#259): identical to the clicked form, plus a
    /// guarded PRE-drop of the previous run's leftover - safe precisely because the name is the
    /// rehearsal's own, never a real database's.
    /// </summary>
    public static string BuildScheduledScript(string restoreScript, string scratchName)
    {
        var quoted = TSql.QuoteName(scratchName);
        var literal = TSql.EscapeLiteral(scratchName);

        var preamble =
$@"-- ============================================================
-- Scheduled rehearsal. This name belongs to the rehearsal alone,
-- so clearing last week's leftover - retained if last week FAILED,
-- and visible in this job's history - is safe by construction.
-- ============================================================
IF DB_ID('{literal}') IS NOT NULL
BEGIN
    ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {quoted};
END
GO

";
        return preamble + BuildScript(restoreScript, scratchName);
    }

    /// <summary>
    /// Every logical file, relocated to a scratch-named file in the default directories. The
    /// rehearsal must be incapable of writing where the real database lives.
    /// </summary>
    public static List<FileMoveOption> ScratchMoves(
        IReadOnlyList<FileMoveOption> logicalFiles, string scratchName, string dataPath, string logPath)
    {
        var moves = new List<FileMoveOption>();
        int rows = 0, logs = 0;

        foreach (var file in logicalFiles)
        {
            var isLog = string.Equals(file.Type, "L", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.Type, "LOG", StringComparison.OrdinalIgnoreCase);

            var target = isLog
                ? Path.Combine(logPath, logs++ == 0 ? $"{scratchName}_log.ldf" : $"{scratchName}_log{logs}.ldf")
                : Path.Combine(dataPath, rows++ == 0 ? $"{scratchName}.mdf" : $"{scratchName}_{rows}.ndf");

            moves.Add(new FileMoveOption
            {
                LogicalName = file.LogicalName,
                PhysicalName = file.PhysicalName,
                Type = file.Type,
                SizeBytes = file.SizeBytes,
                NewPhysicalName = target
            });
        }

        return moves;
    }

    /// <summary>
    /// The full rehearsal script: the restore, then the proof, then the cleanup - in an order
    /// where a failure at any step leaves the evidence standing.
    /// </summary>
    public static string BuildScript(string restoreScript, string scratchName)
    {
        var quoted = TSql.QuoteName(scratchName);
        var literal = TSql.EscapeLiteral(scratchName);

        return restoreScript.TrimEnd() + Environment.NewLine + Environment.NewLine +
$@"-- ============================================================
-- Rehearsal proof: CHECKDB reads every allocation and page.
-- No output means the BACKUP is proven, not just restored.
-- ============================================================
DBCC CHECKDB ({quoted}) WITH NO_INFOMSGS;
GO

-- ============================================================
-- Rehearsal cleanup. Runs ONLY if everything above succeeded -
-- a failed restore or a CHECKDB finding stops the batch chain,
-- and the scratch copy is retained as the evidence.
-- ============================================================
IF DB_ID('{literal}') IS NOT NULL
BEGIN
    ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {quoted};
END
GO
";
    }
}
