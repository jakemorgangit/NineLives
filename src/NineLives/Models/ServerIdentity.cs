namespace Blackcat.NineLives.Models;

/// <summary>
/// Formatting and matching for the "which SQL Server did this backup come from" identity.
///
/// A host can run several named instances, and two instances of one host routinely hold
/// same-named databases. Comparing only the host part therefore merges them: selecting
/// SQLHOST\PROD would also match SQLHOST\TEST, and the restore timeline would interleave both.
///
/// This lived as four separate hand-rolled comparisons across two ViewModels, two of which
/// silently ignored the instance. One implementation, used everywhere.
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Renders a server/instance pair the way the filter dropdowns present it:
    /// <c>HOST\INSTANCE</c>, or bare <c>HOST</c> when there is no instance.
    /// Returns null when there is no server information at all.
    /// </summary>
    public static string? Format(string? server, string? instance)
    {
        if (string.IsNullOrWhiteSpace(server))
            return string.IsNullOrWhiteSpace(instance) ? null : instance;

        return string.IsNullOrWhiteSpace(instance) ? server : $@"{server}\{instance}";
    }

    /// <summary>
    /// True when a server/instance pair matches a filter value taken from the dropdown.
    ///
    /// An empty filter matches everything. A <c>HOST\INSTANCE</c> filter requires both to match.
    /// A bare <c>HOST</c> filter matches only backups with NO instance, because a container that
    /// mixes both shapes lists them as separate entries - so a bare host must not silently
    /// swallow every instance under it.
    /// </summary>
    public static bool Matches(string? server, string? instance, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var parts = filter.Split('\\', 2);
        var filterServer = parts[0];
        var filterInstance = parts.Length == 2 ? parts[1] : null;

        if (!string.Equals(server, filterServer, StringComparison.OrdinalIgnoreCase))
            return false;

        // Treat null and empty as the same "no instance" state - path parsing can produce either.
        var hasInstance = !string.IsNullOrWhiteSpace(instance);
        var filterHasInstance = !string.IsNullOrWhiteSpace(filterInstance);

        if (!filterHasInstance) return !hasInstance;

        return hasInstance
            && string.Equals(instance, filterInstance, StringComparison.OrdinalIgnoreCase);
    }
}
