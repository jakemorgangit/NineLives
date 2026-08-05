namespace Blackcat.NineLives.Services;

/// <summary>
/// What <see cref="SqlServerService.EnsureCredentialExistsAsync"/> actually did to the server.
///
/// Callers report this in the execution log. Modifying a server-scoped credential is a change to
/// shared state on someone's production instance, made by a tool that was only asked to run a
/// restore - so it should never happen without a line saying it happened.
/// </summary>
public enum CredentialChange
{
    /// <summary>Nothing was sent to the server; a usable credential was already there.</summary>
    None,

    /// <summary>The credential did not exist and was created.</summary>
    Created,

    /// <summary>The credential existed and its secret (and identity) were updated in place.</summary>
    Updated
}
