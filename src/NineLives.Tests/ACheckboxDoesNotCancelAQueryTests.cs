using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Ticking a checkbox does not cancel somebody's work (#413).
///
/// Fetching the target's default directories is one of the mutually exclusive server operations
/// on this screen: they share a cancellation source so one Stop button covers them all, and
/// starting one deliberately stops the last. That doctrine is right for the BUTTONS - pressing
/// "Fetch from Server" while a chain verification runs is a choice to do the other thing instead.
///
/// It was wrong for the automatic trigger. Ticking WITH MOVE is a request to see the move
/// options; it fired the fetch as a convenience, and that cancelled a chain verification which
/// reads the header of every backup in the chain - several minutes' work - for a value the user
/// can ask for explicitly a moment later.
/// </summary>
public class ACheckboxDoesNotCancelAQueryTests
{
    private static (RestoreViewModel Vm, FakeSqlServerService Sql) Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        var sql = new FakeSqlServerService();
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(), new FakeRestoreHistoryStore());

        vm.ConnectedServer = store.Config.Servers[0];
        vm.IsConnectedToServer = true;
        return (vm, sql);
    }

    [Fact]
    public async Task WithNothingRunningTheTickStillFetches()
    {
        var (vm, sql) = Screen();

        vm.UseWithMove = true;
        await Settle();

        Assert.True(sql.DefaultPathsAsked > 0);
        Assert.Contains("detected from", vm.PathSourceText);
    }

    /// <summary>
    /// The rule. A query is in flight, so the tick defers instead of cancelling it - and says
    /// what it did, rather than silently leaving placeholder paths.
    /// </summary>
    [Fact]
    public async Task WithAQueryRunningTheTickDefersAndSaysSo()
    {
        var (vm, sql) = Screen();

        // A query in flight on the shared source, started by a button - held open, the way a
        // chain verification reading every header would be.
        var gate = new TaskCompletionSource();
        sql.HoldDefaultPaths = gate;
        var running = vm.FetchDefaultPathsCommand.ExecuteAsync(null);

        var asked = sql.DefaultPathsAsked;
        vm.UseWithMove = true;
        await Settle();

        Assert.Equal(asked, sql.DefaultPathsAsked);
        Assert.Contains("already running", vm.PathSourceText);
        Assert.Contains("Fetch from Server", vm.PathSourceText);

        gate.SetResult();
        await running;
    }

    /// <summary>Unticking never fetched anything and still does not.</summary>
    [Fact]
    public async Task UntickingFetchesNothing()
    {
        var (vm, sql) = Screen();

        vm.UseWithMove = true;
        await Settle();
        var asked = sql.DefaultPathsAsked;

        vm.UseWithMove = false;
        await Settle();

        Assert.Equal(asked, sql.DefaultPathsAsked);
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 20; i++) await Task.Yield();
        await Task.Delay(20);
    }
}
