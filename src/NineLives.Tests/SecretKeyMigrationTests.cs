using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Secrets keyed by stable id rather than display name (#8).
///
/// The credential key used to be derived from Name. Renaming a container or server therefore
/// pointed every lookup at a key that did not exist, and the working token stayed under the old
/// name with no UI to recover it - which bites hardest on containers, because the edit form
/// deliberately never shows a stored SAS and so actively invites you to leave the field blank.
///
/// These write to the real Windows Credential Manager, so every key is namespaced under
/// "ninelives-test-" plus a fresh Guid and removed in Dispose. Config goes to a temp directory.
/// </summary>
public class SecretKeyMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-migration-tests", Guid.NewGuid().ToString("n"));

    private readonly string _prefix = "ninelives-test-" + Guid.NewGuid().ToString("n")[..8];
    private readonly List<string> _writtenKeys = [];

    private string ConfigPath => Path.Combine(_dir, "config.json");
    private CredentialStore Store() => new(_dir);

    public SecretKeyMigrationTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>Writes a secret under a legacy name-derived key, as a pre-#8 config would have.</summary>
    private void GiveLegacySecret(string key, string username, string secret)
    {
        Store().SaveSecret(key, username, secret);
        _writtenKeys.Add(key);
    }

    private BlobContainerConfig LegacyContainer(string name) => new()
    {
        Name = $"{_prefix}-{name}",
        ContainerUrl = $"https://acct.blob.core.windows.net/{name}"
        // No Id: this is what an entry written before #8 looks like.
    };

    private ServerConnection LegacyServer(string name) => new()
    {
        Name = $"{_prefix}-{name}",
        ServerName = "SRV01",
        AuthMode = AuthMode.SqlAuth,
        Username = "sa"
    };

    private void TrackNewKey(string key) => _writtenKeys.Add(key);

    // ── migration ───────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyContainer_GetsAnId_AndItsTokenMovesToTheNewKey()
    {
        var container = LegacyContainer("prod");
        GiveLegacySecret(container.LegacyCredentialKey, "acct", "sv=2026&sig=the-real-token");

        var config = new AppConfig { BlobContainers = { container } };
        var result = new ConfigMigrator(Store()).Migrate(config);
        TrackNewKey(container.CredentialKey);

        Assert.Null(result.Error);
        Assert.Equal(1, result.ContainersMigrated);
        Assert.False(string.IsNullOrEmpty(container.Id));
        Assert.Equal("sv=2026&sig=the-real-token", Store().GetSasToken(container));
    }

    [Fact]
    public void LegacyServer_GetsAnId_AndItsPasswordMovesToTheNewKey()
    {
        var server = LegacyServer("sql01");
        GiveLegacySecret(server.LegacyCredentialKey, "sa", "correct horse battery staple");

        var config = new AppConfig { Servers = { server } };
        var result = new ConfigMigrator(Store()).Migrate(config);
        TrackNewKey(server.CredentialKey);

        Assert.Null(result.Error);
        Assert.Equal(1, result.ServersMigrated);
        Assert.Equal("correct horse battery staple", Store().GetSqlPassword(server));
    }

    [Fact]
    public void Migration_RemovesTheOldKey()
    {
        var container = LegacyContainer("prod");
        var legacyKey = container.LegacyCredentialKey;
        GiveLegacySecret(legacyKey, "acct", "token");

        new ConfigMigrator(Store()).Migrate(new AppConfig { BlobContainers = { container } });
        TrackNewKey(container.CredentialKey);

        Assert.Null(Store().ReadSecret(legacyKey).secret);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var container = LegacyContainer("prod");
        GiveLegacySecret(container.LegacyCredentialKey, "acct", "token");
        var config = new AppConfig { BlobContainers = { container } };

        new ConfigMigrator(Store()).Migrate(config);
        TrackNewKey(container.CredentialKey);
        var idAfterFirst = container.Id;

        var second = new ConfigMigrator(Store()).Migrate(config);

        Assert.Equal(0, second.ContainersMigrated);
        Assert.Equal(idAfterFirst, container.Id);
        Assert.Equal("token", Store().GetSasToken(container));
    }

    [Fact]
    public void LegacyEntryWithNoStoredSecret_StillGetsAnId()
    {
        var container = LegacyContainer("no-token-yet");

        new ConfigMigrator(Store()).Migrate(new AppConfig { BlobContainers = { container } });

        Assert.False(string.IsNullOrEmpty(container.Id));
    }

    // ── the actual bug ──────────────────────────────────────────────────────────

    /// <summary>
    /// #8 itself. Rename a container and the token must still be found. Before the fix the key
    /// moved with the name and the lookup came back empty.
    /// </summary>
    [Fact]
    public void RenamingAContainer_KeepsItsToken()
    {
        var container = LegacyContainer("prod");
        GiveLegacySecret(container.LegacyCredentialKey, "acct", "sv=2026&sig=still-here");
        new ConfigMigrator(Store()).Migrate(new AppConfig { BlobContainers = { container } });
        TrackNewKey(container.CredentialKey);

        container.Name = $"{_prefix}-prod-backups";

        Assert.Equal("sv=2026&sig=still-here", Store().GetSasToken(container));
    }

    [Fact]
    public void RenamingAServer_KeepsItsPassword()
    {
        var server = LegacyServer("sql01");
        GiveLegacySecret(server.LegacyCredentialKey, "sa", "hunter2");
        new ConfigMigrator(Store()).Migrate(new AppConfig { Servers = { server } });
        TrackNewKey(server.CredentialKey);

        server.Name = $"{_prefix}-sql01-renamed";

        Assert.Equal("hunter2", Store().GetSqlPassword(server));
    }

    [Fact]
    public void TwoEntriesSharingAName_DoNotShareASecret()
    {
        // The other half of keying on a display name: two entries called the same thing collided
        // on one credential key, so whichever saved last silently owned both.
        var first = new BlobContainerConfig { Id = BlobContainerConfig.NewId(), Name = $"{_prefix}-dup" };
        var second = new BlobContainerConfig { Id = BlobContainerConfig.NewId(), Name = $"{_prefix}-dup" };

        Store().SaveSasToken(first, "token-one");
        Store().SaveSasToken(second, "token-two");
        TrackNewKey(first.CredentialKey);
        TrackNewKey(second.CredentialKey);

        Assert.Equal("token-one", Store().GetSasToken(first));
        Assert.Equal("token-two", Store().GetSasToken(second));
    }

    // ── failure handling ────────────────────────────────────────────────────────

    /// <summary>
    /// If the config write fails, the migration must not be half-applied: the old key has to
    /// survive, because it is still the only thing the on-disk config points at.
    /// </summary>
    [Fact]
    public void WhenTheConfigCannotBeSaved_TheOldKeySurvives_AndNoIdIsKept()
    {
        var container = LegacyContainer("prod");
        var legacyKey = container.LegacyCredentialKey;
        GiveLegacySecret(legacyKey, "acct", "token");

        var config = new AppConfig { BlobContainers = { container } };
        Store().SaveConfig(new AppConfig());

        ConfigMigrator.Result result;
        using (var _ = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = new ConfigMigrator(Store()).Migrate(config);
        }

        Assert.NotNull(result.Error);
        Assert.True(string.IsNullOrEmpty(container.Id));
        Assert.Equal("token", Store().ReadSecret(legacyKey).secret);
    }

    [Fact]
    public void AConfigThatFailedToLoad_IsNotMigrated()
    {
        // It holds empty defaults rather than anything real, and saving it would be refused.
        var config = new AppConfig { LoadError = "locked" };

        var result = new ConfigMigrator(Store()).Migrate(config);

        Assert.Null(result.Error);
        Assert.False(result.DidWork);
    }

}
