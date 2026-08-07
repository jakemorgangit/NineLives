using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Copying a database from one server onto another in one action (#105).
///
/// Two things make this more dangerous than either half alone, and most of these are about one or
/// the other.
///
/// TWO servers are involved and only one is being protected: the source is read at full speed, the
/// target is overwritten. And a PARTIAL run is not a failure - a backup that succeeded with a
/// restore that did not has left a perfectly good backup behind, and reporting that as "failed" is
/// how a second backup of a production database gets taken for no reason.
/// </summary>
public class CopyDatabaseTests
{
    private static OperationLog TestLog() =>
        new(Path.Combine(Path.GetTempPath(), "ninelives-copy-tests", Guid.NewGuid().ToString("n")));

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();

        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb", "OtherDb"] };
        var vm = new CopyDatabaseViewModel(store, sql, TestLog());

        vm.SourceServer = vm.Servers.First();
        vm.TargetServer = vm.Servers.Last();
        vm.Container = vm.Containers.Single();

        return (vm, sql);
    }

    private static async Task<(CopyDatabaseViewModel vm, FakeSqlServerService sql)> ReadyAsync(
        BackupMedium medium = BackupMedium.AzureBlob)
    {
        var (vm, sql) = New();

        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Test";

        vm.Medium = medium;
        if (medium == BackupMedium.SharedPath) vm.SharedPathRoot = @"\\nas01\sql";

        vm.GenerateCommand.Execute(null);
        return (vm, sql);
    }

    private static async Task RunAsync(CopyDatabaseViewModel vm)
    {
        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);
    }

    // ── both halves, in order, on the right servers ─────────────────────────────

    /// <summary>
    /// The backup runs on the SOURCE and the restore on the TARGET. Getting that the wrong way
    /// round would restore a production database over a test one's contents, or worse.
    /// </summary>
    [Fact]
    public async Task TheBackupRunsOnTheSourceAndTheRestoreOnTheTarget()
    {
        var (vm, sql) = await ReadyAsync();

        await RunAsync(vm);

        Assert.Equal(2, sql.ExecutedScripts.Count);
        Assert.Contains("BACKUP DATABASE", sql.ExecutedScripts[0]);
        Assert.Contains("RESTORE DATABASE", sql.ExecutedScripts[1]);

        Assert.Equal("SRV01", sql.ExecutedAgainst[0].ServerName);
        Assert.Equal("SRV02", sql.ExecutedAgainst[1].ServerName);
    }

    [Fact]
    public async Task ASuccessfulCopyReportsItselfAsCopied()
    {
        var (vm, _) = await ReadyAsync();

        await RunAsync(vm);

        Assert.Equal(CopyOutcome.Copied, vm.Outcome);
        Assert.False(vm.HasError);
    }

    /// <summary>The restore names the target database, not the source one.</summary>
    [Fact]
    public async Task TheCopyIsRestoredUnderTheNameItWasGiven()
    {
        var (vm, _) = await ReadyAsync();

        Assert.Contains("BACKUP DATABASE [MyDb]", vm.BackupScript);
        Assert.Contains("[MyDb_Test]", vm.RestoreScript);
    }

    /// <summary>
    /// The restore reads back exactly what the backup wrote. If these two ever disagree the copy
    /// restores something else, or nothing.
    /// </summary>
    [Fact]
    public async Task TheRestoreReadsTheFileTheBackupWrote()
    {
        var (vm, _) = await ReadyAsync();

        var destination = Assert.Single(vm.Destinations);

        Assert.Contains(destination, vm.BackupScript);
        Assert.Contains(destination, vm.RestoreScript);
    }

    [Fact]
    public async Task ACopyThroughAShareUsesDiskAtBothEnds()
    {
        var (vm, _) = await ReadyAsync(BackupMedium.SharedPath);

        Assert.Contains("TO DISK", vm.BackupScript);
        Assert.Contains("FROM DISK", vm.RestoreScript);
    }

    // ── a partial run is not a failure ──────────────────────────────────────────

    /// <summary>
    /// The distinction this whole outcome type exists for. A backup that succeeded and a restore
    /// that did not has left a real, usable backup behind - and the right next step is to restore
    /// from it, not to run this again and read the production database a second time.
    /// </summary>
    [Fact]
    public async Task ABackupThatSucceededWithAFailedRestoreSaysTheBackupIsStillGood()
    {
        var (vm, sql) = await ReadyAsync();
        sql.FailOnExecuteNumber = 2;

        await RunAsync(vm);

        Assert.Equal(CopyOutcome.BackupTakenRestoreFailed, vm.Outcome);
        Assert.Contains("backup is real and usable", vm.OutcomeText);
        Assert.Contains("rather than running this again", vm.OutcomeText);
    }

    /// <summary>
    /// A failed backup means nothing was restored, and any file written is not a backup - which is
    /// worth saying, because it will sit in the container looking exactly like one.
    /// </summary>
    [Fact]
    public async Task AFailedBackupStopsBeforeTheTargetIsTouched()
    {
        var (vm, sql) = await ReadyAsync();
        sql.FailOnExecuteNumber = 1;

        await RunAsync(vm);

        Assert.Equal(CopyOutcome.BackupFailed, vm.Outcome);
        Assert.Single(sql.ExecutedScripts);
        Assert.Contains("incomplete", vm.OutcomeText);
    }

    [Fact]
    public async Task AFailedBackupNeverNamesTheTargetServerAsHavingBeenChanged()
    {
        var (vm, sql) = await ReadyAsync();
        sql.FailOnExecuteNumber = 1;

        await RunAsync(vm);

        Assert.DoesNotContain(sql.ExecutedAgainst, s => s.ServerName == "SRV02");
    }

    // ── the check between the halves ────────────────────────────────────────────

    /// <summary>
    /// The question a shared path turns on, and it can only be asked once the backup exists.
    ///
    /// The SOURCE writes the file as its service account and the TARGET reads it as a different
    /// one. Those are routinely not the same account, and nothing before this point would notice.
    /// </summary>
    [Fact]
    public async Task TheTargetIsAskedWhetherItCanReadWhatTheSourceWrote()
    {
        var (vm, sql) = await ReadyAsync(BackupMedium.SharedPath);

        await RunAsync(vm);

        Assert.Equal(Assert.Single(vm.Destinations), Assert.Single(sql.CheckedPaths));
    }

    /// <summary>
    /// And when it cannot, the restore does not run - which leaves the backup intact and the target
    /// untouched, the most recoverable state this screen can produce.
    /// </summary>
    [Fact]
    public async Task ATargetThatCannotReadTheBackupStopsBeforeTheRestore()
    {
        var (vm, sql) = await ReadyAsync(BackupMedium.SharedPath);
        sql.UnreadablePaths[vm.Destinations.Single()] = BackupFileProblem.AccessDenied;

        await RunAsync(vm);

        Assert.Equal(CopyOutcome.BackupTakenRestoreFailed, vm.Outcome);
        Assert.Single(sql.ExecutedScripts);
        Assert.Contains(vm.Console, l => l.Contains("service account", StringComparison.Ordinal));
    }

    /// <summary>Blob has no share permission to check, so nothing is asked and the copy proceeds.</summary>
    [Fact]
    public async Task ACopyThroughBlobAsksNoQuestionAboutFilePermissions()
    {
        var (vm, sql) = await ReadyAsync();

        await RunAsync(vm);

        Assert.Empty(sql.CheckedPaths);
        Assert.Equal(CopyOutcome.Copied, vm.Outcome);
    }

    // ── the guard rails ─────────────────────────────────────────────────────────

    /// <summary>
    /// Onto itself under its own name is not a copy - it is a backup restored over the database it
    /// was taken from, which at best is an expensive no-op and at worst destroys it when the
    /// restore fails halfway.
    /// </summary>
    [Fact]
    public async Task RestoringADatabaseOverItselfIsRefused()
    {
        var (vm, _) = await ReadyAsync();

        vm.TargetServer = vm.SourceServer;
        vm.TargetDatabaseName = "MyDb";

        Assert.True(vm.IsRefused);
        Assert.False(vm.CanGenerate);
        Assert.False(vm.CanRun);
        Assert.Contains("over itself", vm.Refusal);
    }

    /// <summary>The same instance under a DIFFERENT name is an ordinary copy, and is allowed.</summary>
    [Fact]
    public async Task CopyingOntoTheSameInstanceUnderAnotherNameIsFine()
    {
        var (vm, _) = await ReadyAsync();

        vm.TargetServer = vm.SourceServer;
        vm.TargetDatabaseName = "MyDb_Copy";

        Assert.False(vm.IsRefused);
        Assert.True(vm.CanGenerate);
    }

    /// <summary>
    /// The confirmation names BOTH servers. The whole point of this screen is that two are
    /// involved, so a prompt naming one is a prompt somebody can read as being about the other.
    /// </summary>
    [Fact]
    public async Task TheConfirmationNamesBothServers()
    {
        var (vm, _) = await ReadyAsync();

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.IsArmed);
        Assert.Contains("SRV01", vm.ConfirmationText);
        Assert.Contains("SRV02", vm.ConfirmationText);
        Assert.Contains("MyDb_Test", vm.ConfirmationText);
    }

    [Fact]
    public async Task ChangingAnythingDisarmsTheRun()
    {
        var (vm, _) = await ReadyAsync();

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsArmed);

        vm.TargetDatabaseName = "MyDb_Other";

        Assert.False(vm.IsArmed);
    }

    // ── the source is somebody else's production server ─────────────────────────

    [Fact]
    public async Task ACopyIsCopyOnlyUntilSomebodyChangesIt()
    {
        var (vm, _) = await ReadyAsync();

        Assert.True(vm.CopyOnly);
        Assert.Contains("COPY_ONLY", vm.BackupScript);
        Assert.False(vm.WillResetTheDifferentialBase);
    }

    [Fact]
    public async Task TurningCopyOnlyOffIsSurfacedAndSaidAgainAsItRuns()
    {
        var (vm, _) = await ReadyAsync();
        vm.CopyOnly = false;

        Assert.True(vm.WillResetTheDifferentialBase);

        await RunAsync(vm);

        Assert.Contains(vm.Console, l => l.Contains("differential base", StringComparison.Ordinal));
    }

    /// <summary>
    /// Overwriting the target is usually the whole point - a test environment being refreshed - but
    /// it is the part that destroys something, so it is stated rather than left in a checkbox.
    /// </summary>
    [Fact]
    public async Task OverwritingTheTargetIsStatedRatherThanImplied()
    {
        var (vm, _) = await ReadyAsync();

        Assert.True(vm.WillOverwriteTheTarget);
        Assert.Contains("overwriting it if it already exists", vm.ConfirmationText);

        vm.WithReplace = false;

        Assert.False(vm.WillOverwriteTheTarget);
        Assert.DoesNotContain("overwriting", vm.ConfirmationText);
    }

    // ── nothing runs that was not on screen first ───────────────────────────────

    [Fact]
    public void NothingCanRunBeforeBothScriptsExist()
    {
        var (vm, _) = New();

        Assert.False(vm.HasScripts);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task ListingTheSourceDatabasesChoosesNoneOfThem()
    {
        var (vm, _) = New();

        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SourceDatabases.Count);
        Assert.Null(vm.SourceDatabase);
        Assert.False(vm.CanGenerate);
    }
}
