using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The browser must not mix two sources' answers (#286).
///
/// One screen, one question - "what do I actually have?" - and every defect here was a way of
/// answering it with two sources' truths at once: a stale listing landing under a new source's
/// name, a round trip emptying the screen, a cancelled reload leaving old rows without their
/// restore button, a renamed container losing its selection.
/// </summary>
public class BrowserStateDisciplineTests
{
    private static BlobContainerConfig Container(string id = "c1", string name = "backups", string? tz = null) => new()
    {
        Id = id,
        Name = name,
        ContainerUrl = $"https://acct.blob.core.windows.net/{name}",
        BackupServerTimeZoneId = tz
    };

    private static BackupFileInfo File(string server, string db) => new()
    {
        BlobName = $"FULL/{server}/{db}/20260801_220000.bak",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/{server}/{db}/20260801_220000.bak",
        Type = BackupType.Full,
        InferredServerName = server,
        InferredDatabaseName = db,
        SizeBytes = 1024,
        LastModified = new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero)
    };

    private static FakeCredentialStore Store(params BlobContainerConfig[] containers)
    {
        var store = new FakeCredentialStore();
        foreach (var c in containers) store.Config.BlobContainers.Add(c);
        return store;
    }

    private static BlobBrowserViewModel New(FakeBlobStorageService blob, FakeCredentialStore store) =>
        new(blob, new FakeSqlServerService(), store);

    // ── a stale result must not land (#286 item 1) ──────────────────────────────

    [Fact]
    public async Task ChangingTheContainerMidListingDropsTheStaleResult()
    {
        var blob = new FakeBlobStorageService
        {
            BeforeListReturns = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales")];
        var store = Store(Container(), Container("c2", "archive"));
        var vm = New(blob, store);
        Assert.Equal("c1", vm.SelectedContainer?.Id);

        var load = vm.LoadBackupsCommand.ExecuteAsync(null);

        // The world moves on while container A's listing is still in flight.
        vm.SelectedContainer = vm.Containers[1];

        blob.BeforeListReturns.SetResult(true);
        await load;

        Assert.Empty(vm.FilteredFiles);
        Assert.False(vm.HasFiles);
        Assert.Empty(vm.DiscoveredServers);
    }

    /// <summary>
    /// The grouping runs with the container that was ASKED. Re-reading the selection after the
    /// await handed container A's files to container B's time zone - every filename-less
    /// timestamp then wrong by the zone offset.
    /// </summary>
    [Fact]
    public async Task TheGroupingIsHandedTheAskedContainersTimeZone()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales")];
        var store = Store(Container(tz: "GMT Standard Time"));
        var vm = New(blob, store);

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal("GMT Standard Time", blob.LastGroupTimeZoneId);
    }

    // ── a round trip keeps the listing (#286 item 2) ────────────────────────────

    [Fact]
    public async Task ARoundTripAwayAndBackKeepsTheListing()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales"), File("SRV01", "Payroll")];
        var store = Store(Container());
        store.Config.Servers.Add(new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        var vm = New(blob, store);
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.HasFiles);
        var rowsBefore = vm.FilteredFiles.Select(f => f.BlobName).ToList();
        Assert.NotEmpty(rowsBefore);

        // What navigating away and back does: the shell refreshes the source lists.
        vm.RefreshContainers();

        Assert.True(vm.HasFiles);
        Assert.Equal(rowsBefore, vm.FilteredFiles.Select(f => f.BlobName).ToList());
    }

    [Fact]
    public async Task F5ReloadsTheListingInsteadOfEmptyingIt()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales")];
        var vm = New(blob, Store(Container()));
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        var listingsBefore = blob.ListCalls;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(listingsBefore + 1, blob.ListCalls);
        Assert.True(vm.HasFiles);
        Assert.NotEmpty(vm.FilteredFiles);
    }

    // ── a cancelled reload keeps the previous answer whole (#286 item 3) ────────

    [Fact]
    public async Task ACancelledReloadKeepsThePreviousAnswerWhole()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales")];
        var vm = New(blob, Store(Container()));
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.HasFiles);

        // The reload is held in flight and cancelled - as the Cancel button does.
        blob.BeforeListReturns = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.CancelLoadCommand.Execute(null);
        blob.BeforeListReturns.SetResult(true);
        await reload;

        // The rows from the completed listing are still the truth, and the screen still says so.
        Assert.True(vm.HasFiles);
        Assert.NotEmpty(vm.FilteredFiles);
    }

    // ── selection by identity (#286 item 4) ─────────────────────────────────────

    [Fact]
    public async Task ARenamedContainerKeepsItsSelectionAndItsListing()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File("SRV01", "Sales")];
        var store = Store(Container());
        var vm = New(blob, store);
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // Renamed on the Blob Storage screen; same container, same Id.
        store.Config.BlobContainers[0].Name = "backups-eu";
        vm.RefreshContainers();

        Assert.Equal("backups-eu", vm.SelectedContainer?.Name);
        Assert.True(vm.HasFiles);
    }

    // ── the two media agree what the count means (#286 item 5) ──────────────────

    [Fact]
    public async Task TheHistoryCountReportsTheInstancesTotalDatabases()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        var sql = new FakeSqlServerService
        {
            BackupHistory =
            [
                new BackupHistoryEntry
                {
                    DatabaseName = "Sales", ServerName = "SRV01", Type = BackupType.Full,
                    FinishedAt = new DateTime(2026, 8, 7, 22, 0, 0), Files = [@"\\nas01\sql\Sales_FULL.bak"]
                },
                new BackupHistoryEntry
                {
                    DatabaseName = "Payroll", ServerName = "SRV02", Type = BackupType.Full,
                    FinishedAt = new DateTime(2026, 8, 7, 22, 0, 0), Files = [@"\\nas01\sql\Payroll_FULL.bak"]
                }
            ]
        };
        var vm = new BlobBrowserViewModel(new FakeBlobStorageService(), sql, store);
        vm.SelectedMedium = BackupMedium.SharedPath;

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // Two databases in the history; the auto-picked server filter shows one. The status
        // reports the total, as the blob path always has.
        Assert.Contains("across 2 database(s)", vm.StatusMessage);
    }
}
