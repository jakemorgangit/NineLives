using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A file the filename could not place going from invisible to restorable (#130).
///
/// The end-to-end claim. Reading a header is only worth anything if what it settles reaches the
/// working set - the sets are keyed on the database and type the header has just corrected, so
/// nothing improves unless the inventory regroups afterwards.
/// </summary>
public class IdentifyThroughTheInventoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupLocation Container() => BackupLocation.Blob(new BlobContainerConfig
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    });

    /// <summary>A file whose name says nothing: no type, no database, no timestamp.</summary>
    private static BackupFileInfo Unplaceable(string name) => new()
    {
        BlobName = name,
        BlobUrl = $"https://acct.blob.core.windows.net/backups/{name}",
        Type = BackupType.Unknown,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    private static (BackupInventoryViewModel vm, FakeSqlServerService sql) New(params BackupFileInfo[] files)
    {
        var blob = new FakeBlobStorageService { Files = files.ToList() };
        var sql = new FakeSqlServerService();
        return (new BackupInventoryViewModel(blob, sql, TestLogs.Temp(), TestAuditStores.Temp()), sql);
    }

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    // ── it is counted, and it is offered ────────────────────────────────────────

    [Fact]
    public async Task FilesTheFilenameCouldNotPlaceAreCounted()
    {
        var (vm, _) = New(Unplaceable("mystery1.bak"), Unplaceable("mystery2.bak"));

        await vm.LoadAsync(Container());

        Assert.Equal(2, vm.UnclassifiedCount);
        Assert.True(vm.HasUnclassified);
    }

    /// <summary>
    /// Until the header is read they are not merely unlabelled - they are absent. An unknown type
    /// never enters the fulls collection, so there is no restore point to select.
    /// </summary>
    [Fact]
    public async Task BeforeTheHeaderIsReadTheyAreInvisible()
    {
        var (vm, _) = New(Unplaceable("mystery.bak"));

        await vm.LoadAsync(Container());
        vm.SelectedDatabaseName = "MyDb";

        Assert.Empty(vm.WorkingSet);
    }

    /// <summary>The whole point: after the header, the file is a restorable set.</summary>
    [Fact]
    public async Task AfterTheHeaderIsReadTheyReachTheWorkingSet()
    {
        var (vm, sql) = New(Unplaceable("mystery.bak"));
        sql.Header = new BackupFileInfo
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            BackupTypeCode = 1,
            BackupStartDate = T0.AddHours(-3),
            CheckpointLsn = 100,
            LastLsn = 200
        };

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());

        vm.SelectedDatabaseName = "MyDb";

        var set = Assert.Single(vm.WorkingSet);
        Assert.Equal(BackupType.Full, set.Type);
        Assert.Equal("MyDb", set.DatabaseName);
        Assert.Equal(0, vm.UnclassifiedCount);
    }

    /// <summary>
    /// And it arrives carrying its LSNs, so the chain builder can pair it definitively rather than
    /// by proximity in time - the second reason the round trip is worth making.
    /// </summary>
    [Fact]
    public async Task TheIdentifiedSetCarriesItsLsns()
    {
        var (vm, sql) = New(Unplaceable("mystery.bak"));
        sql.Header = new BackupFileInfo
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            BackupTypeCode = 1,
            BackupStartDate = T0.AddHours(-3),
            CheckpointLsn = 100,
            LastLsn = 200
        };

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());
        vm.SelectedDatabaseName = "MyDb";

        var set = Assert.Single(vm.WorkingSet);

        Assert.True(set.HasLsns);
        Assert.Equal(100, set.CheckpointLsn);
    }

    /// <summary>
    /// The header's own record of when the backup ran replaces the fallback, which for a file whose
    /// name says nothing is the blob's LastModified - the moment the UPLOAD finished, in UTC.
    /// </summary>
    [Fact]
    public async Task TheIdentifiedSetIsTimedFromTheHeaderRatherThanTheUpload()
    {
        var (vm, sql) = New(Unplaceable("mystery.bak"));
        sql.Header = new BackupFileInfo
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            BackupTypeCode = 1,
            BackupStartDate = T0.AddHours(-3)
        };

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());
        vm.SelectedDatabaseName = "MyDb";

        var set = Assert.Single(vm.WorkingSet);

        Assert.Equal(T0.AddHours(-3), set.Timestamp);
        Assert.Equal(BackupTimestampSource.BackupHeader, set.TimestampSource);
        Assert.False(set.IsTimestampApproximate);
    }

    // ── what it does not do ─────────────────────────────────────────────────────

    /// <summary>
    /// Only the unplaceable ones. That scoping is the whole design - one HEADERONLY per file is a
    /// network read, and across a real container that is thousands of round trips.
    /// </summary>
    [Fact]
    public async Task FilesTheFilenameAlreadyPlacedAreNotAskedAbout()
    {
        var placed = new BackupFileInfo
        {
            BlobName = "FULL/SRV01/MyDb/MyDb_20260801_220000.bak",
            BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/MyDb_20260801_220000.bak",
            Type = BackupType.Full,
            InferredDatabaseName = "MyDb",
            InferredServerName = "SRV01",
            LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
        };

        var (vm, sql) = New(placed, Unplaceable("mystery.bak"));
        sql.Header = new BackupFileInfo { DatabaseName = "MyDb", Type = BackupType.Full, BackupTypeCode = 1 };

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());

        Assert.Single(sql.HeaderReads);
    }

    [Fact]
    public async Task WithNothingUnplaceableNoHeaderIsRead()
    {
        var (vm, sql) = New();

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());

        Assert.Empty(sql.HeaderReads);
        Assert.False(vm.HasUnclassified);
    }

    /// <summary>
    /// A container that answers nothing useful is reported honestly rather than as a success. The
    /// files are still unplaceable, and saying otherwise would send somebody looking for them on a
    /// timeline they are not on.
    /// </summary>
    [Fact]
    public async Task HeadersThatSettleNothingSaySo()
    {
        var (vm, sql) = New(Unplaceable("mystery.bak"));
        sql.Header = null;

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());

        Assert.Equal(1, vm.UnclassifiedCount);
        Assert.Contains("none of them", vm.StatusMessage);
    }

    /// <summary>
    /// Nothing read from an instance's msdb ever arrives unplaced - msdb recorded the database, the
    /// type and the LSNs - so the offer never appears on that medium.
    /// </summary>
    [Fact]
    public async Task BackupsReadFromMsdbAreNeverUnplaceable()
    {
        var sql = new FakeSqlServerService
        {
            BackupHistory =
            [
                new BackupHistoryEntry
                {
                    DatabaseName = "MyDb", ServerName = "SRV01", Type = BackupType.Full,
                    StartedAt = T0, FinishedAt = T0.AddMinutes(2),
                    Files = [@"\\nas01\sql\full.bak"]
                }
            ]
        };

        var vm = new BackupInventoryViewModel(new FakeBlobStorageService(), sql, TestLogs.Temp(), TestAuditStores.Temp());

        await vm.LoadAsync(BackupLocation.Shared(Server()));

        Assert.False(vm.HasUnclassified);
    }
}
