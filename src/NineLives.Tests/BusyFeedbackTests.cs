using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Feedback that the app is doing something (#128).
///
/// Check chain and Verify backups only changed their own label, and verify can run as long as the
/// restore - so on a large chain the app looked hung. Once one has passed for the selected chain
/// there is also no reason to run it again, and re-running VERIFYONLY by accident is expensive.
/// </summary>
public class ChainCheckStateTests
{
    private readonly FakeBlobStorageService _blob = new();
    private readonly FakeSqlServerService _sql = new();
    private readonly FakeCredentialStore _store = new();

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private RestoreViewModel NewViewModel() => new(
        _blob, _sql, new BackupChainBuilder(), new RestoreScriptGenerator(), _store,
        new OperationLog(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ninelives-vm-tests", Guid.NewGuid().ToString("n"))),
        new FakeRestoreHistoryStore());

    private static BackupFileInfo File(string blobName, BackupType type, DateTime stamp) => new()
    {
        BlobName = blobName,
        BlobUrl = $"https://mystorageaccount.blob.core.windows.net/backups/{blobName}",
        Type = type,
        InferredServerName = "SRV01",
        InferredDatabaseName = "MyDb",
        SizeBytes = 1000,
        LastModified = new DateTimeOffset(stamp, TimeSpan.Zero)
    };

    private async Task<RestoreViewModel> Loaded()
    {
        _blob.Files =
        [
            File("FULL/SRV01/MyDb/20260110_220000.bak", BackupType.Full, T0),
            File("LOG/SRV01/MyDb/20260110_230000.trn", BackupType.TransactionLog, T0.AddHours(1)),
        ];

        var vm = NewViewModel();
        vm.SelectedContainer = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsConnectedToServer = true;
        vm.ConnectedServer = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        return vm;
    }

    [Fact]
    public async Task AChainThatPassedItsCheckDoesNotOfferToCheckItAgain()
    {
        var vm = await Loaded();

        Assert.False(vm.ChainCheckPassed);
        Assert.True(vm.ValidateChainCommand.CanExecute(null));

        // The outcome the real check produces when the headers line up.
        vm.ChainLsnVerified = true;
        vm.HasChainIssues = false;

        Assert.True(vm.ChainCheckPassed);
        Assert.False(vm.ValidateChainCommand.CanExecute(null));
    }

    [Fact]
    public async Task AChainWithProblemsStaysCheckable()
    {
        var vm = await Loaded();

        // A check that found something is worth re-running once the problem is dealt with.
        vm.ChainLsnVerified = true;
        vm.HasChainIssues = true;

        Assert.False(vm.ChainCheckPassed);
        Assert.True(vm.ValidateChainCommand.CanExecute(null));
    }

    /// <summary>
    /// Running the real command against headers it cannot read leaves the button enabled - the
    /// check did not pass, so offering it again is right.
    /// </summary>
    [Fact]
    public async Task RunningTheCheckAndFindingProblemsLeavesItEnabled()
    {
        var vm = await Loaded();

        await vm.ValidateChainCommand.ExecuteAsync(null);

        Assert.True(vm.ChainLsnVerified);
        Assert.True(vm.HasChainIssues);
        Assert.False(vm.ChainCheckPassed);
        Assert.True(vm.ValidateChainCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerifyingTwiceOnTheSameChainIsNotOffered()
    {
        var vm = await Loaded();

        Assert.True(vm.VerifyChainCommand.CanExecute(null));

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(vm.VerifyPassed);
        Assert.False(vm.VerifyChainCommand.CanExecute(null));
    }

    [Fact]
    public async Task AVerifyThatFoundMissingDirectoriesHasNotPassed()
    {
        var vm = await Loaded();
        _sql.VerifyResult = new VerifyOnlyResult(true, "Directory lookup failed.", TargetPathsMissing: true);

        await vm.VerifyChainCommand.ExecuteAsync(null);

        // The backups read back, but the restore cannot land - not a pass.
        Assert.False(vm.VerifyPassed);
        Assert.True(vm.VerifyChainCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingTheSelectedPointMakesBothCheckableAgain()
    {
        var vm = await Loaded();
        await vm.VerifyChainCommand.ExecuteAsync(null);
        vm.ChainLsnVerified = true;
        vm.HasChainIssues = false;

        Assert.True(vm.ChainCheckPassed);
        Assert.True(vm.VerifyPassed);

        // The result belongs to the chain that was checked.
        vm.Timeline.SelectedPoint = vm.Timeline.Points.First();

        Assert.False(vm.ChainCheckPassed);
        Assert.False(vm.VerifyPassed);
        Assert.True(vm.ValidateChainCommand.CanExecute(null));
        Assert.True(vm.VerifyChainCommand.CanExecute(null));
    }

    // ── what the screen says it is doing ────────────────────────────────────────

    [Fact]
    public async Task TheScreenNamesWhatItIsDoing()
    {
        var vm = await Loaded();

        Assert.Empty(vm.BusyDescription);
        Assert.False(vm.IsBusyWithAnything);

        vm.IsValidatingChain = true;
        Assert.Equal("Checking the chain...", vm.BusyDescription);

        vm.IsValidatingChain = false;
        vm.IsVerifyingChain = true;
        Assert.Equal("Verifying backups...", vm.BusyDescription);

        vm.IsVerifyingChain = false;
        vm.IsExecuting = true;
        Assert.Contains("MyDb_Restored", vm.BusyDescription);

        vm.IsExecuting = false;
        Assert.Empty(vm.BusyDescription);
    }

    [Fact]
    public async Task ARestoreOutranksTheOtherWork()
    {
        var vm = await Loaded();

        vm.IsBusy = true;
        vm.IsExecuting = true;

        // The one that matters is the one writing to a database.
        Assert.Contains("Restoring", vm.BusyDescription);
    }
}

/// <summary>
/// The window says what the app is doing, wherever the user has scrolled to.
/// </summary>
public class GlobalBusyTests
{
    [Fact]
    public void TheWindowIsIdleUntilAChildIsBusy()
    {
        var vm = new MainViewModel(new FakeCredentialStore());

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.BusyText);
    }

    [Fact]
    public void AChildStartingWorkReachesTheWindow()
    {
        var vm = new MainViewModel(new FakeCredentialStore());

        vm.BlobBrowser.IsBusy = true;
        Assert.True(vm.IsBusy);
        Assert.Equal("Listing the container...", vm.BusyText);

        vm.BlobBrowser.IsBusy = false;
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.BusyText);
    }

    [Fact]
    public void TheRestoreScreenNamesItsOwnWork()
    {
        var vm = new MainViewModel(new FakeCredentialStore());

        vm.Restore.IsVerifyingChain = true;

        Assert.True(vm.IsBusy);
        Assert.Equal("Verifying backups...", vm.BusyText);
    }

    [Fact]
    public void ARestoreOutranksAnythingElseRunning()
    {
        var vm = new MainViewModel(new FakeCredentialStore());

        vm.ServerManager.IsBusy = true;
        vm.Restore.TargetDatabaseName = "MyDb_Restored";
        vm.Restore.IsExecuting = true;

        Assert.Contains("Restoring", vm.BusyText);
    }
}
