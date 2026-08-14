using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The execution verbs end as data when asked (#303): --json puts outcome, duration, chain,
/// point, refusals and the history id on stdout, machine-shaped - the rehearsal's proof
/// becomes an artefact a pipeline can archive. And receipts carry their Origin, so the
/// History screen can tell a 3am CLI restore from a clicked one.
/// </summary>
public class CliJsonResultTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (CliServices services, FakeSqlServerService sql, FakeOperationHistoryStore history)
        Stage()
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
        var history = new FakeOperationHistoryStore();
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), history, new FakeRunNotifier());
        return (services, sql, history);
    }

    private static async Task<(int exit, string output, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, CancellationToken, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors, default);
        return (exit, output.ToString(), errors.ToString());
    }

    private static JsonElement Parse(string output) =>
        JsonDocument.Parse(output).RootElement;

    // ── the restore ends as data ────────────────────────────────────────────────

    [Fact]
    public async Task AGenerateOnlyRestoreEmitsTheVerdictAndTheScript()
    {
        var (services, _, _) = Stage();

        var (exit, output, _) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02", "--json"], services);

        Assert.Equal(0, exit);
        var result = Parse(output);
        Assert.Equal("restore", result.GetProperty("verb").GetString());
        Assert.Equal("Generated", result.GetProperty("outcome").GetString());
        Assert.Contains("RESTORE DATABASE", result.GetProperty("script").GetString());
        Assert.Equal(0, result.GetProperty("refusals").GetArrayLength());
    }

    [Fact]
    public async Task ARefusedRestoreCarriesItsRefusalsAsData()
    {
        var (services, sql, _) = Stage();
        sql.DatabaseList = ["MyDb"];   // exists without --with-replace: refused

        var (exit, output, _) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02", "--json"], services);

        Assert.Equal(2, exit);
        var result = Parse(output);
        Assert.Equal("Refused", result.GetProperty("outcome").GetString());
        Assert.Contains("--with-replace",
            result.GetProperty("refusals")[0].GetString());
    }

    [Fact]
    public async Task AnExecutedRestoreEmitsTheOutcomeDurationAndHistoryId()
    {
        var (services, _, history) = Stage();

        var (exit, output, _) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02",
             "--execute", "--json"], services);

        Assert.Equal(0, exit);
        var result = Parse(output);
        Assert.Equal("Succeeded", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("durationSeconds").GetDouble() >= 0);
        Assert.Equal(history.Entries.Single().Id, result.GetProperty("historyId").GetString());
    }

    // ── the rehearsal's proof is an artefact ────────────────────────────────────

    private static FileMoveOption ScratchableFile() => new()
    {
        LogicalName = "MyDb",
        PhysicalName = @"E:\Data\MyDb.mdf",
        NewPhysicalName = @"E:\Data\MyDb.mdf",
        Type = "D",
        SizeBytes = 1024
    };

    [Fact]
    public async Task AProvenRehearsalEmitsTheMeasuredRto()
    {
        var (services, sql, history) = Stage();
        sql.FileList = [ScratchableFile()];
        // rehearse now runs the space preflight (#359), and an unreported volume is a
        // refusal by design, so the target has to say what it has free.
        sql.VolumeFreeSpace = new() { [@"D:\"] = 500L * 1024 * 1024 * 1024 };

        var (exit, output, _) = await Run(RehearseVerb.RunAsync, RehearseVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02",
             "--execute", "--json"], services);

        Assert.Equal(0, exit);
        var result = Parse(output);
        Assert.Equal("rehearse", result.GetProperty("verb").GetString());
        Assert.Equal("Proven", result.GetProperty("outcome").GetString());
        Assert.True(result.TryGetProperty("durationSeconds", out _));
        Assert.Equal(history.Entries.Single().Id, result.GetProperty("historyId").GetString());
    }

    [Fact]
    public async Task AFailedRehearsalSaysNotProvenAndThatTheScratchRemains()
    {
        var (services, sql, _) = Stage();
        sql.FileList = [ScratchableFile()];
        // rehearse now runs the space preflight (#359), and an unreported volume is a
        // refusal by design, so the target has to say what it has free.
        sql.VolumeFreeSpace = new() { [@"D:\"] = 500L * 1024 * 1024 * 1024 };
        sql.FailOnExecuteNumber = 1;

        var (exit, output, _) = await Run(RehearseVerb.RunAsync, RehearseVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02",
             "--execute", "--json"], services);

        Assert.Equal(2, exit);
        var result = Parse(output);
        Assert.Equal("NotProven", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("scratchRetained").GetBoolean());
        Assert.NotNull(result.GetProperty("error").GetString());
    }

    // ── the backup too, and stdout stays pure ───────────────────────────────────

    [Fact]
    public async Task AnExecutedBackupEmitsOneParseableObjectOnStdout()
    {
        var (services, _, _) = Stage();

        var (exit, output, _) = await Run(BackupVerb.RunAsync, BackupVerb.Spec,
            ["--path", @"\\nas01\sql", "--server", "SRV01", "--database", "MyDb",
             "--execute", "--json"], services);

        Assert.Equal(0, exit);
        var result = Parse(output);   // parse of the WHOLE stdout: nothing else leaked in
        Assert.Equal("backup", result.GetProperty("verb").GetString());
        Assert.Equal("Succeeded", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("destinations").GetArrayLength() > 0);
    }

    // ── receipts say which front end acted (#303) ───────────────────────────────

    [Fact]
    public async Task CliReceiptsCarryTheirOrigin()
    {
        var (services, _, history) = Stage();

        await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02", "--execute"],
            services);

        Assert.Equal("CLI", history.Entries.Single().Origin);
    }

    [Fact]
    public void AppReceiptsReadAsAppAndTheRowSaysNothingExtra()
    {
        var entry = new OperationHistoryEntry { TargetDatabase = "MyDb", ServerName = "SRV01" };

        Assert.Equal("App", entry.Origin);
        Assert.DoesNotContain("via", entry.Detail);

        entry.Origin = "CLI";
        Assert.Contains("via CLI", entry.Detail);
    }
}
