using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Moving the configuration between machines without moving a single secret (#213).
///
/// The boundary IS the feature: the exported file holds shapes - containers, servers, settings -
/// and is safe on a share or in a ticket, because SAS tokens and SQL passwords live in Windows
/// Credential Manager and never leave the machine.
/// </summary>
public class ConfigPortabilityTests
{
    private static AppConfig Config()
    {
        var config = new AppConfig { Mode = AppMode.Pro };

        config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        config.Servers.Add(new ServerConnection
        {
            Id = "s1",
            Name = "SRV01",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "restore_svc",
            UnsavedPassword = "NEVER-THIS"
        });

        return config;
    }

    // ── what leaves the machine ─────────────────────────────────────────────────

    /// <summary>The one plain-sight secret: a SAS pasted into the container URL.</summary>
    [Fact]
    public void ASasPastedIntoTheUrlIsStrippedOnExport()
    {
        var config = Config();
        config.BlobContainers[0].ContainerUrl =
            "https://acct.blob.core.windows.net/backups?sv=2024&sig=SECRETSIGNATURE";

        var json = ConfigPortability.Export(config);

        Assert.DoesNotContain("SECRETSIGNATURE", json);
        Assert.Contains("https://acct.blob.core.windows.net/backups", json);
    }

    /// <summary>An unsaved password in memory at export time does not travel.</summary>
    [Fact]
    public void AnUnsavedPasswordNeverReachesTheFile()
    {
        var json = ConfigPortability.Export(Config());

        Assert.DoesNotContain("NEVER-THIS", json);
        Assert.Contains("restore_svc", json); // the username is a shape, not a secret
    }

    /// <summary>Local-machine state stays local - the file describes the estate, not this desk.</summary>
    [Fact]
    public void WindowGeometryAndLastScreenStayLocal()
    {
        var config = Config();
        config.Window = new WindowGeometry { Left = 100, Top = 100, Width = 1200, Height = 800 };
        config.LastScreen = "History";

        var json = ConfigPortability.Export(config);

        Assert.DoesNotContain("LastScreen", json);
        Assert.DoesNotContain("\"Window\"", json);
    }

    // ── the round trip ──────────────────────────────────────────────────────────

    [Fact]
    public void AnExportReadsBackWhole()
    {
        var read = ConfigPortability.Read(ConfigPortability.Export(Config()));

        Assert.NotNull(read);
        Assert.Single(read!.BlobContainers);
        Assert.Single(read.Servers);
        Assert.Equal("SRV01", read.Servers[0].Name);
        Assert.Equal(AppMode.Pro, read.Mode);
    }

    [Fact]
    public void GarbageIsRefusedNotThrown()
    {
        Assert.Null(ConfigPortability.Read("this is not json"));
    }

    // ── the merge ───────────────────────────────────────────────────────────────

    [Fact]
    public void NewEntriesAreAddedAndCountedAndCredentialNeedsNamed()
    {
        var local = new AppConfig { Mode = AppMode.Basic };
        var imported = ConfigPortability.Read(ConfigPortability.Export(Config()))!;

        var summary = ConfigPortability.Merge(local, imported);

        Assert.Equal(1, summary.ContainersAdded);
        Assert.Equal(1, summary.ServersAdded);
        Assert.Single(local.BlobContainers);
        Assert.Single(local.Servers);
        Assert.Contains(summary.NeedCredentials, n => n.Contains("backups"));
        Assert.Contains(summary.NeedCredentials, n => n.Contains("SRV01"));
        Assert.Contains("Credentials do not travel", summary.Describe());
    }

    [Fact]
    public void AMatchingIdUpdatesInPlace()
    {
        var local = Config();
        var incoming = Config();
        incoming.Servers[0].ServerName = "SRV01.internal.example";

        var summary = ConfigPortability.Merge(local, ConfigPortability.Read(ConfigPortability.Export(incoming))!);

        Assert.Equal(1, summary.ServersUpdated);
        Assert.Single(local.Servers);
        Assert.Equal("SRV01.internal.example", local.Servers[0].ServerName);
    }

    /// <summary>
    /// Never deletes: a stale file taken last month must not silently remove this month's
    /// containers. An import is additive or it is a trap.
    /// </summary>
    [Fact]
    public void WhatTheFileDoesNotMentionSurvives()
    {
        var local = Config();
        local.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c2",
            Name = "newer-container",
            ContainerUrl = "https://acct.blob.core.windows.net/newer"
        });

        var oldFile = ConfigPortability.Read(ConfigPortability.Export(Config()))!;
        ConfigPortability.Merge(local, oldFile);

        Assert.Contains(local.BlobContainers, c => c.Id == "c2");
    }

    /// <summary>A machine that chose its mode keeps it; one that never chose inherits the file's.</summary>
    [Fact]
    public void TheLocalModeIsNotOverwritten()
    {
        var chosen = new AppConfig { Mode = AppMode.Basic };
        ConfigPortability.Merge(chosen, ConfigPortability.Read(ConfigPortability.Export(Config()))!);
        Assert.Equal(AppMode.Basic, chosen.Mode);

        var fresh = new AppConfig();
        ConfigPortability.Merge(fresh, ConfigPortability.Read(ConfigPortability.Export(Config()))!);
        Assert.Equal(AppMode.Pro, fresh.Mode);
    }
}
