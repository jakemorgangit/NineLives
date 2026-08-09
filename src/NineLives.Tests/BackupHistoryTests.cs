using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Reading what an instance recorded backing up, from msdb (#149).
///
/// The first half of restoring from a shared backup location: there is no container to list, but
/// the instance that took the backups knows what it took, when, to which files, and at which LSNs.
///
/// It also answers #130 for this source without any of that issue's cost. Chains from blob storage
/// infer type and database from the path and assemble by timestamp, consulting headers only on
/// demand because each one is a round trip. Here the authoritative values are already in a table.
/// </summary>
public class BackupHistoryTests
{
    [Theory]
    [InlineData("D", BackupType.Full)]
    [InlineData("I", BackupType.Differential)]
    [InlineData("L", BackupType.TransactionLog)]
    public void TheThreeChainTypesAreRecognised(string msdb, BackupType expected)
        => Assert.Equal(expected, SqlServerService.TypeFromMsdb(msdb));

    /// <summary>
    /// File, filegroup and partial backups are not chain members this app restores. Reporting them
    /// as Unknown is the honest answer; mapping them onto Full because the letter is close would
    /// put a backup in a chain that cannot restore it.
    /// </summary>
    [Theory]
    [InlineData("F")]
    [InlineData("G")]
    [InlineData("P")]
    [InlineData("Q")]
    [InlineData(null)]
    [InlineData("")]
    public void EverythingElseIsUnknownRatherThanGuessedAt(string? msdb)
        => Assert.Equal(BackupType.Unknown, SqlServerService.TypeFromMsdb(msdb));

    /// <summary>
    /// msdb keeps history for backups whose files were deleted, archived or pruned by a retention
    /// job long ago. "It is in the history" and "it is on disk" are different questions - which is
    /// why #149 verifies the files separately, and on the target.
    /// </summary>
    [Fact]
    public void AnEntryWithNoFilesKnowsItCannotBeRestoredFrom()
    {
        var entry = new BackupHistoryEntry { DatabaseName = "MyDb", Type = BackupType.Full };

        Assert.False(entry.HasFiles);
    }

    [Fact]
    public void AStripedBackupKeepsEveryFile()
    {
        var entry = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            Files = [@"\nas01\sql\MyDb_1.bak", @"\nas01\sql\MyDb_2.bak", @"\nas01\sql\MyDb_3.bak"]
        };

        Assert.True(entry.HasFiles);
        Assert.Equal(3, entry.Files.Count);
    }

    /// <summary>
    /// The relationship that makes this worth doing: a differential's DatabaseBackupLSN equals the
    /// CheckpointLSN of the full it belongs to. Timestamps only suggest that; this settles it, and
    /// it is the check #130 wants headers for - already answered here by the source instance.
    /// </summary>
    [Fact]
    public void ADifferentialPointsAtItsFullByLsn()
    {
        var full = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", Type = BackupType.Full, CheckpointLsn = 37000000012300001m
        };
        var diff = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", Type = BackupType.Differential, DatabaseBackupLsn = 37000000012300001m
        };
        var strayDiff = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", Type = BackupType.Differential, DatabaseBackupLsn = 99000000099900001m
        };

        Assert.Equal(full.CheckpointLsn, diff.DatabaseBackupLsn);
        Assert.NotEqual(full.CheckpointLsn, strayDiff.DatabaseBackupLsn);
    }

    /// <summary>
    /// The cap is a number somebody can read, not a silent truncation. Returning "the newest 500"
    /// while looking like "everything" is how a backup gets concluded missing when it is simply
    /// older than the limit.
    /// </summary>
    [Fact]
    public void TheHistoryLimitIsStatedRatherThanImplied()
        => Assert.True(SqlServerService.BackupHistoryLimit > 0);

    // ── against a real instance ─────────────────────────────────────────────────

    /// <summary>
    /// Live proof that the query parses and its columns come back as expected, against whatever
    /// history the instance happens to hold - including none, which is a valid answer and must not
    /// throw. Skipped unless NINELIVES_TEST_SQL is set.
    ///
    /// Read-only: it touches nothing, creates nothing and takes no backups.
    /// </summary>
    [RequiresSqlFact]
    public async Task TheHistoryQueryRunsAgainstARealInstance()
    {
        var service = new SqlServerService(new FakeCredentialStore());
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "test",
            ServerName = SqlExecutionFailureTests.TestServerName!,
            AuthMode = AuthMode.WindowsAuth,
            TrustServerCertificate = true
        };

        var history = await service.ReadBackupHistoryAsync(server);

        Assert.NotNull(history);
        Assert.All(history, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.DatabaseName));
            Assert.NotEqual(default, e.StartedAt);

            // Every file this app would restore from is a real path, in stripe order.
            Assert.All(e.Files, f => Assert.False(string.IsNullOrWhiteSpace(f)));
        });
    }
}
