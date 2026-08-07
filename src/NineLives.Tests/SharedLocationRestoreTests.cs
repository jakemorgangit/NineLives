using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Restoring from a location both hosts can see (#149).
///
/// The ordering in that issue is not presentation: each step's output is the next one's input, so a
/// script cannot be generated from files the target has not confirmed it can read. These are the
/// tests for that, plus the path substitution - which is the part of this workflow that catches
/// people out, because msdb records paths as the SOURCE saw them.
/// </summary>
public class SharedLocationRestoreTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static ServerConnection Server(string name) => new()
    {
        Id = ServerConnection.NewId(),
        Name = name,
        ServerName = name
    };

    private static BackupHistoryEntry Full(params string[] files) => new()
    {
        DatabaseName = "MyDb",
        ServerName = "SRV01",
        Type = BackupType.Full,
        StartedAt = T0,
        FinishedAt = T0.AddMinutes(5),
        CheckpointLsn = 100,
        LastLsn = 200,
        Files = files
    };

    private static (SharedLocationRestoreViewModel vm, FakeSqlServerService sql) New(
        params BackupHistoryEntry[] history)
    {
        var store = new FakeCredentialStore();
        var source = Server("SRV01");
        var target = Server("SRV02");
        store.Config.Servers.Add(source);
        store.Config.Servers.Add(target);

        var sql = new FakeSqlServerService { BackupHistory = history.ToList() };
        var vm = new SharedLocationRestoreViewModel(store, sql)
        {
            SourceServer = source,
            TargetServer = target,
            TargetDatabaseName = "MyDb_Restored"
        };
        return (vm, sql);
    }

    // ── the ordering ────────────────────────────────────────────────────────────

    /// <summary>
    /// The source knowing about a backup says nothing about whether the target can reach it. A
    /// script generated before that is answered is one that fails part-way through - after WITH
    /// REPLACE has already dropped the database being restored over.
    /// </summary>
    [Fact]
    public async Task NoScriptIsOfferedBeforeTheTargetHasConfirmedItCanReadTheFiles()
    {
        var (vm, _) = New(Full(@"\\nas01\sql\full.bak"));
        await vm.ReadHistoryCommand.ExecuteAsync(null);
        vm.SelectedChain = vm.AvailableChains.Single();

        Assert.False(vm.CanGenerate);

        vm.GenerateScriptCommand.Execute(null);
        Assert.Equal(string.Empty, vm.GeneratedScript);
    }

    [Fact]
    public async Task OnceTheTargetHasConfirmedThemAScriptIsGenerated()
    {
        var (vm, _) = New(Full(@"\\nas01\sql\full.bak"));
        await vm.ReadHistoryCommand.ExecuteAsync(null);
        vm.SelectedChain = vm.AvailableChains.Single();

        await vm.VerifyFilesCommand.ExecuteAsync(null);

        Assert.True(vm.FilesVerified);
        Assert.True(vm.CanGenerate);

        vm.GenerateScriptCommand.Execute(null);
        Assert.Contains(@"DISK = N'\\nas01\sql\full.bak'", vm.GeneratedScript);
    }

    /// <summary>
    /// Choosing a different chain means different files, so anything proved about the last one no
    /// longer applies. Leaving the ticks up would let a script be generated from a verification
    /// that was never run against these backups.
    /// </summary>
    [Fact]
    public async Task ChangingWhatIsBeingRestoredWithdrawsTheVerification()
    {
        var older = Full(@"\\nas01\sql\older.bak");
        var newer = new BackupHistoryEntry
        {
            DatabaseName = "MyDb", ServerName = "SRV01", Type = BackupType.Full,
            StartedAt = T0.AddDays(1), FinishedAt = T0.AddDays(1).AddMinutes(5),
            CheckpointLsn = 300, LastLsn = 400, Files = [@"\\nas01\sql\newer.bak"]
        };

        var (vm, _) = New(older, newer);
        await vm.ReadHistoryCommand.ExecuteAsync(null);

        vm.SelectedChain = vm.AvailableChains.First();
        await vm.VerifyFilesCommand.ExecuteAsync(null);
        Assert.True(vm.FilesVerified);

        vm.SelectedChain = vm.AvailableChains.Last();

        Assert.False(vm.FilesVerified);
        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedScript);
    }

    /// <summary>Nothing is chosen for the user - the same rule the Restore screen learned.</summary>
    [Fact]
    public async Task ReadingTheHistoryChoosesNothing()
    {
        var (vm, _) = New(Full(@"\\nas01\sql\full.bak"));

        await vm.ReadHistoryCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.AvailableChains);
        Assert.Null(vm.SelectedChain);
        Assert.False(vm.CanGenerate);
    }

    // ── the target's answer ─────────────────────────────────────────────────────

    /// <summary>
    /// An unreadable file is explained in the target's terms, and the workflow stops there. Access
    /// denied is the case this whole check exists for.
    /// </summary>
    [Fact]
    public async Task AFileTheTargetCannotReadStopsTheWorkflowAndSaysWhy()
    {
        var (vm, sql) = New(Full(@"\\nas01\sql\full.bak"));
        sql.UnreadablePaths[@"\\nas01\sql\full.bak"] = BackupFileProblem.AccessDenied;

        await vm.ReadHistoryCommand.ExecuteAsync(null);
        vm.SelectedChain = vm.AvailableChains.Single();
        await vm.VerifyFilesCommand.ExecuteAsync(null);

        Assert.False(vm.FilesVerified);
        Assert.False(vm.CanGenerate);
        Assert.Contains("service account", vm.VerificationSummary);
        Assert.Contains("SRV02", vm.VerificationSummary);
    }

    // ── the path substitution ───────────────────────────────────────────────────

    /// <summary>
    /// msdb records the path the SOURCE wrote. When the target reaches the same place by another
    /// name, the target must be asked about ITS name - asking about the source's would check a
    /// path that means something different on that machine, or nothing at all.
    /// </summary>
    [Fact]
    public async Task TheTargetIsAskedAboutThePathItReachesTheFilesBy()
    {
        var (vm, sql) = New(Full(@"E:\SQLBackups\MyDb\full.bak"));
        vm.SourcePathPrefix = @"E:\SQLBackups";
        vm.TargetPathPrefix = @"\\SRV01\SQLBackups";

        await vm.ReadHistoryCommand.ExecuteAsync(null);
        vm.SelectedChain = vm.AvailableChains.Single();
        await vm.VerifyFilesCommand.ExecuteAsync(null);

        Assert.Equal(@"\\SRV01\SQLBackups\MyDb\full.bak", Assert.Single(sql.CheckedPaths));
    }

    [Fact]
    public async Task TheScriptNamesTheFilesTheWayTheTargetReachesThem()
    {
        var (vm, _) = New(Full(@"E:\SQLBackups\MyDb\full.bak"));
        vm.SourcePathPrefix = @"E:\SQLBackups";
        vm.TargetPathPrefix = @"\\SRV01\SQLBackups";

        await vm.ReadHistoryCommand.ExecuteAsync(null);
        vm.SelectedChain = vm.AvailableChains.Single();
        await vm.VerifyFilesCommand.ExecuteAsync(null);
        vm.GenerateScriptCommand.Execute(null);

        Assert.Contains(@"\\SRV01\SQLBackups\MyDb\full.bak", vm.GeneratedScript);
        Assert.DoesNotContain(@"E:\SQLBackups", vm.GeneratedScript);
    }

    /// <summary>
    /// The one failure here that can end in a SUCCESSFUL restore of the wrong backup: a local path
    /// on the source may resolve on the target to the target's own drive of that letter. Said
    /// before the check runs, not after it.
    /// </summary>
    [Fact]
    public async Task ALocalSourcePathIsWarnedAboutBeforeAnythingIsChecked()
    {
        var (vm, _) = New(Full(@"E:\SQLBackups\MyDb\full.bak"));
        await vm.ReadHistoryCommand.ExecuteAsync(null);

        vm.SelectedChain = vm.AvailableChains.Single();

        Assert.Contains("local path", vm.PathAdvice);
        Assert.Contains("target's own drive", vm.PathAdvice);
    }

    [Fact]
    public async Task NoWarningWhenTheBackupsWereAlreadyOnAShare()
    {
        var (vm, _) = New(Full(@"\\nas01\sql\full.bak"));
        await vm.ReadHistoryCommand.ExecuteAsync(null);

        vm.SelectedChain = vm.AvailableChains.Single();

        Assert.Equal(string.Empty, vm.PathAdvice);
    }

    // ── the mapping itself ──────────────────────────────────────────────────────

    [Fact]
    public void AMappingRewritesOnlyWhatItMatches()
    {
        var mapping = new BackupPathMapping(@"E:\SQLBackups", @"\\SRV01\SQLBackups");

        Assert.Equal(@"\\SRV01\SQLBackups\MyDb\full.bak", mapping.Apply(@"E:\SQLBackups\MyDb\full.bak"));

        // A chain can hold files from more than one place; rewriting one that was already
        // reachable would break a restore that would otherwise have worked.
        Assert.Equal(@"\\nas02\other\full.bak", mapping.Apply(@"\\nas02\other\full.bak"));
    }

    [Fact]
    public void AMappingIsCaseInsensitiveBecauseWindowsPathsAre()
    {
        var mapping = new BackupPathMapping(@"e:\sqlbackups", @"\\SRV01\SQLBackups");

        Assert.Equal(@"\\SRV01\SQLBackups\MyDb\full.bak", mapping.Apply(@"E:\SQLBackups\MyDb\full.bak"));
    }

    [Theory]
    [InlineData(@"E:\SQLBackups\", @"\\SRV01\SQLBackups\")]
    [InlineData(@"E:\SQLBackups", @"\\SRV01\SQLBackups")]
    public void TrailingSeparatorsOnEitherSideDoNotDoubleUp(string source, string target)
    {
        var mapping = new BackupPathMapping(source, target);

        Assert.Equal(@"\\SRV01\SQLBackups\MyDb\full.bak", mapping.Apply(@"E:\SQLBackups\MyDb\full.bak"));
    }

    [Fact]
    public void WithNoMappingPathsAreLeftExactlyAsTheyWere()
    {
        Assert.Equal(@"\\nas01\sql\full.bak", BackupPathMapping.None.Apply(@"\\nas01\sql\full.bak"));
    }

    [Theory]
    [InlineData(@"E:\SQLBackups\full.bak", true)]
    [InlineData(@"C:\backups\full.bak", true)]
    [InlineData(@"\\nas01\sql\full.bak", false)]
    public void ALocalPathIsToldApartFromAShare(string path, bool expected)
        => Assert.Equal(expected, BackupPathMapping.LooksLocalToTheSource(path));
}
