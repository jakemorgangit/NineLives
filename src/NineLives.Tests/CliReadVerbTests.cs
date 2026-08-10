using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The CLI's read verbs (#63 step 2), run against the same fakes the GUI's tests use -
/// which is itself the claim under test: one engine, two front ends. Output goes through
/// injected writers, so what a pipeline would see on stdout and stderr is exactly what these
/// assert on, exit codes included.
/// </summary>
public class CliReadVerbTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    // ── the shared stage ────────────────────────────────────────────────────────

    private static (CliServices services, FakeSqlServerService sql) Stage(
        params BackupHistoryEntry[] history)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        var sql = new FakeSqlServerService { BackupHistory = history.ToList() };
        return (new CliServices(store, sql, new FakeBlobStorageService()), sql);
    }

    private static BackupHistoryEntry Full(decimal checkpoint, DateTime at) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.Full,
        StartedAt = at,
        FinishedAt = at.AddMinutes(5),
        CheckpointLsn = checkpoint,
        Position = 1,
        Files = [@"X:\backups\full.bak"]
    };

    private static BackupHistoryEntry Log(
        decimal first, decimal last, decimal baseLsn, DateTime at) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        StartedAt = at,
        FinishedAt = at.AddMinutes(1),
        FirstLsn = first,
        LastLsn = last,
        DatabaseBackupLsn = baseLsn,
        Position = 1,
        Files = [$@"X:\backups\log_{at:HHmm}.trn"]
    };

    private static async Task<(int exit, string output, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors);
        return (exit, output.ToString(), errors.ToString());
    }

    // ── list ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNamesEachDatabaseWithCountsAndFreshness()
    {
        var (services, _) = Stage(
            Full(100, T0),
            Log(100, 200, 100, T0.AddDays(1)));

        var (exit, output, _) = await Run(
            ListVerb.RunAsync, ListVerb.Spec, ["--server", "SRV01"], services);

        Assert.Equal(0, exit);
        Assert.Contains("MyDb", output);
        Assert.Contains("2026-08-02 22:00:00", output);
    }

    [Fact]
    public async Task AMissingSourceIsAUsageErrorNamingTheChoices()
    {
        var (services, _) = Stage();

        var (exit, _, errors) = await Run(
            ListVerb.RunAsync, ListVerb.Spec, [], services);

        Assert.Equal(64, exit);
        Assert.Contains("--container", errors);
        Assert.Contains("--server", errors);
    }

    [Fact]
    public async Task AnUnknownServerNamesWhatIsConfigured()
    {
        var (services, _) = Stage();

        var (exit, _, errors) = await Run(
            ListVerb.RunAsync, ListVerb.Spec, ["--server", "NOPE"], services);

        Assert.Equal(64, exit);
        Assert.Contains("SRV01", errors);
    }

    // ── points ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PointsListsEveryReachableMoment()
    {
        var (services, _) = Stage(
            Full(100, T0),
            Log(100, 200, 100, T0.AddDays(1)),
            Log(200, 300, 100, T0.AddDays(2)));

        var (exit, output, _) = await Run(
            PointsVerb.RunAsync, PointsVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb"], services);

        Assert.Equal(0, exit);
        Assert.Contains("2026-08-01 22:00:00", output);
        Assert.Contains("2026-08-02 22:00:00", output);
        Assert.Contains("2026-08-03 22:00:00", output);
    }

    /// <summary>An empty answer exits 2: "nothing is restorable" must never read as fine.</summary>
    [Fact]
    public async Task NoPointsIsAFailureExitCode()
    {
        // A log with no full anywhere: nothing to anchor a chain.
        var (services, _) = Stage(Log(100, 200, 100, T0));

        var (exit, _, errors) = await Run(
            PointsVerb.RunAsync, PointsVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb"], services);

        Assert.Equal(2, exit);
        Assert.Contains("validate", errors);
    }

    // ── script ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScriptEmitsTheChainWithStopatWhenTheMomentIsInsideALog()
    {
        var (services, _) = Stage(
            Full(100, T0),
            Log(100, 200, 100, T0.AddDays(1)),
            Log(200, 300, 100, T0.AddDays(2)));

        var (exit, output, errors) = await Run(
            ScriptVerb.RunAsync, ScriptVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--at", "2026-08-03 10:00"], services);

        Assert.Equal(0, exit);
        Assert.Contains("RESTORE DATABASE", output);
        Assert.Contains("RESTORE LOG", output);
        Assert.Contains("STOPAT", output);
        // Human narration stays off stdout, where the script is.
        Assert.Contains("Generated only", errors);
        Assert.DoesNotContain("Generated only", output);
    }

    [Fact]
    public async Task AMomentNoChainCoversNamesTheNearestPoints()
    {
        // Full then a second full, no logs: between them is a hole nothing reaches.
        var (services, _) = Stage(
            Full(100, T0),
            Full(400, T0.AddDays(2)));

        var (exit, _, errors) = await Run(
            ScriptVerb.RunAsync, ScriptVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--at", "2026-08-02 12:00"], services);

        Assert.Equal(2, exit);
        Assert.Contains("no log backup spans it", errors);
        Assert.Contains("2026-08-01 22:00:00", errors);
        Assert.Contains("2026-08-03 22:00:00", errors);
    }

    [Fact]
    public async Task AMarkAndATimeTogetherAreRefused()
    {
        var (services, _) = Stage(Full(100, T0));

        var (exit, _, errors) = await Run(
            ScriptVerb.RunAsync, ScriptVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb",
             "--at", "2026-08-01 22:00", "--stop-before-mark", "deploy_v2"], services);

        Assert.Equal(64, exit);
        Assert.Contains("not both", errors);
    }

    // ── validate ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIntactChainValidatesCleanWithExitZero()
    {
        var (services, _) = Stage(
            Full(100, T0),
            Log(100, 200, 100, T0.AddDays(1)));

        var (exit, output, _) = await Run(
            ValidateVerb.RunAsync, ValidateVerb.Spec, ["--server", "SRV01"], services);

        Assert.Equal(0, exit);
        Assert.Contains("intact", output);
    }

    /// <summary>
    /// The stranded-set case the GUI shows by omission: a differential whose base full is not
    /// in hand forms no point, and the verb must say so out loud with the error it prevents.
    /// </summary>
    [Fact]
    public async Task ADifferentialWithNoBaseIsNamedAsBroken()
    {
        var orphanDiff = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            Type = BackupType.Differential,
            StartedAt = T0.AddDays(1),
            FinishedAt = T0.AddDays(1).AddMinutes(2),
            DatabaseBackupLsn = 999,
            Position = 1,
            Files = [@"X:\backups\diff.bak"]
        };

        var (services, _) = Stage(Full(100, T0), orphanDiff);

        var (exit, output, errors) = await Run(
            ValidateVerb.RunAsync, ValidateVerb.Spec, ["--server", "SRV01"], services);

        Assert.Equal(2, exit);
        Assert.Contains("BROKEN", output);
        Assert.Contains("3136", errors);
    }

    // ── exposure ────────────────────────────────────────────────────────────────

    private static ExposureRow Row(string db, DateTime? lastFull, DateTime? lastLog) => new()
    {
        ServerName = "SRV01",
        DatabaseName = db,
        RecoveryModel = "FULL",
        LastFull = lastFull,
        LastLog = lastLog
    };

    [Fact]
    public async Task AHealthyEstateExitsZero()
    {
        var now = DateTime.Now;
        var (services, sql) = Stage();
        sql.ExposureByServer["SRV01"] =
            [Row("MyDb", now.AddHours(-20), now.AddMinutes(-10))];

        var (exit, output, _) = await Run(
            ExposureVerb.RunAsync, ExposureVerb.Spec, [], services);

        Assert.Equal(0, exit);
        Assert.Contains("MyDb", output);
    }

    [Fact]
    public async Task LogSilenceTurnsTheExitCodeAmber()
    {
        var now = DateTime.Now;
        var (services, sql) = Stage();
        sql.ExposureByServer["SRV01"] =
            [Row("MyDb", now.AddHours(-20), now.AddHours(-2))];

        var (exit, _, _) = await Run(
            ExposureVerb.RunAsync, ExposureVerb.Spec, [], services);

        Assert.Equal(1, exit);
    }

    /// <summary>
    /// The monitoring contract: a server that will not answer is an ALARM carried in the exit
    /// code, not a dead verb and not a silent omission - "I don't know" must never exit 0.
    /// </summary>
    [Fact]
    public async Task AnUnreachableServerIsAnAlarmRowNotADeadVerb()
    {
        var now = DateTime.Now;
        var (services, sql) = Stage();
        sql.ExposureByServer["SRV01"] =
            [Row("MyDb", now.AddHours(-20), now.AddMinutes(-10))];

        services.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRVGONE", ServerName = "SRVGONE" });

        var (exit, output, _) = await Run(
            ExposureVerb.RunAsync, ExposureVerb.Spec, [], services);

        Assert.Equal(2, exit);
        Assert.Contains("UNREACHABLE", output);
        // The reachable server's rows still made it out - the sweep survives its casualties.
        Assert.Contains("MyDb", output);
    }
}
