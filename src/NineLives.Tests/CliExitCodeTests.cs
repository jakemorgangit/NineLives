using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The exit code is the contract a pipeline branches on (#370).
///
/// Two ways it lied. An output format changed the verdict: `list --json` returned 0 on a source
/// holding nothing, while the same command without --json returned 2 and the spec documented 2 -
/// so adding --json to a monitoring check turned a silent backup failure into a pass, and the
/// pipeline piped an empty array into jq and did nothing.
///
/// And a finding wore a usage error's clothes: "this source has no backups for a database called
/// Sales" came back as 64, indistinguishable from a malformed command line. A pipeline reading
/// the documented contract treats 64 as "I invoked it wrongly" - log and abort, never page -
/// when the correct reading is the alarm the verb exists to raise.
/// </summary>
public class CliExitCodeTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static CliServices Stage(params BackupHistoryEntry[] history)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        var sql = new FakeSqlServerService { BackupHistory = history.ToList() };
        return new CliServices(
            store, sql, new FakeBlobStorageService(),
            new FakeRestoreHistoryStore(), new FakeRunNotifier());
    }

    private static BackupHistoryEntry Full(string database) => new()
    {
        DatabaseName = database,
        Type = BackupType.Full,
        StartedAt = T0,
        FinishedAt = T0.AddMinutes(5),
        CheckpointLsn = 100,
        Position = 1,
        Files = [@"X:\backups\full.bak"]
    };

    private static async Task<(int exit, string output)> RunList(
        CliServices services, params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await ListVerb.RunAsync(
            CliArguments.Parse(args, ListVerb.Spec), services, output, errors);
        return (exit, output.ToString());
    }

    // ── an output format does not change the answer ─────────────────────────────

    [Fact]
    public async Task AnEmptySourceFailsWhicheverWayItIsPrinted()
    {
        var (human, _) = await RunList(Stage(), "--server", "SRV01");
        var (json, jsonOut) = await RunList(Stage(), "--server", "SRV01", "--json");

        Assert.Equal(ExitCodes.Failed, human);
        Assert.Equal(ExitCodes.Failed, json);

        // And it still emits parseable JSON - the caller asked for JSON, so it gets JSON.
        Assert.Equal("[]", jsonOut.Trim());
    }

    [Fact]
    public async Task ASourceWithBackupsSucceedsWhicheverWayItIsPrinted()
    {
        var (human, _) = await RunList(Stage(Full("Sales")), "--server", "SRV01");
        var (json, _) = await RunList(Stage(Full("Sales")), "--server", "SRV01", "--json");

        Assert.Equal(ExitCodes.Ok, human);
        Assert.Equal(ExitCodes.Ok, json);
    }

    // ── a finding is not a usage error ──────────────────────────────────────────

    /// <summary>
    /// The alarm case: the source is fine and the database is not in it. That is exactly what
    /// validate is watched for, and it used to exit 64.
    /// </summary>
    [Fact]
    public async Task ADatabaseWithNoBackupsIsAFindingNotAUsageError()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var exit = await ValidateVerb.RunAsync(
            CliArguments.Parse(
                ["--server", "SRV01", "--database", "Sales"], ValidateVerb.Spec),
            Stage(Full("Payroll")), output, errors);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.NotEqual(ExitCodes.Usage, exit);
        Assert.Contains("no backups for a database called 'Sales'", errors.ToString());
    }

    /// <summary>
    /// And a genuinely malformed invocation still exits 64, or the distinction is worthless.
    /// </summary>
    [Fact]
    public async Task AMalformedInvocationIsStillAUsageError()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        // Neither source given.
        var exit = await ValidateVerb.RunAsync(
            CliArguments.Parse(["--database", "Sales"], ValidateVerb.Spec),
            Stage(Full("Sales")), output, errors);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("exactly one source", errors.ToString());
    }

    [Fact]
    public async Task AnUnknownContainerNameIsStillAUsageError()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var exit = await ValidateVerb.RunAsync(
            CliArguments.Parse(
                ["--container", "no-such-container", "--database", "Sales"], ValidateVerb.Spec),
            Stage(Full("Sales")), output, errors);

        Assert.Equal(ExitCodes.Usage, exit);
    }
}
