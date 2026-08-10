using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The real <see cref="IRunNotifier"/>: reads the configured endpoints and posts to them in the
/// background (#242).
///
/// The endpoint list is read fresh per notification rather than cached, so a webhook added in
/// Settings starts working immediately - a restore is exactly the wrong moment to discover the
/// app is still holding yesterday's list.
///
/// Fire-and-forget by construction: Notify returns before any network happens, and everything
/// that can go wrong lands in the operation log. A notification is ABOUT a run; it must never
/// slow, block or fail one.
/// </summary>
public sealed class WebhookRunNotifier : IRunNotifier
{
    private readonly ICredentialStore _store;
    private readonly OperationLog _log;
    private readonly WebhookNotifier? _injected;

    /// <summary>
    /// Deliveries still in flight, so a short-lived process can drain them before exiting
    /// (#296). Completed entries are pruned as new ones start; the lock guards the list, not
    /// the deliveries.
    /// </summary>
    private readonly List<Task> _inFlight = [];
    private readonly Lock _gate = new();

    public WebhookRunNotifier(ICredentialStore store, OperationLog log, WebhookNotifier? notifier = null)
    {
        _store = store;
        _log = log;
        _injected = notifier;
    }

    public void Notify(RunNotification notification)
    {
        List<WebhookEndpoint> endpoints;
        WebhookProxySettings? proxy;
        try
        {
            var config = _store.LoadConfig();
            // Hydrated clones (#317): saved URLs live in the vault, resolved at send time,
            // and the config's own objects never carry the secret.
            endpoints = WebhookTransport.HydrateUsable(config.Webhooks, _store);
            proxy = config.WebhookProxy;
        }
        catch
        {
            // An unreadable config is reported where it is loaded for real; a notification is
            // not the place to add a second copy of that failure.
            return;
        }

        if (endpoints.Count == 0) return;

        var delivery = Task.Run(async () =>
        {
            try
            {
                // The injected notifier (tests) is used as-is; the real one is built per
                // delivery with the configured transport (#316), so a proxy change in
                // Settings applies to the very next send.
                // The stamp makes silent breakage visible in Settings (#292): every
                // attempt records when and how it went, per endpoint, test and run alike.
                Action<WebhookEndpoint, string?> stamp =
                    (endpoint, error) => WebhookTransport.RecordDelivery(_store, endpoint.Id, error);

                if (_injected != null)
                {
                    await _injected.NotifyAsync(endpoints, notification, line => _log.Info(line), stamp);
                }
                else
                {
                    using var handler = WebhookTransport.BuildHandler(proxy, _store);
                    var notifier = new WebhookNotifier(handler);
                    await notifier.NotifyAsync(endpoints, notification, line => _log.Info(line), stamp);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[notify] delivery failed: {ex.Message}");
            }
        });

        lock (_gate)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            _inFlight.Add(delivery);
        }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _inFlight.Where(t => !t.IsCompleted).ToArray();
        }

        if (pending.Length == 0) return;

        // WhenAny against the clock: a dead endpoint must not hold a scheduled task's process
        // open. The deliveries own their exceptions (logged above), so WhenAll cannot throw.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
    }
}
