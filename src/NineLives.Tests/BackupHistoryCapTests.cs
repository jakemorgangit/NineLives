using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// How much history the missing-backups check reads, and what it says when that was not all of it
/// (#484).
///
/// Reported from a real run: a container more than sixteen hours behind, and the panel listing
/// exactly 500 log backups covering about eight hours - the newest 500 of far more. Every log older
/// than the cap was invisible to the one feature whose job is to name backups that are missing.
///
/// Under-reporting is the dangerous direction here. A short list reads as an all-clear, so the copy
/// script names the files it knows about, the rescan says they all arrived, and the chain is still
/// broken by the ones nobody was told about.
///
/// The cap itself stays - msdb on an instance nobody prunes holds years - but it is asked for at a
/// size that suits the question, and the panel says when it was reached instead of presenting the
/// newest N as the whole history.
/// </summary>
public class BackupHistoryCapTests
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

    /// <summary>One log backup, as msdb records it, at a minute-by-minute cadence.</summary>
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

    // ── how deep it reads ───────────────────────────────────────────────────────

    /// <summary>
    /// The check asks for its own depth, not the general-purpose one. A database on a one-minute
    /// log schedule takes 1,440 backups a day, so the 500 that suits a recent-end restore listing
    /// covers about eight hours here - and this check is routinely asked about longer than that.
    /// </summary>
    [Fact]
    public async Task TheCheckAsksForMoreHistoryThanTheGeneralListingDoes()
    {
        var (vm, sql) = Panel(10);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.Equal(SqlServerService.GapCheckHistoryLimit, sql.LastHistoryLimit);
        Assert.True(SqlServerService.GapCheckHistoryLimit > SqlServerService.BackupHistoryLimit);
    }

    /// <summary>Enough for more than a day of minute-by-minute logs, which 500 is not.</summary>
    [Fact]
    public void TheCheckSDepthCoversMoreThanADayOfMinuteByMinuteLogs()
        => Assert.True(SqlServerService.GapCheckHistoryLimit > 24 * 60);

    // ── saying so when it was not all of it ─────────────────────────────────────

    /// <summary>
    /// A read that came back full means the instance has at least this much and probably more.
    /// Saying nothing there is what turned a truncated answer into an apparent complete one.
    /// </summary>
    [Fact]
    public async Task AFullReadSaysTheAnswerMayBeShort()
    {
        var (vm, _) = Panel(SqlServerService.GapCheckHistoryLimit + 50);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.True(vm.HasHistoryCapNote);
        Assert.Contains("this list may be short", vm.HistoryCapNote);

        // And how far back it actually got, so the reader can see where the blind spot starts.
        Assert.Contains(
            T0.AddMinutes(-(SqlServerService.GapCheckHistoryLimit - 1)).ToString("yyyy-MM-dd HH:mm"),
            vm.HistoryCapNote);
    }

    /// <summary>
    /// And stays quiet otherwise, because a caution on every check is a caution nobody reads.
    /// </summary>
    [Fact]
    public async Task AHistoryShorterThanTheCapSaysNothing()
    {
        var (vm, _) = Panel(25);

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.False(vm.HasHistoryCapNote);
        Assert.Empty(vm.HistoryCapNote);
    }

    /// <summary>The caution belongs to the check that produced it, not to the panel.</summary>
    [Fact]
    public async Task AFreshCheckDropsThePreviousCaution()
    {
        var (vm, sql) = Panel(SqlServerService.GapCheckHistoryLimit + 50);
        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));
        Assert.True(vm.HasHistoryCapNote);

        sql.BackupHistory = [Log(1), Log(2)];
        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("MyDb", Container(), []));

        Assert.False(vm.HasHistoryCapNote);
    }

    /// <summary>
    /// The Copy screen's version of the question reads the same history and is capped the same
    /// way, so it has to be as honest about it.
    /// </summary>
    [Fact]
    public async Task TheCopyScreensCheckSaysItToo()
    {
        var (vm, _) = Panel(SqlServerService.GapCheckHistoryLimit + 50);

        await vm.CheckLogsAfterAsync("MyDb", T0.AddDays(-30));

        Assert.True(vm.HasHistoryCapNote);
    }
}

/// <summary>
/// The shape of the history read itself (#484).
///
/// Not a substitute for running it - that needs a real msdb, and these pin only what a careless
/// edit would undo. But the thing they pin is exactly what was wrong: the cap sat on the joined
/// result, where it counted FILES rather than backups.
///
/// backupmediafamily holds one row per file a backup was written to, so a four-way striped backup
/// is four rows. Capping there meant a limit described and reasoned about as 500 backups returned
/// 125 of them - and worse, the cut could land INSIDE a set, so the oldest entry came back holding
/// some of its stripes. That entry is indistinguishable from a genuine single-file backup: a
/// RESTORE built from it names two of four devices and fails, and this very check reports the
/// stripes beyond the cut as files the container is missing when they are neither.
/// </summary>
public class BackupHistoryQueryShapeTests
{
    private const string Sql = SqlServerService.BackupHistoryQuery;

    /// <summary>The cap picks backup SETS, in its own pass, before any file rows are involved.</summary>
    [Fact]
    public void TheCapIsTakenOverBackupSetsNotOverFileRows()
    {
        Assert.Contains("WITH recent AS", Sql);
        Assert.Contains("SELECT TOP (@limit) bs.backup_set_id", Sql);

        // The CTE names one table. Whatever else it grows, the moment it joins the media families
        // the cap is counting files again.
        var cte = Sql[Sql.IndexOf("WITH recent AS", StringComparison.Ordinal)..
                      Sql.IndexOf("SELECT bs.backup_set_id", StringComparison.Ordinal)];
        Assert.DoesNotContain("JOIN msdb.dbo.backupmediafamily", cte);
    }

    /// <summary>One cap, so there is no second one further down doing the old job.</summary>
    [Fact]
    public void ThereIsExactlyOneCap()
        => Assert.Equal(1, Sql.Split("TOP (").Length - 1);

    /// <summary>
    /// Every family of the sets that survived the cap, so a striped backup arrives whole. The
    /// outer query filters by device type but never re-applies a row limit.
    /// </summary>
    [Fact]
    public void TheFamiliesAreJoinedAfterTheCapAndAreNotLimited()
    {
        var outer = Sql[Sql.IndexOf("SELECT bs.backup_set_id", StringComparison.Ordinal)..];

        Assert.Contains("JOIN recent AS r", outer);
        Assert.Contains("JOIN msdb.dbo.backupmediafamily", outer);
        Assert.DoesNotContain("TOP", outer);
        Assert.Contains("family_sequence_number", outer);
    }

    /// <summary>The cap is a parameter, not pasted into the statement.</summary>
    [Fact]
    public void TheCapIsParameterised()
    {
        Assert.Contains("@limit", Sql);
        Assert.DoesNotContain("TOP (500)", Sql);
    }
}
