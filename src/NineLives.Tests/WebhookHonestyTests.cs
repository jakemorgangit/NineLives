using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The webhook settings tell the truth (#292): a test against an endpoint subscribed to
/// nothing says so instead of promising a channel that will stay silent forever; a URL typo
/// is refused at save rather than accepted and failed on every run; and every delivery
/// attempt stamps the endpoint, so silent breakage is visible on the row.
/// </summary>
public class WebhookHonestyTests
{
    private static WebhookEndpoint Endpoint(bool started = true, bool finished = true, bool problems = true) => new()
    {
        Name = "DBA channel",
        NotifyStarted = started,
        NotifyFinished = finished,
        NotifyProblems = problems
    };

    // ── the test says when nothing is subscribed ────────────────────────────────

    [Fact]
    public void ATestAgainstASubscribedEndpointPromisesTheChannel()
    {
        var text = SettingsViewModel.DescribeTestOutcome("DBA channel", null, Endpoint());

        Assert.Contains("check the channel", text);
    }

    /// <summary>
    /// Unticking all three moments is the natural way to pause an endpoint - there is no
    /// Enabled flag - and the plain "test sent" then promised a channel that would stay
    /// silent forever.
    /// </summary>
    [Fact]
    public void ATestAgainstAMutedEndpointSaysItWillNeverFire()
    {
        var text = SettingsViewModel.DescribeTestOutcome(
            "DBA channel", null, Endpoint(started: false, finished: false, problems: false));

        Assert.Contains("subscribed to nothing", text);
        Assert.Contains("never fire", text);
    }

    [Fact]
    public void AFailedTestReportsTheErrorNotTheSubscription()
    {
        var text = SettingsViewModel.DescribeTestOutcome(
            "DBA channel", "404 NotFound", Endpoint(started: false, finished: false, problems: false));

        Assert.Contains("404", text);
        Assert.DoesNotContain("subscribed", text);
    }

    // ── the URL is validated at save (#292) ─────────────────────────────────────

    private static WebhookEndpointViewModel Row(FakeCredentialStore store, WebhookEndpoint? model = null) =>
        new(model ?? Endpoint(), store, () => { }, _ => Task.CompletedTask);

    [Fact]
    public void ATypoIsRefusedAtSaveWithTheReasonOnTheRow()
    {
        var store = new FakeCredentialStore();
        var row = Row(store);

        row.UrlInput = "htps://hooks.example/x";
        row.SaveUrlCommand.Execute(null);

        Assert.Contains("https://", row.UrlFeedback);
        Assert.False(row.HasStoredUrl);
        Assert.Equal("htps://hooks.example/x", row.UrlInput);   // still there to correct
    }

    [Fact]
    public void PlainHttpIsRefusedTheSecretTravelsEncryptedOrNotAtAll()
    {
        var store = new FakeCredentialStore();
        var row = Row(store);

        row.UrlInput = "http://hooks.example/x";
        row.SaveUrlCommand.Execute(null);

        Assert.False(row.HasStoredUrl);
        Assert.NotEmpty(row.UrlFeedback);
    }

    [Fact]
    public void AValidUrlSavesAndClearsTheRefusal()
    {
        var store = new FakeCredentialStore();
        var model = Endpoint();
        var row = Row(store, model);

        row.UrlInput = "htp://wrong";
        row.SaveUrlCommand.Execute(null);
        Assert.NotEmpty(row.UrlFeedback);

        row.UrlInput = "https://hooks.example/services/T000/B000";
        row.SaveUrlCommand.Execute(null);

        Assert.True(row.HasStoredUrl);
        Assert.Empty(row.UrlFeedback);
        Assert.Empty(row.UrlInput);
        Assert.Equal("https://hooks.example/services/T000/B000",
            WebhookTransport.ResolveUrl(model, store));
    }

    // ── every attempt stamps the endpoint (#292) ────────────────────────────────

    [Fact]
    public void RecordDeliveryStampsTheEndpointInTheStore()
    {
        var store = new FakeCredentialStore();
        var model = Endpoint();
        store.Config.Webhooks.Add(model);

        WebhookTransport.RecordDelivery(store, model.Id, null);

        Assert.NotNull(model.LastDeliveryAt);
        Assert.Equal("delivered", model.LastDeliveryOutcome);

        WebhookTransport.RecordDelivery(store, model.Id, "410 Gone");
        Assert.Equal("410 Gone", model.LastDeliveryOutcome);
    }

    [Fact]
    public void AStampForAnUnknownEndpointIsANoOp()
    {
        var store = new FakeCredentialStore();

        WebhookTransport.RecordDelivery(store, "gone", "whatever");

        Assert.Empty(store.Config.Webhooks);
    }

    /// <summary>
    /// The run notifier hands the notifier a stamp callback, and what the callback stamps is
    /// the store - so real runs mark their endpoints exactly as the test button does.
    /// </summary>
    [Fact]
    public async Task ARealRunsDeliveryStampsTheEndpoint()
    {
        var store = new FakeCredentialStore();
        var model = Endpoint();
        store.Config.Webhooks.Add(model);
        WebhookTransport.SaveUrl(model, store, "https://hooks.example/x");

        var notifier = new WebhookRunNotifier(store, TestLogs.Temp(), new StampingNotifier());
        notifier.Notify(new RunNotification(RunPhase.Succeeded, "Restore", "MyDb", "SRV01"));
        await notifier.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(model.LastDeliveryAt);
        Assert.Equal("delivered", model.LastDeliveryOutcome);
    }

    /// <summary>Invokes the outcome callback the way the real notifier does after a post.</summary>
    private sealed class StampingNotifier : WebhookNotifier
    {
        public override Task NotifyAsync(
            IReadOnlyList<WebhookEndpoint> endpoints, RunNotification notification,
            Action<string>? log = null, Action<WebhookEndpoint, string?>? outcome = null)
        {
            foreach (var endpoint in endpoints)
                outcome?.Invoke(endpoint, null);
            return Task.CompletedTask;
        }
    }

    // ── the row reads the stamp (#292) ──────────────────────────────────────────

    [Fact]
    public void TheRowSaysWhenNothingHasBeenDelivered()
    {
        var row = Row(new FakeCredentialStore());

        Assert.Equal("No deliveries yet", row.LastDeliveryDisplay);
    }

    [Fact]
    public void TheRowNamesTheLastOutcomeBothWays()
    {
        var store = new FakeCredentialStore();
        var row = Row(store);

        row.NoteDelivery(null);
        Assert.Contains("delivered", row.LastDeliveryDisplay);

        row.NoteDelivery("timed out");
        Assert.Contains("FAILED - timed out", row.LastDeliveryDisplay);
    }
}
