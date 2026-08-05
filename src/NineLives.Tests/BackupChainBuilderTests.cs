using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class BackupChainBuilderTests
{
    private readonly BackupChainBuilder _builder = new();

    // ---- Arrange helpers -------------------------------------------------

    private static DateTime T(int hour, int minute = 0)
        => new(2026, 1, 15, hour, minute, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp, int fileCount = 1, long fileSize = 100)
    {
        var set = new BackupSet
        {
            SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
            Type = type,
            Timestamp = timestamp
        };
        for (int i = 0; i < fileCount; i++)
        {
            set.Files.Add(new BackupFileInfo
            {
                BlobName = $"db_{set.SetId}_{i + 1}.bak",
                Type = type,
                SizeBytes = fileSize
            });
        }
        return set;
    }

    private static BackupFileInfo File(BackupType type, DateTime lastModified, DateTime? backupStartDate = null)
        => new()
        {
            BlobName = $"file_{lastModified:yyyyMMdd_HHmmss}.bak",
            Type = type,
            LastModified = new DateTimeOffset(lastModified, TimeSpan.Zero),
            BackupStartDate = backupStartDate
        };

    // ---- ComputeRestorePoints: fulls -------------------------------------

    [Fact]
    public void ComputeRestorePoints_EmptyInput_ReturnsEmptyList()
    {
        var points = _builder.ComputeRestorePoints([]);

        Assert.Empty(points);
    }

    [Fact]
    public void ComputeRestorePoints_FullsOnly_OnePointPerFullWithNoDiffsOrLogs()
    {
        var full1 = Set(BackupType.Full, T(1));
        var full2 = Set(BackupType.Full, T(5));

        var points = _builder.ComputeRestorePoints([full2, full1]);

        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Equal(BackupType.Full, p.Type));

        Assert.Same(full1, points[0].PrimarySet);
        Assert.Same(full1, points[0].RequiredFullSet);
        Assert.Equal(T(1), points[0].Timestamp);
        Assert.Empty(points[0].RequiredDiffSets);
        Assert.Empty(points[0].RequiredLogSets);

        Assert.Same(full2, points[1].PrimarySet);
        Assert.Same(full2, points[1].RequiredFullSet);
        Assert.Equal(T(5), points[1].Timestamp);
    }

    // ---- ComputeRestorePoints: diffs -------------------------------------

    [Fact]
    public void ComputeRestorePoints_Diffs_MapToLatestFullAtOrBeforeThem()
    {
        var full1 = Set(BackupType.Full, T(1));
        var full2 = Set(BackupType.Full, T(5));
        var diff1 = Set(BackupType.Differential, T(3));
        var diff2 = Set(BackupType.Differential, T(7));

        var points = _builder.ComputeRestorePoints([full1, full2, diff1, diff2]);

        var diffPoints = points.Where(p => p.Type == BackupType.Differential).ToList();
        Assert.Equal(2, diffPoints.Count);

        Assert.Same(diff1, diffPoints[0].PrimarySet);
        Assert.Same(full1, diffPoints[0].RequiredFullSet);
        Assert.Equal([diff1], diffPoints[0].RequiredDiffSets);
        Assert.Empty(diffPoints[0].RequiredLogSets);

        Assert.Same(diff2, diffPoints[1].PrimarySet);
        Assert.Same(full2, diffPoints[1].RequiredFullSet);
        Assert.Equal([diff2], diffPoints[1].RequiredDiffSets);
    }

    [Fact]
    public void ComputeRestorePoints_DiffAtExactFullTimestamp_MapsToThatFull()
    {
        var full1 = Set(BackupType.Full, T(1));
        var full2 = Set(BackupType.Full, T(5));
        var diff = Set(BackupType.Differential, T(5));

        var points = _builder.ComputeRestorePoints([full1, full2, diff]);

        var diffPoint = Assert.Single(points, p => p.Type == BackupType.Differential);
        Assert.Same(full2, diffPoint.RequiredFullSet);

        // Stable sort: at equal timestamps the full's point precedes the diff's point.
        Assert.Equal(3, points.Count);
        Assert.Same(full2, points[1].PrimarySet);
        Assert.Same(diff, points[2].PrimarySet);
    }

    [Fact]
    public void ComputeRestorePoints_DiffBeforeAnyFull_ProducesNoRestorePoint()
    {
        var diff = Set(BackupType.Differential, T(1));
        var full = Set(BackupType.Full, T(5));

        var points = _builder.ComputeRestorePoints([diff, full]);

        var point = Assert.Single(points);
        Assert.Equal(BackupType.Full, point.Type);
        Assert.Same(full, point.PrimarySet);
    }

    // ---- ComputeRestorePoints: log chains --------------------------------

    [Fact]
    public void ComputeRestorePoints_LogChains_BoundedByNextFull()
    {
        var full1 = Set(BackupType.Full, T(1));
        var log1 = Set(BackupType.TransactionLog, T(2));
        var log2 = Set(BackupType.TransactionLog, T(3));
        var full2 = Set(BackupType.Full, T(5));
        var log3 = Set(BackupType.TransactionLog, T(6));

        var points = _builder.ComputeRestorePoints([full1, log1, log2, full2, log3]);

        Assert.Equal(5, points.Count);

        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();
        Assert.Equal(3, logPoints.Count);

        // Logs at 2 and 3 chain from full1; the log at 6 chains from full2.
        Assert.Same(full1, logPoints[0].RequiredFullSet);
        Assert.Same(full1, logPoints[1].RequiredFullSet);
        Assert.Same(full2, logPoints[2].RequiredFullSet);
        Assert.Equal([log3], logPoints[2].RequiredLogSets);
    }

    [Fact]
    public void ComputeRestorePoints_LogPoints_AccumulateRequiredLogSetsIncrementally()
    {
        var full = Set(BackupType.Full, T(1));
        var log1 = Set(BackupType.TransactionLog, T(2));
        var log2 = Set(BackupType.TransactionLog, T(3));
        var log3 = Set(BackupType.TransactionLog, T(4));

        var points = _builder.ComputeRestorePoints([full, log1, log2, log3]);

        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();
        Assert.Equal(3, logPoints.Count);

        Assert.Equal([log1], logPoints[0].RequiredLogSets);
        Assert.Equal([log1, log2], logPoints[1].RequiredLogSets);
        Assert.Equal([log1, log2, log3], logPoints[2].RequiredLogSets);

        Assert.All(logPoints, p => Assert.Empty(p.RequiredDiffSets));
        Assert.All(logPoints, p => Assert.Same(full, p.RequiredFullSet));
        Assert.Same(log2, logPoints[1].PrimarySet);
    }

    [Fact]
    public void ComputeRestorePoints_LogPoints_EachHasIndependentLogSetSnapshot()
    {
        var full = Set(BackupType.Full, T(1));
        var log1 = Set(BackupType.TransactionLog, T(2));
        var log2 = Set(BackupType.TransactionLog, T(3));

        var points = _builder.ComputeRestorePoints([full, log1, log2]);

        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();

        // The first point's list must not have grown when the second was built.
        Assert.Single(logPoints[0].RequiredLogSets);
        Assert.Equal(2, logPoints[1].RequiredLogSets.Count);
        Assert.NotSame(logPoints[0].RequiredLogSets, logPoints[1].RequiredLogSets);
    }

    [Fact]
    public void ComputeRestorePoints_DiffWithinLogRange_PicksTheDifferentialPerLog()
    {
        // Rewritten for #24. This previously asserted that logs before the latest differential
        // were DROPPED - the defect itself, faithfully characterized. Every log now yields a
        // restore point, each using the shortest valid chain that reaches it.
        var full = Set(BackupType.Full, T(1));
        var logBeforeDiffs = Set(BackupType.TransactionLog, T(2));
        var diff1 = Set(BackupType.Differential, T(3));
        var logBetweenDiffs = Set(BackupType.TransactionLog, T(4));
        var diff2 = Set(BackupType.Differential, T(5));
        var logAfter1 = Set(BackupType.TransactionLog, T(6));
        var logAfter2 = Set(BackupType.TransactionLog, T(7));

        var points = _builder.ComputeRestorePoints(
            [full, logBeforeDiffs, diff1, logBetweenDiffs, diff2, logAfter1, logAfter2]);

        // Every backup is now a reachable point in time.
        Assert.Equal(
            new[] { T(1), T(2), T(3), T(4), T(5), T(6), T(7) },
            points.Select(p => p.Timestamp).ToArray());

        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();
        Assert.Equal(4, logPoints.Count);

        // Before any differential: full + logs, no differential at all.
        Assert.Empty(logPoints[0].RequiredDiffSets);
        Assert.Equal([logBeforeDiffs], logPoints[0].RequiredLogSets);

        // Between the two differentials: the EARLIER one is correct here, not the latest.
        Assert.Equal([diff1], logPoints[1].RequiredDiffSets);
        Assert.Equal([logBetweenDiffs], logPoints[1].RequiredLogSets);

        // After the latest differential: unchanged from before the fix.
        Assert.Equal([diff2], logPoints[2].RequiredDiffSets);
        Assert.Equal([logAfter1], logPoints[2].RequiredLogSets);
        Assert.Equal([diff2], logPoints[3].RequiredDiffSets);
        Assert.Equal([logAfter1, logAfter2], logPoints[3].RequiredLogSets);
    }

    [Fact]
    public void ComputeRestorePoints_LogsBeforeTheFirstDifferential_UseFullPlusLogsOnly()
    {
        // The chain the app could never generate: full + logs, skipping the differential
        // entirely because it had not been taken yet.
        var full = Set(BackupType.Full, T(1));
        var log1 = Set(BackupType.TransactionLog, T(2));
        var log2 = Set(BackupType.TransactionLog, T(3));
        var diff = Set(BackupType.Differential, T(4));

        var points = _builder.ComputeRestorePoints([full, log1, log2, diff]);
        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();

        Assert.Equal(2, logPoints.Count);
        Assert.All(logPoints, p => Assert.Empty(p.RequiredDiffSets));
        Assert.All(logPoints, p => Assert.Same(full, p.RequiredFullSet));
        Assert.Equal([log1], logPoints[0].RequiredLogSets);
        Assert.Equal([log1, log2], logPoints[1].RequiredLogSets);
    }

    [Fact]
    public void ComputeRestorePoints_LogAtExactDiffTimestamp_IsIncludedInChain()
    {
        var full = Set(BackupType.Full, T(1));
        var diff = Set(BackupType.Differential, T(3));
        var logAtDiff = Set(BackupType.TransactionLog, T(3));
        var logAfter = Set(BackupType.TransactionLog, T(4));

        var points = _builder.ComputeRestorePoints([full, diff, logAtDiff, logAfter]);

        var logPoints = points.Where(p => p.Type == BackupType.TransactionLog).ToList();
        Assert.Equal(2, logPoints.Count);
        Assert.Equal([logAtDiff], logPoints[0].RequiredLogSets);
        Assert.Equal([logAtDiff, logAfter], logPoints[1].RequiredLogSets);
        Assert.Equal([diff], logPoints[0].RequiredDiffSets);
    }

    [Fact]
    public void ComputeRestorePoints_LogBeforeTheOnlyDiff_IsStillOffered()
    {
        // Rewritten for #24: this asserted the log produced NO restore point. It now gets one,
        // restored as full + log with the differential skipped.
        var full = Set(BackupType.Full, T(1));
        var log = Set(BackupType.TransactionLog, T(2));
        var diff = Set(BackupType.Differential, T(3));

        var points = _builder.ComputeRestorePoints([full, log, diff]);

        Assert.Equal(3, points.Count);
        Assert.Equal(new[] { T(1), T(2), T(3) }, points.Select(p => p.Timestamp).ToArray());

        var logPoint = Assert.Single(points, p => p.Type == BackupType.TransactionLog);
        Assert.Same(full, logPoint.RequiredFullSet);
        Assert.Empty(logPoint.RequiredDiffSets);
        Assert.Equal([log], logPoint.RequiredLogSets);
    }

    [Fact]
    public void ComputeRestorePoints_FullWithNoLogsInItsRange_ProducesNoLogPoints()
    {
        var full1 = Set(BackupType.Full, T(1));
        var full2 = Set(BackupType.Full, T(5));
        var log = Set(BackupType.TransactionLog, T(6));

        var points = _builder.ComputeRestorePoints([full1, full2, log]);

        Assert.Equal(3, points.Count);
        var logPoint = Assert.Single(points, p => p.Type == BackupType.TransactionLog);
        Assert.Same(full2, logPoint.RequiredFullSet);
    }

    [Fact]
    public void ComputeRestorePoints_ScrambledInput_ReturnsPointsSortedByTimestamp()
    {
        var full1 = Set(BackupType.Full, T(1));
        var log1 = Set(BackupType.TransactionLog, T(2));
        var diff1 = Set(BackupType.Differential, T(3));
        var log2 = Set(BackupType.TransactionLog, T(4));
        var full2 = Set(BackupType.Full, T(5));
        var log3 = Set(BackupType.TransactionLog, T(6));

        var points = _builder.ComputeRestorePoints([log3, diff1, full2, log1, full1, log2]);

        var timestamps = points.Select(p => p.Timestamp).ToList();
        Assert.Equal(timestamps.OrderBy(t => t).ToList(), timestamps);

        // Every backup is a point: full1, log@2 (full+log, pre-differential), diff@3,
        // log@4 (full+diff+log), full2, log@6. log@2 was previously missing - see #24.
        Assert.Equal(new[] { T(1), T(2), T(3), T(4), T(5), T(6) }, timestamps.ToArray());
    }

    // ---- GetRestoreWindow (BackupSet overload) ---------------------------

    [Fact]
    public void GetRestoreWindow_Sets_EmptyList_ReturnsNull()
    {
        Assert.Null(_builder.GetRestoreWindow(new List<BackupSet>()));
    }

    [Fact]
    public void GetRestoreWindow_Sets_NoFulls_ReturnsNull()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Differential, T(1)),
            Set(BackupType.TransactionLog, T(2))
        };

        Assert.Null(_builder.GetRestoreWindow(sets));
    }

    [Fact]
    public void GetRestoreWindow_Sets_ReturnsEarliestFullToLatestOverallSet()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Differential, T(0)), // before any full: ignored for earliest
            Set(BackupType.Full, T(1)),
            Set(BackupType.Full, T(5)),
            Set(BackupType.TransactionLog, T(7))
        };

        var window = _builder.GetRestoreWindow(sets);

        Assert.NotNull(window);
        Assert.Equal(T(1), window.Value.earliest);
        Assert.Equal(T(7), window.Value.latest);
    }

    [Fact]
    public void GetRestoreWindow_Sets_SingleFull_WindowStartsAndEndsAtThatFull()
    {
        var window = _builder.GetRestoreWindow(new List<BackupSet> { Set(BackupType.Full, T(3)) });

        Assert.NotNull(window);
        Assert.Equal(T(3), window.Value.earliest);
        Assert.Equal(T(3), window.Value.latest);
    }

    // The file-level GetRestoreWindow overload and its two tests are gone (#42). It had no
    // production caller and compared BackupStartDate against LastModified - two different clocks
    // (#47) - so its tests were pinning behaviour nothing depended on and nobody should copy.

    // ---- BuildChainFromRestorePoint --------------------------------------

    [Fact]
    public void BuildChainFromRestorePoint_MapsSetsByReferenceAndLeavesStopAtNull()
    {
        var full = Set(BackupType.Full, T(1));
        var diff = Set(BackupType.Differential, T(2));
        var log = Set(BackupType.TransactionLog, T(3));
        var point = new RestorePoint
        {
            Timestamp = T(3),
            Type = BackupType.TransactionLog,
            PrimarySet = log,
            RequiredFullSet = full,
            RequiredDiffSets = [diff],
            RequiredLogSets = [log]
        };

        var chain = _builder.BuildChainFromRestorePoint(point);

        Assert.Same(full, chain.FullSet);
        Assert.Same(point.RequiredDiffSets, chain.DiffSets);
        Assert.Same(point.RequiredLogSets, chain.LogSets);
        Assert.Null(chain.StopAt);
    }

    // ---- RestorePoint model ----------------------------------------------

    [Fact]
    public void RestorePoint_TotalFiles_SumsFileCountsAcrossWholeChain()
    {
        var full = Set(BackupType.Full, T(1), fileCount: 2);
        var diff = Set(BackupType.Differential, T(2), fileCount: 1);
        var log1 = Set(BackupType.TransactionLog, T(3), fileCount: 1);
        var log2 = Set(BackupType.TransactionLog, T(4), fileCount: 3);

        var point = new RestorePoint
        {
            PrimarySet = log2,
            RequiredFullSet = full,
            RequiredDiffSets = [diff],
            RequiredLogSets = [log1, log2]
        };

        Assert.Equal(7, point.TotalFiles);
    }

    [Fact]
    public void RestorePoint_TotalSizeBytes_SumsSizesAcrossWholeChain()
    {
        var full = Set(BackupType.Full, T(1), fileCount: 2, fileSize: 100);
        var diff = Set(BackupType.Differential, T(2), fileCount: 1, fileSize: 50);
        var log = Set(BackupType.TransactionLog, T(3), fileCount: 2, fileSize: 10);

        var point = new RestorePoint
        {
            PrimarySet = log,
            RequiredFullSet = full,
            RequiredDiffSets = [diff],
            RequiredLogSets = [log]
        };

        Assert.Equal(270, point.TotalSizeBytes);
    }

    [Fact]
    public void RestorePoint_ChainDescription_FullOnly_ListsFullFilesAndSize()
    {
        var full = Set(BackupType.Full, T(1), fileCount: 1, fileSize: 512);
        var point = new RestorePoint { PrimarySet = full, RequiredFullSet = full };

        // Size format goes through "{value:F1}" so build the expectation the same way.
        Assert.Equal($"1 Full | 1 files | {512d:F1} B", point.ChainDescription);
    }

    [Fact]
    public void RestorePoint_ChainDescription_WithDiffsAndLogs_ListsEachPart()
    {
        var full = Set(BackupType.Full, T(1), fileCount: 2, fileSize: 1024);
        var diff = Set(BackupType.Differential, T(2), fileCount: 1, fileSize: 1024);
        var log1 = Set(BackupType.TransactionLog, T(3), fileCount: 1, fileSize: 1024);
        var log2 = Set(BackupType.TransactionLog, T(4), fileCount: 1, fileSize: 1024);

        var point = new RestorePoint
        {
            PrimarySet = log2,
            RequiredFullSet = full,
            RequiredDiffSets = [diff],
            RequiredLogSets = [log1, log2]
        };

        // 5 files, 5120 bytes total = 5.0 KB.
        Assert.Equal($"1 Full + 1 Diff(s) + 2 Log(s) | 5 files | {5d:F1} KB", point.ChainDescription);
    }

    // ---- BackupChain model -----------------------------------------------

    [Fact]
    public void BackupChain_Summary_PlainFullOnly()
    {
        var chain = new BackupChain { FullSet = Set(BackupType.Full, T(1)) };

        Assert.Equal("1 Full", chain.Summary);
    }

    [Fact]
    public void BackupChain_Summary_StripedFullWithDiffsAndLogs()
    {
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T(1), fileCount: 3),
            DiffSets = [Set(BackupType.Differential, T(2)), Set(BackupType.Differential, T(3))],
            LogSets = [Set(BackupType.TransactionLog, T(4))]
        };

        Assert.Equal("1 Full (3 files) + 2 Diff(s) + 1 Log(s)", chain.Summary);
    }

    [Fact]
    public void BackupChain_AllSets_ReturnsFullThenDiffsThenLogs()
    {
        var full = Set(BackupType.Full, T(1));
        var diff = Set(BackupType.Differential, T(2));
        var log1 = Set(BackupType.TransactionLog, T(3));
        var log2 = Set(BackupType.TransactionLog, T(4));

        var chain = new BackupChain
        {
            FullSet = full,
            DiffSets = [diff],
            LogSets = [log1, log2]
        };

        Assert.Equal([full, diff, log1, log2], chain.AllSets);
    }

    [Fact]
    public void BackupChain_FromRestorePoint_ReusesSetListsAndClearsStopAt()
    {
        var full = Set(BackupType.Full, T(1));
        var point = new RestorePoint
        {
            PrimarySet = full,
            RequiredFullSet = full,
            RequiredDiffSets = [Set(BackupType.Differential, T(2))],
            RequiredLogSets = [Set(BackupType.TransactionLog, T(3))]
        };

        var chain = BackupChain.FromRestorePoint(point);

        Assert.Same(point.RequiredFullSet, chain.FullSet);
        Assert.Same(point.RequiredDiffSets, chain.DiffSets);
        Assert.Same(point.RequiredLogSets, chain.LogSets);
        Assert.Null(chain.StopAt);
    }

    // ---- StopAtWindow (point-in-time bounds) -----------------------------

    [Fact]
    public void StopAtWindow_NoLogs_IsNull()
    {
        var chain = new BackupChain { FullSet = Set(BackupType.Full, T(1)) };
        Assert.Null(chain.StopAtWindow);
    }

    [Fact]
    public void StopAtWindow_SingleLogAfterFull_SpansFullToLog()
    {
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T(1)),
            LogSets = [Set(BackupType.TransactionLog, T(2))]
        };

        var window = chain.StopAtWindow;

        Assert.NotNull(window);
        Assert.Equal(T(1), window!.Value.Earliest);
        Assert.Equal(T(2), window.Value.Latest);
    }

    [Fact]
    public void StopAtWindow_SingleLogAfterDiff_StartsAtTheDiff()
    {
        // The database is restored up to the differential before the log is applied, so the
        // differential - not the full - is the lower bound.
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T(1)),
            DiffSets = [Set(BackupType.Differential, T(4))],
            LogSets = [Set(BackupType.TransactionLog, T(5))]
        };

        Assert.Equal(T(4), chain.StopAtWindow!.Value.Earliest);
        Assert.Equal(T(5), chain.StopAtWindow.Value.Latest);
    }

    [Fact]
    public void StopAtWindow_MultipleLogs_StartsAtThePenultimateLog()
    {
        // The target must land inside the LAST log; every earlier log applies in full.
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T(1)),
            LogSets =
            [
                Set(BackupType.TransactionLog, T(2)),
                Set(BackupType.TransactionLog, T(3)),
                Set(BackupType.TransactionLog, T(4))
            ]
        };

        Assert.Equal(T(3), chain.StopAtWindow!.Value.Earliest);
        Assert.Equal(T(4), chain.StopAtWindow.Value.Latest);
    }

    [Fact]
    public void StopAtWindow_PrefersPenultimateLogOverAnEarlierDiff()
    {
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T(1)),
            DiffSets = [Set(BackupType.Differential, T(4))],
            LogSets =
            [
                Set(BackupType.TransactionLog, T(5)),
                Set(BackupType.TransactionLog, T(6))
            ]
        };

        Assert.Equal(T(5), chain.StopAtWindow!.Value.Earliest);
    }

    [Fact]
    public void StopAtWindow_UpperBoundMatchesTheSelectedRestorePoint()
    {
        // The bound the UI offers must equal the point the user clicked on the timeline.
        var points = _builder.ComputeRestorePoints(
        [
            Set(BackupType.Full, T(1)),
            Set(BackupType.TransactionLog, T(2)),
            Set(BackupType.TransactionLog, T(3))
        ]);

        var selected = points.Last(p => p.Type == BackupType.TransactionLog);
        var chain = _builder.BuildChainFromRestorePoint(selected);

        Assert.Equal(selected.Timestamp, chain.StopAtWindow!.Value.Latest);
    }
}
