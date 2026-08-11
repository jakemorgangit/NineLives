using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A striped backup read back from an instance's own msdb is not damaged (#351).
///
/// msdb reports a backup's size once for the whole set rather than per stripe, so
/// BackupHistoryInventory records it against the first file. Every other stripe was then left
/// at zero, and the validator reads zero as "an interrupted or failed upload" - an Error, which
/// the restore screen turns into "This chain cannot restore."
///
/// So every striped backup discovered through the shared-path route was declared damaged and
/// refused, at DR time, on exactly the large databases that get striped in the first place.
/// The distinction that fixes it is that unknown is not zero.
/// </summary>
public class StripedSetValidationTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupHistoryEntry Striped(int stripes) => new()
    {
        DatabaseName = "Sales",
        Type = BackupType.Full,
        StartedAt = T0,
        FinishedAt = T0.AddMinutes(30),
        ServerName = "SRV01",
        Position = 1,
        BackupSizeBytes = 900_000_000_000,
        Files = [.. Enumerable.Range(1, stripes).Select(i => $@"\\nas01\sql\Sales_FULL_{i}.bak")]
    };

    [Fact]
    public void AStripedBackupFromMsdbIsNotReportedAsEmpty()
    {
        var set = BackupHistoryInventory.ToSet(Striped(3));

        var issues = new BackupChainValidator().Validate(
            new BackupChain { FullSet = set });

        Assert.DoesNotContain(issues, i => i.Title.Contains("empty file"));
        Assert.DoesNotContain(issues, i => i.Severity == ChainIssueSeverity.Error);
    }

    /// <summary>
    /// The set still reports its real size once, not once per stripe - the reason the zeroes
    /// were there in the first place.
    /// </summary>
    [Fact]
    public void TheSetStillReportsItsRealSizeOnce()
    {
        Assert.Equal(900_000_000_000, BackupHistoryInventory.ToSet(Striped(3)).TotalSizeBytes);
    }

    /// <summary>
    /// A size msdb did not give at all is unknown too, rather than an empty first file.
    /// </summary>
    [Fact]
    public void ASetWithNoRecordedSizeIsNotReportedAsEmpty()
    {
        var entry = new BackupHistoryEntry
        {
            DatabaseName = "Sales",
            Type = BackupType.Full,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(30),
            ServerName = "SRV01",
            Position = 1,
            BackupSizeBytes = null,
            Files = [@"\nas01\sql\Sales_FULL.bak"]
        };

        var issues = new BackupChainValidator().Validate(
            new BackupChain { FullSet = BackupHistoryInventory.ToSet(entry) });

        Assert.DoesNotContain(issues, i => i.Title.Contains("empty file"));
    }

    /// <summary>
    /// The other half: a container listing knows every file's size, so a genuinely zero-byte
    /// stripe among good ones is still caught. This is the failed-upload case the check exists
    /// for, and the fix must not cost it.
    /// </summary>
    [Fact]
    public void AGenuinelyEmptyStripeInAContainerIsStillCaught()
    {
        var set = new BackupSet
        {
            SetId = "20260801_220000",
            Type = BackupType.Full,
            Timestamp = T0,
            Files =
            [
                new BackupFileInfo { BlobName = "Sales_FULL_1.bak", BlobUrl = "https://a/1.bak", SizeBytes = 500 },
                new BackupFileInfo { BlobName = "Sales_FULL_2.bak", BlobUrl = "https://a/2.bak", SizeBytes = 0 }
            ]
        };

        var issues = new BackupChainValidator().Validate(new BackupChain { FullSet = set });

        Assert.Contains(issues, i =>
            i.Severity == ChainIssueSeverity.Error && i.Title.Contains("empty file"));
    }
}
