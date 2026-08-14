using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Zero persistence (#302): definitions from environment variables, consulted before config,
/// written nowhere - for the locked-down service account that must not own vault state and
/// the CI agent where nothing may outlive the job. Secrets ride on the objects
/// (UnsavedPassword, UnsavedSasToken), which every service already prefers over the vault.
/// </summary>
public class CliEphemeralTests
{
    private static Func<string, string?> Env(params (string key, string value)[] vars) =>
        key => vars.FirstOrDefault(v => v.key == key).value;

    // ── the definitions parse ───────────────────────────────────────────────────

    [Fact]
    public void AServerDefinitionCarriesItsSecretInMemoryOnly()
    {
        var server = EnvironmentCredentials.Server(Env(
            ("NINELIVES_SERVER", "10.0.0.4,1433"),
            ("NINELIVES_SQL_USER", "restore_svc"),
            ("NINELIVES_SQL_PASSWORD", "s3cret")));

        Assert.NotNull(server);
        Assert.Equal("env", server!.Name);
        Assert.Equal("10.0.0.4,1433", server.ServerName);
        Assert.Equal(AuthMode.SqlAuth, server.AuthMode);
        Assert.Equal("restore_svc", server.Username);
        Assert.Equal("s3cret", server.UnsavedPassword);
    }

    [Fact]
    public void WithoutAUserTheServerIsWindowsAuth()
    {
        var server = EnvironmentCredentials.Server(Env(("NINELIVES_SERVER", "SRV01")));

        Assert.Equal(AuthMode.WindowsAuth, server!.AuthMode);
        Assert.Null(server.Username);
    }

    [Fact]
    public void AContainerDefinitionCarriesItsSasInMemoryOnly()
    {
        var container = EnvironmentCredentials.Container(Env(
            ("NINELIVES_CONTAINER_URL", "https://acct.blob.core.windows.net/backups"),
            ("NINELIVES_SAS", "sv=2024&sig=abc")));

        Assert.NotNull(container);
        Assert.Equal("env", container!.Name);
        Assert.Equal("sv=2024&sig=abc", container.UnsavedSasToken);
    }

    [Fact]
    public void NothingSetMeansNothingDefined()
    {
        Assert.Null(EnvironmentCredentials.Server(Env()));
        Assert.Null(EnvironmentCredentials.Container(Env()));
    }

    // ── the finders consult ephemerals first, config second ─────────────────────

    private static CliServices Services(
        ServerConnection? envServer = null, BlobContainerConfig? envContainer = null,
        FakeCredentialStore? store = null)
    {
        store ??= new FakeCredentialStore();
        return new CliServices(
            store, new FakeSqlServerService(), new FakeBlobStorageService(),
            new FakeOperationHistoryStore(), new FakeRunNotifier(), envServer, envContainer);
    }

    [Fact]
    public void TheEphemeralNameOutranksTheProfileAndConfigStaysSecond()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = "cfg", Name = "SRV01", ServerName = "SRV01.corp" });

        var envServer = new ServerConnection { Id = "env-server", Name = "env", ServerName = "10.0.0.4" };
        var services = Services(envServer, store: store);

        var (fromEnv, _) = services.FindServer("env");
        Assert.Same(envServer, fromEnv);

        var (fromConfig, _) = services.FindServer("SRV01");
        Assert.Equal("cfg", fromConfig!.Id);
    }

    [Fact]
    public void AMissLisTsTheEphemeralAmongTheChoices()
    {
        var services = Services(
            new ServerConnection { Id = "env-server", Name = "env", ServerName = "10.0.0.4" });

        var (server, error) = services.FindServer("nope");

        Assert.Null(server);
        Assert.Contains("env (ephemeral)", error);
    }

    // ── the whole loop: a verb runs against definitions that exist nowhere ──────

    [Fact]
    public async Task ABackupRunsAgainstAnEstateDefinedOnlyInTheEnvironment()
    {
        var envServer = new ServerConnection
        {
            Id = "env-server", Name = "env", ServerName = "10.0.0.4",
            AuthMode = AuthMode.SqlAuth, Username = "svc", UnsavedPassword = "pw"
        };
        var envContainer = new BlobContainerConfig
        {
            Id = "env-container", Name = "env",
            ContainerUrl = "https://acct.blob.core.windows.net/backups",
            UnsavedSasToken = "sv=2024&sig=abc"
        };
        var store = new FakeCredentialStore();   // deliberately EMPTY: no profile config at all
        var sql = new FakeSqlServerService();
        var history = new FakeOperationHistoryStore();
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), history, new FakeRunNotifier(),
            envServer, envContainer);

        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await BackupVerb.RunAsync(
            CliArguments.Parse(
                ["--container", "env", "--server", "env", "--database", "Sales", "--execute"],
                BackupVerb.Spec),
            services, output, errors);

        Assert.Equal(0, exit);
        Assert.Contains(sql.ExecutedScripts, s => s.Contains("BACKUP DATABASE [Sales]"));
        Assert.Same(envServer, sql.ExecutedAgainst[^1]);      // the in-memory password travels
        Assert.Single(history.Entries);                        // the receipt still writes
        Assert.Equal(0, store.SaveConfigCalls);                // and nothing was persisted
    }
}
