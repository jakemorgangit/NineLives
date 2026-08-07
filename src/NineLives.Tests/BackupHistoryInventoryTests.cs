using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// msdb history becomes the same inventory the restore workflow already works on (#149, #165).
///
/// The point of these is that nothing downstream needs to know. If a set read from an instance's
/// history is a <see cref="BackupSet"/> like any other - carrying its database, its server, its
/// type, its time and its files - then the chain builder, the timeline, the options, the script and
/// the execute path all apply unchanged, which is what this widening is for.
/// </summary>
public class BackupHistoryInventoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupHistoryEntry Entry(
        BackupType type, DateTime at, string server = "SRV01", params string[] files) => new()
    {
        DatabaseName = "MyDb",
        ServerName = server,
        Type = type,
        StartedAt = at,
        FinishedAt = at.AddMinutes(2),
        CheckpointLsn = 100,
        LastLsn = 200,
        BackupSizeBytes = 5_000_000,
        Files = files.Length == 0 ? [@"\\nas01\sql\full.bak"] : files
    };

    // ── it is an ordinary backup set ────────────────────────────────────────────

    [Fact]
    public void AHistoryEntryBecomesASetTheRestWorkflowRecognises()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0));

        Assert.Equal("MyDb", set.DatabaseName);
        Assert.Equal("SRV01", set.ServerName);
        Assert.Equal(BackupType.Full, set.Type);
        Assert.Equal(T0, set.Timestamp);
        Assert.Equal(@"\\nas01\sql\full.bak", Assert.Single(set.Files).RestoreDevice);
    }

    /// <summary>
    /// The file says where it lives, so the script generator addresses it as DISK without anything
    /// else having to agree with it.
    /// </summary>
    [Fact]
    public void TheFilesKnowTheyAreOnDiskRatherThanInAContainer()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0));
        var file = Assert.Single(set.Files);

        Assert.True(file.IsOnDisk);

        // No URL is invented. A path in that field would be the kind of quiet lie that ends in a
        // restore aimed at the wrong device.
        Assert.Equal(string.Empty, file.BlobUrl);
    }

    /// <summary>
    /// The instance's own record of when it ran, on its own clock. Not parsed out of a name and not
    /// read off an upload, so it is not marked approximate and nothing offers to convert it.
    /// </summary>
    [Fact]
    public void TheTimeIsTreatedAsTheStrongestReadingTheAppHas()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0));

        Assert.Equal(BackupTimestampSource.BackupHeader, set.TimestampSource);
        Assert.False(set.IsTimestampApproximate);
    }

    /// <summary>
    /// msdb records one string. Keeping SRV01\PROD whole as the server would make it a different
    /// server from the SRV01 a container path yields for the same machine, so the two sources would
    /// never agree - and the server filter, which splits on the backslash, would match neither.
    /// </summary>
    [Fact]
    public void ANamedInstanceIsSplitTheWayThePathParserWouldSplitIt()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0, @"SRV01\PROD"));

        Assert.Equal("SRV01", set.ServerName);
        Assert.Equal("PROD", set.InstanceName);
        Assert.Equal(@"SRV01\PROD", set.ServerDisplay);
        Assert.True(set.MatchesServer(@"SRV01\PROD"));
        Assert.False(set.MatchesServer(@"SRV01\TEST"));
    }

    [Fact]
    public void ADefaultInstanceHasNoInstanceName()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0, "SRV01"));

        Assert.Equal("SRV01", set.ServerName);
        Assert.Null(set.InstanceName);
    }

    [Fact]
    public void ACopyOnlyFullStaysCopyOnly()
    {
        var entry = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", Type = BackupType.Full,
            StartedAt = T0, FinishedAt = T0.AddMinutes(2),
            IsCopyOnly = true, Files = [@"\\nas01\sql\copyonly.bak"]
        };

        Assert.True(BackupHistoryInventory.ToSet(entry).IsCopyOnly);
    }

    /// <summary>
    /// msdb records size per SET, not per stripe. Repeating it on every file would report a striped
    /// backup as several times its real size.
    /// </summary>
    [Fact]
    public void AStripedBackupIsNotCountedOncePerStripe()
    {
        var set = BackupHistoryInventory.ToSet(
            Entry(BackupType.Full, T0, "SRV01", @"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak"));

        Assert.Equal(2, set.Files.Count);
        Assert.Equal(5_000_000, set.TotalSizeBytes);
    }

    // ── the path substitution ───────────────────────────────────────────────────

    /// <summary>
    /// Applied once, here, so everything downstream names the same file. A substitution applied in
    /// some places and not others is how a screen ends up checking one path and restoring another.
    /// </summary>
    [Fact]
    public void ThePathSubstitutionIsAppliedOnceAtTheSource()
    {
        var set = BackupHistoryInventory.ToSet(
            Entry(BackupType.Full, T0, "SRV01", @"E:\SQLBackups\MyDb\full.bak"),
            new BackupPathMapping(@"E:\SQLBackups", @"\\SRV01\SQLBackups"));

        Assert.Equal(@"\\SRV01\SQLBackups\MyDb\full.bak", Assert.Single(set.Files).RestoreDevice);
    }

    [Fact]
    public void WithNoSubstitutionThePathsAreLeftExactlyAsMsdbRecordedThem()
    {
        var set = BackupHistoryInventory.ToSet(Entry(BackupType.Full, T0));

        Assert.Equal(@"\\nas01\sql\full.bak", Assert.Single(set.Files).RestoreDevice);
    }

    /// <summary>
    /// msdb keeps the record of a backup long after its files have been deleted, archived or
    /// pruned. Carried through, such a row becomes a set with nothing in it - which the chain
    /// builder offers as a restore point and which then fails at RESTORE with nothing to read.
    /// </summary>
    [Fact]
    public void ABackupWhoseFilesAreGoneIsNotOfferedAtAll()
    {
        var kept = Entry(BackupType.Full, T0);
        var pruned = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", Type = BackupType.Full,
            StartedAt = T0.AddDays(-30), FinishedAt = T0.AddDays(-30).AddMinutes(2),
            Files = []
        };

        var sets = BackupHistoryInventory.ToSets([pruned, kept]);

        Assert.Single(sets);
        Assert.Equal(T0, sets[0].Timestamp);
    }

    [Fact]
    public void SetsComeBackOldestFirst()
    {
        var sets = BackupHistoryInventory.ToSets(
        [
            Entry(BackupType.TransactionLog, T0.AddHours(2)),
            Entry(BackupType.Full, T0),
            Entry(BackupType.Differential, T0.AddHours(1))
        ]);

        Assert.Equal([BackupType.Full, BackupType.Differential, BackupType.TransactionLog],
            sets.Select(s => s.Type));
    }
}
