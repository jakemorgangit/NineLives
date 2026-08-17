using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The panel that asks the source instance what the container is missing (#451).
///
/// It answers a question the restore screen cannot answer alone: is this chain short because that
/// is all there ever was, or because the logs went somewhere else? Only the instance that took
/// them knows.
/// </summary>
public class BackupGapViewModelTests
{
    private static readonly DateTime T0 = new(2026, 8, 14, 22, 0, 0);

    private static ServerConnection Server(string name = "SRV01") => new()
    {
        Id = ServerConnection.NewId(),
        Name = name,
        ServerName = name
    };

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
    };

    private static BackupHistoryEntry Recorded(BackupType type, DateTime at, string folder) => new()
    {
        DatabaseName = "Sales",
        Type = type,
        StartedAt = at,
        Files = [$@"{folder}\Sales_{at:yyyyMMdd_HHmmss}.trn"],
        BackupSizeBytes = 10 * 1024 * 1024
    };

    private static BackupSet Held(BackupType type, DateTime at) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = at,
        DatabaseName = "Sales",
        Files = [new BackupFileInfo { BlobName = "x.bak", Type = type }]
    };

    private static (BackupGapViewModel vm, FakeSqlServerService sql) Panel(
        params BackupHistoryEntry[] history)
    {
        var sql = new FakeSqlServerService { BackupHistory = history.ToList() };
        var vm = new BackupGapViewModel(sql);
        vm.Servers.Add(Server());
        vm.SourceServer = vm.Servers[0];
        return (vm, sql);
    }

    private static GapCheckRequest Ask(params BackupSet[] held)
        => new("Sales", Container(), held);

    // ── the case it exists for ──────────────────────────────────────────────────

    [Fact]
    public async Task LogsTakenToDiskAreFoundAndTheirFolderNamed()
    {
        var (vm, _) = Panel(
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs"),
            Recorded(BackupType.TransactionLog, T0.AddHours(2), @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(BackupType.Full, T0)));

        Assert.True(vm.HasGap);
        var row = Assert.Single(vm.Locations);
        Assert.Equal(@"E:\SQLLogs", row.Folder);
        Assert.Equal("2 log backups", row.Summary);
        Assert.True(row.IsOnDisk);
    }

    [Fact]
    public async Task ItSaysHowFarBehindTheContainerIs()
    {
        var (vm, _) = Panel(
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(12).AddMinutes(45), @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(BackupType.Full, T0)));

        Assert.Equal("12 hours 45 minutes", vm.BehindBy);
    }

    /// <summary>
    /// A clean answer has to be said out loud. "No locations" rendering as an empty panel is
    /// indistinguishable from never having pressed the button.
    /// </summary>
    [Fact]
    public async Task AContainerHoldingEverythingSaysSoRatherThanShowingNothing()
    {
        var (vm, _) = Panel(Recorded(BackupType.Full, T0, @"C:\Backups"));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(BackupType.Full, T0)));

        Assert.False(vm.HasGap);
        Assert.True(vm.FoundNothingMissing);
        Assert.Contains("Everything the instance recorded", vm.StatusMessage);
    }

    [Fact]
    public void BeforeAnyCheckTheAllClearIsNotClaimed()
    {
        var (vm, _) = Panel();

        Assert.False(vm.HasChecked);
        Assert.False(vm.FoundNothingMissing);
        Assert.False(vm.HasGap);
    }

    // ── what was compared ───────────────────────────────────────────────────────

    /// <summary>
    /// The answer is only worth anything if somebody can see it was asked of the right instance
    /// and the right database - this panel connects to a DIFFERENT server from the one the restore
    /// runs on, which is exactly the confusion worth heading off.
    /// </summary>
    [Fact]
    public async Task ItStatesWhatItCompared()
    {
        var (vm, _) = Panel(Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(BackupType.Full, T0.AddHours(-1))));

        Assert.Contains("Sales on SRV01", vm.ComparedWhat);
        Assert.Contains("sqlbackups", vm.ComparedWhat);
    }

    // ── refusals and failures ───────────────────────────────────────────────────

    [Fact]
    public async Task WithNoDatabaseChosenItSaysSoRatherThanCheckingNothing()
    {
        var (vm, _) = Panel(Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(new GapCheckRequest("", Container(), []));

        Assert.True(vm.HasError);
        Assert.Contains("per database", vm.ErrorMessage);
        Assert.False(vm.HasChecked);
    }

    [Fact]
    public async Task AnUnreachableSourceNamesItselfInTheFailure()
    {
        var (vm, sql) = Panel();
        sql.BackupHistoryThrows = new InvalidOperationException("Login failed for user 'sa'.");

        await vm.CheckCommand.ExecuteAsync(Ask());

        Assert.True(vm.HasError);
        Assert.Contains("SRV01", vm.ErrorMessage);
        Assert.Contains("Login failed", vm.ErrorMessage);
        Assert.False(vm.HasChecked);
    }

    [Fact]
    public void TheCheckIsRefusedUntilASourceIsChosen()
    {
        var vm = new BackupGapViewModel(new FakeSqlServerService());
        Assert.False(vm.CanCheck);

        vm.Servers.Add(Server());
        vm.SourceServer = vm.Servers[0];

        Assert.True(vm.CanCheck);
    }

    /// <summary>
    /// A previous answer describes a previous server. Leaving it on screen under a new selection
    /// is the stale-result problem, and a panel whose whole job is to be believed cannot have it.
    /// </summary>
    [Fact]
    public async Task ChangingTheSourceClearsThePreviousAnswer()
    {
        var (vm, _) = Panel(Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs"));
        await vm.CheckCommand.ExecuteAsync(Ask());
        Assert.True(vm.HasGap);

        vm.Servers.Add(Server("SRV02"));
        vm.SourceServer = vm.Servers[1];

        Assert.False(vm.HasGap);
        Assert.False(vm.HasChecked);
        Assert.Empty(vm.BehindBy);
    }

    /// <summary>
    /// A gap and a MEASURED gap are different things, and rendering the panel is what made that
    /// obvious: a container holding nothing at all for this database has everything missing and no
    /// interval to quote, because there is no newest-held backup to measure from. The banner
    /// assumed the two went together and drew "This container is  behind what the instance
    /// recorded" - a sentence with a hole in it.
    /// </summary>
    [Fact]
    public async Task AContainerHoldingNothingHasAGapButNoIntervalToQuote()
    {
        var (vm, _) = Panel(Recorded(BackupType.TransactionLog, T0, @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(Ask());

        Assert.True(vm.HasGap);
        Assert.False(vm.HasMeasuredGap);
        Assert.Empty(vm.BehindBy);
    }

    [Fact]
    public async Task AContainerThatIsMerelyBehindHasBoth()
    {
        var (vm, _) = Panel(
            Recorded(BackupType.Full, T0, @"C:\Backups"),
            Recorded(BackupType.TransactionLog, T0.AddHours(3), @"E:\SQLLogs"));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(BackupType.Full, T0)));

        Assert.True(vm.HasGap);
        Assert.True(vm.HasMeasuredGap);
        Assert.Equal("3 hours", vm.BehindBy);
    }

    // ── the copy script ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCopyScriptIsBuiltForTheChosenLocationOnly()
    {
        var (vm, _) = Panel(
            Recorded(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs"),
            Recorded(BackupType.TransactionLog, T0.AddHours(2), @"\\nas01\shipping"));

        await vm.CheckCommand.ExecuteAsync(Ask());
        Assert.Equal(2, vm.Locations.Count);

        var first = vm.Locations.First(l => l.Folder == @"E:\SQLLogs");
        Assert.False(first.HasScript);

        vm.BuildScript(first, Container());

        Assert.True(first.HasScript);
        Assert.Contains(@"E:\SQLLogs", first.Script);
        Assert.DoesNotContain(@"\\nas01\shipping", first.Script);

        // And the other is untouched - one location, one script, one run.
        Assert.False(vm.Locations.First(l => l.Folder == @"\\nas01\shipping").HasScript);
    }

    /// <summary>
    /// A backup msdb recorded to a URL is not a file on the source machine, so there is nothing
    /// for a copy script to pick up. That finding means the container listing did not see
    /// something that WAS written to storage - a different problem with a different answer.
    /// </summary>
    [Fact]
    public async Task ABackupRecordedToAUrlIsNotOfferedAsSomethingToCopy()
    {
        var (vm, _) = Panel(new BackupHistoryEntry
        {
            DatabaseName = "Sales",
            Type = BackupType.TransactionLog,
            StartedAt = T0,
            Files = ["https://acct.blob.core.windows.net/sqlbackups/Sales_log.trn"]
        });

        await vm.CheckCommand.ExecuteAsync(Ask());

        var row = Assert.Single(vm.Locations);
        Assert.False(row.IsOnDisk);
    }

    // ── the wording of the gap ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 30, "30 minutes")]
    [InlineData(0, 1, 0, "1 hour")]
    [InlineData(0, 12, 45, "12 hours 45 minutes")]
    [InlineData(2, 3, 0, "2 days 3 hours")]
    [InlineData(0, 0, 0, "under a minute")]
    public void TheGapIsQuotedInUnitsSomebodyDecidesOn(int d, int h, int m, string expected)
        => Assert.Equal(expected, BackupGapViewModel.Humanise(new TimeSpan(d, h, m, 0)));
}
