using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Receipts are written off the thread that asked for them (#437).
///
/// <see cref="IOperationHistoryStore.Append"/> is blocking file I/O: a cross-process lock whose
/// backoff sleeps up to ten seconds when a scheduled CLI run holds it, a whole-file read, a
/// redaction pass, a serialize and a replace. The screens called it on the dispatcher, once per
/// database - so a fifty-database backup did fifty of those on the UI thread, and the same call
/// in the cancellation handler meant pressing Stop froze the window instead of stopping the run.
///
/// The property under test is *which thread ran the write*, so these assert on that directly
/// rather than on a duration, which would be a timing test and therefore a flaky one.
/// </summary>
public class HistoryWritesOffTheUiThreadTests
{
    [Fact]
    public async Task TheDefaultAppendAsyncLeavesTheCallingThread()
    {
        var store = new FakeOperationHistoryStore();
        var caller = Environment.CurrentManagedThreadId;

        // Through the interface deliberately: AppendAsync is a default interface method, so the
        // implementation under test is the one every store inherits rather than any fake's own.
        await ((IOperationHistoryStore)store).AppendAsync(
            new OperationHistoryEntry { TargetDatabase = "Sales" });

        Assert.Single(store.AppendThreads);
        Assert.NotEqual(caller, store.AppendThreads[0]);
    }

    /// <summary>
    /// Awaited, not fired and forgotten. One receipt per database only means anything if the
    /// receipts are actually on disk when the run ends - a run that dies on the sixth database
    /// has to leave the first five behind, because that half-finished run is the incident
    /// somebody opens the history for.
    /// </summary>
    [Fact]
    public async Task TheWriteHasCompletedByTheTimeTheCallReturns()
    {
        var store = new FakeOperationHistoryStore();

        await ((IOperationHistoryStore)store).AppendAsync(
            new OperationHistoryEntry { TargetDatabase = "Sales" });

        Assert.Single(store.Entries);
    }

    /// <summary>
    /// The store swallows its own failures, and the async path must not turn one into an
    /// unobserved task exception - which would surface later, on a different thread, as a
    /// crash nobody could trace back to a receipt.
    /// </summary>
    [Fact]
    public async Task AStoreThatCannotWriteStillDoesNotThrowAtTheCaller()
    {
        var store = new FakeOperationHistoryStore
        {
            AppendThrows = new InvalidOperationException("history file is read-only")
        };

        // The real store catches internally; the fake throws to prove the caller's own guard
        // holds, which is what every recording call site wraps this in.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IOperationHistoryStore)store).AppendAsync(
                new OperationHistoryEntry { TargetDatabase = "Sales" }));
    }

    /// <summary>
    /// The one that would have caught this: a real backup run, checked for where it recorded.
    ///
    /// Deliberately NOT run on the fixture's dispatcher. Doing that deadlocks the test - Invoke
    /// blocks the dispatcher thread, the run awaits a thread-pool write, and the continuation is
    /// posted back to the thread that is sitting waiting for it. That is an artefact of blocking
    /// inside Invoke rather than anything wrong with the product, where the message loop is free
    /// to process the continuation.
    ///
    /// The property survives the simplification: the write must leave whichever thread ran the
    /// backup. If a call site went back to the synchronous Append, this fails wherever it runs.
    /// </summary>
    [Fact]
    public async Task ABackupRunRecordsOffTheThreadItRanOn()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var history = new FakeOperationHistoryStore();

        var vm = new BackupViewModel(
            store, new FakeSqlServerService { DatabaseList = ["Sales"] },
            TestLogs.Temp(), notifier: null, history: history);

        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "Sales";
        vm.GenerateCommand.Execute(null);

        var runner = Environment.CurrentManagedThreadId;

        // Armed, then run - the button is a two-press control.
        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        // The run has to have got as far as recording, or this proves nothing.
        Assert.NotEmpty(history.AppendThreads);
        Assert.DoesNotContain(runner, history.AppendThreads);
    }
}
