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

    /// <summary>
    /// A log in a temp directory. Without this the execute path appends real restore lines to the
    /// user's actual %LOCALAPPDATA% log - a test writing into the thing someone attaches to a bug
    /// report.
    /// </summary>
    private static OperationLog ThrowawayLog() => new(System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ninelives-vm-tests", Guid.NewGuid().ToString("n")));

    /// <summary>Loads one full backup and arms the execute button, ready to fire.</summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> ReadyToExecute(
        ServerConnection connected, FakeCredentialStore store,
        FakeOperationHistoryStore? history = null)
    {
        var blob = new FakeBlobStorageService { Files = [FullBackup()] };
        var sql = new FakeSqlServerService();

        var vm = new RestoreViewModel(
            blob, sql, new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            ThrowawayLog(), history ?? new FakeOperationHistoryStore())
        {
            SelectedContainer = Container()
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // The app no longer chooses a database or a restore point for anybody.
        RestoreSetup.ChooseADatabaseAndAPoint(vm);

        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsConnectedToServer = true;
        vm.ConnectedServer = connected;

        // The button arms on the first press and fires on the second.
        await vm.ExecuteScriptCommand.ExecuteAsync(null);
        Assert.True(vm.Execution.IsArmed);

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
            sql.Credential = new BlobCredentialStatus(
                BlobCredentialIdentity.SharedAccessSignature, "SHARED ACCESS SIGNATURE");
            store.SaveSasToken(vm.SelectedContainer!, "sv=2024-01-01&sig=x");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);
            log = vm.Execution.Console.Text;
        });

        Assert.Contains("Server state not modified", log);
    }

    /// <summary>
    /// The #145 regression, and the reason it mattered.
    ///
    /// A managed-identity credential restores perfectly well, but the old check reduced every
    /// identity to "is it SAS", so this arrived at the same branch as a broken one and was ALTERed -
    /// which resets IDENTITY. The instance stopped authenticating to that container as itself, and
    /// so did every other job pointed at the same URL, under the log line "Credential updated on
    /// the server".
    /// </summary>
    [Fact]
    public void AManagedIdentityCredentialIsLeftAloneRatherThanConvertedToSas()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var store = new FakeCredentialStore();
        string log = string.Empty;
        List<string> writes = [];
        List<string> executed = [];

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, store);
            sql.Credential = new BlobCredentialStatus(
                BlobCredentialIdentity.ManagedIdentity, "Managed Identity");

            // A SAS token IS stored for the container - that is what made this reachable. Browsing
            // with one and restoring with the instance's identity is an ordinary pairing.
            store.SaveSasToken(vm.SelectedContainer!, "sv=2024-01-01&sig=x");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);

            log = vm.Execution.Console.Text;
            writes = sql.CredentialWrites;
            executed = sql.ExecutedScripts;
        });

        Assert.Empty(writes);
        Assert.Contains("Managed Identity", log);
        Assert.Contains("Server state not modified", log);

        // Left alone, not skipped: the restore still ran.
        Assert.Single(executed);
    }

    /// <summary>
    /// An identity that genuinely cannot serve a restore is still not this app's to overwrite on
    /// the way past. Converting it is a real option - it is what the button on the panel does -
    /// but it changes shared state on someone's instance, so it takes a decision rather than a
    /// side effect of pressing Execute (#145).
    /// </summary>
    [Fact]
    public void AnUnusableCredentialStopsTheRestoreInsteadOfBeingOverwritten()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var store = new FakeCredentialStore();
        var history = new FakeOperationHistoryStore();
        string log = string.Empty;
        string error = string.Empty;
        List<string> writes = [];
        List<string> executed = [];

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, store, history);
            sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.Other, "MYDOMAIN\\svc_sql");
            store.SaveSasToken(vm.SelectedContainer!, "sv=2024-01-01&sig=x");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);

            log = vm.Execution.Console.Text;
            error = vm.ErrorMessage;
            writes = sql.CredentialWrites;
            executed = sql.ExecutedScripts;
        });

        Assert.Empty(writes);
        Assert.Empty(executed);

        // Named, so it is obvious whether the credential is a mistake or somebody's arrangement.
        Assert.Contains("MYDOMAIN\\svc_sql", error);
        Assert.Contains("MYDOMAIN\\svc_sql", log);

        // Nothing was attempted, so this is not an execution to file.
        Assert.Empty(history.Entries);
    }

    /// <summary>
    /// The one case that does still write: nothing of that name exists, so there is nothing to
    /// destroy and the restore cannot proceed without it.
    /// </summary>
    [Fact]
    public void AMissingCredentialIsStillCreated()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var store = new FakeCredentialStore();
        List<string> writes = [];
        List<string> executed = [];

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, store);
            sql.Credential = BlobCredentialStatus.Missing;
            store.SaveSasToken(vm.SelectedContainer!, "sv=2024-01-01&sig=x");

            await vm.ExecuteScriptCommand.ExecuteAsync(null);

            writes = sql.CredentialWrites;
            executed = sql.ExecutedScripts;
        });

        Assert.Single(writes);
        Assert.Single(executed);
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
    /// Every execution is filed, so the record exists after the app closes (#31).
    /// </summary>
    [Fact]
    public void ASuccessfulRestoreIsRecordedWithEnoughToPutInATicket()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var history = new FakeOperationHistoryStore();

        RunOnUi(async () =>
        {
            var (vm, _) = await ReadyToExecute(server, new FakeCredentialStore(), history);
            await vm.ExecuteScriptCommand.ExecuteAsync(null);
        });

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
        Assert.Equal("SRV01", entry.ServerName);
        Assert.Equal("MyDb_Restored", entry.TargetDatabase);
        Assert.Contains("RESTORE DATABASE [MyDb_Restored]", entry.Script);

        // The log is captured AFTER the console flush, so it is the whole thing rather than
        // whatever had escaped the batching buffer at the moment the run ended.
        Assert.Contains("Restore completed successfully", entry.Log);
        Assert.True(entry.CompletedAt >= entry.StartedAt);
    }

    [Fact]
    public void AFailedRestoreIsRecordedAsFailedWithTheReason()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var history = new FakeOperationHistoryStore();

        RunOnUi(async () =>
        {
            var (vm, sql) = await ReadyToExecute(server, new FakeCredentialStore(), history);
            sql.ExecuteThrows = new InvalidOperationException("RESTORE terminating abnormally.");
            await vm.ExecuteScriptCommand.ExecuteAsync(null);
        });

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Failed, entry.Outcome);
        Assert.Contains("terminating abnormally", entry.ErrorMessage);
    }

    /// <summary>
    /// Pressing Execute once only arms the button. Nothing ran, so nothing belongs in the history -
    /// otherwise it fills with entries for restores that never happened.
    /// </summary>
    [Fact]
    public void ArmingTheButtonRecordsNothing()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        var history = new FakeOperationHistoryStore();

        RunOnUi(async () =>
        {
            await ReadyToExecute(server, new FakeCredentialStore(), history);
        });

        Assert.Empty(history.Entries);
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
