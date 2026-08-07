using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Restore screen restoring from either medium (#149, #165).
///
/// The claim this whole change rests on: only two things ever cared where a backup lives - where
/// the list comes from, and how a RESTORE addresses a file. Everything else - the working set, the
/// chain, the restore points, the options, the script - is the same code either way. These are the
/// tests that hold that claim up, so a future change that quietly forks the two paths fails here.
/// </summary>
public class BackupMediumTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupHistoryEntry Entry(BackupType type, DateTime at, params string[] files) => new()
    {
        DatabaseName = "MyDb",
        ServerName = "SRV01",
        Type = type,
        StartedAt = at,
        FinishedAt = at.AddMinutes(2),
        CheckpointLsn = type == BackupType.Full ? 100 : null,
        DatabaseBackupLsn = type == BackupType.Differential ? 100 : null,

        // Advances with the clock: an LSN is a position in the log, so two backups cannot end at
        // the same one and hold different transactions.
        LastLsn = 100 + (decimal)(at - T0).TotalMinutes,
        Files = files.Length == 0 ? [@"\\nas01\sql\full.bak"] : files
    };

    private static (RestoreViewModel vm, FakeSqlServerService sql, ServerConnection source) New(
        params BackupHistoryEntry[] history)
    {
        var store = new FakeCredentialStore();
        var source = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
        store.Config.Servers.Add(source);

        var sql = new FakeSqlServerService { BackupHistory = history.ToList() };

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store,
            log: null, history: new FakeRestoreHistoryStore());

        vm.RefreshContainers();
        vm.SelectedMedium = BackupMedium.SharedPath;
        vm.SourceServer = vm.SourceServers.Single(s => s.Id == source.Id);

        return (vm, sql, source);
    }

    // ── the whole workflow, on a shared path ────────────────────────────────────

    /// <summary>
    /// The same timeline the blob path fills. If this works, the restore points, the chain, the
    /// point-in-time window and the options all work too - they are the same objects.
    /// </summary>
    [Fact]
    public async Task BackupsOnASharedPathReachTheTimeline()
    {
        var (vm, _, _) = New(
            Entry(BackupType.Full, T0),
            Entry(BackupType.TransactionLog, T0.AddHours(1), @"\\nas01\sql\log1.trn"));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.NotEmpty(vm.Timeline.Points);
    }

    /// <summary>
    /// And the script that comes out addresses them as DISK, because the FILES say where they live -
    /// not because the screen is in a mode.
    /// </summary>
    [Fact]
    public async Task TheScriptRestoresFromDiskWithoutTheScreenBeingToldTo()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();
        vm.TargetDatabaseName = "MyDb_Restored";

        Assert.Contains(@"DISK = N'\\nas01\sql\full.bak'", vm.GeneratedScript);
        Assert.DoesNotContain("URL =", vm.GeneratedScript);
    }

    [Fact]
    public async Task TheHistoryIsReadFromTheServerThatTookTheBackups()
    {
        var (vm, _, source) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.Inventory.LoadedFromSharedPath);
        Assert.Equal(source.Id, vm.Inventory.LoadedFrom!.SourceServer!.Id);
    }

    // ── what changing the medium has to invalidate ──────────────────────────────

    /// <summary>
    /// Switching medium throws away what was loaded, for the reason #112 exists: an armed Execute
    /// that survives the switch is aimed at backups nobody is looking at any more - and here they
    /// are not merely a different container, they are a different KIND of device.
    /// </summary>
    [Fact]
    public async Task SwitchingMediumDropsWhatTheOtherOneLoaded()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedDatabaseName = "MyDb";
        Assert.True(vm.Inventory.BackupsLoaded);

        vm.SelectedMedium = BackupMedium.AzureBlob;

        Assert.False(vm.Inventory.BackupsLoaded);
        Assert.Null(vm.Inventory.LoadedFrom);
        Assert.Empty(vm.Timeline.Points);
    }

    /// <summary>
    /// Same for the substitution. It changes which FILES the chain names, so a chain built before
    /// it was typed is a chain of different files.
    /// </summary>
    [Fact]
    public async Task ChangingThePathSubstitutionDropsWhatWasLoadedUnderTheOldOne()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.Inventory.BackupsLoaded);

        vm.TargetPathPrefix = @"\\SRV01\SQLBackups";

        Assert.False(vm.Inventory.BackupsLoaded);
    }

    [Fact]
    public async Task ChangingTheSourceServerDropsWhatTheOldOneLoaded()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        Assert.True(vm.Inventory.BackupsLoaded);

        vm.SourceServer = null;

        Assert.False(vm.Inventory.BackupsLoaded);
    }

    // ── what is applicable under which medium ───────────────────────────────────

    /// <summary>
    /// FROM DISK uses no credential at all, so the panel is inapplicable rather than unsatisfied.
    /// Leaving it up presents something that can never be satisfied and sends people to fix the
    /// wrong thing.
    /// </summary>
    [Fact]
    public void TheServerSideCredentialIsNotApplicableToASharedPath()
    {
        var (vm, _, _) = New();

        Assert.False(vm.CredentialApplies);

        vm.SelectedMedium = BackupMedium.AzureBlob;
        Assert.True(vm.CredentialApplies);
    }

    [Fact]
    public async Task WithNoSourceServerChosenNothingIsRead()
    {
        var (vm, sql, _) = New(Entry(BackupType.Full, T0));
        vm.SourceServer = null;

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(vm.Inventory.BackupsLoaded);
        Assert.Empty(sql.CheckedPaths);
    }

    // ── the advice that has to arrive before the failure ────────────────────────

    /// <summary>
    /// The one failure here that can end in a SUCCESSFUL restore of the wrong backup: a local path
    /// on the source may resolve on the target to the target's OWN drive of that letter. Said as
    /// soon as the history is read, not when the restore goes wrong.
    /// </summary>
    [Fact]
    public async Task ALocalSourcePathIsWarnedAboutAsSoonAsTheHistoryIsRead()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0, @"E:\SQLBackups\MyDb\full.bak"));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.True(vm.HasPathAdvice);
        Assert.Contains("target's own drive", vm.PathAdvice);
    }

    [Fact]
    public async Task NoWarningWhenTheBackupsWereAlreadyOnAShare()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0));

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.False(vm.HasPathAdvice);
    }

    // ── the check that has to happen before WITH REPLACE ────────────────────────

    private async Task<(RestoreViewModel vm, FakeSqlServerService sql, List<string> log)> ArmedAsync(
        params BackupHistoryEntry[] history)
    {
        var (vm, sql, _) = New(history);

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        return (vm, sql, []);
    }

    /// <summary>
    /// The reason a shared path needs a preflight of its own.
    ///
    /// This app's process can see a share the SQL Server service account cannot, and the RESTORE
    /// runs as that account on the target host. Proceeding on a check made from here would mean
    /// failing with "Operating system error 5" AFTER WITH REPLACE had dropped the database being
    /// restored over.
    /// </summary>
    [Fact]
    public async Task ARestoreIsRefusedWhenTheTargetCannotReadTheBackupFiles()
    {
        var (vm, sql, log) = await ArmedAsync(Entry(BackupType.Full, T0));
        sql.UnreadablePaths[@"\\nas01\sql\full.bak"] = BackupFileProblem.AccessDenied;

        var target = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" };

        var result = await vm.PreflightAsync(target, log.Add);

        Assert.False(result.CanProceed);

        // The service account, not the file: the error sends people to check the file, and the file
        // is almost never the problem.
        Assert.Contains("service account", result.Refusal);
        Assert.Contains("SRV02", result.Refusal);
    }

    [Fact]
    public async Task ARestoreProceedsWhenTheTargetCanReadThemAll()
    {
        var (vm, sql, log) = await ArmedAsync(Entry(BackupType.Full, T0));

        var target = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" };

        var result = await vm.PreflightAsync(target, log.Add);

        Assert.True(result.CanProceed);
        Assert.Equal(@"\\nas01\sql\full.bak", Assert.Single(sql.CheckedPaths));
    }

    /// <summary>
    /// The question is asked about every file in the chain, not just the full - a chain given three
    /// of four stripes, or a readable full and an unreadable log, fails part-way through.
    /// </summary>
    [Fact]
    public async Task EveryFileInTheChainIsAskedAbout()
    {
        var (vm, sql, log) = await ArmedAsync(
            Entry(BackupType.Full, T0),
            Entry(BackupType.TransactionLog, T0.AddHours(1), @"\\nas01\sql\log1.trn"));

        var target = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" };

        await vm.PreflightAsync(target, log.Add);

        Assert.Equal(2, sql.CheckedPaths.Count);
    }

    /// <summary>
    /// A check that could not be completed is not a check that passed. Proceeding here would put the
    /// whole point of this preflight - finding out BEFORE the target is dropped - back where it was.
    /// </summary>
    [Fact]
    public async Task ACheckThatCannotBeCompletedRefusesRatherThanProceeds()
    {
        var (vm, sql, log) = await ArmedAsync(Entry(BackupType.Full, T0));
        sql.ThrowOnCheck = new InvalidOperationException("the network is down");

        var target = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" };

        var result = await vm.PreflightAsync(target, log.Add);

        Assert.False(result.CanProceed);
        Assert.Contains("the network is down", result.Refusal);
    }

    [Fact]
    public async Task NoWarningOnceASubstitutionHasBeenGiven()
    {
        var (vm, _, _) = New(Entry(BackupType.Full, T0, @"E:\SQLBackups\MyDb\full.bak"));

        vm.SourcePathPrefix = @"E:\SQLBackups";
        vm.TargetPathPrefix = @"\\SRV01\SQLBackups";

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        Assert.False(vm.HasPathAdvice);
    }
}
