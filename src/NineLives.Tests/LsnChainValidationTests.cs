using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// LSN-level chain validation — the authoritative check, against what SQL Server recorded inside
/// each backup rather than what the filename suggests.
///
/// The LSN values and relationships here mirror those measured from a real backup set before the
/// rules were written:
///   diff.DatabaseBackupLSN == full.CheckpointLSN
///   consecutive logs meet exactly: log[i].FirstLSN == log[i-1].LastLSN
///   only the FIRST log spans the recovery point
/// </summary>
public class LsnChainValidationTests
{
    private readonly BackupChainValidator _validator = new();

    private static DateTime T(int hour) => new(2026, 8, 4, hour, 0, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp, string? db = "Utility") => new()
    {
        SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = timestamp,
        DatabaseName = db,
        Files = [new BackupFileInfo { BlobName = $"{timestamp:yyyyMMdd_HHmmss}.bak", Type = type, SizeBytes = 100 }]
    };

    private static BackupFileInfo Header(
        BackupType type,
        decimal firstLsn, decimal lastLsn,
        decimal? checkpointLsn = null,
        decimal? databaseBackupLsn = null,
        string db = "Utility") => new()
        {
            Type = type,
            DatabaseName = db,
            FirstLsn = firstLsn,
            LastLsn = lastLsn,
            CheckpointLsn = checkpointLsn ?? firstLsn,
            DatabaseBackupLsn = databaseBackupLsn
        };

    // A healthy full + diff + 3 logs, using realistic LSN spacing.
    private const decimal FullFirst = 481000000008800001m;
    private const decimal FullLast = 481000000011200001m;
    private const decimal DiffFirst = 482000001412800001m;
    private const decimal DiffLast = 482000001415200001m;
    private const decimal Log1First = 482000001058400001m;
    private const decimal Log1Last = 482000001946400001m;
    private const decimal Log2Last = 482000001967200001m;
    private const decimal Log3Last = 482000001986400001m;

    private static (BackupChain chain, List<ChainHeader> headers) HealthyChain()
    {
        var full = Set(BackupType.Full, T(0));
        var diff = Set(BackupType.Differential, T(4));
        var log1 = Set(BackupType.TransactionLog, T(5));
        var log2 = Set(BackupType.TransactionLog, T(6));
        var log3 = Set(BackupType.TransactionLog, T(7));

        var chain = new BackupChain { FullSet = full, DiffSets = [diff], LogSets = [log1, log2, log3] };

        var headers = new List<ChainHeader>
        {
            new(full, Header(BackupType.Full, FullFirst, FullLast, checkpointLsn: FullFirst)),
            new(diff, Header(BackupType.Differential, DiffFirst, DiffLast, databaseBackupLsn: FullFirst)),
            new(log1, Header(BackupType.TransactionLog, Log1First, Log1Last)),
            new(log2, Header(BackupType.TransactionLog, Log1Last, Log2Last)),
            new(log3, Header(BackupType.TransactionLog, Log2Last, Log3Last))
        };

        return (chain, headers);
    }

    [Fact]
    public void ValidateLsnChain_HealthyChain_IsClean()
    {
        var (chain, headers) = HealthyChain();
        Assert.Empty(_validator.ValidateLsnChain(chain, headers));
    }

    [Fact]
    public void ValidateLsnChain_NoHeaders_ProducesNothing()
        => Assert.Empty(_validator.ValidateLsnChain(HealthyChain().chain, []));

    [Fact]
    public void ValidateLsnChain_LaterLogsNeedNotSpanTheRecoveryPoint()
    {
        // Measured behaviour: only the first log spans the base point; later logs start after it.
        // Requiring all of them to span it would reject every valid chain.
        var (chain, headers) = HealthyChain();

        var issues = _validator.ValidateLsnChain(chain, headers);

        Assert.DoesNotContain(issues, i => i.Title.Contains("starts too late"));
    }

    // ── differential base ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateLsnChain_DifferentialFromADifferentFull_IsAnError()
    {
        // The copy-only scenario as SQL Server sees it: timestamps look fine, but the
        // differential's DatabaseBackupLSN points at a full that is not the one in the chain.
        var (chain, headers) = HealthyChain();
        var diff = chain.DiffSets[0];
        headers[1] = new ChainHeader(diff,
            Header(BackupType.Differential, DiffFirst, DiffLast, databaseBackupLsn: 999000000000000001m));

        var issue = Assert.Single(_validator.ValidateLsnChain(chain, headers), i => i.IsError);

        Assert.Contains("not based on this full", issue.Title);
        Assert.Contains("3136", issue.Detail);
    }

    [Fact]
    public void ValidateLsnChain_DifferentialMatchingCheckpointLsn_IsAccepted()
    {
        // The rule is against CheckpointLSN specifically, not FirstLSN - they coincide on many
        // fulls but are not the same field.
        var full = Set(BackupType.Full, T(0));
        var diff = Set(BackupType.Differential, T(4));
        var chain = new BackupChain { FullSet = full, DiffSets = [diff] };

        var headers = new List<ChainHeader>
        {
            new(full, Header(BackupType.Full, firstLsn: 100m, lastLsn: 200m, checkpointLsn: 150m)),
            new(diff, Header(BackupType.Differential, 300m, 400m, databaseBackupLsn: 150m))
        };

        Assert.Empty(_validator.ValidateLsnChain(chain, headers));
    }

    [Fact]
    public void ValidateLsnChain_DifferentialMatchingFirstLsnButNotCheckpoint_IsRejected()
    {
        var full = Set(BackupType.Full, T(0));
        var diff = Set(BackupType.Differential, T(4));
        var chain = new BackupChain { FullSet = full, DiffSets = [diff] };

        var headers = new List<ChainHeader>
        {
            new(full, Header(BackupType.Full, firstLsn: 100m, lastLsn: 200m, checkpointLsn: 150m)),
            new(diff, Header(BackupType.Differential, 300m, 400m, databaseBackupLsn: 100m))
        };

        Assert.Single(_validator.ValidateLsnChain(chain, headers), i => i.IsError);
    }

    // ── log continuity ───────────────────────────────────────────────────────────

    [Fact]
    public void ValidateLsnChain_GapBetweenLogs_IsAnError()
    {
        // The break timestamps cannot see: a log taken by another job to another destination.
        // The visible sequence stays perfectly regular; the LSN chain does not.
        var (chain, headers) = HealthyChain();
        headers[3] = new ChainHeader(chain.LogSets[1],
            Header(BackupType.TransactionLog, Log1Last + 5000m, Log2Last));

        var issue = Assert.Single(_validator.ValidateLsnChain(chain, headers), i => i.IsError);

        Assert.Contains("Break in the log chain", issue.Title);
        Assert.Contains("4305", issue.Detail);
    }

    [Fact]
    public void ValidateLsnChain_OverlappingLogs_AreAccepted()
    {
        // An overlap is not a gap - it restores fine.
        var (chain, headers) = HealthyChain();
        headers[3] = new ChainHeader(chain.LogSets[1],
            Header(BackupType.TransactionLog, Log1Last - 5000m, Log2Last));

        Assert.Empty(_validator.ValidateLsnChain(chain, headers));
    }

    [Fact]
    public void ValidateLsnChain_FirstLogStartsAfterTheRecoveryPoint_IsAnError()
    {
        var (chain, headers) = HealthyChain();
        headers[2] = new ChainHeader(chain.LogSets[0],
            Header(BackupType.TransactionLog, DiffLast + 1000m, Log1Last));

        Assert.Single(_validator.ValidateLsnChain(chain, headers),
            i => i.IsError && i.Title.Contains("starts too late"));
    }

    // ── identity and type ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateLsnChain_ChainMixingTwoDatabases_IsAnError()
    {
        var (chain, headers) = HealthyChain();
        headers[2] = new ChainHeader(chain.LogSets[0],
            Header(BackupType.TransactionLog, Log1First, Log1Last, db: "SomeOtherDatabase"));

        Assert.Single(_validator.ValidateLsnChain(chain, headers),
            i => i.IsError && i.Title.Contains("different databases"));
    }

    [Fact]
    public void ValidateLsnChain_HeaderDatabaseDiffersFromThePath_IsAWarning()
    {
        // Every header agrees, but the path pattern inferred a different name - the backups are
        // consistent, the parsing is not.
        var full = Set(BackupType.Full, T(0), db: "PathSaysThis");
        var chain = new BackupChain { FullSet = full };
        var headers = new List<ChainHeader>
        {
            new(full, Header(BackupType.Full, 100m, 200m, db: "HeaderSaysThat"))
        };

        var issue = Assert.Single(_validator.ValidateLsnChain(chain, headers));

        Assert.False(issue.IsError);
        Assert.Contains("different database than the path suggests", issue.Title);
    }

    [Fact]
    public void ValidateLsnChain_FilenameTypeContradictsTheHeader_IsAnError()
    {
        // Catches a misparsed filename - e.g. a log backup written with a .bak extension and
        // therefore classified as a full.
        var set = Set(BackupType.Full, T(0));
        var chain = new BackupChain { FullSet = set };
        var headers = new List<ChainHeader>
        {
            new(set, Header(BackupType.TransactionLog, 100m, 200m))
        };

        Assert.Single(_validator.ValidateLsnChain(chain, headers),
            i => i.IsError && i.Title.Contains("not the type its filename suggests"));
    }

    // ── partial reads ────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateLsnChain_UnreadableMember_WarnsAndKeepsCheckingTheRest()
    {
        var (chain, headers) = HealthyChain();
        headers[3] = new ChainHeader(chain.LogSets[1], null);   // header read failed

        var issues = _validator.ValidateLsnChain(chain, headers);

        Assert.Contains(issues, i => !i.IsError && i.Title.Contains("Could not read"));
        // The unreadable member is skipped rather than reported as a break.
        Assert.DoesNotContain(issues, i => i.Title.Contains("Break in the log chain"));
    }

    [Fact]
    public void ValidateLsnChain_MissingLsnValues_AreSkippedNotReportedAsBreaks()
    {
        var (chain, headers) = HealthyChain();
        headers[3] = new ChainHeader(chain.LogSets[1],
            new BackupFileInfo { Type = BackupType.TransactionLog, DatabaseName = "Utility" });

        Assert.DoesNotContain(_validator.ValidateLsnChain(chain, headers), i => i.IsError);
    }
}
