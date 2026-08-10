using System.Text.Json.Serialization;

namespace Blackcat.NineLives.Models;

/// <summary>Which shape of payload an endpoint expects (#242).</summary>
public enum WebhookFormat
{
    /// <summary>
    /// A MessageCard. Understood by both the legacy Teams incoming-webhook connectors and the
    /// Power Automate "when a Teams webhook request is received" flows that replaced them - the
    /// same dual audience the PerformanceMonitor implementation serves with this format.
    /// </summary>
    Teams = 0,

    /// <summary>Slack Block Kit - a header block and mrkdwn section fields.</summary>
    Slack = 1,

    /// <summary>Plain JSON, for Power Automate HTTP triggers, Zapier, n8n, or anything home-made.</summary>
    Generic = 2
}

/// <summary>
/// One place notifications go (#242): a Teams channel, a Slack channel, or any endpoint that
/// accepts JSON. A list of these, because "Teams for the team, Slack for the on-call bridge, and
/// the generic one into the ticket system" is an ordinary arrangement, not an edge case.
/// </summary>
public class WebhookEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What it is called in Settings and in the log - "DBA Teams channel".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The webhook URL. A SECRET in all but name: a Teams or Slack webhook URL carries its
    /// signature in the path, and anyone holding it can post as the integration. It therefore
    /// never leaves this machine in a config export (#213's rule).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    public WebhookFormat Format { get; set; } = WebhookFormat.Teams;

    // ── which moments are worth a message ───────────────────────────────────────
    //
    // All three on by default: the whole point (#242) is knowing what the app is doing when focus
    // is NOT on the application, and a problem nobody hears about is the worst of the three.

    /// <summary>A backup, restore or copy has started.</summary>
    public bool NotifyStarted { get; set; } = true;

    /// <summary>It finished successfully.</summary>
    public bool NotifyFinished { get; set; } = true;

    /// <summary>It failed, was cancelled, or hit a problem mid-run.</summary>
    public bool NotifyProblems { get; set; } = true;

    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(Url);
}
