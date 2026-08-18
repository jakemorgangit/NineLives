using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The instance's backup history is read WHOLE (#486).
///
/// It used to come back capped, and the cap was never asked for by anybody - it was the default.
/// Every caller feeds something that a short answer quietly breaks:
///
///   the missing-backups check, whose entire job is naming backups a container has not got
///   the restore inventory, which turns the answer into the chain that gets restored
///   the CLI's inventory, the same
///   the Browse Backups listing
///
/// "The newest N" is indistinguishable on screen from "all there is", and the direction it is
/// wrong in is the one that reads as an all-clear: copy the files it named, press rescan, be told
/// they all arrived, and the chain is still broken by the logs nobody was told about.
///
/// A cap can still be asked for. Nothing asks.
/// </summary>
public class BackupHistoryIsNotCappedTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 9, 0, 0);

    private static ServerConnection Server() => new()
    { Id = "s1", Name = "SRV01", ServerName = "SRV01" };

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
    };

    /// <summary>One log backup, at the minute-by-minute cadence that made 500 laughable.</summary>
    private static BackupHistoryEntry Log(int minutesBeforeT0) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        StartedAt = T0.AddMinutes(-minutesBeforeT0),
        Files = [$@"E:\SQLLogs\MyDb_{minutesBeforeT0}.trn"],
        BackupSizeBytes = 10 * 1024 * 1024
    };

    private static (BackupGapViewModel vm, FakeSqlServerService sql) Panel(int logCount)
    {
        var sql = new FakeSqlServerService
        {
            BackupHistory = Enumerable.Range(0, logCount).Select(Log).ToList()
        };

        var vm = new BackupGapViewModel(sql);
        vm.Servers.Add(Server());
        vm.SourceServer = vm.Servers[0];
        return (vm, sql);
    }

    // ── the check asks for everything ───────────────────────────────────────────

    [Fact]
    public async Task TheMissingBackupsCheckAsksForTheWholeHistory()
    {
        var (vm, sql) = Panel(10);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.False(sql.LastHistoryReadWasCapped);
        Assert.Null(sql.LastHistoryLimit);
    }

    /// <summary>The Copy screen's version of the question reads the same history, the same way.</summary>
    [Fact]
    public async Task TheCopyScreensCheckAsksForTheWholeHistoryToo()
    {
        var (vm, sql) = Panel(10);

        await vm.CheckLogsAfterAsync("MyDb", T0.AddDays(-30));

        Assert.False(sql.LastHistoryReadWasCapped);
    }

    /// <summary>
    /// The number that started this. A container sixteen hours behind, against a database taking a
    /// log a minute: nearly twice the old cap, and every one of them has to be named or the copy
    /// script that follows is incomplete.
    /// </summary>
    [Fact]
    public async Task EveryLogIsReportedNotJustTheNewestFiveHundred()
    {
        const int SixteenHoursOfMinuteByMinuteLogs = 16 * 60;
        var (vm, _) = Panel(SixteenHoursOfMinuteByMinuteLogs);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.True(vm.HasGap);
        Assert.Equal(SixteenHoursOfMinuteByMinuteLogs, vm.Locations.Sum(l => l.FileCount));

        // And the OLDEST is named, not just the recent end - that is the one a cap loses.
        var oldest = $@"E:\SQLLogs\MyDb_{SixteenHoursOfMinuteByMinuteLogs - 1}.trn";
        Assert.Contains(
            vm.Locations.SelectMany(l => l.Location.Backups).SelectMany(b => b.Files),
            f => f == oldest);
    }

    /// <summary>
    /// Well past any round number, so a plausible-looking total cannot be read as the real one.
    /// </summary>
    [Fact]
    public async Task AHistoryOfTensOfThousandsComesBackWhole()
    {
        var (vm, _) = Panel(25_000);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.Equal(25_000, vm.Locations.Sum(l => l.FileCount));
        Assert.Contains("25000 backup(s) in its history", vm.ComparedWhat);
    }
}

/// <summary>
/// The shape of the history read itself (#484, #486).
///
/// Not a substitute for running it - that needs a real msdb - but these pin what a careless edit
/// would undo, and the second of them is exactly what was wrong before.
///
/// backupmediafamily holds one row per file a backup was written to, so a four-way striped backup
/// is four rows. The cap used to sit on the joined result, where it counted those: a limit
/// described and reasoned about as 500 backups returned 125 of them, and the cut could land INSIDE
/// a set, leaving an entry holding only some of its stripes. That entry is indistinguishable from
/// a genuine single-file backup - a RESTORE built from it names two of four devices and fails, and
/// the missing-backups check reports the stripes beyond the cut as files the container lacks.
/// </summary>
public class BackupHistoryQueryShapeTests
{
    private static readonly string Uncapped = SqlServerService.BuildBackupHistoryQuery(capped: false);
    private static readonly string Capped = SqlServerService.BuildBackupHistoryQuery(capped: true);

    /// <summary>What every caller in the app gets: no row limit anywhere in the statement.</summary>
    [Fact]
    public void TheDefaultStatementHasNoLimitAtAll()
    {
        Assert.DoesNotContain("TOP", Uncapped);
        Assert.DoesNotContain("@limit", Uncapped);
    }

    /// <summary>And when one IS asked for, it counts backup sets rather than file rows.</summary>
    [Fact]
    public void ACapIsTakenOverBackupSetsNotOverFileRows()
    {
        Assert.Contains("SELECT TOP (@limit) bs.backup_set_id", Capped);

        // The moment the inner query joins the media families, the cap is counting files again.
        var inner = Capped[Capped.IndexOf("WITH candidates AS", StringComparison.Ordinal)..
                           Capped.IndexOf("SELECT bs.backup_set_id", StringComparison.Ordinal)];
        Assert.DoesNotContain("JOIN msdb.dbo.backupmediafamily", inner);
    }

    /// <summary>One cap, so there is no second one further down doing the old job.</summary>
    [Fact]
    public void ACappedStatementCapsExactlyOnce()
        => Assert.Equal(1, Capped.Split("TOP (").Length - 1);

    /// <summary>
    /// Every family of the sets that survived, so a striped backup arrives whole either way.
    /// </summary>
    [Fact]
    public void TheFamiliesAreJoinedAfterwardsAndAreNeverLimited()
    {
        foreach (var sql in new[] { Uncapped, Capped })
        {
            var outer = sql[sql.IndexOf("SELECT bs.backup_set_id", StringComparison.Ordinal)..];

            Assert.Contains("JOIN candidates AS c", outer);
            Assert.Contains("JOIN msdb.dbo.backupmediafamily", outer);
            Assert.Contains("family_sequence_number", outer);
            Assert.DoesNotContain("TOP", outer);
        }
    }

    /// <summary>The cap is a parameter, never pasted into the statement.</summary>
    [Fact]
    public void ACapIsParameterised() => Assert.DoesNotContain("TOP (500)", Capped);
}
