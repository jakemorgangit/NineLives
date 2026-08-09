using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// An audit survives closing the app (#130).
///
/// The results were already written to disk, but nothing read them back until somebody pressed
/// Audit again - so relaunching threw away the visible result of a three-and-a-half minute
/// operation. The cache still held it and the re-run would have been instant, but you would first
/// have to guess that a button you had already pressed needed pressing again.
///
/// A backup header never changes. An answer from last week is as good as one from this second, and
/// applying it costs one local file read rather than a round trip per set.
/// </summary>
public class AuditSurvivesARestartTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupFileInfo File_(string name, BackupType type, string etag) => new()
    {
        BlobName = $"FULL/SRV01/MyDb/{name}",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/{name}",
        ETag = etag,
        Type = type,
        InferredDatabaseName = "MyDb",
        InferredServerName = "SRV01",
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    private static BackupLocation Container() => BackupLocation.Blob(new BlobContainerConfig
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    });

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    /// <summary>A fresh inventory over the same blobs - the app having been closed and reopened.</summary>
    private static BackupInventoryViewModel Relaunched(IBackupAuditStore store, params BackupFileInfo[] files)
        => new(new FakeBlobStorageService { Files = files.ToList() },
               new FakeSqlServerService { Header = Header() },
               TestLogs.Temp(),
               store);

    private static BackupFileInfo Header(BackupType type = BackupType.Full) => new()
    {
        DatabaseName = "MyDb",
        Type = type,
        BackupTypeCode = type == BackupType.Full ? 1 : 2
    };

    // ── the thing that was missing ──────────────────────────────────────────────

    /// <summary>
    /// Load, audit, close, reopen, load again - the backups come back already marked, with nothing
    /// pressed and nothing read from the server.
    /// </summary>
    [Fact]
    public async Task BackupsAuditedInAPreviousRunComeBackAlreadyMarked()
    {
        var store = TestAuditStores.Temp();

        var first = Relaunched(store, File_("MyDb_FULL_20260801_220000.bak", BackupType.Full, "\"aaa\""));
        await first.LoadAsync(Container());
        first.SelectedDatabaseName = "MyDb";
        await first.AuditAsync(Server());

        Assert.True(first.WorkingSet.Single().AuditPassed);

        // Reopened: a new viewmodel, new file objects, the same blobs and the same cache.
        var second = Relaunched(store, File_("MyDb_FULL_20260801_220000.bak", BackupType.Full, "\"aaa\""));
        await second.LoadAsync(Container());
        second.SelectedDatabaseName = "MyDb";

        Assert.True(second.WorkingSet.Single().AuditPassed);
        Assert.Equal(1, second.PreAuditedCount);
    }

    /// <summary>Nothing goes to the server to find that out.</summary>
    [Fact]
    public async Task NothingIsReadFromTheServerToRecogniseThem()
    {
        var store = TestAuditStores.Temp();

        var first = Relaunched(store, File_("a.bak", BackupType.Full, "\"aaa\""));
        await first.LoadAsync(Container());
        first.SelectedDatabaseName = "MyDb";
        await first.AuditAsync(Server());

        var blob = new FakeBlobStorageService { Files = [File_("a.bak", BackupType.Full, "\"aaa\"")] };
        var sql = new FakeSqlServerService { Header = Header() };
        var second = new BackupInventoryViewModel(blob, sql, TestLogs.Temp(), store);

        await second.LoadAsync(Container());
        second.SelectedDatabaseName = "MyDb";

        Assert.Empty(sql.HeaderBatches);
        Assert.True(second.WorkingSet.Single().AuditPassed);
    }

    /// <summary>And the estimate agrees, so nobody is quoted three minutes for work already done.</summary>
    [Fact]
    public async Task TheEstimateSaysThereIsNothingLeftToRead()
    {
        var store = TestAuditStores.Temp();

        var first = Relaunched(store, File_("a.bak", BackupType.Full, "\"aaa\""));
        await first.LoadAsync(Container());
        first.SelectedDatabaseName = "MyDb";
        await first.AuditAsync(Server());

        var second = Relaunched(store, File_("a.bak", BackupType.Full, "\"aaa\""));
        await second.LoadAsync(Container());
        second.SelectedDatabaseName = "MyDb";

        Assert.Contains("instant", second.AuditEstimate);
    }

    /// <summary>A mismatch is remembered too - it is at least as worth knowing as a pass.</summary>
    [Fact]
    public async Task AMismatchFromAPreviousRunComesBackAsAMismatch()
    {
        var store = TestAuditStores.Temp();

        var blob = new FakeBlobStorageService { Files = [File_("a.trn", BackupType.TransactionLog, "\"aaa\"")] };
        var first = new BackupInventoryViewModel(
            blob, new FakeSqlServerService { Header = Header(BackupType.Full) }, TestLogs.Temp(), store);

        await first.LoadAsync(Container());
        first.SelectedDatabaseName = "MyDb";
        await first.AuditAsync(Server());

        Assert.True(first.WorkingSet.Single().AuditFailed);

        var second = Relaunched(store, File_("a.trn", BackupType.TransactionLog, "\"aaa\""));
        await second.LoadAsync(Container());
        second.SelectedDatabaseName = "MyDb";

        Assert.True(second.WorkingSet.Single().AuditFailed);
    }

    // ── and what it must NOT do ─────────────────────────────────────────────────

    /// <summary>
    /// A blob replaced under the same name is a different blob, and its old answer is worthless.
    /// The ETag is the whole reason the key is what it is.
    /// </summary>
    [Fact]
    public async Task AReplacedBackupIsNotRecognisedFromTheOldAnswer()
    {
        var store = TestAuditStores.Temp();

        var first = Relaunched(store, File_("a.bak", BackupType.Full, "\"aaa\""));
        await first.LoadAsync(Container());
        first.SelectedDatabaseName = "MyDb";
        await first.AuditAsync(Server());

        // Same name, different content.
        var second = Relaunched(store, File_("a.bak", BackupType.Full, "\"bbb\""));
        await second.LoadAsync(Container());
        second.SelectedDatabaseName = "MyDb";

        Assert.False(second.WorkingSet.Single().AuditPassed);
        Assert.Equal(0, second.PreAuditedCount);
    }

    /// <summary>Backups nobody has ever audited come back unmarked, not assumed good.</summary>
    [Fact]
    public async Task BackupsNeverAuditedComeBackUnmarked()
    {
        var vm = Relaunched(TestAuditStores.Temp(), File_("a.bak", BackupType.Full, "\"aaa\""));

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "MyDb";

        var set = vm.WorkingSet.Single();

        Assert.False(set.AuditPassed);
        Assert.False(set.AuditFailed);
        Assert.Equal(string.Empty, set.AuditDisplay);
        Assert.Equal(0, vm.PreAuditedCount);
    }

    /// <summary>
    /// A striped set is audited as a whole - one HEADERONLY covering every stripe - so a cache that
    /// only answers for some of its files is one where a stripe has been replaced. That has to go
    /// back to the server rather than be counted as known.
    /// </summary>
    [Fact]
    public void ASetTheCacheOnlyPartlyAnswersForIsNotMarked()
    {
        var one = File_("p1.bak", BackupType.Full, "\"aaa\"");
        var two = File_("p2.bak", BackupType.Full, "\"bbb\"");

        var set = new BackupSet { SetId = "s", Type = BackupType.Full, Files = [one, two] };

        var cached = new Dictionary<string, AuditRecord>
        {
            [BackupAuditStore.KeyFor(one)] = new("k", true, "MyDb", 1, T0)
        };

        Assert.Equal(0, BackupAuditor.ApplyCached([set], cached));
        Assert.False(set.AuditPassed);
    }
}
