using System.IO;
using System.Threading;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The CLI's endings (#296): a short-lived process must not exit before its story is told.
/// The completion webhooks used to race process exit and mostly lose - fire-and-forget is
/// right in the app, fatal in a process that returns from Main immediately after Notify -
/// and Ctrl+C killed the process with no Cancelled receipt, no notification, no defined
/// exit code.
/// </summary>
public class CliEndingsTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (CliServices services, FakeSqlServerService sql,
        FakeRestoreHistoryStore history, FakeRunNotifier notifier) Stage()
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
                    CheckpointLsn = 100m,
                    Position = 1,
                    Files = [@"X:\backups\full.bak"]
                }
            ]
        };
        var history = new FakeRestoreHistoryStore();
        var notifier = new FakeRunNotifier();
        return (new CliServices(store, sql, new FakeBlobStorageService(), history, notifier),
            sql, history, notifier);
    }

    private static async Task<(int exit, string errors)> RunRestore(
        CliServices services, CancellationToken ct, params string[] extra)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        string[] args = [.. new[]
        { "--server", "SRV01", "--database", "MyDb", "--target", "SRV02" }, .. extra];
        var exit = await RestoreVerb.RunAsync(
            CliArguments.Parse(args, RestoreVerb.Spec), services, output, errors, ct);
        return (exit, errors.ToString());
    }

    // ── Ctrl+C ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelledRestoreLeavesTheCancelledReceiptAndExits130()
    {
        var (services, sql, history, notifier) = Stage();

        var (exit, errors) = await RunRestore(
            services, new CancellationToken(canceled: true), "--execute");

        Assert.Equal(130, exit);
        Assert.Contains("CANCELLED", errors);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(RestoreOutcome.Cancelled, entry.Outcome);

        Assert.Equal(RunPhase.Problem, notifier.Sent.Last().Phase);
        Assert.True(notifier.DrainCalls >= 1);
    }

    // ── the drain before exit ───────────────────────────────────────────────────

    [Fact]
    public async Task EveryExecutionEndingDrainsTheNotifier()
    {
        var (ok, _, _, okNotifier) = Stage();
        await RunRestore(ok, CancellationToken.None, "--execute");
        Assert.Equal(1, okNotifier.DrainCalls);

        var (bad, badSql, _, badNotifier) = Stage();
        badSql.FailOnExecuteNumber = 1;
        await RunRestore(bad, CancellationToken.None, "--execute");
        Assert.Equal(1, badNotifier.DrainCalls);
    }

    /// <summary>
    /// The real notifier's drain genuinely waits: a delivery that takes a moment has completed
    /// by the time DrainAsync returns. Without the drain this test's process-exit stand-in -
    /// asserting immediately after Notify - observes the loss directly.
    /// </summary>
    [Fact]
    public async Task TheRealNotifierDrainWaitsForInFlightDeliveries()
    {
        var store = new FakeCredentialStore();
        store.Config.Webhooks.Add(new WebhookEndpoint
        { Id = "w1", Name = "Ops", Url = "https://example.invalid/hook" });

        var slow = new SlowWebhookNotifier();
        var notifier = new WebhookRunNotifier(store, TestLogs.Temp(), slow);

        notifier.Notify(new RunNotification(
            RunPhase.Succeeded, "Restore", "MyDb", "SRV02", null, TimeSpan.FromMinutes(1)));

        Assert.False(slow.Delivered);
        await notifier.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.True(slow.Delivered);
    }

    /// <summary>And a dead endpoint cannot hold the process: the timeout wins.</summary>
    [Fact]
    public async Task TheDrainTimeoutBeatsAHungDelivery()
    {
        var store = new FakeCredentialStore();
        store.Config.Webhooks.Add(new WebhookEndpoint
        { Id = "w1", Name = "Ops", Url = "https://example.invalid/hook" });

        var hung = new HungWebhookNotifier();
        var notifier = new WebhookRunNotifier(store, TestLogs.Temp(), hung);
        notifier.Notify(new RunNotification(RunPhase.Started, "Restore", "MyDb", "SRV02"));

        var drain = notifier.DrainAsync(TimeSpan.FromMilliseconds(200));
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(drain, finished);
    }

    private sealed class SlowWebhookNotifier : WebhookNotifier
    {
        public volatile bool Delivered;

        public override async Task NotifyAsync(
            IReadOnlyList<WebhookEndpoint> endpoints, RunNotification notification,
            Action<string>? log = null, Action<WebhookEndpoint, string?>? outcome = null)
        {
            await Task.Delay(250);
            Delivered = true;
        }
    }

    private sealed class HungWebhookNotifier : WebhookNotifier
    {
        public override Task NotifyAsync(
            IReadOnlyList<WebhookEndpoint> endpoints, RunNotification notification,
            Action<string>? log = null, Action<WebhookEndpoint, string?>? outcome = null)
            => Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
