using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The disk-space check on the copy screen (#206).
///
/// The restore screen has warned since #182; the copy - which ends in exactly the same RESTORE on
/// the target - did not. So the screen where somebody pays the least attention was the one that
/// never warned, and a copy without room fails in the restore half: source backup already taken,
/// target database already dropped by WITH REPLACE.
///
/// A copy has no backup to FILELISTONLY when the check should run, and needs none - the database is
/// live on the source, so its own catalog answers up front. And because the copy's restore carries
/// no MOVE clauses, the files land at the paths the source recorded, which is what makes the
/// source's own drive letters the right volumes to ask the target about.
/// </summary>
public class CopySpaceCheckTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static ServerConnection Server(string name) =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static FileMoveOption File_(string logical, string path, long size) => new()
    {
        LogicalName = logical,
        PhysicalName = path,
        NewPhysicalName = path,
        SizeBytes = size
    };

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server("SRV01"));
        store.Config.Servers.Add(Server("SRV02"));
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService
        {
            DatabaseFiles =
            [
                File_("MyDb", @"D:\SQL\Data\MyDb.mdf", 40 * GB),
                File_("MyDb_log", @"L:\SQL\Log\MyDb_log.ldf", 10 * GB)
            ],
            VolumeFreeSpace = new Dictionary<string, long>
            {
                [@"D:\"] = 100 * GB,
                [@"L:\"] = 50 * GB
            }
        };

        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers[0];
        vm.TargetServer = vm.Servers[1];
        vm.SourceDatabases = new ObservableCollection<string>(["MyDb"]);
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb";
        vm.Container = vm.Containers[0];

        return (vm, sql);
    }

    /// <summary>Generating the scripts asks the question; room enough reports without alarm.</summary>
    [Fact]
    public async Task GeneratingReportsTheVolumesWhenEverythingFits()
    {
        var (vm, _) = New();

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(2, vm.VolumeSpace.Count);
        Assert.All(vm.VolumeSpace, v => Assert.True(v.Fits));
        Assert.False(vm.HasSpaceWarning);
    }

    /// <summary>The one this exists for: the target has less room than the copy needs.</summary>
    [Fact]
    public async Task ATargetWithoutRoomWarnsBeforeAnythingRuns()
    {
        var (vm, sql) = New();
        sql.VolumeFreeSpace[@"D:\"] = 10 * GB;

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(vm.HasSpaceWarning);
        Assert.Contains(@"D:\", vm.SpaceWarning);
    }

    /// <summary>
    /// The volumes checked are the SOURCE's paths against the TARGET's free space - the copy's
    /// restore carries no MOVE clauses, so that is where the files will actually land.
    /// </summary>
    [Fact]
    public async Task TheVolumesCheckedAreWhereTheFilesWillLand()
    {
        var (vm, _) = New();

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.Contains(vm.VolumeSpace, v => v.Volume.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.VolumeSpace, v => v.Volume.StartsWith(@"L:\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Failing to answer is not a warning. An instance that will not report files or volumes has
    /// said nothing, and silence must not read as "the copy will fail".
    /// </summary>
    [Fact]
    public async Task AnInstanceThatWillNotAnswerRaisesNoAlarm()
    {
        var (vm, sql) = New();
        sql.DatabaseFilesThrows = new InvalidOperationException("VIEW SERVER STATE denied");

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(vm.HasSpaceWarning);
        Assert.Empty(vm.VolumeSpace);
        Assert.False(vm.HasError);
    }

    /// <summary>
    /// A drive the target never reports warns rather than passing - dm_os_volume_stats only
    /// describes volumes that host database files, so "not reported" routinely means "the target
    /// has no such drive". For a copy that is the classic layout mismatch: the source keeps logs
    /// on L:\ and the target has no L:\ at all, and without MOVE clauses the restore aims at it.
    /// </summary>
    [Fact]
    public async Task ADriveTheTargetDoesNotHaveWarns()
    {
        var (vm, sql) = New();
        sql.VolumeFreeSpace.Remove(@"L:\");

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(vm.HasSpaceWarning);
        Assert.Contains(@"L:\", vm.SpaceWarning);

        // Absent is said as absent - "0.0 B free" was the wrong sentence, because the fix is a
        // MOVE clause or a different target, not freeing space.
        Assert.Contains(@"no L:\ volume", vm.SpaceWarning);
        Assert.Contains("MOVE", vm.SpaceWarning);
    }
}
