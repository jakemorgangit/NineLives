using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The S3 capability gate (#51): the S3 connector needs SQL Server 2022+ and a non-Express
/// edition. That is a capability, not evidence - the engine would simply error after WITH
/// REPLACE had dropped the target - so --force cannot override it. No verdict from silence.
/// </summary>
public class S3PreflightGateTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (CliServices services, FakeSqlServerService sql) Stage(
        string deviceUrl = "s3://s3.eu-west-2.amazonaws.com/backups/Sales_FULL.bak")
    {
        var store = new FakeCredentialStore();
        // SRV01 is the source instance whose msdb history is read; SRV02 runs the restore.
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
                    // The recorded device path is s3:// - not on-disk, so it reaches the target
                    // as a blob device, which is exactly what the gate keys off.
                    Files = [deviceUrl]
                }
            ]
        };
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), new FakeOperationHistoryStore(),
            new FakeRunNotifier());
        return (services, sql);
    }

    private static async Task<(int exit, string errors)> Run(string[] extra, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await RestoreVerb.RunAsync(
            CliArguments.Parse(
                [.. new[] { "--server", "SRV01", "--database", "Sales", "--target", "SRV02" }, .. extra],
                RestoreVerb.Spec),
            services, output, errors, default);
        return (exit, errors.ToString());
    }

    [Fact]
    public async Task Sql2019RefusesAnS3Restore()
    {
        var (services, sql) = Stage();
        sql.ProductMajorVersion = 15;   // SQL Server 2019

        var (exit, errors) = await Run(["--execute"], services);

        Assert.Equal(2, exit);
        Assert.Contains("S3 connector arrived in SQL Server 2022", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    [Fact]
    public async Task ExpressEditionRefusesAnS3RestoreEvenOn2022()
    {
        var (services, sql) = Stage();
        sql.ProductMajorVersion = 16;   // SQL Server 2022...
        sql.EngineEdition = 4;          // ...but Express

        var (exit, errors) = await Run(["--execute"], services);

        Assert.Equal(2, exit);
        Assert.Contains("Express edition", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>A capability the engine lacks cannot be forced into existence.</summary>
    [Fact]
    public async Task ForceDoesNotOverrideTheCapabilityGate()
    {
        var (services, sql) = Stage();
        sql.ProductMajorVersion = 15;

        var (exit, errors) = await Run(["--execute", "--force", "--with-replace"], services);

        Assert.Equal(2, exit);
        Assert.Contains("S3 connector arrived", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    [Fact]
    public async Task A2022StandardInstanceRestoresFromS3()
    {
        var (services, sql) = Stage();
        sql.ProductMajorVersion = 16;
        sql.EngineEdition = 3;   // non-Express

        var (exit, _) = await Run(["--execute"], services);

        Assert.Equal(0, exit);
        Assert.Contains(sql.ExecutedScripts, s => s.Contains("RESTORE DATABASE [Sales]"));
    }

    /// <summary>The gate is S3-only: an Azure restore is untouched by it.</summary>
    [Fact]
    public async Task AnAzureRestoreIsNotGatedOnVersion()
    {
        var (services, sql) = Stage("https://acct.blob.core.windows.net/backups/Sales_FULL.bak");
        sql.ProductMajorVersion = 13;   // SQL Server 2016 - fine for Azure

        var (exit, _) = await Run(["--execute"], services);

        Assert.Equal(0, exit);
        Assert.Contains(sql.ExecutedScripts, s => s.Contains("RESTORE DATABASE [Sales]"));
    }
}
