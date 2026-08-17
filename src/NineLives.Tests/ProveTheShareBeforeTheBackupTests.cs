using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Both accounts are proven against the shared folder before anything is backed up (#452).
///
/// The screen's own text has always said it: the source writes here as its own service account and
/// the target reads here as its own, two different accounts and routinely not the same one. But
/// the check that existed ran on a FILE, so it could not run until a file was there - which meant
/// after a full backup had been written. A share the target could not read cost the whole backup
/// first, and the source's write was never checked ahead of time at all.
/// </summary>
public class ProveTheShareBeforeTheBackupTests
{
    private const string Folder = @"\\nas01\sql";

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql) Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s2", Name = "SRV02", ServerName = "SRV02" });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var vm = new CopyDatabaseViewModel(store, sql);
        vm.Refresh();
        return (vm, sql);
    }

    /// <summary>Everything answered, going through a folder rather than a container.</summary>
    private static async Task<(CopyDatabaseViewModel vm, FakeSqlServerService sql)> ReadyAsync()
    {
        var (vm, sql) = Screen();

        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = Folder;

        // The sweep runs on Generate, not on every keystroke - re-asking two production instances
        // per character typed was the disease #285 cured. Still before anything is written.
        vm.GenerateCommand.Execute(null);
        await vm.WaitForChecksForTests();
        return (vm, sql);
    }

    private static string Key(string server, bool write)
        => $"{server}|{Folder}|{(write ? "write" : "read")}";

    // ── both sides are asked, as themselves ─────────────────────────────────────

    [Fact]
    public async Task BothAccountsAreAskedAndEachAboutItsOwnQuestion()
    {
        var (_, sql) = await ReadyAsync();

        // The source has to CREATE here; the target only has to see it.
        Assert.Contains(Key("SRV01", write: true), sql.FolderChecks);
        Assert.Contains(Key("SRV02", write: false), sql.FolderChecks);
    }

    [Fact]
    public async Task WhenBothCanUseItTheScreenSaysSoAndDoesNotRefuse()
    {
        var (vm, _) = await ReadyAsync();

        Assert.True(vm.SharedFolderProven);
        Assert.Contains("SRV01", vm.SourceCanWriteHere);
        Assert.Contains("SRV02", vm.TargetCanReadHere);
        Assert.Empty(vm.SharedFolderRefusal);
    }

    // ── and refused when one of them cannot ─────────────────────────────────────

    /// <summary>
    /// The failure this exists for, and the reason the message names the ACCOUNT: an instance
    /// running as a local account has no identity on the network, so it cannot open any share -
    /// while the share opens perfectly from the operator's own logged-in session.
    /// </summary>
    [Fact]
    public async Task ATargetThatCannotSeeTheFolderRefusesBeforeAnythingIsWritten()
    {
        var (vm, sql) = Screen();
        sql.FolderAccess[Key("SRV02", write: false)] =
            new FolderAccessCheck(Folder, FolderAccess.NotVisible, "Operating system error 5.");

        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = Folder;
        vm.GenerateCommand.Execute(null);
        await vm.WaitForChecksForTests();

        Assert.False(vm.SharedFolderProven);
        Assert.True(vm.IsRefused);
        Assert.Contains("SRV02", vm.Refusal);
        Assert.Contains("service account", vm.Refusal);
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public async Task ASourceThatCannotWriteRefusesToo()
    {
        var (vm, sql) = Screen();
        sql.FolderAccess[Key("SRV01", write: true)] =
            new FolderAccessCheck(Folder, FolderAccess.CannotWrite, "Access is denied.");

        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = Folder;
        vm.GenerateCommand.Execute(null);
        await vm.WaitForChecksForTests();

        Assert.True(vm.IsRefused);
        Assert.Contains("SRV01", vm.Refusal);
        Assert.Contains("cannot create", vm.Refusal);
    }

    /// <summary>
    /// A check that could not be COMPLETED is not a check that failed. An instance that never
    /// answers - a UNC host that does not resolve, so the statement hangs until the timeout - must
    /// not block a copy that would have worked. Same rule the space check follows.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatNeverAnsweredDoesNotBlockTheCopy()
    {
        var (vm, sql) = Screen();
        sql.FolderAccess[Key("SRV02", write: false)] =
            new FolderAccessCheck(Folder, FolderAccess.Unreachable, "Execution Timeout Expired.");

        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.Medium = BackupMedium.SharedPath;
        vm.SharedPathRoot = Folder;
        vm.GenerateCommand.Execute(null);
        await vm.WaitForChecksForTests();

        Assert.False(vm.SharedFolderProven);
        Assert.Empty(vm.SharedFolderRefusal);

        // Said, though - not knowing is worth reporting even when it is not worth refusing over.
        Assert.Contains("never answered", vm.TargetCanReadHere);
    }

    // ── it is a shared-folder question only ─────────────────────────────────────

    [Fact]
    public async Task GoingThroughACloudContainerAsksNothingAboutFolders()
    {
        var (vm, sql) = Screen();
        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadSourceDatabasesCommand.ExecuteAsync(null);
        vm.SourceDatabase = "MyDb";
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.Medium = BackupMedium.AzureBlob;
        vm.GenerateCommand.Execute(null);
        await vm.WaitForChecksForTests();

        Assert.Empty(sql.FolderChecks);
        Assert.Empty(vm.SourceCanWriteHere);
    }

    // ── what it does and does not prove ─────────────────────────────────────────

    /// <summary>
    /// The wording has to stay narrower than the check. xp_fileexist proves the folder is visible
    /// to that account; it does not prove a backup inside it will be readable, because a share can
    /// be traversable and still refuse the read. The file-level check after the backup is still
    /// the authoritative one, and claiming more here would make it look redundant.
    /// </summary>
    [Fact]
    public void TheVisibilityVerdictDoesNotClaimReadability()
    {
        var seen = new FolderAccessCheck(Folder, FolderAccess.Ok);

        var said = seen.Explain("SRV02", wasWriteCheck: false);

        Assert.Contains("can see", said);
        Assert.DoesNotContain("readable", said);
        Assert.DoesNotContain("can read the backup", said);
    }

    [Fact]
    public void AnUnreachableFolderPointsAtTheMachineNameFirst()
    {
        var check = new FolderAccessCheck(
            Folder, FolderAccess.Unreachable, "timeout");

        Assert.Contains("machine name", check.Explain("SRV02", wasWriteCheck: false));
    }
}
