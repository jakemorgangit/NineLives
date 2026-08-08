using System.Windows.Shell;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The server's progress prose, turned back into a number (#204).
///
/// The scripts run WITH STATS = 10 and the "10 percent processed." lines were scrolling past as
/// console text - a 40-minute restore looked identical at 5% and 95% unless somebody read the
/// log. The number was always there; these pin that it survives the trip.
/// </summary>
public class RestoreProgressTests
{
    private const string ChainScript = @"
RESTORE DATABASE [MyDb] FROM DISK = N'D:\full.bak' WITH NORECOVERY, STATS = 10;
GO
RESTORE LOG [MyDb] FROM DISK = N'D:\log1.trn' WITH NORECOVERY, STATS = 10;
GO
RESTORE LOG [MyDb] FROM DISK = N'D:\log2.trn' WITH RECOVERY, STATS = 10;";

    // ── counting the denominator ────────────────────────────────────────────────

    /// <summary>From the script itself, so it can never disagree with what runs.</summary>
    [Fact]
    public void StatementsAreCountedFromTheScript()
    {
        Assert.Equal(3, RestoreProgress.CountStatements(ChainScript));
    }

    /// <summary>VERIFYONLY and friends report no STATS and must not dilute the total.</summary>
    [Fact]
    public void InspectionStatementsDoNotCount()
    {
        var script = @"
RESTORE VERIFYONLY FROM DISK = N'D:\full.bak';
RESTORE HEADERONLY FROM DISK = N'D:\full.bak';
RESTORE DATABASE [MyDb] FROM DISK = N'D:\full.bak' WITH RECOVERY;";

        Assert.Equal(1, RestoreProgress.CountStatements(script));
    }

    // ── the trajectory ──────────────────────────────────────────────────────────

    [Fact]
    public void PercentLinesMoveTheBar()
    {
        var progress = new RestoreProgress(1);

        Assert.True(progress.Feed("10 percent processed."));
        Assert.Equal(10, progress.OverallPercent, 1);

        Assert.True(progress.Feed("90 percent processed."));
        Assert.Equal(90, progress.OverallPercent, 1);
    }

    /// <summary>
    /// Across a chain, a finished statement is worth its whole share - and the server does not
    /// always say "100 percent" before the completion line, so completion IS the 100.
    /// </summary>
    [Fact]
    public void AChainAdvancesByStatement()
    {
        var progress = new RestoreProgress(3);

        progress.Feed("50 percent processed.");
        Assert.Equal(100.0 / 3 / 2, progress.OverallPercent, 1);
        Assert.Equal("Statement 1 of 3 - 50%", progress.Describe());

        progress.Feed("RESTORE DATABASE successfully processed 823 pages in 0.208 seconds (30.9 MB/sec).");
        Assert.Equal(100.0 / 3, progress.OverallPercent, 1);

        progress.Feed("30 percent processed.");
        Assert.Equal("Statement 2 of 3 - 30%", progress.Describe());

        progress.Feed("RESTORE LOG successfully processed 12 pages in 0.011 seconds (8.1 MB/sec).");
        progress.Feed("RESTORE LOG successfully processed 9 pages in 0.009 seconds (7.4 MB/sec).");

        Assert.Equal(100, progress.OverallPercent, 1);
        Assert.Equal("All 3 statements complete", progress.Describe());
    }

    /// <summary>Ordinary output moves nothing, and says so - the caller skips re-rendering.</summary>
    [Fact]
    public void OrdinaryLinesAreIgnored()
    {
        var progress = new RestoreProgress(1);

        Assert.False(progress.Feed("Processed 823 pages for database 'MyDb', file 'MyDb' on file 1."));
        Assert.False(progress.Feed("Beginning restore execution..."));
        Assert.Equal(0, progress.OverallPercent, 1);
    }

    /// <summary>"1 of 1" is noise wearing a number - one statement shows the bare percent.</summary>
    [Fact]
    public void OneStatementSpeaksPlainly()
    {
        var progress = new RestoreProgress(1);
        progress.Feed("40 percent processed.");

        Assert.Equal("40%", progress.Describe());
    }

    /// <summary>More completions than statements cannot push past 100.</summary>
    [Fact]
    public void ProgressNeverExceedsTheWhole()
    {
        var progress = new RestoreProgress(1);
        progress.Feed("RESTORE DATABASE successfully processed 1 pages in 0.001 seconds (1.0 MB/sec).");
        progress.Feed("RESTORE DATABASE successfully processed 1 pages in 0.001 seconds (1.0 MB/sec).");

        Assert.Equal(100, progress.OverallPercent, 1);
    }

    // ── the execution surface and the taskbar ───────────────────────────────────

    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql) New()
    {
        var sql = new FakeSqlServerService();
        var vm = new RestoreExecutionViewModel(
            sql, new FakeRestoreHistoryStore(), TestLogs.Temp(), new OperationCancellation());
        return (vm, sql);
    }

    private static RestoreRun Run() => new(
        Server: new ServerConnection { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" },
        Script: "RESTORE DATABASE [MyDb] FROM DISK = N'D:\\b.bak' WITH REPLACE, RECOVERY;",
        TargetDatabase: "MyDb",
        SourceDatabase: null,
        ContainerName: null,
        ChainSummary: "1 full",
        RestorePointTimestamp: null,
        OptionsForLog: "WITH REPLACE, RECOVERY");

    /// <summary>A finished run reads 100 even when the last STATS line never said so.</summary>
    [Fact]
    public async Task SuccessEndsAtOneHundred()
    {
        var (vm, _) = New();

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Equal(100, vm.ProgressValue, 1);
        Assert.Equal(1.0, vm.TaskbarValue, 2);
        Assert.Equal(TaskbarItemProgressState.None, vm.TaskbarState);
    }

    /// <summary>
    /// Failure stays red on the taskbar until the next run - it is the signal somebody who
    /// alt-tabbed away most needs to see.
    /// </summary>
    [Fact]
    public async Task FailureStaysRedOnTheTaskbar()
    {
        var (vm, sql) = New();
        sql.ExecuteThrows = new InvalidOperationException("Msg 3201");

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Equal(TaskbarItemProgressState.Error, vm.TaskbarState);
        Assert.Equal(1.0, vm.TaskbarValue, 2);
    }

    [Fact]
    public async Task TheNextRunResetsTheBar()
    {
        var (vm, sql) = New();
        sql.ExecuteThrows = new InvalidOperationException("boom");
        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));
        Assert.Equal(TaskbarItemProgressState.Error, vm.TaskbarState);

        sql.ExecuteThrows = null;
        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Equal(TaskbarItemProgressState.None, vm.TaskbarState);
        Assert.Equal(100, vm.ProgressValue, 1);
    }
}
