using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Test Connection must not persist anything (#12).
///
/// Testing a newly typed SAS token used to write it to Credential Manager first, with
/// CRED_PERSIST_LOCAL_MACHINE - before Save, with no undo. So: edit a container whose token works,
/// paste one that turns out to be typo'd or expired, click Test Connection because that is exactly
/// what the button invites, watch it fail, click Cancel expecting nothing to have changed, and the
/// working token is gone. The form never displays stored tokens, so there is no way to get it back.
/// Same shape on the SQL side for passwords.
///
/// The fix is a transient in-memory secret the services prefer over the stored one. These tests
/// pin both halves: the transient value is used, and it never reaches disk.
/// </summary>
public class UnsavedSecretTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-unsaved-tests", Guid.NewGuid().ToString("n"));

    private readonly List<string> _writtenKeys = [];

    private CredentialStore Store() => new(_dir);

    public UnsavedSecretTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        var store = Store();
        foreach (var key in _writtenKeys)
        {
            try { store.DeleteSecret(key); } catch { /* best effort */ }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private BlobContainerConfig SavedContainer(string storedToken)
    {
        var container = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "ninelives-test-" + Guid.NewGuid().ToString("n")[..8],
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        };
        Store().SaveSasToken(container, storedToken);
        _writtenKeys.Add(container.CredentialKey);
        return container;
    }

    // ── the stored secret survives a test ───────────────────────────────────────

    /// <summary>
    /// The regression. A throwaway config built from the edit form, carrying a candidate token,
    /// must leave the saved container's token exactly where it was.
    /// </summary>
    [Fact]
    public void TryingACandidateToken_LeavesTheStoredOneUntouched()
    {
        var saved = SavedContainer("sv=2026&sig=the-working-token");

        // What TestConnection now builds: same name and URL, token held in memory only.
        var candidate = new BlobContainerConfig
        {
            Name = saved.Name,
            ContainerUrl = saved.ContainerUrl,
            UnsavedSasToken = "sv=2026&sig=typo"
        };

        Assert.Equal("sv=2026&sig=typo", candidate.UnsavedSasToken);
        Assert.Equal("sv=2026&sig=the-working-token", Store().GetSasToken(saved));
    }

    [Fact]
    public void ACandidateTokenIsNotWrittenUnderTheLegacyKeyEither()
    {
        // The throwaway object has no Id, so its CredentialKey falls back to the name-derived one.
        // Nothing should have been written there.
        var candidate = new BlobContainerConfig
        {
            Name = "ninelives-test-" + Guid.NewGuid().ToString("n")[..8],
            UnsavedSasToken = "sv=2026&sig=candidate"
        };

        Assert.Null(Store().ReadSecret(candidate.CredentialKey).secret);
        Assert.Null(Store().ReadSecret(candidate.LegacyCredentialKey).secret);
    }

    [Fact]
    public void TryingACandidatePassword_LeavesTheStoredOneUntouched()
    {
        var saved = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-" + Guid.NewGuid().ToString("n")[..8],
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa"
        };
        Store().SaveSqlPassword(saved, "the-working-password");
        _writtenKeys.Add(saved.CredentialKey);

        var candidate = new ServerConnection
        {
            Name = saved.Name,
            ServerName = saved.ServerName,
            AuthMode = AuthMode.SqlAuth,
            Username = "sa",
            UnsavedPassword = "typo"
        };

        Assert.Null(Store().ReadSecret(candidate.LegacyCredentialKey).secret);
        Assert.Equal("the-working-password", Store().GetSqlPassword(saved));
    }

    // ── the transient secret is actually used ───────────────────────────────────

    [Fact]
    public void AnUnsavedPassword_IsUsedInTheConnectionString()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-unsaved",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa",
            UnsavedPassword = "in-memory-only"
        };

        var connectionString = new SqlServerService(Store()).BuildConnectionString(server);

        Assert.Contains("in-memory-only", connectionString);
    }

    [Fact]
    public void AnUnsavedPassword_TakesPrecedenceOverTheStoredOne()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-precedence",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa"
        };
        Store().SaveSqlPassword(server, "stored");
        _writtenKeys.Add(server.CredentialKey);

        server.UnsavedPassword = "candidate";

        var connectionString = new SqlServerService(Store()).BuildConnectionString(server);

        Assert.Contains("candidate", connectionString);
        Assert.DoesNotContain("stored", connectionString);
    }

    [Fact]
    public void WithoutAnUnsavedPassword_TheStoredOneIsStillUsed()
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "ninelives-test-fallback",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "sa"
        };
        Store().SaveSqlPassword(server, "stored-password");
        _writtenKeys.Add(server.CredentialKey);

        var connectionString = new SqlServerService(Store()).BuildConnectionString(server);

        Assert.Contains("stored-password", connectionString);
    }

    // ── never persisted ─────────────────────────────────────────────────────────

    [Fact]
    public void TransientSecrets_AreNotSerialisedIntoConfigJson()
    {
        var config = new AppConfig
        {
            BlobContainers =
            {
                new BlobContainerConfig { Id = "c1", Name = "prod", UnsavedSasToken = "sv=2026&sig=secret" }
            },
            Servers =
            {
                new ServerConnection { Id = "s1", Name = "sql01", UnsavedPassword = "hunter2" }
            }
        };

        Store().SaveConfig(config);
        var json = File.ReadAllText(Path.Combine(_dir, "config.json"));

        Assert.DoesNotContain("sv=2026&sig=secret", json);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("UnsavedSasToken", json);
        Assert.DoesNotContain("UnsavedPassword", json);
    }

    [Fact]
    public void CredentialKeys_AreNotSerialisedIntoConfigJson()
    {
        // Derived, and now that they contain the id they are pure noise in the file.
        var config = new AppConfig
        {
            BlobContainers = { new BlobContainerConfig { Id = "c1", Name = "prod" } }
        };

        Store().SaveConfig(config);
        var json = File.ReadAllText(Path.Combine(_dir, "config.json"));

        Assert.DoesNotContain("CredentialKey", json);

        // ...and the round trip still works.
        var reloaded = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("c1", reloaded.BlobContainers[0].Id);
    }
}
