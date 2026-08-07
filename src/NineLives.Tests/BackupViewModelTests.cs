using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Taking a backup (#165).
///
/// The asymmetry these are mostly about: a restore destroys the TARGET and everybody expects that,
/// so the app guards it loudly. A backup looks harmless and can quietly damage the SOURCE - a plain
/// full moves a production database's differential base, and nothing about pressing the button says
/// so. Most of what follows is that guard.
/// </summary>
public class BackupViewModelTests
{
    /// <summary>A log in a temp directory: constructing this must not append to the user's own.</summary>
    private static OperationLog TestLog() =>
        new(Path.Combine(Path.GetTempPath(), "ninelives-backup-tests", Guid.NewGuid().ToString("n")));

    private static (BackupViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();

        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb", "OtherDb"] };
        var vm = new BackupViewModel(store, sql, TestLog());

        vm.Server = vm.Servers.Single();
        vm.Container = vm.Containers.Single();

        return (vm, sql);
    }

    private static async Task<(BackupViewModel vm, FakeSqlServerService sql)> ReadyAsync()
    {
        var (vm, sql) = New();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        return (vm, sql);
    }

    // ── nothing runs that was not on screen first ───────────────────────────────

    [Fact]
    public async Task NothingCanRunUntilThereIsAScriptToRead()
    {
        var (vm, sql) = New();

        Assert.False(vm.CanExecute);

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>
    /// Armed on the first press, run on the second - the same rule the restore has, for a different
    /// reason. A backup reads a production server flat out and can move its differential base.
    /// </summary>
    [Fact]
    public async Task TheFirstPressArmsAndTheSecondRuns()
    {
        var (vm, sql) = await ReadyAsync();

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.True(vm.IsArmed);
        Assert.Empty(sql.ExecutedScripts);

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.False(vm.IsArmed);
        Assert.Single(sql.ExecutedScripts);
    }

    /// <summary>
    /// An armed button that survives an option change is armed for something else. Changing what
    /// the backup would do has to take the safety catch back off.
    /// </summary>
    [Fact]
    public async Task ChangingAnOptionDisarmsTheButton()
    {
        var (vm, _) = await ReadyAsync();

        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.True(vm.IsArmed);

        vm.CopyOnly = false;

        Assert.False(vm.IsArmed);
    }

    /// <summary>
    /// The script on screen is what runs. A stale one is a script that does something different
    /// from what it shows, and here what it shows is the only warning anybody gets.
    /// </summary>
    [Fact]
    public async Task ChangingAnOptionRewritesTheScript()
    {
        var (vm, _) = await ReadyAsync();

        Assert.Contains("COPY_ONLY", vm.GeneratedScript);

        vm.CopyOnly = false;

        Assert.DoesNotContain("COPY_ONLY", vm.GeneratedScript);
        Assert.Contains("RESETS THE DIFFERENTIAL", vm.GeneratedScript);
    }

    // ── the source-side guard ───────────────────────────────────────────────────

    [Fact]
    public void ABackupIsCopyOnlyUntilSomebodyChangesIt()
    {
        var (vm, _) = New();

        Assert.True(vm.CopyOnly);
        Assert.False(vm.WillResetTheDifferentialBase);
    }

    [Fact]
    public void TurningCopyOnlyOffIsSurfacedAsSomethingThatHappensToTheSource()
    {
        var (vm, _) = New();

        vm.CopyOnly = false;

        Assert.True(vm.WillResetTheDifferentialBase);
    }

    /// <summary>
    /// It is also said in the console, because by then the script has scrolled out of view and this
    /// is the last chance to say it before a production database's schedule changes.
    /// </summary>
    [Fact]
    public async Task RunningANonCopyOnlyBackupSaysSoAsItStarts()
    {
        var (vm, _) = await ReadyAsync();
        vm.CopyOnly = false;

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.Console, l => l.Contains("differential base", StringComparison.Ordinal));
    }

    // ── nothing is chosen for the user ──────────────────────────────────────────

    /// <summary>
    /// The same rule the Restore screen learned, and it matters more here: the wrong choice is a
    /// production database read at full speed for several minutes.
    /// </summary>
    [Fact]
    public async Task ListingTheDatabasesChoosesNoneOfThem()
    {
        var (vm, _) = New();

        await vm.LoadDatabasesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Databases.Count);
        Assert.Null(vm.SelectedDatabase);
        Assert.False(vm.CanGenerate);
    }

    /// <summary>
    /// A database list left over from another instance invites somebody to back up a name that
    /// exists on both and mean the other one.
    /// </summary>
    [Fact]
    public async Task ChangingTheServerDropsTheDatabaseList()
    {
        var (vm, _) = await ReadyAsync();

        vm.Server = null;

        Assert.Empty(vm.Databases);
        Assert.Null(vm.SelectedDatabase);
        Assert.False(vm.CanGenerate);
    }

    // ── where it goes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ABackupToBlobIsWrittenIntoTheChosenContainer()
    {
        var (vm, _) = await ReadyAsync();

        Assert.Contains("TO URL", vm.GeneratedScript);
        Assert.Contains("https://acct.blob.core.windows.net/backups/", Assert.Single(vm.Destinations));
    }

    [Fact]
    public async Task ABackupToAShareIsWrittenToTheFolderGiven()
    {
        var (vm, _) = await ReadyAsync();

        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = @"\\nas01\sql";
        vm.GenerateCommand.Execute(null);

        Assert.Contains("TO DISK", vm.GeneratedScript);
        Assert.StartsWith(@"\\nas01\sql\MyDb\", Assert.Single(vm.Destinations));
    }

    /// <summary>
    /// Switching medium leaves the destination unanswered, so nothing can be generated against the
    /// other medium's answer.
    /// </summary>
    [Fact]
    public async Task SwitchingToASharedPathNeedsAFolderBeforeAnythingCanBeGenerated()
    {
        var (vm, _) = await ReadyAsync();

        vm.Medium = BackupMedium.SharedPath;

        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public async Task AStripedBackupNamesOneFilePerStripe()
    {
        var (vm, _) = await ReadyAsync();

        vm.Stripes = 4;

        Assert.Equal(4, vm.Destinations.Count);
    }

    // ── what happens when it goes wrong ─────────────────────────────────────────

    /// <summary>
    /// A cancelled BACKUP leaves a partial file behind. It is not a backup, and it will sit in the
    /// container looking exactly like one - so the app says so rather than reporting "cancelled"
    /// and leaving somebody to find out at restore time.
    /// </summary>
    [Fact]
    public async Task ACancelledBackupSaysThePartialFileIsNotUsable()
    {
        var (vm, sql) = await ReadyAsync();
        sql.ExecuteThrows = new OperationCanceledException();

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.Console, l => l.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedBackupReportsWhatTheServerSaid()
    {
        var (vm, sql) = await ReadyAsync();
        sql.ExecuteThrows = new InvalidOperationException("Cannot open backup device");

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("Cannot open backup device", vm.ErrorMessage);
    }

    [Fact]
    public async Task AFailureLeavesTheButtonUsableAgain()
    {
        var (vm, sql) = await ReadyAsync();
        sql.ExecuteThrows = new InvalidOperationException("nope");

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanExecute);
    }
}
