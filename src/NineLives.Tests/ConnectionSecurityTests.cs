using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The password stays out of the connection string (#20), and certificate trust is answerable
/// rather than assumed (#17).
/// </summary>
public class ConnectionSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-connsec-tests", Guid.NewGuid().ToString("n"));

    private readonly List<string> _writtenKeys = [];

    private CredentialStore Store() => new(_dir);
    private SqlServerService Service() => new(Store());

    public ConnectionSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        var store = Store();
        foreach (var key in _writtenKeys) { try { store.DeleteSecret(key); } catch { } }
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ServerConnection SqlAuthServer(string password)
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-" + Guid.NewGuid().ToString("n")[..8],
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa"
        };
        Store().SaveSqlPassword(server, password);
        _writtenKeys.Add(server.CredentialKey);
        return server;
    }

    // ── #20 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression. The password used to be assigned into the connection string, which is a
    /// long-lived managed string that cannot be zeroed and turns up in crash dumps - and which
    /// anything logging a connection string would spill.
    /// </summary>
    [Fact]
    public void ThePasswordNeverAppearsInTheConnectionString()
    {
        const string password = "unmistakeable-password-value";
        var server = SqlAuthServer(password);

        var connectionString = Service().BuildConnectionString(server);

        Assert.DoesNotContain(password, connectionString);
        Assert.DoesNotContain("Password", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnsavedPasswordDoesNotAppearInTheConnectionStringEither()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-unsaved-conn",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa",
            UnsavedPassword = "candidate-password-value"
        };

        var connectionString = Service().BuildConnectionString(server);

        Assert.DoesNotContain("candidate-password-value", connectionString);
    }

    [Fact]
    public void SqlAuthConnectionsCarryACredential()
    {
        var server = SqlAuthServer("stored");

        using var conn = Service().CreateConnection(server);

        Assert.NotNull(conn.Credential);
        Assert.Equal("sa", conn.Credential!.UserId);
    }

    [Fact]
    public void SqlAuthConnectionStringDoesNotUseIntegratedSecurity()
    {
        // SqlCredential refuses to attach when the connection string asks for integrated security,
        // so this is load-bearing rather than cosmetic.
        var connectionString = Service().BuildConnectionString(SqlAuthServer("stored"));

        Assert.Contains("Integrated Security=False", connectionString);
    }

    [Fact]
    public void WindowsAuthStillUsesIntegratedSecurityAndNoCredential()
    {
        var server = new ServerConnection
        {
            Name = "ninelives-test-winauth-conn",
            ServerName = "SRV01",
            AuthMode = AuthMode.WindowsAuth
        };

        var connectionString = Service().BuildConnectionString(server);
        using var conn = Service().CreateConnection(server);

        Assert.Contains("Integrated Security=True", connectionString);
        Assert.Null(conn.Credential);
    }

    [Fact]
    public void SqlAuthWithNoStoredPasswordGetsNoCredentialRatherThanAnEmptyOne()
    {
        // SqlCredential with an empty password would send a blank password rather than failing
        // clearly, so a missing one is left off entirely.
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-nopassword",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa"
        };

        using var conn = Service().CreateConnection(server);

        Assert.Null(conn.Credential);
    }

    // ── #17 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrustServerCertificateIsReflectedInTheConnectionString()
    {
        var trusting = new ServerConnection { ServerName = "SRV01", TrustServerCertificate = true };
        var validating = new ServerConnection { ServerName = "SRV01", TrustServerCertificate = false };

        Assert.Contains("Trust Server Certificate=True", Service().BuildConnectionString(trusting));
        Assert.Contains("Trust Server Certificate=False", Service().BuildConnectionString(validating));
    }

    [Fact]
    public async Task AServerThatAlreadyValidatesNeedsNoProbe()
    {
        var server = new ServerConnection
        {
            Name = "ninelives-test-validating",
            ServerName = "SRV01",
            TrustServerCertificate = false
        };

        Assert.True(await Service().WouldConnectWithCertificateValidationAsync(server));
    }

    [RequiresSqlFact]
    public async Task AgainstARealInstanceTheProbeGivesADefiniteAnswer()
    {
        var server = new ServerConnection
        {
            Name = "ninelives-test-probe",
            ServerName = Environment.GetEnvironmentVariable("NINELIVES_TEST_SQL")!,
            AuthMode = AuthMode.WindowsAuth,
            Encrypt = EncryptMode.Yes,
            TrustServerCertificate = true,
            ConnectionTimeoutSeconds = 15
        };

        // Either answer is fine - a local instance may or may not have a trusted certificate. What
        // matters is that the probe reaches a conclusion rather than returning null, which is what
        // the UI treats as "say nothing".
        var result = await Service().WouldConnectWithCertificateValidationAsync(server);

        Assert.NotNull(result);
    }
}
