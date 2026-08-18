using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Leaving the Restore screen and coming back does not throw the work away (#478).
///
/// The third screen with the fault behind #457 and #476, and the one where it costs most.
/// RefreshContainers runs on every visit - deliberately, so a container or server added elsewhere
/// is selectable here - and it re-points four selections at entries from a freshly loaded config.
/// Every one of those is a different OBJECT, because the store deserializes on each read, so all
/// four assignments counted as changes and the handlers did what they should do when somebody
/// genuinely picks something else:
///
///   SelectedContainer  -> ClearLoadedBackups: the listing, the timeline, and Execute disarmed
///   SourceServer       -> the same
///   Gap.SourceServer   -> the gap answer and the arrival comparison thrown away (#451)
///   SelectedTargetServer -> a fresh connection to the target instance, per visit
///
/// This is the screen somebody is on at 2am with production down. Reading a container of 98 headers
/// takes minutes; glancing at Browse Backups to check a file name emptied it, silently, and the
/// only thing on screen said "Load Backups". The gap panel is worse: the whole point of the second
/// check is comparing against what was missing before, and returning to the screen is exactly what
/// somebody does after going away to run the copy script.
/// </summary>
public class RestoreScreenSurvivesNavigationTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 22, 0, 0);

    private static (RestoreViewModel vm, FakeSqlServerService sql, FakeCredentialStore store) Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c2",
            Name = "archive",
            ContainerUrl = "https://acct.blob.core.windows.net/archive"
        });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s2", Name = "SRV02", ServerName = "SRV02" });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/MyDb/MyDb_FULL_20260818_220000.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/MyDb_FULL_20260818_220000.bak",
                    Type = BackupType.Full,
                    InferredDatabaseName = "MyDb",
                    InferredServerName = "SRV01",
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        var sql = new FakeSqlServerService();
        var vm = new RestoreViewModel(
            blob, sql, new BackupChainBuilder(), new RestoreScriptGenerator(),
            store, new FakeOperationHistoryStore(), TestLogs.Temp(), TestAuditStores.Temp());

        return (vm, sql, store);
    }

    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> LoadedAsync()
    {
        var (vm, sql, _) = Screen();
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Inventory.AllSets);
        return (vm, sql);
    }

    // ── the listing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported shape: load a container, glance at another screen, come back to an empty one.
    /// </summary>
    [Fact]
    public async Task RevisitingTheScreenKeepsTheLoadedBackups()
    {
        var (vm, _) = await LoadedAsync();

        // What navigating away and back does.
        vm.RefreshContainers();

        Assert.NotEmpty(vm.Inventory.AllSets);
    }

    /// <summary>
    /// Arming is a five-second window somebody opens deliberately after reading a banner naming
    /// the server and the database. Disarming it because navigation re-read the config is not the
    /// safety this was written for - it is the button going dead under the cursor.
    /// </summary>
    [Fact]
    public async Task RevisitingTheScreenDoesNotDisarmExecute()
    {
        var (vm, _) = await LoadedAsync();
        vm.Execution.Arm();

        vm.RefreshContainers();

        Assert.True(vm.Execution.IsArmed);
    }

    /// <summary>
    /// The guard. A container genuinely changed still clears everything, and the comment on the
    /// handler explains why in full: the credential panel targeted the new container while the
    /// script still restored from the old one's URLs, so Execute stayed armed and failed with
    /// Msg 3201 after WITH REPLACE had already dropped the target.
    /// </summary>
    [Fact]
    public async Task ChoosingADifferentContainerStillClearsTheListing()
    {
        var (vm, _) = await LoadedAsync();

        vm.SelectedContainer = vm.Containers.First(c => c.Name == "archive");

        Assert.Empty(vm.Inventory.AllSets);
    }

    /// <summary>And the same for the shared-path source, which clears for the same reason.</summary>
    [Fact]
    public async Task ChoosingADifferentSourceServerStillClearsTheListing()
    {
        var (vm, _) = await LoadedAsync();
        vm.SourceServer = vm.SourceServers.First(s => s.Id == "s1");
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.SourceServer = vm.SourceServers.First(s => s.Id == "s2");

        Assert.Empty(vm.Inventory.AllSets);
    }

    // ── the gap answer (#451) ───────────────────────────────────────────────────

    /// <summary>
    /// Worse here than anywhere: going away is the workflow. The panel names the logs that never
    /// reached the container and hands over a script to copy them, somebody runs it on the source
    /// machine, and comes back to press "rescan and check again". Coming back is what cleared the
    /// answer it was about to be compared with, so the second check reported everything as still
    /// missing.
    /// </summary>
    [Fact]
    public async Task RevisitingTheScreenKeepsTheGapAnswer()
    {
        var (vm, sql, _) = Screen();
        sql.BackupHistory =
        [
            new BackupHistoryEntry
            {
                DatabaseName = "MyDb",
                Type = BackupType.TransactionLog,
                StartedAt = T0,
                Files = [@"E:\SQLLogs\MyDb_20260818_220000.trn"]
            }
        ];

        vm.RefreshContainers();
        vm.Gap.SourceServer = vm.Gap.Servers.First(s => s.Id == "s1");
        await vm.Gap.CheckCommand.ExecuteAsync(
            new GapCheckRequest("MyDb", vm.Containers[0], []));
        Assert.True(vm.Gap.HasGap);

        vm.RefreshContainers();

        Assert.True(vm.Gap.HasGap);
        Assert.NotEmpty(vm.Gap.Locations);
    }

    /// <summary>
    /// The guard again. Measuring one instance's backups against another's would report every
    /// file as arrived, so a real change of source still throws the comparison away.
    /// </summary>
    [Fact]
    public async Task ChoosingADifferentGapSourceStillClearsTheAnswer()
    {
        var (vm, sql, _) = Screen();
        sql.BackupHistory =
        [
            new BackupHistoryEntry
            {
                DatabaseName = "MyDb",
                Type = BackupType.TransactionLog,
                StartedAt = T0,
                Files = [@"E:\SQLLogs\MyDb_20260818_220000.trn"]
            }
        ];

        vm.RefreshContainers();
        vm.Gap.SourceServer = vm.Gap.Servers.First(s => s.Id == "s1");
        await vm.Gap.CheckCommand.ExecuteAsync(
            new GapCheckRequest("MyDb", vm.Containers[0], []));

        vm.Gap.SourceServer = vm.Gap.Servers.First(s => s.Id == "s2");

        Assert.False(vm.Gap.HasGap);
        Assert.Empty(vm.Gap.Locations);
    }

    // ── the target connection ───────────────────────────────────────────────────

    /// <summary>
    /// A connection per visit to a production instance, for a target that was already connected.
    /// Nothing on screen changes, so nothing says it is happening.
    /// </summary>
    [Fact]
    public async Task RevisitingTheScreenDoesNotReconnectToTheTarget()
    {
        var (vm, sql, _) = Screen();
        vm.SelectedTargetServer = vm.TargetServers.First(s => s.Id == "s1");
        await vm.WaitForTargetConnectionForTests();
        Assert.Single(sql.Connected);

        vm.RefreshContainers();
        await vm.WaitForTargetConnectionForTests();

        Assert.Single(sql.Connected);
    }

    /// <summary>
    /// A target the user actually changes to still takes effect everywhere it matters.
    ///
    /// Not by connecting a second time - the probe has always been skipped once something is
    /// connected (#420), and that is deliberate. What has to follow the change is the server every
    /// call on this screen runs against, and the name the confirmation banner reads (#479).
    /// </summary>
    [Fact]
    public async Task ChoosingADifferentTargetStillTakesEffect()
    {
        var (vm, sql, _) = Screen();
        vm.SelectedTargetServer = vm.TargetServers.First(s => s.Id == "s1");
        await vm.WaitForTargetConnectionForTests();
        Assert.Single(sql.Connected);

        vm.SelectedTargetServer = vm.TargetServers.First(s => s.Id == "s2");
        await vm.WaitForTargetConnectionForTests();

        Assert.Equal("SRV02", vm.ConnectedServer?.ServerName);
        Assert.Equal("SRV02", vm.ConnectedServerName);
    }
}
