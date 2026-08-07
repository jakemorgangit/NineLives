using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Restoring from a shared backup location produces a FROM DISK script (#149).
///
/// The interesting property is not that DISK appears - it is that everything else is identical.
/// WITH REPLACE, NORECOVERY on every step but the last, STOPAT on every log rather than only the
/// final one, the MOVE clauses, disconnecting sessions: all of it is the same logic that has been
/// through #110, #45 and #44, reached through an adapter rather than reimplemented.
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
        CheckpointLsn = 100,
        LastLsn = 200,
        BackupSizeBytes = 5_000_000,
        Files = files
    };

    private static string Generate(BackupHistoryChain chain, RestoreOptions? options = null)
        => new RestoreScriptGenerator().Generate(
            BackupHistoryChainAdapter.ToRestorableChain(chain, options?.StopAt),
            options ?? new RestoreOptions { TargetDatabaseName = "MyDb_Restored" });

    [Fact]
    public void AFullFromAShareIsRestoredFromDisk()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\MyDb_full.bak"), null, []);

        var script = Generate(chain);

        Assert.Contains(@"DISK = N'\\nas01\sql\MyDb_full.bak'", script);
        Assert.DoesNotContain("URL =", script);
    }

    /// <summary>
    /// A UNC path is not a URI. Percent-encoding one - which is right for a blob URL - would
    /// produce a path SQL Server cannot open.
    /// </summary>
    [Fact]
    public void APathWithSpacesIsNotUrlEncoded()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\SQL Backups\My Db\full backup.bak"), null, []);

        var script = Generate(chain);

        Assert.Contains(@"DISK = N'\\nas01\SQL Backups\My Db\full backup.bak'", script);
        Assert.DoesNotContain("%20", script);
    }

    [Fact]
    public void EveryStripeIsNamedInOrder()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak", @"\\nas01\sql\p3.bak"),
            null, []);

        var script = Generate(chain);

        var one = script.IndexOf(@"p1.bak", StringComparison.Ordinal);
        var two = script.IndexOf(@"p2.bak", StringComparison.Ordinal);
        var three = script.IndexOf(@"p3.bak", StringComparison.Ordinal);

        Assert.True(one > 0 && two > one && three > two, "stripes must be named in order");
    }

    /// <summary>
    /// The whole point of the adapter: a chain from a share gets the restore semantics the blob
    /// path already has, rather than a second implementation of them.
    /// </summary>
    [Fact]
    public void EveryStepButTheLastLeavesTheDatabaseRestoring()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"),
            Entry(BackupType.Differential, T0.AddHours(1), @"\\nas01\sql\diff.bak"),
            [Entry(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\sql\log1.trn"),
             Entry(BackupType.TransactionLog, T0.AddHours(3), @"\\nas01\sql\log2.trn")]);

        var script = Generate(chain);

        // Three NORECOVERY steps - full, differential, first log - then RECOVERY on the last.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(script, "NORECOVERY").Count);
        Assert.Contains("RECOVERY,", script);
        Assert.Contains("RESTORE LOG", script);
    }

    [Fact]
    public void PointInTimePutsStopAtOnEveryLog()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"),
            null,
            [Entry(BackupType.TransactionLog, T0.AddHours(1), @"\\nas01\sql\log1.trn"),
             Entry(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\sql\log2.trn")]);

        var script = Generate(chain, new RestoreOptions
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
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"), null, []);

        var script = Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = "MyDb_Restored",
            WithReplace = true
        });

        Assert.Contains("REPLACE", script);
    }

    // ── what the adapter carries across ─────────────────────────────────────────

    [Fact]
    public void TheFilesKnowTheyAreOnDiskRatherThanInAContainer()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\full.bak"), null, []);

        var restorable = BackupHistoryChainAdapter.ToRestorableChain(chain);
        var file = Assert.Single(restorable.FullSet.Files);

        Assert.True(file.IsOnDisk);
        Assert.Equal(@"\\nas01\sql\full.bak", file.RestoreDevice);

        // No URL is invented. A path in that field would be the kind of quiet lie that ends in a
        // restore aimed at the wrong device.
        Assert.Equal(string.Empty, file.BlobUrl);
    }

    /// <summary>
    /// msdb records size per SET, not per stripe. Repeating it on every file would report a striped
    /// backup as several times its real size.
    /// </summary>
    [Fact]
    public void AStripedBackupIsNotCountedOncePerStripe()
    {
        var chain = new BackupHistoryChain(
            Entry(BackupType.Full, T0, @"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak"), null, []);

        var restorable = BackupHistoryChainAdapter.ToRestorableChain(chain);

        Assert.Equal(5_000_000, restorable.TotalSizeBytes);
    }

    [Fact]
    public void ACopyOnlyFullStaysCopyOnlyThroughTheAdapter()
    {
        var full = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(2),
            IsCopyOnly = true,
            Files = [@"\\nas01\sql\copyonly.bak"]
        };

        var restorable = BackupHistoryChainAdapter.ToRestorableChain(new BackupHistoryChain(full, null, []));

        Assert.True(restorable.FullSet.IsCopyOnly);
    }
}
