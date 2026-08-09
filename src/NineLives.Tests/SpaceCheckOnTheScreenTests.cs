using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The space check as the Restore screen runs it (#32).
///
/// The behaviour that needs holding down is what happens when the check CANNOT be made. This is a
/// courtesy check over somebody else's storage, and an instance that will not report its volumes -
/// permissions, an edition that does not expose the DMV - must not turn that into a frightening
/// message about a restore that is very probably fine.
/// </summary>
public class SpaceCheckOnTheScreenTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (RestoreViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = "https://acct.blob.core.windows.net/backups" });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    Type = BackupType.Full,
                    InferredDatabaseName = "MyDb",
                    InferredServerName = "SRV01",
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        var sql = new FakeSqlServerService
        {
            FileList =
            [
                new FileMoveOption
                {
                    LogicalName = "MyDb", Type = "ROWS",
                    PhysicalName = @"E:\Source\MyDb.mdf",
                    NewPhysicalName = @"D:\Data\MyDb.mdf",
                    SizeBytes = 100 * Gb
                }
            ]
        };

        var vm = new RestoreViewModel(
            blob, sql, new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            TestLogs.Temp(), new FakeRestoreHistoryStore(), TestAuditStores.Temp());

        vm.RefreshContainers();
        vm.ConnectedServer = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
        vm.IsConnectedToServer = true;

        // Where the files will actually land. The screen rewrites every NewPhysicalName to these
        // when it reads the logical names, so THESE are the volumes the check must measure - not
        // the paths recorded inside the backup, which belong to the source machine.
        vm.MoveDataFilePath = @"D:\Data\MyDb.mdf";
        vm.MoveLogFilePath = @"D:\Logs\MyDb_log.ldf";

        return (vm, sql);
    }

    private static async Task ReadyAsync(RestoreViewModel vm)
    {
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();
        await vm.FetchLogicalNamesCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ARestoreThatDoesNotFitIsWarnedAbout()
    {
        var (vm, sql) = New();
        sql.VolumeFreeSpace = new Dictionary<string, long> { [@"D:\"] = 10 * Gb };

        await ReadyAsync(vm);

        Assert.True(vm.HasSpaceWarning);
        Assert.Contains("short by", vm.SpaceWarning);
    }

    [Fact]
    public async Task ARestoreThatFitsSaysNothing()
    {
        var (vm, sql) = New();
        sql.VolumeFreeSpace = new Dictionary<string, long> { [@"D:\"] = 500 * Gb };

        await ReadyAsync(vm);

        Assert.False(vm.HasSpaceWarning,
            "volumes: " + string.Join(" | ", vm.VolumeSpace.Select(v => v.Describe)));
        Assert.True(vm.VolumeSpace.All(v => v.Fits));
    }

    /// <summary>
    /// The part worth guarding. Not being able to ask is not the same as the answer being no - and
    /// unlike the shared-path readability check, where refusing was right, here a false alarm about
    /// somebody else's storage is the greater harm. Logged, not shown.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatWillNotReportItsVolumesRaisesNoAlarm()
    {
        var (vm, sql) = New();
        sql.VolumeCheckThrows = new InvalidOperationException("no permission on the DMV");

        await ReadyAsync(vm);

        Assert.False(vm.HasSpaceWarning);
        Assert.False(vm.HasError);
        Assert.Empty(vm.VolumeSpace);
    }

    /// <summary>
    /// And it comes with the file list rather than as a separate step: the sizes were in that same
    /// result set, so this costs one query rather than another read of the backup.
    /// </summary>
    [Fact]
    public async Task TheCheckHappensWhenTheFileNamesAreRead()
    {
        var (vm, sql) = New();
        sql.VolumeFreeSpace = new Dictionary<string, long> { [@"D:\"] = 500 * Gb };

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        Assert.Empty(vm.VolumeSpace);

        await vm.FetchLogicalNamesCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.VolumeSpace);
    }
}
