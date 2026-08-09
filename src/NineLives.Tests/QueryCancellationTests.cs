using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Stopping a server query that is already running (#111).
///
/// `RESTORE VERIFYONLY` reads every byte of every backup in the chain at `CommandTimeout = 0`.
/// Before this there was no token, no Stop button and no timeout: on a large chain the app was
/// unresponsive for hours and the only way out was killing the process, which left the reads
/// running server-side.
/// </summary>
public class QueryCancellationTests
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

    private static BackupFileInfo File(string blobName, BackupType type, DateTime stamp)
        => new()
        {
            BlobName = blobName,
            BlobUrl = $"https://mystorageaccount.blob.core.windows.net/backups/{blobName}",
            Type = type,
            InferredServerName = "SRV01",
            InferredDatabaseName = "MyDb",
            SizeBytes = 1000,
            LastModified = new DateTimeOffset(stamp, TimeSpan.Zero)
        };

    /// <summary>A full plus three logs, so the chain has four sets to verify one at a time.</summary>
    private async Task<RestoreViewModel> Connected()
    {
        _blob.Files =
        [
            File("FULL/SRV01/MyDb/20260110_220000.bak", BackupType.Full, T0),
            File("LOG/SRV01/MyDb/20260110_230000.trn", BackupType.TransactionLog, T0.AddHours(1)),
            File("LOG/SRV01/MyDb/20260111_000000.trn", BackupType.TransactionLog, T0.AddHours(2)),
            File("LOG/SRV01/MyDb/20260111_010000.trn", BackupType.TransactionLog, T0.AddHours(3)),
        ];

        var vm = NewViewModel();
        vm.SelectedContainer = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // The app no longer chooses a database or a restore point for anybody.
        RestoreSetup.ChooseADatabaseAndAPoint(vm);

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
    public async Task VerifyIsGivenARealTokenRatherThanNone()
    {
        var vm = await Connected();

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.NotEmpty(_sql.VerifyTokens);
        Assert.All(_sql.VerifyTokens, t => Assert.True(t.CanBeCanceled,
            "The viewmodel passed CancellationToken.None, so there is nothing a Stop button could do."));
    }

    [Fact]
    public async Task StoppingMidVerifyLeavesTheRestUnread()
    {
        var vm = await Connected();

        // Stop as soon as the first backup is being read, the way pressing the button would.
        _sql.OnVerify = _ => vm.CancelQueryCommand.Execute(null);

        await vm.VerifyChainCommand.ExecuteAsync(null);

        // Four sets in the chain; the first raises the cancel, so nothing after it runs.
        Assert.Single(_sql.VerifyTokens);
        Assert.Contains("cancel", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cancelling is not a failure. Reporting it as one would tell the user their backups are bad
    /// when all they did was press Stop - the same trap the restore path already guards against.
    /// </summary>
    [Fact]
    public async Task StoppingIsNotReportedAsAVerificationFailure()
    {
        var vm = await Connected();
        _sql.OnVerify = _ => vm.CancelQueryCommand.Execute(null);

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.False(vm.HasVerifyFailures);
    }

    [Fact]
    public async Task TheStopButtonIsOfferedOnlyWhileSomethingIsRunning()
    {
        var vm = await Connected();

        Assert.False(vm.CanCancelQuery);

        bool offeredDuring = false;
        _sql.OnVerify = _ => offeredDuring = vm.CanCancelQuery;

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.True(offeredDuring, "No Stop was offered while the verify was running.");
        Assert.False(vm.CanCancelQuery);
    }

    [Fact]
    public async Task AFinishedVerifyLeavesNothingToCancel()
    {
        var vm = await Connected();

        await vm.VerifyChainCommand.ExecuteAsync(null);

        Assert.False(vm.CanCancelQuery);
        Assert.False(vm.IsCancelling);
        Assert.Equal(4, _sql.VerifyTokens.Count);
    }
}
