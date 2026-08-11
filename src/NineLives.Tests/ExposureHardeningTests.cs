using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The sweep machinery gets the same discipline as the rest (#287): it can be stopped, it
/// cannot fail silently, "has swept" is a fact rather than an inference from an empty list,
/// the clock carries a date, and the one-click handoff hands over the connection whose sweep
/// actually produced the row.
/// </summary>
public class ExposureHardeningTests
{
    private static ExposureRow Row(string server, string db) => new()
    {
        ServerName = server,
        DatabaseName = db,
        RecoveryModel = "FULL",
        StateDescription = "ONLINE",
        LastFull = new DateTime(2026, 8, 9, 22, 0, 0),
        LastLog = new DateTime(2026, 8, 10, 11, 40, 0)
    };

    private static (ExposureViewModel vm, FakeSqlServerService sql, FakeCredentialStore store)
        New(params string[] servers)
    {
        var store = new FakeCredentialStore();
        foreach (var name in servers)
            store.Config.Servers.Add(new ServerConnection
            { Id = ServerConnection.NewId(), Name = name, ServerName = name });

        var sql = new FakeSqlServerService();
        return (new ExposureViewModel(store, sql, new FakeRestoreHistoryStore(), TestLogs.Temp()), sql, store);
    }

    // ── the sweep can be stopped (#287 item 1) ──────────────────────────────────

    [Fact]
    public async Task TheSweepCanBeStoppedAndStoppingIsNotAnAlarm()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "Sales")];
        sql.BeforeExposureReturns = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sweep = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanCancelSweep);

        vm.StopSweepCommand.Execute(null);
        sql.BeforeExposureReturns.SetResult(true);
        await sweep;

        // Stopping is the user's doing - it must not fabricate an UNREACHABLE alarm row.
        Assert.Empty(vm.Rows);
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsSweeping);
        Assert.False(vm.CanCancelSweep);
    }

    [Fact]
    public async Task StoppingAReSweepKeepsThePreviousAnswer()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "Sales")];
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);
        var summaryBefore = vm.Summary;

        sql.BeforeExposureReturns = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resweep = vm.RefreshCommand.ExecuteAsync(null);
        vm.StopSweepCommand.Execute(null);
        sql.BeforeExposureReturns.SetResult(true);
        await resweep;

        Assert.Single(vm.Rows);
        Assert.Equal(summaryBefore, vm.Summary);
    }

    // ── has-swept is a fact, not an inference (#287 item 2) ─────────────────────

    /// <summary>
    /// The first-visit auto-sweep used to infer "never swept" from Rows.Count == 0 - so an
    /// estate whose honest answer was empty re-swept every server on every visit.
    /// </summary>
    [Fact]
    public async Task AnEmptyAnswerStillCountsAsSwept()
    {
        var (vm, _, _) = New();   // no servers configured

        Assert.False(vm.HasSwept);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasSwept);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task ACancelledFirstSweepStillCountsAsAttempted()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "Sales")];
        sql.BeforeExposureReturns = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sweep = vm.RefreshCommand.ExecuteAsync(null);
        vm.StopSweepCommand.Execute(null);
        sql.BeforeExposureReturns.SetResult(true);
        await sweep;

        // Stopping it was a decision about THIS sweep; the shell must not immediately restart
        // one on the next visit.
        Assert.True(vm.HasSwept);
    }

    // ── the clock carries a date (#287 item 3) ──────────────────────────────────

    [Fact]
    public async Task TheClockSaysWhichDayItMeans()
    {
        var (vm, sql, _) = New("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "Sales")];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Matches(@"as of \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", vm.LastRefreshed);
    }

    // ── the handoff is exact (#287 item 4) ──────────────────────────────────────

    /// <summary>
    /// Two saved entries for the same instance - different credentials - are different
    /// connections. The handoff used to dedupe by server name with First(), which could hand
    /// the restore screen the OTHER entry's credentials.
    /// </summary>
    [Fact]
    public async Task TheHandoffHandsOverTheConnectionWhoseSweepProducedTheRow()
    {
        var store = new FakeCredentialStore();
        var asAdmin = new ServerConnection { Id = "s1", Name = "SRV01 (admin)", ServerName = "SRV01" };
        var asReader = new ServerConnection { Id = "s2", Name = "SRV01 (reader)", ServerName = "SRV01" };
        store.Config.Servers.Add(asAdmin);
        store.Config.Servers.Add(asReader);
        var sql = new FakeSqlServerService();
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "Sales")];
        var vm = new ExposureViewModel(store, sql, new FakeRestoreHistoryStore(), TestLogs.Temp());
        await vm.RefreshCommand.ExecuteAsync(null);

        // Both entries answered, so the same database appears once per connection; each row
        // must hand over its own.
        BrowseHandoff? handoff = null;
        vm.RestoreRequested += h => handoff = h;

        foreach (var row in vm.Rows)
        {
            handoff = null;
            vm.RestoreFromRowCommand.Execute(row);
            Assert.NotNull(handoff);
        }

        var owners = new List<string?>();
        foreach (var row in vm.Rows)
        {
            vm.RestoreFromRowCommand.Execute(row);
            owners.Add(handoff!.SourceServer?.Id);
        }
        Assert.Contains("s1", owners);
        Assert.Contains("s2", owners);
    }

    [Fact]
    public async Task AnUnreachableRowRefusesTheHandoffByItsFlagNotByItsCaption()
    {
        var (vm, _, _) = New("SRV01");   // SRV01 not in ExposureByServer -> fake throws

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.True(row.IsUnreachable);
        Assert.Equal(ExposureLevel.Alarm, row.Level);

        var raised = false;
        vm.RestoreRequested += _ => raised = true;
        vm.RestoreFromRowCommand.Execute(row);
        Assert.False(raised);
    }
}
