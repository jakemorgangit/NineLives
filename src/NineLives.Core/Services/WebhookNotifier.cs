using System.Net.Http;
using System.Text;
using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Builds and posts the notification payloads (#242).
///
/// The formats follow the PerformanceMonitor implementation deliberately - the same three
/// providers, the same shapes that are already proven against real Teams and Slack channels:
/// Teams gets a MessageCard (legacy connectors and Power Automate request-trigger flows both
/// accept it), Slack gets Block Kit, and Generic gets plain JSON for anything home-made.
///
/// Delivery is best-effort with a short timeout, and a failure is a log line, never a dialog:
/// the run's outcome must not be hostage to a chat service.
/// </summary>
public class WebhookNotifier
{
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">Lets a test intercept the POST; null uses the real network.</param>
    public WebhookNotifier(HttpMessageHandler? handler = null) => _handler = handler;

    // ── what each phase looks like ──────────────────────────────────────────────

    /// <summary>Colour, badge and emoji per phase - blue for started, green done, red trouble.</summary>
    internal static (string ThemeColor, string Badge, string Emoji) Look(RunPhase phase) => phase switch
    {
        RunPhase.Started => ("0078D7", "STARTED", "\U0001F535"),
        RunPhase.Succeeded => ("107C10", "COMPLETED", "✅"),
        _ => ("D13438", "PROBLEM", "❌")
    };

    internal static string Title(RunNotification n)
    {
        var (_, badge, emoji) = Look(n.Phase);
        return $"{emoji} {badge} — {n.Operation} {n.Subject}";
    }

    private static string DescribeDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m {d.Seconds:D2}s"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:D2}s"
        : $"{d.TotalSeconds:N0}s";

    // ── the payloads ────────────────────────────────────────────────────────────

    /// <summary>
    /// A MessageCard: activityTitle, facts, themeColor. The same card the PerformanceMonitor
    /// webhooks send, which both the legacy Teams connectors and the Power Automate flows that
    /// replaced them render.
    /// </summary>
    internal static string BuildTeamsPayload(RunNotification n, bool isTest = false)
    {
        var (themeColor, badge, _) = Look(n.Phase);

        var facts = new List<object>();

        if (isTest)
        {
            facts.Add(new { name = "Status", value = "Webhook configuration is working correctly" });
            facts.Add(new { name = "Sent at", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        else
        {
            facts.Add(new { name = "Operation", value = $"{n.Operation} {n.Subject}" });
            facts.Add(new { name = "Server", value = n.Target });
            if (n.Duration is { } d)
                facts.Add(new { name = "Duration", value = DescribeDuration(d) });
            if (!string.IsNullOrWhiteSpace(n.Detail))
                facts.Add(new { name = n.Phase == RunPhase.Problem ? "Problem" : "Detail", value = n.Detail });
            facts.Add(new { name = "Time (Local)", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            facts.Add(new { name = "Time (UTC)", value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        var card = new Dictionary<string, object>
        {
            // Dictionary keys, not anonymous-object members: MessageCard requires the literal
            // "@type" and "@context", and @ in a C# identifier is an escape, not a character -
            // the anonymous form serialises as "type", which Teams quietly drops on the floor.
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["themeColor"] = themeColor,
            ["summary"] = isTest
                ? "[Nine Lives] Test Notification"
                : $"[Nine Lives] {badge}: {n.Operation} {n.Subject} on {n.Target}",
            ["sections"] = new object[]
            {
                new
                {
                    activityTitle = isTest ? "\U0001F535 TEST — Nine Lives notifications" : Title(n),
                    activitySubtitle = $"Nine Lives {AppVersion.Display}",
                    facts,
                    markdown = true
                }
            }
        };

        return JsonSerializer.Serialize(card, JsonOptions);
    }

    /// <summary>Slack Block Kit: a header block, then mrkdwn fields - PerformanceMonitor's shape.</summary>
    internal static string BuildSlackPayload(RunNotification n, bool isTest = false)
    {
        var fields = new List<object>();

        if (isTest)
        {
            fields.Add(new { type = "mrkdwn", text = "*Status:*\nWebhook configuration is working correctly" });
            fields.Add(new { type = "mrkdwn", text = $"*Sent at:*\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}" });
        }
        else
        {
            fields.Add(new { type = "mrkdwn", text = $"*Operation:*\n{n.Operation} {n.Subject}" });
            fields.Add(new { type = "mrkdwn", text = $"*Server:*\n{n.Target}" });
            if (n.Duration is { } d)
                fields.Add(new { type = "mrkdwn", text = $"*Duration:*\n{DescribeDuration(d)}" });
            fields.Add(new { type = "mrkdwn", text = $"*Time (Local):*\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}" });
        }

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new
                {
                    type = "plain_text",
                    text = isTest ? "\U0001F535 TEST — Nine Lives notifications" : Title(n),
                    emoji = true
                }
            },
            new { type = "section", fields }
        };

        if (!isTest && !string.IsNullOrWhiteSpace(n.Detail))
        {
            blocks.Add(new { type = "divider" });
            blocks.Add(new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*{(n.Phase == RunPhase.Problem ? "Problem" : "Detail")}:*\n{n.Detail}"
                }
            });
        }

        return JsonSerializer.Serialize(new { blocks }, JsonOptions);
    }

    /// <summary>
    /// Plain JSON for the home-made consumer - a Power Automate HTTP trigger, Zapier, a ticket
    /// system. Everything the fancy formats carry, as fields, nothing needing parsing back out
    /// of display text.
    /// </summary>
    internal static string BuildGenericPayload(RunNotification n, bool isTest = false)
    {
        var payload = new
        {
            app = "NineLives",
            version = AppVersion.Display,
            isTest,
            phase = n.Phase.ToString(),
            operation = n.Operation,
            subject = n.Subject,
            target = n.Target,
            detail = n.Detail,
            durationSeconds = n.Duration?.TotalSeconds,
            atLocal = DateTime.Now,
            atUtc = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static string BuildPayload(WebhookFormat format, RunNotification n, bool isTest = false) =>
        format switch
        {
            WebhookFormat.Slack => BuildSlackPayload(n, isTest),
            WebhookFormat.Generic => BuildGenericPayload(n, isTest),
            _ => BuildTeamsPayload(n, isTest)
        };

    // ── sending ─────────────────────────────────────────────────────────────────

    /// <summary>Whether this endpoint wants to hear about this phase.</summary>
    internal static bool Wants(WebhookEndpoint endpoint, RunPhase phase) => phase switch
    {
        RunPhase.Started => endpoint.NotifyStarted,
        RunPhase.Succeeded => endpoint.NotifyFinished,
        _ => endpoint.NotifyProblems
        };

    /// <summary>
    /// Posts to every endpoint that wants this phase. Failures go to <paramref name="log"/> and
    /// nowhere else - the run this describes must never be affected by its announcement.
    /// </summary>
    public async Task NotifyAsync(
        IReadOnlyList<WebhookEndpoint> endpoints, RunNotification notification, Action<string>? log = null)
    {
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.IsUsable || !Wants(endpoint, notification.Phase)) continue;

            var error = await PostAsync(endpoint.Url, BuildPayload(endpoint.Format, notification));

            log?.Invoke(error == null
                ? $"[notify] {endpoint.Name}: {notification.Phase} {notification.Operation} {notification.Subject}"
                : $"[notify] {endpoint.Name} FAILED: {error}");
        }
    }

    /// <summary>A test message to one endpoint. Null on success, the error text on failure.</summary>
    public Task<string?> SendTestAsync(WebhookEndpoint endpoint)
    {
        if (!endpoint.IsUsable) return Task.FromResult<string?>("The webhook URL is empty.");

        var test = new RunNotification(RunPhase.Started, "Test", "notification", Environment.MachineName);
        return PostAsync(endpoint.Url, BuildPayload(endpoint.Format, test, isTest: true));
    }

    private async Task<string?> PostAsync(string url, string payload)
    {
        try
        {
            using var client = _handler == null ? new HttpClient() : new HttpClient(_handler, false);
            client.Timeout = PostTimeout;

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            return $"HTTP {(int)response.StatusCode}: {Truncate(body)}";
        }
        catch (TaskCanceledException)
        {
            return $"No response within {PostTimeout.TotalSeconds:N0}s.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
