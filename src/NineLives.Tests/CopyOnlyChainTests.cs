using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A copy-only full backup does NOT reset the differential base — the base LSN stays with the
/// last regular full — so a differential restored on top of a copy-only full fails with
/// error 3136.
///
/// Ola writes copy-only backups into the same FULL folder with the same naming, so an ad-hoc
/// copy-only taken mid-week silently became the base for every later differential, and through
/// the log-chain code broke every restore point from that differential onward until the next
/// regular full — the entire recent end of the timeline.
///
/// Copy-only sets stay first-class everywhere else: valid Full restore points, and valid anchors
/// for a log chain.
/// </summary>
public class CopyOnlyChainTests
{
    private readonly BackupChainBuilder _builder = new();

    private static DateTime T(int day, int hour = 0) => new(2026, 8, day, hour, 0, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp, bool copyOnly = false) => new()
    {
        SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = timestamp,
        IsCopyOnly = copyOnly,
        DatabaseName = "Sales",
        Files =
        [
            new BackupFileInfo
            {
                BlobName = $"{timestamp:yyyyMMdd_HHmmss}.bak",
                Type = type,
                IsCopyOnly = copyOnly,
                SizeBytes = 100
            }
        ]
    };

    [Fact]
    public void Differential_SkipsCopyOnlyFull_AndUsesTheLastRegularFull()
    {
        // Sunday full, Wednesday ad-hoc copy-only, Thursday differential.
        var sundayFull = Set(BackupType.Full, T(2));
        var wedCopyOnly = Set(BackupType.Full, T(5), copyOnly: true);
        var thuDiff = Set(BackupType.Differential, T(6));

        var points = _builder.ComputeRestorePoints([sundayFull, wedCopyOnly, thuDiff]);
        var diffPoint = Assert.Single(points, p => p.Type == BackupType.Differential);

        Assert.Same(sundayFull, diffPoint.RequiredFullSet);
        Assert.NotSame(wedCopyOnly, diffPoint.RequiredFullSet);
    }

    [Fact]
    public void CopyOnlyFull_IsStillOfferedAsItsOwnRestorePoint()
    {
        // Excluding it from differential bases must not remove it from the timeline: restoring
        // a copy-only full on its own is perfectly valid.
        var full = Set(BackupType.Full, T(2));
        var copyOnly = Set(BackupType.Full, T(5), copyOnly: true);

        var points = _builder.ComputeRestorePoints([full, copyOnly]);

        Assert.Equal(2, points.Count(p => p.Type == BackupType.Full));
        Assert.Contains(points, p => ReferenceEquals(p.PrimarySet, copyOnly));
    }

    [Fact]
    public void CopyOnlyFull_CanAnchorALogChain()
    {
        // Copy-only backups do not break the log chain, so copy-only + logs is a valid restore.
        var copyOnly = Set(BackupType.Full, T(5), copyOnly: true);
        var log = Set(BackupType.TransactionLog, T(5, 6));

        var points = _builder.ComputeRestorePoints([copyOnly, log]);
        var logPoint = Assert.Single(points, p => p.Type == BackupType.TransactionLog);

        Assert.Same(copyOnly, logPoint.RequiredFullSet);
        Assert.Empty(logPoint.RequiredDiffSets);
    }

    [Fact]
    public void LogChainAnchoredOnCopyOnly_NeverPullsInADifferential()
    {
        // The defect that broke the whole recent timeline: the differential fell inside the
        // copy-only full's range and was picked up as latestDiff, producing
        // copy-only + diff + logs — which SQL Server rejects with 3136.
        var sundayFull = Set(BackupType.Full, T(2));
        var wedCopyOnly = Set(BackupType.Full, T(5), copyOnly: true);
        var thuDiff = Set(BackupType.Differential, T(6));
        var friLog = Set(BackupType.TransactionLog, T(7));

        var points = _builder.ComputeRestorePoints([sundayFull, wedCopyOnly, thuDiff, friLog]);
        var logPoint = Assert.Single(points, p => p.Type == BackupType.TransactionLog);

        Assert.Empty(logPoint.RequiredDiffSets);
        Assert.Same(wedCopyOnly, logPoint.RequiredFullSet);
    }

    [Fact]
    public void DifferentialStillJoinsALogChainAnchoredOnItsOwnBase()
    {
        // The rule must not over-correct: with no copy-only involved, the differential still
        // belongs to the chain exactly as before.
        var full = Set(BackupType.Full, T(2));
        var diff = Set(BackupType.Differential, T(4));
        var log = Set(BackupType.TransactionLog, T(5));

        var points = _builder.ComputeRestorePoints([full, diff, log]);
        var logPoint = Assert.Single(points, p => p.Type == BackupType.TransactionLog);

        Assert.Same(full, logPoint.RequiredFullSet);
        Assert.Same(diff, Assert.Single(logPoint.RequiredDiffSets));
    }

    [Fact]
    public void DifferentialWithOnlyCopyOnlyFullsAvailable_GetsNoRestorePoint()
    {
        // There is no valid base, so offering the differential at all would generate a script
        // that cannot succeed.
        var copyOnly = Set(BackupType.Full, T(2), copyOnly: true);
        var diff = Set(BackupType.Differential, T(4));

        var points = _builder.ComputeRestorePoints([copyOnly, diff]);

        Assert.DoesNotContain(points, p => p.Type == BackupType.Differential);
        Assert.Single(points); // just the copy-only full
    }

    [Fact]
    public void CopyOnlyBetweenFullAndDiff_DoesNotStrandTheDifferential()
    {
        // End-to-end shape from the issue: the differential must still be restorable, and its
        // chain must name the Sunday full.
        var sundayFull = Set(BackupType.Full, T(2));
        var wedCopyOnly = Set(BackupType.Full, T(5), copyOnly: true);
        var thuDiff = Set(BackupType.Differential, T(6));

        var points = _builder.ComputeRestorePoints([sundayFull, wedCopyOnly, thuDiff]);
        var diffPoint = points.Single(p => p.Type == BackupType.Differential);
        var chain = _builder.BuildChainFromRestorePoint(diffPoint);

        Assert.Same(sundayFull, chain.FullSet);
        Assert.Same(thuDiff, Assert.Single(chain.DiffSets));
    }
}
