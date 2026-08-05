using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Config load/save behaviour (#7).
///
/// LoadConfig used to catch everything and return empty defaults, and SaveConfig used to catch
/// everything and return silently while the UI reported success. Together those turned a
/// momentary file lock - antivirus, a backup agent, a sync client mid-upload - into permanent
/// data loss: the app came up showing nothing, the user re-added one container, and the save
/// wrote that single entry over every server and container they had.
///
/// These use a temp directory through the internal constructor, so they never touch the real
/// %LOCALAPPDATA%\NineLives\config.json.
/// </summary>
public class ConfigPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-config-tests", Guid.NewGuid().ToString("n"));

    private string ConfigPath => Path.Combine(_dir, "config.json");
    private CredentialStore Store() => new(_dir);

    public ConfigPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private static AppConfig ConfigWith(params string[] containerNames)
    {
        var config = new AppConfig();
        foreach (var name in containerNames)
        {
            config.BlobContainers.Add(new BlobContainerConfig
            {
                Name = name,
                ContainerUrl = $"https://acct.blob.core.windows.net/{name}"
            });
        }
        return config;
    }

    // ── the distinction that matters ────────────────────────────────────────────

    [Fact]
    public void MissingFile_IsAFreshInstall_NotAnError()
    {
        var config = Store().LoadConfig();

        Assert.Null(config.LoadError);
        Assert.Empty(config.BlobContainers);
    }

    [Fact]
    public void UnreadableFile_ReportsLoadError()
    {
        Store().SaveConfig(ConfigWith("prod", "uat", "dev"));

        // Hold the file open exclusively, the way a scanner or sync client briefly does.
        using var _ = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var config = Store().LoadConfig();

        Assert.NotNull(config.LoadError);
        Assert.Empty(config.BlobContainers);
    }

    [Fact]
    public void CorruptFile_ReportsLoadError()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var config = Store().LoadConfig();

        Assert.NotNull(config.LoadError);
    }

    // ── the data-loss regression ────────────────────────────────────────────────

    /// <summary>
    /// The whole of #7 in one test: the file is briefly locked, the app comes up empty, the user
    /// re-adds a container and saves. Before the fix that wrote one container over three. The
    /// save must now refuse, and the file on disk must be untouched.
    /// </summary>
    [Fact]
    public void SavingAConfigThatFailedToLoad_IsRefused_AndLeavesTheFileIntact()
    {
        Store().SaveConfig(ConfigWith("prod", "uat", "dev"));

        AppConfig loaded;
        using (var _ = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = Store().LoadConfig();
            Assert.NotNull(loaded.LoadError);
        }

        // The user, seeing an empty list, adds a container back.
        loaded.BlobContainers.Add(new BlobContainerConfig { Name = "prod", ContainerUrl = "https://acct/prod" });

        Assert.Throws<ConfigSaveRefusedException>(() => Store().SaveConfig(loaded));

        var onDisk = Store().LoadConfig();
        Assert.Null(onDisk.LoadError);
        Assert.Equal(new[] { "prod", "uat", "dev" }, onDisk.BlobContainers.Select(c => c.Name));
    }

    [Fact]
    public void SaveFailure_Propagates_RatherThanReportingSuccess()
    {
        Store().SaveConfig(ConfigWith("prod"));
        var config = Store().LoadConfig();

        // Something else holds the target open for writing; the swap cannot happen.
        using var _ = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => Store().SaveConfig(config));
    }

    // ── ordinary behaviour that must keep working ───────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        Store().SaveConfig(ConfigWith("prod", "uat"));

        var config = Store().LoadConfig();

        Assert.Null(config.LoadError);
        Assert.Equal(new[] { "prod", "uat" }, config.BlobContainers.Select(c => c.Name));
    }

    [Fact]
    public void LoadError_IsNotWrittenToTheFile()
    {
        // It describes one load attempt, not the configuration. Persisting it would make a
        // transient failure permanent, and every later save would be refused.
        Store().SaveConfig(ConfigWith("prod"));

        Assert.DoesNotContain("LoadError", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void OverwritingAnExistingConfig_KeepsThePreviousContentsAsBak()
    {
        Store().SaveConfig(ConfigWith("prod", "uat"));
        Store().SaveConfig(ConfigWith("prod"));

        var backup = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath + ".bak"))!;
        Assert.Equal(new[] { "prod", "uat" }, backup.BlobContainers.Select(c => c.Name));
    }

    [Fact]
    public void SaveLeavesNoTempFileBehind()
    {
        Store().SaveConfig(ConfigWith("prod"));

        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void SaveCreatesTheDirectoryWhenItIsMissing()
    {
        Directory.Delete(_dir, recursive: true);

        Store().SaveConfig(ConfigWith("prod"));

        Assert.Single(Store().LoadConfig().BlobContainers);
    }
}
