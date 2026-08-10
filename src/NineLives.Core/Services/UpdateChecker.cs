using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Blackcat.NineLives.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>
/// Asks GitHub whether a newer release exists. Tells the user; never downloads or installs.
///
/// Every failure here is a no-op. No network, a proxy, a rate limit, GitHub down, a changed API
/// shape - all of it must leave the app behaving exactly as if the check never ran. A restore
/// tool must not be impeded by its own update check.
/// </summary>
public class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/jakemorgangit/NineLives/releases/latest";

    public const string ReleasesPage = "https://github.com/jakemorgangit/NineLives/releases";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpMessageHandler? _handler;

    public UpdateChecker(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>Latest published release, or null if it could not be determined.</summary>
    public async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = _handler == null ? new HttpClient() : new HttpClient(_handler, false);
            client.Timeout = Timeout;

            // GitHub rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("NineLives", AppVersion.Current.ToString(3)));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var response = await client.GetAsync(LatestReleaseApi, ct);
            if (!response.IsSuccessStatusCode) return null;

            return ParseRelease(await response.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a releases API payload. Returns null for anything unexpected.</summary>
    internal static ReleaseInfo? ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            // A draft or prerelease is not something to nudge people towards.
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return null;

            if (!root.TryGetProperty("tag_name", out var tagElement)) return null;

            var tag = tagElement.GetString();
            if (AppVersion.Parse(tag) is not { } version) return null;

            var url = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? ReleasesPage
                : ReleasesPage;

            return new ReleaseInfo(version, tag!, url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether to show the banner. Separate from fetching so the rules are testable without
    /// touching the network.
    /// </summary>
    internal static bool ShouldNotify(
        Version current, ReleaseInfo? latest, string? alreadyNotifiedTag, bool enabled)
    {
        if (!enabled || latest == null) return false;

        // Running a build newer than the latest release (a local build) is not an update.
        if (latest.Version <= current) return false;

        // Only mention a given release once.
        return !string.Equals(latest.Tag, alreadyNotifiedTag, StringComparison.OrdinalIgnoreCase);
    }
}
