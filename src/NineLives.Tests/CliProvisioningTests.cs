using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Configuration from nothing (#63): a Terraform-built VM has no app config, so add-server
/// and add-container create it - validated by default, converging on re-run, secrets in the
/// vault and never echoed. These pins are the provisioning template's contract: three lines
/// chained on exit codes, with the restore's moment as the only variable.
/// </summary>
public class CliProvisioningTests
{
    private static (CliServices services, FakeCredentialStore store, FakeSqlServerService sql,
        FakeBlobStorageService blobs) Stage()
    {
        var store = new FakeCredentialStore();
        var sql = new FakeSqlServerService();
        var blobs = new FakeBlobStorageService();
        return (new CliServices(store, sql, blobs,
            new FakeOperationHistoryStore(), new FakeRunNotifier()), store, sql, blobs);
    }

    private static async Task<(int exit, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors);
        return (exit, errors.ToString());
    }

    // ── add-server ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddServerSavesValidatesAndReportsTheVersion()
    {
        var (services, store, sql, _) = Stage();
        sql.MajorVersionByServer["localhost"] = 16;

        var (exit, errors) = await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
            ["--name", "target", "--address", "localhost",
             "--user", "svc_restore", "--password", "pw1"], services);

        Assert.Equal(0, exit);
        var server = Assert.Single(store.LoadConfig().Servers);
        Assert.Equal("target", server.Name);
        Assert.Equal("localhost", server.ServerName);
        Assert.Equal(AuthMode.SqlAuth, server.AuthMode);
        Assert.Equal("svc_restore", server.Username);
        Assert.Equal("pw1", store.GetSqlPassword(server));
        Assert.Contains("2022", errors);
        // The secret goes to the vault, never back out the terminal.
        Assert.DoesNotContain("pw1", errors);
    }

    /// <summary>A provisioning script that runs twice must converge, not error or duplicate.</summary>
    [Fact]
    public async Task AddServerUpdatesAnExistingEntryInPlace()
    {
        var (services, store, sql, _) = Stage();
        sql.MajorVersionByServer["srv-a"] = 16;
        sql.MajorVersionByServer["srv-b"] = 16;

        await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
            ["--name", "target", "--address", "srv-a"], services);
        var (exit, errors) = await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
            ["--name", "target", "--address", "srv-b"], services);

        Assert.Equal(0, exit);
        Assert.Contains("Updated", errors);
        var server = Assert.Single(store.LoadConfig().Servers);
        Assert.Equal("srv-b", server.ServerName);
    }

    /// <summary>
    /// The fail-fast the template depends on: a dead server exits 3 at the ADD, so the chain
    /// stops before the restore trusts it. Saved anyway - the re-run overwrites.
    /// </summary>
    [Fact]
    public async Task AddServerThatDoesNotAnswerExitsThree()
    {
        var (services, store, sql, _) = Stage();
        sql.ThrowOnMajorVersion = new InvalidOperationException("network path not found");

        var (exit, errors) = await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
            ["--name", "target", "--address", "gone"], services);

        Assert.Equal(3, exit);
        Assert.Contains("VALIDATION FAILED", errors);
        Assert.Single(store.LoadConfig().Servers);
    }

    [Fact]
    public async Task AddServerReadsThePasswordFromTheEnvironmentWhenNotGiven()
    {
        var (services, store, sql, _) = Stage();
        sql.MajorVersionByServer["localhost"] = 17;

        Environment.SetEnvironmentVariable("NINELIVES_SQL_PASSWORD", "from-env");
        try
        {
            var (exit, _) = await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
                ["--name", "target", "--address", "localhost", "--user", "svc"], services);

            Assert.Equal(0, exit);
            Assert.Equal("from-env",
                store.GetSqlPassword(store.LoadConfig().Servers.Single()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NINELIVES_SQL_PASSWORD", null);
        }
    }

    [Fact]
    public async Task AddServerWithAUserButNoPasswordAnywhereIsAUsageError()
    {
        var (services, _, _, _) = Stage();

        var (exit, errors) = await Run(AddServerVerb.RunAsync, AddServerVerb.Spec,
            ["--name", "target", "--address", "localhost", "--user", "svc"], services);

        Assert.Equal(64, exit);
        Assert.Contains("NINELIVES_SQL_PASSWORD", errors);
    }

    // ── add-container ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AddContainerSavesTheSasAndProvesItReaches()
    {
        var (services, store, _, blobs) = Stage();

        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "backups", "--url", "https://acct.blob.core.windows.net/sqlbackups/",
             "--sas", "?sv=2024&sig=secret123"], services);

        Assert.Equal(0, exit);
        var container = Assert.Single(store.LoadConfig().BlobContainers);
        Assert.Equal("backups", container.Name);
        // Normalised at the edge: no trailing slash on the URL, no leading ? on the token.
        Assert.Equal("https://acct.blob.core.windows.net/sqlbackups", container.ContainerUrl);
        Assert.Equal("sv=2024&sig=secret123", store.GetSasToken(container));
        Assert.Equal(BlobAuthMode.SasToken, container.AuthMode);
        // The verify call ran against the just-saved entry, and the secret never echoed.
        Assert.NotNull(blobs.LastConfig);
        Assert.Contains("Validated", errors);
        Assert.DoesNotContain("secret123", errors);
    }

    /// <summary>An s3:// endpoint is just another container (#51): the key pair is stored as
    /// the one string the engine wants, the region is kept, and validation runs the same way.</summary>
    [Fact]
    public async Task AddContainerAcceptsAnS3EndpointWithAKeyPairAndRegion()
    {
        var (services, store, _, _) = Stage();

        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "s3backups", "--url", "s3://s3.eu-west-2.amazonaws.com/sqlbackups",
             "--sas", "AKIAEXAMPLE:abc/def+secret", "--region", "eu-west-2"], services);

        Assert.Equal(0, exit);
        var container = Assert.Single(store.LoadConfig().BlobContainers);
        Assert.True(container.IsS3);
        Assert.Equal("eu-west-2", container.S3Region);
        // The pair is stored verbatim - it IS the credential secret the engine will use.
        Assert.Equal("AKIAEXAMPLE:abc/def+secret", store.GetSasToken(container));
        Assert.DoesNotContain("secret", errors);
    }

    /// <summary>A malformed key pair is refused before storage, not discovered at restore.</summary>
    [Fact]
    public async Task AddContainerRefusesAMalformedS3KeyPair()
    {
        var (services, store, _, _) = Stage();

        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "s3backups", "--url", "s3://s3.eu-west-2.amazonaws.com/sqlbackups",
             "--sas", "no-colon-here"], services);

        Assert.Equal(64, exit);
        Assert.Contains("AccessKeyId:SecretKey", errors);
        Assert.Empty(store.LoadConfig().BlobContainers);
    }

    /// <summary>The SAS that looks right but is not: recorded, refused, exit 3, chain stops.</summary>
    [Fact]
    public async Task AddContainerWithASasTheContainerRefusesExitsThree()
    {
        var (services, _, _, blobs) = Stage();
        blobs.VerifyAnswer = false;

        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "backups", "--url", "https://acct.blob.core.windows.net/sqlbackups",
             "--sas", "sv=expired"], services);

        Assert.Equal(3, exit);
        Assert.Contains("VALIDATION FAILED", errors);
        Assert.Contains("expiry", errors);
    }

    [Fact]
    public async Task AddContainerReadsTheSasFromTheEnvironmentWhenNotGiven()
    {
        var (services, store, _, _) = Stage();

        Environment.SetEnvironmentVariable("NINELIVES_SAS", "sv=env-token");
        try
        {
            var (exit, _) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
                ["--name", "backups",
                 "--url", "https://acct.blob.core.windows.net/sqlbackups"], services);

            Assert.Equal(0, exit);
            Assert.Equal("sv=env-token",
                store.GetSasToken(store.LoadConfig().BlobContainers.Single()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NINELIVES_SAS", null);
        }
    }

    [Fact]
    public async Task AddContainerWithNoSasAnywhereIsAUsageError()
    {
        var (services, _, _, _) = Stage();

        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "backups",
             "--url", "https://acct.blob.core.windows.net/sqlbackups"], services);

        Assert.Equal(64, exit);
        Assert.Contains("NINELIVES_SAS", errors);
    }

    /// <summary>A SAS refresh is the add verb again - same name, new token, updated in place.</summary>
    [Fact]
    public async Task AddContainerRefreshesTheTokenOnAnExistingEntry()
    {
        var (services, store, _, _) = Stage();
        var url = "https://acct.blob.core.windows.net/sqlbackups";

        await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "backups", "--url", url, "--sas", "sv=old"], services);
        var (exit, errors) = await Run(AddContainerVerb.RunAsync, AddContainerVerb.Spec,
            ["--name", "backups", "--url", url, "--sas", "sv=new"], services);

        Assert.Equal(0, exit);
        Assert.Contains("Updated", errors);
        var container = Assert.Single(store.LoadConfig().BlobContainers);
        Assert.Equal("sv=new", store.GetSasToken(container));
    }
}
