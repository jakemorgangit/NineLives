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
