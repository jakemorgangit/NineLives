using System.Net;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>How webhook deliveries reach the network (#316).</summary>
public enum WebhookProxyMode
{
    /// <summary>The machine's own proxy configuration - the default, and what happened before.</summary>
    SystemDefault,

    /// <summary>No proxy at all, even if the system has one configured.</summary>
    Direct,

    /// <summary>An explicit proxy URL, optionally authenticated.</summary>
    Custom
}

/// <summary>
/// The proxy webhook deliveries use (#316). Estate-level rather than per-endpoint: a corporate
/// proxy is a property of the network the machine sits on, not of any one Teams channel. The
/// password is NOT here - it lives in Windows Credential Manager like every other secret.
/// </summary>
public class WebhookProxySettings
{
    public WebhookProxyMode Mode { get; set; } = WebhookProxyMode.SystemDefault;
    public string? Url { get; set; }
    public string? Username { get; set; }
}

/// <summary>
/// Builds the transport pieces every webhook delivery shares - the app's runs, the Settings
/// test button and the CLI's drained sends alike (#316, #317). Pure construction, so the
/// handler a given configuration produces is provable in a test without a live proxy.
/// </summary>
public static class WebhookTransport
{
    /// <summary>Where the custom proxy's password lives in the vault.</summary>
    public const string ProxyCredentialKey = "NineLives:WebhookProxy";

    public static HttpMessageHandler BuildHandler(
        WebhookProxySettings? settings, ICredentialStore store)
    {
        var handler = new HttpClientHandler();

        switch (settings?.Mode)
        {
            case WebhookProxyMode.Direct:
                handler.UseProxy = false;
                break;

            case WebhookProxyMode.Custom when !string.IsNullOrWhiteSpace(settings.Url):
                var proxy = new WebProxy(settings.Url);
                if (!string.IsNullOrWhiteSpace(settings.Username))
                {
                    var (_, password) = store.ReadSecret(ProxyCredentialKey);
                    proxy.Credentials = new NetworkCredential(settings.Username, password);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;

            // SystemDefault and a Custom mode with no URL both fall through to the handler's
            // own defaults: the system proxy, exactly as before this setting existed.
        }

        return handler;
    }

    /// <summary>
    /// The URL a delivery should actually post to (#317): the vault first, the legacy in-file
    /// value second. Old configs keep working untouched; a row's next explicit save migrates
    /// its URL into the vault and blanks the file copy.
    /// </summary>
    public static string? ResolveUrl(WebhookEndpoint endpoint, ICredentialStore store)
    {
        var (_, stored) = store.ReadSecret(endpoint.CredentialKey);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        return string.IsNullOrWhiteSpace(endpoint.Url) ? null : endpoint.Url;
    }

    /// <summary>True when the endpoint has a URL anywhere - the vault or the legacy file copy.</summary>
    public static bool HasUrl(WebhookEndpoint endpoint, ICredentialStore store)
        => ResolveUrl(endpoint, store) != null;

    /// <summary>
    /// Commits a URL as a secret (#317): into the vault, out of the file. After this the app
    /// never displays it again - replacing it means saving a new one, not reading this one.
    /// </summary>
    public static void SaveUrl(WebhookEndpoint endpoint, ICredentialStore store, string url)
    {
        store.SaveSecret(endpoint.CredentialKey, "webhook", url.Trim());
        endpoint.Url = string.Empty;
    }

    /// <summary>The endpoints ready to post, hydrated as CLONES - config objects never carry the secret.</summary>
    public static List<WebhookEndpoint> HydrateUsable(
        IEnumerable<WebhookEndpoint> endpoints, ICredentialStore store)
    {
        var usable = new List<WebhookEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var url = ResolveUrl(endpoint, store);
            if (url == null) continue;

            usable.Add(new WebhookEndpoint
            {
                Id = endpoint.Id,
                Name = endpoint.Name,
                Url = url,
                Format = endpoint.Format,
                NotifyStarted = endpoint.NotifyStarted,
                NotifyFinished = endpoint.NotifyFinished,
                NotifyProblems = endpoint.NotifyProblems
            });
        }

        return usable;
    }
}
