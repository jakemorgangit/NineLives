using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The script on screen is the script that runs.
///
/// This is the invariant the whole Restore screen rests on: people read the generated T-SQL before
/// executing it against production, and if an option can change without the script following, the
/// thing they read is not the thing that runs. Four options - both WITH MOVE paths, the STANDBY
/// undo file and STATS - had no change handler at all, so editing them changed the display and
/// nothing else.
///
/// Every test here fails against the code before that was fixed.
/// </summary>
public class ScriptStaysInStepTests
{
    private readonly FakeBlobStorageService _blob = new();
    private readonly FakeSqlServerService _sql = new();
    private readonly FakeCredentialStore _store = new();

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private RestoreViewModel NewViewModel() => new(
        _blob, _sql, new BackupChainBuilder(), new RestoreScriptGenerator(), _store,
        new OperationLog(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ninelives-vm-tests", Guid.NewGuid().ToString("n"))),
        new FakeRestoreHistoryStore());

    private static BackupFileInfo FullBackup() => new()
    {
        BlobName = "FULL/SRV01/MyDb/20260110_220000.bak",
        BlobUrl = "https://mystorageaccount.blob.core.windows.net/backups/FULL/SRV01/MyDb/20260110_220000.bak",
        Type = BackupType.Full,
        InferredServerName = "SRV01",
        InferredDatabaseName = "MyDb",
        SizeBytes = 1000,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    private async Task<RestoreViewModel> Loaded()
    {
        _blob.Files = [FullBackup()];

        var vm = NewViewModel();
        vm.SelectedContainer = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // The app no longer chooses a database or a restore point for anybody.
        RestoreSetup.ChooseADatabaseAndAPoint(vm);
        vm.TargetDatabaseName = "MyDb_Restored";
        return vm;
    }

    // ── WITH MOVE ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TypingADataFilePathPutsItInTheScript()
    {
        var vm = await Loaded();
        vm.UseWithMove = true;

        vm.MoveDataFilePath = @"E:\SQLData\MyDb_Restored.mdf";
        vm.MoveLogFilePath = @"F:\SQLLogs\MyDb_Restored_log.ldf";

        Assert.Contains(@"MOVE N'MyDb' TO N'E:\SQLData\MyDb_Restored.mdf'", vm.GeneratedScript);
        Assert.Contains(@"MOVE N'MyDb_log' TO N'F:\SQLLogs\MyDb_Restored_log.ldf'", vm.GeneratedScript);
    }

    /// <summary>
    /// The worst shape of the bug. WITH MOVE ticked and paths visibly filled in, but the script
    /// carried no MOVE clause - so SQL Server would restore to the file paths baked into the
    /// backup, which are the SOURCE database's own files.
    /// </summary>
    [Fact]
    public async Task WithMoveTickedNeverProducesAScriptWithNoMoveClause()
    {
        var vm = await Loaded();

        vm.UseWithMove = true;
        vm.MoveDataFilePath = @"E:\SQLData\MyDb_Restored.mdf";
        vm.MoveLogFilePath = @"F:\SQLLogs\MyDb_Restored_log.ldf";

        Assert.Contains("MOVE N'", vm.GeneratedScript);
    }

    [Fact]
    public async Task EditingAFetchedLogicalFilesTargetPathReachesTheScript()
    {
        var vm = await Loaded();
        vm.UseWithMove = true;

        // As RESTORE FILELISTONLY would have populated it.
        vm.FetchedFileMoves =
        [
            new FileMoveOption
            {
                LogicalName = "MyDb",
                PhysicalName = @"C:\Data\MyDb.mdf",
                NewPhysicalName = @"C:\Data\MyDb.mdf",
                Type = "ROWS"
            }
        ];
        vm.HasFetchedFileMoves = true;

        // The user retypes it in the grid because C: is full.
        vm.FetchedFileMoves[0].NewPhysicalName = @"E:\Data\MyDb.mdf";

        Assert.Contains(@"TO N'E:\Data\MyDb.mdf'", vm.GeneratedScript);
        Assert.DoesNotContain(@"TO N'C:\Data\MyDb.mdf'", vm.GeneratedScript);
    }

    // ── STANDBY ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StandbyWithNoUndoFileProducesNoScriptRatherThanAnEmptyLiteral()
    {
        var vm = await Loaded();

        vm.Options.RecoveryMode = RecoveryMode.Standby;

        // STANDBY = '' is the last statement of the chain, so it fails AFTER the target has been
        // dropped and overwritten. Refusing to produce a script is the only safe answer.
        Assert.False(vm.HasScript);
        Assert.Empty(vm.GeneratedScript);
    }

    [Fact]
    public async Task StandbyWithAnUndoFileProducesTheClause()
    {
        var vm = await Loaded();

        vm.Options.RecoveryMode = RecoveryMode.Standby;
        vm.Options.StandbyFilePath = @"E:\Standby\MyDb.undo";

        Assert.Contains(@"STANDBY = 'E:\Standby\MyDb.undo'", vm.GeneratedScript);
    }

    [Fact]
    public async Task TheEmptyPaneSaysWhyWhenStandbyHasNoUndoFile()
    {
        var vm = await Loaded();
        vm.Options.RecoveryMode = RecoveryMode.Standby;

        // The script builds itself live now, so the explanation is a passive hint where the
        // script would be - an error dialog per keystroke would be noise.
        Assert.False(vm.HasScript);
        Assert.Contains("undo file", vm.ScriptBlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── STATS ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangingStatsPercentReachesTheScript()
    {
        var vm = await Loaded();

        vm.Options.StatsPercent = 25;

        Assert.Contains("STATS = 25;", vm.GeneratedScript);
        Assert.DoesNotContain("STATS = 10;", vm.GeneratedScript);
    }
}
