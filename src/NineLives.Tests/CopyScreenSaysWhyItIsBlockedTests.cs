using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Copy screen explains itself, and stops losing the choice that was made (#457).
///
/// Reported from the app: every visible field filled in, the overwrite banner naming the target,
/// and both buttons dead with nothing on screen saying why. Two faults behind it.
///
/// The source database had been cleared out from under the form. Refresh runs on every visit to
/// this screen and re-assigns SourceServer to a fresh instance from the config - a different
/// object every time, because the real store deserializes - which fires OnSourceServerChanged,
/// which clears the database and reloads the list. The target server, container, target name and
/// overwrite tick all survive that, so the form looked complete and was not.
///
/// And nothing said so. Six things have to be true before Generate is live and the screen named
/// none of them.
/// </summary>
public class CopyScreenSaysWhyItIsBlockedTests
{
    private static (CopyDatabaseViewModel vm, FakeCredentialStore store, FakeSqlServerService sql) Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s2", Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb", "Payroll", "Warehouse"] };
        return (new CopyDatabaseViewModel(store, sql), store, sql);
    }

    private static async Task<CopyDatabaseViewModel> FullyAnsweredAsync()
    {
        var (vm, _, _) = Screen();
        vm.Refresh();

        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.Container = vm.Containers[0];
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb";

        return vm;
    }

    // ── the sentence that was missing ───────────────────────────────────────────

    [Fact]
    public async Task AFullyAnsweredScreenIsNotBlockedAndSaysNothing()
    {
        var vm = await FullyAnsweredAsync();

        Assert.True(vm.CanGenerate);
        Assert.False(vm.IsGenerateBlocked);
        Assert.Empty(vm.GenerateBlockedReason);
    }

    /// <summary>The reported case: everything else answered, no source database.</summary>
    [Fact]
    public async Task WithNoSourceDatabaseItNamesThatAndTheStepToGoBackTo()
    {
        var vm = await FullyAnsweredAsync();
        vm.SourceDatabase = null;

        Assert.False(vm.CanGenerate);
        Assert.True(vm.IsGenerateBlocked);
        Assert.Contains("database to copy", vm.GenerateBlockedReason);
        Assert.Contains("step 1", vm.GenerateBlockedReason);
    }

    [Fact]
    public async Task WithNoTargetNameItSaysSo()
    {
        var vm = await FullyAnsweredAsync();
        vm.TargetDatabaseName = "";

        Assert.Contains("restore as", vm.GenerateBlockedReason);
        Assert.Contains("step 3", vm.GenerateBlockedReason);
    }

    [Fact]
    public async Task WithNoContainerItSaysSo()
    {
        var vm = await FullyAnsweredAsync();
        vm.Container = null;

        Assert.Contains("container", vm.GenerateBlockedReason);
        Assert.Contains("step 2", vm.GenerateBlockedReason);
    }

    /// <summary>
    /// The order is the order the screen asks the questions, so somebody with three blanks is sent
    /// to the first one rather than the last.
    /// </summary>
    [Fact]
    public async Task WithSeveralBlanksItNamesTheEarliest()
    {
        var vm = await FullyAnsweredAsync();
        vm.SourceDatabase = null;
        vm.TargetDatabaseName = "";
        vm.Container = null;

        Assert.Contains("step 1", vm.GenerateBlockedReason);
    }

    // ── the choice survives a revisit ───────────────────────────────────────────

    /// <summary>
    /// The fault behind the report. Refresh re-assigns SourceServer to a fresh instance, which
    /// clears the database and reloads - and everything the user filled in downstream survives, so
    /// the form looks complete.
    /// </summary>
    [Fact]
    public async Task RevisitingTheScreenKeepsTheDatabaseThatWasChosen()
    {
        var vm = await FullyAnsweredAsync();
        Assert.True(vm.CanGenerate);

        // What navigating away and back does.
        vm.Refresh();
        await Task.Delay(50);

        Assert.Equal("MyDb", vm.SourceDatabase);
        Assert.True(vm.CanGenerate);
    }

    /// <summary>
    /// Putting back what the user chose is not the same as choosing for them. A source server they
    /// have just switched to has no previous answer, and the app must not invent one - the wrong
    /// choice here reads a production database at full speed and overwrites one on another server.
    /// </summary>
    [Fact]
    public async Task SwitchingToADifferentSourceServerStillChoosesNothing()
    {
        var vm = await FullyAnsweredAsync();

        vm.SourceServer = vm.Servers.First(s => s.Id == "s2");
        await Task.Delay(50);

        Assert.Null(vm.SourceDatabase);
        Assert.Contains("database to copy", vm.GenerateBlockedReason);
    }

    /// <summary>
    /// And a database that has gone from the instance since is not put back. Restoring a selection
    /// that no longer exists would arm the screen against a database nobody could back up.
    /// </summary>
    [Fact]
    public async Task ADatabaseThatIsNoLongerThereIsNotRestored()
    {
        var (vm, _, sql) = Screen();
        vm.Refresh();
        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";

        sql.DatabaseList = ["Payroll", "Warehouse"];
        vm.Refresh();
        await Task.Delay(50);

        Assert.Null(vm.SourceDatabase);
    }
}
