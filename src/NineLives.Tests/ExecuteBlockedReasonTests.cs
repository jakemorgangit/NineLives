using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Saying why Execute cannot be pressed (#117).
///
/// It was disabled identically whether the user was not connected, had no script, or had a chain
/// the app already knew would not restore. Three different problems, one greyed-out button, and
/// the answer sitting in properties nobody displayed.
/// </summary>
public class ExecuteBlockedReasonTests
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

    private async Task<RestoreViewModel> Loaded()
    {
        _blob.Files =
        [
            new BackupFileInfo
            {
                BlobName = "FULL/SRV01/MyDb/20260110_220000.bak",
                BlobUrl = "https://mystorageaccount.blob.core.windows.net/backups/FULL/SRV01/MyDb/20260110_220000.bak",
                Type = BackupType.Full,
                InferredServerName = "SRV01",
                InferredDatabaseName = "MyDb",
                SizeBytes = 1000,
                LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
            }
        ];

        var vm = NewViewModel();
        vm.SelectedContainer = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        };
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task NotConnectedSaysSo()
    {
        var vm = await Loaded();
        vm.TargetDatabaseName = "MyDb_Restored";

        Assert.False(vm.CanPressExecute);
        Assert.Contains("Connect", vm.ExecuteBlockedReason);
    }

    [Fact]
    public async Task ConnectedButWithNoScriptSaysThatInstead()
    {
        var vm = await Loaded();
        vm.IsConnectedToServer = true;
        vm.TargetDatabaseName = string.Empty;   // no target, so no script

        Assert.False(vm.CanPressExecute);
        Assert.Contains("No script", vm.ExecuteBlockedReason);
    }

    [Fact]
    public async Task AChainThatCannotRestoreSaysThatInstead()
    {
        var vm = await Loaded();
        vm.IsConnectedToServer = true;
        vm.TargetDatabaseName = "MyDb_Restored";
        vm.HasChainErrors = true;

        Assert.False(vm.CanPressExecute);
        Assert.Contains("cannot restore", vm.ExecuteBlockedReason);
    }

    [Fact]
    public async Task NothingIsSaidWhenExecuteIsAvailable()
    {
        var vm = await Loaded();
        vm.IsConnectedToServer = true;
        vm.TargetDatabaseName = "MyDb_Restored";

        Assert.True(vm.CanPressExecute);
        Assert.False(vm.IsExecuteBlocked);
        Assert.Empty(vm.ExecuteBlockedReason);
    }

    /// <summary>The reason has to keep up, or it explains a state the button is no longer in.</summary>
    [Fact]
    public async Task TheReasonFollowsTheStateThatCausedIt()
    {
        var vm = await Loaded();
        vm.TargetDatabaseName = "MyDb_Restored";

        Assert.Contains("Connect", vm.ExecuteBlockedReason);

        vm.IsConnectedToServer = true;
        Assert.Empty(vm.ExecuteBlockedReason);

        vm.HasChainErrors = true;
        Assert.Contains("cannot restore", vm.ExecuteBlockedReason);
    }

    [Fact]
    public async Task NothingIsSaidWhileTheRestoreIsRunning()
    {
        var vm = await Loaded();
        vm.IsConnectedToServer = true;
        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsExecuting = true;

        // The button is doing its other job here; a "why not" line would be noise.
        Assert.Empty(vm.ExecuteBlockedReason);
    }
}
