using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Restoring the logs from where they already are, rather than copying them in first (#451).
///
/// The remedy for mid-incident, when uploading twenty-three files is time nobody has. It works
/// because the script generator picks DISK or URL per FILE, from the device string rather than
/// from an option - so a chain holding a blob full and disk-resident logs generates correct T-SQL
/// with no further work. It has simply never been possible to BUILD one, because discovery only
/// ever looked at a single medium.
/// </summary>
public class RestoreFromWhereTheyAreTests
{
    private static readonly DateTime T0 = new(2026, 8, 14, 22, 0, 0);

    private static BackupSet FromContainer(BackupType type, DateTime at) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = at,
        DatabaseName = "Sales",
        ServerName = "SRV01",
        Files =
        [
            new BackupFileInfo
            {
                BlobName = $"Sales_{at:yyyyMMdd_HHmmss}.bak",
                BlobUrl = $"https://acct.blob.core.windows.net/c/Sales_{at:yyyyMMdd_HHmmss}.bak",
                Type = type,
                InferredDatabaseName = "Sales",
                InferredServerName = "SRV01"
            }
        ]
    };

    private static BackupSet FromDisk(BackupType type, DateTime at, string folder) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = at,
        DatabaseName = "Sales",
        ServerName = "SRV01",
        Files =
        [
            new BackupFileInfo
            {
                BlobName = $"Sales_{at:yyyyMMdd_HHmmss}.trn",
                LocalPath = $@"{folder}\Sales_{at:yyyyMMdd_HHmmss}.trn",
                Type = type,
                InferredDatabaseName = "Sales",
                InferredServerName = "SRV01"
            }
        ]
    };

    // ── the merge ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An inventory loaded from a container the ordinary way, so what these exercise is the real
    /// load path rather than a seam that only tests use.
    /// </summary>
    private static BackupInventoryViewModel Inventory()
    {
        var blob = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = $"FULL/SRV01/Sales/Sales_{T0:yyyyMMdd_HHmmss}.bak",
                    BlobUrl = $"https://acct.blob.core.windows.net/c/FULL/SRV01/Sales/Sales_{T0:yyyyMMdd_HHmmss}.bak",
                    Type = BackupType.Full,
                    InferredDatabaseName = "Sales",
                    InferredServerName = "SRV01",
                    SizeBytes = 1024,
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        var vm = new BackupInventoryViewModel(blob, new FakeSqlServerService(), TestLogs.Temp());

        vm.LoadAsync(BackupLocation.Blob(new BlobContainerConfig
        {
            Id = "c1",
            Name = "sqlbackups",
            ContainerUrl = "https://acct.blob.core.windows.net/c"
        })).GetAwaiter().GetResult();

        vm.SelectedDatabaseName = "Sales";
        return vm;
    }

    [Fact]
    public void SetsFromTheInstanceHistoryJoinTheWorkingSet()
    {
        var inv = Inventory();
        Assert.Single(inv.WorkingSet);
        Assert.False(inv.HasSetsFromInstanceHistory);

        inv.IncludeFromInstanceHistory([FromDisk(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs")]);

        Assert.Equal(2, inv.WorkingSet.Count);
        Assert.True(inv.HasSetsFromInstanceHistory);
    }

    /// <summary>
    /// Adding the same location twice - pressing the button again - must not double the timeline.
    /// </summary>
    [Fact]
    public void IncludingTheSameSetTwiceAddsItOnce()
    {
        var inv = Inventory();
        var log = FromDisk(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs");

        inv.IncludeFromInstanceHistory([log]);
        inv.IncludeFromInstanceHistory([log]);

        Assert.Equal(2, inv.WorkingSet.Count);
    }

    /// <summary>
    /// The same database and server filters apply to these as to everything else. A log from
    /// another instance must not arrive through a side door the ordinary path would have refused -
    /// mixing two servers' backups into one chain is a defect this codebase has already had once.
    /// </summary>
    [Fact]
    public void ASetForAnotherDatabaseDoesNotReachTheWorkingSet()
    {
        var inv = Inventory();

        var otherDatabase = FromDisk(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs");
        otherDatabase.DatabaseName = "Payroll";
        otherDatabase.Files[0].InferredDatabaseName = "Payroll";

        inv.IncludeFromInstanceHistory([otherDatabase]);

        Assert.Single(inv.WorkingSet);
    }

    [Fact]
    public void IncludingNothingChangesNothing()
    {
        var inv = Inventory();

        inv.IncludeFromInstanceHistory([]);

        Assert.Single(inv.WorkingSet);
        Assert.False(inv.HasSetsFromInstanceHistory);
    }

    // ── what the generator then produces ────────────────────────────────────────

    /// <summary>
    /// The point of the whole feature: one script, the full read from the container by URL and the
    /// logs read from the path they are actually on by DISK.
    /// </summary>
    [Fact]
    public void TheGeneratedScriptReadsTheContainerByUrlAndTheLogsByDisk()
    {
        var chain = new BackupChain
        {
            FullSet = FromContainer(BackupType.Full, T0),
            LogSets =
            [
                FromDisk(BackupType.TransactionLog, T0.AddHours(1), @"E:\SQLLogs"),
                FromDisk(BackupType.TransactionLog, T0.AddHours(2), @"E:\SQLLogs")
            ]
        };

        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = "Sales_Restored",
            RecoveryMode = RecoveryMode.Recovery
        });

        Assert.Contains("URL = N'https://acct.blob.core.windows.net/c/Sales_", script);
        Assert.Contains(@"DISK = N'E:\SQLLogs\Sales_20260814_230000.trn'", script);
        Assert.Contains(@"DISK = N'E:\SQLLogs\Sales_20260815_000000.trn'", script);

        // And it still ends properly: NORECOVERY through the chain, RECOVERY once.
        Assert.Contains("NORECOVERY", script);
        Assert.Equal(1, CountOccurrences(script, "         RECOVERY,"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
