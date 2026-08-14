using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The S3 capability gate on the APP's restore path (#51).
///
/// It shipped in the CLI's preflights alone, which left the README promising - under "Safety
/// nets", the section whose whole claim is that these fire before WITH REPLACE drops anything -
/// a check that only the other front end performed. The app is what most people restore with.
///
/// The engine either has the S3 connector or it does not, so this is a capability rather than
/// evidence: there is no override, and no amount of insistence puts a connector into SQL 2019.
/// </summary>
public class S3GuiPreflightTests
{
    private static readonly DateTime T0 = new(2026, 8, 11, 21, 0, 0);

    private const string S3Device =
        "s3://s3.eu-west-2.amazonaws.com/backups/FULL/VendorDb_FULL_20260811_210000.bak";

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    /// <summary>
    /// A loaded chain whose one device is an s3:// URL. Reached through the ad-hoc source, which
    /// is the shortest route to a chain with a device this test controls exactly - and it also
    /// pins the case that matters most for the check's shape: a device recorded as a PATH which
    /// happens to be an s3:// URL, which is how an instance's own history reports one.
    /// </summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql)> LoadedAsync()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());

        var sql = new FakeSqlServerService
        {
            FileHeaders =
            {
                [S3Device] =
                [
                    new BackupHistoryEntry
                    {
                        DatabaseName = "VendorDb",
                        Type = BackupType.Full,
                        StartedAt = T0,
                        FinishedAt = T0.AddMinutes(1),
                        CheckpointLsn = 100m,
                        Position = 1,
                        Files = [S3Device]
                    }
                ]
            },
            Header = new BackupFileInfo
            {
                DatabaseName = "VendorDb",
                Type = BackupType.Full,
                BackupTypeCode = 1,
                SoftwareVersionMajor = 16
            }
        };

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, new FakeOperationHistoryStore(),
            TestLogs.Temp(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };
        vm.RefreshContainers();

        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = S3Device;
        await vm.Inventory.LoadAsync(vm.CurrentLocation!);
        vm.Inventory.SelectedDatabaseName = "VendorDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        Assert.NotNull(vm.RestoreChain);
        return (vm, sql);
    }

    [Fact]
    public async Task Sql2019RefusesAnS3RestoreInTheAppToo()
    {
        var (vm, sql) = await LoadedAsync();
        sql.ProductMajorVersion = 15;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.False(result.CanProceed);
        Assert.Contains("S3-compatible storage", result.Refusal);
        Assert.Contains("SQL Server 2022", result.Refusal);
    }

    [Fact]
    public async Task ExpressRefusesAnS3RestoreEvenOn2022()
    {
        var (vm, sql) = await LoadedAsync();
        sql.ProductMajorVersion = 16;
        sql.EngineEdition = 4;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.False(result.CanProceed);
        Assert.Contains("Express", result.Refusal);
    }

    [Fact]
    public async Task A2022StandardInstanceProceeds()
    {
        var (vm, sql) = await LoadedAsync();
        sql.ProductMajorVersion = 16;
        sql.EngineEdition = 3;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.True(result.CanProceed);
    }

    /// <summary>
    /// No verdict from silence, the same rule every other preflight here follows: an instance
    /// that will not report its version is not evidence of an incapable one.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatWillNotSayIsNotRefused()
    {
        var (vm, sql) = await LoadedAsync();
        sql.ProductMajorVersion = null;
        sql.EngineEdition = null;

        var result = await vm.PreflightAsync(Server(), _ => { });

        Assert.True(result.CanProceed);
    }

    /// <summary>An Azure chain is not asked the question at all.</summary>
    [Fact]
    public void AnAzureChainDoesNotTriggerTheGate()
    {
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "1",
                Type = BackupType.Full,
                Files =
                [
                    new BackupFileInfo
                    {
                        BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/a.bak"
                    }
                ]
            }
        };

        Assert.False(S3CapabilityPreflight.UsesS3(chain));
    }
}
