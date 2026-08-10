using System.Reflection;

namespace Blackcat.NineLives.Services;

/// <summary>The running app's version, read from the assembly rather than a hardcoded string.</summary>
public static class AppVersion
{
    public static Version Current { get; } = Resolve();

    /// <summary>e.g. "v1.1.0"</summary>
    public static string Display => $"v{Current.ToString(3)}";

    private static Version Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        // InformationalVersion carries what the csproj <Version> said. It can have a build
        // suffix (+sha, -beta) that Version.Parse rejects, so trim before parsing.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (Parse(informational) is { } parsed) return parsed;

        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    internal static Version? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim().TrimStart('v', 'V');
        foreach (var separator in new[] { '+', '-' })
        {
            var index = text.IndexOf(separator);
            if (index > 0) text = text[..index];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }
}
