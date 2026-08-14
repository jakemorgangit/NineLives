using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The fake credential store behaves like the real one where it counts (#439).
///
/// A fake that diverges from production does not merely fail to catch a bug - it makes that bug
/// invisible, which is worse than having no test at all. This one did exactly that:
/// <c>CredentialStore.LoadConfig</c> reads and deserializes on every call, so every caller gets
/// brand-new objects, while the fake handed back its cached instances.
///
/// What that hid: <c>BackupViewModel.Refresh</c> rebuilds its server list from the config and
/// reselects by id. Against the real store that is always a different instance, so the setter
/// raises a change and <c>OnServerChanged</c> runs - and once choosing a server started listing
/// its databases, every navigation to Backup or Copy silently opened a connection to a production
/// instance and wiped the user's ticks. Against identical instances the setter short-circuited
/// and nothing fired. The suite was green throughout.
///
/// These pin the fidelity itself, so the next person to simplify this fake finds out here rather
/// than in production.
/// </summary>
public class FakeStoreFidelityTests
{
    private static FakeCredentialStore Populated()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "sqlbackups",
            ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
        });
        return store;
    }

    [Fact]
    public void EveryLoadHandsBackFreshObjects()
    {
        var store = Populated();

        var first = store.LoadConfig();
        var second = store.LoadConfig();

        Assert.NotSame(first, second);
        Assert.NotSame(first.Servers[0], second.Servers[0]);
        Assert.NotSame(first.BlobContainers[0], second.BlobContainers[0]);

        // Different objects, same facts - a copy, not a blank.
        Assert.Equal(first.Servers[0].Id, second.Servers[0].Id);
        Assert.Equal("SRV01", second.Servers[0].ServerName);
        Assert.Equal("sqlbackups", second.BlobContainers[0].Name);
    }

    /// <summary>
    /// The identity question the hidden bug turned on: an object resolved out of a loaded config
    /// is never the one sitting in the store, so an ObservableProperty setter sees a change.
    /// </summary>
    [Fact]
    public void AServerResolvedFromALoadedConfigIsNotTheStoredInstance()
    {
        var store = Populated();
        var stored = store.Config.Servers[0];

        var resolved = store.LoadConfig().Servers.First(s => s.Id == stored.Id);

        Assert.NotSame(stored, resolved);
        Assert.Equal(stored.Id, resolved.Id);
    }

    [Fact]
    public void MutatingALoadedConfigDoesNotReachTheStoreUntilItIsSaved()
    {
        var store = Populated();

        var loaded = store.LoadConfig();
        loaded.Servers[0].Name = "renamed in a copy";

        Assert.Equal("SRV01", store.Config.Servers[0].Name);

        store.SaveConfig(loaded);
        Assert.Equal("renamed in a copy", store.Config.Servers[0].Name);
    }

    /// <summary>
    /// LoadError is JsonIgnore'd, but the real store SETS it on the object it hands back when the
    /// file existed and could not be read. So it has to survive the copy, or the fake can no
    /// longer play an unreadable config and the rule that one is never overwritten - the shape of
    /// the original config-loss defect - stops being testable.
    /// </summary>
    [Fact]
    public void AConfigThatCouldNotBeReadStillSaysSoThroughTheCopy()
    {
        var store = Populated();
        store.Config.LoadError = "config.json could not be read";

        Assert.Equal("config.json could not be read", store.LoadConfig().LoadError);
    }

    /// <summary>
    /// The in-memory secrets are JsonIgnore'd, and dropping them through the copy is FAITHFUL:
    /// the real store deserializes from disk, where they were never written. Pinned so nobody
    /// "fixes" the fake by carrying them across and quietly reintroduces a divergence.
    /// </summary>
    [Fact]
    public void InMemorySecretsDoNotSurviveALoadEitherJustAsTheyDoNotInProduction()
    {
        var store = Populated();
        store.Config.Servers[0].UnsavedPassword = "held in memory only";

        Assert.Null(store.LoadConfig().Servers[0].UnsavedPassword);
    }
}
