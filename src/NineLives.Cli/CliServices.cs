using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// The engine services a verb runs against, and the configuration they share. The same
/// implementations the GUI composes - which is the entire point (#63): configure containers,
/// servers and credentials once in the app, then script against that exact configuration here.
/// Bundled so tests hand verbs fakes the same way the app's tests do.
///
/// History and notifications are the SAME stores and endpoints as the app's: a restore run
/// from a pipeline lands in the History screen, a rehearsal's receipt feeds the exposure
/// dashboard's Proven column, and the Teams channel hears about both. One estate, two front
/// ends.
/// </summary>
internal sealed class CliServices(
    ICredentialStore store, ISqlServerService sql, IBlobStorageService blobs,
    IOperationHistoryStore history, IRunNotifier notifier,
    ServerConnection? ephemeralServer = null, BlobContainerConfig? ephemeralContainer = null)
{
    /// <summary>The --ephemeral definitions (#302), consulted before config, persisted nowhere.</summary>
    private readonly ServerConnection? _ephemeralServer = ephemeralServer;
    private readonly BlobContainerConfig? _ephemeralContainer = ephemeralContainer;

    public ICredentialStore Store { get; } = store;
    public ISqlServerService Sql { get; } = sql;
    public IBlobStorageService Blobs { get; } = blobs;
    public IOperationHistoryStore History { get; } = history;
    public IRunNotifier Notifier { get; } = notifier;

    public AppConfig Config => _config ??= Store.LoadConfig();
    private AppConfig? _config;

    /// <summary>
    /// A container by its configured name. The error names what IS configured - being told the
    /// options beats being told to go and look them up.
    /// </summary>
    public (BlobContainerConfig? container, string? error) FindContainer(string name)
    {
        // Ephemeral first (#302): a name defined for this invocation outranks the profile's.
        if (_ephemeralContainer != null &&
            string.Equals(_ephemeralContainer.Name, name, StringComparison.OrdinalIgnoreCase))
            return (_ephemeralContainer, null);

        var match = Config.BlobContainers
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match != null) return (match, null);

        var names = Config.BlobContainers.Select(c => c.Name).ToList();
        if (_ephemeralContainer != null) names.Insert(0, _ephemeralContainer.Name + " (ephemeral)");
        var known = names.Count == 0
            ? "none are configured - add one in the app first"
            : string.Join(", ", names);
        return (null, $"No container called '{name}'. Configured containers: {known}.");
    }

    /// <summary>A server by its configured name, same contract as <see cref="FindContainer"/>.</summary>
    public (ServerConnection? server, string? error) FindServer(string name)
    {
        // Ephemeral first (#302): a name defined for this invocation outranks the profile's.
        if (_ephemeralServer != null &&
            string.Equals(_ephemeralServer.Name, name, StringComparison.OrdinalIgnoreCase))
            return (_ephemeralServer, null);

        var match = Config.Servers
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match != null) return (match, null);

        var names = Config.Servers.Select(s => s.Name).ToList();
        if (_ephemeralServer != null) names.Insert(0, _ephemeralServer.Name + " (ephemeral)");
        var known = names.Count == 0
            ? "none are configured - add one in the app first"
            : string.Join(", ", names);
        return (null, $"No server called '{name}'. Configured servers: {known}.");
    }
}
