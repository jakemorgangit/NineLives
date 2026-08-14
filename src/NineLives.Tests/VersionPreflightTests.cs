using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The one-directional law of RESTORE (#210): a backup taken on a newer major version can never be
/// restored onto an older one - error 3169, no exceptions, not even between adjacent releases.
/// Without a preflight it arrives from the server mid-restore, after WITH REPLACE has already
/// dropped the database being restored over.
/// </summary>
public class VersionPreflightTests
{
    // ── the law itself ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(17, 16, VersionVerdict.Refuse)]
    [InlineData(16, 15, VersionVerdict.Refuse)]
    [InlineData(16, 16, VersionVerdict.Same)]
    [InlineData(15, 16, VersionVerdict.UpgradeInPassing)]
    [InlineData(11, 16, VersionVerdict.UpgradeInPassing)]
    public void TheVerdictFollowsTheDirection(int backup, int target, VersionVerdict expected)
    {
        Assert.Equal(expected, VersionCompatibility.Check(backup, target));
    }

    /// <summary>
    /// No verdict from silence. A header that did not say, or an edition that does not report, is
    /// not evidence of a mismatch - refusing on a guess would block legal restores.
    /// </summary>
    [Theory]
    [InlineData(null, 16)]
    [InlineData(17, null)]
    [InlineData(null, null)]
    public void SilenceEarnsNoVerdict(int? backup, int? target)
    {
        Assert.Equal(VersionVerdict.Unknown, VersionCompatibility.Check(backup, target));
    }

    /// <summary>Named in years people recognise, with the way out stated.</summary>
    [Fact]
    public void TheRefusalNamesBothSidesAndTheWayOut()
    {
        var text = VersionCompatibility.ExplainRefusal(17, 16, "SRV01");

        Assert.Contains("SQL Server 2025 (17.x)", text);
        Assert.Contains("SQL Server 2022 (16.x)", text);
        Assert.Contains("SRV01", text);
        Assert.Contains("error 3169", text);
        Assert.Contains("Nothing has been changed", text);
    }

    [Fact]
    public void TheUpgradeNoteWarnsAboutTheOneWayDoor()
    {
        var text = VersionCompatibility.ExplainUpgrade(15, 16);

        Assert.Contains("SQL Server 2019 (15.x)", text);
        Assert.Contains("never go back", text);
        Assert.Contains("compatibility level", text);
    }

    // ── the preflight on the restore screen ─────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    /// <summary>A restore screen with a loaded ad-hoc chain whose header reports a version.</summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> LoadedAsync(int backupMajor)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());

        var sql = new FakeSqlServerService
        {
            FileHeaders =
            {
                [@"D:\drop\VendorDb.bak"] =
                [
                    new BackupHistoryEntry
                    {
                        DatabaseName = "VendorDb",
                        Type = BackupType.Full,
                        StartedAt = T0,
                        FinishedAt = T0.AddMinutes(1),
                        CheckpointLsn = 100m,
                        Position = 1,
                        Files = [@"D:\drop\VendorDb.bak"]
                    }
                ]
            },
            // Header, not HeaderForUrls: the preflight goes through RestoreHeaderOnlyMultiAsync,
            // and the fake routes that through the single Header property.
            Header = new BackupFileInfo
            {
                DatabaseName = "VendorDb",
                Type = BackupType.Full,
                BackupTypeCode = 1,
                SoftwareVersionMajor = backupMajor
            }
        };

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            new FakeOperationHistoryStore(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };
        vm.RefreshContainers();

        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = @"D:\drop\VendorDb.bak";
        await vm.Inventory.LoadAsync(vm.CurrentLocation!);
        vm.Inventory.SelectedDatabaseName = "VendorDb";

        // The preflight reads the CHAIN's full set, and a chain exists once a restore point is
        // chosen - which is the only state Execute can run from anyway.
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();
        Assert.NotNull(vm.RestoreChain);

        return (vm, sql);
    }

    /// <summary>
    /// The one this exists for: a 2025 backup aimed at a 2022 server refuses BEFORE anything runs,
    /// naming both versions.
    /// </summary>
    [Fact]
    public async Task ANewerBackupOntoAnOlderTargetRefusesByName()
    {
        var (vm, sql) = await LoadedAsync(backupMajor: 17);
        sql.ProductMajorVersion = 16;

        var log = new List<string>();
        var result = await vm.PreflightAsync(Server(), log.Add);

        Assert.False(result.CanProceed);
        Assert.Contains("SQL Server 2025 (17.x)", result.Refusal);
        Assert.Contains("SQL Server 2022 (16.x)", result.Refusal);
    }

    /// <summary>The legal direction proceeds, with the one-way door said out loud.</summary>
    [Fact]
    public async Task AnOlderBackupOntoANewerTargetProceedsWithANote()
    {
        var (vm, sql) = await LoadedAsync(backupMajor: 15);
        sql.ProductMajorVersion = 16;

        var log = new List<string>();
        var result = await vm.PreflightAsync(Server(), log.Add);

        Assert.True(result.CanProceed);
        Assert.Contains(log, l => l.Contains("never go back"));
    }

    [Fact]
    public async Task MatchingVersionsProceedQuietly()
    {
        var (vm, sql) = await LoadedAsync(backupMajor: 16);
        sql.ProductMajorVersion = 16;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.True(result.CanProceed);
    }

    /// <summary>
    /// A version check that cannot run does not block the restore - the restore reads the same
    /// header in a moment and reports its own, better error if something is genuinely wrong.
    /// </summary>
    [Fact]
    public async Task ACheckThatCannotRunDoesNotBlock()
    {
        var (vm, sql) = await LoadedAsync(backupMajor: 17);
        sql.ProductMajorVersion = null;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.True(result.CanProceed);
    }
}
