using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Every operation that actually ran leaves a receipt (#434).
///
/// History held restores and rehearsals only. A backup taken from this app is as much a thing that
/// happened to a production server - and an ordinary full backup moves the differential base, so a
/// server's whole differential schedule can come to depend on a file this app wrote with nothing
/// in the app saying it happened. A copy touches two servers and overwrites a database on one.
///
/// The CLI already recorded its backups. It was the app that did not, which made the two front
/// ends disagree about what was worth remembering.
/// </summary>
public class OperationHistoryForEveryKindTests
{
    private static FakeCredentialStore Store()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        return store;
    }

    private static (BackupViewModel vm, FakeSqlServerService sql, FakeOperationHistoryStore history)
        BackupScreen()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales", "Archive"] };
        var history = new FakeOperationHistoryStore();
        var vm = new BackupViewModel(Store(), sql, TestLogs.Temp(), history: history);
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];
        return (vm, sql, history);
    }

    /// <summary>Arm-and-confirm: the first press arms, the second runs.</summary>
    private static async Task RunAsync(BackupViewModel vm)
    {
        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);
    }

    // ── backup ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABackupLeavesAReceipt()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);

        await RunAsync(vm);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Backup, entry.Kind);
        Assert.Equal("Sales", entry.TargetDatabase);
        Assert.Equal("SRV01", entry.ServerName);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
        Assert.Equal("backups", entry.ContainerName);
        Assert.Contains("BACKUP DATABASE [Sales]", entry.Script);
    }

    /// <summary>
    /// The question the receipt exists to answer months later. Stated either way, because "no
    /// COPY_ONLY mentioned" and "COPY_ONLY was off" are indistinguishable if only the true case
    /// is written down.
    /// </summary>
    [Fact]
    public async Task TheReceiptSaysWhetherTheDifferentialBaseMoved()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.CopyOnly = false;
        vm.GenerateCommand.Execute(null);

        await RunAsync(vm);

        Assert.Contains("NOT copy-only", Assert.Single(history.Entries).OptionsSummary);
    }

    [Fact]
    public async Task ACopyOnlyBackupSaysSoToo()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);   // copy-only is the default

        await RunAsync(vm);

        Assert.Contains("COPY_ONLY", Assert.Single(history.Entries).OptionsSummary);
        Assert.DoesNotContain("NOT copy-only", Assert.Single(history.Entries).OptionsSummary);
    }

    /// <summary>
    /// One entry per database, matching the semantics the run loop already commits to: a failure on
    /// one names that one and the rest still run (#208). A single entry would have to describe a
    /// success and a failure with one outcome.
    /// </summary>
    [Fact]
    public async Task EachDatabaseInAMultiDatabaseRunGetsItsOwnReceipt()
    {
        var (vm, sql, history) = BackupScreen();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);

        sql.FailOnExecuteNumber = 2;   // the second database fails, the rest carry on

        await RunAsync(vm);

        Assert.Equal(2, history.Entries.Count);
        Assert.Contains(history.Entries, e => e.Outcome == OperationOutcome.Succeeded);

        var failure = Assert.Single(history.Entries, e => e.Outcome == OperationOutcome.Failed);
        Assert.Contains("fake failure", failure.ErrorMessage);
    }

    [Fact]
    public async Task AFailedBackupIsRecordedAsAFailure()
    {
        var (vm, sql, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);
        sql.ExecuteThrows = new InvalidOperationException("Msg 3201: cannot open backup device");

        await RunAsync(vm);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Failed, entry.Outcome);
        Assert.Contains("Msg 3201", entry.ErrorMessage);
    }

    /// <summary>
    /// Recording must never be able to fail the operation. The backup has already happened by the
    /// time the entry is written, so throwing here would report a completed backup as a failure -
    /// the worst possible lie for this app to tell.
    /// </summary>
    [Fact]
    public async Task AHistoryThatCannotBeWrittenDoesNotFailTheBackup()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);
        history.AppendThrows = new IOException("the history file is locked");

        await RunAsync(vm);

        Assert.False(vm.HasError);
        Assert.Empty(history.Entries);
    }

    // ── copy ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACopyLeavesAReceiptNamingBothServers()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales"] };
        var history = new FakeOperationHistoryStore();
        var vm = new CopyDatabaseViewModel(Store(), sql, TestLogs.Temp(), history: history);
        vm.SourceServer = vm.Servers[0];
        vm.TargetServer = vm.Servers[1];
        vm.Container = vm.Containers[0];
        vm.SourceDatabase = "Sales";
        vm.TargetDatabaseName = "Sales_Copy";
        vm.GenerateCommand.Execute(null);

        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Copy, entry.Kind);

        // The server written to, and the one read from beside it - which machine was the source is
        // the first thing anyone asks about a copy afterwards.
        Assert.Equal("SRV02", entry.ServerName);
        Assert.Equal("SRV01", entry.SourceServerName);
        Assert.Equal("Sales_Copy", entry.TargetDatabase);
        Assert.Equal("Sales", entry.SourceDatabase);
        Assert.Contains("SRV01", entry.Summary);
        Assert.Contains("SRV02", entry.Summary);

        // Both halves, so a copy that half-worked can be finished from the restore script alone.
        Assert.Contains("BACKUP DATABASE", entry.Script);
        Assert.Contains("RESTORE DATABASE", entry.Script);
    }

    // ── what must NOT be recorded ───────────────────────────────────────────────

    /// <summary>
    /// Generating a script is not performing an operation. If it were recorded, the history would
    /// fill with things that never touched a server and bury the ones that did.
    /// </summary>
    [Fact]
    public void GeneratingAScriptRecordsNothing()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";

        vm.GenerateCommand.Execute(null);

        Assert.Empty(history.Entries);
    }

    /// <summary>Arming and thinking better of it is not an operation either.</summary>
    [Fact]
    public async Task ArmingWithoutConfirmingRecordsNothing()
    {
        var (vm, _, history) = BackupScreen();
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);

        await vm.ExecuteCommand.ExecuteAsync(null);   // armed only

        Assert.True(vm.IsArmed);
        Assert.Empty(history.Entries);
    }
}

/// <summary>
/// The History screen, now that it lists four kinds (#434).
///
/// A "back up everything before the patch" run writes one entry per database, so without a way to
/// narrow the list "when did we last restore this" means reading past fifty backups.
/// </summary>
public class HistoryKindFilterTests
{
    private static FakeOperationHistoryStore StoreWith(params string[] kinds)
    {
        var store = new FakeOperationHistoryStore();

        // Appended oldest first; the fake inserts at the front, same as the real store.
        foreach (var kind in kinds)
            store.Append(new OperationHistoryEntry
            {
                Kind = kind,
                ServerName = "SRV01",
                TargetDatabase = $"Db_{kind}",
                Outcome = OperationOutcome.Succeeded
            });

        return store;
    }

    [Fact]
    public void EverythingShowsByDefault()
    {
        var vm = new HistoryViewModel(StoreWith(
            OperationKind.Backup, OperationKind.Restore, OperationKind.Copy));

        Assert.Equal(HistoryViewModel.AllKinds, vm.KindFilter);
        Assert.Equal(3, vm.Entries.Count);
    }

    [Fact]
    public void FilteringToOneKindShowsOnlyThat()
    {
        var vm = new HistoryViewModel(StoreWith(
            OperationKind.Backup, OperationKind.Backup, OperationKind.Restore));

        vm.KindFilter = OperationKind.Restore;

        Assert.Equal(OperationKind.Restore, Assert.Single(vm.Entries).Kind);
    }

    /// <summary>
    /// Records written before Kind existed hold "Restore", which is what they were - so filtering
    /// to Restore must include them rather than hiding somebody's oldest history.
    /// </summary>
    [Fact]
    public void OlderRecordsWithNoKindStillCountAsRestores()
    {
        var store = new FakeOperationHistoryStore();
        store.Append(new OperationHistoryEntry { ServerName = "SRV01", TargetDatabase = "Legacy" });

        var vm = new HistoryViewModel(store) { KindFilter = OperationKind.Restore };

        Assert.Equal("Legacy", Assert.Single(vm.Entries).TargetDatabase);
    }

    /// <summary>The kind filter and the text box narrow together rather than replacing each other.</summary>
    [Fact]
    public void TheTextFilterAndTheKindFilterBothApply()
    {
        var store = new FakeOperationHistoryStore();
        store.Append(new OperationHistoryEntry
        { Kind = OperationKind.Backup, ServerName = "SRV01", TargetDatabase = "Sales" });
        store.Append(new OperationHistoryEntry
        { Kind = OperationKind.Backup, ServerName = "SRV01", TargetDatabase = "Archive" });
        store.Append(new OperationHistoryEntry
        { Kind = OperationKind.Restore, ServerName = "SRV01", TargetDatabase = "Sales" });

        var vm = new HistoryViewModel(store)
        {
            KindFilter = OperationKind.Backup,
            FilterText = "Sales"
        };

        var entry = Assert.Single(vm.Entries);
        Assert.Equal(OperationKind.Backup, entry.Kind);
        Assert.Equal("Sales", entry.TargetDatabase);
    }

    /// <summary>Searching a server name finds copies that READ it, not just ones that wrote to it.</summary>
    [Fact]
    public void SearchingFindsTheSourceServerOfACopy()
    {
        var store = new FakeOperationHistoryStore();
        store.Append(new OperationHistoryEntry
        {
            Kind = OperationKind.Copy,
            ServerName = "SRV02",
            SourceServerName = "SRV01",
            TargetDatabase = "Sales_Copy"
        });

        var vm = new HistoryViewModel(store) { FilterText = "SRV01" };

        Assert.Single(vm.Entries);
    }
}
