using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The exposure dashboard (#239): "if this server died now, how much work is gone?" - per
/// database, across every configured server, worst first. The silent failures are the loudest:
/// no backup ever, FULL recovery with no log backups, chains that stopped, servers that will not
/// answer.
/// </summary>
public class ExposureDashboardTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0);

    private static ExposureRow Row(
        string db = "MyDb", string model = "FULL",
        DateTime? full = null, DateTime? diff = null, DateTime? log = null) => new()
    {
        ServerName = "SRV01",
        DatabaseName = db,
        RecoveryModel = model,
        StateDescription = "ONLINE",
        LastFull = full,
        LastDifferential = diff,
        LastLog = log
    };

    // ── the advisor's rule table ────────────────────────────────────────────────

    [Fact]
    public void NeverBackedUpIsTheLoudestAlarm()
    {
        var row = Row();
        ExposureAdvisor.Judge(row, Now);

        Assert.Equal(ExposureLevel.Alarm, row.Level);
        Assert.Contains("Never backed up", row.Verdict);
    }

    /// <summary>The classic silent failure: FULL recovery, log never backed up.</summary>
    [Fact]
    public void FullRecoveryWithNoLogBackupsIsAnAlarmAboutTwoThingsAtOnce()
    {
        var row = Row(full: Now.AddHours(-10));
        ExposureAdvisor.Judge(row, Now);

        Assert.Equal(ExposureLevel.Alarm, row.Level);
        Assert.Contains("log file grows", row.Verdict);
        Assert.Contains("unprotected", row.Verdict);
    }

    [Fact]
    public void AHealthyLogChainIsQuietAndSaysWhatItCouldLose()
    {
        var row = Row(full: Now.AddDays(-1), log: Now.AddMinutes(-20));
        ExposureAdvisor.Judge(row, Now);

        Assert.Equal(ExposureLevel.Ok, row.Level);
        Assert.Contains("up to 20m", row.Verdict);
    }

    [Theory]
    [InlineData(-2, ExposureLevel.Warning)]   // 2h of log silence: look at the schedule
    [InlineData(-30, ExposureLevel.Alarm)]    // 30h: the chain has stopped
    public void LogSilenceEscalatesWithAge(int hoursAgo, ExposureLevel expected)
    {
        var row = Row(full: Now.AddDays(-2), log: Now.AddHours(hoursAgo));
        ExposureAdvisor.Judge(row, Now);

        Assert.Equal(expected, row.Level);
    }

    /// <summary>SIMPLE recovery is judged against its own cycle, not against log expectations.</summary>
    [Fact]
    public void SimpleRecoveryGetsADailyCycleGrace()
    {
        var healthy = Row(model: "SIMPLE", full: Now.AddHours(-20));
        ExposureAdvisor.Judge(healthy, Now);
        Assert.Equal(ExposureLevel.Ok, healthy.Level);

        var stale = Row(model: "SIMPLE", full: Now.AddDays(-8));
        ExposureAdvisor.Judge(stale, Now);
        Assert.Equal(ExposureLevel.Alarm, stale.Level);
    }

    /// <summary>A differential moves the recovery point for SIMPLE databases.</summary>
    [Fact]
    public void ADifferentialCountsTowardTheRecoveryPoint()
    {
        var row = Row(model: "SIMPLE", full: Now.AddDays(-3), diff: Now.AddHours(-2));
        ExposureAdvisor.Judge(row, Now);

        Assert.Equal(ExposureLevel.Ok, row.Level);
        Assert.Contains("up to 2h", row.Verdict);
    }

    // ── the sweep ───────────────────────────────────────────────────────────────

    private static (ExposureViewModel vm, FakeSqlServerService sql, FakeRestoreHistoryStore history)
        New(params string[] servers)
    {
        var store = new FakeCredentialStore();
        foreach (var name in servers)
            store.Config.Servers.Add(new ServerConnection
            { Id = ServerConnection.NewId(), Name = name, ServerName = name });

        var sql = new FakeSqlServerService();
        var history = new FakeRestoreHistoryStore();
        return (new ExposureViewModel(store, sql, history, TestLogs.Temp()), sql, history);
    }

    [Fact]
    public async Task TheWorstThingIsTheFirstThing()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] =
        [
            Row(db: "Healthy", full: DateTime.Now.AddDays(-1), log: DateTime.Now.AddMinutes(-5)),
            Row(db: "Naked"),  // never backed up
            Row(db: "Stale", full: DateTime.Now.AddDays(-2), log: DateTime.Now.AddHours(-3))
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Naked", vm.Rows[0].DatabaseName);
        Assert.Equal(ExposureLevel.Alarm, vm.Rows[0].Level);
        Assert.Equal("Healthy", vm.Rows[^1].DatabaseName);
        Assert.Contains("1 alarm(s), 1 warning(s)", vm.Summary);
    }

    /// <summary>
    /// A server that will not answer is an ALARM row, not a silent absence - a dashboard that
    /// omits the unreachable server reads "all clear" at the exact moment it should not.
    /// </summary>
    [Fact]
    public async Task AServerThatWillNotAnswerIsAnAlarmRow()
    {
        var (vm, sql, _) = New("SRV01", "SRV02");
        sql.ExposureByServer["SRV01"] =
            [Row(db: "Fine", full: DateTime.Now.AddDays(-1), log: DateTime.Now.AddMinutes(-10))];
        // SRV02 deliberately absent from the fake: it throws.

        await vm.RefreshCommand.ExecuteAsync(null);

        var down = Assert.Single(vm.Rows, r => r.StateDescription == "UNREACHABLE");
        Assert.Equal("SRV02", down.ServerName);
        Assert.Equal(ExposureLevel.Alarm, down.Level);
        Assert.Contains("UNKNOWN, which is not the same as fine", down.Verdict);
    }

    /// <summary>The rehearsal receipts (#238) join in: proof, not just arithmetic.</summary>
    [Fact]
    public async Task RehearsalReceiptsShowWhenADatabaseWasLastProven()
    {
        var (vm, sql, history) = New("SRV01");
        sql.ExposureByServer["SRV01"] =
            [Row(db: "MyDb", full: DateTime.Now.AddDays(-1), log: DateTime.Now.AddMinutes(-10))];

        history.Entries.Add(new RestoreHistoryEntry
        {
            ServerName = "SRV01",
            SourceDatabase = "MyDb",
            TargetDatabase = "MyDb_rehearsal_20260809_2100",
            Kind = "Rehearsal",
            Outcome = RestoreOutcome.Succeeded,
            StartedAt = new DateTime(2026, 8, 9, 21, 16, 0),
            CompletedAt = new DateTime(2026, 8, 9, 21, 30, 0)
        });

        await vm.RefreshCommand.ExecuteAsync(null);

        var proven = vm.Rows.Single();
        Assert.Equal(new DateTime(2026, 8, 9, 21, 30, 0), proven.LastProven);

        // The receipt measured the real restore-plus-CHECKDB time - the RTO number that
        // conversations otherwise invent - and the cell says it.
        Assert.Equal(TimeSpan.FromMinutes(14), proven.MeasuredRestore);
        Assert.Contains("took 14m", proven.ProvenDisplay);
    }

    /// <summary>
    /// From seeing to acting in one click (#202's pattern): the row hands its server and database
    /// to the restore screen through the server's own msdb - exactly where the numbers came from.
    /// </summary>
    [Fact]
    public async Task ARowHandsItsServerAndDatabaseToTheRestoreScreen()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] =
            [Row(db: "Payroll", full: DateTime.Now.AddHours(-10))];
        await vm.RefreshCommand.ExecuteAsync(null);

        BrowseHandoff? handoff = null;
        vm.RestoreRequested += h => handoff = h;

        vm.RestoreFromRowCommand.Execute(vm.Rows.Single());

        Assert.NotNull(handoff);
        Assert.Equal(BackupMedium.SharedPath, handoff!.Medium);
        Assert.Equal("SRV01", handoff.SourceServer?.ServerName);
        Assert.Equal("Payroll", handoff.Database);
    }

    /// <summary>An unreachable row has nowhere to hand over to - the click does nothing.</summary>
    [Fact]
    public async Task AnUnreachableRowDoesNotHandOff()
    {
        var (vm, _, _) = New("SRV01", "SRV02");
        var fired = false;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.RestoreRequested += _ => fired = true;

        vm.RestoreFromRowCommand.Execute(
            vm.Rows.First(r => r.StateDescription == "UNREACHABLE"));

        Assert.False(fired);
    }

    [Fact]
    public async Task NoServersSaysSoInsteadOfShowingAnEmptyAllClear()
    {
        var (vm, _, _) = New();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Rows);
        Assert.Contains("No servers configured", vm.Summary);
    }
}
