using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// --notify closes the monitoring loop (#301): the sweep's verdict through the configured
/// webhooks - ONE message per sweep, worst offenders first, warning level and above by
/// default. Task Scheduler plus this flag is estate backup monitoring with no agent.
/// </summary>
public class CliExposureNotifyTests
{
    private static (CliServices services, FakeSqlServerService sql, FakeRunNotifier notifier)
        Stage(params string[] servers)
    {
        var store = new FakeCredentialStore();
        foreach (var name in servers)
            store.Config.Servers.Add(new ServerConnection
            { Id = ServerConnection.NewId(), Name = name, ServerName = name });

        var sql = new FakeSqlServerService();
        var notifier = new FakeRunNotifier();
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), new FakeRestoreHistoryStore(), notifier);
        return (services, sql, notifier);
    }

    private static ExposureRow Row(string server, string db, DateTime? lastFull, DateTime? lastLog) => new()
    {
        ServerName = server,
        DatabaseName = db,
        RecoveryModel = "FULL",
        StateDescription = "ONLINE",
        LastFull = lastFull,
        LastLog = lastLog
    };

    private static async Task<(int exit, string errors)> Run(string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await ExposureVerb.RunAsync(
            CliArguments.Parse(args, ExposureVerb.Spec), services, output, errors);
        return (exit, errors.ToString());
    }

    [Fact]
    public async Task AHealthyEstateSaysNothingByDefault()
    {
        var now = DateTime.Now;
        var (services, sql, notifier) = Stage("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "MyDb", now.AddHours(-20), now.AddMinutes(-10))];

        var (exit, _) = await Run(["--notify"], services);

        Assert.Equal(0, exit);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task TroubleSendsOneMessageWorstFirstAndDrains()
    {
        var now = DateTime.Now;
        var (services, sql, notifier) = Stage("SRV01", "SRV02");
        sql.ExposureByServer["SRV01"] =
        [
            Row("SRV01", "Naked", null, null),                               // never backed up: alarm
            Row("SRV01", "Stale", now.AddDays(-2), now.AddHours(-3))         // log silence: warning
        ];
        sql.ExposureByServer["SRV02"] =
            [Row("SRV02", "Healthy", now.AddHours(-20), now.AddMinutes(-10))];

        var (exit, _) = await Run(["--notify"], services);

        Assert.Equal(2, exit);   // the exit code is unaffected by notifying

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(RunPhase.Problem, sent.Phase);
        Assert.Equal("Exposure", sent.Operation);
        Assert.Contains("1 alarm(s), 1 warning(s)", sent.Subject);
        Assert.Contains("2 server(s)", sent.Target);
        // Worst first: the alarm row leads the detail.
        Assert.StartsWith("SRV01/Naked", sent.Detail);
        Assert.DoesNotContain("Healthy", sent.Detail);
        Assert.True(notifier.DrainCalls > 0);
    }

    /// <summary>An unreachable server is an alarm - and therefore a message.</summary>
    [Fact]
    public async Task AnUnreachableServerReachesTheChannel()
    {
        var (services, _, notifier) = Stage("SRV01");   // not in ExposureByServer: fake throws

        var (exit, _) = await Run(["--notify"], services);

        Assert.Equal(2, exit);
        var sent = Assert.Single(notifier.Sent);
        Assert.Contains("UNREACHABLE", sent.Detail);
    }

    [Fact]
    public async Task NotifyAlwaysSendsTheAllClearHeartbeat()
    {
        var now = DateTime.Now;
        var (services, sql, notifier) = Stage("SRV01");
        sql.ExposureByServer["SRV01"] = [Row("SRV01", "MyDb", now.AddHours(-20), now.AddMinutes(-10))];

        var (exit, _) = await Run(["--notify-always"], services);

        Assert.Equal(0, exit);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(RunPhase.Succeeded, sent.Phase);
        Assert.Contains("inside their windows", sent.Subject);
    }

    [Fact]
    public async Task WithoutTheFlagNothingIsEverSent()
    {
        var (services, _, notifier) = Stage("SRV01");   // unreachable, worst case

        var (exit, _) = await Run([], services);

        Assert.Equal(2, exit);
        Assert.Empty(notifier.Sent);
    }
}
