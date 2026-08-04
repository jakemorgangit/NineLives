using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Tests for the pure (non-Azure-IO) surface of BlobStorageService: set grouping,
/// summaries, and discovery helpers. No network or credential access is involved.
/// </summary>
public class BlobStorageServiceTests
{
    private readonly BlobStorageService _service = new(new CredentialStore());

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BackupFileInfo File(
        string blobName,
        BackupType type = BackupType.Full,
        string? db = null,
        string? server = null,
        string? instance = null,
        long size = 100,
        DateTime? lastModified = null,
        string? agSetId = null)
    {
        return new BackupFileInfo
        {
            BlobName = blobName,
            BlobUrl = $"https://acct.blob.core.windows.net/backups/{blobName}",
            Type = type,
            InferredDatabaseName = db,
            InferredServerName = server,
            InferredInstanceName = instance,
            SizeBytes = size,
            LastModified = new DateTimeOffset(lastModified ?? T0, TimeSpan.Zero),
            IsAgDefaultNaming = agSetId != null,
            InferredSetId = agSetId
        };
    }

    [Fact]
    public void GroupIntoBackupSets_StripedFiles_CollapseToOneOrderedSet()
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/Db/20260110_220000_3.bak", db: "Db"),
            File("FULL/SRV01/Db/20260110_220000_1.bak", db: "Db"),
            File("FULL/SRV01/Db/20260110_220000_2.bak", db: "Db"),
        };

        var sets = _service.GroupIntoBackupSets(files);

        var set = Assert.Single(sets);
        Assert.Equal("20260110_220000", set.SetId);
        Assert.Equal(3, set.FileCount);
        Assert.True(set.IsStriped);
        Assert.Equal(new DateTime(2026, 1, 10, 22, 0, 0), set.Timestamp);
        // Files ordered by file name regardless of input order
        Assert.Equal(
            ["20260110_220000_1.bak", "20260110_220000_2.bak", "20260110_220000_3.bak"],
            set.Files.Select(f => f.FileName).ToList());
    }

    [Fact]
    public void GroupIntoBackupSets_SameSetIdDifferentTypes_StaySeparate()
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/Db/20260110_220000.bak", BackupType.Full, db: "Db"),
            File("DIFF/SRV01/Db/20260110_220000.bak", BackupType.Differential, db: "Db"),
        };

        var sets = _service.GroupIntoBackupSets(files);

        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, s => s.Type == BackupType.Full);
        Assert.Contains(sets, s => s.Type == BackupType.Differential);
    }

    [Fact]
    public void GroupIntoBackupSets_SameSetIdDifferentDatabases_StaySeparate()
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/DbA/20260110_220000.bak", db: "DbA"),
            File("FULL/SRV01/DbB/20260110_220000.bak", db: "DbB"),
        };

        var sets = _service.GroupIntoBackupSets(files);

        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, s => s.DatabaseName == "DbA");
        Assert.Contains(sets, s => s.DatabaseName == "DbB");
    }

    [Fact]
    public void GroupIntoBackupSets_AgFiles_GroupByInferredSetId()
    {
        var files = new List<BackupFileInfo>
        {
            File("mycluster01$My-AG1_MyDb_FULL_20260226_200032_1.bak", db: "MyDb", agSetId: "20260226_200032"),
            File("mycluster01$My-AG1_MyDb_FULL_20260226_200032_2.bak", db: "MyDb", agSetId: "20260226_200032"),
        };

        var sets = _service.GroupIntoBackupSets(files);

        var set = Assert.Single(sets);
        Assert.Equal(2, set.FileCount);
        Assert.Equal(new DateTime(2026, 2, 26, 20, 0, 32), set.Timestamp);
    }

    [Fact]
    public void GroupIntoBackupSets_UnparsableTimestamp_FallsBackToLastModified()
    {
        var modified = new DateTime(2026, 3, 1, 8, 30, 0);
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/Db/adhoc-backup.bak", db: "Db", lastModified: modified),
        };

        var sets = _service.GroupIntoBackupSets(files);

        var set = Assert.Single(sets);
        Assert.Equal(modified, set.Timestamp);
    }

    [Fact]
    public void GroupIntoBackupSets_ResultOrderedByTimestamp()
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/Db/20260112_220000.bak", db: "Db"),
            File("FULL/SRV01/Db/20260110_220000.bak", db: "Db"),
            File("FULL/SRV01/Db/20260111_220000.bak", db: "Db"),
        };

        var sets = _service.GroupIntoBackupSets(files);

        var timestamps = sets.Select(s => s.Timestamp).ToList();
        Assert.Equal(timestamps.OrderBy(t => t).ToList(), timestamps);
    }

    [Fact]
    public void GetContainerSummary_CountsAndSizes()
    {
        var files = new List<BackupFileInfo>
        {
            File("a.bak", BackupType.Full, size: 1000, lastModified: T0),
            File("b.bak", BackupType.Differential, size: 200, lastModified: T0.AddHours(1)),
            File("c.trn", BackupType.TransactionLog, size: 50, lastModified: T0.AddHours(2)),
            File("d.txt", BackupType.Unknown, size: 5, lastModified: T0.AddHours(3)),
        };

        var summary = _service.GetContainerSummary(files);

        Assert.Equal(4, summary.TotalFiles);
        Assert.Equal(1, summary.FullBackups);
        Assert.Equal(1, summary.DiffBackups);
        Assert.Equal(1, summary.LogBackups);
        Assert.Equal(1, summary.UnknownFiles);
        Assert.Equal(1255, summary.TotalSizeBytes);
        Assert.Equal(new DateTimeOffset(T0, TimeSpan.Zero), summary.EarliestBackup);
        Assert.Equal(new DateTimeOffset(T0.AddHours(3), TimeSpan.Zero), summary.LatestBackup);
    }

    [Fact]
    public void GetContainerSummary_EmptyList_NullDateRange()
    {
        var summary = _service.GetContainerSummary([]);

        Assert.Equal(0, summary.TotalFiles);
        Assert.Null(summary.EarliestBackup);
        Assert.Null(summary.LatestBackup);
    }

    [Fact]
    public void GetSetBasedSummary_CountsSetsNotFiles()
    {
        var files = new List<BackupFileInfo>
        {
            File("FULL/SRV01/Db/20260110_220000_1.bak", db: "Db", size: 100),
            File("FULL/SRV01/Db/20260110_220000_2.bak", db: "Db", size: 100),
            File("LOG/SRV01/Db/20260110_230000.trn", BackupType.TransactionLog, db: "Db", size: 10),
        };
        var sets = _service.GroupIntoBackupSets(files);

        var summary = _service.GetSetBasedSummary(sets);

        Assert.Equal(2, summary.TotalSets);
        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(1, summary.FullBackups);
        Assert.Equal(1, summary.LogBackups);
        Assert.Equal(210, summary.TotalSizeBytes);
        Assert.Equal(new DateTime(2026, 1, 10, 22, 0, 0), summary.EarliestBackup!.Value.DateTime);
        Assert.Equal(new DateTime(2026, 1, 10, 23, 0, 0), summary.LatestBackup!.Value.DateTime);
    }

    [Fact]
    public void GetDiscoveredDatabases_DistinctCaseInsensitive_Sorted()
    {
        var files = new List<BackupFileInfo>
        {
            File("a.bak", db: "Zebra"),
            File("b.bak", db: "apple"),
            File("c.bak", db: "APPLE"),
            File("d.bak", db: null),
        };

        var databases = _service.GetDiscoveredDatabases(files);

        Assert.Equal(2, databases.Count);
        Assert.Equal("apple", databases[0], ignoreCase: true);
        Assert.Equal("Zebra", databases[1], ignoreCase: true);
    }

    [Fact]
    public void GetDiscoveredServers_JoinsInstanceAndSkipsNulls()
    {
        var files = new List<BackupFileInfo>
        {
            File("a.bak", server: "SRV01", instance: "INST1"),
            File("b.bak", server: "SRV02"),
            File("c.bak"), // no server info at all
        };

        var servers = _service.GetDiscoveredServers(files);

        Assert.Equal(2, servers.Count);
        Assert.Contains(@"SRV01\INST1", servers);
        Assert.Contains("SRV02", servers);
    }
}
