using Microsoft.Data.SqlClient;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Entra ID authentication to SQL Server (#30).
///
/// **Untested against a real tenant.** There is no Entra-enabled instance to develop against, so
/// what is pinned here is the connection string the driver is handed and the promise that nothing
/// is stored - not that a sign-in succeeds. The token flow itself belongs to
/// Microsoft.Data.SqlClient and is Microsoft's to get right; what this project can get wrong is the
/// string it hands over, and that is exactly what these tests cover.
///
/// The reason it matters is the estate that needs it: Azure SQL Managed Instance and Azure-VM SQL
/// increasingly mandate Entra with MFA, which neither Windows nor SQL auth can satisfy.
/// </summary>
public class EntraSqlAuthTests
{
    private static SqlServerService Service() => new(new FakeCredentialStore());

    private static ServerConnection Server(AuthMode mode, string? username = null) => new()
    {
        Id = ServerConnection.NewId(),
        Name = "SRV01",
        ServerName = "srv01.database.windows.net",
        AuthMode = mode,
        Username = username
    };

    private static SqlConnectionStringBuilder Built(AuthMode mode, string? username = null) =>
        new(Service().BuildConnectionString(Server(mode, username)));

    // ── the connection string ───────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthMode.EntraInteractive, SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [InlineData(AuthMode.EntraIntegrated, SqlAuthenticationMethod.ActiveDirectoryIntegrated)]
    [InlineData(AuthMode.EntraDefault, SqlAuthenticationMethod.ActiveDirectoryDefault)]
    public void EachEntraModeMapsToItsDriverAuthenticationMethod(
        AuthMode mode, SqlAuthenticationMethod expected)
    {
        Assert.Equal(expected, Built(mode).Authentication);
    }

    /// <summary>
    /// Integrated Security and Authentication are mutually exclusive - the driver rejects a
    /// connection string carrying both, so this is the difference between working and not opening
    /// at all.
    /// </summary>
    [Theory]
    [InlineData(AuthMode.EntraInteractive)]
    [InlineData(AuthMode.EntraIntegrated)]
    [InlineData(AuthMode.EntraDefault)]
    public void EntraNeverAlsoAsksForIntegratedSecurity(AuthMode mode)
    {
        Assert.False(Built(mode).IntegratedSecurity);
    }

    [Theory]
    [InlineData(AuthMode.WindowsAuth)]
    [InlineData(AuthMode.SqlAuth)]
    public void TheExistingModesAskForNoAuthenticationMethod(AuthMode mode)
    {
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, Built(mode).Authentication);
    }

    [Fact]
    public void WindowsAuthStillUsesIntegratedSecurity()
    {
        Assert.True(Built(AuthMode.WindowsAuth).IntegratedSecurity);
    }

    // ── the username ────────────────────────────────────────────────────────────

    /// <summary>
    /// Interactive sign-in takes a username as a hint that pre-selects the account, which is worth
    /// having on a machine signed in to several. It is not a credential - there is no password to
    /// pair it with - so unlike the SQL auth username it is safe in the connection string.
    /// </summary>
    [Fact]
    public void AnInteractiveHintIsPassedThroughToPreSelectTheAccount()
    {
        Assert.Equal("dba@example.com", Built(AuthMode.EntraInteractive, "dba@example.com").UserID);
    }

    [Fact]
    public void InteractiveWithNoHintAsksForNoParticularAccount()
    {
        Assert.Empty(Built(AuthMode.EntraInteractive).UserID);
    }

    /// <summary>
    /// The other two modes choose the account themselves - integrated from the signed-in Windows
    /// account, default from the environment or a managed identity. Passing a username would either
    /// be ignored or contradict what the mode is for.
    /// </summary>
    [Theory]
    [InlineData(AuthMode.EntraIntegrated)]
    [InlineData(AuthMode.EntraDefault)]
    public void TheNonInteractiveModesIgnoreAUsername(AuthMode mode)
    {
        Assert.Empty(Built(mode, "dba@example.com").UserID);
    }

    // ── nothing is stored ───────────────────────────────────────────────────────

    /// <summary>
    /// The whole reason an organisation mandates Entra is to stop tools holding passwords. A
    /// SqlCredential attached to an Entra connection would also make the driver reject it.
    /// </summary>
    [Theory]
    [InlineData(AuthMode.EntraInteractive)]
    [InlineData(AuthMode.EntraIntegrated)]
    [InlineData(AuthMode.EntraDefault)]
    public void NoPasswordIsEverAttachedForAnEntraConnection(AuthMode mode)
    {
        var store = new FakeCredentialStore();
        var server = Server(mode, "dba@example.com");

        // Even with a password sitting in the vault from a previous SQL auth life.
        store.SaveSqlPassword(server, "left-over-password");

        using var conn = new SqlServerService(store).CreateConnection(server);

        Assert.Null(conn.Credential);
        Assert.DoesNotContain("left-over-password", conn.ConnectionString);
    }

    [Theory]
    [InlineData(AuthMode.EntraInteractive, false)]
    [InlineData(AuthMode.EntraIntegrated, false)]
    [InlineData(AuthMode.EntraDefault, false)]
    [InlineData(AuthMode.WindowsAuth, false)]
    [InlineData(AuthMode.SqlAuth, true)]
    public void OnlySqlAuthNeedsAStoredPassword(AuthMode mode, bool expected)
    {
        Assert.Equal(expected, mode.NeedsStoredPassword());
    }

    // ── how it reads ────────────────────────────────────────────────────────────

    /// <summary>
    /// The list shows the username only when it identifies the login connecting. For interactive
    /// Entra it is a hint for the account picker, not necessarily the account that ends up
    /// connecting, so showing it would misstate who is connected to a production instance.
    /// </summary>
    [Fact]
    public void TheListNamesTheModeRatherThanTheHint()
    {
        Assert.Equal(
            "srv01.database.windows.net (Entra ID (interactive))",
            Server(AuthMode.EntraInteractive, "dba@example.com").DisplayText);
    }

    [Fact]
    public void SqlAuthStillShowsWhoIsConnecting()
    {
        Assert.Equal("srv01.database.windows.net (sa)", Server(AuthMode.SqlAuth, "sa").DisplayText);
    }

    [Fact]
    public void WindowsAuthStillSaysSo()
    {
        Assert.Equal(
            "srv01.database.windows.net (Windows Auth)", Server(AuthMode.WindowsAuth).DisplayText);
    }

    /// <summary>
    /// The stored values are what config.json holds. Reordering the enum would silently repoint
    /// every saved connection at a different authentication mode - a Windows auth entry quietly
    /// becoming an Entra one is the kind of change nobody notices until a restore fails.
    /// </summary>
    [Theory]
    [InlineData(AuthMode.WindowsAuth, 0)]
    [InlineData(AuthMode.SqlAuth, 1)]
    [InlineData(AuthMode.EntraInteractive, 2)]
    [InlineData(AuthMode.EntraIntegrated, 3)]
    [InlineData(AuthMode.EntraDefault, 4)]
    public void TheStoredValuesArePinned(AuthMode mode, int expected)
    {
        Assert.Equal(expected, (int)mode);
    }
}
