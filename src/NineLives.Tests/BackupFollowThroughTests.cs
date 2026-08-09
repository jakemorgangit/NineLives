using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The end of a backup stops being a dead end (#207).
///
/// After "Backup complete", the natural next move is proving it - a backup nobody has verified is
/// a hope, not a backup - and the other half of the issue is handing a backup over as an Agent
/// job at all, which the restore screen could do (#186) and the screen for the thing people
/// actually schedule could not.
/// </summary>
public class BackupFollowThroughTests
{
    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    private static (BackupViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };

        var vm = new BackupViewModel(store, sql, TestLogs.Temp());
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];

        return (vm, sql);
    }

    private static async Task<(BackupViewModel vm, FakeSqlServerService sql)> RanAsync()
    {
        var (vm, sql) = New();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);

        // Armed then run - the button is a two-press control.
        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        return (vm, sql);
    }

    // ── verify what was written ─────────────────────────────────────────────────

    /// <summary>A successful run leaves the verify offer behind.</summary>
    [Fact]
    public async Task ASuccessfulBackupOffersToVerifyIt()
    {
        var (vm, _) = await RanAsync();

        Assert.True(vm.CanVerifyLastBackup);
    }

    /// <summary>
    /// The verify reads the devices the statement wrote - captured, not re-derived - so changing
    /// options afterwards cannot point it at files that were never written.
    /// </summary>
    [Fact]
    public async Task TheVerifyReadsExactlyWhatWasWritten()
    {
        var (vm, sql) = await RanAsync();
        var written = vm.Destinations.ToList();

        vm.Stripes = 4; // the next run would write elsewhere

        await vm.VerifyLastBackupCommand.ExecuteAsync(null);

        Assert.Equal(written, sql.VerifiedUrls);
    }

    [Fact]
    public async Task AFailedVerifyIsLoudAndDistrustsTheBackup()
    {
        var (vm, sql) = await RanAsync();
        sql.VerifyResult = new VerifyOnlyResult(false, "Msg 3013: VERIFY DATABASE is terminating abnormally.");

        await vm.VerifyLastBackupCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("NOT", vm.StatusMessage);
    }

    /// <summary>A failed backup leaves nothing to verify - the offer is for real files only.</summary>
    [Fact]
    public async Task AFailedBackupOffersNothing()
    {
        var (vm, sql) = New();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        sql.ExecuteThrows = new InvalidOperationException("Msg 3201");

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.False(vm.CanVerifyLastBackup);
    }

    // ── the Agent job ───────────────────────────────────────────────────────────

    /// <summary>Pro shows the handover; Basic and Standard do not (#176's gate, same as restore).</summary>
    [Fact]
    public void TheAgentJobFollowsTheMode()
    {
        var (vm, _) = New();

        vm.Mode = AppMode.Pro;
        Assert.True(vm.ShowAgentJob);

        vm.Mode = AppMode.Standard;
        Assert.False(vm.ShowAgentJob);
    }

    /// <summary>The shell keeps every screen's mode current, this one included.</summary>
    [Fact]
    public void TheShellPropagatesTheModeToTheBackupScreen()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;

        var main = new MainViewModel(store);
        main.Mode = AppMode.Standard;

        Assert.Equal(AppMode.Standard, main.Backup.Mode);
    }
}
