using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Restoring a backup file that no configured instance's msdb knows (#203).
///
/// The classic case in the job: a vendor sends a .bak, or the file outlived the server that took
/// it. Until this, neither medium could reach it - blob obviously not, and the shared path reads a
/// source instance's history, which by definition does not contain a backup nobody here took.
///
/// The file's own headers are the only account of what it holds, and they carry the same fields
/// msdb records - database, type, LSNs, position. So the reader maps headers onto the same
/// BackupHistoryEntry the shared path uses, and everything downstream is shared machinery: ToSets,
/// the LSN chain, FROM DISK, the target-side readability preflight.
/// </summary>
public class AdHocFileRestoreTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static ServerConnection Server(string name = "SRV01") =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static BackupHistoryEntry Header(
        string file, BackupType type, int position = 1, int familyCount = 1,
        decimal? firstLsn = null, decimal? lastLsn = null,
        decimal? checkpointLsn = null, decimal? databaseBackupLsn = null,
        string database = "VendorDb", int minutesAfterT0 = 0) => new()
    {
        DatabaseName = database,
        ServerName = "THEIR-SERVER",
        Type = type,
        StartedAt = T0.AddMinutes(minutesAfterT0),
        FinishedAt = T0.AddMinutes(minutesAfterT0 + 1),
        FirstLsn = firstLsn,
        LastLsn = lastLsn,
        CheckpointLsn = checkpointLsn,
        DatabaseBackupLsn = databaseBackupLsn,
        Position = position,
        FamilyCount = familyCount,
        Files = [file]
    };

    private static (BackupInventoryViewModel vm, FakeSqlServerService sql) New()
    {
        var sql = new FakeSqlServerService();
        var vm = new BackupInventoryViewModel(
            new FakeBlobStorageService(), sql, TestLogs.Temp(), TestAuditStores.Temp());
        return (vm, sql);
    }

    // ── reading a file ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileIsReadByItsOwnHeaders()
    {
        var (vm, sql) = New();
        sql.FileHeaders[@"\\share\drop\VendorDb.bak"] =
            [Header(@"\\share\drop\VendorDb.bak", BackupType.Full, checkpointLsn: 100m)];

        await vm.LoadAsync(BackupLocation.AdHoc(Server(), [@"\\share\drop\VendorDb.bak"]));

        Assert.True(vm.BackupsLoaded);
        var set = Assert.Single(vm.AllSets);
        Assert.Equal("VendorDb", set.DatabaseName);
        Assert.Equal(BackupType.Full, set.Type);

        // FROM DISK, because the file model says where it lives - not a flag elsewhere.
        var file = Assert.Single(set.Files);
        Assert.True(file.IsOnDisk);
        Assert.Equal(@"\\share\drop\VendorDb.bak", file.RestoreDevice);
    }

    /// <summary>
    /// A full and the logs that follow it, listed together, chain by their own LSNs - the file
    /// names say nothing and are not consulted.
    /// </summary>
    [Fact]
    public async Task AFullAndItsLogsChainByLsn()
    {
        var (vm, sql) = New();
        sql.FileHeaders[@"D:\drop\full.bak"] =
            [Header(@"D:\drop\full.bak", BackupType.Full,
                    firstLsn: 100m, lastLsn: 150m, checkpointLsn: 120m)];
        sql.FileHeaders[@"D:\drop\log1.trn"] =
            [Header(@"D:\drop\log1.trn", BackupType.TransactionLog,
                    firstLsn: 150m, lastLsn: 200m, databaseBackupLsn: 120m, minutesAfterT0: 30)];

        await vm.LoadAsync(BackupLocation.AdHoc(Server(), [@"D:\drop\full.bak", @"D:\drop\log1.trn"]));

        Assert.Equal(2, vm.AllSets.Count);
        Assert.Contains(vm.AllSets, s => s.Type == BackupType.Full);
        Assert.Contains(vm.AllSets, s => s.Type == BackupType.TransactionLog);
    }

    /// <summary>
    /// A file holds several backups whenever NOINIT appended to it, and each is its own restore
    /// point carrying its position - restoring "the file" without saying which would silently
    /// mean the oldest.
    /// </summary>
    [Fact]
    public async Task AMultiBackupFileYieldsOneSetPerPosition()
    {
        var (vm, sql) = New();
        sql.FileHeaders[@"D:\drop\appended.bak"] =
        [
            Header(@"D:\drop\appended.bak", BackupType.Full, position: 1, checkpointLsn: 100m),
            Header(@"D:\drop\appended.bak", BackupType.Full, position: 2, checkpointLsn: 200m, minutesAfterT0: 60)
        ];

        await vm.LoadAsync(BackupLocation.AdHoc(Server(), [@"D:\drop\appended.bak"]));

        Assert.Equal(2, vm.AllSets.Count);
        Assert.Equal([1, 2], vm.AllSets.Select(s => s.Position ?? 0).OrderBy(p => p));
    }

    /// <summary>The generated RESTORE says WITH FILE = n, or SQL Server quietly restores position 1.</summary>
    [Fact]
    public void TheScriptNamesThePosition()
    {
        var generator = new RestoreScriptGenerator();
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "VendorDb_p2",
                DatabaseName = "VendorDb",
                Type = BackupType.Full,
                Position = 2,
                Timestamp = T0,
                Files =
                [
                    new BackupFileInfo
                    {
                        BlobName = "appended.bak",
                        LocalPath = @"D:\drop\appended.bak",
                        Type = BackupType.Full
                    }
                ]
            }
        };

        var script = generator.Generate(chain, new RestoreOptions { TargetDatabaseName = "VendorDb" });

        Assert.Contains(@"FROM DISK = N'D:\drop\appended.bak'", script);
        Assert.Contains("FILE = 2,", script);
    }

    /// <summary>A chain with no position says nothing about FILE - existing scripts do not change.</summary>
    [Fact]
    public void NoPositionMeansNoFileClause()
    {
        var generator = new RestoreScriptGenerator();
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "MyDb",
                DatabaseName = "MyDb",
                Type = BackupType.Full,
                Timestamp = T0,
                Files =
                [
                    new BackupFileInfo
                    {
                        BlobName = "MyDb.bak",
                        BlobUrl = "https://acct.blob.core.windows.net/backups/MyDb.bak",
                        Type = BackupType.Full
                    }
                ]
            }
        };

        var script = generator.Generate(chain, new RestoreOptions { TargetDatabaseName = "MyDb" });

        Assert.DoesNotContain("FILE =", script);
    }

    // ── refusals, by name ───────────────────────────────────────────────────────

    /// <summary>
    /// A stripe cannot be restored alone, and HEADERONLY on one member happily describes the whole
    /// set - FamilyCount is the only tell, so it refuses loudly rather than offering a restore the
    /// media cannot deliver.
    /// </summary>
    [Fact]
    public async Task AStripeMemberIsRefusedWithAnExplanation()
    {
        var (vm, sql) = New();
        sql.FileHeaders[@"D:\drop\stripe1of3.bak"] =
            [Header(@"D:\drop\stripe1of3.bak", BackupType.Full, familyCount: 3)];

        await vm.LoadAsync(BackupLocation.AdHoc(Server(), [@"D:\drop\stripe1of3.bak"]));

        Assert.False(vm.BackupsLoaded);
        Assert.True(vm.HasError);
        Assert.Contains("stripe", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", vm.StatusMessage);
    }

    /// <summary>
    /// One unreadable file fails the whole load, by name. Reading three files and quietly showing
    /// two is how somebody restores "everything" minus the log that carried the chain to the
    /// point they wanted.
    /// </summary>
    [Fact]
    public async Task OneUnreadableFileFailsTheLoadByName()
    {
        var (vm, sql) = New();
        sql.FileHeaders[@"D:\drop\full.bak"] =
            [Header(@"D:\drop\full.bak", BackupType.Full)];
        // log1.trn deliberately not present in the fake.

        await vm.LoadAsync(BackupLocation.AdHoc(Server(), [@"D:\drop\full.bak", @"D:\drop\log1.trn"]));

        Assert.False(vm.BackupsLoaded);
        Assert.True(vm.HasError);
        Assert.Contains("log1.trn", vm.StatusMessage);
    }

    // ── the location itself ─────────────────────────────────────────────────────

    [Fact]
    public void TheSameFilesReadViaADifferentServerAreADifferentPlace()
    {
        var srv1 = Server("SRV01");
        var srv2 = Server("SRV02");

        var viaOne = BackupLocation.AdHoc(srv1, [@"D:\drop\full.bak"]);
        var viaTwo = BackupLocation.AdHoc(srv2, [@"D:\drop\full.bak"]);

        // A local drive letter names a different disk on every machine.
        Assert.False(viaOne.SamePlaceAs(viaTwo));
        Assert.True(viaOne.SamePlaceAs(BackupLocation.AdHoc(srv1, [@"D:\drop\full.bak"])));
    }

    [Fact]
    public void ALocationNamesItsFile()
    {
        var one = BackupLocation.AdHoc(Server(), [@"D:\drop\VendorDb.bak"]);
        var several = BackupLocation.AdHoc(Server(), [@"D:\a.bak", @"D:\b.trn", @"D:\c.trn"]);

        Assert.Equal("VendorDb.bak", one.Describe());
        Assert.Equal("3 backup files", several.Describe());
    }

    // ── the restore screen ──────────────────────────────────────────────────────

    [Fact]
    public void ThePathsBoxFeedsTheLocation()
    {
        var vm = Restore();
        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = "D:\\drop\\full.bak\r\n\r\n  D:\\drop\\log1.trn  \r\n";

        var location = vm.CurrentLocation;

        Assert.NotNull(location);
        Assert.True(location!.IsAdHocFile);
        Assert.Equal([@"D:\drop\full.bak", @"D:\drop\log1.trn"], location.FilePaths);
    }

    [Fact]
    public void WithNoPathsThereIsNothingToLoad()
    {
        var vm = Restore();
        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = "   ";

        Assert.Null(vm.CurrentLocation);
    }

    /// <summary>Same mode gate as the shared path: both are FROM DISK with an instance asked.</summary>
    [Fact]
    public void EditingThePathsClearsWhatWasLoaded()
    {
        var vm = Restore();
        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.Inventory.BackupsLoaded = true;

        vm.AdHocPathsText = @"D:\drop\other.bak";

        Assert.False(vm.Inventory.BackupsLoaded);
    }

    private static RestoreViewModel Restore()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            new FakeRestoreHistoryStore(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };

        vm.RefreshContainers();
        return vm;
    }
}
