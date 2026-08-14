using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Receipts are written without blocking the thread that asked for them (#437).
///
/// <see cref="IOperationHistoryStore.Append"/> is blocking file I/O: a cross-process lock whose
/// backoff sleeps up to ten seconds when a scheduled CLI run holds it, a whole-file read, a
/// redaction pass, a serialize and a replace. The screens called it on the dispatcher, once per
/// database - so a fifty-database backup did fifty of those on the UI thread, and the same call in
/// the cancellation handler meant pressing Stop froze the window instead of stopping the run.
///
/// These first asserted the write ran on a DIFFERENT thread, which was not merely fragile but a
/// claim the runtime never makes: Task.Run guarantees the work is queued to the thread pool, not
/// which thread picks it up, and a loaded runner can hand it straight back to the caller's own
/// pool thread. It did exactly that on CI, on a commit that was green locally.
///
/// They now assert the two things that are guaranteed and that actually matter: the caller is not
/// held while the write happens, and the write has finished by the time the call is awaited.
/// </summary>
public class HistoryWritesOffTheUiThreadTests
{
    /// <summary>
    /// A store whose Append blocks until released, and which deliberately does NOT implement
    /// AppendAsync - so what these exercise is the default interface method every store inherits.
    /// </summary>
    private sealed class BlockingStore : IOperationHistoryStore
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);
        public List<OperationHistoryEntry> Written { get; } = [];

        public void Release() => _release.Set();

        public void Append(OperationHistoryEntry entry)
        {
            Entered.Set();
            _release.Wait(TimeSpan.FromSeconds(10));
            lock (Written) Written.Add(entry);
        }

        public List<OperationHistoryEntry> Load() => [];
        public bool CouldNotRead => false;
        public void Clear() { }
        public string FilePath => "(in memory)";
    }

    /// <summary>
    /// The property the fix exists for: the caller gets on with its life while the write is still
    /// inside the lock. Deterministic - it turns on a gate this test controls rather than on
    /// which thread the scheduler happened to choose.
    /// </summary>
    [Fact]
    public async Task AppendAsyncDoesNotHoldTheCallerWhileTheWriteHappens()
    {
        var store = new BlockingStore();

        var write = ((IOperationHistoryStore)store).AppendAsync(
            new OperationHistoryEntry { TargetDatabase = "Sales" });

        // The write is under way and the caller is here, not in it. Entered is an event, not a
        // task, so waiting on it is not the blocking-a-task mistake this file is about.
        Assert.True(store.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(write.IsCompleted);

        store.Release();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Awaited, not fired and forgotten. One receipt per database only means something if the
    /// receipts are on disk when the run ends - a run that dies on the sixth database has to leave
    /// the first five behind, because that half-finished run is the incident somebody opens the
    /// history for.
    /// </summary>
    [Fact]
    public async Task TheWriteHasCompletedByTheTimeTheCallIsAwaited()
    {
        var store = new BlockingStore();
        store.Release();

        await ((IOperationHistoryStore)store).AppendAsync(
            new OperationHistoryEntry { TargetDatabase = "Sales" });

        Assert.Single(store.Written);
    }

    /// <summary>
    /// The store swallows its own failures, and the async path must not turn one into an
    /// unobserved task exception - which would surface later, on another thread, as a crash
    /// nobody could trace back to a receipt.
    /// </summary>
    [Fact]
    public async Task AStoreThatCannotWriteFaultsTheTaskRatherThanTheProcess()
    {
        var store = new FakeOperationHistoryStore
        {
            AppendThrows = new InvalidOperationException("history file is read-only")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(new OperationHistoryEntry { TargetDatabase = "Sales" }));
    }

    /// <summary>
    /// And the call site that matters takes the non-blocking route. Counted rather than inferred
    /// from a thread id: a call site that reverted to the synchronous Append would record its
    /// receipt just the same, and only this tells the two apart.
    /// </summary>
    [Fact]
    public async Task ABackupRunRecordsThroughTheNonBlockingPath()
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

        // Armed, then run - the button is a two-press control.
        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.NotEmpty(history.Entries);
        Assert.Equal(history.Entries.Count, history.AsyncAppends);
    }
}
