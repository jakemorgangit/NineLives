using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A backup is not declared present on the strength of a timestamp that was never a backup time
/// (#487).
///
/// The comparison matches by LSN wherever both sides have one, and falls back to type plus
/// timestamp otherwise - container sets only carry LSNs once they have been audited, so the
/// fallback is the ordinary path, not the exception. The tolerance is five seconds, on the stated
/// grounds that "a container set's timestamp is parsed from its file name, which the writer stamps
/// at the START of the backup; msdb records the start too, so these agree exactly".
///
/// That is true only when the timestamp came from the file name. When it could not, the set falls
/// back to the blob's LastModified, and BackupSet says what that means in its own comment: the
/// reading is on a DIFFERENT CLOCK from its neighbours and "can sort it hours out of position".
/// It is also the wrong event - when the upload finished, not when the backup started.
///
/// IsHeld never looked at TimestampSource, so it compared that number to msdb's start time as
/// though the two were the same kind of thing. Two ways that goes wrong, and only one of them is
/// safe:
///
///   No match - the backup is reported missing when it is there. An unnecessary copy.
///   A match against the WRONG backup - the backup is reported present when it is not.
///
/// The second is the one that matters, and at a one-minute log cadence it is not exotic: every
/// approximate reading lands within five seconds of SOME log's start time roughly one time in six,
/// and a container holds hundreds of them. The log is then left out of the copy script, the rescan
/// says everything arrived, and the chain is still broken.
///
/// This file's own doctrine already settles it: "Reporting a present backup as missing costs an
/// unnecessary copy; the reverse would leave somebody restoring to an hour earlier than they could
/// have, so the bias is deliberate." An approximate timestamp is not evidence of presence.
/// </summary>
public class GapCheckDoesNotTrustApproximateTimestampsTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 22, 0, 0);

    private static BackupHistoryEntry Log(DateTime at, decimal? lastLsn = null) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        StartedAt = at,
        LastLsn = lastLsn,
        Files = [$@"E:\SQLLogs\MyDb_{at:yyyyMMdd_HHmmss}.trn"],
        BackupSizeBytes = 10 * 1024 * 1024
    };

    private static BackupSet Held(
        DateTime at,
        BackupTimestampSource source = BackupTimestampSource.FileName,
        decimal? lastLsn = null) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        Timestamp = at,
        TimestampSource = source,
        LastLsn = lastLsn,
        Files = [new BackupFileInfo { BlobName = "x.trn", Type = BackupType.TransactionLog }]
    };

    private static IReadOnlyList<string> MissingFiles(
        IReadOnlyList<BackupHistoryEntry> history, IReadOnlyList<BackupSet> held)
        => BackupGapAnalyser.Compare(history, held, "MyDb")
            .SelectMany(l => l.Backups)
            .SelectMany(b => b.Files)
            .ToList();

    /// <summary>
    /// The failure, in the shape it actually takes: the container holds ONE log, and the only
    /// timestamp available for it is when the blob finished uploading - which happens to land
    /// three seconds after a DIFFERENT log started.
    ///
    /// Both logs must still be named. Neither has been shown to be in the container: the one
    /// that is there cannot be identified, and the one that is not must not inherit its identity.
    /// </summary>
    [Fact]
    public void AnUploadTimeThatLandsNearAnotherBackupDoesNotVouchForIt()
    {
        var history = new[] { Log(T0), Log(T0.AddMinutes(1)) };
        var held = new[] { Held(T0.AddSeconds(3), BackupTimestampSource.BlobLastModified) };

        var missing = MissingFiles(history, held);

        Assert.Contains(missing, f => f.EndsWith("220000.trn", StringComparison.Ordinal));
        Assert.Contains(missing, f => f.EndsWith("220100.trn", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same when the clock has been reconciled but the reading is still an upload time. The
    /// conversion fixes which clock it is on, not which event it describes.
    /// </summary>
    [Fact]
    public void AConvertedUploadTimeIsStillAnUploadTime()
    {
        var history = new[] { Log(T0) };
        var held = new[] { Held(T0.AddSeconds(2), BackupTimestampSource.BlobLastModifiedConverted) };

        Assert.Single(MissingFiles(history, held));
    }

    // ── what must keep working ──────────────────────────────────────────────────

    /// <summary>
    /// A filename timestamp is a backup START time, on the backup server's own clock, and matching
    /// on it is the ordinary path for a container nobody has audited. It has to keep matching, or
    /// every unaudited container reports its entire contents as missing.
    /// </summary>
    [Fact]
    public void AFilenameTimestampStillMatches()
    {
        var history = new[] { Log(T0) };
        var held = new[] { Held(T0, BackupTimestampSource.FileName) };

        Assert.Empty(MissingFiles(history, held));
    }

    /// <summary>And still within the tolerance, for a writer that stamps a second late.</summary>
    [Fact]
    public void AFilenameTimestampWithinTheToleranceStillMatches()
    {
        var history = new[] { Log(T0) };
        var held = new[] { Held(T0.AddSeconds(2), BackupTimestampSource.FileName) };

        Assert.Empty(MissingFiles(history, held));
    }

    /// <summary>
    /// A header timestamp is SQL Server's own account of the backup, on the backup server's clock -
    /// as good as the filename, and better.
    /// </summary>
    [Fact]
    public void AHeaderTimestampStillMatches()
    {
        var history = new[] { Log(T0) };
        var held = new[] { Held(T0, BackupTimestampSource.BackupHeader) };

        Assert.Empty(MissingFiles(history, held));
    }

    /// <summary>
    /// An LSN is the backup itself rather than a description of it, so it settles the question
    /// whatever the timestamp is doing. An audited set matches on LSN even when its only timestamp
    /// is an upload time hours out of position.
    /// </summary>
    [Fact]
    public void AMatchingLsnIsBelievedEvenWhenTheTimestampIsApproximate()
    {
        var history = new[] { Log(T0, lastLsn: 4200m) };
        var held = new[]
        {
            Held(T0.AddHours(-7), BackupTimestampSource.BlobLastModified, lastLsn: 4200m)
        };

        Assert.Empty(MissingFiles(history, held));
    }

    /// <summary>
    /// And an LSN that does not match is not rescued by a nearby approximate timestamp on some
    /// other set. This is the two faults compounding: the entry has a definitive identifier, no
    /// audited set claims it, and an unaudited one vouches for it on a number that is not a
    /// backup time.
    /// </summary>
    [Fact]
    public void ADefinitiveLsnIsNotOverriddenByAnUnauditedSetsUploadTime()
    {
        var history = new[] { Log(T0, lastLsn: 4200m) };
        var held = new[]
        {
            Held(T0.AddMinutes(-1), BackupTimestampSource.FileName, lastLsn: 4100m),
            Held(T0.AddSeconds(1), BackupTimestampSource.BlobLastModified)
        };

        Assert.Single(MissingFiles(history, held));
    }
}

/// <summary>
/// The panel says why a container full of unidentifiable sets reports everything as missing
/// (#487).
///
/// The comparison is right to refuse them - an upload time vouches for nothing - but the result
/// on screen is a container that appears to have lost its entire contents. Without a reason and a
/// remedy that reads as a catastrophe rather than as a limit on what can be known from a listing.
/// </summary>
public class UnidentifiableSetsAreExplainedTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 22, 0, 0);

    private static BackupGapViewModel Panel()
    {
        var sql = new FakeSqlServerService
        {
            BackupHistory =
            [
                new BackupHistoryEntry
                {
                    DatabaseName = "MyDb",
                    Type = BackupType.TransactionLog,
                    StartedAt = T0,
                    Files = [@"E:\SQLLogs\MyDb_20260818_220000.trn"]
                }
            ]
        };

        var vm = new BackupGapViewModel(sql);
        vm.Servers.Add(new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        vm.SourceServer = vm.Servers[0];
        return vm;
    }

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
    };

    private static BackupSet Set(BackupTimestampSource source) => new()
    {
        SetId = "s",
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        Timestamp = T0,
        TimestampSource = source,
        Files = [new BackupFileInfo { BlobName = "x.trn", Type = BackupType.TransactionLog }]
    };

    [Fact]
    public async Task ASetThatCannotBeIdentifiedIsCountedAndTheRemedyNamed()
    {
        var vm = Panel();

        await vm.CheckCommand.ExecuteAsync(
            new GapCheckRequest("MyDb", Container(), [Set(BackupTimestampSource.BlobLastModified)]));

        Assert.Contains("1 of those cannot be identified", vm.ComparedWhat);
        Assert.Contains("Auditing this container", vm.ComparedWhat);
    }

    /// <summary>Silent when every set carries a real backup time, which is the ordinary case.</summary>
    [Fact]
    public async Task NothingIsSaidWhenEverySetCarriesARealBackupTime()
    {
        var vm = Panel();

        await vm.CheckCommand.ExecuteAsync(
            new GapCheckRequest("MyDb", Container(), [Set(BackupTimestampSource.FileName)]));

        Assert.DoesNotContain("cannot be identified", vm.ComparedWhat);
    }
}
