using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The other end of the copy script (#451).
///
/// The script is taken away and run on the source machine, and whoever ran it comes back wanting
/// one thing: did it work. Re-running the check answers "what is missing now", which is a
/// different question - a still-red panel listing five files looks identical whether eighteen
/// arrived or none did.
/// </summary>
public class RescanAndConfirmTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 22, 0, 0);

    private static ServerConnection Server() => new()
    { Id = "s1", Name = "SRV01", ServerName = "SRV01" };

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
    };

    private static BackupHistoryEntry Log(DateTime at, string folder = @"E:\SQLLogs") => new()
    {
        DatabaseName = "Sales",
        Type = BackupType.TransactionLog,
        StartedAt = at,
        Files = [$@"{folder}\Sales_{at:yyyyMMdd_HHmmss}.trn"],
        BackupSizeBytes = 10 * 1024 * 1024
    };

    private static BackupSet Held(DateTime at) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        Type = BackupType.TransactionLog,
        Timestamp = at,
        DatabaseName = "Sales",
        Files = [new BackupFileInfo { BlobName = "x.trn", Type = BackupType.TransactionLog }]
    };

    private static BackupGapViewModel Panel(params BackupHistoryEntry[] history)
    {
        var vm = new BackupGapViewModel(new FakeSqlServerService { BackupHistory = history.ToList() });
        vm.Servers.Add(Server());
        vm.SourceServer = vm.Servers[0];
        return vm;
    }

    private static GapCheckRequest Ask(params BackupSet[] held)
        => new("Sales", Container(), held);

    // ── what the second check says ──────────────────────────────────────────────

    [Fact]
    public async Task TheFirstCheckComparesWithNothingAndSaysNothing()
    {
        var vm = Panel(Log(T0), Log(T0.AddHours(1)));

        await vm.CheckCommand.ExecuteAsync(Ask());

        Assert.True(vm.HasGap);
        Assert.False(vm.HasArrivalReport);
    }

    /// <summary>The press somebody is hoping for.</summary>
    [Fact]
    public async Task WhenEverythingArrivedItSaysSo()
    {
        var vm = Panel(Log(T0), Log(T0.AddHours(1)));

        await vm.CheckCommand.ExecuteAsync(Ask());
        Assert.Equal(2, vm.Locations.Sum(l => l.FileCount));

        // The copy ran: both are in the container now.
        await vm.CheckCommand.ExecuteAsync(Ask(Held(T0), Held(T0.AddHours(1))));

        Assert.False(vm.HasGap);
        Assert.True(vm.HasArrivalReport);
        Assert.Contains("All 2 arrived", vm.ArrivalReport);
    }

    /// <summary>
    /// The one the panel alone cannot tell you. Five still missing looks the same whether the
    /// copy moved eighteen or nothing at all.
    /// </summary>
    [Fact]
    public async Task WhenSomeArrivedItSaysHowManyOfHowMany()
    {
        var logs = Enumerable.Range(0, 5).Select(i => Log(T0.AddHours(i))).ToArray();
        var vm = Panel(logs);

        await vm.CheckCommand.ExecuteAsync(Ask());

        // Three of the five made it across.
        await vm.CheckCommand.ExecuteAsync(
            Ask(Held(T0), Held(T0.AddHours(1)), Held(T0.AddHours(2))));

        Assert.True(vm.HasGap);
        Assert.Contains("3 of 5 arrived", vm.ArrivalReport);
        Assert.Contains("2 still to come", vm.ArrivalReport);
    }

    [Fact]
    public async Task WhenNothingArrivedItSaysThatRatherThanRepeatingTheList()
    {
        var vm = Panel(Log(T0), Log(T0.AddHours(1)));

        await vm.CheckCommand.ExecuteAsync(Ask());
        await vm.CheckCommand.ExecuteAsync(Ask());

        Assert.Contains("None of the 2 arrived", vm.ArrivalReport);
    }

    /// <summary>
    /// A backup taken between the two checks is not a failed copy, and saying so separately
    /// matters because the answer is different - nothing went wrong, there is simply more now.
    /// </summary>
    [Fact]
    public async Task BackupsTakenSinceTheLastCheckAreCalledOutSeparately()
    {
        var sql = new FakeSqlServerService { BackupHistory = [Log(T0), Log(T0.AddHours(1))] };
        var vm = new BackupGapViewModel(sql);
        vm.Servers.Add(Server());
        vm.SourceServer = vm.Servers[0];

        await vm.CheckCommand.ExecuteAsync(Ask());

        // Both copied across - and the instance has taken two more in the meantime.
        sql.BackupHistory.Add(Log(T0.AddHours(2)));
        sql.BackupHistory.Add(Log(T0.AddHours(3)));

        await vm.CheckCommand.ExecuteAsync(Ask(Held(T0), Held(T0.AddHours(1))));

        Assert.Contains("All 2 arrived", vm.ArrivalReport);
        Assert.Contains("2 more have been taken since", vm.ArrivalReport);
    }

    /// <summary>
    /// Counted by file, not by number. A retention job trimming an old log between the checks
    /// would otherwise be indistinguishable from a file that arrived.
    /// </summary>
    [Fact]
    public void ArrivalsAreJudgedByWhichFilesNotHowMany()
    {
        var before = new HashSet<string> { @"E:\a.trn", @"E:\b.trn" };
        var after = new HashSet<string> { @"E:\c.trn", @"E:\d.trn" };

        var said = BackupGapViewModel.DescribeArrivals(before, after);

        // Both originals gone AND two unfamiliar ones present: two arrived, two are new.
        Assert.Contains("All 2 arrived", said);
        Assert.Contains("2 more have been taken since", said);
    }

    [Fact]
    public void WithNoPreviousCheckThereIsNothingToCompare()
        => Assert.Empty(BackupGapViewModel.DescribeArrivals(
            new HashSet<string>(), new HashSet<string> { @"E:\a.trn" }));

    /// <summary>
    /// Changing the source instance throws the comparison away with the answer. Measuring one
    /// instance's backups against another's would report every file as arrived.
    /// </summary>
    [Fact]
    public async Task ChangingTheSourceForgetsWhatWasMissing()
    {
        var vm = Panel(Log(T0), Log(T0.AddHours(1)));
        await vm.CheckCommand.ExecuteAsync(Ask());

        vm.Servers.Add(new ServerConnection { Id = "s2", Name = "SRV02", ServerName = "SRV02" });
        vm.SourceServer = vm.Servers[1];

        await vm.CheckCommand.ExecuteAsync(Ask());

        Assert.False(vm.HasArrivalReport);
    }
}
