using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Auditing a whole container rather than one database (#130).
///
/// The last piece of that issue, and the one its title actually asks about. It only became small
/// once the estimate, the cache, the progress and the Stop already existed - what is left is the
/// scope, and making sure the estimate and the run cannot disagree about it.
///
/// That last part is the whole risk here. At ~2.1s per set a database is a coffee and a container
/// can be most of an hour, so the estimate is the only thing standing between somebody and an
/// unexpected forty minutes. An estimate for one scope and a run over another would be worse than
/// no estimate at all.
/// </summary>
public class AuditWholeContainerTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupFileInfo File_(string database, string name) => new()
    {
        BlobName = $"FULL/SRV01/{database}/{name}",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/SRV01/{database}/{name}",
        ETag = $"\"{database}-{name}\"",
        Type = BackupType.Full,
        InferredDatabaseName = database,
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

    /// <summary>Three databases, one set each, so the two scopes are 1 and 3.</summary>
    private static (BackupInventoryViewModel vm, FakeSqlServerService sql) New()
    {
        var blob = new FakeBlobStorageService
        {
            Files =
            [
                File_("Sales", "Sales_20260801_220000.bak"),
                File_("Payroll", "Payroll_20260801_220000.bak"),
                File_("Archive", "Archive_20260801_220000.bak")
            ]
        };

        var sql = new FakeSqlServerService
        {
            // A header per file, or a Sales header answering for Payroll would report a mismatch
            // that is an artefact of the fake.
            HeaderForUrls = urls => new BackupFileInfo
            {
                DatabaseName = DatabaseIn(urls[0]),
                Type = BackupType.Full,
                BackupTypeCode = 1
            }
        };

        return (new BackupInventoryViewModel(blob, sql, TestLogs.Temp(), TestAuditStores.Temp()), sql);
    }

    private static string DatabaseIn(string url) => url.Split('/')[^2];

    // ── the scope ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheSwitchOnlyTheChosenDatabaseIsRead()
    {
        var (vm, sql) = New();

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "Sales";

        await vm.AuditAsync(Server());

        Assert.Single(sql.HeaderReads);
    }

    [Fact]
    public async Task WithTheSwitchEveryDatabaseInTheContainerIsRead()
    {
        var (vm, sql) = New();

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "Sales";
        vm.AuditWholeContainer = true;

        await vm.AuditAsync(Server());

        Assert.Equal(3, sql.HeaderReads.Count);
    }

    /// <summary>
    /// The wider scope does not need a database chosen at all - it is not about the selection, and
    /// requiring one would be a step that means nothing.
    /// </summary>
    [Fact]
    public async Task TheWiderScopeWorksWithNoDatabaseChosen()
    {
        var (vm, sql) = New();

        await vm.LoadAsync(Container());
        vm.AuditWholeContainer = true;

        Assert.True(vm.CanAudit);

        await vm.AuditAsync(Server());

        Assert.Equal(3, sql.HeaderReads.Count);
    }

    /// <summary>And without it, no selection means nothing to audit.</summary>
    [Fact]
    public async Task TheNarrowScopeStillNeedsADatabase()
    {
        var (vm, _) = New();

        await vm.LoadAsync(Container());

        Assert.False(vm.CanAudit);
    }

    // ── the estimate and the run agree ──────────────────────────────────────────

    /// <summary>
    /// The one that matters. The estimate is the only thing standing between somebody and an
    /// unexpected forty minutes, so an estimate for one scope and a run over another would be worse
    /// than no estimate at all.
    /// </summary>
    [Fact]
    public async Task TheEstimateCountsWhatTheRunWillActuallyRead()
    {
        var (vm, sql) = New();

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "Sales";

        Assert.Contains("1 backup header(s)", vm.AuditEstimate);

        vm.AuditWholeContainer = true;
        Assert.Contains("3 backup header(s)", vm.AuditEstimate);

        await vm.AuditAsync(Server());
        Assert.Equal(3, sql.HeaderReads.Count);
    }

    /// <summary>The wider scope says so, because forty minutes is not a thing to discover mid-run.</summary>
    [Fact]
    public async Task TheWiderEstimateSaysItCoversTheWholeContainer()
    {
        var (vm, _) = New();

        await vm.LoadAsync(Container());
        vm.AuditWholeContainer = true;

        Assert.Contains("Every database in this container", vm.AuditEstimate);
    }

    [Fact]
    public async Task TheSummarySaysWhichScopeItCovered()
    {
        var (vm, _) = New();

        await vm.LoadAsync(Container());
        vm.AuditWholeContainer = true;
        await vm.AuditAsync(Server());

        Assert.Contains("in this container", vm.AuditSummary);
    }

    // ── the cache carries across scopes ─────────────────────────────────────────

    /// <summary>
    /// Auditing one database and then the container reads only what is left. The cache is keyed on
    /// the blob, not on the scope that happened to reach it - which is what makes the wider scope
    /// approachable at all: run it once, then it only ever costs the new backups.
    /// </summary>
    [Fact]
    public async Task AuditingOneDatabaseFirstMakesTheContainerCheaper()
    {
        var store = TestAuditStores.Temp();

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                File_("Sales", "Sales_20260801_220000.bak"),
                File_("Payroll", "Payroll_20260801_220000.bak"),
                File_("Archive", "Archive_20260801_220000.bak")
            ]
        };

        var sql = new FakeSqlServerService
        {
            HeaderForUrls = urls => new BackupFileInfo
            { DatabaseName = DatabaseIn(urls[0]), Type = BackupType.Full, BackupTypeCode = 1 }
        };

        var vm = new BackupInventoryViewModel(blob, sql, TestLogs.Temp(), store);

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "Sales";
        await vm.AuditAsync(Server());
        Assert.Single(sql.HeaderReads);

        vm.AuditWholeContainer = true;
        await vm.AuditAsync(Server());

        // Two more, not three - Sales was already answered.
        Assert.Equal(3, sql.HeaderReads.Count);
    }
}
