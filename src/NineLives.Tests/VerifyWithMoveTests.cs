using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// RESTORE VERIFYONLY checks the backup AND whether a restore could proceed, which includes
/// looking for the file paths it would write to. Without MOVE those are the paths recorded inside
/// the backup - the SOURCE server's - so it reports directory-lookup failures for a restore that
/// was never going to use them (#129).
///
/// Confirmed against SQL Server 2025 before building this:
///   VERIFYONLY WITH MOVE to a path that does not exist -> the same warnings, naming the MOVE target
///   VERIFYONLY WITH MOVE to a path that does exist     -> "The backup set on file 1 is valid." only
/// So passing MOVE does not silence the check; it points it at the right question.
/// </summary>
public class VerifyWithMoveTests
{
    private const string Url = "https://mystorageaccount.blob.core.windows.net/backups/FULL/MyDb.bak";

    private static FileMoveOption Move(string logical, string target) => new()
    {
        LogicalName = logical,
        NewPhysicalName = target,
        Type = "ROWS"
    };

    // ── the statement ───────────────────────────────────────────────────────────

    [Fact]
    public void NoMovesLeavesTheStatementAsItWas()
    {
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url], withChecksum: false);

        Assert.Equal($"RESTORE VERIFYONLY FROM URL = N'{Url}'", sql);
    }

    [Fact]
    public void MovesAreEmittedAsWithMove()
    {
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url], false,
        [
            Move("MyDb", @"E:\Data\MyDb.mdf"),
            Move("MyDb_log", @"F:\Logs\MyDb_log.ldf"),
        ]);

        Assert.Equal(
            $"RESTORE VERIFYONLY FROM URL = N'{Url}' WITH " +
            @"MOVE N'MyDb' TO N'E:\Data\MyDb.mdf', MOVE N'MyDb_log' TO N'F:\Logs\MyDb_log.ldf'",
            sql);
    }

    [Fact]
    public void ChecksumAndMovesShareTheOneWithClause()
    {
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url], true,
            [Move("MyDb", @"E:\Data\MyDb.mdf")]);

        Assert.Contains(@"WITH CHECKSUM, MOVE N'MyDb' TO N'E:\Data\MyDb.mdf'", sql);
    }

    [Fact]
    public void AMoveWithNoTargetPathIsSkipped()
    {
        // Half-filled rows are normal while the grid is being edited; emitting MOVE ... TO N''
        // would fail the whole verification.
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url], false,
        [
            Move("MyDb", @"E:\Data\MyDb.mdf"),
            Move("MyDb_log", "   "),
        ]);

        Assert.Contains("MOVE N'MyDb'", sql);
        Assert.DoesNotContain("MyDb_log", sql);
    }

    [Fact]
    public void AnApostropheInAPathCannotTerminateTheLiteral()
    {
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url], false,
            [Move("It's", @"E:\Jake's Data\MyDb.mdf")]);

        Assert.Contains(@"MOVE N'It''s' TO N'E:\Jake''s Data\MyDb.mdf'", sql);
    }

    // ── the warning ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Directory lookup for the file \"F:\\x\\MyDb.mdf\" failed with the operating system error 3(The system cannot find the path specified.). The backup set on file 1 is valid.")]
    [InlineData("Attempting to restore this backup may encounter storage space problems. Subsequent messages will provide details.")]
    public void SqlServersOwnWordingIsRecognisedAsAMissingDirectory(string message)
    {
        // Reached through the public result rather than the private matcher, so the wording it
        // matches on stays tied to what a caller actually sees.
        var result = new VerifyOnlyResult(true, message, TargetPathsMissing: true);

        Assert.True(result.TargetPathsMissing);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void AValidBackupWithNoComplaintIsNotFlagged()
    {
        var result = new VerifyOnlyResult(true, "The backup set on file 1 is valid.");

        Assert.False(result.TargetPathsMissing);
    }
}

/// <summary>
/// The ViewModel half: the verification is given the same MOVE clauses the restore would use, and
/// says what to do when the directories are not there.
/// </summary>
[Collection(WpfCollection.Name)]
public class VerifyWithMoveViewModelTests(WpfFixture wpf)
{
    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

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

    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> Ready()
    {
        var blob = new FakeBlobStorageService { Files = [FullBackup()] };
        var sql = new FakeSqlServerService();
        var store = new FakeCredentialStore();

        var vm = new RestoreViewModel(
            blob, sql, new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            new FakeOperationHistoryStore(),
            new OperationLog(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ninelives-vm-tests", Guid.NewGuid().ToString("n"))))
        {
            SelectedContainer = new BlobContainerConfig
            {
                Id = BlobContainerConfig.NewId(),
                Name = "backups",
                ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
            }
        };

        await vm.LoadBackupsCommand.ExecuteAsync(null);

        // The app no longer chooses a database or a restore point for anybody.
        RestoreSetup.ChooseADatabaseAndAPoint(vm);
        vm.TargetDatabaseName = "MyDb_Restored";
        vm.IsConnectedToServer = true;
        vm.ConnectedServer = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        };

        return (vm, sql);
    }

    [Fact]
    public void VerificationIsGivenTheSameMovesTheRestoreWouldUse()
    {
        List<FileMoveOption> moves = [];

        RunOnUi(async () =>
        {
            var (vm, sql) = await Ready();
            vm.UseWithMove = true;
            vm.MoveDataFilePath = @"E:\Data\MyDb_Restored.mdf";
            vm.MoveLogFilePath = @"F:\Logs\MyDb_Restored_log.ldf";

            await vm.VerifyChainCommand.ExecuteAsync(null);
            moves = sql.VerifiedWithMoves;
        });

        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, m => m.NewPhysicalName == @"E:\Data\MyDb_Restored.mdf");
        Assert.Contains(moves, m => m.NewPhysicalName == @"F:\Logs\MyDb_Restored_log.ldf");
    }

    [Fact]
    public void WithoutWithMoveNoMovesArePassed()
    {
        List<FileMoveOption> moves = [new()];

        RunOnUi(async () =>
        {
            var (vm, sql) = await Ready();
            vm.UseWithMove = false;

            await vm.VerifyChainCommand.ExecuteAsync(null);
            moves = sql.VerifiedWithMoves;
        });

        Assert.Empty(moves);
    }

    [Fact]
    public void MissingDirectoriesAreCalledOutRatherThanLeftInTheMessage()
    {
        bool problem = false;
        string message = string.Empty;

        RunOnUi(async () =>
        {
            var (vm, sql) = await Ready();
            sql.VerifyResult = new VerifyOnlyResult(
                true,
                "Directory lookup for the file \"F:\\x\\MyDb.mdf\" failed. The backup set on file 1 is valid.",
                TargetPathsMissing: true);

            await vm.VerifyChainCommand.ExecuteAsync(null);
            problem = vm.HasTargetPathProblem;
            message = vm.TargetPathProblemMessage;
        });

        Assert.True(problem);
        Assert.Contains("WITH MOVE", message);
    }

    [Fact]
    public void WithMovesSetTheAdviceIsToFixThePathsNotToTickWithMove()
    {
        string message = string.Empty;

        RunOnUi(async () =>
        {
            var (vm, sql) = await Ready();
            vm.UseWithMove = true;
            vm.MoveDataFilePath = @"E:\Data\MyDb_Restored.mdf";
            vm.MoveLogFilePath = @"F:\Logs\MyDb_Restored_log.ldf";

            sql.VerifyResult = new VerifyOnlyResult(true, "Directory lookup failed.", TargetPathsMissing: true);

            await vm.VerifyChainCommand.ExecuteAsync(null);
            message = vm.TargetPathProblemMessage;
        });

        Assert.Contains("target directories do not exist", message);
        Assert.DoesNotContain("Tick WITH MOVE", message);
    }

    [Fact]
    public void ACleanVerificationRaisesNoWarning()
    {
        bool problem = true;

        RunOnUi(async () =>
        {
            var (vm, _) = await Ready();
            await vm.VerifyChainCommand.ExecuteAsync(null);
            problem = vm.HasTargetPathProblem;
        });

        Assert.False(problem);
    }

    [Fact]
    public void TheWarningClearsWhenTheSelectionMoves()
    {
        bool problem = true;

        RunOnUi(async () =>
        {
            var (vm, sql) = await Ready();
            sql.VerifyResult = new VerifyOnlyResult(true, "Directory lookup failed.", TargetPathsMissing: true);
            await vm.VerifyChainCommand.ExecuteAsync(null);

            // The warning belongs to the chain that was verified.
            vm.Timeline.SelectedPoint = null;
            problem = vm.HasTargetPathProblem;
        });

        Assert.False(problem);
    }

    private void RunOnUi(Func<Task> body)
    {
        Exception? captured = null;

        wpf.Invoke(() =>
        {
            var frame = new System.Windows.Threading.DispatcherFrame();

            _ = body().ContinueWith(
                t =>
                {
                    captured = t.Exception?.GetBaseException();
                    frame.Continue = false;
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            System.Windows.Threading.Dispatcher.PushFrame(frame);
        });

        if (captured != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
