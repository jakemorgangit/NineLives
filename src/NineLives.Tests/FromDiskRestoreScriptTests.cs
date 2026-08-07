using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Backups on a shared path produce a FROM DISK script (#149, #165).
///
/// The interesting property is not that DISK appears - it is that everything else is identical, and
/// that these go through the SAME builder and generator the blob path uses rather than an adapter
/// written alongside them. WITH REPLACE, NORECOVERY on every step but the last, STOPAT on every log
/// rather than only the final one, the MOVE clauses: all of it is the logic that has been through
/// #110, #45 and #44, reached by handing it ordinary backup sets whose files happen to carry a path.
///
/// So a shared location gets the whole of that behaviour for free, and a fix to any of it applies to
/// both media at once. That is the entire argument for a source abstraction over a second screen.
/// </summary>
public class FromDiskRestoreScriptTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupHistoryEntry Entry(BackupType type, DateTime at, params string[] files) => new()
    {
        DatabaseName = "MyDb",
        ServerName = "SRV01",
        Type = type,
        StartedAt = at,
        FinishedAt = at.AddMinutes(2),
        CheckpointLsn = type == BackupType.Full ? 100 : null,
        DatabaseBackupLsn = type == BackupType.Differential ? 100 : null,
        LastLsn = 200,
        BackupSizeBytes = 5_000_000,
        Files = files
    };

    /// <summary>
    /// The real path: msdb history becomes sets, the ordinary chain builder computes restore
    /// points, and the ordinary generator writes the script. Nothing here is specific to disk.
    /// </summary>
    private static string Generate(
        IEnumerable<BackupHistoryEntry> history,
        RestoreOptions? options = null,
        BackupPathMapping? mapping = null)
    {
        var sets = BackupHistoryInventory.ToSets(history, mapping);
        var builder = new BackupChainBuilder();

        var point = builder.ComputeRestorePoints(sets).Last();
        var chain = builder.BuildChainFromRestorePoint(point);
        chain.StopAt = options?.StopAt;

        return new RestoreScriptGenerator().Generate(
            chain, options ?? new RestoreOptions { TargetDatabaseName = "MyDb_Restored" });
    }

    [Fact]
    public void AFullFromAShareIsRestoredFromDisk()
    {
        var script = Generate([Entry(BackupType.Full, T0, @"\\nas01\sql\MyDb_full.bak")]);

        Assert.Contains(@"DISK = N'\\nas01\sql\MyDb_full.bak'", script);
        Assert.DoesNotContain("URL =", script);
    }

    /// <summary>
    /// A UNC path is not a URI. Percent-encoding one - which is right for a blob URL - would produce
    /// a path SQL Server cannot open.
    /// </summary>
    [Fact]
    public void APathWithSpacesIsNotUrlEncoded()
    {
        var script = Generate([Entry(BackupType.Full, T0, @"\\nas01\SQL Backups\My Db\full backup.bak")]);

        Assert.Contains(@"DISK = N'\\nas01\SQL Backups\My Db\full backup.bak'", script);
        Assert.DoesNotContain("%20", script);
    }

    [Fact]
    public void EveryStripeIsNamedInOrder()
    {
        var script = Generate(
            [Entry(BackupType.Full, T0, @"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak", @"\\nas01\sql\p3.bak")]);

        var one = script.IndexOf("p1.bak", StringComparison.Ordinal);
        var two = script.IndexOf("p2.bak", StringComparison.Ordinal);
        var three = script.IndexOf("p3.bak", StringComparison.Ordinal);

        Assert.True(one > 0 && two > one && three > two, "stripes must be named in order");
    }

    /// <summary>
    /// The whole point of doing this through the existing workflow: a chain from a share gets the
    /// restore semantics the blob path already has, rather than a second implementation of them.
    /// </summary>
    [Fact]
    public void EveryStepButTheLastLeavesTheDatabaseRestoring()
    {
        var script = Generate(
        [
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"),
            Entry(BackupType.Differential, T0.AddHours(1), @"\\nas01\sql\diff.bak"),
            Entry(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\sql\log1.trn"),
            Entry(BackupType.TransactionLog, T0.AddHours(3), @"\\nas01\sql\log2.trn")
        ]);

        // Three NORECOVERY steps - full, differential, first log - then RECOVERY on the last.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(script, "NORECOVERY").Count);
        Assert.Contains("RECOVERY,", script);
        Assert.Contains("RESTORE LOG", script);
    }

    [Fact]
    public void PointInTimePutsStopAtOnEveryLog()
    {
        var script = Generate(
        [
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"),
            Entry(BackupType.TransactionLog, T0.AddHours(1), @"\\nas01\sql\log1.trn"),
            Entry(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\sql\log2.trn")
        ],
        new RestoreOptions
        {
            TargetDatabaseName = "MyDb_Restored",
            StopAt = T0.AddHours(1).AddMinutes(30)
        });

        // On EVERY log, not just the last: a log restored without it rolls past the target and the
        // ones after it then have nothing to apply.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "STOPAT").Count);
    }

    [Fact]
    public void ReplaceIsCarriedThroughFromTheOptions()
    {
        var script = Generate(
            [Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak")],
            new RestoreOptions { TargetDatabaseName = "MyDb_Restored", WithReplace = true });

        Assert.Contains("REPLACE", script);
    }

    /// <summary>
    /// The substitution reaches the script, because it was applied where the sets were built rather
    /// than somewhere the generator would have to know about.
    /// </summary>
    [Fact]
    public void TheScriptNamesTheFilesTheWayTheTargetReachesThem()
    {
        var script = Generate(
            [Entry(BackupType.Full, T0, @"E:\SQLBackups\MyDb\full.bak")],
            mapping: new BackupPathMapping(@"E:\SQLBackups", @"\\SRV01\SQLBackups"));

        Assert.Contains(@"DISK = N'\\SRV01\SQLBackups\MyDb\full.bak'", script);
        Assert.DoesNotContain(@"E:\SQLBackups", script);
    }

    /// <summary>
    /// A differential from a share reaches the script through the LSN pairing, so what is restored
    /// is the full the differential was actually taken against.
    /// </summary>
    [Fact]
    public void ADifferentialRestoresOnTopOfTheFullItsLsnNames()
    {
        var script = Generate(
        [
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"),
            Entry(BackupType.Differential, T0.AddHours(1), @"\\nas01\sql\diff.bak")
        ]);

        var full = script.IndexOf("full.bak", StringComparison.Ordinal);
        var diff = script.IndexOf("diff.bak", StringComparison.Ordinal);

        Assert.True(full > 0 && diff > full, "the full must be restored before the differential");
    }
}
