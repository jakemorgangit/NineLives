using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Backing up more than one database in one run (#208).
///
/// "Take a copy-only of everything before we patch" is a completely ordinary request, and the
/// script for it is N BACKUP statements sharing one set of options. The run is
/// database-at-a-time, deliberately: a failure on the sixth database names the sixth database and
/// the rest still run - the whole point of backing up everything is that everything gets backed
/// up.
/// </summary>
public class MultiDatabaseBackupTests
{
    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    private static async Task<(BackupViewModel vm, FakeSqlServerService sql)> NewAsync()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["Archive", "Payroll", "Sales"] };

        var vm = new BackupViewModel(store, sql, TestLogs.Temp());
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];
        await vm.LoadDatabasesCommand.ExecuteAsync(null);

        return (vm, sql);
    }

    // ── generation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PickingSeveralGeneratesOneStatementEach()
    {
        var (vm, _) = await NewAsync();
        vm.MultiSelect = true;
        vm.DatabasePicks[0].IsPicked = true;
        vm.DatabasePicks[2].IsPicked = true;

        vm.GenerateCommand.Execute(null);

        Assert.Contains("BACKUP DATABASE [Archive]", vm.GeneratedScript);
        Assert.Contains("BACKUP DATABASE [Sales]", vm.GeneratedScript);
        Assert.DoesNotContain("[Payroll]", vm.GeneratedScript);

        // Each database writes its own files - the destinations are per-database, not shared.
        Assert.Contains(vm.Destinations, d => d.Contains("/Archive/"));
        Assert.Contains(vm.Destinations, d => d.Contains("/Sales/"));
    }

    [Fact]
    public async Task PickAllTicksEverythingTheInstanceListed()
    {
        var (vm, _) = await NewAsync();
        vm.MultiSelect = true;

        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);

        Assert.Equal(3, vm.DatabasePicks.Count(p => p.IsPicked));
        Assert.Contains("[Archive]", vm.GeneratedScript);
        Assert.Contains("[Payroll]", vm.GeneratedScript);
        Assert.Contains("[Sales]", vm.GeneratedScript);
    }

    [Fact]
    public async Task NothingPickedGeneratesNothing()
    {
        var (vm, _) = await NewAsync();
        vm.MultiSelect = true;

        Assert.False(vm.CanGenerate);
    }

    /// <summary>The single-database path is untouched: same screen, same behaviour as before.</summary>
    [Fact]
    public async Task SingleModeStillWorksAlone()
    {
        var (vm, _) = await NewAsync();
        vm.SelectedDatabase = "Sales";

        vm.GenerateCommand.Execute(null);

        Assert.Contains("BACKUP DATABASE [Sales]", vm.GeneratedScript);
        Assert.DoesNotContain("[Archive]", vm.GeneratedScript);
    }

    // ── the run ─────────────────────────────────────────────────────────────────

    private static async Task RunAsync(BackupViewModel vm)
    {
        await vm.ExecuteCommand.ExecuteAsync(null); // arm
        await vm.ExecuteCommand.ExecuteAsync(null); // run
    }

    [Fact]
    public async Task EachDatabaseRunsAsItsOwnStatement()
    {
        var (vm, sql) = await NewAsync();
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);

        await RunAsync(vm);

        Assert.Equal(3, sql.ExecutedScripts.Count);
        Assert.All(sql.ExecutedScripts, s => Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(s, "BACKUP DATABASE")));
    }

    /// <summary>
    /// The one this exists for: the sixth failing names the sixth, and the rest still run.
    /// </summary>
    [Fact]
    public async Task AFailureIsNamedAndTheRestStillRun()
    {
        var (vm, sql) = await NewAsync();
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);

        sql.FailOnExecuteNumber = 2; // Payroll, the second of three

        await RunAsync(vm);

        Assert.Equal(3, sql.ExecutedScripts.Count);
        Assert.True(vm.HasError);
        Assert.Contains("Payroll", vm.StatusMessage);
        Assert.Contains("1 of 3", vm.StatusMessage);
    }

    /// <summary>Only what succeeded is offered for verification - a failed backup has no files worth proving.</summary>
    [Fact]
    public async Task TheVerifyOfferCoversOnlyWhatSucceeded()
    {
        var (vm, sql) = await NewAsync();
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);
        sql.FailOnExecuteNumber = 2;

        await RunAsync(vm);
        await vm.VerifyLastBackupCommand.ExecuteAsync(null);

        Assert.True(vm.CanVerifyLastBackup);
        Assert.Contains(sql.VerifiedUrls, u => u.Contains("/Archive/"));
        Assert.Contains(sql.VerifiedUrls, u => u.Contains("/Sales/"));
        Assert.DoesNotContain(sql.VerifiedUrls, u => u.Contains("/Payroll/"));
    }
}
