using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// The engine services a verb runs against, and the configuration they share. The same
/// implementations the GUI composes - which is the entire point (#63): configure containers,
/// servers and credentials once in the app, then script against that exact configuration here.
/// Bundled so tests hand verbs fakes the same way the app's tests do.
/// </summary>
internal sealed class CliServices(
    ICredentialStore store, ISqlServerService sql, IBlobStorageService blobs)
{
    public ICredentialStore Store { get; } = store;
    public ISqlServerService Sql { get; } = sql;
    public IBlobStorageService Blobs { get; } = blobs;

    public AppConfig Config => _config ??= Store.LoadConfig();
    private AppConfig? _config;

    /// <summary>
    /// A container by its configured name. The error names what IS configured - being told the
    /// options beats being told to go and look them up.
    /// </summary>
    public (BlobContainerConfig? container, string? error) FindContainer(string name)
    {
        var match = Config.BlobContainers
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match != null) return (match, null);

        var known = Config.BlobContainers.Count == 0
            ? "none are configured - add one in the app first"
            : string.Join(", ", Config.BlobContainers.Select(c => c.Name));
        return (null, $"No container called '{name}'. Configured containers: {known}.");
    }

    /// <summary>A server by its configured name, same contract as <see cref="FindContainer"/>.</summary>
    public (ServerConnection? server, string? error) FindServer(string name)
    {
        var match = Config.Servers
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match != null) return (match, null);

        var known = Config.Servers.Count == 0
            ? "none are configured - add one in the app first"
            : string.Join(", ", Config.Servers.Select(s => s.Name));
        return (null, $"No server called '{name}'. Configured servers: {known}.");
    }
}
