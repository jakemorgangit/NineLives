using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Choosing which logs a chain needs by LSN, where the sets carry them (#130).
///
/// A timestamp says WHEN a backup was taken; an LSN says what is IN it. They answer the same
/// question with different accuracy, and they differ exactly when a log backup overlaps the backup
/// it follows — which on a busy instance is routine, not exotic.
///
/// Both cases are exercised, because sets from a container listing have no LSNs and must go on
/// behaving precisely as they did.
/// </summary>
public class LogChainFromLsnsTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupSet Full(DateTime at, decimal? checkpoint = null, decimal? lastLsn = null) => new()
    {
        SetId = $"full_{at:HHmmss}",
        DatabaseName = "MyDb",
        Type = BackupType.Full,
        Timestamp = at,
        CheckpointLsn = checkpoint,
        LastLsn = lastLsn,
        Files = [new BackupFileInfo { LocalPath = $@"\\nas01\sql\full_{at:HHmmss}.bak" }]
    };

    private static BackupSet Log(DateTime at, decimal? lastLsn = null) => new()
    {
        SetId = $"log_{at:HHmmss}",
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        Timestamp = at,
        LastLsn = lastLsn,
        Files = [new BackupFileInfo { LocalPath = $@"\\nas01\sql\log_{at:HHmmss}.trn" }]
    };

    private static BackupSet Diff(DateTime at, decimal? basedOn = null, decimal? lastLsn = null) => new()
    {
        SetId = $"diff_{at:HHmmss}",
        DatabaseName = "MyDb",
        Type = BackupType.Differential,
        Timestamp = at,
        DatabaseBackupLsn = basedOn,
        LastLsn = lastLsn,
        Files = [new BackupFileInfo { LocalPath = $@"\\nas01\sql\diff_{at:HHmmss}.bak" }]
    };

    private static List<RestorePoint> Points(params BackupSet[] sets)
        => new BackupChainBuilder().ComputeRestorePoints(sets.ToList());

    // ── the case timestamps get wrong ───────────────────────────────────────────

    /// <summary>
    /// The one the LSN rule exists for.
    ///
    /// A log backup that STARTED before the full but finished after it. By time it looks like it
    /// belongs before the full and gets dropped; by LSN it holds transactions past the end of the
    /// full and the chain needs it. Dropping it breaks point-in-time recovery at exactly that spot.
    /// </summary>
    [Fact]
    public void ALogThatOverlapsTheFullIsStillOfferedWhenItsLsnSaysItIsNeeded()
    {
        // Timestamped a minute BEFORE the full, but ends past it.
        var overlapping = Log(T0.AddMinutes(-1), lastLsn: 250);
        var full = Full(T0, checkpoint: 100, lastLsn: 200);

        var points = Points(full, overlapping);

        var log = points.Single(p => p.Type == BackupType.TransactionLog);
        Assert.Same(overlapping, log.PrimarySet);
        Assert.Same(full, log.RequiredFullSet);
    }

    /// <summary>
    /// And the mirror image: a log timestamped after the full whose LSNs show it contains nothing
    /// past it. Restoring it would be rejected as already applied.
    /// </summary>
    [Fact]
    public void ALogThatAddsNothingPastTheFullIsNotOffered()
    {
        var full = Full(T0, checkpoint: 100, lastLsn: 500);
        var spent = Log(T0.AddMinutes(5), lastLsn: 400);

        Assert.DoesNotContain(Points(full, spent), p => p.Type == BackupType.TransactionLog);
    }

    /// <summary>
    /// Once a differential is in the chain, the logs still needed are the ones past the
    /// DIFFERENTIAL - not past the full. Including the earlier ones adds restores SQL Server
    /// rejects.
    /// </summary>
    [Fact]
    public void OnlyTheLogsPastTheDifferentialJoinAChainThatUsesIt()
    {
        var full = Full(T0, checkpoint: 100, lastLsn: 200);
        var beforeDiff = Log(T0.AddHours(1), lastLsn: 300);
        var diff = Diff(T0.AddHours(2), basedOn: 100, lastLsn: 400);
        var afterDiff = Log(T0.AddHours(3), lastLsn: 500);

        var points = Points(full, beforeDiff, diff, afterDiff);
        var last = points.Last(p => p.Type == BackupType.TransactionLog);

        Assert.Same(afterDiff, last.PrimarySet);
        Assert.Same(diff, Assert.Single(last.RequiredDiffSets));
        Assert.Same(afterDiff, Assert.Single(last.RequiredLogSets));
    }

    /// <summary>
    /// And before the differential exists, the chain is full + logs - which SQL Server restores
    /// happily, and which the per-log choice was introduced to stop hiding.
    /// </summary>
    [Fact]
    public void ALogBeforeTheDifferentialStillGetsAChainOfFullPlusLogs()
    {
        var full = Full(T0, checkpoint: 100, lastLsn: 200);
        var beforeDiff = Log(T0.AddHours(1), lastLsn: 300);
        var diff = Diff(T0.AddHours(2), basedOn: 100, lastLsn: 400);

        var points = Points(full, beforeDiff, diff);
        var log = points.Single(p => p.Type == BackupType.TransactionLog);

        Assert.Same(beforeDiff, log.PrimarySet);
        Assert.Empty(log.RequiredDiffSets);
        Assert.Same(beforeDiff, Assert.Single(log.RequiredLogSets));
    }

    [Fact]
    public void EveryLogNeededToReachAPointIsInItsChain()
    {
        var full = Full(T0, checkpoint: 100, lastLsn: 200);
        var one = Log(T0.AddHours(1), lastLsn: 300);
        var two = Log(T0.AddHours(2), lastLsn: 400);
        var three = Log(T0.AddHours(3), lastLsn: 500);

        var points = Points(full, one, two, three);
        var last = points.Last(p => p.Type == BackupType.TransactionLog);

        Assert.Equal([one, two, three], last.RequiredLogSets);
    }

    // ── sets from a container behave exactly as before ──────────────────────────

    /// <summary>
    /// A blob listing gives a type and a time and nothing else, so the timestamp rule is all there
    /// is - and it has to stay precisely as it was, including its known-awkward edges.
    /// </summary>
    [Fact]
    public void WithoutLsnsLogsAreStillChosenByTime()
    {
        var full = Full(T0);
        var one = Log(T0.AddHours(1));
        var two = Log(T0.AddHours(2));

        var points = Points(full, one, two);
        var last = points.Last(p => p.Type == BackupType.TransactionLog);

        Assert.Equal([one, two], last.RequiredLogSets);
    }

    /// <summary>
    /// The collision #46 is about: a log sharing a full's exact timestamp. Nothing says which came
    /// first, so it cannot be trusted to follow the full - and treating it as though it did
    /// produced restore points that could not be restored.
    ///
    /// This is why the two callers of the rule differ on what a tie means, and it only arises
    /// without LSNs: two backups cannot both end at the same LSN and differ in what they hold.
    /// </summary>
    [Fact]
    public void WithoutLsnsALogAtTheFullsExactTimestampIsStillNotTrustedToFollowIt()
    {
        var full = Full(T0);
        var colliding = Log(T0);

        Assert.DoesNotContain(Points(full, colliding), p => p.Type == BackupType.TransactionLog);
    }

    /// <summary>A mixed set falls back rather than refusing - there is nothing to be definitive with.</summary>
    [Fact]
    public void ALogWithAnLsnAgainstAFullWithoutOneFallsBackToTime()
    {
        var full = Full(T0);
        var log = Log(T0.AddHours(1), lastLsn: 300);

        Assert.Single(Points(full, log), p => p.Type == BackupType.TransactionLog);
    }
}
