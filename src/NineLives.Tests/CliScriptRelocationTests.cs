using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// <c>script</c> can relocate (#370).
///
/// <c>restore</c> took --relocate, --data-path and --log-path; <c>script</c> took none of them.
/// So the workflow the two exist to serve together - generate the script here, hand it to a DBA,
/// run it in the change window on a freshly provisioned machine - silently dropped the WITH MOVE
/// clauses, and the restore failed at run time with a directory-not-found, after WITH REPLACE had
/// already dropped the target.
///
/// The interesting part is that relocation cannot be done offline, and <c>script</c> is otherwise
/// a verb that touches nothing. The logical file names come from RESTORE FILELISTONLY and the
/// default directories come from the instance, so both need a server. Hence --target, used only
/// to ask those two questions, and a refusal rather than a silent omission when it is absent.
/// </summary>
public class CliScriptRelocationTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static CliServices Stage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        var sql = new FakeSqlServerService
        {
            BackupHistory =
            [
                new BackupHistoryEntry
                {
                    DatabaseName = "Sales",
                    Type = BackupType.Full,
                    StartedAt = T0,
                    FinishedAt = T0.AddMinutes(5),
                    CheckpointLsn = 100,
                    Position = 1,
                    Files = [@"X:\backups\Sales_full.bak"]
                }
            ],
            FileList =
            [
                new FileMoveOption { LogicalName = "Sales", Type = "D", PhysicalName = @"X:\data\Sales.mdf" },
                new FileMoveOption { LogicalName = "Sales_log", Type = "L", PhysicalName = @"X:\log\Sales.ldf" }
            ]
        };

        return new CliServices(
            store, sql, new FakeBlobStorageService(),
            new FakeOperationHistoryStore(), new FakeRunNotifier());
    }

    private static async Task<(int exit, string script, string said)> RunScript(params string[] argv)
    {
        var services = Stage();
        var output = new StringWriter();
        var errors = new StringWriter();

        var args = CliArguments.Parse(argv, ScriptVerb.Spec);
        var exit = await ScriptVerb.RunAsync(args, services, output, errors);
        return (exit, output.ToString(), errors.ToString());
    }

    [Fact]
    public async Task RelocateMovesEveryFileToTheTargetsDefaults()
    {
        var (exit, script, said) = await RunScript(
            "--server", "SRV01", "--database", "Sales", "--target", "SRV02", "--relocate");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains(@"MOVE N'Sales' TO N'D:\Data\Sales.mdf'", script);
        Assert.Contains(@"MOVE N'Sales_log' TO N'D:\Logs\Sales.ldf'", script);
        Assert.Contains("Relocating 2 file(s)", said);
    }

    [Fact]
    public async Task ExplicitDirectoriesWin()
    {
        var (exit, script, _) = await RunScript(
            "--server", "SRV01", "--database", "Sales", "--target", "SRV02",
            "--data-path", @"E:\SQLData", "--log-path", @"F:\SQLLogs");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains(@"E:\SQLData\Sales.mdf", script);
        Assert.Contains(@"F:\SQLLogs\Sales.ldf", script);
    }

    /// <summary>
    /// Half an instruction is still a complete one: the side not given falls back to the target's
    /// own default rather than leaving those files where the backup recorded them.
    /// </summary>
    [Fact]
    public async Task OneDirectoryGivenLeavesTheOtherOnTheTargetsDefault()
    {
        var (exit, script, _) = await RunScript(
            "--server", "SRV01", "--database", "Sales", "--target", "SRV02",
            "--data-path", @"E:\SQLData");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains(@"E:\SQLData\Sales.mdf", script);
        Assert.Contains(@"D:\Logs\Sales.ldf", script);
    }

    /// <summary>
    /// The refusal that matters. Emitting a script with no MOVE in it, when MOVE is exactly what
    /// was asked for, produces a file that looks right and fails in the change window it was
    /// generated for.
    /// </summary>
    [Theory]
    [InlineData("--relocate")]
    [InlineData("--data-path", @"D:\Data")]
    [InlineData("--log-path", @"L:\Logs")]
    public async Task RelocationWithoutATargetIsRefusedRatherThanDropped(params string[] flags)
    {
        var argv = new[] { "--server", "SRV01", "--database", "Sales" }.Concat(flags).ToArray();
        var (exit, script, said) = await RunScript(argv);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Empty(script);
        Assert.Contains("--target", said);
        Assert.Contains("FILELISTONLY", said);
    }

    /// <summary>Without any relocation flag, nothing is moved - and no server is asked.</summary>
    [Fact]
    public async Task NoRelocationFlagsMeansNoMoveAndNoTargetNeeded()
    {
        var (exit, script, _) = await RunScript("--server", "SRV01", "--database", "Sales");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains("RESTORE DATABASE", script);
        Assert.DoesNotContain("MOVE", script);
    }

    [Fact]
    public async Task AnUnknownTargetIsAUsageError()
    {
        var (exit, _, said) = await RunScript(
            "--server", "SRV01", "--database", "Sales", "--target", "SRV99", "--relocate");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("SRV99", said);
    }
}
