using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Every run CLOSES on the channel (#295). A notification story that starts and never ends
/// teaches people the channel is unreliable - and the single-database backup failure did
/// exactly that, while the copy's six-notification lifecycle had no pins at all.
/// </summary>
public class RunNotificationContractTests
{
    // ── the backup closes, single database included (#295 item 1) ───────────────

    private static (BackupViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier) Backup()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var notifier = new FakeRunNotifier();
        var vm = new BackupViewModel(store, sql, TestLogs.Temp(), notifier);
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];
        return (vm, sql, notifier);
    }

    [Fact]
    public async Task ASingleDatabaseBackupFailureClosesTheRunWithItsDuration()
    {
        var (vm, sql, notifier) = Backup();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        sql.FailOnExecuteNumber = 1;

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        // Started, the failure as it happened, and the CLOSE of the run - with a duration,
        // exactly as the multi-database path has always sent it.
        Assert.Equal(RunPhase.Started, notifier.Sent[0].Phase);
        Assert.Contains(notifier.Sent, n =>
            n.Phase == RunPhase.Problem && n.Detail != null &&
            n.Detail.Contains("did not complete") && n.Duration != null);
        Assert.DoesNotContain(notifier.Sent, n => n.Phase == RunPhase.Succeeded);
    }

    // ── the copy's lifecycle (#295 item 2) ──────────────────────────────────────

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier) Copy()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var notifier = new FakeRunNotifier();
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp(), notifier);
        vm.SourceServer = vm.Servers.First();
        vm.TargetServer = vm.Servers.Last();
        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = @"\\nas01\sql";
        return (vm, sql, notifier);
    }

    private static async Task<(CopyDatabaseViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier)>
        ReadyCopyAsync()
    {
        var (vm, sql, notifier) = Copy();
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Test";
        vm.GenerateCommand.Execute(null);
        return (vm, sql, notifier);
    }

    private static async Task RunAsync(CopyDatabaseViewModel vm)
    {
        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ACopyThatWorksSaysStartedThenSucceededWithItsDuration()
    {
        var (vm, _, notifier) = await ReadyCopyAsync();

        await RunAsync(vm);

        Assert.Equal(RunPhase.Started, notifier.Sent[0].Phase);
        var close = notifier.Sent[^1];
        Assert.Equal(RunPhase.Succeeded, close.Phase);
        Assert.Equal("Copy", close.Operation);
        Assert.NotNull(close.Duration);
    }

    [Fact]
    public async Task ABackupHalfFailureClosesTheCopyWithTheHalfNamed()
    {
        var (vm, sql, notifier) = await ReadyCopyAsync();
        sql.FailOnExecuteNumber = 1;

        await RunAsync(vm);

        var close = notifier.Sent[^1];
        Assert.Equal(RunPhase.Problem, close.Phase);
        Assert.Contains("backup half failed", close.Detail);
        Assert.NotNull(close.Duration);
        Assert.DoesNotContain(notifier.Sent, n => n.Phase == RunPhase.Succeeded);
    }

    [Fact]
    public async Task ARestoreHalfFailureClosesTheCopyWithTheHalfNamed()
    {
        var (vm, sql, notifier) = await ReadyCopyAsync();
        sql.FailOnExecuteNumber = 2;

        await RunAsync(vm);

        var close = notifier.Sent[^1];
        Assert.Equal(RunPhase.Problem, close.Phase);
        Assert.Contains("restore half failed", close.Detail);
        Assert.NotNull(close.Duration);
    }

    /// <summary>Refused before anything happened - the channel hears the refusal, not a start.</summary>
    [Fact]
    public async Task ARefusedCopyNeverClaimsToHaveStarted()
    {
        var (vm, _, notifier) = await ReadyCopyAsync();
        vm.TargetServer = vm.SourceServer;   // the same-instance refusal (#282)

        await RunAsync(vm);

        Assert.DoesNotContain(notifier.Sent, n => n.Phase == RunPhase.Started);
        Assert.DoesNotContain(notifier.Sent, n => n.Phase == RunPhase.Succeeded);
    }
}
