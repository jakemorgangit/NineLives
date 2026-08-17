using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Finding the backups the instance took that never reached the container (#451).
///
/// The estate this is for: fulls and diffs to blob, logs to a local or cluster disk. The container
/// holds an honest chain that stops at the last differential, and nothing tells anybody the logs
/// exist somewhere else - so a restore silently discards hours of recoverable time.
/// </summary>
public class BackupGapAnalyserTests
{
    private static readonly DateTime T0 = new(2026, 8, 14, 22, 0, 0);

    private static BackupHistoryEntry Recorded(
        BackupType type, DateTime at, string folder, decimal? lastLsn = null, long size = 1024,
        string database = "Sales", int stripes = 1)
    {
        var ext = type == BackupType.TransactionLog ? "trn" : "bak";
        var stamp = at.ToString("yyyyMMdd_HHmmss");

        var files = Enumerable.Range(1, stripes)
            .Select(n => stripes == 1
                ? $@"{folder}\{database}_{stamp}.{ext}"
                : $@"{folder}\{database}_{stamp}_{n}.{ext}")
            .ToList();

        return new BackupHistoryEntry
        {
            DatabaseName = database,
            Type = type,
            StartedAt = at,
            FinishedAt = at.AddMinutes(1),
            Files = files,
            LastLsn = lastLsn,
            BackupSizeBytes = size,
            FamilyCount = stripes
        };
    }

    private static BackupSet InContainer(
        BackupType type, DateTime at, decimal? lastLsn = null, string database = "Sales") => new()
        {
            SetId = at.ToString("yyyyMMdd_HHmmss"),
            Type = type,
            Timestamp = at,
            DatabaseName = database,
            LastLsn = lastLsn,
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = $"{database}_{at:yyyyMMdd_HHmmss}.bak",
                    BlobUrl = $"https://acct.blob.core.windows.net/c/{database}_{at:yyyyMMdd_HHmmss}.bak",
                    Type = type
                }
            ]
        };

    // ── the case the feature exists for ─────────────────────────────────────────

    [Fact]
    public void LogsWrittenToDiskAreReportedAsMissingFromTheContainer()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs"),
            Recorded(BackupType.TransactionLog, T0.AddHours(2), @"E:\SQLLogs"),
            Recorded(BackupType.TransactionLog, T0.AddHours(3), @"E:\SQLLogs")
        };

        // The container has the full, and nothing else.
        var container = new List<BackupSet> { InContainer(BackupType.Full, T0) };

        var locations = BackupGapAnalyser.Compare(history, container, "Sales");

        var logs = Assert.Single(locations);
        Assert.Equal(@"E:\SQLLogs", logs.Folder);
        Assert.Equal(3, logs.Backups.Count);
        Assert.Equal("3 log backups", logs.Summary);
        Assert.Equal(T0.AddHours(1), logs.Earliest);
        Assert.Equal(T0.AddHours(3), logs.Latest);
    }

    [Fact]
    public void TheRecoveryTimeTheContainerIsBehindIsReported()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(12), @"E:\SQLLogs")
        };
        var container = new List<BackupSet> { InContainer(BackupType.Full, T0) };

        var behind = BackupGapAnalyser.RecoveryTimeNotInContainer(history, container, "Sales");

        Assert.Equal(TimeSpan.FromHours(12), behind);
    }

    /// <summary>Two paths, two groups - a copy script is written per location.</summary>
    [Fact]
    public void MissingBackupsAreGroupedByTheFolderTheyWereWrittenTo()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs"),
            Recorded(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\shipping"),
            Recorded(BackupType.TransactionLog, T0.AddHours(3), @"E:\SQLLogs")
        };

        var locations = BackupGapAnalyser.Compare(history, [], "Sales");

        Assert.Equal(2, locations.Count);
        Assert.Contains(locations, l => l.Folder == @"E:\SQLLogs" && l.Backups.Count == 2);
        Assert.Contains(locations, l => l.Folder == @"\\nas01\shipping" && l.Backups.Count == 1);
    }

    // ── matching ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reliable identifier. A file that was renamed or written to two places is still the same
    /// backup, and its LSN range is the only thing that says so.
    /// </summary>
    [Fact]
    public void ABackupHeldUnderADifferentNameIsMatchedByItsLsn()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.Full, T0, @"C:\Backups", lastLsn: 4200m)
        };

        // Same backup, different name and a timestamp that would not match.
        var container = new List<BackupSet>
        {
            InContainer(BackupType.Full, T0.AddHours(9), lastLsn: 4200m)
        };

        Assert.Empty(BackupGapAnalyser.Compare(history, container, "Sales"));
    }

    /// <summary>
    /// And two different backups that share a timestamp are not conflated, because the LSNs
    /// disagree - a full and its log can start in the same second.
    /// </summary>
    [Fact]
    public void DifferentLsnsAtTheSameInstantAreDifferentBackups()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs", lastLsn: 5000m)
        };
        var container = new List<BackupSet>
        {
            InContainer(BackupType.TransactionLog, T0, lastLsn: 4000m)
        };

        Assert.Single(BackupGapAnalyser.Compare(history, container, "Sales"));
    }

    /// <summary>
    /// Without LSNs on both sides - a container that has never been audited - type and timestamp
    /// are what is left.
    /// </summary>
    [Fact]
    public void WithoutLsnsAMatchFallsBackToTypeAndTimestamp()
    {
        var history = new List<BackupHistoryEntry> { Recorded(BackupType.Full, T0, @"C:\Backups") };
        var container = new List<BackupSet> { InContainer(BackupType.Full, T0) };

        Assert.Empty(BackupGapAnalyser.Compare(history, container, "Sales"));
    }

    [Fact]
    public void TheSameInstantButADifferentTypeIsNotAMatch()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs")
        };
        var container = new List<BackupSet> { InContainer(BackupType.Full, T0) };

        Assert.Single(BackupGapAnalyser.Compare(history, container, "Sales"));
    }

    // ── things that must stay quiet ─────────────────────────────────────────────

    [Fact]
    public void AContainerHoldingEverythingReportsNoGap()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(1), @"C:\Backups")
        };
        var container = new List<BackupSet>
        {
            InContainer(BackupType.Full, T0),
            InContainer(BackupType.TransactionLog, T0.AddHours(1))
        };

        Assert.Empty(BackupGapAnalyser.Compare(history, container, "Sales"));
        Assert.Null(BackupGapAnalyser.RecoveryTimeNotInContainer(history, container, "Sales"));
    }

    /// <summary>Another database's backups are not this database's problem.</summary>
    [Fact]
    public void AnotherDatabasesBackupsAreIgnored()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs", database: "Payroll")
        };

        Assert.Empty(BackupGapAnalyser.Compare(history, [], "Sales"));
    }

    /// <summary>
    /// A container ahead of msdb is not behind it. Instance history gets trimmed, so a container
    /// holding backups older than anything msdb still remembers is the ordinary case, not a fault.
    /// </summary>
    [Fact]
    public void AContainerAheadOfTheInstanceHistoryIsNotReportedAsBehind()
    {
        var history = new List<BackupHistoryEntry> { Recorded(BackupType.Full, T0, @"C:\Backups") };
        var container = new List<BackupSet>
        {
            InContainer(BackupType.Full, T0),
            InContainer(BackupType.TransactionLog, T0.AddHours(6))
        };

        Assert.Null(BackupGapAnalyser.RecoveryTimeNotInContainer(history, container, "Sales"));
    }

    [Fact]
    public void AnEmptyHistoryReportsNothing()
    {
        Assert.Empty(BackupGapAnalyser.Compare([], [], "Sales"));
        Assert.Null(BackupGapAnalyser.RecoveryTimeNotInContainer([], [], "Sales"));
    }

    /// <summary>A recorded backup with no files recorded against it has no path to report.</summary>
    [Fact]
    public void ARecordedBackupWithNoFilesIsSkipped()
    {
        var history = new List<BackupHistoryEntry>
        {
            new() { DatabaseName = "Sales", Type = BackupType.TransactionLog, StartedAt = T0, Files = [] }
        };

        Assert.Empty(BackupGapAnalyser.Compare(history, [], "Sales"));
    }

    // ── the paths themselves ────────────────────────────────────────────────────

    /// <summary>
    /// These strings describe the SOURCE instance's file system, not this machine's, and they are
    /// about to be printed into a script that runs over there.
    /// </summary>
    [Theory]
    [InlineData(@"E:\SQLLogs\Sales_20260814.trn", @"E:\SQLLogs")]
    [InlineData(@"\\nas01\shipping\Sales.trn", @"\\nas01\shipping")]
    [InlineData(@"/var/opt/mssql/backups/Sales.trn", "/var/opt/mssql/backups")]
    [InlineData(@"Sales.trn", @"Sales.trn")]
    public void TheFolderIsTakenFromThePathAsTheSourceWroteIt(string device, string expected)
        => Assert.Equal(expected, BackupGapAnalyser.FolderOf(device));

    /// <summary>A striped set is one missing backup with several files to copy, not several.</summary>
    [Fact]
    public void AStripedBackupIsOneEntryWithEveryFileNamed()
    {
        var history = new List<BackupHistoryEntry>
        {
            Recorded(BackupType.Full, T0, @"E:\SQLLogs", stripes: 3)
        };

        var location = Assert.Single(BackupGapAnalyser.Compare(history, [], "Sales"));

        Assert.Single(location.Backups);
        Assert.Equal(3, location.FileCount);
    }
}
