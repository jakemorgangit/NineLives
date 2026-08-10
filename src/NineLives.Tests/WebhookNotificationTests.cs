using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Run notifications to Teams, Slack and generic endpoints (#242).
///
/// For everyone whose focus is rightly NOT on the application while a long run executes: a
/// message when it starts, when it finishes, and - most importantly - when there is a problem.
/// The payload shapes follow the PerformanceMonitor webhooks, already proven against real Teams
/// and Slack channels.
/// </summary>
public class WebhookNotificationTests
{
    private static readonly RunNotification Restore =
        new(RunPhase.Succeeded, "Restore", "MyDb", "SRV01", "1 full + 2 logs", TimeSpan.FromMinutes(40));

    // ── the payloads ────────────────────────────────────────────────────────────

    /// <summary>Teams gets a MessageCard - the shape both connectors and Power Automate accept.</summary>
    [Fact]
    public void TeamsGetsAMessageCard()
    {
        var json = WebhookNotifier.BuildTeamsPayload(Restore);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("MessageCard", doc.RootElement.GetProperty("@type").GetString());
        Assert.Equal("107C10", doc.RootElement.GetProperty("themeColor").GetString());
        Assert.Contains("COMPLETED", doc.RootElement.GetProperty("summary").GetString());

        var section = doc.RootElement.GetProperty("sections")[0];
        Assert.Contains("Restore MyDb", section.GetProperty("activityTitle").GetString());

        var facts = section.GetProperty("facts").EnumerateArray()
            .Select(f => (f.GetProperty("name").GetString(), f.GetProperty("value").GetString()))
            .ToList();
        Assert.Contains(facts, f => f.Item1 == "Server" && f.Item2 == "SRV01");
        Assert.Contains(facts, f => f.Item1 == "Duration" && f.Item2 == "40m 00s");
    }

    [Fact]
    public void SlackGetsBlocks()
    {
        var json = WebhookNotifier.BuildSlackPayload(
            Restore with { Phase = RunPhase.Problem, Detail = "Msg 3201: Cannot open backup device" });
        using var doc = JsonDocument.Parse(json);

        var blocks = doc.RootElement.GetProperty("blocks");
        Assert.Equal("header", blocks[0].GetProperty("type").GetString());
        Assert.Contains("PROBLEM", blocks[0].GetProperty("text").GetProperty("text").GetString());

        // The failure line rides in its own section - the sofa reader needs the WHY.
        var all = json;
        Assert.Contains("Msg 3201", all);
    }

    /// <summary>The generic payload is fields, not display text - nothing needs parsing back out.</summary>
    [Fact]
    public void GenericGetsPlainFields()
    {
        var json = WebhookNotifier.BuildGenericPayload(Restore);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("NineLives", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal("Succeeded", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal("MyDb", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal(2400, doc.RootElement.GetProperty("durationSeconds").GetDouble(), 1);
    }

    [Theory]
    [InlineData(RunPhase.Started, "0078D7")]
    [InlineData(RunPhase.Succeeded, "107C10")]
    [InlineData(RunPhase.Problem, "D13438")]
    public void EachPhaseHasItsColour(RunPhase phase, string colour)
    {
        Assert.Equal(colour, WebhookNotifier.Look(phase).ThemeColor);
    }

    // ── routing ─────────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Posts { get; } = [];
        public HttpStatusCode Respond { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Posts.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Respond) { Content = new StringContent("") };
        }
    }

    private static WebhookEndpoint Endpoint(
        string name = "chan", WebhookFormat format = WebhookFormat.Teams,
        bool started = true, bool finished = true, bool problems = true) => new()
    {
        Name = name,
        Url = "https://example.test/hook",
        Format = format,
        NotifyStarted = started,
        NotifyFinished = finished,
        NotifyProblems = problems
    };

    /// <summary>An endpoint hears only about the phases it asked for.</summary>
    [Fact]
    public async Task TogglesDecideWhatEachEndpointHears()
    {
        var handler = new RecordingHandler();
        var notifier = new WebhookNotifier(handler);
        var quietStart = Endpoint(started: false);

        await notifier.NotifyAsync([quietStart], Restore with { Phase = RunPhase.Started });
        Assert.Empty(handler.Posts);

        await notifier.NotifyAsync([quietStart], Restore);
        Assert.Single(handler.Posts);
    }

    [Fact]
    public async Task EveryConfiguredEndpointIsPosted()
    {
        var handler = new RecordingHandler();
        var notifier = new WebhookNotifier(handler);

        await notifier.NotifyAsync(
            [Endpoint("teams"), Endpoint("slack", WebhookFormat.Slack)], Restore);

        Assert.Equal(2, handler.Posts.Count);
        Assert.Contains("MessageCard", handler.Posts[0].Body);
        Assert.Contains("blocks", handler.Posts[1].Body);
    }

    /// <summary>A dead endpoint is a log line, never an exception - the run must not notice.</summary>
    [Fact]
    public async Task AFailingEndpointOnlyLogs()
    {
        var handler = new RecordingHandler { Respond = HttpStatusCode.NotFound };
        var notifier = new WebhookNotifier(handler);
        var log = new List<string>();

        await notifier.NotifyAsync([Endpoint()], Restore, log.Add);

        Assert.Contains(log, l => l.Contains("FAILED") && l.Contains("404"));
    }

    [Fact]
    public async Task TheTestSendReportsSuccessAndFailureHonestly()
    {
        var ok = new WebhookNotifier(new RecordingHandler());
        Assert.Null(await ok.SendTestAsync(Endpoint()));

        var dead = new WebhookNotifier(new RecordingHandler { Respond = HttpStatusCode.Unauthorized });
        Assert.Contains("401", await dead.SendTestAsync(Endpoint()));

        Assert.Contains("empty", (await ok.SendTestAsync(new WebhookEndpoint()))!);
    }

    // ── the screens fire the right events ───────────────────────────────────────

    private static ServerConnection Server(string name = "SRV01") =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static RestoreRun Run() => new(
        Server: Server(),
        Script: "RESTORE DATABASE [MyDb] FROM DISK = N'D:\\b.bak' WITH REPLACE, RECOVERY;",
        TargetDatabase: "MyDb",
        SourceDatabase: null,
        ContainerName: null,
        ChainSummary: "1 full",
        RestorePointTimestamp: null,
        OptionsForLog: "WITH REPLACE, RECOVERY");

    [Fact]
    public async Task ARestoreAnnouncesStartAndSuccess()
    {
        var notifier = new FakeRunNotifier();
        var vm = new RestoreExecutionViewModel(
            new FakeSqlServerService(), new FakeRestoreHistoryStore(), TestLogs.Temp(),
            new OperationCancellation(), notifier);

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Equal([RunPhase.Started, RunPhase.Succeeded], notifier.Sent.Select(n => n.Phase));
        Assert.All(notifier.Sent, n => Assert.Equal("MyDb", n.Subject));
        Assert.NotNull(notifier.Sent[1].Duration);
    }

    [Fact]
    public async Task AFailedRestoreAnnouncesTheProblemWithTheReason()
    {
        var notifier = new FakeRunNotifier();
        var sql = new FakeSqlServerService { ExecuteThrows = new InvalidOperationException("Msg 3201") };
        var vm = new RestoreExecutionViewModel(
            sql, new FakeRestoreHistoryStore(), TestLogs.Temp(), new OperationCancellation(), notifier);

        await vm.RunAsync(Run(), _ => Task.FromResult(CredentialPreflight.Proceed));

        Assert.Equal([RunPhase.Started, RunPhase.Problem], notifier.Sent.Select(n => n.Phase));
        Assert.Contains("Msg 3201", notifier.Sent[1].Detail);
    }

    /// <summary>
    /// A multi-database backup announces each problem AT the problem - the run continues, and
    /// whoever is elsewhere decides now whether it changes their evening - then closes the run.
    /// </summary>
    [Fact]
    public async Task AMultiDatabaseBackupAnnouncesProblemsAsTheyHappen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["Archive", "Payroll", "Sales"] };
        var notifier = new FakeRunNotifier();
        var vm = new BackupViewModel(store, sql, TestLogs.Temp(), notifier);
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        vm.GenerateCommand.Execute(null);
        sql.FailOnExecuteNumber = 2;

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(
            [RunPhase.Started, RunPhase.Problem, RunPhase.Problem],
            notifier.Sent.Select(n => n.Phase));
        Assert.Equal("Payroll", notifier.Sent[1].Subject);         // the problem, as it happened
        Assert.Contains("1 of 3", notifier.Sent[2].Detail);        // the close of the run
    }

    // ── the export boundary ─────────────────────────────────────────────────────

    /// <summary>A webhook URL is a bearer secret; the entry travels, the secret stays (#213).</summary>
    [Fact]
    public void WebhookUrlsNeverLeaveInAnExport()
    {
        var config = new AppConfig();
        config.Webhooks.Add(new WebhookEndpoint
        {
            Name = "DBA Teams",
            Url = "https://prod.powerautomate.example/workflows/abc/triggers/manual/paths/invoke?sig=SECRETSIG"
        });

        var json = ConfigPortability.Export(config);

        Assert.DoesNotContain("SECRETSIG", json);
        Assert.Contains("DBA Teams", json);

        // And an import over a machine that HAS the URL keeps it.
        var local = new AppConfig();
        local.Webhooks.Add(new WebhookEndpoint { Id = config.Webhooks[0].Id, Name = "old", Url = "https://kept" });
        ConfigPortability.Merge(local, ConfigPortability.Read(json)!);

        Assert.Equal("https://kept", local.Webhooks.Single().Url);
        Assert.Equal("DBA Teams", local.Webhooks.Single().Name);
    }
}
