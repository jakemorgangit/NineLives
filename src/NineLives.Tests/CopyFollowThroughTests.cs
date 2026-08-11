using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Copy failures get the restore screen's follow-through (#283): a pressed Stop between the
/// halves is reported as a cancellation rather than a share-permission failure, a failed
/// restore half describes what state the target is in and the statements that get it out,
/// and a copy that WORKED runs the orphaned-user scan - a copy to a different server being
/// the canonical orphaned-login scenario. These are also the copy's first notification-path
/// pins; it fires six distinct notifications and had zero coverage.
/// </summary>
public class CopyFollowThroughTests
{
    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier)
        Stage(BackupMedium medium = BackupMedium.AzureBlob)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var notifier = new FakeRunNotifier();
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp(), notifier);
        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV02");
        if (medium == BackupMedium.SharedPath)
        {
            vm.Medium = BackupMedium.SharedPath;
            vm.SharedPathRoot = @"\\backuphost\sql";
        }
        else
        {
            vm.Container = vm.Containers.Single();
        }

        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.GenerateCommand.Execute(null);
        return (vm, sql, notifier);
    }

    private static async Task Run(CopyDatabaseViewModel vm)
    {
        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AStopBetweenTheHalvesIsACancellationNotASharePermissionStory()
    {
        var (vm, sql, notifier) = Stage(BackupMedium.SharedPath);

        // Stop lands after the backup half: the cancelled token then reaches the
        // between-halves readability check.
        sql.OnExecute = n =>
        {
            if (n == 1) vm.CancelCommand.Execute(null);
        };

        await Run(vm);

        var problem = notifier.Sent.Last(n => n.Phase == RunPhase.Problem);
        Assert.Contains("Cancelled between the halves", problem.Detail);
        Assert.DoesNotContain("share permission", problem.Detail);
        Assert.Contains(vm.Console, line => line.Contains("Cancelled before the restore half"));
    }

    [Fact]
    public async Task AFailedRestoreHalfDescribesTheTargetStateAndTheWayOut()
    {
        var (vm, sql, _) = Stage();
        sql.FailOnExecuteNumber = 2;
        sql.RecoveryState = new DatabaseRecoveryState(
            Exists: true, StateDescription: "RESTORING", UserAccessDescription: "MULTI_USER");

        await Run(vm);

        Assert.Equal(CopyOutcome.BackupTakenRestoreFailed, vm.Outcome);
        Assert.Contains(vm.Console, line => line.Contains("RESTORING", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.Console, line => line.Contains("RESTORE DATABASE", StringComparison.OrdinalIgnoreCase)
                                            || line.Contains("RECOVERY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASuccessfulCopyRunsTheOrphanScan()
    {
        var (vm, sql, notifier) = Stage();
        sql.OrphanedUsers = [new OrphanedUser("app_user", true)];

        await Run(vm);

        Assert.Equal(CopyOutcome.Copied, vm.Outcome);
        Assert.Contains(vm.Console, line => line.Contains("app_user"));
        Assert.Equal(RunPhase.Succeeded, notifier.Sent.Last().Phase);
    }
}
