using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What finishes a restore (#205).
///
/// The recovery panel (#14) handles databases left in a bad state. This is its counterpart for
/// the restore that WORKED, because "restored" is not the end of the job: nobody has verified the
/// data, and on a different server every SQL-auth user is orphaned - login succeeds, database
/// access fails, and nothing on screen says why.
/// </summary>
public class PostRestoreTests
{
    // ── the advice itself ───────────────────────────────────────────────────────

    [Fact]
    public void CheckDbNamesTheDatabaseAndHoldsNoSurprises()
    {
        var action = PostRestoreAdvice.CheckDb("MyDb");

        Assert.Equal("DBCC CHECKDB ([MyDb]) WITH NO_INFOMSGS", action.Sql);
        Assert.Contains("Duration", action.Caution);
    }

    /// <summary>Quoting goes through TSql, so a bracket in a name cannot break out.</summary>
    [Fact]
    public void CheckDbQuotesAwkwardNames()
    {
        var action = PostRestoreAdvice.CheckDb("My]Db");

        Assert.Contains("[My]]Db]", action.Sql);
    }

    [Fact]
    public void AFixableOrphanRemapsTheSid()
    {
        var action = PostRestoreAdvice.FixOrphan("MyDb", new OrphanedUser("app_user", true));

        Assert.Contains("ALTER USER [app_user] WITH LOGIN = [app_user]", action.Sql);
        Assert.Contains("USE [MyDb]", action.Sql);
    }

    /// <summary>
    /// No login to map onto means no runnable statement is invented - inventing one means
    /// inventing a password. The CREATE LOGIN line appears only as commented guidance.
    /// </summary>
    [Fact]
    public void AnUnmappableOrphanGetsGuidanceNotAStatement()
    {
        var action = PostRestoreAdvice.ExplainUnmappableOrphan("MyDb", new OrphanedUser("ghost", false));

        Assert.Contains("-- CREATE LOGIN [ghost]", action.Sql);
        Assert.Contains("no login of that name", action.Caution);
    }

    /// <summary>Guidance is not a statement: the unmappable card copies but never runs.</summary>
    [Fact]
    public void GuidanceIsNotRunnable()
    {
        Assert.False(PostRestoreAdvice.ExplainUnmappableOrphan("MyDb", new OrphanedUser("ghost", false)).Runnable);
        Assert.True(PostRestoreAdvice.FixOrphan("MyDb", new OrphanedUser("a", true)).Runnable);
        Assert.True(PostRestoreAdvice.CheckDb("MyDb").Runnable);
    }

    [Fact]
    public void TheOrphanVerdictCountsHonestly()
    {
        Assert.Contains("No orphaned users", PostRestoreAdvice.DescribeOrphans([]));
        Assert.Contains("1 database user is orphaned",
            PostRestoreAdvice.DescribeOrphans([new OrphanedUser("a", true)]));
        Assert.Contains("2 database users are orphaned",
            PostRestoreAdvice.DescribeOrphans([new OrphanedUser("a", true), new OrphanedUser("b", false)]));
    }

    [Fact]
    public void TheOverviewStatesWhatArrived()
    {
        var overview = new DatabaseOverview(150, "FULL", "sa");

        var line = overview.Describe("MyDb");

        Assert.Contains("compatibility level 150", line);
        Assert.Contains("recovery model FULL", line);
        Assert.Contains("sa", line);
    }

    // ── the execution surface ───────────────────────────────────────────────────

    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql) New()
    {
        var sql = new FakeSqlServerService();
        var vm = new RestoreExecutionViewModel(
            sql, new FakeOperationHistoryStore(), TestLogs.Temp(), new OperationCancellation());
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

    /// <summary>Success now ends with the job's remainder on screen, CHECKDB always first.</summary>
    [Fact]
    public async Task ASuccessfulRestoreOffersCheckDb()
    {
        var (vm, sql) = New();
        sql.DatabaseOverview = new DatabaseOverview(160, "FULL", "sa");

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.True(vm.HasPostRestoreActions);
        Assert.Contains(vm.PostRestoreActions, a => a.Sql.Contains("DBCC CHECKDB"));
        Assert.Contains("compatibility level 160", vm.PostRestoreMessage);
    }

    [Fact]
    public async Task OrphanedUsersAreFoundAndOfferedFixes()
    {
        var (vm, sql) = New();
        sql.OrphanedUsers = [new OrphanedUser("app_user", true), new OrphanedUser("ghost", false)];

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Contains("2 database users are orphaned", vm.PostRestoreMessage);
        Assert.Contains(vm.PostRestoreActions, a => a.Sql.Contains("ALTER USER [app_user]"));
        Assert.Contains(vm.PostRestoreActions, a => a.Title.Contains("ghost"));
    }

    /// <summary>A failed restore gets the recovery panel, not this one.</summary>
    [Fact]
    public async Task AFailedRestoreOffersNoPostRestoreActions()
    {
        var (vm, sql) = New();
        sql.ExecuteThrows = new InvalidOperationException("Msg 3201: Cannot open backup device");

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.False(vm.HasPostRestoreActions);
        Assert.Empty(vm.PostRestoreActions);
    }

    /// <summary>
    /// A restore that succeeded must never LOOK failed because the follow-up queries could not
    /// run - the panel still appears, with CHECKDB, and the failure goes to the log.
    /// </summary>
    [Fact]
    public async Task FollowUpQueryFailuresDoNotDressSuccessAsFailure()
    {
        var (vm, sql) = New();
        sql.OverviewThrows = new InvalidOperationException("VIEW DATABASE STATE denied");

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.True(vm.ExecutionSuccess);
        Assert.True(vm.HasPostRestoreActions);
        Assert.Contains(vm.PostRestoreActions, a => a.Sql.Contains("DBCC CHECKDB"));
        Assert.False(vm.HasError);
    }

    /// <summary>The next run starts clean - last run's advice is not this run's.</summary>
    [Fact]
    public async Task ANewRunClearsTheLastRunsAdvice()
    {
        var (vm, sql) = New();
        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));
        Assert.True(vm.HasPostRestoreActions);

        sql.ExecuteThrows = new InvalidOperationException("boom");
        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.False(vm.HasPostRestoreActions);
        Assert.Empty(vm.PostRestoreActions);
    }
}
