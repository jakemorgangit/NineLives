using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Audit button becomes live when there is something to audit (#130).
///
/// Reported from a real run: the panel said "Reading 98 backup header(s) - about 3 minute(s)" and
/// the button underneath it was dead.
///
/// The subtlety, and the reason a first attempt at these tests passed against the broken code:
/// asking <c>CanExecute</c> directly always gives the right answer, because a RelayCommand
/// evaluates its predicate on the spot. A BUTTON does not ask. It caches what it was last told and
/// re-asks only when <c>CanExecuteChanged</c> is raised - so what was broken was the notification,
/// not the answer, and only a test that watches the notification can see it.
///
/// The estimate beside the button is a plain property, so it re-read itself and updated to 98 while
/// the button stayed disabled. A stale CanExecute is a silent failure: the control looks
/// deliberately disabled, so it reads as "not allowed" rather than "broken", and there is nothing
/// to click to find out otherwise.
/// </summary>
public class AuditButtonComesAliveTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static RestoreViewModel Loaded()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = "https://acct.blob.core.windows.net/backups" });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    Type = BackupType.Full,
                    InferredDatabaseName = "MyDb",
                    InferredServerName = "SRV01",
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        return new RestoreViewModel(
            blob, new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(), new FakeRestoreHistoryStore(), TestAuditStores.Temp());
    }

    private static void Connect(RestoreViewModel vm)
    {
        vm.ConnectedServer = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
        vm.IsConnectedToServer = true;
    }

    /// <summary>
    /// The exact sequence that was reported: load, connect, then pick a database from a list that
    /// was already loaded - so neither the load nor the connection is what changes.
    /// </summary>
    [Fact]
    public async Task PickingADatabaseTellsTheButtonToAskAgain()
    {
        var vm = Loaded();
        Connect(vm);

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var told = 0;
        vm.AuditDatabaseCommand.CanExecuteChanged += (_, _) => told++;

        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.NotEmpty(vm.Inventory.WorkingSet);
        Assert.True(vm.CanAuditDatabase);

        // The part that was missing. Without it the button keeps the answer it was given while the
        // working set was still empty, and stays disabled next to an estimate for 98 headers.
        Assert.True(told > 0, "the button was never told to re-ask whether it can run");
    }

    /// <summary>The same for the offer to identify unplaceable files, which is gated the same way.</summary>
    [Fact]
    public async Task PickingADatabaseTellsTheIdentifyButtonToAskAgainToo()
    {
        var vm = Loaded();
        Connect(vm);

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var told = 0;
        vm.IdentifyUnclassifiedCommand.CanExecuteChanged += (_, _) => told++;

        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.True(told > 0, "the identify button was never told to re-ask");
    }

    /// <summary>Losing the selection has to withdraw the offer, not just fail to run.</summary>
    [Fact]
    public async Task DeselectingTheDatabaseTellsItToAskAgainAndTheAnswerIsNo()
    {
        var vm = Loaded();
        Connect(vm);

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        var told = 0;
        vm.AuditDatabaseCommand.CanExecuteChanged += (_, _) => told++;

        vm.Inventory.SelectedDatabaseName = null;

        Assert.Empty(vm.Inventory.WorkingSet);
        Assert.False(vm.CanAuditDatabase);
        Assert.True(told > 0, "the button was never told the offer had gone");
    }

    /// <summary>
    /// Connecting afterwards is at least as common an order, and was already handled - kept so it
    /// stays handled.
    /// </summary>
    [Fact]
    public async Task ConnectingAfterwardsAlsoTellsItToAskAgain()
    {
        var vm = Loaded();

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.False(vm.CanAuditDatabase);

        var told = 0;
        vm.AuditDatabaseCommand.CanExecuteChanged += (_, _) => told++;

        Connect(vm);

        Assert.True(vm.CanAuditDatabase);
        Assert.True(told > 0, "connecting never told the button to re-ask");
    }

    /// <summary>
    /// Disconnecting withdraws it, because it is the SERVER that reads the headers - and says so,
    /// rather than leaving a dead button with no explanation, which is what this whole class is
    /// about.
    /// </summary>
    [Fact]
    public async Task DisconnectingWithdrawsTheOfferAndSaysWhy()
    {
        var vm = Loaded();
        Connect(vm);

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.Equal(string.Empty, vm.AuditBlockedReason);

        vm.IsConnectedToServer = false;

        Assert.False(vm.CanAuditDatabase);
        Assert.Contains("it is the server that reads the headers", vm.AuditBlockedReason);
    }

    /// <summary>
    /// The estimate and the button read the same working set, and the whole defect was that only
    /// one of them noticed it had changed.
    /// </summary>
    [Fact]
    public async Task TheEstimateAndTheButtonAgreeAboutWhetherThereIsAnythingToAudit()
    {
        var vm = Loaded();
        Connect(vm);

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.NotEqual(string.Empty, vm.Inventory.AuditEstimate);
        Assert.True(vm.CanAuditDatabase);

        vm.Inventory.SelectedDatabaseName = null;

        Assert.Equal(string.Empty, vm.Inventory.AuditEstimate);
        Assert.False(vm.CanAuditDatabase);
    }
}
