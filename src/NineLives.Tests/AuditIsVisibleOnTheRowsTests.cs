using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// An audit that passed says so where somebody is looking (#130).
///
/// Reported from a real run: 98 sets audited, all matched, and nothing on any row said so. The
/// pills existed only in the restore-chain panel, which is collapsed until somebody asks for it -
/// so the result of a three-and-a-half minute operation was invisible unless you knew to go and
/// open a panel and look.
///
/// Two things were wrong. The result was not on the grid somebody actually has open, and even where
/// it was, RestorePoint and BackupSet are plain models with no change notification - so a property
/// becoming true underneath a grid reaches no binding at all.
/// </summary>
public class AuditIsVisibleOnTheRowsTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupFileInfo File_(string name, BackupType type, DateTime at) => new()
    {
        BlobName = $"FULL/SRV01/MyDb/{name}",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/{name}",
        ETag = $"\"{name}\"",
        Type = type,
        InferredDatabaseName = "MyDb",
        InferredServerName = "SRV01",
        LastModified = new DateTimeOffset(at, TimeSpan.Zero)
    };

    private static RestoreViewModel Loaded()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = "https://acct.blob.core.windows.net/backups" });

        var blob = new FakeBlobStorageService
        {
            Files =
            [
                File_("MyDb_FULL_20260801_220000.bak", BackupType.Full, T0),
                File_("MyDb_LOG_20260801_230000.trn", BackupType.TransactionLog, T0.AddHours(1))
            ]
        };

        var vm = new RestoreViewModel(
            // A header per file: a fake that answers "Full" for a log would report a mismatch that
            // is an artefact of the fake rather than anything the audit found.
            blob, new FakeSqlServerService
            {
                HeaderForUrls = urls => HeaderFor(
                    urls.Any(u => u.EndsWith(".trn", StringComparison.Ordinal))
                        ? BackupType.TransactionLog
                        : BackupType.Full)
            },
            new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            TestLogs.Temp(), new FakeOperationHistoryStore(), TestAuditStores.Temp());

        vm.ConnectedServer = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
        vm.IsConnectedToServer = true;

        return vm;
    }

    private static BackupFileInfo HeaderFor(BackupType type) => new()
    {
        DatabaseName = "MyDb",
        Type = type,
        BackupTypeCode = type == BackupType.Full ? 1 : 2
    };

    // ── a set, and a point, know their own answer ───────────────────────────────

    [Fact]
    public void ASetWhoseFilesAllPassedReadsAsAudited()
    {
        var set = new BackupSet
        {
            SetId = "s1",
            Files =
            [
                new BackupFileInfo { AuditState = BackupAuditState.Passed },
                new BackupFileInfo { AuditState = BackupAuditState.Passed }
            ]
        };

        Assert.True(set.AuditPassed);
        Assert.Equal("✓ audited", set.AuditDisplay);
    }

    [Fact]
    public void ASetWithAnyMismatchReadsAsAMismatch()
    {
        var set = new BackupSet
        {
            SetId = "s1",
            Files =
            [
                new BackupFileInfo { AuditState = BackupAuditState.Passed },
                new BackupFileInfo { AuditState = BackupAuditState.Failed }
            ]
        };

        Assert.False(set.AuditPassed);
        Assert.Equal("✗ mismatch", set.AuditDisplay);
    }

    /// <summary>
    /// Blank until something has been checked. Not having asked is not a finding about the backup,
    /// and a column full of crosses would say the opposite.
    /// </summary>
    [Fact]
    public void AnUncheckedSetSaysNothingEitherWay()
    {
        var set = new BackupSet { SetId = "s1", Files = [new BackupFileInfo()] };

        Assert.Equal(string.Empty, set.AuditDisplay);
        Assert.False(set.AuditPassed);
        Assert.False(set.AuditFailed);
    }

    /// <summary>
    /// A restore point answers for its WHOLE chain, because that is the decision being made in the
    /// grid: this is the moment somebody is about to restore to, and what they want to know is
    /// whether every backup needed to get there has been confirmed.
    /// </summary>
    [Fact]
    public void ARestorePointIsOnlyAuditedWhenEverySetItNeedsIs()
    {
        var full = new BackupSet
        { SetId = "f", Files = [new BackupFileInfo { AuditState = BackupAuditState.Passed }] };

        var unchecked_ = new BackupSet { SetId = "l", Files = [new BackupFileInfo()] };

        var point = new RestorePoint
        {
            Timestamp = T0,
            Type = BackupType.TransactionLog,
            PrimarySet = unchecked_,
            RequiredFullSet = full,
            RequiredLogSets = [unchecked_]
        };

        Assert.False(point.AuditPassed);
        Assert.Equal(string.Empty, point.AuditDisplay);
    }

    [Fact]
    public void ARestorePointWhoseWholeChainPassedReadsAsAudited()
    {
        var full = new BackupSet
        { SetId = "f", Files = [new BackupFileInfo { AuditState = BackupAuditState.Passed }] };
        var log = new BackupSet
        { SetId = "l", Files = [new BackupFileInfo { AuditState = BackupAuditState.Passed }] };

        var point = new RestorePoint
        {
            Timestamp = T0,
            Type = BackupType.TransactionLog,
            PrimarySet = log,
            RequiredFullSet = full,
            RequiredLogSets = [log]
        };

        Assert.True(point.AuditPassed);
        Assert.Equal("✓ audited", point.AuditDisplay);
    }

    /// <summary>One bad set in the chain is a mismatch for the whole point, not a partial pass.</summary>
    [Fact]
    public void ARestorePointWithOneBadSetReadsAsAMismatch()
    {
        var full = new BackupSet
        { SetId = "f", Files = [new BackupFileInfo { AuditState = BackupAuditState.Passed }] };
        var log = new BackupSet
        { SetId = "l", Files = [new BackupFileInfo { AuditState = BackupAuditState.Failed }] };

        var point = new RestorePoint
        {
            Timestamp = T0,
            Type = BackupType.TransactionLog,
            PrimarySet = log,
            RequiredFullSet = full,
            RequiredLogSets = [log]
        };

        Assert.True(point.AuditFailed);
        Assert.Equal("✗ mismatch", point.AuditDisplay);
    }

    // ── and the grid is told to look again ──────────────────────────────────────

    /// <summary>
    /// The defect as reported. RestorePoint has no change notification, so an audit marking its
    /// sets underneath a grid reaches no binding - the rows have to be re-published.
    /// </summary>
    [Fact]
    public async Task AfterAnAuditTheRestorePointRowsAreRepublished()
    {
        var vm = Loaded();

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        var before = vm.Timeline.Points;
        Assert.NotEmpty(before);

        await vm.AuditDatabaseCommand.ExecuteAsync(null);

        Assert.NotSame(before, vm.Timeline.Points);
    }

    /// <summary>
    /// And the chosen restore point survives it. Losing it would move what is about to be restored,
    /// as a side effect of something that was only supposed to be checking.
    /// </summary>
    [Fact]
    public async Task TheChosenRestorePointSurvivesTheRefresh()
    {
        var vm = Loaded();

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        var chosen = vm.Timeline.SelectedPoint;

        await vm.AuditDatabaseCommand.ExecuteAsync(null);

        Assert.Same(chosen, vm.Timeline.SelectedPoint);
    }

    /// <summary>The rows say it too, not merely the summary line.</summary>
    [Fact]
    public async Task AfterAnAuditThePointsThemselvesReadAsAudited()
    {
        var vm = Loaded();

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.Inventory.SelectedServerName = "SRV01";
        vm.Inventory.SelectedDatabaseName = "MyDb";

        await vm.AuditDatabaseCommand.ExecuteAsync(null);

        Assert.All(vm.Timeline.Points, p => Assert.Equal("✓ audited", p.AuditDisplay));
    }
}
