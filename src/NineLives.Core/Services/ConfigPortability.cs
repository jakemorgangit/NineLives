using System.Text.Json;
using System.Text.Json.Serialization;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// What an exported configuration file holds (#213). An explicit shape, not a dump of AppConfig,
/// so what travels is decided here and secrets are impossible by construction rather than by
/// remembering to exclude them.
/// </summary>
public sealed class ExportedConfig
{
    /// <summary>
    /// Format version. Nullable so ABSENCE is detectable (#277): with a defaulted int, any
    /// well-formed JSON - an empty object included - deserialised into a plausible-looking
    /// export and imported as "Nothing new". Export always writes it; Read refuses a file
    /// without it, or from a newer format than this build knows.
    /// </summary>
    public int? FormatVersion { get; set; }

    /// <summary>The newest format this build can read - and the one it writes.</summary>
    public const int CurrentFormatVersion = 1;

    public string? ExportedBy { get; set; }
    public DateTime ExportedAt { get; set; }

    public List<BlobContainerConfig> BlobContainers { get; set; } = [];
    public List<ServerConnection> Servers { get; set; } = [];

    /// <summary>Endpoints travel as shapes - name, format, toggles - with their URLs stripped.</summary>
    public List<WebhookEndpoint> Webhooks { get; set; } = [];

    // Mode is the one preference that travels: nullable, so "this machine never chose" is a
    // real state the merge can respect. Theme and the update/log settings used to be exported
    // too but were never applied on import - an export that carries what the import discards
    // is a promise the file cannot keep, so they are gone (#277). Old files' extra fields
    // deserialise harmlessly.
    public AppMode? Mode { get; set; }
}

/// <summary>What an import did, in words somebody can check against what they expected.</summary>
public sealed record ImportSummary(
    int ContainersAdded, int ContainersUpdated,
    int ServersAdded, int ServersUpdated,
    int WebhooksAdded, int WebhooksUpdated,
    IReadOnlyList<string> NeedCredentials)
{
    public string Describe()
    {
        var parts = new List<string>();

        if (ContainersAdded > 0) parts.Add($"{ContainersAdded} container(s) added");
        if (ContainersUpdated > 0) parts.Add($"{ContainersUpdated} container(s) updated");
        if (ServersAdded > 0) parts.Add($"{ServersAdded} server(s) added");
        if (ServersUpdated > 0) parts.Add($"{ServersUpdated} server(s) updated");
        if (WebhooksAdded > 0) parts.Add($"{WebhooksAdded} webhook(s) added");
        if (WebhooksUpdated > 0) parts.Add($"{WebhooksUpdated} webhook(s) updated");
        if (parts.Count == 0) parts.Add("Nothing new - everything in the file was already here");

        var summary = string.Join(", ", parts) + ".";

        if (NeedCredentials.Count > 0)
            summary += " Credentials do not travel: " + string.Join(", ", NeedCredentials) +
                       " will ask for theirs on first use.";

        return summary;
    }
}

/// <summary>
/// Moves the configuration between machines without moving a single secret (#213).
///
/// The boundary IS the feature: SAS tokens and SQL passwords live in Windows Credential Manager
/// and stay there, so the exported file holds shapes rather than secrets and is safe to put on a
/// share or in a ticket. The one place a secret could hide in plain sight - a SAS pasted into a
/// container URL - is stripped on export.
/// </summary>
public static class ConfigPortability
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Export(AppConfig config)
    {
        var exported = new ExportedConfig
        {
            FormatVersion = ExportedConfig.CurrentFormatVersion,
            ExportedBy = $"Nine Lives {AppVersion.Display}",
            ExportedAt = DateTime.Now,
            BlobContainers = config.BlobContainers.Select(Sanitise).ToList(),
            Servers = config.Servers.Select(Clone).ToList(),

            // A Teams or Slack webhook URL carries its signature in the path - anyone holding it
            // can post as the integration. The entry travels; the secret stays.
            Webhooks = config.Webhooks
                .Select(w => { var c = CloneViaJson(w); c.Url = string.Empty; return c; })
                .ToList(),
            Mode = config.Mode
        };

        return JsonSerializer.Serialize(exported, Options);
    }

    /// <summary>
    /// Null when the file is not an exported configuration at all - malformed JSON, JSON that
    /// never came from Export (no FormatVersion; an empty object used to import as "Nothing
    /// new"), or a format newer than this build knows how to read honestly (#277).
    /// </summary>
    public static ExportedConfig? Read(string json)
    {
        try
        {
            var read = JsonSerializer.Deserialize<ExportedConfig>(json);
            return read?.FormatVersion is >= 1 and <= ExportedConfig.CurrentFormatVersion
                ? read
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folds an exported file into the local configuration: adds what is new, updates what
    /// matches by id, never deletes. Deleting on import would let a stale file taken last month
    /// silently remove this month's containers - an import is additive or it is a trap.
    /// </summary>
    public static ImportSummary Merge(AppConfig target, ExportedConfig imported)
    {
        int containersAdded = 0, containersUpdated = 0, serversAdded = 0, serversUpdated = 0;
        int webhooksAdded = 0, webhooksUpdated = 0;
        var needCredentials = new List<string>();

        foreach (var incoming in imported.BlobContainers)
        {
            var clean = Sanitise(incoming);
            var existing = target.BlobContainers.FirstOrDefault(c => c.Id == clean.Id);

            if (existing == null)
            {
                target.BlobContainers.Add(clean);
                containersAdded++;

                if (clean.AuthMode == BlobAuthMode.SasToken)
                    needCredentials.Add($"container {clean.Name}");
            }
            else
            {
                // The exported URL travels stripped, so an update must not delete the query
                // this machine's URL carries - the same protection webhook URLs get below
                // (#277). Only while the base still matches: a SAS is scoped to what it was
                // issued for, so a changed base makes the old query meaningless and the
                // entry goes back on the needs-credentials list instead.
                var localQuery = existing.ContainerUrl.IndexOf('?');
                if (localQuery >= 0)
                {
                    var localBase = existing.ContainerUrl[..localQuery].TrimEnd('/');
                    if (string.Equals(localBase, clean.ContainerUrl.TrimEnd('/'),
                            StringComparison.OrdinalIgnoreCase))
                        clean.ContainerUrl += existing.ContainerUrl[localQuery..];
                    else if (clean.AuthMode == BlobAuthMode.SasToken)
                        needCredentials.Add($"container {clean.Name}");
                }

                var index = target.BlobContainers.IndexOf(existing);
                target.BlobContainers[index] = clean;
                containersUpdated++;
            }
        }

        foreach (var incoming in imported.Servers)
        {
            var clone = Clone(incoming);
            var existing = target.Servers.FirstOrDefault(s => s.Id == clone.Id);

            if (existing == null)
            {
                target.Servers.Add(clone);
                serversAdded++;

                if (clone.AuthMode.NeedsStoredPassword())
                    needCredentials.Add($"server {clone.Name}");
            }
            else
            {
                var index = target.Servers.IndexOf(existing);
                target.Servers[index] = clone;
                serversUpdated++;
            }
        }

        foreach (var incoming in imported.Webhooks)
        {
            var clone = CloneViaJson(incoming);
            var existing = target.Webhooks.FirstOrDefault(w => w.Id == clone.Id);

            if (existing == null)
            {
                target.Webhooks.Add(clone);
                webhooksAdded++;
                if (!clone.IsUsable) needCredentials.Add($"webhook {clone.Name}");
            }
            else
            {
                // The URL never travels, so an update must not blank the one this machine holds.
                clone.Url = existing.Url;
                target.Webhooks[target.Webhooks.IndexOf(existing)] = clone;
                webhooksUpdated++;
            }
        }

        // The one travelling preference: mode is taken from the file ONLY on a machine that
        // never chose one. Overwriting a choice this machine made is not an import's business.
        target.Mode ??= imported.Mode;

        return new ImportSummary(
            containersAdded, containersUpdated, serversAdded, serversUpdated,
            webhooksAdded, webhooksUpdated, needCredentials);
    }

    /// <summary>
    /// A copy with the one plain-sight secret removed: a SAS pasted into the container URL. The
    /// query string is not part of a container's identity, so nothing of value is lost.
    /// </summary>
    internal static BlobContainerConfig Sanitise(BlobContainerConfig source)
    {
        var clone = CloneViaJson(source);

        var url = clone.ContainerUrl;
        var query = url.IndexOf('?');
        if (query >= 0) clone.ContainerUrl = url[..query];

        return clone;
    }

    private static ServerConnection Clone(ServerConnection source) => CloneViaJson(source);

    /// <summary>
    /// Serialize-deserialize cloning, deliberately: it applies every [JsonIgnore] in the models,
    /// so anything those marked as not-for-disk - unsaved passwords, unsaved SAS tokens - cannot
    /// survive into the export even if a future field forgets this class exists.
    /// </summary>
    private static T CloneViaJson<T>(T source) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;
}
