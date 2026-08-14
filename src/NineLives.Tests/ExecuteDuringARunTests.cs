using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A screen that is running something does not offer to run it again (#401, #402).
///
/// Backup and Copy Database both had this right - `HasScript &amp;&amp; !IsRunning` - and Restore,
/// the screen where WITH REPLACE has already dropped the target, did not. Its Execute button took
/// its enablement from `ExecuteBlockedReason`, which is deliberately EMPTY during a run because
/// there is nothing to nag about mid-restore. Empty read as "not blocked", so the button stayed
/// live.
///
/// Two presses then armed it and called `RunAsync` again, whose first act is a cancellation
/// `Begin()` - abandoning the restore in flight, leaving its target mid-chain in RESTORING, and
/// starting the whole thing over.
///
/// The same shape on the backup screen's Verify: RESTORE VERIFYONLY reads the entire backup, the
/// button looked unchanged throughout, and a second press cancelled and restarted it.
/// </summary>
public class ExecuteDuringARunTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    // ── the restore screen ──────────────────────────────────────────────────────

    private static async Task<RestoreViewModel> LoadedRestore()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/Sales/20260801_220000.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/Sales/20260801_220000.bak",
                    Type = BackupType.Full,
                    InferredServerName = "SRV01",
                    InferredDatabaseName = "Sales",
                    SizeBytes = 1000,
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        var vm = new RestoreViewModel(
            blob, new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store,
            new OperationLog(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ninelives-exec", Guid.NewGuid().ToString("n"))),
            new FakeOperationHistoryStore());

        vm.SelectedContainer = store.Config.BlobContainers[0];
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        RestoreSetup.ChooseADatabaseAndAPoint(vm);
        vm.TargetDatabaseName = "Sales_Restored";
        return vm;
    }

    [Fact]
    public async Task ExecuteIsNotPressableWhileARestoreRuns()
    {
        var vm = await LoadedRestore();

        vm.Execution.SetExecutingForTests(true);

        Assert.True(vm.IsExecuting);
        Assert.False(vm.CanPressExecute);
    }

    /// <summary>
    /// The reason line stays empty on purpose - mid-restore the wanted control is Stop, not an
    /// explanation - so this pins that the two questions are answered separately rather than one
    /// being derived from the other.
    /// </summary>
    [Fact]
    public async Task TheBlockedReasonStaysQuietDuringTheRun()
    {
        var vm = await LoadedRestore();

        vm.Execution.SetExecutingForTests(true);

        Assert.Equal(string.Empty, vm.ExecuteBlockedReason);
        Assert.False(vm.CanPressExecute);
    }

    /// <summary>And the button comes back when the run ends.</summary>
    [Fact]
    public async Task ExecuteIsPressableAgainOnceTheRunEnds()
    {
        var vm = await LoadedRestore();
        vm.IsConnectedToServer = true;

        vm.Execution.SetExecutingForTests(true);
        Assert.False(vm.CanPressExecute);

        vm.Execution.SetExecutingForTests(false);
        Assert.True(vm.CanPressExecute);
    }

    /// <summary>
    /// The last line of defence. RunAsync is reachable from the rehearsal path too, and its first
    /// act is a cancellation Begin() - so re-entering it does not waste a call, it abandons a
    /// restore that is running.
    /// </summary>
    [Fact]
    public async Task RunAsyncRefusesToStartASecondRun()
    {
        var vm = await LoadedRestore();
        vm.Execution.SetExecutingForTests(true);

        await vm.Execution.RunAsync(
            new RestoreRun(
                new ServerConnection { Name = "SRV01", ServerName = "SRV01" },
                "RESTORE DATABASE [x] FROM DISK = N'y' WITH RECOVERY;",
                "Sales_Restored", "Sales", "backups", "1 Full", null, "options"),
            _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.True(vm.Execution.HasError);
        Assert.Contains("already running", vm.Execution.ErrorMessage);
    }

    // ── the backup screen's verify ──────────────────────────────────────────────

    [Fact]
    public void VerifyIsNotPressableWhileVerifying()
    {
        var vm = new BackupViewModel(new FakeCredentialStore(), new FakeSqlServerService())
        {
            CanVerifyLastBackup = true
        };

        Assert.True(vm.CanVerify);
        Assert.True(vm.VerifyLastBackupCommand.CanExecute(null));

        vm.IsVerifying = true;

        Assert.False(vm.CanVerify);
        Assert.False(vm.VerifyLastBackupCommand.CanExecute(null));
    }

    /// <summary>Nothing written means nothing to verify, running or not.</summary>
    [Fact]
    public void VerifyIsNotOfferedBeforeAnythingHasBeenWritten()
    {
        var vm = new BackupViewModel(new FakeCredentialStore(), new FakeSqlServerService());

        Assert.False(vm.CanVerify);
        Assert.False(vm.VerifyLastBackupCommand.CanExecute(null));
    }

    // ── and the two screens that already had it right ───────────────────────────

    [Fact]
    public void TheBackupRunIsStillGatedOnNotAlreadyRunning()
    {
        var vm = new BackupViewModel(new FakeCredentialStore(), new FakeSqlServerService())
        {
            IsRunning = true
        };

        Assert.False(vm.CanExecute);
    }

    [Fact]
    public void TheCopyRunIsStillGatedOnNotAlreadyRunning()
    {
        var vm = new CopyDatabaseViewModel(new FakeCredentialStore(), new FakeSqlServerService())
        {
            IsRunning = true
        };

        Assert.False(vm.CanRun);
    }
}
