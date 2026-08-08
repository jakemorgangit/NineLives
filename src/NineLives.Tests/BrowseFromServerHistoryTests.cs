using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Browsing what a server has recorded backing up (#197).
///
/// Backing up and restoring have both taken either medium since #165, and Browse Backups did not
/// come along - so the one screen whose entire purpose is LOOKING was the one that could not look
/// at half of what the app writes.
///
/// A share is not browsed by walking a directory: a folder of .bak files says nothing about which
/// database each belongs to, what type it is, or which full a differential was taken against, and
/// inferring that from filenames is the whole reason #130 exists. The instance that TOOK the
/// backups recorded all of it, so that is what is read.
/// </summary>
public class BrowseFromServerHistoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static ServerConnection Server(string name = "SRV01") =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static BackupHistoryEntry Entry(string database, BackupType type, string file) => new()
    {
        DatabaseName = database,
        ServerName = "SRV01",
        Type = type,
        FinishedAt = T0,
        Files = [file]
    };

    /// <summary>A store holding one container and one server, so either source can be chosen.</summary>
    private static FakeCredentialStore Store()
    {
        var store = new FakeCredentialStore();

        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        store.Config.Servers.Add(Server());

        return store;
    }

    private static (BlobBrowserViewModel vm, FakeSqlServerService sql) New(FakeCredentialStore? store = null)
    {
        store ??= Store();

        var sql = new FakeSqlServerService
        {
            BackupHistory =
            [
                Entry("Sales", BackupType.Full, @"\\nas01\sql\Sales_FULL.bak"),
                Entry("Sales", BackupType.TransactionLog, @"\\nas01\sql\Sales_LOG.trn"),
                Entry("Payroll", BackupType.Full, @"\\nas01\sql\Payroll_FULL.bak")
            ]
        };

        return (new BlobBrowserViewModel(new FakeBlobStorageService(), sql, store), sql);
    }

    // ── the second source ───────────────────────────────────────────────────────

    /// <summary>Blob by default: it is what this screen has always done.</summary>
    [Fact]
    public void TheScreenStartsOnBlob()
    {
        var (vm, _) = New();

        Assert.Equal(BackupMedium.AzureBlob, vm.SelectedMedium);
        Assert.True(vm.MediumIsBlob);
        Assert.False(vm.MediumIsSharedPath);
    }

    /// <summary>The servers come from the same config the rest of the app reads.</summary>
    [Fact]
    public void TheConfiguredServersAreOffered()
    {
        var (vm, _) = New();

        Assert.Single(vm.Servers);
        Assert.Equal("SRV01", vm.SourceServer?.Name);
    }

    [Fact]
    public async Task AServersHistoryIsReadAndShown()
    {
        var (vm, _) = New();
        vm.SelectedMedium = BackupMedium.SharedPath;

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.HasFiles);
        Assert.Contains("Sales", vm.DiscoveredDatabases);
        Assert.Contains("Payroll", vm.DiscoveredDatabases);
    }

    /// <summary>
    /// The paths shown are the ones the source actually wrote. No mapping is applied, because
    /// nothing is being restored here - there is no target to reach the files by another route.
    /// </summary>
    [Fact]
    public async Task ThePathsShownAreTheOnesTheSourceWrote()
    {
        var (vm, _) = New();
        vm.SelectedMedium = BackupMedium.SharedPath;
        vm.SelectedDatabase = null;

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // LocalPath, not BlobUrl: BlobUrl is deliberately left empty for a file on disk, because
        // filling one in with a path would be the kind of quiet lie that ends in a restore aimed
        // at the wrong device.
        Assert.Contains(vm.FilteredFiles, f => f.IsOnDisk
                                            && f.RestoreDevice.StartsWith(@"\\nas01\sql\", StringComparison.OrdinalIgnoreCase));
    }

    // ── what is on screen belongs to what is named above it ─────────────────────

    /// <summary>
    /// Switching source empties the list. A container's files sitting under a server's name is
    /// worse than an empty screen, because it reads as an answer.
    /// </summary>
    [Fact]
    public async Task ChangingTheMediumEmptiesWhatWasLoaded()
    {
        var (vm, _) = New();
        vm.SelectedMedium = BackupMedium.SharedPath;
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.HasFiles);

        vm.SelectedMedium = BackupMedium.AzureBlob;

        Assert.False(vm.HasFiles);
        Assert.Empty(vm.FilteredFiles);
        Assert.Empty(vm.DiscoveredDatabases);
    }

    [Fact]
    public async Task ChangingTheServerEmptiesWhatWasLoaded()
    {
        var store = Store();
        store.Config.Servers.Add(Server("SRV02"));

        var (vm, _) = New(store);
        vm.SelectedMedium = BackupMedium.SharedPath;
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.HasFiles);

        vm.SourceServer = vm.Servers.Single(s => s.Name == "SRV02");

        Assert.False(vm.HasFiles);
        Assert.Empty(vm.FilteredFiles);
    }

    // ── nothing chosen ──────────────────────────────────────────────────────────

    /// <summary>Says which thing is missing, rather than complaining about a container.</summary>
    [Fact]
    public async Task WithNoServerChosenItAsksForOne()
    {
        var store = new FakeCredentialStore();
        var (vm, _) = New(store);

        vm.SelectedMedium = BackupMedium.SharedPath;
        Assert.Null(vm.SourceServer);

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("server", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An instance with no history says so rather than looking like a failure.</summary>
    [Fact]
    public async Task AServerWithNoHistorySaysSo()
    {
        var (vm, sql) = New();
        sql.BackupHistory.Clear();

        vm.SelectedMedium = BackupMedium.SharedPath;
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.False(vm.HasFiles);
        Assert.Contains("no backup history", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── the assumption this screen rests on ─────────────────────────────────────

    /// <summary>
    /// The medium choice is offered here without a mode gate, because every mode that offers this
    /// screen at all also offers the shared path. That is true today and is not a law - so it is
    /// asserted, rather than left as a coincidence for somebody to discover by finding a radio
    /// button that selects a medium the mode does not otherwise allow.
    /// </summary>
    [Theory]
    [InlineData(AppMode.Basic)]
    [InlineData(AppMode.Standard)]
    [InlineData(AppMode.Pro)]
    public void AnyModeThatCanBrowseCanAlsoUseAServersHistory(AppMode mode)
    {
        if (AppModeCapabilities.CanBrowseBackups(mode))
            Assert.True(AppModeCapabilities.CanUseSharedPath(mode));
    }
}
