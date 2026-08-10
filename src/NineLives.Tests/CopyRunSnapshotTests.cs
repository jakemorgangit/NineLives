using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The copy runs the scripts it SHOWED, not the ones on screen later (#280). The view stays
/// editable while the halves run and Generate stamps a fresh timestamp into the destinations -
/// so one keystroke mid-backup used to regenerate the restore half and point it at a file
/// that was never written. The run snapshots its three inputs at the moment of consent, the
/// same medicine the restore screen's immutable run record takes.
/// </summary>
public class CopyRunSnapshotTests
{
    [Fact]
    public async Task AnEditDuringTheBackupHalfCannotChangeWhatTheRestoreHalfRuns()
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
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV02");
        vm.Container = vm.Containers.Single();
        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.GenerateCommand.Execute(null);

        var shownRestore = vm.RestoreScript;
        Assert.Contains("MyDb_Copy", shownRestore);

        // The keystroke lands while the BACKUP half is executing - the regeneration it
        // triggers renames the target and stamps new destination filenames.
        sql.OnExecute = n =>
        {
            if (n == 1) vm.TargetDatabaseName = "MyDb_Edited";
        };

        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, sql.ExecutedScripts.Count);
        // The restore half ran the statements that were on screen at consent - byte for byte.
        Assert.Equal(shownRestore, sql.ExecutedScripts[1]);
        Assert.DoesNotContain("MyDb_Edited", sql.ExecutedScripts[1]);
    }
}
