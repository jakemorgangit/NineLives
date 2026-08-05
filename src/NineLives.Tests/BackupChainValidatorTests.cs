using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class BackupChainValidatorTests
{
    private readonly BackupChainValidator _validator = new();

    private static DateTime T(int hour, int minute = 0) => new(2026, 8, 4, hour, minute, 0);

    private static BackupSet Set(
        BackupType type, DateTime timestamp, int fileCount = 1,
        int[]? stripeNumbers = null, long sizePerFile = 100)
    {
        var files = new List<BackupFileInfo>();
        var stamp = timestamp.ToString("yyyyMMdd_HHmmss");

        if (stripeNumbers != null)
        {
            foreach (var n in stripeNumbers)
                files.Add(new BackupFileInfo
                {
                    BlobName = $"{stamp}_{n}.bak",
                    Type = type,
                    SizeBytes = sizePerFile
                });
        }
        else
        {
            for (int i = 0; i < fileCount; i++)
                files.Add(new BackupFileInfo
                {
                    BlobName = fileCount == 1 ? $"{stamp}.bak" : $"{stamp}_{i + 1}.bak",
                    Type = type,
                    SizeBytes = sizePerFile
                });
        }

        return new BackupSet
        {
            SetId = stamp,
            Type = type,
            Timestamp = timestamp,
            Files = files
        };
    }

    private static BackupChain Chain(BackupSet full, BackupSet[]? diffs = null, BackupSet[]? logs = null) => new()
    {
        FullSet = full,
        DiffSets = diffs?.ToList() ?? [],
        LogSets = logs?.ToList() ?? []
    };

    // ── clean chains produce nothing ─────────────────────────────────────────────

    [Fact]
    public void Validate_NullChain_NoIssues()
        => Assert.Empty(_validator.Validate(null));

    [Fact]
    public void Validate_HealthyChain_NoIssues()
    {
        var chain = Chain(
            Set(BackupType.Full, T(0), fileCount: 4),
            [Set(BackupType.Differential, T(4))],
            [Set(BackupType.TransactionLog, T(5)), Set(BackupType.TransactionLog, T(6)), Set(BackupType.TransactionLog, T(7))]);

        Assert.Empty(_validator.Validate(chain));
    }

    // ── missing stripes ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_MissingStripe_IsAnError()
    {
        // Stripes 1, 2, 4 - number 3 never arrived. SQL Server cannot restore a partial set.
        var chain = Chain(Set(BackupType.Full, T(0), stripeNumbers: [1, 2, 4]));

        var issue = Assert.Single(_validator.Validate(chain));

        Assert.True(issue.IsError);
        Assert.Contains("missing stripe", issue.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", issue.Title);
    }

    [Fact]
    public void Validate_MultipleMissingStripes_AreAllNamed()
    {
        var chain = Chain(Set(BackupType.Full, T(0), stripeNumbers: [1, 5]));

        var issue = Assert.Single(_validator.Validate(chain));

        Assert.Contains("2", issue.Title);
        Assert.Contains("3", issue.Title);
        Assert.Contains("4", issue.Title);
    }

    [Fact]
    public void Validate_CompleteStripeSet_IsClean()
        => Assert.Empty(_validator.Validate(Chain(Set(BackupType.Full, T(0), stripeNumbers: [1, 2, 3, 4]))));

    [Fact]
    public void Validate_SingleFileBackup_IsNotTreatedAsAStripeSet()
        => Assert.Empty(_validator.Validate(Chain(Set(BackupType.Full, T(0)))));

    [Fact]
    public void Validate_MissingStripeInALogSet_IsAlsoCaught()
    {
        var chain = Chain(
            Set(BackupType.Full, T(0)),
            null,
            [Set(BackupType.TransactionLog, T(1), stripeNumbers: [1, 3])]);

        Assert.Single(_validator.Validate(chain), i => i.IsError);
    }

    // ── empty files ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ZeroByteFile_IsAnError()
    {
        var chain = Chain(Set(BackupType.Full, T(0), sizePerFile: 0));

        var issue = Assert.Single(_validator.Validate(chain));

        Assert.True(issue.IsError);
        Assert.Contains("empty file", issue.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ── log cadence ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_HoleInHourlyLogSequence_IsAWarning()
    {
        // Hourly logs with 03:00 and 04:00 absent - a 3 hour hole against a 1 hour cadence.
        var logs = new[]
        {
            Set(BackupType.TransactionLog, T(1)),
            Set(BackupType.TransactionLog, T(2)),
            Set(BackupType.TransactionLog, T(5)),
            Set(BackupType.TransactionLog, T(6)),
            Set(BackupType.TransactionLog, T(7))
        };
        var chain = Chain(Set(BackupType.Full, T(0)), null, logs);

        var issue = Assert.Single(_validator.Validate(chain));

        Assert.False(issue.IsError);           // a schedule change is a legitimate cause
        Assert.Contains("missing log backup", issue.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4305", issue.Detail); // tells the user how it will fail
    }

    [Fact]
    public void Validate_EvenlySpacedLogs_ProduceNoCadenceWarning()
    {
        var logs = Enumerable.Range(1, 8)
            .Select(i => Set(BackupType.TransactionLog, T(i)))
            .ToArray();

        Assert.Empty(_validator.Validate(Chain(Set(BackupType.Full, T(0)), null, logs)));
    }

    [Fact]
    public void Validate_TooFewLogsToEstablishCadence_ProducesNoWarning()
    {
        // Two logs cannot define a "normal" interval, so any gap between them is unremarkable.
        var logs = new[]
        {
            Set(BackupType.TransactionLog, T(1)),
            Set(BackupType.TransactionLog, T(20))
        };

        Assert.Empty(_validator.Validate(Chain(Set(BackupType.Full, T(0)), null, logs)));
    }

    [Fact]
    public void Validate_SingleMissingLog_IsWarned()
    {
        // The most common break and the one most worth catching: exactly one hourly log absent,
        // showing up as a single 2 hour interval. A laxer threshold would miss this entirely.
        var logs = new[]
        {
            Set(BackupType.TransactionLog, T(1)),
            Set(BackupType.TransactionLog, T(2)),
            Set(BackupType.TransactionLog, T(3)),
            Set(BackupType.TransactionLog, T(5)),   // 04:00 missing
            Set(BackupType.TransactionLog, T(6))
        };

        var issue = Assert.Single(_validator.Validate(Chain(Set(BackupType.Full, T(0)), null, logs)));

        Assert.False(issue.IsError);
        Assert.Contains("missing log backup", issue.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_SchedulerJitter_DoesNotWarn()
    {
        // 5-minute logs drifting by a minute or two. Real schedules do this constantly, and
        // warning on it would train the user to ignore the panel.
        var logs = new[]
        {
            Set(BackupType.TransactionLog, T(1, 0)),
            Set(BackupType.TransactionLog, T(1, 5)),
            Set(BackupType.TransactionLog, T(1, 11)),
            Set(BackupType.TransactionLog, T(1, 16)),
            Set(BackupType.TransactionLog, T(1, 22)),
            Set(BackupType.TransactionLog, T(1, 27))
        };

        // Full immediately before the first log, so this test isolates jitter between logs and
        // does not accidentally describe a coverage gap after the full.
        Assert.Empty(_validator.Validate(Chain(Set(BackupType.Full, T(0, 57)), null, logs)));
    }

    // ── log coverage reaching back to the data backup ────────────────────────────

    [Fact]
    public void Validate_FirstLogLongAfterTheDifferential_IsWarned()
    {
        // The retention shape found on real data: logs kept for days, fulls and differentials for
        // weeks, so the oldest surviving log sits long after the differential and cannot connect
        // to it. The chain looks complete - regular hourly logs - but will fail with 4305.
        var chain = Chain(
            Set(BackupType.Full, T(0)),
            [Set(BackupType.Differential, T(1))],
            [
                Set(BackupType.TransactionLog, T(14)),
                Set(BackupType.TransactionLog, T(15)),
                Set(BackupType.TransactionLog, T(16)),
                Set(BackupType.TransactionLog, T(17))
            ]);

        var issue = Assert.Single(_validator.Validate(chain));

        Assert.False(issue.IsError);
        Assert.Contains("may not reach back", issue.Title);
        Assert.Contains("4305", issue.Detail);
    }

    [Fact]
    public void Validate_FirstLogShortlyAfterTheFull_IsClean()
    {
        // A full at 00:00 with hourly logs starting at 01:00 is entirely normal.
        var chain = Chain(
            Set(BackupType.Full, T(0)),
            null,
            [
                Set(BackupType.TransactionLog, T(1)),
                Set(BackupType.TransactionLog, T(2)),
                Set(BackupType.TransactionLog, T(3)),
                Set(BackupType.TransactionLog, T(4))
            ]);

        Assert.Empty(_validator.Validate(chain));
    }

    [Fact]
    public void Validate_TooFewLogsToJudgeCoverage_ProducesNoWarning()
    {
        // With one log there is no cadence to compare against - only the LSN check can tell,
        // and claiming otherwise would be guessing.
        var chain = Chain(
            Set(BackupType.Full, T(0)),
            [Set(BackupType.Differential, T(1))],
            [Set(BackupType.TransactionLog, T(20))]);

        Assert.Empty(_validator.Validate(chain));
    }

    // ── ordering ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_DifferentialOlderThanItsFull_IsAnError()
    {
        var chain = Chain(Set(BackupType.Full, T(5)), [Set(BackupType.Differential, T(2))]);

        Assert.Single(_validator.Validate(chain), i => i.IsError);
    }

    [Fact]
    public void Validate_LogOlderThanTheChainBase_IsAnError()
    {
        var chain = Chain(
            Set(BackupType.Full, T(0)),
            [Set(BackupType.Differential, T(5))],
            [Set(BackupType.TransactionLog, T(2))]);

        Assert.Single(_validator.Validate(chain), i => i.IsError);
    }

    // ── inventory-level ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidateInventory_NoFullAtAll_IsAnError()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Differential, T(2)),
            Set(BackupType.TransactionLog, T(3))
        };

        var issue = Assert.Single(_validator.ValidateInventory(sets));

        Assert.True(issue.IsError);
        Assert.Contains("No full backup", issue.Title);
    }

    [Fact]
    public void ValidateInventory_DifferentialBeforeEarliestFull_IsFlagged()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Differential, T(1)),
            Set(BackupType.Full, T(5))
        };

        Assert.Single(_validator.ValidateInventory(sets),
            i => i.Title.Contains("no base full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateInventory_CopyOnlyFullDoesNotCountAsABase()
    {
        // A copy-only full cannot base a differential (#49), so a differential preceded only by
        // a copy-only full is still orphaned.
        var copyOnly = Set(BackupType.Full, T(1));
        copyOnly.IsCopyOnly = true;

        var sets = new List<BackupSet> { copyOnly, Set(BackupType.Differential, T(2)) };

        Assert.Contains(_validator.ValidateInventory(sets), i => i.IsError);
    }

    [Fact]
    public void ValidateInventory_HealthyInventory_IsClean()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Full, T(0)),
            Set(BackupType.Differential, T(4)),
            Set(BackupType.TransactionLog, T(5))
        };

        Assert.Empty(_validator.ValidateInventory(sets));
    }

    [Fact]
    public void ValidateInventory_Empty_IsClean()
        => Assert.Empty(_validator.ValidateInventory([]));
}
