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

    private static BackupFileInfo File(string blobName, BackupType type, DateTime lastModified)
        => new()
        {
            BlobName = blobName,
            BlobUrl = $"https://mystorageaccount.blob.core.windows.net/backups/{blobName}",
            Type = type,
            InferredServerName = "SRV01",
            InferredDatabaseName = "MyDb",
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
        Assert.True(vm.HasRestorePoints);

        // A full plus three logs: the full itself, then one point per log.
        Assert.Equal(4, vm.RestorePoints.Count);

        // The default selection is the latest point, which is what someone restoring after an
        // incident almost always wants.
        Assert.Equal(T0.AddHours(3), vm.SelectedRestorePoint!.Timestamp);
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
        vm.SelectedRestorePoint = vm.RestorePoints.Single(
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

        vm.SelectedRestorePoint = vm.RestorePoints.First(p => p.Type == BackupType.Full);
        var fullOnly = vm.GeneratedScript;

        vm.SelectedRestorePoint = vm.RestorePoints.Last();
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

        Assert.False(vm.HasRestorePoints);

        // The explanation has to survive the rest of the load. It used to be overwritten by
        // "Loaded 1 files in 1 backup set(s)...", leaving an empty timeline and a status bar
        // reporting success.
        Assert.True(vm.HasError);
        Assert.Contains("full backup", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // And the inventory panel says the same thing in the place that persists.
        Assert.True(vm.HasInventoryIssues);
    }

    // ── timeline maths ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TimelinePositionsSpanTheWholeTrack()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var ordered = vm.RestorePoints.OrderBy(p => p.Timestamp).ToList();
        Assert.Equal(0.0, ordered.First().TimelinePosition, 6);
        Assert.Equal(1.0, ordered.Last().TimelinePosition, 6);

        // Evenly spaced points must stay evenly spaced, and monotonic.
        Assert.All(ordered, p => Assert.InRange(p.TimelinePosition, 0.0, 1.0));
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].TimelinePosition >= ordered[i - 1].TimelinePosition);
    }

    [Fact]
    public async Task ASinglePointSitsInTheMiddleRatherThanAtZero()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(0);
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var only = Assert.Single(vm.RestorePoints);
        Assert.Equal(0.5, only.TimelinePosition, 6);
    }

    [Fact]
    public async Task PointsTooCloseToDrawSideBySideAreStackedIntoRows()
    {
        var vm = NewViewModel();

        // 60 hourly logs over a fixed track: the gap between neighbours falls below the
        // separation the row packer allows, so they cannot all sit on one row.
        _blob.Files = FullPlusLogs(60);
        vm.SelectedContainer = Container();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.RestorePoints.Max(p => p.Row) > 0,
            "Every point was placed on row 0, so they would be drawn on top of each other.");

        // The track grows to fit however many rows were needed, otherwise the upper rows are
        // drawn outside the control.
        Assert.True(vm.TimelineHeight > 50);
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
        Assert.NotEmpty(vm.RestorePoints);
        Assert.NotEmpty(vm.GeneratedScript);

        vm.SelectedContainer = Container();   // a different container

        Assert.False(vm.BackupsLoaded);
        Assert.Empty(vm.RestorePoints);
        Assert.False(vm.HasRestorePoints);
        Assert.Null(vm.SelectedRestorePoint);
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

    // ── narrowing the restore points (#27) ──────────────────────────────────────

    [Fact]
    public async Task TurningOffATypeRemovesThosePointsFromBothSelectors()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.RestorePoints.Count);

        vm.ShowLogPoints = false;

        // The list and the timeline are the same collection, so this is both.
        var only = Assert.Single(vm.RestorePoints);
        Assert.Equal(BackupType.Full, only.Type);
        Assert.Contains("Showing 1 of 4", vm.PointCountText);
    }

    [Fact]
    public async Task ADateRangeNarrowsToTheWindowAsked()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(5);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.PointsFromText = T0.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");
        vm.PointsToText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Equal(3, vm.RestorePoints.Count);
        Assert.All(vm.RestorePoints, p => Assert.InRange(p.Timestamp, T0.AddHours(2), T0.AddHours(4)));
    }

    /// <summary>
    /// The point of the range: the surviving points spread across the whole track rather than
    /// staying bunched in the sliver they occupied before. Without this it filters but does not
    /// zoom, and picking one of them is no easier than it was.
    /// </summary>
    [Fact]
    public async Task NarrowingTheRangeSpreadsTheRemainingPointsAcrossTheTrack()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(20);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var before = vm.RestorePoints
            .Where(p => p.Timestamp >= T0.AddHours(2) && p.Timestamp <= T0.AddHours(4))
            .Select(p => p.TimelinePosition)
            .ToList();

        // Before narrowing, three of twenty-one points sit inside a tenth of the track.
        Assert.True(before.Max() - before.Min() < 0.2);

        vm.PointsFromText = T0.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");
        vm.PointsToText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");

        var after = vm.RestorePoints.Select(p => p.TimelinePosition).ToList();
        Assert.Equal(0.0, after.Min(), 6);
        Assert.Equal(1.0, after.Max(), 6);
    }

    [Fact]
    public async Task AHalfTypedDateDoesNotBlankTheTimeline()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // Mid-keystroke. Unparsable means no bound rather than an error.
        vm.PointsFromText = "2026-01-";

        Assert.Equal(4, vm.RestorePoints.Count);
        Assert.Null(vm.PointsFrom);
    }

    [Fact]
    public async Task ADateIsReadTheSameWayWhateverTheMachinesCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // en-US would read 02/03 as February; en-GB as March. The box is documented as
            // yyyy-MM-dd, which is unambiguous, and it is parsed invariantly so it stays that way.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");

            var vm = NewViewModel();
            _blob.Files = FullPlusLogs(3);
            vm.SelectedContainer = Container();
            await vm.LoadBackupsCommand.ExecuteAsync(null);

            vm.PointsFromText = "2026-01-10 23:30:00";

            Assert.Equal(new DateTime(2026, 1, 10, 23, 30, 0), vm.PointsFrom);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task AFilterThatMatchesNothingSaysSoRatherThanLookingEmpty()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(3);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.ShowFullPoints = false;
        vm.ShowLogPoints = false;
        vm.ShowDiffPoints = false;

        Assert.Empty(vm.RestorePoints);
        Assert.False(vm.HasVisiblePoints);

        // Still true - the container HAS restore points, they are just all filtered out. The two
        // are different states and the view shows different things for them.
        Assert.True(vm.HasRestorePoints);
    }

    [Fact]
    public async Task AnExistingSelectionSurvivesAFilterThatStillIncludesIt()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(5);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        var chosen = vm.RestorePoints.First(p => p.Timestamp == T0.AddHours(2));
        vm.SelectedRestorePoint = chosen;

        // Narrow to a window that still contains it.
        vm.PointsFromText = T0.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Same(chosen, vm.SelectedRestorePoint);
    }

    [Fact]
    public async Task ZoomToDayNarrowsToTheSelectedPointsDay()
    {
        var vm = NewViewModel();

        // Two days of hourly logs.
        _blob.Files = FullPlusLogs(30);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // T0 is 22:00, so hourly logs cross midnight - the day being zoomed to is the selected
        // point's, not the first point's.
        var chosen = vm.RestorePoints.First(p => p.Timestamp == T0.AddHours(5));
        vm.SelectedRestorePoint = chosen;
        vm.ZoomToSelectedDayCommand.Execute(null);

        Assert.All(vm.RestorePoints, p => Assert.Equal(chosen.Timestamp.Date, p.Timestamp.Date));
        Assert.True(vm.RestorePoints.Count < 31);
        Assert.Contains(vm.RestorePoints, p => p.Timestamp == chosen.Timestamp);
    }

    [Fact]
    public async Task ResetPutsEveryPointBack()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(5);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // Logs off leaves only the full, which sits before the range - so nothing at all.
        vm.ShowLogPoints = false;
        vm.PointsFromText = T0.AddHours(3).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Empty(vm.RestorePoints);

        vm.ResetPointFiltersCommand.Execute(null);

        Assert.Equal(6, vm.RestorePoints.Count);
        Assert.Equal(string.Empty, vm.PointsFromText);
        Assert.True(vm.ShowLogPoints);
    }

    [Fact]
    public async Task LoadingADifferentDatabaseDoesNotInheritThePreviousFilters()
    {
        var vm = NewViewModel();
        _blob.Files = FullPlusLogs(5);
        vm.SelectedContainer = Container();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.PointsFromText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(2, vm.RestorePoints.Count);

        // A range typed for one database would silently hide points belonging to the next.
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(6, vm.RestorePoints.Count);
        Assert.Equal(string.Empty, vm.PointsFromText);
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

        Assert.Equal(3, vm.RestorePoints.Count);

        // A new log arrives in the container, and the user presses Load again.
        var fresh = T0.AddHours(3);
        _blob.Files = FullPlusLogs(3);
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.RestorePoints.Count);
        Assert.Contains(vm.RestorePoints, p => p.Timestamp == fresh);
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
