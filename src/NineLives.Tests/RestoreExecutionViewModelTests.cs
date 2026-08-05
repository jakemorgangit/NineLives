using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The execute path, which needs a WPF Application because it marshals progress onto the
/// dispatcher and batches console output through a DispatcherTimer.
///
/// This is the path where getting it wrong is most expensive - it runs RESTORE against someone's
/// server - and until the services were behind interfaces (#41) none of it could be tested at all.
/// </summary>
[Collection(WpfCollection.Name)]
public class RestoreExecutionViewModelTests(WpfFixture wpf)
{
    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BlobContainerConfig Container() => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
    };

    private static BackupFileInfo FullBackup() => new()
    {
        BlobName = "FULL/SRV01/MyDb/20260110_220000.bak",
        BlobUrl = "https://mystorageaccount.blob.core.windows.net/backups/FULL/SRV01/MyDb/20260110_220000.bak",
        Type = BackupType.Full,
        InferredServerName = "SRV01",
        InferredDatabaseName = "MyDb",
        SizeBytes = 1000,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    /// <summary>Loads one full backup and arms the execute button, ready to fire.</summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> ReadyToExecute(
        ServerConnection connected, FakeCredentialStore store)
    {
        var blob = new FakeBlobStorageService { Files = [FullBackup()] };
        var sql = new FakeSqlServerService();

        var vm = new RestoreViewModel(
            blob, sql, new BackupChainBuilder(), new RestoreScriptGenerator(), store)
        {
            SelectedContainer = Container()
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsConnectedToServer = true;
        vm.ConnectedServer = connected;

        // The button arms on the first press and fires on the second.
        await vm.ExecuteScriptCommand.ExecuteAsync(null);
        Assert.True(vm.IsExecuteArmed);

        return (vm, sql);
    }

    /// <summary>
    /// The #11 regression, now checkable by CI.
    ///
    /// Execute used to re-read config.json and take the first entry whose ServerName matched,
    /// rather than the connection in use. Two entries for one host that differ in authentication -
    /// a Windows-auth entry and a SQL-auth entry for the same box - is an ordinary setup, and the
    /// restore would then run under credentials the user never connected with or tested.
    /// </summary>
    [Fact]
    public void TheRestoreRunsAgainstTheConnectionInUseNotOneLookedUpByName()
    {
        ServerConnection? executedAgainst = null;

        var windowsAuthEntry = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01 (Windows)",
            ServerName = "SRV01",
            AuthMode = AuthMode.WindowsAuth
        };
        var sqlAuthEntry = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01 (SQL)",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "restoreadmin"
        };

        var store = new FakeCredentialStore();
        // Both entries in the config, the Windows one first - which is what a name lookup returns.
        store.Config.Servers.Add(windowsAuthEntry);
        store.Config.Servers.Add(sqlAuthEntry);

        RunOnUi(async () =>
        {
            // Connected as the SECOND entry.
            var (vm, sql) = await ReadyToExecute(sqlAuthEntry, store);
            await vm.ExecuteScriptCommand.ExecuteAsync(null);
            executedAgainst = Assert.Single(sql.ExecutedAgainst);
        });

        Assert.Same(sqlAuthEntry, executedAgainst);
        Assert.NotSame(windowsAuthEntry, executedAgainst);
    }

    /// <summary>
    /// The #10 regression. A credential is server-scoped, so dropping and recreating it on every
    /// run removed it out from under anything else relying on it - a backup job writing to the
    /// same container being the obvious casualty.
    /// </summary>
    [Fact]
    public void AnExistingSasCredentialIsLeftAloneRatherThanRecreated()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var store = new FakeCredentialStore();
        string log = string.Empty;

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, store);
            sql.CredentialExists = true;
            sql.CredentialIsSas = true;
            store.SaveSasToken(vm.SelectedContainer!, "sv=2024-01-01&sig=x");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);
            log = vm.ConsoleText;
        });

        Assert.Contains("Server state not modified", log);
    }

    /// <summary>The script that runs is the one on screen, not one rebuilt on the way out.</summary>
    [Fact]
    public void TheScriptExecutedIsTheScriptThatWasShown()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        string shown = string.Empty, executed = string.Empty;

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, new FakeCredentialStore());
            shown = vm.GeneratedScript;

            await vm.ExecuteScriptCommand.ExecuteAsync(null);
            executed = Assert.Single(sql.ExecutedScripts);
        });

        Assert.Equal(shown, executed);
        Assert.Contains("RESTORE DATABASE [MyDb_Restored]", executed);
    }

    /// <summary>
    /// A failed restore has to report failure. The FireInfoMessageEventOnUserErrors defect (#6)
    /// meant the app said "completed successfully" over a total failure; this pins the ViewModel
    /// half of that contract, which the live SQL tests cannot reach.
    /// </summary>
    [Fact]
    public void AFailedRestoreIsReportedAsAFailure()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        bool hasError = false;

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, new FakeCredentialStore());
            sql.ExecuteThrows = new InvalidOperationException("RESTORE terminating abnormally.");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);
            hasError = vm.HasError;
        });

        Assert.True(hasError, "A restore that threw was not reported as a failure.");
    }

    /// <summary>
    /// Runs an async body on the WPF thread and waits for it.
    ///
    /// Blocking the dispatcher thread with .GetAwaiter().GetResult() would deadlock: the
    /// ViewModel's awaits resume on that same thread, and it would be sitting in the wait. Pumping
    /// a DispatcherFrame keeps it processing until the body completes.
    /// </summary>
    private void RunOnUi(Func<Task> body)
    {
        Exception? captured = null;

        wpf.Invoke(() =>
        {
            var frame = new DispatcherFrame();

            _ = body().ContinueWith(
                t =>
                {
                    captured = t.Exception?.GetBaseException();
                    frame.Continue = false;
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            Dispatcher.PushFrame(frame);
        });

        if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
