using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Backups that exist but reach no restore point (#46).
///
/// The chain builder bounds each full's log range with strict inequalities at both ends, so a log
/// whose timestamp exactly equals a full's belongs to neither the earlier full's range nor the
/// colliding full's own. It vanishes from the timeline while still being counted in the header.
///
/// The collision is not exotic: timestamps come from filenames and the seconds group is optional,
/// so under HHMM naming any full and log starting in the same minute collide - a nightly full plus
/// quarter-hourly logs does it every day.
///
/// These tests pin the reporting, not a repair. Widening the bound would force the log to restore
/// after the full always, which is wrong whenever the log job fired first and finished before the
/// full's checkpoint. Only the LSNs can tell those apart.
/// </summary>
public class ChainReachabilityTests
{
    private static readonly DateTime Day = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Unspecified);

    private static BackupSet Set(BackupType type, DateTime timestamp, string name) => new()
    {
        Type = type,
        Timestamp = timestamp,
        SetId = name,
        DatabaseName = "MyDb",
        Files = [new BackupFileInfo { BlobName = name, SizeBytes = 1024, Type = type }]
    };

    private static (List<BackupSet> Sets, List<RestorePoint> Points) Build(params BackupSet[] sets)
    {
        var list = sets.ToList();
        return (list, new BackupChainBuilder().ComputeRestorePoints(list));
    }

    // ── the defect ──────────────────────────────────────────────────────────────

    [Fact]
    public void ALogAtAFullsExactTimestampReachesNoRestorePoint()
    {
        // Establishes the behaviour the warning exists for. If this ever starts failing because
        // the builder learned to place the log, the warning below should go with it.
        var full = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL_20260804_2200.bak");
        var collide = Set(BackupType.TransactionLog, Day.AddHours(22), "MyDb_LOG_20260804_2200.trn");
        var later = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(30), "MyDb_LOG_20260804_2230.trn");

        var (_, points) = Build(full, collide, later);

        var reached = points.Any(p => ReferenceEquals(p.PrimarySet, collide))
                   || points.Any(p => p.RequiredLogSets.Contains(collide));
        Assert.False(reached);

        // ...while the log that does not collide is fine, so this is about the collision and not
        // about logs generally.
        Assert.Contains(points, p => ReferenceEquals(p.PrimarySet, later));
    }

    [Fact]
    public void TheUnreachableLogIsReported()
    {
        var full = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL_20260804_2200.bak");
        var collide = Set(BackupType.TransactionLog, Day.AddHours(22), "MyDb_LOG_20260804_2200.trn");
        var later = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(30), "MyDb_LOG_20260804_2230.trn");

        var (sets, points) = Build(full, collide, later);
        var issues = new BackupChainValidator().ValidateReachability(sets, points);

        var issue = Assert.Single(issues);
        Assert.Equal(ChainIssueSeverity.Warning, issue.Severity);
        Assert.Contains("1 log backup(s) are not reachable", issue.Title);
        Assert.Contains("share an exact timestamp with a full backup", issue.Detail);
    }

    [Fact]
    public void TheReportNamesTheEarliestAffectedTime()
    {
        var full = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL_20260804_2200.bak");
        var collide = Set(BackupType.TransactionLog, Day.AddHours(22), "MyDb_LOG_20260804_2200.trn");
        var later = Set(BackupType.TransactionLog, Day.AddHours(23), "MyDb_LOG_20260804_2300.trn");

        var (sets, points) = Build(full, collide, later);
        var issue = Assert.Single(new BackupChainValidator().ValidateReachability(sets, points));

        Assert.Contains("2026-08-04 22:00:00", issue.Detail);
    }

    [Fact]
    public void CollisionsAtASecondFullAreAlsoCaught()
    {
        // The issue's boundary sweep found the same behaviour at the later full, not just the first.
        var first = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL_20260804_2200.bak");
        var between = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(30), "MyDb_LOG_20260804_2230.trn");
        var second = Set(BackupType.Full, Day.AddHours(23), "MyDb_FULL_20260804_2300.bak");
        var collide = Set(BackupType.TransactionLog, Day.AddHours(23), "MyDb_LOG_20260804_2300.trn");

        var (sets, points) = Build(first, between, second, collide);
        var issue = Assert.Single(new BackupChainValidator().ValidateReachability(sets, points));

        Assert.Contains("1 log backup(s) are not reachable", issue.Title);
    }

    // ── no false positives ──────────────────────────────────────────────────────

    [Fact]
    public void AnOrdinaryChainReportsNothing()
    {
        var full = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL.bak");
        var log1 = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(15), "MyDb_LOG_2215.trn");
        var log2 = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(30), "MyDb_LOG_2230.trn");

        var (sets, points) = Build(full, log1, log2);

        Assert.Empty(new BackupChainValidator().ValidateReachability(sets, points));
    }

    [Fact]
    public void LogsBeforeTheFirstFullAreLeftToTheInventoryCheck()
    {
        // ValidateInventory already explains these. Reporting them twice in different words makes
        // both messages easier to ignore.
        var early = Set(BackupType.TransactionLog, Day.AddHours(20), "MyDb_LOG_2000.trn");
        var full = Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL.bak");
        var after = Set(BackupType.TransactionLog, Day.AddHours(22).AddMinutes(30), "MyDb_LOG_2230.trn");

        var (sets, points) = Build(early, full, after);

        Assert.Empty(new BackupChainValidator().ValidateReachability(sets, points));
        Assert.Contains(
            new BackupChainValidator().ValidateInventory(sets),
            i => i.Title.Contains("predate the earliest full"));
    }

    [Fact]
    public void NothingIsReportedWhenThereAreNoLogs()
    {
        var (sets, points) = Build(Set(BackupType.Full, Day.AddHours(22), "MyDb_FULL.bak"));

        Assert.Empty(new BackupChainValidator().ValidateReachability(sets, points));
    }

    [Fact]
    public void NothingIsReportedWhenThereAreNoRestorePointsAtAll()
    {
        // A container with only logs produces no points; ValidateInventory reports the missing
        // full, and this check must not pile on with a second warning about all of them.
        var (sets, points) = Build(
            Set(BackupType.TransactionLog, Day.AddHours(22), "MyDb_LOG_2200.trn"));

        Assert.Empty(points);
        Assert.Empty(new BackupChainValidator().ValidateReachability(sets, points));
    }
}
