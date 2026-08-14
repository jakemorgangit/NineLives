using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The restore rehearsal (#238): the same chain, restored to a scratch name, proven with CHECKDB,
/// dropped - leaving nothing behind but the receipt. The only tested backup is a restored backup,
/// and this makes the test cheap enough to actually run.
///
/// The safety promise is BY CONSTRUCTION: generated name refused if it exists, no WITH REPLACE
/// ever, every file MOVEd to scratch-named files, and the DROP guarded and last so any failure
/// retains the evidence.
/// </summary>
public class RestoreRehearsalTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 9, 30, 0);

    // ── the planner ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheScratchNameSaysWhatItIs()
    {
        Assert.Equal("MyDb_rehearsal_20260810_0930", RehearsalPlanner.ScratchName("MyDb", T0));
    }

    /// <summary>Every file relocates - rows to data, logs to log, all scratch-named.</summary>
    [Fact]
    public void EveryFileMovesToScratchNamedFiles()
    {
        var files = new List<FileMoveOption>
        {
            new() { LogicalName = "MyDb", PhysicalName = @"D:\SQL\MyDb.mdf", Type = "D" },
            new() { LogicalName = "MyDb_2", PhysicalName = @"D:\SQL\MyDb_2.ndf", Type = "D" },
            new() { LogicalName = "MyDb_log", PhysicalName = @"L:\SQL\MyDb_log.ldf", Type = "L" }
        };

        var moves = RehearsalPlanner.ScratchMoves(files, "MyDb_rehearsal_20260810_0930",
            @"C:\Data", @"C:\Log");

        Assert.Equal(@"C:\Data\MyDb_rehearsal_20260810_0930.mdf", moves[0].NewPhysicalName);
        Assert.Equal(@"C:\Data\MyDb_rehearsal_20260810_0930_2.ndf", moves[1].NewPhysicalName);
        Assert.Equal(@"C:\Log\MyDb_rehearsal_20260810_0930_log.ldf", moves[2].NewPhysicalName);
    }

    /// <summary>Restore, then proof, then guarded cleanup - in that order, always.</summary>
    [Fact]
    public void TheScriptProvesBeforeItCleansUp()
    {
        var script = RehearsalPlanner.BuildScript(
            "RESTORE DATABASE [MyDb_rehearsal_20260810_0930] FROM DISK = N'D:\\b.bak' WITH RECOVERY;",
            "MyDb_rehearsal_20260810_0930");

        var checkdb = script.IndexOf("DBCC CHECKDB", StringComparison.Ordinal);
        var drop = script.IndexOf("DROP DATABASE", StringComparison.Ordinal);

        Assert.True(checkdb > 0 && drop > checkdb, "order must be restore, CHECKDB, drop");
        Assert.Contains("IF DB_ID('MyDb_rehearsal_20260810_0930') IS NOT NULL", script);
        Assert.Contains("retained as the evidence", script);
    }

    // ── the scheduled variant (#259) ────────────────────────────────────────────

    /// <summary>
    /// A job runs the same text weekly, so the scheduled name is STABLE and each run clears its
    /// own leftover first - which by then has been seen in the job history.
    /// </summary>
    [Fact]
    public void TheScheduledScriptClearsItsOwnLeftoverBeforeRestoring()
    {
        var scratch = RehearsalPlanner.ScheduledScratchName("MyDb");
        Assert.Equal("MyDb_rehearsal_scheduled", scratch);

        var script = RehearsalPlanner.BuildScheduledScript(
            $"RESTORE DATABASE [{scratch}] FROM DISK = N'D:/b.bak' WITH RECOVERY;", scratch);

        var preDrop = script.IndexOf("DROP DATABASE", StringComparison.Ordinal);
        var restore = script.IndexOf("RESTORE DATABASE", StringComparison.Ordinal);
        var checkdb = script.IndexOf("DBCC CHECKDB", StringComparison.Ordinal);
        var postDrop = script.LastIndexOf("DROP DATABASE", StringComparison.Ordinal);

        Assert.True(preDrop < restore && restore < checkdb && checkdb < postDrop,
            "order must be pre-drop, restore, CHECKDB, post-drop");
        Assert.Contains("safe by construction", script);
    }

    // ── the command's construction promises ─────────────────────────────────────

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    /// <summary>A wired restore screen with a chain selected, connected, in the widest mode.</summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier)>
        ReadyAsync()
    {
        var (vm, sql, notifier, _) = await ReadyWithHistoryAsync();
        return (vm, sql, notifier);
    }

    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql, FakeRunNotifier notifier,
            FakeOperationHistoryStore history)>
        ReadyWithHistoryAsync()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());

        var sql = new FakeSqlServerService
        {
            FileHeaders =
            {
                [@"D:\drop\MyDb.bak"] =
                [
                    new BackupHistoryEntry
                    {
                        DatabaseName = "MyDb",
                        Type = BackupType.Full,
                        StartedAt = T0.AddHours(-1),
                        FinishedAt = T0.AddHours(-1).AddMinutes(1),
                        CheckpointLsn = 100m,
                        Position = 1,
                        Files = [@"D:\drop\MyDb.bak"]
                    }
                ]
            },
            FileList =
            [
                new FileMoveOption { LogicalName = "MyDb", PhysicalName = @"D:\SQL\MyDb.mdf", Type = "D" },
                new FileMoveOption { LogicalName = "MyDb_log", PhysicalName = @"L:\SQL\MyDb_log.ldf", Type = "L" }
            ]
        };

        var notifier = new FakeRunNotifier();
        var history = new FakeOperationHistoryStore();
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            history, TestAuditStores.Temp(), notifier)
        {
            Mode = AppMode.Pro
        };
        vm.RefreshContainers();

        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = @"D:\drop\MyDb.bak";
        await vm.Inventory.LoadAsync(vm.CurrentLocation!);
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        vm.ConnectedServer = Server();
        vm.IsConnectedToServer = true;

        return (vm, sql, notifier, history);
    }

    [Fact]
    public async Task ARehearsalRunsScratchCheckdbAndGuardedDrop()
    {
        var (vm, sql, notifier) = await ReadyAsync();

        await vm.RehearseCommand.ExecuteAsync(null);

        var script = Assert.Single(sql.ExecutedScripts);

        Assert.Contains("_rehearsal_", script);
        Assert.DoesNotContain("REPLACE", script);           // nothing to replace, ever
        Assert.Contains("MOVE N'MyDb'", script);            // every file relocated
        Assert.Contains("MOVE N'MyDb_log'", script);
        Assert.Contains("DBCC CHECKDB", script);
        Assert.Contains("DROP DATABASE", script);

        // The receipt and the announcement both say what this was - and the announcement names
        // the database being PROVEN, not the scratch copy it was proven on.
        var done = notifier.Sent.Single(n => n.Operation == "Rehearsal" && n.Phase == RunPhase.Succeeded);
        Assert.Equal("MyDb", done.Subject);
    }

    /// <summary>The whole design promise: a name that exists refuses, before anything runs.</summary>
    [Fact]
    public async Task AScratchNameThatSomehowExistsRefuses()
    {
        var (vm, sql, _) = await ReadyAsync();
        sql.RecoveryState = new DatabaseRecoveryState(true, "ONLINE", "MULTI_USER");

        await vm.RehearseCommand.ExecuteAsync(null);

        Assert.Empty(sql.ExecutedScripts);
        Assert.True(vm.HasError);
        Assert.Contains("refused", vm.StatusMessage);
    }

    /// <summary>Rehearsal is part of the checking machinery - the widest mode only (#176).</summary>
    [Fact]
    public async Task TheNarrowerModesDoNotOfferIt()
    {
        var (vm, _, _) = await ReadyAsync();

        Assert.True(vm.CanRehearse);

        vm.Mode = AppMode.Standard;

        Assert.False(vm.CanRehearse);
    }

    [Fact]
    public async Task TheHistoryReceiptSaysRehearsal()
    {
        var (vm, _, _, history) = await ReadyWithHistoryAsync();

        await vm.RehearseCommand.ExecuteAsync(null);

        var entry = Assert.Single(history.Entries);
        Assert.Equal("Rehearsal", entry.Kind);
        Assert.Contains("_rehearsal_", entry.TargetDatabase);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
    }
}
