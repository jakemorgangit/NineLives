using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A viewmodel writes to the log it was GIVEN.
///
/// Found in a real log file:
///
///     2026-08-07 19:23:19 INFO  [headeronly] fake: 1 statement(s)
///
/// That line came from <see cref="FakeSqlServerService"/> - a test run, appending to the log in the
/// profile of whoever ran it, because the inventory reached for App.Log directly instead of taking
/// one. Harmless here, and the same class of side effect #41 was about: a test that touches the
/// user's real machine can also delete from it.
///
/// So this asserts the wiring rather than the absence, which is the part that can be checked: give
/// a viewmodel a log and its output lands in that file and no other.
/// </summary>
public class LogGoesWhereItIsToldTests
{
    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    private static BackupLocation Container() => BackupLocation.Blob(new BlobContainerConfig
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    });

    /// <summary>
    /// The header timing is the line that escaped, so it is the one worth pinning: it has to reach
    /// the log the inventory was constructed with.
    /// </summary>
    [Fact]
    public async Task TheHeaderTimingIsWrittenToTheLogTheInventoryWasGiven()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n"));
        var log = new OperationLog(directory);

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "mystery.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/mystery.bak",
                    Type = BackupType.Unknown
                }
            ]
        };

        var vm = new BackupInventoryViewModel(blob, new FakeSqlServerService(), log, TestAuditStores.Temp());

        await vm.LoadAsync(Container());
        await vm.IdentifyUnclassifiedAsync(Server());

        var written = Directory.Exists(directory)
            ? string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText))
            : string.Empty;

        Assert.Contains("[headeronly]", written);
    }

    /// <summary>
    /// And the Restore screen hands its own log down rather than letting the inventory find one -
    /// otherwise the injection point exists and is simply not used, which is where this started.
    /// </summary>
    [Fact]
    public async Task TheRestoreScreenGivesItsOwnLogToTheInventory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n"));
        var log = new OperationLog(directory);

        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = "https://acct.blob.core.windows.net/backups" });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "mystery.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/mystery.bak",
                    Type = BackupType.Unknown
                }
            ]
        };

        var vm = new RestoreViewModel(
            blob, new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, log, new FakeRestoreHistoryStore());

        vm.RefreshContainers();
        await vm.LoadBackupsCommand.ExecuteAsync(null);
        await vm.Inventory.IdentifyUnclassifiedAsync(Server());

        var written = Directory.Exists(directory)
            ? string.Join("\n", Directory.GetFiles(directory).Select(File.ReadAllText))
            : string.Empty;

        Assert.Contains("[headeronly]", written);
    }
}
