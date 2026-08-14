using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Relocation: the Terraform case (#299). A freshly provisioned VM rarely has the source
/// server's drive layout, and a restore aimed at the recorded paths fails with
/// directory-not-found mid-run - after WITH REPLACE has dropped what it was replacing. The
/// rehearse verb has always relocated; the restore verb, the one the template actually ends
/// with, now can - and with relocation in play the space preflight judges the volumes the
/// files actually LAND on.
/// </summary>
public class CliRelocationTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (CliServices services, FakeSqlServerService sql) Stage()
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
                    DatabaseName = "MyDb",
                    Type = BackupType.Full,
                    StartedAt = T0,
                    FinishedAt = T0.AddMinutes(5),
                    CheckpointLsn = 100,
                    Position = 1,
                    Files = [@"X:\backups\full.bak"]
                }
            ]
        };
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), new FakeOperationHistoryStore(),
            new FakeRunNotifier());
        return (services, sql);
    }

    private static FileMoveOption LogicalFile(string logical, string path, string type, long size = 1024) => new()
    {
        LogicalName = logical,
        PhysicalName = path,
        NewPhysicalName = path,
        Type = type,
        SizeBytes = size
    };

    private static async Task<(int exit, string output, string errors)> Run(
        string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await RestoreVerb.RunAsync(
            CliArguments.Parse(args, RestoreVerb.Spec), services, output, errors);
        return (exit, output.ToString(), errors.ToString());
    }

    private static string[] Args(params string[] extra) =>
        [.. new[] { "--server", "SRV01", "--database", "MyDb", "--target", "SRV02" }, .. extra];

    [Fact]
    public async Task RelocateMovesEveryFileToTheTargetsDefaultsKeepingItsName()
    {
        var (services, sql) = Stage();
        sql.FileList =
        [
            LogicalFile("MyDb", @"E:\SourceData\MyDb.mdf", "D"),
            LogicalFile("MyDb_log", @"F:\SourceLogs\MyDb_log.ldf", "L")
        ];
        sql.VolumeFreeSpace = new() { [@"D:\"] = 500L * 1024 * 1024 * 1024 };

        var (exit, output, errors) = await Run(Args("--relocate"), services);

        // The fake target's defaults are D:\Data and D:\Logs; the names travel unchanged.
        Assert.Equal(0, exit);
        Assert.Contains(@"MOVE N'MyDb' TO N'D:\Data\MyDb.mdf'", output);
        Assert.Contains(@"MOVE N'MyDb_log' TO N'D:\Logs\MyDb_log.ldf'", output);
        Assert.Contains("Relocating 2 file(s)", errors);
    }

    [Fact]
    public async Task ExplicitDataAndLogPathsPlaceTheFilesExactly()
    {
        var (services, sql) = Stage();
        sql.FileList =
        [
            LogicalFile("MyDb", @"E:\SourceData\MyDb.mdf", "D"),
            LogicalFile("MyDb_log", @"F:\SourceLogs\MyDb_log.ldf", "L")
        ];
        sql.VolumeFreeSpace = new()
        {
            [@"X:\"] = 500L * 1024 * 1024 * 1024,
            [@"Y:\"] = 500L * 1024 * 1024 * 1024
        };

        var (exit, output, _) = await Run(
            Args("--data-path", @"X:\NewData", "--log-path", @"Y:\NewLogs"), services);

        Assert.Equal(0, exit);
        Assert.Contains(@"MOVE N'MyDb' TO N'X:\NewData\MyDb.mdf'", output);
        Assert.Contains(@"MOVE N'MyDb_log' TO N'Y:\NewLogs\MyDb_log.ldf'", output);
    }

    /// <summary>
    /// With relocation in play the space check judges the volumes the files LAND on - the
    /// destination being too small matters; the recorded source volume no longer does.
    /// </summary>
    [Fact]
    public async Task TheSpaceCheckFollowsTheRelocation()
    {
        var (services, sql) = Stage();
        sql.FileList = [LogicalFile("MyDb", @"E:\SourceData\MyDb.mdf", "D", 100L * 1024 * 1024 * 1024)];
        sql.VolumeFreeSpace = new() { [@"D:\"] = 10L * 1024 * 1024 * 1024 };

        var (exit, _, errors) = await Run(Args("--relocate", "--execute"), services);

        // The file would land on D:\ (the fake default), which cannot fit it.
        Assert.Equal(2, exit);
        Assert.Contains(@"D:\ needs", errors);
        Assert.Contains("short by", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>Two source files sharing a name land side by side, not on one another.</summary>
    [Fact]
    public void RelocationDisambiguatesCollidingFileNames()
    {
        var moves = RestoreRelocation.ToDirectories(
        [
            LogicalFile("Rows1", @"E:\a\MyDb.ndf", "D"),
            LogicalFile("Rows2", @"F:\b\MyDb.ndf", "D")
        ], @"D:\Data", @"D:\Logs");

        Assert.Equal(@"D:\Data\MyDb.ndf", moves[0].NewPhysicalName);
        Assert.Equal(@"D:\Data\MyDb_2.ndf", moves[1].NewPhysicalName);
    }
}
