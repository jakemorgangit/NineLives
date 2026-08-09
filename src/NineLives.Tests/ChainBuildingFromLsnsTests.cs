using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Pairing a differential with its base full by LSN, where the sets carry LSNs (#130, #149).
///
/// A container listing offers a type and a time and nothing else, so the blob path has to reason
/// about proximity - the rule that produced #49's copy-only bug. An instance's own msdb hands over
/// the answer: a differential's DatabaseBackupLsn IS the CheckpointLsn of the full it was taken
/// against, and that is the same comparison SQL Server makes before it raises 3136.
///
/// So the builder asks the data where the data exists, and keeps the timestamp rule where it does
/// not. Both are exercised here, because a container's sets must go on behaving exactly as before.
/// </summary>
public class ChainBuildingFromLsnsTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupSet Full(DateTime at, decimal? checkpoint = null, bool copyOnly = false) => new()
    {
        SetId = $"full_{at:HHmmss}",
        DatabaseName = "MyDb",
        Type = BackupType.Full,
        Timestamp = at,
        IsCopyOnly = copyOnly,
        CheckpointLsn = checkpoint,
        Files = [new BackupFileInfo { LocalPath = $@"\\nas01\sql\full_{at:HHmmss}.bak" }]
    };

    private static BackupSet Diff(DateTime at, decimal? basedOn = null) => new()
    {
        SetId = $"diff_{at:HHmmss}",
        DatabaseName = "MyDb",
        Type = BackupType.Differential,
        Timestamp = at,
        DatabaseBackupLsn = basedOn,
        Files = [new BackupFileInfo { LocalPath = $@"\\nas01\sql\diff_{at:HHmmss}.bak" }]
    };

    private static RestorePoint? DiffPoint(params BackupSet[] sets)
        => new BackupChainBuilder()
            .ComputeRestorePoints(sets.ToList())
            .FirstOrDefault(p => p.Type == BackupType.Differential);

    // ── where the LSNs are there ────────────────────────────────────────────────

    /// <summary>
    /// The case the timestamp rule gets wrong and this one does not need a special case for.
    ///
    /// Two fulls, and a differential taken against the EARLIER one - which happens whenever a
    /// second full is taken with COPY_ONLY, and also whenever an ad-hoc full runs between a
    /// scheduled full and its differentials. By time the answer is the later full; by LSN it is the
    /// one the backup actually says.
    /// </summary>
    [Fact]
    public void ADifferentialIsPairedWithTheFullItWasActuallyTakenAgainst()
    {
        var first = Full(T0, checkpoint: 100);
        var second = Full(T0.AddHours(1), checkpoint: 500);
        var diff = Diff(T0.AddHours(2), basedOn: 100);

        var point = DiffPoint(first, second, diff);

        Assert.NotNull(point);
        Assert.Same(first, point.RequiredFullSet);
    }

    [Fact]
    public void ADifferentialTakenAgainstTheLaterFullIsPairedWithThatOne()
    {
        var first = Full(T0, checkpoint: 100);
        var second = Full(T0.AddHours(1), checkpoint: 500);
        var diff = Diff(T0.AddHours(2), basedOn: 500);

        var point = DiffPoint(first, second, diff);

        Assert.NotNull(point);
        Assert.Same(second, point.RequiredFullSet);
    }

    /// <summary>
    /// A differential whose base is not among the fulls in hand is genuinely unrestorable, and
    /// saying nothing is the correct answer.
    ///
    /// Falling back to the nearest by time would hand back a full SQL Server will reject with 3136 -
    /// worse than offering nothing, because the restore fails after WITH REPLACE has already
    /// dropped the target.
    /// </summary>
    [Fact]
    public void ADifferentialWhoseBaseIsMissingIsNotOfferedAtAll()
    {
        var full = Full(T0, checkpoint: 100);
        var orphan = Diff(T0.AddHours(2), basedOn: 999);

        Assert.Null(DiffPoint(full, orphan));
    }

    /// <summary>
    /// A copy-only full does not reset the differential base, so no differential ever carries its
    /// checkpoint. The rule that needed stating explicitly for the blob path falls out of the data
    /// here rather than being handled.
    /// </summary>
    [Fact]
    public void ACopyOnlyFullIsNeverADifferentialsBase()
    {
        var regular = Full(T0, checkpoint: 100);
        var copyOnly = Full(T0.AddHours(1), checkpoint: 500, copyOnly: true);
        var diff = Diff(T0.AddHours(2), basedOn: 100);

        var point = DiffPoint(regular, copyOnly, diff);

        Assert.NotNull(point);
        Assert.Same(regular, point.RequiredFullSet);
    }

    // ── where they are not ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets from a container carry no LSNs, and must go on behaving exactly as they did: the latest
    /// non-copy-only full at or before the differential.
    /// </summary>
    [Fact]
    public void WithoutLsnsTheTimestampRuleStillApplies()
    {
        var first = Full(T0);
        var second = Full(T0.AddHours(1));
        var diff = Diff(T0.AddHours(2));

        var point = DiffPoint(first, second, diff);

        Assert.NotNull(point);
        Assert.Same(second, point.RequiredFullSet);
    }

    [Fact]
    public void WithoutLsnsACopyOnlyFullIsStillSkippedAsABase()
    {
        var regular = Full(T0);
        var copyOnly = Full(T0.AddHours(1), copyOnly: true);
        var diff = Diff(T0.AddHours(2));

        var point = DiffPoint(regular, copyOnly, diff);

        Assert.NotNull(point);
        Assert.Same(regular, point.RequiredFullSet);
    }

    /// <summary>
    /// A mixed set - a differential that knows its base among fulls that do not - falls back rather
    /// than refusing. The fulls cannot answer, so there is nothing to be definitive with, and the
    /// timestamp rule is still better than nothing.
    /// </summary>
    [Fact]
    public void ADifferentialWithLsnsAmongFullsWithoutThemFallsBackToTime()
    {
        var first = Full(T0);
        var second = Full(T0.AddHours(1));
        var diff = Diff(T0.AddHours(2), basedOn: 100);

        var point = DiffPoint(first, second, diff);

        Assert.NotNull(point);
        Assert.Same(second, point.RequiredFullSet);
    }
}
