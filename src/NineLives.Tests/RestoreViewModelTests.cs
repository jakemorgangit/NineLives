using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The first tests of a ViewModel rather than a service (#41).
///
/// Everything here used to be unreachable from a test: the ViewModels built their own
/// <c>BlobStorageService</c>, <c>SqlServerService</c> and <c>CredentialStore</c>, so exercising any
/// of it meant a real container, a real instance and the user's own credential vault. That is why
/// the restore executing against a name-resolved server, and the credential being recreated on
/// every run, both reached a release unnoticed.
///
/// Nothing in this file touches the network, a SQL Server, the credential vault or the config file.
/// </summary>
public class RestoreViewModelTests
{
    private readonly FakeBlobStorageService _blob = new();
    private readonly FakeSqlServerService _sql = new();
    private readonly FakeCredentialStore _store = new();

    private RestoreViewModel NewViewModel() => new(
        _blob, _sql, new BackupChainBuilder(), new RestoreScriptGenerator(), _store,
        new OperationLog(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ninelives-vm-tests", Guid.NewGuid().ToString("n"))));

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BackupFileInfo File(
        string blobName, BackupType type, DateTime lastModified, string database = "MyDb")
        => new()
        {
            BlobName = blobName,
            BlobUrl = $"https://mystorageaccount.blob.core.windows.net/backups/{blobName}",
            Type = type,
            InferredServerName = "SRV01",
            InferredDatabaseName = database,
            SizeBytes = 1000,
            LastModified = new DateTimeOffset(lastModified, TimeSpan.Zero)
        };

    /// <summary>A full at T0 plus <paramref name="logCount"/> logs at hourly intervals after it.</summary>
    private static List<BackupFileInfo> FullPlusLogs(int logCount)
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/MyDb/20260110_220000.bak", BackupType.Full, T0)
        };

        for (int i = 1; i <= logCount; i++)
        {
            var stamp = T0.AddHours(i);
            files.Add(File(
                $"LOG/SRV01/MyDb/{stamp:yyyyMMdd_HHmmss}.trn",
                BackupType.TransactionLog,
                stamp));
        }

        return files;
    }

    private static BlobContainerConfig Container() => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
    };

    // ── loading and chain selection ─────────────────────────────────────────────

    [Fact]
    public async Task LoadingBuildsRestorePointsAndSelectsTheMostRecent()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.BackupsLoaded);
        Assert.True(vm.Timeline.HasPoints);

        // A full plus three logs: the full itself, then one point per log.
        Assert.Equal(4, vm.Timeline.Points.Count);

        // The default selection is the latest point, which is what someone restoring after an
        // incident almost always wants.
        Assert.Equal(T0.AddHours(3), vm.Timeline.SelectedPoint!.Timestamp);
        Assert.True(vm.HasValidChain);
    }

    [Fact]
    public async Task SelectingAPointBuildsTheChainThatReachesIt()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // The second log: full + the logs up to and including it, and nothing after.
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Single(
            p => p.Timestamp == T0.AddHours(2) && p.Type == BackupType.TransactionLog);

        Assert.NotNull(vm.RestoreChain);
        Assert.Equal(BackupType.Full, vm.RestoreChain!.FullSet.Type);
        Assert.Equal(2, vm.RestoreChain.LogSets.Count);
        Assert.DoesNotContain(vm.RestoreChain.LogSets, s => s.Timestamp > T0.AddHours(2));
    }

    [Fact]
    public async Task ChangingTheSelectionRegeneratesTheScriptForTheNewChain()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.TargetDatabaseName = "MyDb_Restored";

        vm.Timeline.SelectedPoint = vm.Timeline.Points.First(p => p.Type == BackupType.Full);
        var fullOnly = vm.GeneratedScript;

        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();
        var withLogs = vm.GeneratedScript;

        Assert.DoesNotContain("RESTORE LOG", fullOnly);
        Assert.Contains("RESTORE LOG", withLogs);
    }

    [Fact]
    public async Task AContainerWithNoFullBackupOffersNoRestorePointAndSaysWhy()
    {
        var vm = NewViewModel();
        _blob.Files =
        [
            File("LOG/SRV01/MyDb/20260110_230000.trn", BackupType.TransactionLog, T0.AddHours(1))
        ];
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.Timeline.HasPoints);

        // The explanation has to survive the rest of the load. It used to be overwritten by
        // "Loaded 1 files in 1 backup set(s)...", leaving an empty timeline and a status bar
        // reporting success.
        Assert.True(vm.HasError);
        Assert.Contains("full backup", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // And the inventory panel says the same thing in the place that persists.
        Assert.True(vm.HasInventoryIssues);
    }

    // ── changing container ──────────────────────────────────────────────────────

    /// <summary>
    /// Everything on the Restore screen below the container dropdown came from that container.
    /// Switching it left the timeline, the chain and the script belonging to the previous one,
    /// while the credential panel and Create credential moved to the new one - so Execute stayed
    /// armed, pointed at the old container's URLs, and failed with Msg 3201 after WITH REPLACE had
    /// already dropped the target.
    /// </summary>
    [Fact]
    public async Task ChangingContainerDropsWhatTheLastOneLoaded()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.TargetDatabaseName = "MyDb_Restored";

        Assert.True(vm.BackupsLoaded);
        Assert.NotEmpty(vm.Timeline.Points);
        Assert.NotEmpty(vm.GeneratedScript);

        vm.SelectedContainer = Container();   // a different container

        Assert.False(vm.BackupsLoaded);
        Assert.Empty(vm.Timeline.Points);
        Assert.False(vm.Timeline.HasPoints);
        Assert.Null(vm.Timeline.SelectedPoint);
        Assert.Null(vm.RestoreChain);
        Assert.Empty(vm.GeneratedScript);
        Assert.False(vm.HasScript);
        Assert.Empty(vm.DiscoveredDatabases);
    }

    [Fact]
    public async Task ChangingContainerDisarmsExecute()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(1);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsConnectedToServer = true;
        vm.ConnectedServer = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        await vm.ExecuteScriptCommand.ExecuteAsync(null);
        Assert.True(vm.IsExecuteArmed);

        vm.SelectedContainer = Container();

        Assert.False(vm.IsExecuteArmed);
    }

    // ── changing database ───────────────────────────────────────────────────────

    /// <summary>
    /// Switching to a database with no restore points has to take the chain and the script with it.
    ///
    /// It did not. The timeline emptied and the error appeared, but the selected point belonged to
    /// the previous database and nothing cleared it - so a complete RESTORE stayed on screen,
    /// naming the previous database's backups, underneath a timeline with nothing on it and a
    /// message saying there was nothing to restore to. Found while splitting the timeline out
    /// (#115 seam 2).
    ///
    /// A database with logs but no full is not a contrived setup: it is what a container looks like
    /// when the fulls have aged out of the retention window.
    /// </summary>
    [Fact]
    public async Task SwitchingToADatabaseWithNoRestorePointsClearsTheChainAndTheScript()
    {
        var vm = NewViewModel();
        _blob.Files =
        [
            .. FullPlusLogs(3),
            File("LOG/SRV01/OtherDb/20260110_230000.trn", BackupType.TransactionLog, T0.AddHours(1), "OtherDb"),
            File("LOG/SRV01/OtherDb/20260111_000000.trn", BackupType.TransactionLog, T0.AddHours(2), "OtherDb")
        ];
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Timeline.SelectedPoint);
        Assert.NotEmpty(vm.GeneratedScript);

        // OtherDb has logs but no full, so there is nothing it can be restored to.
        vm.SelectedDatabaseName = "OtherDb";

        Assert.Empty(vm.Timeline.Points);
        Assert.Null(vm.Timeline.SelectedPoint);
        Assert.Null(vm.RestoreChain);
        Assert.Empty(vm.GeneratedScript);
        Assert.False(vm.HasScript);
        Assert.True(vm.HasError);
    }

    // ── narrowing the restore points (#27) ──────────────────────────────────────

    [Fact]
    public async Task LoadingADifferentDatabaseDoesNotInheritThePreviousFilters()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(5);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.Timeline.FromText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(2, vm.Timeline.Points.Count);

        // A range typed for one database would silently hide points belonging to the next.
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(6, vm.Timeline.Points.Count);
        Assert.Equal(string.Empty, vm.Timeline.FromText);
    }

    /// <summary>
    /// Reloading with the same database still selected has to pick up backups taken since - which
    /// is the whole reason someone presses Load again.
    ///
    /// The restore points were rebuilt only when the SELECTION changed. Loading again with the
    /// same server and database raised no property change, so the working set and the timeline
    /// kept the previous scan's contents and a backup taken in between never appeared.
    /// </summary>
    [Fact]
    public async Task ReloadingPicksUpBackupsTakenSinceTheLastScan()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(2);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Timeline.Points.Count);

        // A new log arrives in the container, and the user presses Load again.
        var fresh = T0.AddHours(3);
        _blob.Files = FullPlusLogs(3);
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Timeline.Points.Count);
        Assert.Contains(vm.Timeline.Points, p => p.Timestamp == fresh);
    }

    // ── filtering ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task PickingADatabaseNarrowsTheNextListingToItRatherThanRescanningEverything()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(1);
        vm.SelectedContainer = Container();

        // First load has no selection yet and must scan the whole container - the server and
        // database lists are built from what it finds.
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.Null(_blob.LastScope);

        vm.SelectedDatabaseName = "MyDb";
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.NotNull(_blob.LastScope);
        Assert.Equal("MyDb", _blob.LastScope!.DatabaseName);
        Assert.Equal("SRV01", _blob.LastScope.ServerName);
    }

    [Fact]
    public async Task AnInstanceScopesToItsHostBecauseThatIsWhatThePathHolds()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(1);
        foreach (var f in _blob.Files) f.InferredInstanceName = "PROD";
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.SelectedDatabaseName = "MyDb";
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // "SRV01\PROD" lives under "SRV01" in the container layout.
        Assert.Equal("SRV01", _blob.LastScope!.ServerName);
    }

    [Fact]
    public async Task AFailedListingSaysSoAndLeavesNothingLoaded()
    {
        var vm = NewViewModel();
        _blob.ListThrows = new InvalidOperationException("No SAS token found.");
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.BackupsLoaded);
        Assert.Contains("No SAS token found.", vm.StatusMessage);
    }
}
