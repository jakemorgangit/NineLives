using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The credential question, asked before anything talks TO URL (#284). The restore screen
/// always asked it; the backup screen (which needs the credential on the SOURCE) and the copy
/// (which needs it on BOTH ends) did not - a missing or wrong-identity credential surfaced as
/// Msg 3201 after the arm-and-confirm, and on the copy after the source had been read at
/// full speed.
/// </summary>
public class BlobCredentialPreflightTests
{
    private static (BackupViewModel vm, FakeSqlServerService sql, FakeCredentialStore store,
        FakeRunNotifier notifier) BackupStage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        var container = new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        };
        store.Config.BlobContainers.Add(container);
        store.SaveSasToken(container, "sv=2024&sig=token");

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var notifier = new FakeRunNotifier();
        var vm = new BackupViewModel(store, sql, TestLogs.Temp(), notifier);
        vm.Server = vm.Servers.Single();
        vm.Container = vm.Containers.Single();
        return (vm, sql, store, notifier);
    }

    private static async Task RunBackup(BackupViewModel vm)
    {
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ABackupRefusesWhenTheCredentialExistsButCannotBeUsed()
    {
        var (vm, sql, _, notifier) = BackupStage();
        sql.Credential = new BlobCredentialStatus(
            BlobCredentialIdentity.Other, "SOME SERVICE PRINCIPAL");

        await RunBackup(vm);

        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(sql.CredentialIdentitiesWritten);
        var problem = notifier.Sent.Last();
        Assert.Equal(RunPhase.Problem, problem.Phase);
        Assert.Contains("deliberate change", problem.Detail);
    }

    [Fact]
    public async Task AMissingCredentialIsCreatedAndTheBackupProceeds()
    {
        var (vm, sql, _, _) = BackupStage();
        sql.Credential = BlobCredentialStatus.Missing;
        sql.CredentialWriteResult = CredentialChange.Created;

        await RunBackup(vm);

        Assert.Single(sql.ExecutedScripts);
        Assert.Contains(vm.Console, line => line.Contains("creating it"));
    }

    [Fact]
    public async Task TheCopyAsksBothEndsBeforeTheBackupHalf()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        var container = new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        };
        store.Config.BlobContainers.Add(container);
        store.SaveSasToken(container, "sv=2024&sig=token");

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var checks = 0;
        sql.OnCredentialCheck = (_, _) =>
        {
            checks++;
            return Task.FromResult(new BlobCredentialStatus(
                BlobCredentialIdentity.SharedAccessSignature, "SHARED ACCESS SIGNATURE"));
        };

        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV02");
        vm.Container = vm.Containers.Single();
        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.GenerateCommand.Execute(null);

        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, checks);
        Assert.Equal(CopyOutcome.Copied, vm.Outcome);
    }
}
