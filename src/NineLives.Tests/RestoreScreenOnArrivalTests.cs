using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What the Restore screen says before it has been asked anything (#419), and what choosing a
/// target actually does (#420). Both reported from the app.
/// </summary>
public class RestoreScreenOnArrivalTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 19, 51, 26);

    private static (RestoreViewModel Vm, FakeSqlServerService Sql) Screen(
        params BackupFileInfo[] files)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SQLEXPRESS", ServerName = "SQLEXPRESS" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService();
        var vm = new RestoreViewModel(
            new FakeBlobStorageService { Files = files.ToList() }, sql,
            new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            new FakeOperationHistoryStore(), TestLogs.Temp());

        return (vm, sql);
    }

    private static BackupFileInfo Full(string db) => new()
    {
        BlobName = $"FULL/SRV01/{db}/20260809_195126.bak",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/SRV01/{db}/20260809_195126.bak",
        Type = BackupType.Full,
        InferredServerName = "SRV01",
        InferredDatabaseName = db,
        SizeBytes = 1024,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    // ── nothing loaded, nothing to say (#419) ───────────────────────────────────

    [Fact]
    public void ArrivingSaysNothingAboutRestorePoints()
    {
        var (vm, _) = Screen();

        Assert.False(vm.HasError);
        Assert.DoesNotContain("No valid restore points", vm.ErrorMessage);
        Assert.DoesNotContain("No valid restore points", vm.StatusMessage);
    }

    /// <summary>
    /// Selecting a container is not loading from it. This is the exact sequence reported: the
    /// dropdown has a container in it, Load Backups has not been pressed, and the screen was
    /// already claiming there was no full backup.
    /// </summary>
    [Fact]
    public void ChoosingAContainerWithoutLoadingSaysNothingEither()
    {
        var (vm, _) = Screen(Full("COaaS"));

        vm.SelectedContainer = vm.Containers.FirstOrDefault();

        Assert.False(vm.HasError);
        Assert.DoesNotContain("No valid restore points", vm.ErrorMessage);
    }

    /// <summary>
    /// Loaded, but no database picked yet - still nothing to judge, because the working set is
    /// empty by choice rather than by absence.
    /// </summary>
    [Fact]
    public async Task LoadedButWithNoDatabaseChosenSaysNothing()
    {
        var (vm, _) = Screen(Full("COaaS"));
        vm.SelectedContainer = vm.Containers.FirstOrDefault();

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
    }

    /// <summary>
    /// And the finding still fires when it is a finding: backups loaded, a database chosen, and
    /// genuinely nothing restorable - here a lone log with no full to base it on.
    /// </summary>
    [Fact]
    public async Task ADatabaseWithNoFullStillReportsIt()
    {
        var log = new BackupFileInfo
        {
            BlobName = "LOG/SRV01/Orphan/20260809_200000.trn",
            BlobUrl = "https://acct.blob.core.windows.net/backups/LOG/SRV01/Orphan/20260809_200000.trn",
            Type = BackupType.TransactionLog,
            InferredServerName = "SRV01",
            InferredDatabaseName = "Orphan",
            SizeBytes = 1024,
            LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
        };

        var (vm, _) = Screen(log);
        vm.SelectedContainer = vm.Containers.FirstOrDefault();
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        vm.Inventory.SelectedDatabaseName = "Orphan";

        Assert.True(vm.HasError);
        Assert.Contains("No valid restore points", vm.ErrorMessage);
    }

    // ── choosing a target proves it (#420) ──────────────────────────────────────

    [Fact]
    public async Task ChoosingATargetConnectsToIt()
    {
        var (vm, sql) = Screen(Full("COaaS"));

        vm.SelectedTargetServer = vm.TargetServers.First();
        await Settle();

        Assert.Contains("SQLEXPRESS", sql.Connected);
        Assert.True(vm.IsConnectedToServer);
        Assert.Contains("Connected to SQLEXPRESS", vm.TargetConnectionState);
    }

    /// <summary>
    /// A target that cannot be reached is a legitimate outcome, not a dead end: the script still
    /// generates and the screen says so, rather than sending somebody to another tab.
    /// </summary>
    [Fact]
    public async Task ATargetThatCannotBeReachedSaysSoAndDoesNotBlockGeneration()
    {
        var (vm, sql) = Screen(Full("COaaS"));
        sql.TestConnectionThrows = new InvalidOperationException(
            "A network-related or instance-specific error occurred.");

        vm.SelectedTargetServer = vm.TargetServers.First();
        await Settle();

        Assert.False(vm.IsConnectedToServer);
        Assert.Contains("Could not connect to SQLEXPRESS", vm.TargetConnectionState);
        Assert.Contains("network-related", vm.TargetConnectionState);
        Assert.Contains("runbook", vm.TargetConnectionState);
    }

    /// <summary>
    /// Choosing a target COMPLETES step 2, which folds it away - so the failure has to reach the
    /// collapsed header, or the screen shows a green tick against a server it could not reach
    /// with the explanation hidden inside. Found by rendering it.
    /// </summary>
    [Fact]
    public async Task TheCollapsedStepHeaderSaysTheTargetWasNotReached()
    {
        var (vm, sql) = Screen(Full("COaaS"));
        sql.TestConnectionThrows = new InvalidOperationException("Login failed.");

        vm.SelectedTargetServer = vm.TargetServers.First();
        await Settle();

        Assert.Contains("could not be reached", vm.Steps.Target.Summary);
        Assert.Contains("SQLEXPRESS", vm.Steps.Target.Summary);
    }

    [Fact]
    public async Task AReachedTargetLeavesTheHeaderSayingJustItsName()
    {
        var (vm, _) = Screen(Full("COaaS"));

        vm.SelectedTargetServer = vm.TargetServers.First();
        await Settle();

        Assert.Equal("SQLEXPRESS", vm.Steps.Target.Summary);
    }

    /// <summary>
    /// The answer belongs to the server that was asked (#409): a connection held open while the
    /// user picks a different target must not report for the one they moved away from.
    /// </summary>
    [Fact]
    public async Task AConnectionThatFinishesAfterTheChoiceMovedOnIsDiscarded()
    {
        var store = new FakeCredentialStore();
        foreach (var name in new[] { "SQLEXPRESS", "SRV02" })
            store.Config.Servers.Add(new ServerConnection
            { Id = ServerConnection.NewId(), Name = name, ServerName = name });

        var sql = new FakeSqlServerService();
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, new FakeOperationHistoryStore(), TestLogs.Temp());

        var gate = new TaskCompletionSource();
        sql.HoldConnection = gate;

        vm.SelectedTargetServer = vm.TargetServers.First(s => s.ServerName == "SQLEXPRESS");

        // Moved on while the first connection hangs.
        sql.HoldConnection = null;
        vm.SelectedTargetServer = vm.TargetServers.First(s => s.ServerName == "SRV02");
        gate.SetResult();
        await Settle();

        Assert.DoesNotContain("SQLEXPRESS", vm.TargetConnectionState);
    }

    /// <summary>Lets the fire-and-forget connection attempt finish.</summary>
    private static async Task Settle()
    {
        for (int i = 0; i < 20; i++) await Task.Yield();
        await Task.Delay(20);
    }
}
