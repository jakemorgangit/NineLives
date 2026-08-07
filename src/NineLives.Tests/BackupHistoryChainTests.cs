using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Chains built from LSNs rather than timestamps (#149, and #130's question for this source).
///
/// The blob path infers type and database from filenames and assembles by time, because that is all
/// a container listing offers - and #130 exists because that inference has been wrong in several
/// ways this repo has since fixed. None of it applies here: the source instance recorded which full
/// each differential belongs to, exactly.
/// </summary>
public class BackupHistoryChainTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupHistoryEntry Full(DateTime at, decimal checkpoint, decimal last, bool copyOnly = false) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.Full,
        StartedAt = at,
        FinishedAt = at.AddMinutes(5),
        CheckpointLsn = checkpoint,
        LastLsn = last,
        IsCopyOnly = copyOnly,
        Files = [$@"\\nas01\sql\MyDb_full_{at:HHmmss}.bak"]
    };

    private static BackupHistoryEntry Diff(DateTime at, decimal databaseBackupLsn, decimal last) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.Differential,
        StartedAt = at,
        FinishedAt = at.AddMinutes(2),
        DatabaseBackupLsn = databaseBackupLsn,
        LastLsn = last,
        Files = [$@"\\nas01\sql\MyDb_diff_{at:HHmmss}.bak"]
    };

    private static BackupHistoryEntry Log(DateTime at, decimal last) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        StartedAt = at,
        FinishedAt = at.AddSeconds(30),
        LastLsn = last,
        Files = [$@"\\nas01\sql\MyDb_log_{at:HHmmss}.trn"]
    };

    private static BackupHistoryChainBuilder Builder() => new();

    // ── the relationship that matters ───────────────────────────────────────────

    /// <summary>
    /// A differential belongs to the full whose CheckpointLSN its DatabaseBackupLSN names - not to
    /// whichever full happens to sit nearest it in time.
    /// </summary>
    [Fact]
    public void ADifferentialGoesWithTheFullItsLsnNames()
    {
        var older = Full(T0, checkpoint: 100, last: 110);
        var newer = Full(T0.AddHours(6), checkpoint: 200, last: 210);

        // Taken AFTER the newer full, but based on the older one - which happens when a differential
        // job overlaps a full, and is precisely where sorting by time gets it wrong.
        var diff = Diff(T0.AddHours(7), databaseBackupLsn: 100, last: 150);

        var chains = Builder().Build([older, newer, diff]);

        Assert.Same(diff, chains.Single(c => c.Full == older).Differential);
        Assert.Null(chains.Single(c => c.Full == newer).Differential);
    }

    /// <summary>
    /// A copy-only full does not reset the differential base, so a differential taken afterwards
    /// still belongs to the previous ordinary full. Pairing them produces error 3136 at restore
    /// time (#49).
    /// </summary>
    [Fact]
    public void ACopyOnlyFullNeverTakesADifferential()
    {
        var ordinary = Full(T0, checkpoint: 100, last: 110);
        var copyOnly = Full(T0.AddHours(1), checkpoint: 150, last: 160, copyOnly: true);
        var diff = Diff(T0.AddHours(2), databaseBackupLsn: 100, last: 170);

        var chains = Builder().Build([ordinary, copyOnly, diff]);

        Assert.Null(chains.Single(c => c.Full == copyOnly).Differential);
        Assert.Same(diff, chains.Single(c => c.Full == ordinary).Differential);
    }

    /// <summary>
    /// A differential whose base is not in the history cannot be restored, so it is not offered
    /// with some other full that happens to be nearby.
    /// </summary>
    [Fact]
    public void ADifferentialWithNoBaseIsNotOffered()
    {
        var full = Full(T0, checkpoint: 100, last: 110);
        var orphan = Diff(T0.AddHours(1), databaseBackupLsn: 999, last: 120);

        var chain = Assert.Single(Builder().Build([full, orphan]));

        Assert.Null(chain.Differential);
    }

    /// <summary>
    /// A log whose LastLSN is behind where the restore has already reached contains nothing it
    /// still needs - even though it may have FINISHED after the full did, which happens whenever a
    /// log job overlaps a full on a busy instance.
    /// </summary>
    [Fact]
    public void ALogThatAddsNothingIsLeftOut()
    {
        var full = Full(T0, checkpoint: 100, last: 500);
        var overlapping = Log(T0.AddMinutes(1), last: 400);
        var useful = Log(T0.AddMinutes(30), last: 600);

        var chain = Assert.Single(Builder().Build([full, overlapping, useful]));

        Assert.Same(useful, Assert.Single(chain.Logs));
    }

    [Fact]
    public void LogsRollForwardFromTheDifferentialWhenThereIsOne()
    {
        var full = Full(T0, checkpoint: 100, last: 200);
        var diff = Diff(T0.AddHours(1), databaseBackupLsn: 100, last: 400);
        var supersededByDiff = Log(T0.AddMinutes(30), last: 300);
        var afterDiff = Log(T0.AddHours(2), last: 500);

        var chain = Assert.Single(Builder().Build([full, diff, supersededByDiff, afterDiff]));

        Assert.Same(afterDiff, Assert.Single(chain.Logs));
    }

    // ── what a restore can actually use ─────────────────────────────────────────

    /// <summary>
    /// msdb keeps history for backups whose files were deleted, archived or pruned by a retention
    /// job. A record with no files is not a restorable backup.
    /// </summary>
    [Fact]
    public void HistoryWithNoFilesIsNotOfferedAsAChain()
    {
        var pruned = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(5),
            CheckpointLsn = 100,
            LastLsn = 110,
            Files = []
        };

        Assert.Empty(Builder().Build([pruned]));
    }

    [Fact]
    public void EveryStripeIsIncludedInOrder()
    {
        var striped = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(5),
            CheckpointLsn = 100,
            LastLsn = 110,
            Files = [@"\\nas01\sql\MyDb_1.bak", @"\\nas01\sql\MyDb_2.bak", @"\\nas01\sql\MyDb_3.bak"]
        };

        var chain = Assert.Single(Builder().Build([striped]));

        Assert.Equal(3, chain.Files.Count());
        Assert.Equal(@"\\nas01\sql\MyDb_1.bak", chain.Files.First());
        Assert.Contains("3 files", chain.Summary);
    }

    // ── point in time ───────────────────────────────────────────────────────────

    /// <summary>
    /// Only the logs needed to reach the moment asked for, plus the one that spans it - that last
    /// one is where STOPAT lands. Rolling every log forward would take the database PAST the point
    /// somebody chose, which on a recovery from a bad DELETE is the thing they were avoiding.
    /// </summary>
    [Fact]
    public void RestoringToAMomentStopsAtTheLogThatSpansIt()
    {
        var full = Full(T0, checkpoint: 100, last: 200);
        var logs = new[]
        {
            Log(T0.AddHours(1), last: 300),
            Log(T0.AddHours(2), last: 400),
            Log(T0.AddHours(3), last: 500),
            Log(T0.AddHours(4), last: 600)
        };

        var target = T0.AddHours(2).AddMinutes(10);
        var chain = Builder().BuildTo([full, .. logs], target);

        Assert.NotNull(chain);
        Assert.Equal(3, chain!.Logs.Count);
        Assert.Same(logs[2], chain.Logs[^1]);
    }

    [Fact]
    public void NothingIsOfferedForAMomentBeforeAnyBackupExists()
    {
        var full = Full(T0, checkpoint: 100, last: 200);

        Assert.Null(Builder().BuildTo([full], T0.AddDays(-1)));
    }
}
